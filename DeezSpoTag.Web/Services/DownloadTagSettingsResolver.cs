using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadTagSettingsResolver : IDownloadTagSettingsResolver
{
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly DownloadTagSettingsConverter _converter;
    private readonly ILogger<DownloadTagSettingsResolver> _logger;

    public DownloadTagSettingsResolver(
        AutoTagProfileResolutionService profileResolutionService,
        DownloadTagSettingsConverter converter,
        ILogger<DownloadTagSettingsResolver> logger)
    {
        _profileResolutionService = profileResolutionService;
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
            if (!destinationFolderId.HasValue)
            {
                return null;
            }

            var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
            if (!state.FoldersById.TryGetValue(destinationFolderId.Value, out var folder)
                || !folder.Enabled)
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

            var profile = AutoTagProfileResolutionService.ResolveFolderProfile(
                state,
                folder.Id,
                folder.AutoTagProfileId);
            if (profile == null)
            {
                _logger.LogDebug("No resolvable AutoTag profile assigned for folder {FolderId}; skipping tag settings resolution.", folder.Id);
                return null;
            }

            var tagSettings = _converter.ToTagSettings(profile.TagConfig, profile.Technical);

            if (IsDownloadTagSelectionEmpty(tagSettings))
            {
                _logger.LogWarning(
                    "Profile {ProfileId} resolved to an empty download tag selection for folder {FolderId}; restoring default download tags.",
                    profile.Id,
                    folder.Id);
                tagSettings = _converter.ToTagSettings(new UnifiedTagConfig(), profile.Technical);
            }

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
            _ => "deezer"
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

    private static bool IsDownloadTagSelectionEmpty(TagSettings settings)
    {
        return !settings.Title
               && !settings.Artist
               && !settings.Artists
               && !settings.Album
               && !settings.AlbumArtist
               && !settings.Cover
               && !settings.TrackNumber
               && !settings.TrackTotal
               && !settings.DiscNumber
               && !settings.DiscTotal
               && !settings.Genre
               && !settings.Year
               && !settings.Date
               && !settings.Isrc
               && !settings.Barcode
               && !settings.Bpm
               && !settings.Length
               && !settings.ReplayGain
               && !settings.Danceability
               && !settings.Energy
               && !settings.Valence
               && !settings.Acousticness
               && !settings.Instrumentalness
               && !settings.Speechiness
               && !settings.Loudness
               && !settings.Tempo
               && !settings.TimeSignature
               && !settings.Liveness
               && !settings.Label
               && !settings.Copyright
               && !settings.Lyrics
               && !settings.SyncedLyrics
               && !settings.Composer
               && !settings.InvolvedPeople
               && !settings.Source
               && !settings.Url
               && !settings.TrackId
               && !settings.ReleaseId
               && !settings.Explicit
               && !settings.Rating;
    }
}
