using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/folders")]
[ApiController]
[Authorize]
public class LibraryFoldersApiController : ControllerBase
{
    private const string FolderContentMusic = "music";
    private const string FolderContentAtmos = "atmos";
    private const string FolderContentVideo = "video";
    private const string FolderContentPodcast = "podcast";
    private const string FolderContentOther = "other";

    private static readonly HashSet<string> AllowedConversionFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3",
        "aac",
        "alac",
        "ogg",
        "opus",
        "flac",
        "wav"
    };

    private static readonly HashSet<string> AllowedConversionBitrates = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUTO",
        "64",
        "96",
        "128",
        "160",
        "192",
        "256",
        "320"
    };

    private readonly LibraryRepository _repository;
    private readonly TaggingProfileService _profileService;
    private readonly DeezSpoTag.Web.Services.LibraryConfigStore _configStore;

    public LibraryFoldersApiController(
        LibraryRepository repository,
        TaggingProfileService profileService,
        DeezSpoTag.Web.Services.LibraryConfigStore configStore)
    {
        _repository = repository;
        _profileService = profileService;
        _configStore = configStore;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] bool downloadOnly = false,
        [FromQuery] bool includeDisabled = false,
        [FromQuery] string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var folders = await _repository.GetFoldersAsync(cancellationToken);
        folders = await NormalizeFolderProfileReferencesAsync(folders, cancellationToken);
        folders = await EnforceAutoTagProfileRequirementsAsync(folders, cancellationToken);
        if (!includeDisabled)
        {
            folders = folders
                .Where(folder => folder.Enabled)
                .ToList();
        }

        var requestedContentType = NormalizeRequestedContentType(contentType);
        if (!string.IsNullOrWhiteSpace(requestedContentType))
        {
            folders = folders
                .Where(folder => IsMatchingRequestedContentType(folder, requestedContentType))
                .ToList();
        }

        if (downloadOnly)
        {
            folders = folders
                .Where(IsEligibleDownloadDestination)
                .ToList();
        }

        return Ok(folders);
    }

    public sealed record BrowseFolderEntry(string Name, string Path, string Type);

    [HttpGet("browse")]
    public IActionResult Browse([FromQuery] string? path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && string.IsNullOrWhiteSpace(path))
        {
            var drives = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => new BrowseFolderEntry(drive.Name, drive.RootDirectory.FullName, "folder"))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Ok(new
            {
                path = string.Empty,
                parentPath = (string?)null,
                entries = drives
            });
        }

        var resolved = ResolveBrowseDirectory(path);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return NotFound(new { error = "Folder not found." });
        }

        var entries = SafeEnumerateDirectories(resolved)
            .Select(directory => new BrowseFolderEntry(
                Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                directory,
                "folder"))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && !entry.Name.StartsWith('.'))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new
        {
            path = resolved,
            parentPath = ResolveParentDirectory(resolved),
            entries
        });
    }

    public sealed record CreateFolderRequest(
        string RootPath,
        string DisplayName,
        bool? Enabled,
        string? LibraryName,
        string? DesiredQuality,
        bool? ConvertEnabled,
        string? ConvertFormat,
        string? ConvertBitrate);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateFolderRequest request, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var desiredQualityValue = string.IsNullOrWhiteSpace(request.DesiredQuality) ? "27" : request.DesiredQuality;
        var convertEnabled = request.ConvertEnabled == true;
        string? convertFormat = null;
        string? convertBitrate = null;
        if (convertEnabled)
        {
            if (!TryNormalizeConversionFormat(request.ConvertFormat, out convertFormat))
            {
                return BadRequest("Invalid conversion format. Allowed values: mp3, aac, alac, ogg, opus, flac, wav.");
            }
            if (!TryNormalizeConversionBitrate(request.ConvertBitrate, out convertBitrate))
            {
                return BadRequest("Invalid conversion bitrate. Allowed values: AUTO, 64, 96, 128, 160, 192, 256, 320.");
            }
        }
        if (!convertEnabled)
        {
            convertFormat = null;
            convertBitrate = null;
        }
        var folder = await _repository.AddFolderAsync(
            new LibraryRepository.FolderUpsertInput(
                request.RootPath,
                request.DisplayName,
                request.Enabled == true,
                request.LibraryName,
                desiredQualityValue,
                convertEnabled,
                convertFormat,
                convertBitrate),
            cancellationToken);
        folder = await EnforceAutoTagProfileRequirementAsync(folder, cancellationToken) ?? folder;
        _configStore.AddLog(new DeezSpoTag.Web.Services.LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Folder added (db): {folder.DisplayName} -> {folder.RootPath}"));
        return Ok(folder);
    }

    public sealed record UpdateFolderRequest(
        string RootPath,
        string DisplayName,
        bool? Enabled,
        string? LibraryName,
        string? DesiredQuality,
        bool? ConvertEnabled,
        string? ConvertFormat,
        string? ConvertBitrate);

    [HttpPatch("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] UpdateFolderRequest request, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var desiredQualityValue = string.IsNullOrWhiteSpace(request.DesiredQuality) ? "27" : request.DesiredQuality;
        var existingFolder = await ResolveExistingFolderAsync(id, cancellationToken);
        if (existingFolder is null)
        {
            return NotFound();
        }

        var conversion = ResolveFolderConversionSettings(request, existingFolder);
        if (!string.IsNullOrWhiteSpace(conversion.Error))
        {
            return BadRequest(conversion.Error);
        }

        var convertEnabled = conversion.ConvertEnabled;
        var convertFormat = conversion.ConvertFormat;
        var convertBitrate = conversion.ConvertBitrate;
        if (!convertEnabled)
        {
            convertFormat = null;
            convertBitrate = null;
        }

        var folder = await _repository.UpdateFolderAsync(
            id,
            new LibraryRepository.FolderUpsertInput(
                request.RootPath,
                request.DisplayName,
                request.Enabled ?? existingFolder.Enabled,
                request.LibraryName,
                desiredQualityValue,
                convertEnabled,
                convertFormat,
                convertBitrate),
            cancellationToken);
        if (folder is null)
        {
            return NotFound();
        }

        folder = await EnforceAutoTagProfileRequirementAsync(folder, cancellationToken) ?? folder;

        var wasEnabled = existingFolder.Enabled;
        var isEnabled = folder.Enabled;
        if (wasEnabled && !isEnabled)
        {
            await _repository.DisableFolderAsync(id, cancellationToken);
        }

        _configStore.AddLog(new DeezSpoTag.Web.Services.LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Folder updated (db): {folder.DisplayName} -> {folder.RootPath}"));
        return Ok(folder);
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var deleted = await _repository.DeleteFolderAsync(id, cancellationToken);
        if (deleted)
        {
            _configStore.AddLog(new DeezSpoTag.Web.Services.LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Folder removed (db): id={id}"));
        }
        return Ok(new { deleted });
    }

    [HttpGet("{id:long}/aliases")]
    public async Task<IActionResult> GetAliases(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var aliases = await _repository.GetFolderAliasesAsync(id, cancellationToken);
        return Ok(aliases);
    }

    public sealed record CreateAliasRequest(string AliasName);

    [HttpPost("{id:long}/aliases")]
    public async Task<IActionResult> AddAlias(long id, [FromBody] CreateAliasRequest request, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var alias = await _repository.AddFolderAliasAsync(id, request.AliasName, cancellationToken);
        return Ok(alias);
    }

    [HttpDelete("aliases/{aliasId:long}")]
    public async Task<IActionResult> DeleteAlias(long aliasId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var deleted = await _repository.DeleteFolderAliasAsync(aliasId, cancellationToken);
        return Ok(new { deleted });
    }

    public sealed record UpdateFolderProfileRequest(string? ProfileId);

    [HttpPut("{id:long}/profile")]
    public async Task<IActionResult> UpdateProfile(long id, [FromBody] UpdateFolderProfileRequest request, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var existingFolder = await ResolveExistingFolderAsync(id, cancellationToken);
        if (existingFolder is null)
        {
            return NotFound();
        }

        var canonicalProfileId = await ResolveCanonicalProfileIdAsync(request.ProfileId);
        if (!string.IsNullOrWhiteSpace(request.ProfileId) && canonicalProfileId == null)
        {
            return BadRequest("Selected AutoTag profile does not exist.");
        }

        var updated = await _repository.UpdateFolderProfileAsync(id, canonicalProfileId, cancellationToken);
        if (updated is null)
        {
            return NotFound();
        }

        updated = await EnforceAutoTagProfileRequirementAsync(updated, cancellationToken) ?? updated;
        return Ok(updated);
    }

    public sealed record UpdateFolderAutoTagEnabledRequest(bool? Enabled);

    [HttpPut("{id:long}/autotag-enabled")]
    public async Task<IActionResult> UpdateAutoTagEnabled(long id, [FromBody] UpdateFolderAutoTagEnabledRequest request, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var folder = await ResolveExistingFolderAsync(id, cancellationToken);
        if (folder is null)
        {
            return NotFound();
        }

        if (request.Enabled != true)
        {
            var disabled = await _repository.UpdateFolderAutoTagEnabledAsync(id, false, cancellationToken);
            if (disabled is null)
            {
                return NotFound();
            }

            return Ok(disabled);
        }

        if (request.Enabled == true
            && RequiresAutoTagProfile(folder))
        {
            var canonicalProfileId = await ResolveCanonicalProfileIdAsync(folder.AutoTagProfileId);
            if (!HasAssignedAutoTagProfile(canonicalProfileId))
            {
                return BadRequest("Music folders require an AutoTag profile before AutoTag can be enabled.");
            }

            if (!string.Equals(folder.AutoTagProfileId?.Trim(), canonicalProfileId, StringComparison.OrdinalIgnoreCase))
            {
                folder = await _repository.UpdateFolderProfileAsync(id, canonicalProfileId, cancellationToken)
                    ?? folder with { AutoTagProfileId = canonicalProfileId };
            }
        }

        var updated = await _repository.UpdateFolderAutoTagEnabledAsync(id, true, cancellationToken);
        if (updated is null)
        {
            return NotFound();
        }

        return Ok(updated);
    }

    private async Task<FolderDto?> ResolveExistingFolderAsync(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return null;
        }

        var folders = await _repository.GetFoldersAsync(cancellationToken);
        return folders.FirstOrDefault(folder => folder.Id == id);
    }

    private static string? NormalizeOptionalString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static bool TryNormalizeConversionFormat(string? value, out string? normalized)
    {
        normalized = NormalizeOptionalString(value);
        if (normalized is null)
        {
            return true;
        }

        var candidate = normalized.Trim().ToLowerInvariant();
        candidate = candidate switch
        {
            "m4a" or "m4a-aac" => "aac",
            "m4a-alac" => "alac",
            _ => candidate
        };

        if (!AllowedConversionFormats.Contains(candidate))
        {
            normalized = null;
            return false;
        }

        normalized = candidate;
        return true;
    }

    private static bool TryNormalizeConversionBitrate(string? value, out string? normalized)
    {
        normalized = NormalizeOptionalString(value);
        if (normalized is null)
        {
            return true;
        }

        var compact = normalized.Trim().Replace(" ", string.Empty).ToLowerInvariant();
        if (compact == "auto")
        {
            normalized = "AUTO";
            return true;
        }

        if (compact.EndsWith("kbps", StringComparison.Ordinal)
            || compact.EndsWith("kb/s", StringComparison.Ordinal))
        {
            compact = compact[..^4];
        }
        else if (compact.EndsWith('k'))
        {
            compact = compact[..^1];
        }

        if (!int.TryParse(compact, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            normalized = null;
            return false;
        }

        var candidate = parsed.ToString(CultureInfo.InvariantCulture);
        if (!AllowedConversionBitrates.Contains(candidate))
        {
            normalized = null;
            return false;
        }

        normalized = candidate;
        return true;
    }

    private async Task<string?> ResolveCanonicalProfileIdAsync(string? profileReference)
    {
        var normalized = profileReference?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var profiles = await _profileService.LoadAsync();
        var match = TaggingProfileService.FindByIdOrName(profiles, normalized);
        return match?.Id;
    }

    private static (bool ConvertEnabled, string? ConvertFormat, string? ConvertBitrate, string? Error)
        ResolveFolderConversionSettings(UpdateFolderRequest request, FolderDto existingFolder)
    {
        var hasConversionPayload = request.ConvertEnabled.HasValue
            || request.ConvertFormat is not null
            || request.ConvertBitrate is not null;

        if (!hasConversionPayload)
        {
            return (existingFolder.ConvertEnabled, existingFolder.ConvertFormat, existingFolder.ConvertBitrate, null);
        }

        if (request.ConvertEnabled != true)
        {
            return (false, null, null, null);
        }

        if (!TryNormalizeConversionFormat(request.ConvertFormat, out var convertFormat))
        {
            return (true, null, null, "Invalid conversion format. Allowed values: mp3, aac, alac, ogg, opus, flac, wav.");
        }

        if (!TryNormalizeConversionBitrate(request.ConvertBitrate, out var convertBitrate))
        {
            return (true, null, null, "Invalid conversion bitrate. Allowed values: AUTO, 64, 96, 128, 160, 192, 256, 320.");
        }

        return (true, convertFormat, convertBitrate, null);
    }

    private async Task<IReadOnlyList<FolderDto>> NormalizeFolderProfileReferencesAsync(
        IReadOnlyList<FolderDto> folders,
        CancellationToken cancellationToken)
    {
        if (folders.Count == 0)
        {
            return folders;
        }

        var profiles = await _profileService.LoadAsync();
        var normalizedFolders = new List<FolderDto>(folders.Count);

        foreach (var folder in folders)
        {
            var currentReference = folder.AutoTagProfileId?.Trim();
            if (string.IsNullOrWhiteSpace(currentReference))
            {
                normalizedFolders.Add(folder with { AutoTagProfileId = null });
                continue;
            }

            var profile = TaggingProfileService.FindByIdOrName(profiles, currentReference);
            var canonicalProfileId = profile?.Id;

            if (!string.Equals(currentReference, canonicalProfileId, StringComparison.OrdinalIgnoreCase))
            {
                await _repository.UpdateFolderProfileAsync(folder.Id, canonicalProfileId, cancellationToken);
            }

            normalizedFolders.Add(folder with { AutoTagProfileId = canonicalProfileId });
        }

        return normalizedFolders;
    }

    private async Task<IReadOnlyList<FolderDto>> EnforceAutoTagProfileRequirementsAsync(
        IReadOnlyList<FolderDto> folders,
        CancellationToken cancellationToken)
    {
        if (folders.Count == 0)
        {
            return folders;
        }

        var guarded = new List<FolderDto>(folders.Count);
        foreach (var folder in folders)
        {
            guarded.Add(await EnforceAutoTagProfileRequirementAsync(folder, cancellationToken) ?? folder);
        }

        return guarded;
    }

    private async Task<FolderDto?> EnforceAutoTagProfileRequirementAsync(FolderDto folder, CancellationToken cancellationToken)
    {
        if (!RequiresAutoTagProfile(folder)
            || !folder.AutoTagEnabled
            || HasAssignedAutoTagProfile(folder.AutoTagProfileId))
        {
            return folder;
        }

        return await _repository.UpdateFolderAutoTagEnabledAsync(folder.Id, false, cancellationToken)
            ?? folder with { AutoTagEnabled = false };
    }

    private static bool RequiresAutoTagProfile(FolderDto folder)
    {
        var desiredQuality = folder.DesiredQuality?.Trim();
        return !string.Equals(desiredQuality, FolderContentVideo, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(desiredQuality, FolderContentPodcast, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAssignedAutoTagProfile(string? profileId)
    {
        return !string.IsNullOrWhiteSpace(profileId);
    }

    private static bool IsEligibleDownloadDestination(FolderDto folder)
    {
        if (!folder.Enabled)
        {
            return false;
        }

        if (!RequiresAutoTagProfile(folder))
        {
            // Video and podcast folders do not require AutoTag profiles.
            return true;
        }

        return folder.AutoTagEnabled
            && HasAssignedAutoTagProfile(folder.AutoTagProfileId);
    }

    private static string? NormalizeRequestedContentType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "music" => FolderContentMusic,
            "audio" => FolderContentMusic,
            "stereo" => FolderContentMusic,
            "track" => FolderContentMusic,
            "atmos" => FolderContentAtmos,
            FolderContentVideo => FolderContentVideo,
            "music-video" => FolderContentVideo,
            "music_videos" => FolderContentVideo,
            FolderContentPodcast => FolderContentPodcast,
            "show" => FolderContentPodcast,
            "episode" => FolderContentPodcast,
            _ => null
        };
    }

    private static bool IsMatchingRequestedContentType(FolderDto folder, string requestedContentType)
    {
        var folderContentType = ResolveFolderContentType(folder);
        return requestedContentType switch
        {
            FolderContentMusic => folderContentType == FolderContentMusic,
            FolderContentAtmos => folderContentType == FolderContentAtmos,
            FolderContentVideo => folderContentType == FolderContentVideo,
            FolderContentPodcast => folderContentType == FolderContentPodcast,
            _ => true
        };
    }

    private static string ResolveFolderContentType(FolderDto folder)
    {
        var normalizedDesiredQuality = (folder.DesiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedDesiredQuality))
        {
            return FolderContentMusic;
        }

        if (normalizedDesiredQuality.Contains("atmos", StringComparison.Ordinal))
        {
            return FolderContentAtmos;
        }

        if (normalizedDesiredQuality.Contains(FolderContentVideo, StringComparison.Ordinal))
        {
            return FolderContentVideo;
        }

        if (normalizedDesiredQuality.Contains(FolderContentPodcast, StringComparison.Ordinal))
        {
            return FolderContentPodcast;
        }

        if (normalizedDesiredQuality == "5")
        {
            return FolderContentAtmos;
        }

        if (normalizedDesiredQuality == "0")
        {
            var fallback = $"{folder.DisplayName} {folder.RootPath}".ToLowerInvariant();
            if (fallback.Contains(FolderContentVideo, StringComparison.Ordinal))
            {
                return FolderContentVideo;
            }

            if (fallback.Contains(FolderContentPodcast, StringComparison.Ordinal))
            {
                return FolderContentPodcast;
            }

            return FolderContentOther;
        }

        return FolderContentMusic;
    }

    private static string[] SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? ResolveBrowseDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? null
                : Path.GetPathRoot("/") ?? Path.DirectorySeparatorChar.ToString();
        }

        try
        {
            var candidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
            while (!Directory.Exists(candidate))
            {
                var parent = Path.GetDirectoryName(candidate);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, candidate, StringComparison.Ordinal))
                {
                    return null;
                }

                candidate = parent;
            }

            return candidate;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveParentDirectory(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.Equals(
                    path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Directory.GetParent(path)?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private ObjectResult DatabaseNotConfigured()
    {
        return StatusCode(503, new { error = "Library DB not configured." });
    }
}
