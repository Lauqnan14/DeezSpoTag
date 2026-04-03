using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using System.Text.Json;

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
            if (ShouldApplyLegacyDownloadTagFallback(tagSettings, profile.AutoTag?.Data, out var legacyDownloadTags))
            {
                ApplyLegacyDownloadTags(tagSettings, legacyDownloadTags);
                _logger.LogWarning(
                    "Profile {ProfileId} had legacy tag mismatch (tagConfig vs autoTag.downloadTags) for folder {FolderId}; restored download tag selection from legacy list.",
                    profile.Id,
                    folder.Id);
            }

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

    private static bool ShouldApplyLegacyDownloadTagFallback(
        TagSettings tagSettings,
        Dictionary<string, JsonElement>? autoTagData,
        out HashSet<string> legacyDownloadTags)
    {
        legacyDownloadTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (autoTagData == null
            || !TryGetStringArray(autoTagData, "downloadTags", legacyDownloadTags)
            || legacyDownloadTags.Count == 0)
        {
            return false;
        }

        // Detect mismatched legacy state where explicit tagConfig disabled core download tags
        // but legacy autoTag.downloadTags still requested them.
        var coreDisabled =
            !tagSettings.Title
            && !tagSettings.Artist
            && !tagSettings.Album
            && !tagSettings.AlbumArtist;

        var legacyRequestsCore =
            legacyDownloadTags.Contains("title")
            || legacyDownloadTags.Contains("artist")
            || legacyDownloadTags.Contains("album")
            || legacyDownloadTags.Contains("albumArtist");

        return coreDisabled && legacyRequestsCore;
    }

    private static bool TryGetStringArray(
        Dictionary<string, JsonElement> source,
        string key,
        HashSet<string> output)
    {
        var matchingKey = source.Keys.FirstOrDefault(entry =>
            string.Equals(entry, key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(matchingKey))
        {
            return false;
        }

        var value = source[matchingKey];
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var normalized = item.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                output.Add(normalized);
            }
        }

        return output.Count > 0;
    }

    private static void ApplyLegacyDownloadTags(TagSettings tagSettings, HashSet<string> legacyDownloadTags)
    {
        foreach (var tag in legacyDownloadTags)
        {
            switch (tag)
            {
                case "title":
                    tagSettings.Title = true;
                    break;
                case "artist":
                    tagSettings.Artist = true;
                    break;
                case "artists":
                    tagSettings.Artists = true;
                    break;
                case "album":
                    tagSettings.Album = true;
                    break;
                case "albumArtist":
                    tagSettings.AlbumArtist = true;
                    break;
                case "cover":
                case "albumArt":
                    tagSettings.Cover = true;
                    break;
                case "trackNumber":
                    tagSettings.TrackNumber = true;
                    break;
                case "trackTotal":
                    tagSettings.TrackTotal = true;
                    break;
                case "discNumber":
                    tagSettings.DiscNumber = true;
                    break;
                case "discTotal":
                    tagSettings.DiscTotal = true;
                    break;
                case "genre":
                    tagSettings.Genre = true;
                    break;
                case "year":
                    tagSettings.Year = true;
                    break;
                case "date":
                    tagSettings.Date = true;
                    break;
                case "isrc":
                    tagSettings.Isrc = true;
                    break;
                case "barcode":
                    tagSettings.Barcode = true;
                    break;
                case "bpm":
                    tagSettings.Bpm = true;
                    break;
                case "duration":
                case "length":
                    tagSettings.Length = true;
                    break;
                case "replayGain":
                    tagSettings.ReplayGain = true;
                    break;
                case "label":
                    tagSettings.Label = true;
                    break;
                case "copyright":
                    tagSettings.Copyright = true;
                    break;
                case "lyrics":
                case "unsyncedLyrics":
                    tagSettings.Lyrics = true;
                    break;
                case "syncedLyrics":
                    tagSettings.SyncedLyrics = true;
                    break;
                case "composer":
                    tagSettings.Composer = true;
                    break;
                case "involvedPeople":
                    tagSettings.InvolvedPeople = true;
                    break;
                case "source":
                    tagSettings.Source = true;
                    break;
                case "url":
                    tagSettings.Url = true;
                    break;
                case "trackId":
                    tagSettings.TrackId = true;
                    break;
                case "releaseId":
                    tagSettings.ReleaseId = true;
                    break;
                case "explicit":
                    tagSettings.Explicit = true;
                    break;
                case "rating":
                    tagSettings.Rating = true;
                    break;
            }
        }
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
