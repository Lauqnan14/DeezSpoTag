using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Collections.Concurrent;
using DeezSpoTag.Core.Models.Settings;
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
    private const int ShazamArtistSampleTrackLimit = 3;
    private const int ShazamArtistCandidateTrackWindow = 5;
    private const int ShazamStrongVoteThreshold = 2;
    private const int CanonicalFallbackMinScore = 2_030;
    private const int CanonicalFallbackMinLead = 250;
    private const int ExactCandidateScoreParallelism = 4;
    private const int ShazamCandidateScoreParallelism = 4;
    private const string DebugActivityLevel = "debug";
    private static readonly TimeSpan LocalAlbumCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SampleTrackPathCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly string[] AliasSuffixes =
    {
        " tell em",
        " tell'em",
        " tell em official"
    };
    private static readonly string[] CompilationAlbumMarkers =
    {
        "greatest hits",
        "best of",
        "anthology",
        "collection",
        "essentials",
        "now that's what i call",
        "top hits",
        "compilation",
        "various artists"
    };
    private readonly LibraryRepository _libraryRepository;
    private readonly ArtistPageCacheRepository _cacheRepository;
    private readonly LibraryConfigStore _configStore;
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly SpotifyMetadataService _metadataService;
    private readonly SpotifyDeezerLinkService _deezerLinkService;
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly AutoTagDefaultsStore _autoTagDefaultsStore;
    private readonly TaggingProfileService _taggingProfileService;
    private readonly ILogger<SpotifyArtistService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<long, (DateTimeOffset Stamp, HashSet<string> Titles)> _localAlbumTitleSetCache = new();
    private readonly ConcurrentDictionary<long, (DateTimeOffset Stamp, IReadOnlyList<string> Paths)> _sampleTrackPathsCache = new();
    private static string ReplaceWithTimeout(string input, string pattern, string replacement, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
        => System.Text.RegularExpressions.Regex.Replace(input, pattern, replacement, options, RegexTimeout);

    public SpotifyArtistService(
        LibraryRepository libraryRepository,
        ArtistPageCacheRepository cacheRepository,
        LibraryConfigStore configStore,
        AutoTagDefaultsStore autoTagDefaultsStore,
        TaggingProfileService taggingProfileService,
        SpotifyArtistServiceDependencies dependencies,
        ILogger<SpotifyArtistService> logger)
    {
        _libraryRepository = libraryRepository;
        _cacheRepository = cacheRepository;
        _configStore = configStore;
        _pathfinderMetadataClient = dependencies.PathfinderMetadataClient;
        _metadataService = dependencies.MetadataService;
        _deezerLinkService = dependencies.DeezerLinkService;
        _shazamRecognitionService = dependencies.ShazamRecognitionService;
        _autoTagDefaultsStore = autoTagDefaultsStore;
        _taggingProfileService = taggingProfileService;
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
            await TryRewriteArtistFoldersToCanonicalNameAsync(artistId, artistName, spotifyId, cancellationToken);
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
            await TryRewriteArtistFoldersToCanonicalNameAsync(artistId, artistName, rematchedSpotifyId, cancellationToken);
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

    public async Task<IReadOnlyList<SpotifyArtistMatchSuggestion>> GetArtistMatchSuggestionsAsync(
        long artistId,
        string artistName,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return Array.Empty<SpotifyArtistMatchSuggestion>();
        }

        var safeLimit = Math.Clamp(limit, 1, 25);
        var localAlbumTitleSet = await TryGetLocalAlbumTitleSetAsync(artistId, cancellationToken);
        var requireLocalAlbumOverlap = localAlbumTitleSet.Count > 0;
        var aliasTargets = BuildArtistAliasTargets(artistName);

        var results = await _pathfinderMetadataClient.SearchArtistsAsync(artistName, Math.Max(10, safeLimit), cancellationToken);
        if (results.Count == 0)
        {
            return Array.Empty<SpotifyArtistMatchSuggestion>();
        }

        var suggestions = new List<SpotifyArtistMatchSuggestion>(safeLimit);
        foreach (var candidate in results.DistinctBy(item => item.Id).Take(Math.Max(safeLimit * 2, 10)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nameMatchesAlias = aliasTargets.Any(alias => IsEquivalentArtistName(candidate.Name, alias));
            var localAlbumOverlap = await ResolveLocalAlbumOverlapAsync(
                candidate.Id,
                requireLocalAlbumOverlap,
                localAlbumTitleSet,
                cancellationToken);
            var info = await _pathfinderMetadataClient.GetArtistCandidateInfoAsync(candidate.Id, cancellationToken);
            var score = ComputeExactCandidateScore(localAlbumOverlap, info) + (nameMatchesAlias ? 100_000 : 0);

            suggestions.Add(new SpotifyArtistMatchSuggestion(
                candidate.Id,
                candidate.Name,
                candidate.ImageUrl,
                score,
                localAlbumOverlap,
                nameMatchesAlias,
                info?.Verified == true,
                info?.TotalAlbums ?? 0,
                info?.TotalTracks ?? 0));
        }

        return suggestions
            .OrderByDescending(suggestion => suggestion.NameMatchesAlias)
            .ThenByDescending(suggestion => suggestion.LocalAlbumOverlap)
            .ThenByDescending(suggestion => suggestion.TotalAlbums)
            .ThenByDescending(suggestion => suggestion.TotalTracks)
            .ThenByDescending(suggestion => suggestion.Verified)
            .ThenByDescending(suggestion => suggestion.Score)
            .Take(safeLimit)
            .ToList();
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
        List<SpotifyTrack> tracks,
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
        List<SpotifyAlbum> primary,
        List<SpotifyAlbum> fallback)
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
        List<SpotifyAlbum> primary,
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
        List<SpotifyTrack> primary,
        List<SpotifyTrack> fallback)
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
        List<SpotifyTrack> tracks,
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
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Spotify artist librespot fallback enrichment failed for {ArtistId}", spotifyId);
                }
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
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Spotify artist top-track ISRC enrichment failed for {ArtistId}", spotifyId);
                }
            }
        });
    }

    private static bool HaveTopTrackIsrcsChanged(
        List<SpotifyTrack> current,
        List<SpotifyTrack> enriched)
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
            var localAlbumTitleSet = FilterResolvableAlbumTitles(
                await TryGetLocalAlbumTitleSetAsync(localArtistId, cancellationToken));
            var aliasTargets = BuildArtistAliasTargets(artistName);
            var results = await SearchArtistCandidatesWithFallbackQueryAsync(artistName, cancellationToken);
            if (results.Count == 0)
            {
                return await ResolveArtistIdWithShazamFallbackAsync(
                    artistName,
                    localArtistId,
                    localAlbumTitleSet,
                    aliasTargets,
                    "[spotify] artist id resolved via shazam evidence (pathfinder unavailable)",
                    $"[spotify] artist ID resolve failed: pathfinder search unavailable for {artistName}.",
                    cancellationToken);
            }

            var candidates = BuildArtistSearchCandidates(results, artistName);
            var exactCandidates = candidates
                .Where(candidate => aliasTargets.Any(target => IsEquivalentArtistName(candidate.Name, target)))
                .ToList();

            if (exactCandidates.Count == 0)
            {
                return await ResolveArtistIdWithShazamFallbackAsync(
                    artistName,
                    localArtistId,
                    localAlbumTitleSet,
                    aliasTargets,
                    "[spotify] artist id resolved via shazam evidence",
                    $"[spotify] artist ID resolve failed: no exact artist-name match for {artistName}.",
                    cancellationToken);
            }
            var selectedExactCandidateId = await TrySelectExactCandidateArtistIdAsync(
                exactCandidates,
                localAlbumTitleSet,
                aliasTargets,
                artistName,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(selectedExactCandidateId))
            {
                return selectedExactCandidateId;
            }

            return await ResolveArtistIdWithShazamFallbackAsync(
                artistName,
                localArtistId,
                localAlbumTitleSet,
                aliasTargets,
                "[spotify] artist id resolved via shazam fallback",
                $"[spotify] artist ID resolve failed: no suitable exact-name candidate for {artistName}.",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify Pathfinder artist search failed.");
            return await ResolveArtistIdAfterPathfinderErrorAsync(artistName, localArtistId, cancellationToken);
        }
    }

    private async Task<string?> TrySelectExactCandidateArtistIdAsync(
        List<SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate> exactCandidates,
        HashSet<string> localAlbumTitleSet,
        HashSet<string> aliasTargets,
        string artistName,
        CancellationToken cancellationToken)
    {
        var best = await SelectBestExactArtistCandidateAsync(exactCandidates, localAlbumTitleSet, cancellationToken);
        if (best is not null)
        {
            AddActivity("info",
                $"[spotify] candidate {best.Candidate.Id} selected for {artistName} " +
                $"(exact_name=true, local_album_overlap={best.LocalAlbumOverlap}).");
            return best.Candidate.Id;
        }

        var canonicalFallback = await TrySelectCanonicalFallbackExactCandidateAsync(
            exactCandidates,
            aliasTargets,
            cancellationToken);
        if (canonicalFallback is not null)
        {
            AddActivity("info",
                $"[spotify] candidate {canonicalFallback.Candidate.Id} selected for {artistName} " +
                "(exact_name=true, local_album_overlap=0, canonical_fallback=true).");
            return canonicalFallback.Candidate.Id;
        }

        return null;
    }

    private async Task<string?> ResolveArtistIdWithShazamFallbackAsync(
        string artistName,
        long? localArtistId,
        HashSet<string> localAlbumTitleSet,
        HashSet<string> aliasTargets,
        string successPrefix,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var shazamResolved = await TryResolveArtistIdViaShazamEvidenceAsync(
            artistName,
            localArtistId,
            localAlbumTitleSet,
            aliasTargets,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(shazamResolved))
        {
            AddActivity("info", $"{successPrefix}: {artistName} -> {shazamResolved}.");
            return shazamResolved;
        }

        AddActivity("warn", failureMessage);
        return null;
    }

    private async Task<string?> ResolveArtistIdAfterPathfinderErrorAsync(
        string artistName,
        long? localArtistId,
        CancellationToken cancellationToken)
    {
        try
        {
            var localAlbumTitleSet = FilterResolvableAlbumTitles(
                await TryGetLocalAlbumTitleSetAsync(localArtistId, cancellationToken));
            var aliasTargets = BuildArtistAliasTargets(artistName);
            return await ResolveArtistIdWithShazamFallbackAsync(
                artistName,
                localArtistId,
                localAlbumTitleSet,
                aliasTargets,
                "[spotify] artist id resolved via shazam evidence (pathfinder error)",
                $"[spotify] artist ID resolve failed: pathfinder search error for {artistName}.",
                cancellationToken);
        }
        catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(fallbackEx, "Shazam fallback after Pathfinder failure also failed for {ArtistName}.", artistName);
            }
        }

        AddActivity("warn", $"[spotify] artist ID resolve failed: pathfinder search error for {artistName}.");
        return null;
    }

    private sealed record ExactArtistCandidateSelection(
        SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate Candidate,
        int LocalAlbumOverlap);

    private sealed record ExactArtistCandidateScore(
        SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate Candidate,
        int LocalAlbumOverlap,
        int Score,
        int TotalAlbums,
        int TotalTracks,
        bool Verified);

    private sealed record CanonicalFallbackCandidateScore(
        SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate Candidate,
        int Score,
        int TotalAlbums,
        int TotalTracks,
        bool Verified);

    private async Task<ExactArtistCandidateSelection?> SelectBestExactArtistCandidateAsync(
        IReadOnlyList<SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate> exactCandidates,
        HashSet<string> localAlbumTitleSet,
        CancellationToken cancellationToken)
    {
        var requireLocalAlbumOverlap = ShouldRequireLocalAlbumOverlap(localAlbumTitleSet);
        var scoredCandidates = new ConcurrentBag<ExactArtistCandidateScore>();
        using var gate = new SemaphoreSlim(ExactCandidateScoreParallelism);

        var tasks = exactCandidates.Select(async candidate =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var scoredCandidate = await ScoreExactArtistCandidateAsync(
                    candidate,
                    requireLocalAlbumOverlap,
                    localAlbumTitleSet,
                    cancellationToken);
                if (scoredCandidate is not null)
                {
                    scoredCandidates.Add(scoredCandidate);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        var best = scoredCandidates
            .OrderByDescending(item => item.LocalAlbumOverlap)
            .ThenByDescending(item => item.TotalAlbums)
            .ThenByDescending(item => item.TotalTracks)
            .ThenByDescending(item => item.Verified)
            .ThenByDescending(item => item.Score)
            .FirstOrDefault();

        if (best is not null)
        {
            return new ExactArtistCandidateSelection(best.Candidate, best.LocalAlbumOverlap);
        }

        return null;
    }

    private async Task<ExactArtistCandidateScore?> ScoreExactArtistCandidateAsync(
        SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate candidate,
        bool requireLocalAlbumOverlap,
        HashSet<string> localAlbumTitleSet,
        CancellationToken cancellationToken)
    {
        var localAlbumOverlap = await ResolveLocalAlbumOverlapAsync(
            candidate.Id,
            requireLocalAlbumOverlap,
            localAlbumTitleSet,
            cancellationToken);
        if (requireLocalAlbumOverlap && localAlbumOverlap <= 0)
        {
            return null;
        }

        var info = await _pathfinderMetadataClient.GetArtistCandidateInfoAsync(candidate.Id, cancellationToken);
        var score = ComputeExactCandidateScore(localAlbumOverlap, info);
        return new ExactArtistCandidateScore(
            candidate,
            localAlbumOverlap,
            score,
            Math.Max(0, info?.TotalAlbums ?? 0),
            Math.Max(0, info?.TotalTracks ?? 0),
            info?.Verified == true);
    }

    private async Task<CanonicalFallbackCandidateScore?> ScoreCanonicalFallbackCandidateAsync(
        SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate candidate,
        IReadOnlyCollection<string> aliasTargets,
        int index,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalArtistNameEquivalent(candidate.Name, aliasTargets))
        {
            return null;
        }

        var info = await _pathfinderMetadataClient.GetArtistCandidateInfoAsync(candidate.Id, cancellationToken);
        var score = ComputeCanonicalFallbackScore(info, index);
        return new CanonicalFallbackCandidateScore(
            candidate,
            score,
            Math.Max(0, info?.TotalAlbums ?? 0),
            Math.Max(0, info?.TotalTracks ?? 0),
            info?.Verified == true);
    }

    private async Task<CanonicalFallbackCandidateScore?> TrySelectCanonicalFallbackExactCandidateAsync(
        List<SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate> exactCandidates,
        IReadOnlyCollection<string> aliasTargets,
        CancellationToken cancellationToken)
    {
        var scored = new List<CanonicalFallbackCandidateScore>(exactCandidates.Count);
        for (var index = 0; index < exactCandidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = exactCandidates[index];
            var scoredCandidate = await ScoreCanonicalFallbackCandidateAsync(
                candidate,
                aliasTargets,
                index,
                cancellationToken);
            if (scoredCandidate is not null)
            {
                scored.Add(scoredCandidate);
            }
        }

        if (scored.Count == 0)
        {
            return null;
        }

        var ordered = scored
            .OrderByDescending(item => item.TotalAlbums)
            .ThenByDescending(item => item.TotalTracks)
            .ThenByDescending(item => item.Verified)
            .ThenByDescending(item => item.Score)
            .ToList();
        var best = ordered[0];
        if (best.Score < CanonicalFallbackMinScore)
        {
            return null;
        }

        var nextBestScore = ordered.Count > 1 ? ordered[1].Score : int.MinValue;
        if (nextBestScore != int.MinValue && (best.Score - nextBestScore) < CanonicalFallbackMinLead)
        {
            return null;
        }

        return best;
    }

    private async Task<int> ResolveLocalAlbumOverlapAsync(
        string candidateId,
        bool requireLocalAlbumOverlap,
        HashSet<string> localAlbumTitleSet,
        CancellationToken cancellationToken)
    {
        if (!requireLocalAlbumOverlap)
        {
            return 0;
        }

        var candidateAlbumTitles = await FetchCandidateAlbumTitlesAsync(candidateId, cancellationToken);
        return ComputeLocalAlbumOverlap(localAlbumTitleSet, candidateAlbumTitles);
    }

    private static int ComputeExactCandidateScore(
        int localAlbumOverlap,
        SpotifyPathfinderMetadataClient.SpotifyArtistCandidateInfo? info)
    {
        var baseScore = localAlbumOverlap * 10_000;
        if (info is null)
        {
            return baseScore;
        }

        var albumWeight = Math.Max(0, info.TotalAlbums) * 200;
        var trackWeight = Math.Max(0, info.TotalTracks);
        var verificationTiebreaker = info.Verified ? 25 : 0;
        return baseScore + albumWeight + trackWeight + verificationTiebreaker;
    }

    private static int ComputeCanonicalFallbackScore(
        SpotifyPathfinderMetadataClient.SpotifyArtistCandidateInfo? info,
        int rankIndex)
    {
        var verifiedBoost = info?.Verified == true ? 25 : 0;
        var albumBoost = Math.Max(0, info?.TotalAlbums ?? 0) * 200;
        var trackBoost = Math.Max(0, info?.TotalTracks ?? 0);
        var rankBoost = Math.Max(0, 30 - Math.Max(0, rankIndex));
        return verifiedBoost + albumBoost + trackBoost + rankBoost;
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

    private static int ComputeLocalAlbumOverlap(HashSet<string> localAlbumTitleSet, List<string> spotifyAlbumTitles)
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

    private async Task<HashSet<string>> TryGetLocalAlbumTitleSetAsync(long? localArtistId, CancellationToken cancellationToken)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!localArtistId.HasValue)
        {
            return titles;
        }

        if (_localAlbumTitleSetCache.TryGetValue(localArtistId.Value, out var cached)
            && (DateTimeOffset.UtcNow - cached.Stamp) <= LocalAlbumCacheTtl)
        {
            return new HashSet<string>(cached.Titles, StringComparer.OrdinalIgnoreCase);
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
            _localAlbumTitleSetCache[localArtistId.Value] = (
                DateTimeOffset.UtcNow,
                new HashSet<string>(titles, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read local album titles for artist {ArtistId}", localArtistId.Value);
            }
        }

        return titles;
    }

    private static bool IsEquivalentArtistName(string candidate, string target)
    {
        if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var candidateVariants = ExpandArtistNameVariants(candidate);
        var targetVariants = ExpandArtistNameVariants(target);
        return candidateVariants.Overlaps(targetVariants);
    }

    private static bool IsCanonicalArtistNameEquivalent(string candidate, IReadOnlyCollection<string> aliasTargets)
    {
        var candidateVariants = ExpandArtistNameVariants(candidate)
            .Select(NormalizeArtistCanonicalKey)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidateVariants.Count == 0)
        {
            return false;
        }

        foreach (var alias in aliasTargets)
        {
            var aliasCanonical = NormalizeArtistCanonicalKey(alias);
            if (!string.IsNullOrWhiteSpace(aliasCanonical) && candidateVariants.Contains(aliasCanonical))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string?> TryResolveArtistIdViaShazamEvidenceAsync(
        string artistName,
        long? localArtistId,
        HashSet<string> localAlbumTitleSet,
        IReadOnlyCollection<string> aliasTargets,
        CancellationToken cancellationToken)
    {
        if (!localArtistId.HasValue || !_shazamRecognitionService.IsAvailable)
        {
            if (!localArtistId.HasValue)
            {
                AddActivity(DebugActivityLevel, $"[spotify] shazam skipped for {artistName}: missing local artist id.");
            }
            else
            {
                AddActivity(DebugActivityLevel, $"[spotify] shazam skipped for {artistName}: shazam runtime unavailable.");
            }
            return null;
        }

        var sampleTrackPaths = await GetLocalArtistSampleTrackPathsAsync(
            localArtistId.Value,
            ShazamArtistSampleTrackLimit,
            cancellationToken);
        if (sampleTrackPaths.Count == 0)
        {
            AddActivity(DebugActivityLevel, $"[spotify] shazam skipped for {artistName}: no sample track paths.");
            return null;
        }

        var candidateVotes = await CollectShazamSpotifyArtistVotesAsync(sampleTrackPaths, artistName, cancellationToken);

        if (candidateVotes.Count == 0)
        {
            AddActivity(DebugActivityLevel, $"[spotify] shazam produced no spotify candidates for {artistName}.");
            return null;
        }

        var bestId = await SelectBestShazamEvidenceCandidateAsync(
            candidateVotes,
            localAlbumTitleSet,
            aliasTargets,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(bestId))
        {
            return bestId;
        }

        AddActivity(DebugActivityLevel, $"[spotify] shazam candidates rejected for {artistName}: no candidate passed acceptance checks.");
        return null;
    }

    private async Task<Dictionary<string, int>> CollectShazamSpotifyArtistVotesAsync(
        IReadOnlyList<string> sampleTrackPaths,
        string artistName,
        CancellationToken cancellationToken)
    {
        var candidateVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var trackPath in sampleTrackPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recognition = TryRecognizeTrackWithShazam(trackPath, artistName);
            if (recognition is null)
            {
                continue;
            }

            var spotifyArtistIds = await ResolveSpotifyArtistIdsFromShazamRecognitionAsync(recognition, cancellationToken);
            AddSpotifyArtistVotes(candidateVotes, spotifyArtistIds);
        }

        return candidateVotes;
    }

    private ShazamRecognitionInfo? TryRecognizeTrackWithShazam(string trackPath, string artistName)
    {
        try
        {
            return _shazamRecognitionService.Recognize(trackPath, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Shazam recognition failed while resolving Spotify artist id for {ArtistName}.", artistName);
            }
            return null;
        }
    }

    private static void AddSpotifyArtistVotes(
        Dictionary<string, int> candidateVotes,
        IEnumerable<string> spotifyArtistIds)
    {
        foreach (var spotifyArtistId in spotifyArtistIds)
        {
            if (candidateVotes.TryGetValue(spotifyArtistId, out var current))
            {
                candidateVotes[spotifyArtistId] = current + 1;
            }
            else
            {
                candidateVotes[spotifyArtistId] = 1;
            }
        }
    }

    private async Task<string?> SelectBestShazamEvidenceCandidateAsync(
        IReadOnlyDictionary<string, int> candidateVotes,
        HashSet<string> localAlbumTitleSet,
        IReadOnlyCollection<string> aliasTargets,
        CancellationToken cancellationToken)
    {
        var requireLocalAlbumOverlap = ShouldRequireLocalAlbumOverlap(localAlbumTitleSet);
        var candidates = new ConcurrentBag<(string Id, int Score, int TotalAlbums, int TotalTracks, bool Verified)>();
        using var gate = new SemaphoreSlim(ShazamCandidateScoreParallelism);

        var tasks = candidateVotes
            .OrderByDescending(entry => entry.Value)
            .Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateId = entry.Key;
                var votes = entry.Value;

                var candidateName = await TryFetchSpotifyArtistNameAsync(candidateId, cancellationToken);
                var matchesAlias = !string.IsNullOrWhiteSpace(candidateName)
                    && aliasTargets.Any(alias => IsEquivalentArtistName(candidateName, alias));
                var localAlbumOverlap = await ResolveLocalAlbumOverlapAsync(
                    candidateId,
                    requireLocalAlbumOverlap,
                    localAlbumTitleSet,
                    cancellationToken);
                var hasStrongVotes = votes >= ShazamStrongVoteThreshold;
                var info = await _pathfinderMetadataClient.GetArtistCandidateInfoAsync(candidateId, cancellationToken);

                var accepted = matchesAlias;
                if (!accepted && localAlbumOverlap > 0)
                {
                    accepted = true;
                }

                if (!accepted && hasStrongVotes)
                {
                    accepted = true;
                }

                if (!accepted)
                {
                    return;
                }

                var score = (votes * 100)
                    + (localAlbumOverlap * 25)
                    + (Math.Max(0, info?.TotalAlbums ?? 0) * 20)
                    + Math.Max(0, info?.TotalTracks ?? 0)
                    + (info?.Verified == true ? 5 : 0);
                candidates.Add((
                    candidateId,
                    score,
                    Math.Max(0, info?.TotalAlbums ?? 0),
                    Math.Max(0, info?.TotalTracks ?? 0),
                    info?.Verified == true));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        return candidates
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.TotalAlbums)
            .ThenByDescending(item => item.TotalTracks)
            .ThenByDescending(item => item.Verified)
            .Select(item => item.Id)
            .FirstOrDefault();
    }

    private async Task<string?> TryFetchSpotifyArtistNameAsync(string spotifyArtistId, CancellationToken cancellationToken)
    {
        if (!LooksLikeSpotifyEntityId(spotifyArtistId))
        {
            return null;
        }

        try
        {
            var metadata = await _pathfinderMetadataClient.FetchByUrlAsync(
                $"https://open.spotify.com/artist/{spotifyArtistId}",
                cancellationToken);
            return string.IsNullOrWhiteSpace(metadata?.Name) ? null : metadata.Name.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed fetching Spotify artist profile for id {SpotifyArtistId}.", spotifyArtistId);
            }
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> ResolveSpotifyArtistIdsFromShazamRecognitionAsync(
        ShazamRecognitionInfo recognition,
        CancellationToken cancellationToken)
    {
        var artistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var spotifyUrl = (recognition.SpotifyUrl ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(spotifyUrl)
            && SpotifyMetadataService.TryParseSpotifyUrl(spotifyUrl, out var type, out var id)
            && !string.IsNullOrWhiteSpace(id))
        {
            await AddArtistIdsFromSpotifyUrlAsync(spotifyUrl, type, id, artistIds, cancellationToken);
        }

        if (artistIds.Count > 0)
        {
            return artistIds.ToList();
        }

        var metadataDerived = await ResolveSpotifyArtistIdsFromShazamMetadataAsync(recognition, cancellationToken);
        if (metadataDerived.Count > 0)
        {
            foreach (var candidate in metadataDerived.Where(LooksLikeSpotifyEntityId))
            {
                artistIds.Add(candidate);
            }
        }

        return artistIds.ToList();
    }

    private async Task<IReadOnlyList<string>> ResolveSpotifyArtistIdsFromShazamMetadataAsync(
        ShazamRecognitionInfo recognition,
        CancellationToken cancellationToken)
    {
        var query = BuildShazamMetadataTrackQuery(recognition);
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        try
        {
            var tracks = await _pathfinderMetadataClient.SearchTracksAsync(query, 15, cancellationToken);
            if (tracks.Count == 0)
            {
                return Array.Empty<string>();
            }

            var recognizedIsrc = (recognition.Isrc ?? string.Empty).Trim();
            var recognizedTitle = NormalizeTitle(recognition.Title ?? string.Empty);
            var recognizedArtist = NormalizeTitle(
                recognition.Artist
                ?? recognition.Artists?.FirstOrDefault()
                ?? string.Empty);

            var candidates = tracks
                .Select(track => new
                {
                    Track = track,
                    Score = ScoreShazamMetadataCandidate(track, recognizedIsrc, recognizedTitle, recognizedArtist)
                })
                .Where(item => item.Score > 0 && item.Track.ArtistIds is { Count: > 0 })
                .OrderByDescending(item => item.Score)
                .Take(5)
                .ToList();

            if (candidates.Count == 0)
            {
                return Array.Empty<string>();
            }

            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                foreach (var artistId in (candidate.Track.ArtistIds ?? Array.Empty<string>()).Where(LooksLikeSpotifyEntityId))
                {
                    resolved.Add(artistId);
                }
            }

            return resolved.ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed resolving Spotify artist ids from Shazam metadata query.");
            return Array.Empty<string>();
        }
    }

    private static int ScoreShazamMetadataCandidate(
        SpotifyTrackSummary track,
        string recognizedIsrc,
        string recognizedTitle,
        string recognizedArtist)
    {
        var score = 0;

        if (!string.IsNullOrWhiteSpace(recognizedIsrc)
            && string.Equals(track.Isrc, recognizedIsrc, StringComparison.OrdinalIgnoreCase))
        {
            score += 10_000;
        }

        var candidateTitle = NormalizeTitle(track.Name ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(recognizedTitle) && !string.IsNullOrWhiteSpace(candidateTitle))
        {
            if (string.Equals(candidateTitle, recognizedTitle, StringComparison.OrdinalIgnoreCase))
            {
                score += 800;
            }
            else if (candidateTitle.Contains(recognizedTitle, StringComparison.OrdinalIgnoreCase)
                     || recognizedTitle.Contains(candidateTitle, StringComparison.OrdinalIgnoreCase))
            {
                score += 250;
            }
        }

        var candidateArtists = NormalizeTitle(track.Artists ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(recognizedArtist) && !string.IsNullOrWhiteSpace(candidateArtists))
        {
            if (candidateArtists.Contains(recognizedArtist, StringComparison.OrdinalIgnoreCase))
            {
                score += 700;
            }
            else if (recognizedArtist.Contains(candidateArtists, StringComparison.OrdinalIgnoreCase))
            {
                score += 250;
            }
        }

        return score;
    }

    private static string BuildShazamMetadataTrackQuery(ShazamRecognitionInfo recognition)
    {
        var title = (recognition.Title ?? string.Empty).Trim();
        var artist = (
            recognition.Artist
            ?? recognition.Artists?.FirstOrDefault()
            ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
        {
            return $"{artist} {title}";
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        return artist;
    }

    private async Task<IReadOnlyList<string>> GetLocalArtistSampleTrackPathsAsync(
        long localArtistId,
        int limit,
        CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(limit, 1, 10);
        if (_sampleTrackPathsCache.TryGetValue(localArtistId, out var cached)
            && (DateTimeOffset.UtcNow - cached.Stamp) <= SampleTrackPathCacheTtl)
        {
            return cached.Paths.Take(safeLimit).ToList();
        }

        var trackIds = await _libraryRepository.GetTrackIdsByArtistAsync(localArtistId, 0, safeLimit * 4, cancellationToken);
        if (trackIds.Count == 0)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>(safeLimit);
        foreach (var trackId in trackIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = await _libraryRepository.GetTrackPrimaryFilePathAsync(trackId, cancellationToken);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            paths.Add(path);
            if (paths.Count >= safeLimit)
            {
                break;
            }
        }

        if (paths.Count > 0)
        {
            _sampleTrackPathsCache[localArtistId] = (DateTimeOffset.UtcNow, paths.ToList());
        }
        return paths;
    }

    private static bool LooksLikeSpotifyEntityId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 22)
        {
            return false;
        }

        return value.All(char.IsLetterOrDigit);
    }

    private static HashSet<string> BuildArtistAliasTargets(string artistName)
    {
        var variants = ExpandArtistNameVariants(artistName);
        variants.Add((artistName ?? string.Empty).Trim());
        return variants;
    }

    private async Task<List<SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate>> SearchArtistCandidatesWithFallbackQueryAsync(
        string artistName,
        CancellationToken cancellationToken)
    {
        var combined = new List<SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var primary = await _pathfinderMetadataClient.SearchArtistsAsync(artistName, 10, cancellationToken);
        AddCandidates(primary);

        var simplifiedQuery = BuildPrimaryArtistQuery(artistName);
        if (!string.IsNullOrWhiteSpace(simplifiedQuery)
            && !string.Equals(simplifiedQuery, artistName, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = await _pathfinderMetadataClient.SearchArtistsAsync(simplifiedQuery, 10, cancellationToken);
            AddCandidates(fallback);
        }

        return combined;

        void AddCandidates(IReadOnlyList<SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate> candidates)
        {
            foreach (var candidate in candidates.Where(candidate => seen.Add(candidate.Id)))
            {
                combined.Add(candidate);
            }
        }
    }

    private async Task AddArtistIdsFromSpotifyUrlAsync(
        string spotifyUrl,
        string type,
        string id,
        HashSet<string> artistIds,
        CancellationToken cancellationToken)
    {
        if (string.Equals(type, "artist", StringComparison.OrdinalIgnoreCase))
        {
            if (LooksLikeSpotifyEntityId(id))
            {
                artistIds.Add(id);
            }

            return;
        }

        try
        {
            var metadata = await _pathfinderMetadataClient.FetchByUrlAsync(spotifyUrl, cancellationToken);
            if (metadata?.TrackList is not { Count: > 0 } trackList)
            {
                return;
            }

            foreach (var artistId in trackList
                         .Take(ShazamArtistCandidateTrackWindow)
                         .SelectMany(track => track.ArtistIds ?? Array.Empty<string>())
                         .Where(LooksLikeSpotifyEntityId))
            {
                artistIds.Add(artistId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed resolving Spotify artist ids from Shazam URL {SpotifyUrl}.", spotifyUrl);
            }
        }
    }

    private static HashSet<string> ExpandArtistNameVariants(string? artistName)
    {
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = NormalizeTitle(artistName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return variants;
        }

        variants.Add(normalized);
        variants.Add(NormalizeArtistCanonicalKey(normalized));
        variants.Add(RemoveConnectorWords(normalized));
        if (normalized.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            variants.Add(normalized[4..].Trim());
        }

        variants.UnionWith(
            AliasSuffixes
                .Where(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(suffix => normalized[..^suffix.Length].Trim())
                .Where(alias => !string.IsNullOrWhiteSpace(alias)));

        variants.RemoveWhere(static item => string.IsNullOrWhiteSpace(item));
        return variants;
    }

    private static string RemoveConnectorWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => !token.Equals("and", StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeArtistCanonicalKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        var index = 0;
        foreach (var ch in value)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                continue;
            }

            buffer[index++] = char.ToLowerInvariant(ch);
        }

        return index == 0 ? string.Empty : new string(buffer, 0, index);
    }

    private static string BuildPrimaryArtistQuery(string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return string.Empty;
        }

        var query = artistName.Trim();
        query = ReplaceWithTimeout(
            query,
            @"\b(feat(?:uring)?|ft|with)\b.*$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var delimiters = new[] { " & ", " x ", ",", ";", "/", "|", "+", " _ ", " and " };
        foreach (var delimiter in delimiters)
        {
            var index = query.IndexOf(delimiter, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                query = query[..index];
                break;
            }
        }

        query = ReplaceWithTimeout(query, @"\s+", " ");
        query = query.Trim(' ', '-', '_', '.', ',', '\'', '"', '!', '?', '(', ')', '[', ']');
        return query;
    }

    private static HashSet<string> FilterResolvableAlbumTitles(HashSet<string> source)
    {
        if (source.Count == 0)
        {
            return source;
        }

        var filtered = source
            .Where(title => !IsLikelyCompilationAlbumTitle(title))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return filtered.Count > 0 ? filtered : source;
    }

    private static bool ShouldRequireLocalAlbumOverlap(HashSet<string> localAlbumTitleSet)
    {
        // Single-track libraries or compilation-only local albums should not hard-block artist resolution.
        return localAlbumTitleSet.Count >= 2;
    }

    private static bool IsLikelyCompilationAlbumTitle(string normalizedAlbumTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedAlbumTitle))
        {
            return false;
        }

        return CompilationAlbumMarkers.Any(marker =>
            normalizedAlbumTitle.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private async Task TryRewriteArtistFoldersToCanonicalNameAsync(
        long localArtistId,
        string currentArtistName,
        string spotifyArtistId,
        CancellationToken cancellationToken)
    {
        if (localArtistId <= 0 || string.IsNullOrWhiteSpace(spotifyArtistId))
        {
            return;
        }

        if (!await ShouldRewriteArtistFoldersToCanonicalNameAsync())
        {
            return;
        }

        var canonicalArtistName = await TryFetchCanonicalSpotifyArtistNameAsync(spotifyArtistId, cancellationToken);

        if (string.IsNullOrWhiteSpace(canonicalArtistName))
        {
            return;
        }

        var sourceNameNormalized = NormalizeTitle(currentArtistName ?? string.Empty);
        var targetNameNormalized = NormalizeTitle(canonicalArtistName);
        if (string.IsNullOrWhiteSpace(targetNameNormalized)
            || string.Equals(sourceNameNormalized, targetNameNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var targetArtistSegment = SanitizePathSegment(canonicalArtistName);
        if (string.IsNullOrWhiteSpace(targetArtistSegment))
        {
            return;
        }

        var enabledRoots = await ResolveEnabledLibraryRootsAsync(cancellationToken);
        if (enabledRoots.Count == 0)
        {
            return;
        }

        var rewriteResult = await RewriteArtistAlbumDirectoriesAsync(
            localArtistId,
            targetArtistSegment,
            enabledRoots,
            cancellationToken);
        if (rewriteResult.MovedAlbumCount > 0 || rewriteResult.MoveConflicts > 0 || rewriteResult.MoveFailures > 0)
        {
            AddActivity(
                rewriteResult.MoveFailures > 0 ? "warn" : "info",
                $"[spotify] canonical artist folder rewrite for {currentArtistName} -> {canonicalArtistName}: moved_albums={rewriteResult.MovedAlbumCount}, conflicts={rewriteResult.MoveConflicts}, failures={rewriteResult.MoveFailures}.");
        }
    }

    private async Task<bool> ShouldRewriteArtistFoldersToCanonicalNameAsync()
    {
        try
        {
            var profiles = await _taggingProfileService.LoadAsync();
            var defaultProfile = profiles.FirstOrDefault(profile => profile.IsDefault)
                ?? profiles.FirstOrDefault();
            if (TryGetRenameSpotifyArtistFoldersFromProfile(defaultProfile, out var profilePreference))
            {
                return profilePreference;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to resolve renameSpotifyArtistFolders from default profile.");
            }
        }

        try
        {
            var defaults = await _autoTagDefaultsStore.LoadAsync();
            return defaults.RenameSpotifyArtistFolders != false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read enhancement defaults for Spotify artist folder canonicalization.");
            }

            return true;
        }
    }

    private static bool TryGetRenameSpotifyArtistFoldersFromProfile(TaggingProfile? profile, out bool value)
    {
        value = true;
        if (profile?.AutoTag?.Data is not { Count: > 0 } data)
        {
            return false;
        }

        var key = data.Keys.FirstOrDefault(entry =>
            string.Equals(entry, "renameSpotifyArtistFolders", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return TryReadBoolean(data[key], out value);
    }

    private static bool TryReadBoolean(JsonElement element, out bool value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
            {
                var raw = element.GetString();
                if (bool.TryParse(raw, out var parsedBool))
                {
                    value = parsedBool;
                    return true;
                }

                if (int.TryParse(raw, out var parsedInt))
                {
                    value = parsedInt != 0;
                    return true;
                }

                break;
            }
            case JsonValueKind.Number:
                if (element.TryGetInt32(out var number))
                {
                    value = number != 0;
                    return true;
                }
                break;
        }

        value = true;
        return false;
    }

    private async Task<string?> TryFetchCanonicalSpotifyArtistNameAsync(string spotifyArtistId, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _pathfinderMetadataClient.FetchByUrlAsync(
                $"https://open.spotify.com/artist/{spotifyArtistId}",
                cancellationToken);
            return metadata?.Name?.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to fetch canonical Spotify artist name for {SpotifyArtistId}.", spotifyArtistId);
            }
            return null;
        }
    }

    private async Task<List<string>> ResolveEnabledLibraryRootsAsync(CancellationToken cancellationToken)
    {
        var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
        return folders
            .Where(folder => folder.Enabled && !string.IsNullOrWhiteSpace(folder.RootPath))
            .Select(folder => Path.GetFullPath(folder.RootPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<(int MovedAlbumCount, int MoveConflicts, int MoveFailures)> RewriteArtistAlbumDirectoriesAsync(
        long localArtistId,
        string targetArtistSegment,
        IReadOnlyList<string> enabledRoots,
        CancellationToken cancellationToken)
    {
        var trackIds = await _libraryRepository.GetTrackIdsByArtistAsync(localArtistId, 0, 200_000, cancellationToken);
        if (trackIds.Count == 0)
        {
            return (0, 0, 0);
        }

        var movedAlbumDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceArtistDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moveConflicts = 0;
        var moveFailures = 0;

        foreach (var trackId in trackIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = await _libraryRepository.GetTrackPrimaryFilePathAsync(trackId, cancellationToken);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var moveOutcome = TryMoveTrackAlbumDirectory(
                path,
                targetArtistSegment,
                enabledRoots,
                movedAlbumDirs,
                sourceArtistDirs);
            if (moveOutcome == FolderMoveOutcome.Conflict)
            {
                moveConflicts++;
            }
            else if (moveOutcome == FolderMoveOutcome.Failed)
            {
                moveFailures++;
            }
        }

        CleanupEmptySourceArtistDirectories(sourceArtistDirs);
        return (movedAlbumDirs.Count - moveConflicts - moveFailures, moveConflicts, moveFailures);
    }

    private enum FolderMoveOutcome
    {
        Skipped = 0,
        Moved = 1,
        Conflict = 2,
        Failed = 3
    }

    private FolderMoveOutcome TryMoveTrackAlbumDirectory(
        string filePath,
        string targetArtistSegment,
        IReadOnlyList<string> enabledRoots,
        HashSet<string> movedAlbumDirs,
        HashSet<string> sourceArtistDirs)
    {
        var fullPath = Path.GetFullPath(filePath);
        var root = enabledRoots.FirstOrDefault(candidateRoot => IsPathUnderRoot(fullPath, candidateRoot));
        if (root is null)
        {
            return FolderMoveOutcome.Skipped;
        }

        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return FolderMoveOutcome.Skipped;
        }

        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
        {
            return FolderMoveOutcome.Skipped;
        }

        var sourceArtistSegment = segments[0].Trim();
        var albumSegment = segments[1].Trim();
        if (string.IsNullOrWhiteSpace(sourceArtistSegment)
            || string.IsNullOrWhiteSpace(albumSegment)
            || string.Equals(sourceArtistSegment, targetArtistSegment, StringComparison.OrdinalIgnoreCase))
        {
            return FolderMoveOutcome.Skipped;
        }

        var sourceAlbumDir = Path.Combine(root, sourceArtistSegment, albumSegment);
        if (!Directory.Exists(sourceAlbumDir) || !movedAlbumDirs.Add(sourceAlbumDir))
        {
            return FolderMoveOutcome.Skipped;
        }

        var targetArtistDir = Path.Combine(root, targetArtistSegment);
        var targetAlbumDir = Path.Combine(targetArtistDir, albumSegment);
        if (Directory.Exists(targetAlbumDir))
        {
            return FolderMoveOutcome.Conflict;
        }

        try
        {
            Directory.CreateDirectory(targetArtistDir);
            Directory.Move(sourceAlbumDir, targetAlbumDir);
            sourceArtistDirs.Add(Path.Combine(root, sourceArtistSegment));
            return FolderMoveOutcome.Moved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex,
                "Failed moving album directory during artist canonicalization: {SourceAlbumDir} -> {TargetAlbumDir}",
                sourceAlbumDir,
                targetAlbumDir);
            return FolderMoveOutcome.Failed;
        }
    }

    private void CleanupEmptySourceArtistDirectories(IEnumerable<string> sourceArtistDirs)
    {
        foreach (var sourceArtistDir in sourceArtistDirs)
        {
            try
            {
                if (Directory.Exists(sourceArtistDir)
                    && !Directory.EnumerateFileSystemEntries(sourceArtistDir).Any())
                {
                    Directory.Delete(sourceArtistDir, false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Failed deleting empty source artist directory {SourceArtistDir}.", sourceArtistDir);
                }
            }
        }
    }

    private static bool IsPathUnderRoot(string fullPath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(fullPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(normalizedPath, normalizedRoot, comparison))
        {
            return true;
        }

        var rootWithSep = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSep, comparison);
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown Artist";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[value.Length];
        var index = 0;
        var previousSpace = false;
        foreach (var ch in value.Trim())
        {
            if (invalid.Contains(ch) || ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar)
            {
                if (!previousSpace)
                {
                    buffer[index++] = ' ';
                    previousSpace = true;
                }

                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!previousSpace)
                {
                    buffer[index++] = ' ';
                    previousSpace = true;
                }
            }
            else
            {
                buffer[index++] = ch;
                previousSpace = false;
            }
        }

        var sanitized = new string(buffer, 0, index).Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown Artist" : sanitized;
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

public sealed record SpotifyArtistServiceDependencies(
    SpotifyPathfinderMetadataClient PathfinderMetadataClient,
    SpotifyMetadataService MetadataService,
    SpotifyDeezerLinkService DeezerLinkService,
    ShazamRecognitionService ShazamRecognitionService);

public sealed record SpotifyArtistMatchSuggestion(
    string SpotifyId,
    string Name,
    string? ImageUrl,
    int Score,
    int LocalAlbumOverlap,
    bool NameMatchesAlias,
    bool Verified,
    int TotalAlbums,
    int TotalTracks);
