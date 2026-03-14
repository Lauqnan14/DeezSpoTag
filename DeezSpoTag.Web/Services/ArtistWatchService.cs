using System.Collections.Generic;
using System.Text.Json;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistWatchPlatformDependencies
{
    public ArtistWatchPlatformDependencies(
        SpotifyArtistService spotifyArtistService,
        SpotifyMetadataService spotifyMetadataService,
        AppleMusicCatalogService appleCatalogService,
        DeezerClient deezerClient)
    {
        SpotifyArtistService = spotifyArtistService;
        SpotifyMetadataService = spotifyMetadataService;
        AppleCatalogService = appleCatalogService;
        DeezerClient = deezerClient;
    }

    public SpotifyArtistService SpotifyArtistService { get; }
    public SpotifyMetadataService SpotifyMetadataService { get; }
    public AppleMusicCatalogService AppleCatalogService { get; }
    public DeezerClient DeezerClient { get; }
}

public sealed class ArtistWatchService
{
    private readonly record struct AppleAlbumIntentContext(
        string AlbumName,
        string AlbumArtist,
        string AlbumImage,
        string AlbumReleaseDate,
        string Storefront);

    private const string AlbumGroup = "album";
    private const string SingleGroup = "single";
    private const string ArtistEntityType = "artist";
    private const string QueuedStatus = "queued";
    private const string AppleSource = "apple";
    private const string DeezerSource = "deezer";
    private const string SpotifySource = "spotify";

    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyArtistService _spotifyArtistService;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly DeezerClient _deezerClient;
    private readonly PlaylistWatchService _playlistWatchService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<ArtistWatchService> _logger;

    public ArtistWatchService(
        LibraryRepository libraryRepository,
        ArtistWatchPlatformDependencies platformDependencies,
        PlaylistWatchService playlistWatchService,
        DeezSpoTagSettingsService settingsService,
        ILogger<ArtistWatchService> logger)
    {
        _libraryRepository = libraryRepository;
        _spotifyArtistService = platformDependencies.SpotifyArtistService;
        _spotifyMetadataService = platformDependencies.SpotifyMetadataService;
        _appleCatalogService = platformDependencies.AppleCatalogService;
        _deezerClient = platformDependencies.DeezerClient;
        _playlistWatchService = playlistWatchService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task CheckArtistWatchItemAsync(WatchlistArtistDto artist, CancellationToken cancellationToken)
    {
        if (artist == null)
        {
            return;
        }

        if (!_libraryRepository.IsConfigured)
        {
            _logger.LogDebug("Artist watch skipped - library DB not configured.");
            return;
        }

        var settings = _settingsService.LoadSettings();
        if (!settings.WatchEnabled)
        {
            _logger.LogDebug("Artist watch skipped - disabled in settings.");
            return;
        }

        var albumGroups = BuildAlbumGroupSet(settings.WatchedArtistAlbumGroup);
        await CheckSpotifyArtistAsync(artist, settings, albumGroups, cancellationToken);
        await CheckAppleArtistAsync(artist, settings, albumGroups, cancellationToken);
        await CheckDeezerArtistAsync(artist, settings, albumGroups, cancellationToken);
    }

    private async Task CheckSpotifyArtistAsync(
        WatchlistArtistDto artist,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        ISet<string> albumGroups,
        CancellationToken cancellationToken)
    {
        _ = albumGroups;
        var spotifyId = artist.SpotifyId;
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            spotifyId = await _spotifyArtistService.EnsureSpotifyArtistIdAsync(
                artist.ArtistId,
                artist.ArtistName,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            _logger.LogDebug("Spotify artist watch skipped - missing Spotify ID for {ArtistId}", artist.ArtistId);
            return;
        }

        var state = await _libraryRepository.GetArtistWatchStateAsync(artist.ArtistId, cancellationToken);
        var offset = state?.BatchNextOffset ?? 0;
        if (offset < 0)
        {
            offset = 0;
        }

        var groups = settings.WatchedArtistAlbumGroup ?? new List<string>();
        var page = await _spotifyArtistService.FetchArtistAlbumsPageAsync(
            spotifyId,
            groups,
            offset,
            Math.Clamp(settings.WatchMaxItemsPerRun, 1, 50),
            cancellationToken);
        if (page == null)
        {
            await _libraryRepository.UpsertArtistWatchStateAsync(
                artist.ArtistId,
                spotifyId,
                offset,
                DateTimeOffset.UtcNow,
                cancellationToken);
            return;
        }

        var existing = await _libraryRepository.GetArtistWatchAlbumIdsAsync(artist.ArtistId, SpotifySource, cancellationToken);
        var newAlbums = page.Albums
            .Where(album => !string.IsNullOrWhiteSpace(album.Id) && !existing.Contains(album.Id))
            .ToList();

        var insertedAlbums = new List<ArtistWatchAlbumInsert>();
        foreach (var album in newAlbums)
        {
            var tracks = await _spotifyMetadataService.FetchAlbumTracksAsync(album.Id, cancellationToken);
            if (tracks.Count > 0)
            {
                var queuedCount = await _playlistWatchService.QueueSpotifyWatchTracksAsync(
                    album.Name ?? string.Empty,
                    AlbumGroup,
                    tracks,
                    destinationFolderId: null,
                    cancellationToken);

                if (queuedCount > 0)
                {
                    await AddArtistAlbumWatchHistoryAsync(
                        SpotifySource,
                        album.Id,
                        album.Name ?? "Album",
                        queuedCount,
                        artist.ArtistName,
                        cancellationToken);
                }
            }

            insertedAlbums.Add(new ArtistWatchAlbumInsert(SpotifySource, album.Id));
        }

        await PersistArtistWatchAlbumsAsync(artist.ArtistId, insertedAlbums, cancellationToken);

        var nextOffset = offset + page.Albums.Count;
        var completed = !page.HasMore;
        if (page.Total.HasValue && nextOffset >= page.Total.Value)
        {
            completed = true;
        }

        var storedOffset = completed ? 0 : nextOffset;
        await _libraryRepository.UpsertArtistWatchStateAsync(
            artist.ArtistId,
            spotifyId,
            storedOffset,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private async Task CheckAppleArtistAsync(
        WatchlistArtistDto artist,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        ISet<string> albumGroups,
        CancellationToken cancellationToken)
    {
        var appleId = await ResolveArtistSourceIdAsync(artist.ArtistId, AppleSource, artist.AppleId, cancellationToken);
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return;
        }

        var storefront = await _appleCatalogService.ResolveStorefrontAsync(
            settings.AppleMusic?.Storefront,
            settings.AppleMusic?.MediaUserToken,
            cancellationToken);

        using var artistAlbumsDoc = await TryGetAppleArtistAlbumsAsync(
            artist,
            appleId,
            storefront,
            settings,
            cancellationToken);
        if (artistAlbumsDoc is null
            || !TryGetDataArray(artistAlbumsDoc.RootElement, out var data))
        {
            return;
        }

        var existing = await _libraryRepository.GetArtistWatchAlbumIdsAsync(artist.ArtistId, AppleSource, cancellationToken);
        var insertedAlbums = new List<ArtistWatchAlbumInsert>();

        foreach (var album in data.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processedAlbumId = await ProcessAppleAlbumAsync(
                artist,
                album,
                albumGroups,
                storefront,
                existing,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(processedAlbumId))
            {
                insertedAlbums.Add(new ArtistWatchAlbumInsert(AppleSource, processedAlbumId));
            }
        }

        await PersistArtistWatchAlbumsAsync(artist.ArtistId, insertedAlbums, cancellationToken);
    }

    private async Task<List<DownloadIntent>> BuildAppleAlbumIntentsAsync(
        string albumId,
        string fallbackAlbumName,
        string storefront,
        CancellationToken cancellationToken)
    {
        using var albumDoc = await TryGetAppleAlbumDocumentAsync(albumId, storefront, cancellationToken);
        if (albumDoc is null
            || !TryGetAppleAlbumIntentContext(albumDoc.RootElement, fallbackAlbumName, storefront, out var context, out var tracksData))
        {
            return new List<DownloadIntent>();
        }

        return BuildAppleTrackIntents(tracksData, context);
    }

    private async Task CheckDeezerArtistAsync(
        WatchlistArtistDto artist,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        ISet<string> albumGroups,
        CancellationToken cancellationToken)
    {
        var deezerId = await ResolveArtistSourceIdAsync(artist.ArtistId, DeezerSource, artist.DeezerId, cancellationToken);
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return;
        }

        if (!_deezerClient.LoggedIn)
        {
            _logger.LogDebug("Deezer artist watch skipped - not logged in.");
            return;
        }

        var discography = await TryGetDeezerDiscographyAsync(artist, deezerId, settings, cancellationToken);
        if (discography is null || discography.Data.Count == 0)
        {
            return;
        }

        var existing = await _libraryRepository.GetArtistWatchAlbumIdsAsync(artist.ArtistId, DeezerSource, cancellationToken);
        var insertedAlbums = new List<ArtistWatchAlbumInsert>();
        foreach (var release in discography.Data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processedAlbumId = await ProcessDeezerReleaseAsync(
                artist,
                release,
                albumGroups,
                existing,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(processedAlbumId))
            {
                insertedAlbums.Add(new ArtistWatchAlbumInsert(DeezerSource, processedAlbumId));
            }
        }

        await PersistArtistWatchAlbumsAsync(artist.ArtistId, insertedAlbums, cancellationToken);
    }

    private async Task<string?> ResolveArtistSourceIdAsync(
        long artistId,
        string source,
        string? sourceId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            return sourceId;
        }

        return await _libraryRepository.GetArtistSourceIdAsync(artistId, source, cancellationToken);
    }

    private async Task<JsonDocument?> TryGetAppleArtistAlbumsAsync(
        WatchlistArtistDto artist,
        string appleId,
        string storefront,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _appleCatalogService.GetArtistAlbumsAsync(
                appleId,
                storefront,
                "en-US",
                Math.Clamp(settings.WatchMaxItemsPerRun, 1, 100),
                0,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple artist watch fetch failed for {ArtistId}:{AppleId}", artist.ArtistId, appleId);
            return null;
        }
    }

    private async Task<string?> ProcessAppleAlbumAsync(
        WatchlistArtistDto artist,
        JsonElement album,
        ISet<string> albumGroups,
        string storefront,
        ISet<string> existing,
        CancellationToken cancellationToken)
    {
        if (!TryGetAppleAlbumCandidate(album, albumGroups, existing, out var albumId, out var albumName))
        {
            return null;
        }

        var intents = await BuildAppleAlbumIntentsAsync(albumId, albumName, storefront, cancellationToken);
        if (intents.Count > 0)
        {
            await QueueAppleAlbumIntentsAsync(artist, albumId, albumName, intents, cancellationToken);
        }

        return albumId;
    }

    private async Task QueueAppleAlbumIntentsAsync(
        WatchlistArtistDto artist,
        string albumId,
        string albumName,
        List<DownloadIntent> intents,
        CancellationToken cancellationToken)
    {
        var queuedCount = await _playlistWatchService.QueueAppleWatchIntentsAsync(
            albumName,
            AlbumGroup,
            intents,
            destinationFolderId: null,
            cancellationToken);

        if (queuedCount > 0)
        {
            await AddArtistAlbumWatchHistoryAsync(
                AppleSource,
                albumId,
                albumName,
                queuedCount,
                artist.ArtistName,
                cancellationToken);
        }
    }

    private async Task<JsonDocument?> TryGetAppleAlbumDocumentAsync(
        string albumId,
        string storefront,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _appleCatalogService.GetAlbumAsync(
                albumId,
                storefront,
                "en-US",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple album fetch failed for album {AlbumId}", albumId);
            return null;
        }
    }

    private async Task<GwDiscographyResponse?> TryGetDeezerDiscographyAsync(
        WatchlistArtistDto artist,
        string deezerId,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _deezerClient.GetArtistDiscographyAsync(
                deezerId,
                index: 0,
                limit: Math.Clamp(settings.WatchMaxItemsPerRun, 1, 100));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer artist watch fetch failed for {ArtistId}:{DeezerId}", artist.ArtistId, deezerId);
            return null;
        }
    }

    private async Task<string?> ProcessDeezerReleaseAsync(
        WatchlistArtistDto artist,
        GwAlbumRelease release,
        ISet<string> albumGroups,
        ISet<string> existing,
        CancellationToken cancellationToken)
    {
        if (!TryGetDeezerReleaseCandidate(release, albumGroups, existing, out var albumId, out var albumName))
        {
            return null;
        }

        var tracks = await _deezerClient.GetAlbumTracksAsync(albumId);
        if (tracks.Count > 0)
        {
            var queuedCount = await _playlistWatchService.QueueDeezerWatchTracksAsync(
                albumName,
                AlbumGroup,
                tracks,
                destinationFolderId: null,
                cancellationToken);
            if (queuedCount > 0)
            {
                await AddArtistAlbumWatchHistoryAsync(
                    DeezerSource,
                    albumId,
                    albumName,
                    queuedCount,
                    artist.ArtistName,
                    cancellationToken);
            }
        }

        return albumId;
    }

    private static bool TryGetDataArray(JsonElement root, out JsonElement data)
    {
        if (root.TryGetProperty("data", out data)
            && data.ValueKind == JsonValueKind.Array
            && data.GetArrayLength() > 0)
        {
            return true;
        }

        data = default;
        return false;
    }

    private static bool TryGetAppleAlbumCandidate(
        JsonElement album,
        ISet<string> albumGroups,
        ISet<string> existing,
        out string albumId,
        out string albumName)
    {
        albumId = string.Empty;
        albumName = string.Empty;
        if (album.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var parsedAlbumId = GetJsonString(album, "id")?.Trim();
        if (string.IsNullOrWhiteSpace(parsedAlbumId) || existing.Contains(parsedAlbumId))
        {
            return false;
        }

        if (!album.TryGetProperty("attributes", out var albumAttributes)
            || albumAttributes.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var albumGroup = NormalizeAlbumGroup(GetJsonString(albumAttributes, "albumType"));
        if (!ShouldIncludeAlbumGroup(albumGroup, albumGroups))
        {
            return false;
        }

        albumId = parsedAlbumId;
        albumName = GetJsonString(albumAttributes, "name") ?? "Album";
        return true;
    }

    private static bool TryGetAppleAlbumIntentContext(
        JsonElement root,
        string fallbackAlbumName,
        string storefront,
        out AppleAlbumIntentContext context,
        out JsonElement tracksData)
    {
        context = default;
        tracksData = default;
        if (!TryGetDataArray(root, out var data))
        {
            return false;
        }

        var album = data[0];
        if (album.ValueKind != JsonValueKind.Object
            || !TryGetAppleTracksData(album, out tracksData))
        {
            return false;
        }

        var albumAttributes = GetAppleAlbumAttributes(album);
        context = new AppleAlbumIntentContext(
            GetJsonString(albumAttributes, "name") ?? fallbackAlbumName,
            GetJsonString(albumAttributes, "artistName") ?? string.Empty,
            ResolveAppleArtworkUrl(albumAttributes) ?? string.Empty,
            GetJsonString(albumAttributes, "releaseDate") ?? string.Empty,
            storefront);
        return true;
    }

    private static JsonElement GetAppleAlbumAttributes(JsonElement album)
    {
        return album.TryGetProperty("attributes", out var attributes)
            && attributes.ValueKind == JsonValueKind.Object
            ? attributes
            : default;
    }

    private static bool TryGetAppleTracksData(JsonElement album, out JsonElement tracksData)
    {
        tracksData = default;
        if (!album.TryGetProperty("relationships", out var relationships)
            || relationships.ValueKind != JsonValueKind.Object
            || !relationships.TryGetProperty("tracks", out var tracksRel)
            || tracksRel.ValueKind != JsonValueKind.Object
            || !tracksRel.TryGetProperty("data", out tracksData)
            || tracksData.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return true;
    }

    private static List<DownloadIntent> BuildAppleTrackIntents(JsonElement tracksData, AppleAlbumIntentContext context)
    {
        var intents = new List<DownloadIntent>();
        foreach (var track in tracksData.EnumerateArray())
        {
            if (TryCreateAppleTrackIntent(track, context, out var intent))
            {
                intents.Add(intent);
            }
        }

        return intents;
    }

    private static bool TryCreateAppleTrackIntent(
        JsonElement track,
        AppleAlbumIntentContext context,
        out DownloadIntent intent)
    {
        intent = null!;
        if (track.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var trackId = GetJsonString(track, "id")?.Trim();
        if (string.IsNullOrWhiteSpace(trackId)
            || !track.TryGetProperty("attributes", out var trackAttributes)
            || trackAttributes.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var artistName = GetJsonString(trackAttributes, "artistName") ?? context.AlbumArtist;
        intent = new DownloadIntent
        {
            SourceService = AppleSource,
            SourceUrl = BuildAppleTrackSourceUrl(trackAttributes, context.Storefront, trackId),
            AppleId = trackId,
            Isrc = GetJsonString(trackAttributes, "isrc") ?? string.Empty,
            Title = GetJsonString(trackAttributes, "name") ?? string.Empty,
            Artist = artistName,
            Album = GetJsonString(trackAttributes, "albumName") ?? context.AlbumName,
            AlbumArtist = artistName,
            Cover = ResolveAppleArtworkUrl(trackAttributes) ?? context.AlbumImage,
            DurationMs = GetJsonInt(trackAttributes, "durationInMillis") ?? 0,
            TrackNumber = GetJsonInt(trackAttributes, "trackNumber") ?? 0,
            DiscNumber = GetJsonInt(trackAttributes, "discNumber") ?? 0,
            ReleaseDate = GetJsonString(trackAttributes, "releaseDate") ?? context.AlbumReleaseDate,
            Explicit = string.Equals(GetJsonString(trackAttributes, "contentRating"), "explicit", StringComparison.OrdinalIgnoreCase)
                ? true
                : null,
            Composer = GetJsonString(trackAttributes, "composerName") ?? string.Empty,
            Genres = ReadJsonStringArray(trackAttributes, "genreNames")
        };
        return true;
    }

    private static string BuildAppleTrackSourceUrl(JsonElement trackAttributes, string storefront, string trackId)
    {
        var sourceUrl = GetJsonString(trackAttributes, "url");
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            return sourceUrl;
        }

        return $"https://music.apple.com/{storefront}/song/{trackId}?i={trackId}";
    }

    private static bool TryGetDeezerReleaseCandidate(
        GwAlbumRelease release,
        ISet<string> albumGroups,
        ISet<string> existing,
        out string albumId,
        out string albumName)
    {
        albumId = string.Empty;
        albumName = string.Empty;

        var parsedAlbumId = release.AlbId?.Trim();
        if (string.IsNullOrWhiteSpace(parsedAlbumId) || existing.Contains(parsedAlbumId))
        {
            return false;
        }

        var albumGroup = GetDeezerAlbumGroup(release);
        if (!ShouldIncludeAlbumGroup(albumGroup, albumGroups))
        {
            return false;
        }

        albumId = parsedAlbumId;
        albumName = string.IsNullOrWhiteSpace(release.AlbTitle) ? "Album" : release.AlbTitle;
        return true;
    }

    private async Task AddArtistAlbumWatchHistoryAsync(
        string source,
        string albumId,
        string albumName,
        int queuedCount,
        string artistName,
        CancellationToken cancellationToken)
    {
        if (queuedCount <= 0)
        {
            return;
        }

        await _libraryRepository.AddWatchlistHistoryAsync(
            new WatchlistHistoryInsert(
                source,
                ArtistEntityType,
                albumId,
                albumName,
                AlbumGroup,
                queuedCount,
                QueuedStatus,
                artistName),
            cancellationToken);
    }

    private async Task PersistArtistWatchAlbumsAsync(
        long artistId,
        List<ArtistWatchAlbumInsert> insertedAlbums,
        CancellationToken cancellationToken)
    {
        if (insertedAlbums.Count == 0)
        {
            return;
        }

        await _libraryRepository.AddArtistWatchAlbumsAsync(artistId, insertedAlbums, cancellationToken);
    }

    private static HashSet<string> BuildAlbumGroupSet(IReadOnlyCollection<string>? configuredGroups)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (configuredGroups == null || configuredGroups.Count == 0)
        {
            groups.Add(AlbumGroup);
            groups.Add(SingleGroup);
            return groups;
        }

        foreach (var normalized in configuredGroups
            .Select(NormalizeAlbumGroup)
            .Where(static group => !string.IsNullOrWhiteSpace(group)))
        {
            groups.Add(normalized!);
        }

        if (groups.Count == 0)
        {
            groups.Add(AlbumGroup);
            groups.Add(SingleGroup);
        }

        return groups;
    }

    private static bool ShouldIncludeAlbumGroup(string? albumGroup, ISet<string> groups)
    {
        if (groups.Count == 0)
        {
            return true;
        }

        var normalized = NormalizeAlbumGroup(albumGroup);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = AlbumGroup;
        }

        return groups.Contains(normalized);
    }

    private static string NormalizeAlbumGroup(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? AlbumGroup
            : value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "compile" => "compilation",
            "compilations" => "compilation",
            "appearson" => "appears_on",
            _ => normalized
        };
    }

    private static string GetDeezerAlbumGroup(GwAlbumRelease release)
    {
        if (release.RoleId == 5)
        {
            return "appears_on";
        }

        return release.Type switch
        {
            0 => "single",
            1 => AlbumGroup,
            2 => "compilation",
            _ => AlbumGroup
        };
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Object => GetJsonStringFromObject(value),
            _ => null
        };
    }

    private static string? GetJsonStringFromObject(JsonElement value)
    {
        if (value.TryGetProperty("standard", out var standard)
            && standard.ValueKind == JsonValueKind.String)
        {
            return standard.GetString();
        }

        if (value.TryGetProperty("short", out var shortValue)
            && shortValue.ValueKind == JsonValueKind.String)
        {
            return shortValue.GetString();
        }

        if (value.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        return null;
    }

    private static int? GetJsonInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static List<string> ReadJsonStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .ToList();
    }

    private static string? ResolveAppleArtworkUrl(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object
            || !attributes.TryGetProperty("artwork", out var artwork)
            || artwork.ValueKind != JsonValueKind.Object
            || !artwork.TryGetProperty("url", out var urlValue)
            || urlValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var rawUrl = urlValue.GetString();
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var width = GetJsonInt(artwork, "width") ?? 1000;
        var height = GetJsonInt(artwork, "height") ?? 1000;

        return rawUrl
            .Replace("{w}", width.ToString(), StringComparison.Ordinal)
            .Replace("{h}", height.ToString(), StringComparison.Ordinal)
            .Replace("{f}", "jpg", StringComparison.Ordinal);
    }
}
