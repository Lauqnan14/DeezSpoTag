using System.Collections.Generic;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Metadata.Qobuz;
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
        DeezerClient deezerClient,
        QobuzArtistService qobuzArtistService,
        IQobuzApiClient qobuzApiClient,
        ITidalAccessTokenProvider tidalTokens,
        IHttpClientFactory httpClientFactory)
    {
        SpotifyArtistService = spotifyArtistService;
        SpotifyMetadataService = spotifyMetadataService;
        AppleCatalogService = appleCatalogService;
        DeezerClient = deezerClient;
        QobuzArtistService = qobuzArtistService;
        QobuzApiClient = qobuzApiClient;
        TidalTokens = tidalTokens;
        HttpClientFactory = httpClientFactory;
    }

    public SpotifyArtistService SpotifyArtistService { get; }
    public SpotifyMetadataService SpotifyMetadataService { get; }
    public AppleMusicCatalogService AppleCatalogService { get; }
    public DeezerClient DeezerClient { get; }
    public QobuzArtistService QobuzArtistService { get; }
    public IQobuzApiClient QobuzApiClient { get; }
    public ITidalAccessTokenProvider TidalTokens { get; }
    public IHttpClientFactory HttpClientFactory { get; }
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
    private const string CompilationGroup = "compilation";
    private const string AppearsOnGroup = "appears_on";
    private const string TopSongsGroup = "top songs";
    private const string ArtistEntityType = "artist";
    private const string AppleSource = "apple";
    private const string DeezerSource = "deezer";
    private const string SpotifySource = "spotify";
    private const string QobuzSource = "qobuz";
    private const string TidalSource = "tidal";
    private const string TidalAlbumsFilter = "albums";
    private const string TidalEpsAndSinglesFilter = "EPSANDSINGLES";
    private const string SpotifyTopTrackWatchIdPrefix = "top-track:";
    private static readonly IReadOnlyList<string> DefaultArtistAlbumGroups = new[] { AlbumGroup, SingleGroup };
    internal const int MaxReleasesPerArtistLimit = 100;

    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyArtistService _spotifyArtistService;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly DeezerClient _deezerClient;
    private readonly QobuzArtistService _qobuzArtistService;
    private readonly IQobuzApiClient _qobuzApiClient;
    private readonly ITidalAccessTokenProvider _tidalTokens;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WatchlistQueueService _watchlistQueue;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly WatchlistHistoryService _watchlistHistory;
    private readonly ILogger<ArtistWatchService> _logger;

    public ArtistWatchService(
        LibraryRepository libraryRepository,
        ArtistWatchPlatformDependencies platformDependencies,
        WatchlistQueueService watchlistQueue,
        DeezSpoTagSettingsService settingsService,
        ActivitiesRealtimeService activitiesRealtime,
        ILogger<ArtistWatchService> logger,
        WatchlistHistoryService? watchlistHistory = null)
    {
        _libraryRepository = libraryRepository;
        _spotifyArtistService = platformDependencies.SpotifyArtistService;
        _spotifyMetadataService = platformDependencies.SpotifyMetadataService;
        _appleCatalogService = platformDependencies.AppleCatalogService;
        _deezerClient = platformDependencies.DeezerClient;
        _qobuzArtistService = platformDependencies.QobuzArtistService;
        _qobuzApiClient = platformDependencies.QobuzApiClient;
        _tidalTokens = platformDependencies.TidalTokens;
        _httpClientFactory = platformDependencies.HttpClientFactory;
        _watchlistQueue = watchlistQueue;
        _settingsService = settingsService;
        _watchlistHistory = watchlistHistory ?? new WatchlistHistoryService(libraryRepository, activitiesRealtime);
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

        var albumGroups = ResolveArtistAlbumGroups(artist);
        await CheckSpotifyArtistAsync(artist, settings, albumGroups, cancellationToken);
        await CheckAppleArtistAsync(artist, settings, albumGroups, cancellationToken);
        await CheckDeezerArtistAsync(artist, settings, albumGroups, cancellationToken);
        await CheckQobuzArtistAsync(artist, settings, cancellationToken);
        await CheckTidalArtistAsync(artist, settings, albumGroups, cancellationToken);
        await TouchArtistWatchStateAsync(artist, cancellationToken);
    }

    private async Task CheckSpotifyArtistAsync(
        WatchlistArtistDto artist,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IReadOnlyCollection<string> albumGroups,
        CancellationToken cancellationToken)
    {
        var spotifyId = await ResolveSpotifyWatchIdAsync(artist, cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            LogMissingSpotifyId(artist.ArtistId);
            return;
        }

        var state = await _libraryRepository.GetArtistWatchStateAsync(artist.ArtistId, cancellationToken);
        var watchState = ResolveSpotifyWatchState(artist, state?.BatchNextOffset);
        var existing = await _libraryRepository.GetArtistWatchAlbumIdsAsync(artist.ArtistId, SpotifySource, cancellationToken);
        var topTrackInserts = watchState.TopSongsEnabled
            ? await QueueSpotifyArtistTopSongsAsync(artist, spotifyId, existing, cancellationToken)
            : new List<ArtistWatchAlbumInsert>();

        var page = await _spotifyArtistService.FetchArtistAlbumsPageAsync(
            spotifyId,
            albumGroups,
            watchState.Offset,
            Math.Clamp(settings.WatchMaxReleasesPerArtist, 1, MaxReleasesPerArtistLimit),
            cancellationToken);
        if (page == null)
        {
            await UpsertSpotifyWatchStateAsync(artist.ArtistId, spotifyId, watchState.Offset, cancellationToken);
            await PersistArtistWatchAlbumsAsync(artist.ArtistId, topTrackInserts, cancellationToken);
            return;
        }

        var insertedAlbums = new List<ArtistWatchAlbumInsert>(topTrackInserts);
        await QueueSpotifyAlbumReleasesAsync(artist, page.Albums, existing, insertedAlbums, cancellationToken);
        await PersistArtistWatchAlbumsAsync(artist.ArtistId, insertedAlbums, cancellationToken);
        var storedOffset = ResolveStoredSpotifyOffset(watchState.DownloadEntireDiscography, watchState.Offset, page);
        await UpsertSpotifyWatchStateAsync(artist.ArtistId, spotifyId, storedOffset, cancellationToken);
    }

    private async Task<string?> ResolveSpotifyWatchIdAsync(WatchlistArtistDto artist, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(artist.SpotifyId))
        {
            return artist.SpotifyId;
        }

        return await _spotifyArtistService.EnsureSpotifyArtistIdAsync(
            artist.ArtistId,
            artist.ArtistName,
            cancellationToken);
    }

    private void LogMissingSpotifyId(long artistId)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Spotify artist watch skipped - missing Spotify ID for {ArtistId}", artistId);
        }
    }

    private static SpotifyWatchState ResolveSpotifyWatchState(
        WatchlistArtistDto artist,
        int? batchNextOffset)
    {
        var latestReleasesOnly = artist.LatestReleasesOnly ?? false;
        var downloadEntireDiscography = artist.DownloadDiscographyEnabled ?? !latestReleasesOnly;
        var offset = downloadEntireDiscography ? Math.Max(0, batchNextOffset ?? 0) : 0;
        return new SpotifyWatchState(
            offset,
            downloadEntireDiscography,
            artist.TopSongsEnabled ?? false);
    }

    private static IReadOnlyList<string> ResolveArtistAlbumGroups(WatchlistArtistDto artist)
    {
        var normalized = NormalizeAlbumGroups(artist.WatchedAlbumGroups);
        return normalized.Count > 0 ? normalized : DefaultArtistAlbumGroups;
    }

    private async Task QueueSpotifyAlbumReleasesAsync(
        WatchlistArtistDto artist,
        IEnumerable<SpotifyAlbum> albums,
        HashSet<string> existing,
        List<ArtistWatchAlbumInsert> insertedAlbums,
        CancellationToken cancellationToken)
    {
        foreach (var album in albums.Where(album => !string.IsNullOrWhiteSpace(album.Id) && !existing.Contains(album.Id)))
        {
            await QueueSpotifyAlbumReleaseAsync(artist, album, insertedAlbums, cancellationToken);
        }
    }

    private async Task QueueSpotifyAlbumReleaseAsync(
        WatchlistArtistDto artist,
        SpotifyAlbum album,
        List<ArtistWatchAlbumInsert> insertedAlbums,
        CancellationToken cancellationToken)
    {
        var tracks = await _spotifyMetadataService.FetchAlbumTracksAsync(album.Id, cancellationToken);
        if (tracks.Count > 0)
        {
            var outcome = await _watchlistQueue.QueueSpotifyWatchTracksWithOutcomeAsync(
                tracks,
                BuildArtistQueueOptions(artist, album.Name ?? string.Empty, AlbumGroup),
                cancellationToken);
            await AddSpotifyAlbumWatchHistoryIfQueuedAsync(artist, album, outcome.Queued, cancellationToken);
            if (outcome.IsSettled)
            {
                insertedAlbums.Add(new ArtistWatchAlbumInsert(SpotifySource, album.Id));
            }
        }
    }

    private async Task AddSpotifyAlbumWatchHistoryIfQueuedAsync(
        WatchlistArtistDto artist,
        SpotifyAlbum album,
        int queuedCount,
        CancellationToken cancellationToken)
    {
        if (queuedCount <= 0)
        {
            return;
        }

        await AddArtistAlbumWatchHistoryAsync(
            artist.ArtistId,
            SpotifySource,
            album.Id,
            album.Name ?? "Album",
            queuedCount,
            artist.ArtistName,
            AlbumGroup,
            cancellationToken);
    }

    private static int ResolveStoredSpotifyOffset(
        bool downloadEntireDiscography,
        int offset,
        SpotifyAlbumPage page)
    {
        var nextOffset = offset + page.Albums.Count;
        var completed = !page.HasMore || (page.Total.HasValue && nextOffset >= page.Total.Value);
        return !downloadEntireDiscography || completed ? 0 : nextOffset;
    }

    private async Task UpsertSpotifyWatchStateAsync(
        long artistId,
        string spotifyId,
        int offset,
        CancellationToken cancellationToken)
    {
        await _libraryRepository.UpsertArtistWatchStateAsync(
            artistId,
            spotifyId,
            offset,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private sealed record SpotifyWatchState(int Offset, bool DownloadEntireDiscography, bool TopSongsEnabled);

    private static ArtistWatchQueueOptions BuildArtistQueueOptions(
        WatchlistArtistDto artist,
        string collectionName,
        string collectionType)
    {
        return new ArtistWatchQueueOptions
        {
            CollectionName = collectionName,
            CollectionType = collectionType,
            DestinationFolderId = artist.DestinationFolderId,
            PreferredEngine = artist.PreferredEngine,
            RoutingRules = artist.RoutingRules,
            DownloadVariantMode = artist.DownloadVariantMode,
            AtmosDestinationFolderId = artist.AtmosDestinationFolderId,
            BlockRules = artist.IgnoreRules
        };
    }

    private async Task<List<ArtistWatchAlbumInsert>> QueueSpotifyArtistTopSongsAsync(
        WatchlistArtistDto artist,
        string spotifyId,
        HashSet<string> existing,
        CancellationToken cancellationToken)
    {
        var artistPage = await _spotifyArtistService.GetArtistPageBySpotifyIdAsync(
            spotifyId,
            artist.ArtistName,
            forceRefresh: false,
            cancellationToken);
        if (artistPage is null)
        {
            return new List<ArtistWatchAlbumInsert>();
        }

        var topTracks = artistPage.TopTracks;
        var currentTopTrackIds = topTracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Id))
            .Select(track => BuildSpotifyTopTrackWatchId(track.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(artist.TopSongsSyncMode, "append", StringComparison.OrdinalIgnoreCase))
        {
            await _libraryRepository.RemoveArtistWatchAlbumsExceptAsync(
                artist.ArtistId,
                SpotifySource,
                SpotifyTopTrackWatchIdPrefix,
                currentTopTrackIds,
                cancellationToken);
            existing = await _libraryRepository.GetArtistWatchAlbumIdsAsync(artist.ArtistId, SpotifySource, cancellationToken);
        }

        var newTracks = topTracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Id) && !existing.Contains(BuildSpotifyTopTrackWatchId(track.Id)))
            .ToList();
        if (newTracks.Count == 0)
        {
            return new List<ArtistWatchAlbumInsert>();
        }

        var summaries = newTracks
            .Select(track => MapSpotifyTopTrackSummary(track, artist.ArtistName))
            .ToList();
        var collectionName = $"{artist.ArtistName} - Top Songs";
        var outcome = await _watchlistQueue.QueueSpotifyWatchTracksWithOutcomeAsync(
            summaries,
            BuildArtistQueueOptions(artist, collectionName, TopSongsGroup),
            cancellationToken);
        if (outcome.Queued > 0)
        {
            await AddArtistAlbumWatchHistoryAsync(
                artist.ArtistId,
                SpotifySource,
                $"artist-top:{spotifyId}",
                collectionName,
                outcome.Queued,
                artist.ArtistName,
                TopSongsGroup,
                cancellationToken);
        }

        return outcome.IsSettled ? newTracks
            .Select(track => new ArtistWatchAlbumInsert(SpotifySource, BuildSpotifyTopTrackWatchId(track.Id)))
            .ToList() : [];
    }

    private async Task CheckAppleArtistAsync(
        WatchlistArtistDto artist,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IReadOnlyCollection<string> albumGroups,
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

        var appleState = await _libraryRepository.GetArtistWatchStateAsync(artist.ArtistId, cancellationToken);
        var applePageSize = Math.Clamp(settings.WatchMaxReleasesPerArtist, 1, MaxReleasesPerArtistLimit);
        var appleOffset = ResolveSourcePagingOffset(artist, appleState?.AppleNextOffset);
        using var artistAlbumsDoc = await TryGetAppleArtistAlbumsAsync(
            artist,
            appleId,
            storefront,
            settings,
            appleOffset,
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
        await _libraryRepository.UpsertArtistWatchSourceOffsetAsync(
            artist.ArtistId,
            AppleSource,
            ResolveNextSourceOffset(artist, appleOffset, data.GetArrayLength(), applePageSize),
            cancellationToken);
    }

    private static int ResolveSourcePagingOffset(WatchlistArtistDto artist, int? storedOffset)
        => ResolveDownloadsEntireDiscography(artist) ? Math.Max(0, storedOffset ?? 0) : 0;

    private static bool ResolveDownloadsEntireDiscography(WatchlistArtistDto artist)
        => artist.DownloadDiscographyEnabled ?? !(artist.LatestReleasesOnly ?? false);

    private static int ResolveNextSourceOffset(
        WatchlistArtistDto artist,
        int offset,
        int returnedCount,
        int pageSize)
    {
        if (!ResolveDownloadsEntireDiscography(artist) || returnedCount < pageSize)
        {
            return 0;
        }

        return offset + returnedCount;
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
        IReadOnlyCollection<string> albumGroups,
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

        var deezerState = await _libraryRepository.GetArtistWatchStateAsync(artist.ArtistId, cancellationToken);
        var deezerPageSize = Math.Clamp(settings.WatchMaxReleasesPerArtist, 1, MaxReleasesPerArtistLimit);
        var deezerOffset = ResolveSourcePagingOffset(artist, deezerState?.DeezerNextOffset);
        var discography = await TryGetDeezerDiscographyAsync(artist, deezerId, settings, deezerOffset, cancellationToken);
        if (discography is null || discography.Data.Count == 0)
        {
            await _libraryRepository.UpsertArtistWatchSourceOffsetAsync(
                artist.ArtistId,
                DeezerSource,
                0,
                cancellationToken);
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
        await _libraryRepository.UpsertArtistWatchSourceOffsetAsync(
            artist.ArtistId,
            DeezerSource,
            ResolveNextSourceOffset(artist, deezerOffset, discography.Data.Count, deezerPageSize),
            cancellationToken);
    }

    private async Task CheckQobuzArtistAsync(
        WatchlistArtistDto artist,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var qobuzId = await ResolveArtistSourceIdAsync(artist.ArtistId, QobuzSource, artist.QobuzId, cancellationToken);
        if (string.IsNullOrWhiteSpace(qobuzId)
            || !int.TryParse(qobuzId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericQobuzId))
        {
            return;
        }

        QobuzArtist? qobuzArtist;
        try
        {
            qobuzArtist = await _qobuzArtistService.GetArtistWithDiscographyAsync(
                numericQobuzId,
                string.Empty,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz artist watch fetch failed for {ArtistId}:{QobuzId}", artist.ArtistId, numericQobuzId);
            }
            return;
        }

        var albums = qobuzArtist?.Albums?.Items;
        if (albums is null || albums.Count == 0)
        {
            return;
        }

        var existing = await _libraryRepository.GetArtistWatchAlbumIdsAsync(artist.ArtistId, QobuzSource, cancellationToken);
        var insertedAlbums = new List<ArtistWatchAlbumInsert>();
        var limit = Math.Clamp(settings.WatchMaxReleasesPerArtist, 1, MaxReleasesPerArtistLimit);
        var processed = 0;
        foreach (var album in albums)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processed >= limit)
            {
                break;
            }

            var albumId = ResolveQobuzAlbumId(album);
            if (string.IsNullOrWhiteSpace(albumId) || existing.Contains(albumId))
            {
                continue;
            }

            processed++;
            var queuedAlbumId = await ProcessQobuzAlbumAsync(artist, album, albumId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(queuedAlbumId))
            {
                insertedAlbums.Add(new ArtistWatchAlbumInsert(QobuzSource, queuedAlbumId));
            }
        }

        await PersistArtistWatchAlbumsAsync(artist.ArtistId, insertedAlbums, cancellationToken);
    }

    private static string? ResolveQobuzAlbumId(QobuzAlbum album)
    {
        if (!string.IsNullOrWhiteSpace(album.Id))
        {
            return album.Id.Trim();
        }

        return album.QobuzId > 0
            ? album.QobuzId.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private async Task<string?> ProcessQobuzAlbumAsync(
        WatchlistArtistDto artist,
        QobuzAlbum album,
        string albumId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(album.Url))
        {
            return null;
        }

        List<QobuzTrack> tracks;
        try
        {
            tracks = await _qobuzApiClient.GetAlbumPageTracksAsync(album.Url, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz album track fetch failed for {AlbumId}", albumId);
            }
            return null;
        }

        var albumName = string.IsNullOrWhiteSpace(album.Title) ? albumId : album.Title!.Trim();
        var intents = BuildQobuzAlbumIntents(tracks, artist, albumName);
        if (intents.Count == 0)
        {
            return null;
        }

        var outcome = await _watchlistQueue.QueueWatchIntentsWithOutcomeAsync(
            intents,
            BuildArtistQueueOptions(artist, albumName, AlbumGroup),
            "Qobuz",
            cancellationToken);
        if (outcome.Queued > 0)
        {
            await AddArtistAlbumWatchHistoryAsync(
                artist.ArtistId,
                QobuzSource,
                albumId,
                albumName,
                outcome.Queued,
                artist.ArtistName,
                AlbumGroup,
                cancellationToken);
        }

        return outcome.IsSettled ? albumId : null;
    }

    private static List<DownloadIntent> BuildQobuzAlbumIntents(
        IReadOnlyCollection<QobuzTrack> tracks,
        WatchlistArtistDto artist,
        string albumName)
    {
        var intents = new List<DownloadIntent>(tracks.Count);
        foreach (var track in tracks.Where(static track => track.Id > 0))
        {
            var trackId = track.Id.ToString(CultureInfo.InvariantCulture);
            intents.Add(new DownloadIntent
            {
                QobuzId = trackId,
                SourceUrl = $"https://open.qobuz.com/track/{trackId}",
                SourceService = QobuzSource,
                Title = track.Title ?? string.Empty,
                Artist = artist.ArtistName,
                Album = albumName,
                Isrc = track.ISRC ?? string.Empty
            });
        }

        return intents;
    }

    private async Task CheckTidalArtistAsync(
        WatchlistArtistDto artist,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IReadOnlyCollection<string> albumGroups,
        CancellationToken cancellationToken)
    {
        var tidalId = await ResolveArtistSourceIdAsync(artist.ArtistId, TidalSource, artist.TidalId, cancellationToken);
        if (string.IsNullOrWhiteSpace(tidalId))
        {
            return;
        }

        var limit = Math.Clamp(settings.WatchMaxReleasesPerArtist, 1, MaxReleasesPerArtistLimit);
        var state = await _libraryRepository.GetArtistWatchStateAsync(artist.ArtistId, cancellationToken);
        var offset = ResolveSourcePagingOffset(artist, state?.TidalNextOffset);
        var existing = await _libraryRepository.GetArtistWatchAlbumIdsAsync(artist.ArtistId, TidalSource, cancellationToken);
        var insertedAlbums = new List<ArtistWatchAlbumInsert>();
        var maxReturned = 0;
        foreach (var filter in ResolveTidalReleaseFilters(albumGroups))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var albumsDocument = await TryGetTidalArtistAlbumsAsync(artist, tidalId, filter, limit, offset, cancellationToken);
            if (albumsDocument is null
                || !albumsDocument.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var returned = 0;
            foreach (var album in items.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                returned++;
                var albumId = ResolveTidalAlbumId(album);
                if (string.IsNullOrWhiteSpace(albumId) || existing.Contains(albumId))
                {
                    continue;
                }

                var queuedAlbumId = await ProcessTidalAlbumAsync(artist, album, albumId, cancellationToken);
                if (!string.IsNullOrWhiteSpace(queuedAlbumId))
                {
                    insertedAlbums.Add(new ArtistWatchAlbumInsert(TidalSource, queuedAlbumId));
                }
            }

            maxReturned = Math.Max(maxReturned, returned);
        }

        await PersistArtistWatchAlbumsAsync(artist.ArtistId, insertedAlbums, cancellationToken);
        await _libraryRepository.UpsertArtistWatchSourceOffsetAsync(
            artist.ArtistId,
            TidalSource,
            ResolveNextSourceOffset(artist, offset, maxReturned, limit),
            cancellationToken);
    }

    private static IReadOnlyList<string> ResolveTidalReleaseFilters(IReadOnlyCollection<string> albumGroups)
    {
        var filters = new List<string>(2);
        if (ShouldIncludeAlbumGroup(AlbumGroup, albumGroups))
        {
            filters.Add(TidalAlbumsFilter);
        }

        if (ShouldIncludeAlbumGroup(SingleGroup, albumGroups))
        {
            filters.Add(TidalEpsAndSinglesFilter);
        }

        return filters.Count > 0 ? filters : new List<string> { TidalAlbumsFilter };
    }

    private async Task<JsonDocument?> TryGetTidalArtistAlbumsAsync(
        WatchlistArtistDto artist,
        string tidalId,
        string filter,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        try
        {
            var token = await _tidalTokens.GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var country = await _tidalTokens.GetCountryCodeAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(country))
            {
                country = "US";
            }

            var filterQuery = string.Equals(filter, TidalAlbumsFilter, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $"&filter={Uri.EscapeDataString(filter)}";
            var url = $"https://api.tidal.com/v1/artists/{Uri.EscapeDataString(tidalId)}/albums"
                + $"?countryCode={Uri.EscapeDataString(country)}{filterQuery}"
                + $"&limit={limit.ToString(CultureInfo.InvariantCulture)}"
                + $"&offset={offset.ToString(CultureInfo.InvariantCulture)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Tidal artist watch fetch failed for {ArtistId}:{TidalId}", artist.ArtistId, tidalId);
            }
            return null;
        }
    }

    private static string? ResolveTidalAlbumId(JsonElement album)
    {
        if (!album.TryGetProperty("id", out var idElement))
        {
            return null;
        }

        return idElement.ValueKind switch
        {
            JsonValueKind.Number => idElement.GetRawText(),
            JsonValueKind.String => idElement.GetString()?.Trim(),
            _ => null
        };
    }

    private async Task<string?> ProcessTidalAlbumAsync(
        WatchlistArtistDto artist,
        JsonElement album,
        string albumId,
        CancellationToken cancellationToken)
    {
        using var tracksDocument = await TryGetTidalAlbumTracksAsync(artist, albumId, cancellationToken);
        if (tracksDocument is null
            || !tracksDocument.RootElement.TryGetProperty("items", out var trackItems)
            || trackItems.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var albumName = album.TryGetProperty("title", out var titleElement)
            && titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString() ?? albumId
                : albumId;
        var intents = BuildTidalAlbumIntents(trackItems, artist, albumName);
        if (intents.Count == 0)
        {
            return null;
        }

        var outcome = await _watchlistQueue.QueueWatchIntentsWithOutcomeAsync(
            intents,
            BuildArtistQueueOptions(artist, albumName, AlbumGroup),
            "Tidal",
            cancellationToken);
        if (outcome.Queued > 0)
        {
            await AddArtistAlbumWatchHistoryAsync(
                artist.ArtistId,
                TidalSource,
                albumId,
                albumName,
                outcome.Queued,
                artist.ArtistName,
                AlbumGroup,
                cancellationToken);
        }

        return outcome.IsSettled ? albumId : null;
    }

    private async Task<JsonDocument?> TryGetTidalAlbumTracksAsync(
        WatchlistArtistDto artist,
        string albumId,
        CancellationToken cancellationToken)
    {
        try
        {
            var token = await _tidalTokens.GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var country = await _tidalTokens.GetCountryCodeAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(country))
            {
                country = "US";
            }

            var url = $"https://api.tidal.com/v1/albums/{Uri.EscapeDataString(albumId)}/tracks"
                + $"?countryCode={Uri.EscapeDataString(country)}&limit=100";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Tidal album track fetch failed for {ArtistId}:{AlbumId}", artist.ArtistId, albumId);
            }
            return null;
        }
    }

    private static List<DownloadIntent> BuildTidalAlbumIntents(
        JsonElement trackItems,
        WatchlistArtistDto artist,
        string albumName)
    {
        var intents = new List<DownloadIntent>();
        foreach (var track in trackItems.EnumerateArray())
        {
            var trackId = ResolveTidalAlbumId(track);
            if (string.IsNullOrWhiteSpace(trackId))
            {
                continue;
            }

            intents.Add(new DownloadIntent
            {
                TidalId = trackId,
                SourceUrl = $"https://tidal.com/browse/track/{trackId}",
                SourceService = TidalSource,
                Title = track.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? string.Empty
                    : string.Empty,
                Artist = artist.ArtistName,
                Album = albumName,
                Isrc = track.TryGetProperty("isrc", out var i) && i.ValueKind == JsonValueKind.String
                    ? i.GetString() ?? string.Empty
                    : string.Empty
            });
        }

        return intents;
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
        int offset,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _appleCatalogService.GetArtistAlbumsAsync(
                appleId,
                storefront,
                "en-US",
                Math.Clamp(settings.WatchMaxReleasesPerArtist, 1, MaxReleasesPerArtistLimit),
                offset,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple artist watch fetch failed for {ArtistId}:{AppleId}", artist.ArtistId, appleId);
            }
            return null;
        }
    }

    private async Task<string?> ProcessAppleAlbumAsync(
        WatchlistArtistDto artist,
        JsonElement album,
        IReadOnlyCollection<string> albumGroups,
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
            var outcome = await QueueAppleAlbumIntentsAsync(artist, albumId, albumName, intents, cancellationToken);
            return outcome.IsSettled ? albumId : null;
        }
        return null;
    }

    private async Task<ArtistWatchQueueOutcome> QueueAppleAlbumIntentsAsync(
        WatchlistArtistDto artist,
        string albumId,
        string albumName,
        List<DownloadIntent> intents,
        CancellationToken cancellationToken)
    {
        var outcome = await _watchlistQueue.QueueAppleWatchIntentsWithOutcomeAsync(
            intents,
            BuildArtistQueueOptions(artist, albumName, AlbumGroup),
            cancellationToken);

        if (outcome.Queued > 0)
        {
            await AddArtistAlbumWatchHistoryAsync(
                artist.ArtistId,
                AppleSource,
                albumId,
                albumName,
                outcome.Queued,
                artist.ArtistName,
                AlbumGroup,
                cancellationToken);
        }
        return outcome;
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
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple album fetch failed for album {AlbumId}", albumId);
            }
            return null;
        }
    }

    private async Task<GwDiscographyResponse?> TryGetDeezerDiscographyAsync(
        WatchlistArtistDto artist,
        string deezerId,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        int offset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _deezerClient.GetArtistDiscographyAsync(
                deezerId,
                index: offset,
                limit: Math.Clamp(settings.WatchMaxReleasesPerArtist, 1, MaxReleasesPerArtistLimit));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer artist watch fetch failed for {ArtistId}:{DeezerId}", artist.ArtistId, deezerId);
            }
            return null;
        }
    }

    private async Task<string?> ProcessDeezerReleaseAsync(
        WatchlistArtistDto artist,
        GwAlbumRelease release,
        IReadOnlyCollection<string> albumGroups,
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
            var outcome = await _watchlistQueue.QueueDeezerWatchTracksWithOutcomeAsync(
                tracks,
                BuildArtistQueueOptions(artist, albumName, AlbumGroup),
                cancellationToken);
            if (outcome.Queued > 0)
            {
                await AddArtistAlbumWatchHistoryAsync(
                    artist.ArtistId,
                    DeezerSource,
                    albumId,
                    albumName,
                    outcome.Queued,
                    artist.ArtistName,
                    AlbumGroup,
                    cancellationToken);
            }
            return outcome.IsSettled ? albumId : null;
        }
        return null;
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
        IReadOnlyCollection<string> albumGroups,
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
        foreach (var intent in tracksData.EnumerateArray()
            .Select(track => TryCreateAppleTrackIntent(track, context, out var intent) ? intent : null)
            .Where(intent => intent is not null))
        {
            intents.Add(intent!);
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
        IReadOnlyCollection<string> albumGroups,
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
        long artistId,
        string source,
        string albumId,
        string albumName,
        int queuedCount,
        string artistName,
        string collectionType,
        CancellationToken cancellationToken)
    {
        if (queuedCount <= 0)
        {
            return;
        }

        await _watchlistHistory.RecordAsync(
            new WatchlistHistoryWrite(
                source,
                ArtistEntityType,
                albumId,
                WatchlistHistoryService.ArtistItemKey(artistId),
                albumName,
                collectionType,
                queuedCount,
                WatchlistHistoryStatus.Queued,
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

    private async Task TouchArtistWatchStateAsync(
        WatchlistArtistDto artist,
        CancellationToken cancellationToken)
    {
        var state = await _libraryRepository.GetArtistWatchStateAsync(artist.ArtistId, cancellationToken);
        await _libraryRepository.UpsertArtistWatchStateAsync(
            artist.ArtistId,
            state?.SpotifyId ?? artist.SpotifyId,
            state?.BatchNextOffset,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    internal static IReadOnlyList<string> NormalizeAlbumGroups(IEnumerable<string>? configuredGroups)
    {
        var groups = new SortedSet<string>(StringComparer.Ordinal);

        if (configuredGroups != null)
        {
            foreach (var normalized in configuredGroups
                .Select(NormalizeAlbumGroup)
                .Where(static group => !string.IsNullOrWhiteSpace(group)))
            {
                groups.Add(normalized!);
            }
        }

        if (groups.Count == 0)
        {
            groups.Add(AlbumGroup);
            groups.Add(SingleGroup);
        }

        return groups.ToList();
    }

    private static SpotifyTrackSummary MapSpotifyTopTrackSummary(SpotifyTrack track, string artistName)
    {
        var trackId = track.Id.Trim();
        return new SpotifyTrackSummary(
            trackId,
            track.Name,
            artistName,
            track.AlbumName,
            track.DurationMs > 0 ? track.DurationMs : null,
            ResolveSpotifyTrackSourceUrl(track.SourceUrl, trackId),
            SelectSpotifyTrackImageUrl(track.AlbumImages),
            track.Isrc,
            track.ReleaseDate,
            Explicit: track.Explicit)
        {
            AlbumId = track.AlbumId
        };
    }

    private static string BuildSpotifyTopTrackWatchId(string trackId)
        => $"{SpotifyTopTrackWatchIdPrefix}{trackId.Trim()}";

    private static string ResolveSpotifyTrackSourceUrl(string? sourceUrl, string trackId)
    {
        var normalized = sourceUrl?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? $"https://open.spotify.com/track/{trackId}"
            : normalized;
    }

    private static string? SelectSpotifyTrackImageUrl(IReadOnlyList<SpotifyImage>? images)
        => images?
            .Where(static image => !string.IsNullOrWhiteSpace(image.Url))
            .OrderByDescending(static image => image.Width ?? 0)
            .Select(static image => image.Url)
            .FirstOrDefault();

    private static bool ShouldIncludeAlbumGroup(string? albumGroup, IReadOnlyCollection<string> groups)
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
            AlbumGroup => AlbumGroup,
            SingleGroup => SingleGroup,
            CompilationGroup => CompilationGroup,
            "compile" => CompilationGroup,
            "compilations" => CompilationGroup,
            "appears-on" => AppearsOnGroup,
            "appears on" => AppearsOnGroup,
            AppearsOnGroup => AppearsOnGroup,
            "appearson" => AppearsOnGroup,
            _ => string.Empty
        };
    }

    private static string GetDeezerAlbumGroup(GwAlbumRelease release)
    {
        if (release.RoleId == 5)
        {
            return AppearsOnGroup;
        }

        return release.Type switch
        {
            0 => SingleGroup,
            1 => AlbumGroup,
            2 => CompilationGroup,
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
