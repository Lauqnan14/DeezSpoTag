using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DeezSpoTag.Services.Library;
using System.Linq;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyArtistService
{
    private const int ArtistCacheSchemaVersion = 9;
    private const string SpotifySource = "spotify";
    private const string AlbumType = "album";
    private const string SingleGroupType = "single";
    private const string EpGroupType = "ep";
    private const string CompilationGroupType = "compilation";
    private const string SinglesEpsSection = "singles_eps";
    private const string AlbumsSection = "albums";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DeezerEnrichmentTimeout = TimeSpan.FromSeconds(15);
    private readonly LibraryRepository _libraryRepository;
    private readonly ArtistPageCacheRepository _cacheRepository;
    private readonly LibraryConfigStore _configStore;
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly SpotifyMetadataService _metadataService;
    private readonly SpotifyDeezerLinkService _deezerLinkService;
    private readonly ILogger<SpotifyArtistService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static string ReplaceWithTimeout(string input, string pattern, string replacement, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
        => System.Text.RegularExpressions.Regex.Replace(input, pattern, replacement, options, RegexTimeout);

    public SpotifyArtistService(
        LibraryRepository libraryRepository,
        ArtistPageCacheRepository cacheRepository,
        LibraryConfigStore configStore,
        SpotifyPathfinderMetadataClient pathfinderMetadataClient,
        SpotifyMetadataService metadataService,
        SpotifyDeezerLinkService deezerLinkService,
        ILogger<SpotifyArtistService> logger)
    {
        _libraryRepository = libraryRepository;
        _cacheRepository = cacheRepository;
        _configStore = configStore;
        _pathfinderMetadataClient = pathfinderMetadataClient;
        _metadataService = metadataService;
        _deezerLinkService = deezerLinkService;
        _logger = logger;
    }

    public async Task<string?> EnsureSpotifyArtistIdAsync(long artistId, string artistName, CancellationToken cancellationToken)
    {
        var spotifyId = await _libraryRepository.GetArtistSourceIdAsync(artistId, SpotifySource, cancellationToken);
        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            return spotifyId;
        }

        if (string.IsNullOrWhiteSpace(artistName))
        {
            AddActivity("warn", "[spotify] artist ID resolve skipped: missing artist name.");
            return null;
        }

        spotifyId = await ResolveArtistIdBySpotiflacSearchAsync(artistName, artistId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            await _libraryRepository.UpsertArtistSourceIdAsync(artistId, SpotifySource, spotifyId, cancellationToken);
            AddActivity("info", $"[spotify] artist id resolved: {artistName} -> {spotifyId}.");
            return spotifyId;
        }

        AddActivity("warn", "[spotify] artist ID resolve failed.");
        return null;
    }

    public async Task<SpotifyAlbumPage?> FetchArtistAlbumsPageAsync(
        string spotifyId,
        IReadOnlyCollection<string> albumGroups,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return null;
        }

        var url = $"https://open.spotify.com/artist/{spotifyId}";
        var metadata = await _pathfinderMetadataClient.FetchByUrlAsync(url, cancellationToken);
        if (metadata is null || metadata.AlbumList.Count == 0)
        {
            return null;
        }

        var boundedLimit = Math.Clamp(limit, 1, 50);
        var boundedOffset = Math.Max(0, offset);
        var slice = metadata.AlbumList
            .Skip(boundedOffset)
            .Take(boundedLimit)
            .ToList();

        var albums = slice.Select(item =>
            new SpotifyAlbum(
                item.Id,
                item.Name,
                null,
                AlbumType,
                item.TotalTracks ?? 0,
                item.ImageUrl is null ? new List<SpotifyImage>() : new List<SpotifyImage> { new SpotifyImage(item.ImageUrl, null, null) },
                item.SourceUrl))
            .ToList();

        var total = metadata.AlbumList.Count;
        var hasMore = boundedOffset + boundedLimit < total;
        return new SpotifyAlbumPage(albums, total, hasMore);
    }

    public async Task<SpotifyArtistPageResult?> GetArtistPageAsync(
        long artistId,
        string artistName,
        bool forceRefresh,
        bool forceRematch,
        CancellationToken cancellationToken,
        bool includeDeezerLinking = true)
    {
        var allowCache = !forceRefresh && !forceRematch;

        var spotifyId = await _libraryRepository.GetArtistSourceIdAsync(artistId, SpotifySource, cancellationToken);
        if (forceRematch)
        {
            spotifyId = null;
        }
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            spotifyId = await EnsureSpotifyArtistIdAsync(artistId, artistName, cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return null;
        }

        // Library artist pages should use the Pathfinder-backed flow so rich fields and Deezer linkage stay consistent.
        var result = await GetArtistPageBySpotifyIdInternalAsync(
            spotifyId,
            artistName,
            allowCache,
            artistId,
            includeDeezerLinking,
            cancellationToken);
        if (result != null || forceRematch)
        {
            return result;
        }

        AddActivity("warn", $"[spotify] stored artist id failed fetch, rematching: {artistName} ({spotifyId}).");
        var rematchedSpotifyId = await ResolveArtistIdBySpotiflacSearchAsync(artistName, artistId, cancellationToken);
        if (string.IsNullOrWhiteSpace(rematchedSpotifyId))
        {
            return null;
        }

        if (!string.Equals(rematchedSpotifyId, spotifyId, StringComparison.Ordinal))
        {
            await _libraryRepository.UpsertArtistSourceIdAsync(artistId, SpotifySource, rematchedSpotifyId, cancellationToken);
            AddActivity("info", $"[spotify] rematched artist id: {artistName} -> {rematchedSpotifyId} (was {spotifyId}).");
        }

        return await GetArtistPageBySpotifyIdInternalAsync(
            rematchedSpotifyId,
            artistName,
            allowCache: false,
            artistId,
            includeDeezerLinking,
            cancellationToken);
    }

    public async Task<SpotifyArtistPageResult?> GetArtistPageByNameAsync(string artistName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var spotifyId = await ResolveArtistIdBySpotiflacSearchAsync(artistName, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return null;
        }

        return await GetArtistPageBySpotifyIdInternalAsync(spotifyId, artistName, true, null, includeDeezerLinking: true, cancellationToken);
    }

    public async Task<SpotifyArtistPageResult?> GetArtistPageBySpotifyIdAsync(
        string spotifyId,
        string? artistName,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return null;
        }

        var normalizedName = string.IsNullOrWhiteSpace(artistName) ? spotifyId : artistName.Trim();
        var allowCache = !forceRefresh;
        return await GetArtistPageBySpotifyIdInternalAsync(spotifyId, normalizedName, allowCache, null, includeDeezerLinking: true, cancellationToken);
    }

    public async Task<SpotifyArtistPageResult?> TryGetCachedArtistPageAsync(
        string spotifyId,
        string artistName,
        bool allowStale,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return null;
        }

        var cached = await _cacheRepository.TryGetAsync(SpotifySource, spotifyId, cancellationToken);
        if (cached == null)
        {
            return null;
        }

        if (allowStale)
        {
            if (!_cacheRepository.IsUsable(cached.FetchedUtc))
            {
                return null;
            }
        }
        else if (!_cacheRepository.IsFresh(cached.FetchedUtc))
        {
            return null;
        }

        var cachedPayload = DeserializeCached(cached.PayloadJson);
        if (cachedPayload == null)
        {
            return null;
        }

        var normalizedCachedPayload = EnsureArtistIdentity(cachedPayload, artistName);
        if (CachePayloadChanged(cachedPayload, normalizedCachedPayload))
        {
            var payloadJson = JsonSerializer.Serialize(new SpotifyArtistCacheEnvelope(ArtistCacheSchemaVersion, normalizedCachedPayload), _jsonOptions);
            await _cacheRepository.UpsertAsync(SpotifySource, spotifyId, payloadJson, cached.FetchedUtc, cancellationToken);
        }

        return normalizedCachedPayload;
    }

    private async Task<SpotifyArtistPageResult?> GetArtistPageBySpotifyIdInternalAsync(
        string spotifyId,
        string artistName,
        bool allowCache,
        long? localArtistId,
        bool includeDeezerLinking,
        CancellationToken cancellationToken)
    {
        var (cachedResult, staleCachedPayload) = await TryGetArtistPageFromCacheAsync(
            spotifyId,
            artistName,
            allowCache,
            cancellationToken);
        if (cachedResult != null)
        {
            return cachedResult;
        }

        AddActivity("info", $"[spotify] pathfinder fetch: {artistName}.");
        var artistPage = await TryFetchSpotifyAsync(
            ct => _pathfinderMetadataClient.FetchArtistHydratedPageAsync(spotifyId, ct),
            artistName,
            "artist page",
            cancellationToken);
        if (artistPage is null)
        {
            if (staleCachedPayload != null)
            {
                AddActivity("warn", $"[spotify] pathfinder fetch failed; serving stale cache for {artistName}.");
                return staleCachedPayload;
            }
            return null;
        }

        var result = await BuildArtistPageResultAsync(
            spotifyId,
            artistName,
            artistPage,
            staleCachedPayload,
            cancellationToken);
        result = await TryEnrichWithDeezerLinksAsync(result, artistName, includeDeezerLinking, localArtistId, cancellationToken);
        await PersistArtistPageResultAsync(spotifyId, artistName, result, cancellationToken);
        return result;
    }

    private async Task<(SpotifyArtistPageResult? CachedResult, SpotifyArtistPageResult? StalePayload)> TryGetArtistPageFromCacheAsync(
        string spotifyId,
        string artistName,
        bool allowCache,
        CancellationToken cancellationToken)
    {
        var cached = await _cacheRepository.TryGetAsync(SpotifySource, spotifyId, cancellationToken);
        if (cached is null)
        {
            return (null, null);
        }

        if (!allowCache)
        {
            AddActivity("info", $"[spotify] cache bypassed due refresh/rematch: {artistName}.");
            return (null, null);
        }

        var cachedPayload = DeserializeCached(cached.PayloadJson);
        if (cachedPayload is null)
        {
            AddActivity("info", $"[spotify] cache invalidated (schema mismatch): {artistName}.");
            return (null, null);
        }

        var normalizedCachedPayload = EnsureArtistIdentity(cachedPayload, artistName);
        await UpsertNormalizedCachePayloadIfChangedAsync(
            spotifyId,
            cachedPayload,
            normalizedCachedPayload,
            cached.FetchedUtc,
            cancellationToken);

        if (_cacheRepository.IsFresh(cached.FetchedUtc))
        {
            AddActivity("info", $"[spotify] cache hit (fresh): {artistName}.");
            return (normalizedCachedPayload, normalizedCachedPayload);
        }

        if (_cacheRepository.IsUsable(cached.FetchedUtc))
        {
            AddActivity("info", $"[spotify] cache hit (stale, serving cached): {artistName}.");
            return (normalizedCachedPayload, normalizedCachedPayload);
        }

        AddActivity("info", $"[spotify] cache expired beyond usable window; refreshing: {artistName}.");
        return (null, normalizedCachedPayload);
    }

    private async Task UpsertNormalizedCachePayloadIfChangedAsync(
        string spotifyId,
        SpotifyArtistPageResult cachedPayload,
        SpotifyArtistPageResult normalizedCachedPayload,
        DateTimeOffset fetchedUtc,
        CancellationToken cancellationToken)
    {
        if (!CachePayloadChanged(cachedPayload, normalizedCachedPayload))
        {
            return;
        }

        var normalizedPayloadJson = JsonSerializer.Serialize(
            new SpotifyArtistCacheEnvelope(ArtistCacheSchemaVersion, normalizedCachedPayload),
            _jsonOptions);
        await _cacheRepository.UpsertAsync(
            SpotifySource,
            spotifyId,
            normalizedPayloadJson,
            fetchedUtc,
            cancellationToken);
    }

    private async Task<SpotifyArtistPageResult> BuildArtistPageResultAsync(
        string spotifyId,
        string artistName,
        SpotifyArtistHydratedPage artistPage,
        SpotifyArtistPageResult? staleCachedPayload,
        CancellationToken cancellationToken)
    {
        var profile = await BuildArtistProfileAsync(spotifyId, artistName, artistPage, cancellationToken);
        var albums = BuildArtistAlbums(artistPage);
        var topTracks = BuildArtistTopTracks(artistPage, albums);
        var relatedArtists = artistPage.RelatedArtists ?? new List<SpotifyRelatedArtist>();
        var appearsOn = BuildAppearsOnAlbums(artistPage);

        var result = new SpotifyArtistPageResult(
            true,
            profile,
            albums,
            appearsOn,
            topTracks,
            relatedArtists);
        return MergeWithStalePayload(result, staleCachedPayload);
    }

    private async Task<SpotifyArtistProfile> BuildArtistProfileAsync(
        string spotifyId,
        string artistName,
        SpotifyArtistHydratedPage artistPage,
        CancellationToken cancellationToken)
    {
        var images = new List<SpotifyImage>();
        if (!string.IsNullOrWhiteSpace(artistPage.Overview.ImageUrl))
        {
            images.Add(new SpotifyImage(artistPage.Overview.ImageUrl, null, null));
        }

        var profile = new SpotifyArtistProfile(
            artistPage.Overview.Id ?? spotifyId,
            ResolveArtistDisplayName(artistPage.Overview.Name, artistName),
            images,
            artistPage.Overview.Genres ?? new List<string>(),
            artistPage.Overview.Followers ?? 0,
            artistPage.Overview.Popularity ?? 0,
            artistPage.Overview.SourceUrl,
            artistPage.Extras.Biography,
            artistPage.Extras.Verified,
            artistPage.Extras.MonthlyListeners,
            artistPage.Extras.Rank,
            artistPage.Overview.HeaderImageUrl,
            artistPage.Overview.Gallery ?? new List<string>(),
            artistPage.Overview.DiscographyType,
            artistPage.Overview.TotalAlbums);

        if (profile.Genres.Count > 0)
        {
            return profile;
        }

        var inferredGenres = await _metadataService.FetchArtistGenresFromSpotifyAsync(spotifyId, cancellationToken);
        if (inferredGenres.Count == 0)
        {
            return profile;
        }

        AddActivity("info", $"[spotify] artist genre fallback: {artistName} -> {string.Join(", ", inferredGenres)}.");
        return profile with { Genres = inferredGenres };
    }

    private static List<SpotifyAlbum> BuildArtistAlbums(SpotifyArtistHydratedPage artistPage)
    {
        var popularReleaseAlbumIds = artistPage.Overview.PopularReleaseAlbumIds is { Count: > 0 }
            ? new HashSet<string>(artistPage.Overview.PopularReleaseAlbumIds, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return (artistPage.Albums ?? new List<SpotifyAlbumSummary>())
            .Select(item =>
            {
                var albumGroup = ResolveAlbumGroup(item.AlbumGroup, item.ReleaseType);
                var section = ResolveDiscographySection(albumGroup, item.ReleaseType);
                var isPopular = !string.IsNullOrWhiteSpace(item.Id) && popularReleaseAlbumIds.Contains(item.Id);
                return new SpotifyAlbum(
                    item.Id,
                    item.Name,
                    item.ReleaseDate,
                    albumGroup,
                    item.TotalTracks ?? 0,
                    item.ImageUrl is null ? new List<SpotifyImage>() : new List<SpotifyImage> { new SpotifyImage(item.ImageUrl, null, null) },
                    item.SourceUrl,
                    DiscographySection: section,
                    IsPopular: isPopular)
                {
                    Genres = item.Genres,
                    Label = item.Label,
                    Popularity = item.Popularity,
                    ReleaseDatePrecision = item.ReleaseDatePrecision,
                    AvailableMarkets = item.AvailableMarkets,
                    Copyrights = item.Copyrights,
                    CopyrightText = item.CopyrightText,
                    Review = item.Review,
                    RelatedAlbumIds = item.RelatedAlbumIds,
                    OriginalTitle = item.OriginalTitle,
                    VersionTitle = item.VersionTitle,
                    SalePeriods = item.SalePeriods,
                    Availability = item.Availability
                };
            })
            .ToList();
    }

    private static List<SpotifyTrack> BuildArtistTopTracks(
        SpotifyArtistHydratedPage artistPage,
        IReadOnlyList<SpotifyAlbum> albums)
    {
        var tracks = (artistPage.TopTracks ?? new List<SpotifyTrackSummary>())
            .Select(track => new SpotifyTrack(
                track.Id,
                track.Name,
                track.DurationMs ?? 0,
                track.Popularity ?? 0,
                track.PreviewUrl,
                track.SourceUrl,
                string.IsNullOrWhiteSpace(track.ImageUrl)
                    ? new List<SpotifyImage>()
                    : new List<SpotifyImage> { new SpotifyImage(track.ImageUrl, null, null) },
                track.Album,
                track.ReleaseDate,
                null,
                null,
                track.AlbumGroup,
                track.ReleaseType,
                track.TrackTotal)
            {
                Isrc = track.Isrc,
                AlbumId = track.AlbumId,
                Explicit = track.Explicit,
                HasLyrics = track.HasLyrics
            })
            .ToList();
        return EnrichTopTracksWithAlbumReleaseDates(tracks, albums);
    }

    private static List<SpotifyAlbum> BuildAppearsOnAlbums(SpotifyArtistHydratedPage artistPage)
    {
        return artistPage.AppearsOn?
            .Select(item =>
                new SpotifyAlbum(
                    item.Id,
                    item.Name,
                    item.ReleaseDate,
                    "appears_on",
                    item.TotalTracks ?? 0,
                    item.ImageUrl is null ? new List<SpotifyImage>() : new List<SpotifyImage> { new SpotifyImage(item.ImageUrl, null, null) },
                    item.SourceUrl)
                {
                    Genres = item.Genres,
                    Label = item.Label,
                    Popularity = item.Popularity,
                    ReleaseDatePrecision = item.ReleaseDatePrecision,
                    AvailableMarkets = item.AvailableMarkets,
                    Copyrights = item.Copyrights,
                    CopyrightText = item.CopyrightText,
                    Review = item.Review,
                    RelatedAlbumIds = item.RelatedAlbumIds,
                    OriginalTitle = item.OriginalTitle,
                    VersionTitle = item.VersionTitle,
                    SalePeriods = item.SalePeriods,
                    Availability = item.Availability
                })
            .ToList() ?? new List<SpotifyAlbum>();
    }

    private async Task<SpotifyArtistPageResult> TryEnrichWithDeezerLinksAsync(
        SpotifyArtistPageResult result,
        string artistName,
        bool includeDeezerLinking,
        long? localArtistId,
        CancellationToken cancellationToken)
    {
        if (!includeDeezerLinking || !localArtistId.HasValue)
        {
            return result;
        }

        try
        {
            AddActivity("info", $"[spotify] link to Deezer: {artistName}.");
            using var deezerLinkCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deezerLinkCts.CancelAfter(DeezerEnrichmentTimeout);
            var enriched = await _deezerLinkService.EnrichAsync(localArtistId.Value, artistName, result, deezerLinkCts.Token);
            AddActivity("info", $"[spotify] Deezer linking complete: {artistName}.");
            return enriched;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AddActivity("warn",
                $"[spotify] Deezer linking timed out after {DeezerEnrichmentTimeout.TotalSeconds:0}s for {artistName}; serving Spotify data without Deezer links.");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddActivity("warn", $"[spotify] Deezer linking failed: {artistName} ({ex.Message}).");
            return result;
        }
    }

    private async Task PersistArtistPageResultAsync(
        string spotifyId,
        string artistName,
        SpotifyArtistPageResult result,
        CancellationToken cancellationToken)
    {
        result = EnsureArtistIdentity(result, artistName);
        var payloadJson = JsonSerializer.Serialize(new SpotifyArtistCacheEnvelope(ArtistCacheSchemaVersion, result), _jsonOptions);
        await _cacheRepository.UpsertAsync(SpotifySource, spotifyId, payloadJson, DateTimeOffset.UtcNow, cancellationToken);
        await _cacheRepository.UpsertGenresAsync(SpotifySource, spotifyId, result.Artist.Genres, cancellationToken);
        QueueArtistFallbackEnrichment(spotifyId, artistName, result);
        QueueArtistTopTrackIsrcEnrichment(spotifyId, artistName, result);
        AddActivity("info", $"[spotify] data stored: {artistName} (id={spotifyId}).");
        AddActivity("info", $"[spotify] fetch complete: {artistName} (albums={result.Albums.Count}, appears_on={result.AppearsOn.Count}, top_tracks={result.TopTracks.Count}, related={result.RelatedArtists.Count}).");
    }

    private static SpotifyArtistPageResult MergeWithStalePayload(
        SpotifyArtistPageResult primary,
        SpotifyArtistPageResult? fallback)
    {
        if (fallback is null)
        {
            return primary;
        }

        return primary with
        {
            Artist = MergeArtistProfileFromFallback(primary.Artist, fallback.Artist),
            Albums = MergeAlbums(primary.Albums, fallback.Albums),
            AppearsOn = PreferExistingList(primary.AppearsOn, fallback.AppearsOn),
            TopTracks = MergeTopTracks(primary.TopTracks, fallback.TopTracks),
            RelatedArtists = PreferExistingList(primary.RelatedArtists, fallback.RelatedArtists)
        };
    }

    private static SpotifyArtistProfile MergeArtistProfileFromFallback(
        SpotifyArtistProfile primary,
        SpotifyArtistProfile fallback)
    {
        return primary with
        {
            Biography = PreferBiography(primary.Biography, fallback.Biography),
            HeaderImageUrl = PreferString(primary.HeaderImageUrl, fallback.HeaderImageUrl),
            Gallery = PreferExistingList(primary.Gallery, fallback.Gallery),
            Images = PreferExistingList(primary.Images, fallback.Images),
            Genres = PreferExistingList(primary.Genres, fallback.Genres),
            ActivityPeriods = PreferExistingOptionalList(primary.ActivityPeriods, fallback.ActivityPeriods),
            SalePeriods = PreferExistingOptionalList(primary.SalePeriods, fallback.SalePeriods),
            Availability = PreferExistingOptionalList(primary.Availability, fallback.Availability),
            IsPortraitAlbumCover = primary.IsPortraitAlbumCover ?? fallback.IsPortraitAlbumCover
        };
    }

    private static string? PreferBiography(string? primary, string? fallback)
    {
        return IsPlaceholderBiography(primary) && !IsPlaceholderBiography(fallback)
            ? fallback
            : primary;
    }

    private static string? PreferString(string? primary, string? fallback)
    {
        return string.IsNullOrWhiteSpace(primary) && !string.IsNullOrWhiteSpace(fallback)
            ? fallback
            : primary;
    }

    private static List<T> PreferExistingList<T>(List<T> primary, List<T> fallback)
    {
        return primary.Count > 0 ? primary : fallback;
    }

    private static List<T>? PreferExistingOptionalList<T>(List<T>? primary, List<T>? fallback)
    {
        if (primary is { Count: > 0 })
        {
            return primary;
        }

        return fallback is { Count: > 0 } ? fallback : primary;
    }

    private static bool IsPlaceholderBiography(string? biography)
    {
        var normalized = (biography ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        return normalized.Equals("N/A", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<SpotifyTrack>> EnrichTopTracksWithIsrcsAsync(
        IReadOnlyList<SpotifyTrack> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return tracks.ToList();
        }

        var missingTrackIds = tracks
            .Where(track => string.IsNullOrWhiteSpace(track.Isrc))
            .Select(track => track.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingTrackIds.Count == 0)
        {
            return tracks.ToList();
        }

        Dictionary<string, string> isrcsByTrackId;
        try
        {
            isrcsByTrackId = await _pathfinderMetadataClient.FetchTrackIsrcsAsync(missingTrackIds, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to hydrate Spotify artist top-track ISRCs.");
            return tracks.ToList();
        }

        if (isrcsByTrackId.Count == 0)
        {
            return tracks.ToList();
        }

        return tracks
            .Select(track =>
            {
                if (!string.IsNullOrWhiteSpace(track.Isrc))
                {
                    return track;
                }

                var trackId = track.Id?.Trim();
                if (string.IsNullOrWhiteSpace(trackId)
                    || !isrcsByTrackId.TryGetValue(trackId, out var hydratedIsrc)
                    || string.IsNullOrWhiteSpace(hydratedIsrc))
                {
                    return track;
                }

                return track with { Isrc = hydratedIsrc.Trim() };
            })
            .ToList();
    }

    private static List<SpotifyAlbum> MergeAlbums(
        IReadOnlyList<SpotifyAlbum> primary,
        IReadOnlyList<SpotifyAlbum> fallback)
    {
        if (primary.Count == 0)
        {
            return fallback.ToList();
        }
        if (fallback.Count == 0)
        {
            return primary.ToList();
        }

        var fallbackByKey = BuildFallbackAlbumMap(fallback);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = MergePrimaryAlbumsWithFallback(primary, fallbackByKey, seen, fallback.Count);
        AddMissingFallbackAlbums(merged, fallback, seen);
        merged.Sort(CompareAlbumReleaseDateDesc);
        return merged;
    }

    private static Dictionary<string, SpotifyAlbum> BuildFallbackAlbumMap(IReadOnlyList<SpotifyAlbum> fallback)
    {
        var fallbackByKey = new Dictionary<string, SpotifyAlbum>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in fallback)
        {
            var key = BuildAlbumMergeKey(album.Id, album.Name);
            if (string.IsNullOrWhiteSpace(key) || fallbackByKey.ContainsKey(key))
            {
                continue;
            }

            fallbackByKey[key] = album;
        }

        return fallbackByKey;
    }

    private static List<SpotifyAlbum> MergePrimaryAlbumsWithFallback(
        IReadOnlyList<SpotifyAlbum> primary,
        Dictionary<string, SpotifyAlbum> fallbackByKey,
        HashSet<string> seen,
        int fallbackCount)
    {
        var merged = new List<SpotifyAlbum>(primary.Count + Math.Min(8, fallbackCount));
        foreach (var album in primary)
        {
            var key = BuildAlbumMergeKey(album.Id, album.Name);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            seen.Add(key);
            fallbackByKey.TryGetValue(key, out var fallbackAlbum);
            merged.Add(fallbackAlbum is null ? album : MergeAlbumWithFallback(album, fallbackAlbum));
        }

        return merged;
    }

    private static SpotifyAlbum MergeAlbumWithFallback(SpotifyAlbum album, SpotifyAlbum fallbackAlbum)
    {
        return album with
        {
            ReleaseDate = string.IsNullOrWhiteSpace(album.ReleaseDate) ? fallbackAlbum.ReleaseDate : album.ReleaseDate,
            AlbumGroup = string.IsNullOrWhiteSpace(album.AlbumGroup) ? fallbackAlbum.AlbumGroup : album.AlbumGroup,
            TotalTracks = album.TotalTracks > 0 ? album.TotalTracks : fallbackAlbum.TotalTracks,
            Images = album.Images.Count > 0 ? album.Images : fallbackAlbum.Images,
            SourceUrl = string.IsNullOrWhiteSpace(album.SourceUrl) ? fallbackAlbum.SourceUrl : album.SourceUrl,
            DeezerId = string.IsNullOrWhiteSpace(album.DeezerId) ? fallbackAlbum.DeezerId : album.DeezerId,
            DeezerUrl = string.IsNullOrWhiteSpace(album.DeezerUrl) ? fallbackAlbum.DeezerUrl : album.DeezerUrl,
            DiscographySection = string.IsNullOrWhiteSpace(album.DiscographySection) ? fallbackAlbum.DiscographySection : album.DiscographySection,
            IsPopular = album.IsPopular || fallbackAlbum.IsPopular
        };
    }

    private static void AddMissingFallbackAlbums(
        List<SpotifyAlbum> merged,
        IReadOnlyList<SpotifyAlbum> fallback,
        HashSet<string> seen)
    {
        foreach (var album in fallback)
        {
            var key = BuildAlbumMergeKey(album.Id, album.Name);
            if (string.IsNullOrWhiteSpace(key) || seen.Contains(key))
            {
                continue;
            }

            merged.Add(album);
        }
    }

    private static int CompareAlbumReleaseDateDesc(SpotifyAlbum left, SpotifyAlbum right)
    {
        var leftDate = left.ReleaseDate ?? string.Empty;
        var rightDate = right.ReleaseDate ?? string.Empty;
        return string.Compare(rightDate, leftDate, StringComparison.Ordinal);
    }

    private static List<SpotifyTrack> MergeTopTracks(
        IReadOnlyList<SpotifyTrack> primary,
        IReadOnlyList<SpotifyTrack> fallback)
    {
        if (primary.Count == 0)
        {
            return fallback.ToList();
        }
        if (fallback.Count == 0)
        {
            return primary.ToList();
        }

        var fallbackById = BuildFallbackTopTrackMap(fallback);
        return primary.Select(track => MergeTopTrackWithFallback(track, fallbackById)).ToList();
    }

    private static Dictionary<string, SpotifyTrack> BuildFallbackTopTrackMap(IReadOnlyList<SpotifyTrack> fallback)
    {
        return fallback
            .Where(track => !string.IsNullOrWhiteSpace(track.Id))
            .GroupBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static SpotifyTrack MergeTopTrackWithFallback(
        SpotifyTrack track,
        Dictionary<string, SpotifyTrack> fallbackById)
    {
        if (string.IsNullOrWhiteSpace(track.Id) || !fallbackById.TryGetValue(track.Id, out var fallbackTrack))
        {
            return track;
        }

        return track with
        {
            ReleaseDate = string.IsNullOrWhiteSpace(track.ReleaseDate) ? fallbackTrack.ReleaseDate : track.ReleaseDate,
            AlbumName = string.IsNullOrWhiteSpace(track.AlbumName) ? fallbackTrack.AlbumName : track.AlbumName,
            AlbumGroup = string.IsNullOrWhiteSpace(track.AlbumGroup) ? fallbackTrack.AlbumGroup : track.AlbumGroup,
            ReleaseType = string.IsNullOrWhiteSpace(track.ReleaseType) ? fallbackTrack.ReleaseType : track.ReleaseType,
            AlbumTrackTotal = track.AlbumTrackTotal is > 0 ? track.AlbumTrackTotal : fallbackTrack.AlbumTrackTotal,
            AlbumId = string.IsNullOrWhiteSpace(track.AlbumId) ? fallbackTrack.AlbumId : track.AlbumId
        };
    }

    private static string BuildAlbumMergeKey(string? albumId, string? albumName)
    {
        if (!string.IsNullOrWhiteSpace(albumId))
        {
            return $"id:{albumId.Trim()}";
        }

        var normalizedName = NormalizeTitle(albumName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            return $"name:{normalizedName}";
        }

        return string.Empty;
    }

    private static List<SpotifyTrack> EnrichTopTracksWithAlbumReleaseDates(
        IReadOnlyList<SpotifyTrack> tracks,
        IReadOnlyList<SpotifyAlbum> albums)
    {
        if (tracks.Count == 0 || albums.Count == 0)
        {
            return tracks.ToList();
        }

        var lookup = BuildAlbumReleaseLookup(albums);
        return tracks
            .Select(track => track with
            {
                ReleaseDate = ResolveTrackReleaseDate(track, lookup),
                AlbumGroup = ResolveTrackAlbumGroup(track, lookup),
                ReleaseType = ResolveTrackReleaseType(track, lookup)
            })
            .ToList();
    }

    private static AlbumReleaseLookup BuildAlbumReleaseLookup(IReadOnlyList<SpotifyAlbum> albums)
    {
        var dateByAlbumId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dateByAlbumName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupByAlbumId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var typeByAlbumId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var album in albums)
        {
            if (string.IsNullOrWhiteSpace(album.ReleaseDate))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(album.Id))
            {
                dateByAlbumId[album.Id] = album.ReleaseDate;
                if (!string.IsNullOrWhiteSpace(album.AlbumGroup))
                {
                    groupByAlbumId[album.Id] = album.AlbumGroup;
                }
                if (!string.IsNullOrWhiteSpace(album.AlbumGroup))
                {
                    typeByAlbumId[album.Id] = album.AlbumGroup;
                }
            }

            var normalizedName = NormalizeTitle(album.Name);
            if (!string.IsNullOrWhiteSpace(normalizedName))
            {
                dateByAlbumName[normalizedName] = album.ReleaseDate;
            }
        }

        return new AlbumReleaseLookup(dateByAlbumId, dateByAlbumName, groupByAlbumId, typeByAlbumId);
    }

    private static string? ResolveTrackReleaseDate(SpotifyTrack track, AlbumReleaseLookup lookup)
    {
        if (!string.IsNullOrWhiteSpace(track.ReleaseDate))
        {
            return track.ReleaseDate;
        }

        if (!string.IsNullOrWhiteSpace(track.AlbumId)
            && lookup.DateByAlbumId.TryGetValue(track.AlbumId, out var byIdDate))
        {
            return byIdDate;
        }

        var normalizedAlbumName = NormalizeTitle(track.AlbumName ?? string.Empty);
        return !string.IsNullOrWhiteSpace(normalizedAlbumName)
            && lookup.DateByAlbumName.TryGetValue(normalizedAlbumName, out var byNameDate)
            ? byNameDate
            : track.ReleaseDate;
    }

    private static string? ResolveTrackAlbumGroup(SpotifyTrack track, AlbumReleaseLookup lookup)
    {
        if (!string.IsNullOrWhiteSpace(track.AlbumGroup))
        {
            return track.AlbumGroup;
        }

        return !string.IsNullOrWhiteSpace(track.AlbumId)
            && lookup.GroupByAlbumId.TryGetValue(track.AlbumId, out var albumGroup)
            ? albumGroup
            : track.AlbumGroup;
    }

    private static string? ResolveTrackReleaseType(SpotifyTrack track, AlbumReleaseLookup lookup)
    {
        if (!string.IsNullOrWhiteSpace(track.ReleaseType))
        {
            return track.ReleaseType;
        }

        return !string.IsNullOrWhiteSpace(track.AlbumId)
            && lookup.TypeByAlbumId.TryGetValue(track.AlbumId, out var releaseType)
            ? releaseType
            : track.ReleaseType;
    }

    private sealed record AlbumReleaseLookup(
        Dictionary<string, string> DateByAlbumId,
        Dictionary<string, string> DateByAlbumName,
        Dictionary<string, string> GroupByAlbumId,
        Dictionary<string, string> TypeByAlbumId);

    private sealed record RankedSpotifyCandidate(
        SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate Candidate,
        int Score,
        DeezerValidationResult Validation,
        int LocalOverlap);

    private async Task<T?> TryFetchSpotifyAsync<T>(
        Func<CancellationToken, Task<T?>> fetch,
        string artistName,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await fetch(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AddActivity("warn", $"[spotify] timeout while fetching {operation} for {artistName}.");
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddActivity("warn", $"[spotify] failed fetching {operation} for {artistName}: {ex.Message}");
            return default;
        }
    }

    private SpotifyArtistPageResult? DeserializeCached(string payloadJson)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<SpotifyArtistCacheEnvelope>(payloadJson, _jsonOptions);
            if (envelope is null || envelope.Version != ArtistCacheSchemaVersion || envelope.Payload is null)
            {
                return null;
            }

            return envelope.Payload;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to deserialize cached Spotify artist payload.");
            return null;
        }
    }

    private static SpotifyArtistPageResult EnsureArtistIdentity(SpotifyArtistPageResult payload, string fallbackArtistName)
    {
        var resolvedName = ResolveArtistDisplayName(payload.Artist.Name, fallbackArtistName);
        if (string.Equals(payload.Artist.Name, resolvedName, StringComparison.Ordinal))
        {
            return payload;
        }

        return payload with
        {
            Artist = payload.Artist with
            {
                Name = resolvedName
            }
        };
    }

    private static string ResolveArtistDisplayName(string? candidate, string? fallbackArtistName)
    {
        var normalizedCandidate = (candidate ?? string.Empty).Trim();
        var normalizedFallback = (fallbackArtistName ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(normalizedCandidate)
            && !string.Equals(normalizedCandidate, "Spotify artist", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedCandidate;
        }

        if (!string.IsNullOrWhiteSpace(normalizedFallback))
        {
            return normalizedFallback;
        }

        if (!string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return normalizedCandidate;
        }

        return "Unknown Artist";
    }

    private static string ResolveAlbumGroup(string? albumGroup, string? releaseType)
    {
        var normalizedGroup = (albumGroup ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedGroup is AlbumType or SingleGroupType or EpGroupType or CompilationGroupType)
        {
            return normalizedGroup;
        }

        var normalizedType = (releaseType ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedType == "EP")
        {
            return "ep";
        }
        if (normalizedType == "SINGLE")
        {
            return SingleGroupType;
        }
        if (normalizedType == "COMPILATION")
        {
            return CompilationGroupType;
        }
        if (normalizedType == "ALBUM")
        {
            return AlbumType;
        }

        return AlbumType;
    }

    private static string ResolveDiscographySection(
        string albumGroup,
        string? releaseType)
    {
        var normalizedGroup = (albumGroup ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedGroup is SingleGroupType or EpGroupType)
        {
            return SinglesEpsSection;
        }
        if (normalizedGroup is AlbumType or CompilationGroupType)
        {
            return AlbumsSection;
        }

        var normalizedType = (releaseType ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedType is "SINGLE" or "EP")
        {
            return SinglesEpsSection;
        }

        return AlbumsSection;
    }

    private static bool CachePayloadChanged(SpotifyArtistPageResult original, SpotifyArtistPageResult normalized)
    {
        return !string.Equals(original.Artist.Name, normalized.Artist.Name, StringComparison.Ordinal);
    }

    private static string NormalizeTitle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var chars = new char[input.Length];
        var index = 0;
        var lastWasSpace = false;
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch))
            {
                chars[index++] = char.ToLowerInvariant(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                chars[index++] = ' ';
                lastWasSpace = true;
            }
        }

        return new string(chars, 0, index).Trim();
    }

    private void QueueArtistFallbackEnrichment(
        string spotifyId,
        string artistName,
        SpotifyArtistPageResult baseResult)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var fallbackArtist = await _metadataService.FetchArtistFallbackWithLibrespotAsync(
                    spotifyId,
                    CancellationToken.None);
                if (fallbackArtist is null)
                {
                    return;
                }

                var enriched = ApplyArtistFallback(baseResult, fallbackArtist);
                if (ReferenceEquals(enriched, baseResult))
                {
                    return;
                }

                enriched = EnsureArtistIdentity(enriched, artistName);
                var payloadJson = JsonSerializer.Serialize(
                    new SpotifyArtistCacheEnvelope(ArtistCacheSchemaVersion, enriched),
                    _jsonOptions);
                await _cacheRepository.UpsertAsync(
                    SpotifySource,
                    spotifyId,
                    payloadJson,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                await _cacheRepository.UpsertGenresAsync(
                    SpotifySource,
                    spotifyId,
                    enriched.Artist.Genres,
                    CancellationToken.None);
                AddActivity("info", $"[spotify] librespot fallback cached lazily: {artistName}.");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Spotify artist librespot fallback enrichment failed for {ArtistId}", spotifyId);
            }
        });
    }

    private void QueueArtistTopTrackIsrcEnrichment(
        string spotifyId,
        string artistName,
        SpotifyArtistPageResult baseResult)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var enrichedTopTracks = await EnrichTopTracksWithIsrcsAsync(
                    baseResult.TopTracks,
                    CancellationToken.None);
                if (enrichedTopTracks.Count == 0 || !HaveTopTrackIsrcsChanged(baseResult.TopTracks, enrichedTopTracks))
                {
                    return;
                }

                var enriched = baseResult with { TopTracks = enrichedTopTracks };
                var payloadJson = JsonSerializer.Serialize(
                    new SpotifyArtistCacheEnvelope(ArtistCacheSchemaVersion, enriched),
                    _jsonOptions);
                await _cacheRepository.UpsertAsync(
                    SpotifySource,
                    spotifyId,
                    payloadJson,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                AddActivity("info", $"[spotify] top-track ISRCs cached lazily: {artistName}.");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Spotify artist top-track ISRC enrichment failed for {ArtistId}", spotifyId);
            }
        });
    }

    private static bool HaveTopTrackIsrcsChanged(
        IReadOnlyList<SpotifyTrack> current,
        IReadOnlyList<SpotifyTrack> enriched)
    {
        if (current.Count != enriched.Count)
        {
            return true;
        }

        for (var i = 0; i < current.Count; i++)
        {
            var before = current[i];
            var after = enriched[i];
            if (!string.Equals(before.Id, after.Id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(before.Isrc, after.Isrc, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static SpotifyArtistPageResult ApplyArtistFallback(
        SpotifyArtistPageResult result,
        SpotifyArtistFallbackMetadata fallback)
    {
        var artist = result.Artist with
        {
            Images = ResolveFallbackImages(result.Artist.Images, fallback.ImageUrl),
            Genres = PreferNonEmptyList(result.Artist.Genres, fallback.Genres),
            Popularity = PreferPopularity(result.Artist.Popularity, fallback.Popularity),
            Biography = PreferBiography(result.Artist.Biography, fallback.Biography),
            Gallery = PreferNonEmptyList(result.Artist.Gallery, fallback.Gallery),
            ActivityPeriods = PreferOptionalList(result.Artist.ActivityPeriods, fallback.ActivityPeriods),
            SalePeriods = PreferOptionalList(result.Artist.SalePeriods, fallback.SalePeriods),
            Availability = PreferOptionalList(result.Artist.Availability, fallback.Availability),
            IsPortraitAlbumCover = result.Artist.IsPortraitAlbumCover ?? fallback.IsPortraitAlbumCover
        };
        var relatedArtists = PreferNonEmptyList(result.RelatedArtists, fallback.RelatedArtists);

        return EqualityComparer<SpotifyArtistProfile>.Default.Equals(artist, result.Artist)
               && ReferenceEquals(relatedArtists, result.RelatedArtists)
            ? result
            : result with { Artist = artist, RelatedArtists = relatedArtists };
    }

    private static List<SpotifyImage> ResolveFallbackImages(List<SpotifyImage> primaryImages, string? fallbackImageUrl)
    {
        if (primaryImages.Count > 0 || string.IsNullOrWhiteSpace(fallbackImageUrl))
        {
            return primaryImages;
        }

        return new List<SpotifyImage> { new(fallbackImageUrl, null, null) };
    }

    private static int PreferPopularity(int primaryPopularity, int? fallbackPopularity)
    {
        return primaryPopularity <= 0 && fallbackPopularity.HasValue
            ? fallbackPopularity.Value
            : primaryPopularity;
    }

    private static List<T> PreferNonEmptyList<T>(List<T> primary, IReadOnlyList<T>? fallback)
    {
        if (primary.Count > 0 || fallback is not { Count: > 0 })
        {
            return primary;
        }

        return fallback.ToList();
    }

    private static List<T>? PreferOptionalList<T>(List<T>? primary, IReadOnlyList<T>? fallback)
    {
        if (primary is { Count: > 0 } || fallback is not { Count: > 0 })
        {
            return primary;
        }

        return fallback.ToList();
    }

    private async Task<string?> ResolveArtistIdBySpotiflacSearchAsync(
        string artistName,
        long? localArtistId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        try
        {
            var results = await _pathfinderMetadataClient.SearchArtistsAsync(artistName, 10, cancellationToken);
            if (results.Count == 0)
            {
                AddActivity("warn", $"[spotify] artist ID resolve failed: pathfinder search unavailable for {artistName}.");
                return null;
            }

            var candidates = BuildArtistSearchCandidates(results, artistName);
            var exactCandidates = candidates
                .Where(candidate => IsEquivalentArtistName(candidate.Name, artistName))
                .ToList();
            var localAlbumTitleSet = await TryGetLocalAlbumTitleSetAsync(localArtistId, cancellationToken);

            if (exactCandidates.Count == 0)
            {
                AddActivity("warn", $"[spotify] artist ID resolve failed: no exact artist-name match for {artistName}.");
                return null;
            }

            var best = await SelectBestExactArtistCandidateAsync(exactCandidates, localAlbumTitleSet, cancellationToken);
            if (best is not null)
            {
                AddActivity("info",
                    $"[spotify] candidate {best.Candidate.Id} selected for {artistName} " +
                    $"(exact_name=true, local_album_overlap={best.LocalAlbumOverlap}).");
                return best.Candidate.Id;
            }

            AddActivity("warn", $"[spotify] artist ID resolve failed: no suitable exact-name candidate for {artistName}.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify Pathfinder artist search failed.");
            AddActivity("warn", $"[spotify] artist ID resolve failed: pathfinder search error for {artistName}.");
            return null;
        }
    }

    private sealed record ExactArtistCandidateSelection(
        SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate Candidate,
        int LocalAlbumOverlap);

    private async Task<ExactArtistCandidateSelection?> SelectBestExactArtistCandidateAsync(
        IReadOnlyList<SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate> exactCandidates,
        HashSet<string> localAlbumTitleSet,
        CancellationToken cancellationToken)
    {
        ExactArtistCandidateSelection? best = null;
        var bestScore = int.MinValue;
        var requireLocalAlbumOverlap = localAlbumTitleSet.Count > 0;

        foreach (var candidate in exactCandidates)
        {
            var localAlbumOverlap = 0;
            if (requireLocalAlbumOverlap)
            {
                var candidateAlbumTitles = await FetchCandidateAlbumTitlesAsync(candidate.Id, cancellationToken);
                localAlbumOverlap = ComputeLocalAlbumOverlap(localAlbumTitleSet, candidateAlbumTitles);
                if (localAlbumOverlap <= 0)
                {
                    continue;
                }
            }

            var info = await _pathfinderMetadataClient.GetArtistCandidateInfoAsync(candidate.Id, cancellationToken);
            var baseScore = localAlbumOverlap * 10_000;
            if (info is null)
            {
                if (best is null)
                {
                    best = new ExactArtistCandidateSelection(candidate, localAlbumOverlap);
                    bestScore = baseScore;
                }

                continue;
            }

            var score = baseScore + (info.Verified ? 1000 : 0) + Math.Max(0, info.TotalAlbums);
            if (score > bestScore)
            {
                best = new ExactArtistCandidateSelection(candidate, localAlbumOverlap);
                bestScore = score;
            }
        }

        if (best is not null)
        {
            return best;
        }

        return requireLocalAlbumOverlap
            ? null
            : exactCandidates
                .Select(candidate => new ExactArtistCandidateSelection(candidate, 0))
                .FirstOrDefault();
    }

    private static List<SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate> BuildArtistSearchCandidates(
        IReadOnlyList<SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate> results,
        string artistName)
    {
        return results
            .Where(candidate => candidate.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            .Concat(results)
            .DistinctBy(candidate => candidate.Id)
            .ToList();
    }

    private async Task<List<RankedSpotifyCandidate>> RankArtistSearchCandidatesAsync(
        IReadOnlyList<SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate> candidates,
        string artistName,
        HashSet<string> localAlbumTitleSet,
        CancellationToken cancellationToken)
    {
        var ranked = new List<RankedSpotifyCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var rankedCandidate = await TryRankArtistCandidateAsync(candidate, artistName, localAlbumTitleSet, cancellationToken);
            if (rankedCandidate is not null)
            {
                ranked.Add(rankedCandidate);
            }
        }

        return ranked;
    }

    private async Task<RankedSpotifyCandidate?> TryRankArtistCandidateAsync(
        SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate candidate,
        string artistName,
        HashSet<string> localAlbumTitleSet,
        CancellationToken cancellationToken)
    {
        var info = await _pathfinderMetadataClient.GetArtistCandidateInfoAsync(candidate.Id, cancellationToken);
        if (info is null || info.TotalAlbums <= 0)
        {
            return null;
        }

        var spotifyAlbumTitles = await FetchCandidateAlbumTitlesAsync(candidate.Id, cancellationToken);
        var localOverlap = ComputeLocalAlbumOverlap(localAlbumTitleSet, spotifyAlbumTitles);
        var validation = await _deezerLinkService.ValidateSpotifyCandidateAsync(
            spotifyAlbumTitles,
            artistName,
            0.80,
            cancellationToken);

        var isExactName = IsEquivalentArtistName(candidate.Name, artistName);
        if (ShouldRejectCandidate(validation, isExactName, localOverlap))
        {
            AddActivity("warn",
                $"[spotify] candidate {candidate.Id} rejected for {artistName}: " +
                $"{validation.OverlapPercentage:P0} album overlap with Deezer (need 80%).");
            return null;
        }

        var score = CalculateCandidateScore(info.TotalAlbums, localOverlap, validation.Status, isExactName);
        return new RankedSpotifyCandidate(candidate, score, validation, localOverlap);
    }

    private async Task<List<string>> FetchCandidateAlbumTitlesAsync(string candidateId, CancellationToken cancellationToken)
    {
        var metadata = await _pathfinderMetadataClient.FetchByUrlAsync(
            $"https://open.spotify.com/artist/{candidateId}",
            cancellationToken);
        return metadata?.AlbumList
            .Select(album => album.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList() ?? new List<string>();
    }

    private static int ComputeLocalAlbumOverlap(HashSet<string> localAlbumTitleSet, IReadOnlyList<string> spotifyAlbumTitles)
    {
        if (localAlbumTitleSet.Count == 0 || spotifyAlbumTitles.Count == 0)
        {
            return 0;
        }

        var normalizedSpotifyTitles = spotifyAlbumTitles
            .Select(NormalizeAlbumTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return localAlbumTitleSet.Count(title => normalizedSpotifyTitles.Contains(title));
    }

    private static bool ShouldRejectCandidate(DeezerValidationResult validation, bool isExactName, int localOverlap)
    {
        return validation.Status == DeezerValidationStatus.Invalid
               && !isExactName
               && localOverlap == 0;
    }

    private static int CalculateCandidateScore(
        int totalAlbums,
        int localOverlap,
        DeezerValidationStatus validationStatus,
        bool isExactName)
    {
        var score = isExactName ? 100 : 0;
        score += Math.Min(totalAlbums, 80);
        score += localOverlap * 8;
        score += validationStatus switch
        {
            DeezerValidationStatus.Valid => 25,
            DeezerValidationStatus.SkipValidation => 10,
            _ => 0
        };
        return score;
    }

    private static RankedSpotifyCandidate SelectBestCandidate(IReadOnlyList<RankedSpotifyCandidate> ranked)
    {
        return ranked
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.LocalOverlap)
            .First();
    }

    private async Task StoreEarlyDeezerArtistIdAsync(
        long? localArtistId,
        string? deezerArtistId,
        string artistName,
        CancellationToken cancellationToken)
    {
        if (!localArtistId.HasValue || string.IsNullOrWhiteSpace(deezerArtistId))
        {
            return;
        }

        await _libraryRepository.UpsertArtistSourceIdAsync(localArtistId.Value, "deezer", deezerArtistId, cancellationToken);
        AddActivity("info", $"[spotify] Deezer ID {deezerArtistId} stored early for {artistName}.");
    }

    private async Task<HashSet<string>> TryGetLocalAlbumTitleSetAsync(long? localArtistId, CancellationToken cancellationToken)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!localArtistId.HasValue)
        {
            return titles;
        }

        try
        {
            var localAlbums = await _libraryRepository.GetArtistAlbumsAsync(localArtistId.Value, cancellationToken);
            foreach (var normalized in localAlbums
                         .Select(album => NormalizeAlbumTitle(album.Title))
                         .Where(static normalized => !string.IsNullOrWhiteSpace(normalized)))
            {
                titles.Add(normalized);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to read local album titles for artist {ArtistId}", localArtistId.Value);
        }

        return titles;
    }

    private static bool IsEquivalentArtistName(string candidate, string target)
    {
        if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedCandidate = NormalizeTitle(candidate);
        var normalizedTarget = NormalizeTitle(target);
        return !string.IsNullOrWhiteSpace(normalizedCandidate)
            && string.Equals(normalizedCandidate, normalizedTarget, StringComparison.OrdinalIgnoreCase);
    }


    private static string NormalizeAlbumTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = normalized.Replace("–", "-").Replace("—", "-");
        normalized = ReplaceWithTimeout(normalized, @"\(.*?\)|\[.*?]|\{.*?\}", string.Empty);
        normalized = ReplaceWithTimeout(normalized, @"\bfeat\.?\b|\bft\.?\b", string.Empty);
        normalized = ReplaceWithTimeout(normalized, @"[^a-z0-9]+", " ").Trim();
        return normalized;
    }

    private void AddActivity(string level, string message)
    {
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, level, message));
    }
}

public sealed record SpotifyArtistPageResult(
    bool Available,
    SpotifyArtistProfile Artist,
    List<SpotifyAlbum> Albums,
    List<SpotifyAlbum> AppearsOn,
    List<SpotifyTrack> TopTracks,
    List<SpotifyRelatedArtist> RelatedArtists);

public sealed record SpotifyArtistProfile(
    string Id,
    string Name,
    List<SpotifyImage> Images,
    List<string> Genres,
    int Followers,
    int Popularity,
    string? SourceUrl,
    string? Biography,
    bool? Verified,
    int? MonthlyListeners,
    int? Rank,
    string? HeaderImageUrl,
    List<string> Gallery,
    string? DiscographyType,
    int? TotalAlbums,
    List<SpotifyActivityPeriod>? ActivityPeriods = null,
    List<SpotifySalePeriod>? SalePeriods = null,
    List<SpotifyAvailabilityInfo>? Availability = null,
    bool? IsPortraitAlbumCover = null,
    string? DeezerId = null,
    string? DeezerUrl = null);

public sealed record SpotifyAlbum(
    string Id,
    string Name,
    string? ReleaseDate,
    string AlbumGroup,
    int TotalTracks,
    List<SpotifyImage> Images,
    string? SourceUrl,
    string? DeezerId = null,
    string? DeezerUrl = null,
    string? DiscographySection = null,
    bool IsPopular = false) : SpotifyAlbumMetadataFields;

public sealed record SpotifyTrack(
    string Id,
    string Name,
    int DurationMs,
    int Popularity,
    string? PreviewUrl,
    string? SourceUrl,
    List<SpotifyImage> AlbumImages,
    string? AlbumName = null,
    string? ReleaseDate = null,
    string? DeezerId = null,
    string? DeezerUrl = null,
    string? AlbumGroup = null,
    string? ReleaseType = null,
    int? AlbumTrackTotal = null)
{
    public string? Isrc { get; init; }
    public string? AlbumId { get; init; }
    public bool? Explicit { get; init; }
    public bool? HasLyrics { get; init; }
}

public sealed record SpotifyRelatedArtist(
    string Id,
    string Name,
    List<SpotifyImage> Images,
    string? SourceUrl,
    string? DeezerId = null,
    string? DeezerUrl = null);

public sealed record SpotifyImage(string Url, int? Width, int? Height);

public sealed record SpotifyAlbumPage(
    List<SpotifyAlbum> Albums,
    int? Total,
    bool HasMore);

public sealed record SpotifyArtistCacheEnvelope(
    int Version,
    SpotifyArtistPageResult Payload);
