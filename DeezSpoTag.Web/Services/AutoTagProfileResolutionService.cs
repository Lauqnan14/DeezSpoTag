using System.Globalization;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class AutoTagProfileResolutionService
{
    public sealed record ResolvedState(
        List<TaggingProfile> Profiles,
        AutoTagDefaultsDto Defaults,
        TaggingProfile? DefaultProfile,
        IReadOnlyDictionary<long, FolderDto> FoldersById);

    private readonly TaggingProfileService _profileService;
    private readonly AutoTagDefaultsStore _defaultsStore;
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<AutoTagProfileResolutionService> _logger;

    public AutoTagProfileResolutionService(
        TaggingProfileService profileService,
        AutoTagDefaultsStore defaultsStore,
        LibraryRepository libraryRepository,
        ILogger<AutoTagProfileResolutionService> logger)
    {
        _profileService = profileService;
        _defaultsStore = defaultsStore;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public async Task<ResolvedState> LoadNormalizedStateAsync(
        bool includeFolders = true,
        CancellationToken cancellationToken = default)
    {
        var profiles = await _profileService.LoadAsync();
        var defaults = await NormalizeDefaultsAsync(profiles, cancellationToken);
        var foldersById = includeFolders
            ? await NormalizeFoldersAsync(profiles, defaults, cancellationToken)
            : new Dictionary<long, FolderDto>();
        if (includeFolders && _libraryRepository.IsConfigured)
        {
            defaults = await _defaultsStore.LoadAsync();
        }
        var defaultProfile = ResolveProfileReference(profiles, defaults.DefaultFileProfile)
            ?? profiles.FirstOrDefault(profile => profile.IsDefault)
            ?? profiles.FirstOrDefault();
        return new ResolvedState(profiles, defaults, defaultProfile, foldersById);
    }

    public async Task PurgeGhostReferencesAsync(CancellationToken cancellationToken = default)
    {
        await LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
    }

    public async Task RemoveDeletedProfileReferencesAsync(
        string? profileId,
        string? profileName,
        CancellationToken cancellationToken = default)
    {
        var references = AutoTagProfileReferenceSet.Build(profileId, profileName);
        if (references.Count == 0)
        {
            return;
        }

        if (_libraryRepository.IsConfigured)
        {
            var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
            foreach (var folder in folders)
            {
                var currentReference = folder.AutoTagProfileId?.Trim();
                if (string.IsNullOrWhiteSpace(currentReference) || !references.Contains(currentReference))
                {
                    continue;
                }

                await _libraryRepository.UpdateFolderProfileAsync(folder.Id, null, cancellationToken);
                if (RequiresAutoTagProfile(folder))
                {
                    await _libraryRepository.UpdateFolderAutoTagEnabledAsync(folder.Id, false, cancellationToken);
                }
            }
        }

        await _defaultsStore.RemoveProfileReferencesAsync(profileId, profileName);
        await PurgeGhostReferencesAsync(cancellationToken);
    }

    public static TaggingProfile? ResolveFolderProfile(
        ResolvedState state,
        long folderId,
        string? explicitProfileReference = null)
    {
        if (state is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(explicitProfileReference))
        {
            var explicitProfile = ResolveProfileReference(state.Profiles, explicitProfileReference);
            if (explicitProfile != null)
            {
                return explicitProfile;
            }
        }

        if (state.FoldersById.TryGetValue(folderId, out var folder))
        {
            var folderProfile = ResolveProfileReference(state.Profiles, folder.AutoTagProfileId);
            if (folderProfile != null)
            {
                return folderProfile;
            }
        }

        return null;
    }

    public static TaggingProfile? ResolveProfileReference(IEnumerable<TaggingProfile> profiles, string? reference)
    {
        if (profiles == null || string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        return TaggingProfileService.FindByIdOrName(profiles, reference);
    }

    private async Task<AutoTagDefaultsDto> NormalizeDefaultsAsync(
        IReadOnlyList<TaggingProfile> profiles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var defaults = await _defaultsStore.LoadAsync();
        var changed = false;

        var defaultFileProfile = NormalizeProfileReference(profiles, defaults.DefaultFileProfile, out var defaultChanged);
        changed |= defaultChanged;

        var libraryProfiles = NormalizeLibraryProfiles(defaults.LibraryProfiles, profiles, ref changed);
        var librarySchedules = NormalizeLibrarySchedules(defaults.LibrarySchedules, libraryProfiles, ref changed);

        var normalized = new AutoTagDefaultsDto(defaultFileProfile, libraryProfiles, librarySchedules);
        if (changed)
        {
            normalized = await _defaultsStore.SaveAsync(normalized);
        }

        return normalized;
    }

    private async Task<IReadOnlyDictionary<long, FolderDto>> NormalizeFoldersAsync(
        IReadOnlyList<TaggingProfile> profiles,
        AutoTagDefaultsDto defaults,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return new Dictionary<long, FolderDto>();
        }

        var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
        if (folders.Count == 0)
        {
            return new Dictionary<long, FolderDto>();
        }

        var normalizedFolders = new Dictionary<long, FolderDto>();
        var mergedLibraryProfiles = defaults.LibraryProfiles is { Count: > 0 }
            ? new Dictionary<string, string>(defaults.LibraryProfiles, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mergedSchedules = defaults.LibrarySchedules is { Count: > 0 }
            ? new Dictionary<string, string>(defaults.LibrarySchedules, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaultsChanged = false;

        foreach (var originalFolder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedFolder = await NormalizeFolderAsync(
                originalFolder,
                profiles,
                mergedLibraryProfiles,
                mergedSchedules,
                cancellationToken);

            defaultsChanged |= normalizedFolder.DefaultsChanged;
            normalizedFolders[normalizedFolder.Folder.Id] = normalizedFolder.Folder;
        }

        if (defaultsChanged)
        {
            await _defaultsStore.SaveAsync(new AutoTagDefaultsDto(
                defaults.DefaultFileProfile,
                mergedLibraryProfiles,
                mergedSchedules));
        }

        return normalizedFolders;
    }

    private Dictionary<string, string> NormalizeLibraryProfiles(
        IReadOnlyDictionary<string, string>? libraryProfiles,
        IReadOnlyList<TaggingProfile> profiles,
        ref bool changed)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (libraryProfiles is not { Count: > 0 })
        {
            return normalized;
        }

        foreach (var (rawFolderId, rawReference) in libraryProfiles)
        {
            var folderId = rawFolderId?.Trim();
            if (string.IsNullOrWhiteSpace(folderId))
            {
                changed = true;
                continue;
            }

            var canonicalReference = NormalizeProfileReference(profiles, rawReference, out var referenceChanged);
            if (referenceChanged
                || !string.Equals(rawFolderId, folderId, StringComparison.Ordinal)
                || !string.Equals(rawReference?.Trim(), canonicalReference, StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(canonicalReference))
            {
                normalized[folderId] = canonicalReference;
            }
        }

        return normalized;
    }

    private static Dictionary<string, string> NormalizeLibrarySchedules(
        IReadOnlyDictionary<string, string>? librarySchedules,
        Dictionary<string, string> libraryProfiles,
        ref bool changed)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (librarySchedules is not { Count: > 0 })
        {
            return normalized;
        }

        foreach (var (rawFolderId, rawSchedule) in librarySchedules)
        {
            var folderId = rawFolderId?.Trim();
            var schedule = rawSchedule?.Trim();
            if (string.IsNullOrWhiteSpace(folderId) || string.IsNullOrWhiteSpace(schedule))
            {
                changed = true;
                continue;
            }

            if (!string.Equals(rawFolderId, folderId, StringComparison.Ordinal)
                || !string.Equals(rawSchedule, schedule, StringComparison.Ordinal))
            {
                changed = true;
            }

            if (!libraryProfiles.ContainsKey(folderId))
            {
                changed = true;
                continue;
            }

            normalized[folderId] = schedule;
        }

        return normalized;
    }

    private async Task<NormalizedFolderResult> NormalizeFolderAsync(
        FolderDto originalFolder,
        IReadOnlyList<TaggingProfile> profiles,
        Dictionary<string, string> mergedLibraryProfiles,
        Dictionary<string, string> mergedSchedules,
        CancellationToken cancellationToken)
    {
        var folder = originalFolder;
        var folderIdKey = folder.Id.ToString(CultureInfo.InvariantCulture);
        var effectiveProfileId = NormalizeProfileReference(profiles, folder.AutoTagProfileId, out _);

        if (!string.Equals(folder.AutoTagProfileId?.Trim(), effectiveProfileId, StringComparison.OrdinalIgnoreCase))
        {
            folder = await _libraryRepository.UpdateFolderProfileAsync(folder.Id, effectiveProfileId, cancellationToken)
                ?? folder with { AutoTagProfileId = effectiveProfileId };
        }

        var defaultsChanged = RequiresAutoTagProfile(folder)
            ? SyncRequiredFolderDefaults(folderIdKey, effectiveProfileId, mergedLibraryProfiles, mergedSchedules)
            : RemoveOptionalFolderDefaults(folderIdKey, mergedLibraryProfiles, mergedSchedules);

        if (folder.AutoTagEnabled && string.IsNullOrWhiteSpace(effectiveProfileId) && RequiresAutoTagProfile(folder))
        {
            folder = await _libraryRepository.UpdateFolderAutoTagEnabledAsync(folder.Id, false, cancellationToken)
                ?? folder with { AutoTagEnabled = false };
        }

        return new NormalizedFolderResult(folder with { AutoTagProfileId = effectiveProfileId }, defaultsChanged);
    }

    private static bool SyncRequiredFolderDefaults(
        string folderIdKey,
        string? effectiveProfileId,
        Dictionary<string, string> mergedLibraryProfiles,
        Dictionary<string, string> mergedSchedules)
    {
        if (string.IsNullOrWhiteSpace(effectiveProfileId))
        {
            return RemoveOptionalFolderDefaults(folderIdKey, mergedLibraryProfiles, mergedSchedules);
        }

        if (mergedLibraryProfiles.TryGetValue(folderIdKey, out var defaultsProfile)
            && string.Equals(defaultsProfile, effectiveProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        mergedLibraryProfiles[folderIdKey] = effectiveProfileId;
        return true;
    }

    private static bool RemoveOptionalFolderDefaults(
        string folderIdKey,
        Dictionary<string, string> mergedLibraryProfiles,
        Dictionary<string, string> mergedSchedules)
    {
        var removedProfile = mergedLibraryProfiles.Remove(folderIdKey);
        var removedSchedule = mergedSchedules.Remove(folderIdKey);
        return removedProfile || removedSchedule;
    }

    private string? NormalizeProfileReference(
        IReadOnlyList<TaggingProfile> profiles,
        string? reference,
        out bool changed)
    {
        var trimmedReference = reference?.Trim();
        var profile = ResolveProfileReference(profiles, trimmedReference);
        var canonicalProfileId = profile?.Id;
        changed = !string.Equals(trimmedReference, canonicalProfileId, StringComparison.OrdinalIgnoreCase);

        if (changed && !string.IsNullOrWhiteSpace(trimmedReference))
        {
            _logger.LogInformation(
                "Normalized stale AutoTag profile reference '{ProfileReference}' to '{CanonicalProfileId}'.",
                trimmedReference,
                canonicalProfileId ?? "<null>");
        }

        return canonicalProfileId;
    }

    private static bool RequiresAutoTagProfile(FolderDto folder)
    {
        var desiredQuality = folder.DesiredQuality?.Trim();
        return !string.Equals(desiredQuality, "video", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(desiredQuality, "podcast", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record NormalizedFolderResult(FolderDto Folder, bool DefaultsChanged);
}
