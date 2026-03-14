using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadTagSettingsResolver : IDownloadTagSettingsResolver
{
    private readonly TaggingProfileService _profileService;
    private readonly LibraryRepository _libraryRepository;
    private readonly DownloadTagSettingsConverter _converter;
    private readonly ILogger<DownloadTagSettingsResolver> _logger;

    public DownloadTagSettingsResolver(
        TaggingProfileService profileService,
        LibraryRepository libraryRepository,
        DownloadTagSettingsConverter converter,
        ILogger<DownloadTagSettingsResolver> logger)
    {
        _profileService = profileService;
        _libraryRepository = libraryRepository;
        _converter = converter;
        _logger = logger;
    }

    public async Task<TagSettings?> ResolveAsync(long? destinationFolderId, CancellationToken cancellationToken)
    {
        var profile = await ResolveProfileAsync(destinationFolderId, cancellationToken);
        return profile?.TagSettings;
    }

    public async Task<DownloadTagProfileSettings?> ResolveProfileAsync(long? destinationFolderId, CancellationToken cancellationToken)
    {
        try
        {
            if (!destinationFolderId.HasValue || !_libraryRepository.IsConfigured)
            {
                return null;
            }

            var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
            var folder = folders.FirstOrDefault(item =>
                item.Id == destinationFolderId.Value
                && item.Enabled);
            if (folder == null)
            {
                return null;
            }

            var folderMode = ResolveFolderMode(folder.DesiredQuality);
            if (folderMode is "video" or "podcast")
            {
                return null;
            }

            if (!folder.AutoTagEnabled)
            {
                return null;
            }

            var profiles = await _profileService.LoadAsync();
            var currentProfileReference = folder.AutoTagProfileId?.Trim();
            if (string.IsNullOrWhiteSpace(currentProfileReference))
            {
                _logger.LogDebug("No AutoTag profile assigned for folder {FolderId}; skipping tag settings resolution.", folder.Id);
                return null;
            }

            var profile = TaggingProfileService.FindByIdOrName(profiles, currentProfileReference);
            if (profile == null)
            {
                _logger.LogDebug("Assigned AutoTag profile '{ProfileRef}' for folder {FolderId} was not found.", currentProfileReference, folder.Id);
                return null;
            }

            var canonicalProfileId = profile.Id;
            if (!string.Equals(currentProfileReference, canonicalProfileId, StringComparison.OrdinalIgnoreCase))
            {
                await _libraryRepository.UpdateFolderProfileAsync(folder.Id, canonicalProfileId, cancellationToken);
            }

            var tagSettings = _converter.ToTagSettings(profile.TagConfig, profile.Technical);
            var downloadTagSource = ExtractDownloadTagSource(profile.AutoTag);
            return new DownloadTagProfileSettings(tagSettings, downloadTagSource, profile.FolderStructure, profile.Technical);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve download tag settings for folder {FolderId}", destinationFolderId);
            return null;
        }
    }

    private static string? ExtractDownloadTagSource(AutoTagSettings? autoTag)
    {
        if (autoTag?.Data == null
            || !autoTag.Data.TryGetValue("downloadTagSource", out var sourceElement)
            || sourceElement.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        var source = sourceElement.GetString()?.Trim().ToLowerInvariant();
        return source switch
        {
            "deezer" => "deezer",
            "spotify" => "spotify",
            _ => null
        };
    }

    private static string ResolveFolderMode(string? desiredQuality)
    {
        var normalized = (desiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == "video")
        {
            return "video";
        }

        if (normalized == "podcast")
        {
            return "podcast";
        }

        return "music";
    }
}
