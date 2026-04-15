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
        var defaultProfile = profiles.FirstOrDefault(profile => profile.IsDefault)
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
        List<TaggingProfile> profiles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var defaults = await _defaultsStore.LoadAsync();
        var changed = false;
        var defaultProfile = await EnsureDefaultProfileAuthorityAsync(profiles, defaults.DefaultFileProfile, cancellationToken);
        var defaultFileProfile = defaultProfile?.Id;
        if (!string.Equals(defaults.DefaultFileProfile?.Trim(), defaultFileProfile, StringComparison.OrdinalIgnoreCase))
        {
            changed = true;
        }

        var librarySchedules = NormalizeLibrarySchedules(defaults.LibrarySchedules, ref changed);
        var recentDownloadWindowHours = NormalizeRecentDownloadWindowHours(defaults.RecentDownloadWindowHours, ref changed);

        var normalized = new AutoTagDefaultsDto(defaultFileProfile, librarySchedules, recentDownloadWindowHours);
        if (changed)
        {
            normalized = await _defaultsStore.SaveAsync(normalized);
        }

        return normalized;
    }

    private static int NormalizeRecentDownloadWindowHours(int? value, ref bool changed)
    {
        var resolved = value ?? AutoTagDefaultsDto.DefaultRecentDownloadWindowHours;
        if (resolved < 0)
        {
            resolved = AutoTagDefaultsDto.DefaultRecentDownloadWindowHours;
        }

        if (value != resolved)
        {
            changed = true;
        }

        return resolved;
    }

    private async Task<TaggingProfile?> EnsureDefaultProfileAuthorityAsync(
        List<TaggingProfile> profiles,
        string? legacyDefaultProfileReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profiles.Count == 0)
        {
            return null;
        }

        var currentDefault = profiles.FirstOrDefault(profile => profile.IsDefault);
        if (currentDefault != null)
        {
            return currentDefault;
        }

        var fallbackDefault = ResolveProfileReference(profiles, legacyDefaultProfileReference)
            ?? profiles[0];
        await _profileService.SetDefaultProfileAsync(fallbackDefault.Id);

        var reloaded = await _profileService.LoadAsync();
        profiles.Clear();
        profiles.AddRange(reloaded);
        return profiles.FirstOrDefault(profile => profile.IsDefault)
            ?? profiles.FirstOrDefault();
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
                mergedSchedules,
                cancellationToken);

            defaultsChanged |= normalizedFolder.DefaultsChanged;
            normalizedFolders[normalizedFolder.Folder.Id] = normalizedFolder.Folder;
        }

        if (defaultsChanged)
        {
            await _defaultsStore.SaveAsync(new AutoTagDefaultsDto(
                defaults.DefaultFileProfile,
                mergedSchedules,
                defaults.RecentDownloadWindowHours));
        }

        return normalizedFolders;
    }

    private static Dictionary<string, string> NormalizeLibrarySchedules(
        IReadOnlyDictionary<string, string>? librarySchedules,
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

            if (!long.TryParse(folderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFolderId)
                || parsedFolderId <= 0)
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

        var defaultsChanged = !RequiresAutoTagProfile(folder)
            && RemoveOptionalFolderDefaults(folderIdKey, mergedSchedules);

        if (folder.AutoTagEnabled && string.IsNullOrWhiteSpace(effectiveProfileId) && RequiresAutoTagProfile(folder))
        {
            folder = await _libraryRepository.UpdateFolderAutoTagEnabledAsync(folder.Id, false, cancellationToken)
                ?? folder with { AutoTagEnabled = false };
        }

        return new NormalizedFolderResult(folder with { AutoTagProfileId = effectiveProfileId }, defaultsChanged);
    }

    private static bool RemoveOptionalFolderDefaults(
        string folderIdKey,
        Dictionary<string, string> mergedSchedules)
    {
        var removedSchedule = mergedSchedules.Remove(folderIdKey);
        return removedSchedule;
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

        if (changed && !string.IsNullOrWhiteSpace(trimmedReference) && _logger.IsEnabled(LogLevel.Information))
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
