using System.Text.Json;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyCentralMetadataService
{
    private const string AlbumType = "album";
    private readonly SpotifyMetadataCacheRepository _cacheRepository;
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly SpotifyMetadataService _metadataService;
    private readonly LibraryConfigStore _configStore;
    private readonly ILogger<SpotifyCentralMetadataService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public SpotifyCentralMetadataService(
        SpotifyMetadataCacheRepository cacheRepository,
        SpotifyPathfinderMetadataClient pathfinderMetadataClient,
        SpotifyMetadataService metadataService,
        LibraryConfigStore configStore,
        ILogger<SpotifyCentralMetadataService> logger)
    {
        _cacheRepository = cacheRepository;
        _pathfinderMetadataClient = pathfinderMetadataClient;
        _metadataService = metadataService;
        _configStore = configStore;
        _logger = logger;
    }

    public async Task<SpotifyArtistPageResult?> GetArtistPageAsync(
        string spotifyId,
        string artistName,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var allowCache = !forceRefresh;
        var cached = await _cacheRepository.TryGetAsync("artist", spotifyId, cancellationToken);
        if (cached != null)
        {
            var cachedPayload = Deserialize<SpotifyArtistPageResult>(cached.PayloadJson);
            cachedPayload = await TryHydrateCachedBiographyAsync(
                spotifyId,
                artistName,
                cachedPayload,
                cached.FetchedUtc,
                cancellationToken);
            if (allowCache)
            {
                AddActivity("info", $"[spotify-central] cache hit (manual refresh required): {artistName}.");
                return cachedPayload;
            }

            AddActivity("info", $"[spotify-central] cache bypassed due force refresh: {artistName}.");
        }

        AddActivity("info", $"[spotify-central] fetch: {artistName}.");
        var overview = await _pathfinderMetadataClient.FetchArtistOverviewAsync(spotifyId, cancellationToken);
        var metadata = await _pathfinderMetadataClient.FetchByUrlAsync($"https://open.spotify.com/artist/{spotifyId}", cancellationToken);
        var extras = await _pathfinderMetadataClient.FetchArtistExtrasAsync(spotifyId, cancellationToken);
        var relatedTask = _pathfinderMetadataClient.FetchArtistRelatedArtistsAsync(spotifyId, cancellationToken);
        var appearsOnTask = _pathfinderMetadataClient.FetchArtistAppearsOnAsync(spotifyId, cancellationToken);
        var topTracksTask = FetchTopTracksAsync(spotifyId, cancellationToken);

        if (overview is null && metadata is null)
        {
            return null;
        }

        var images = new List<SpotifyImage>();
        var imageUrl = overview?.ImageUrl ?? metadata?.ImageUrl;
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            images.Add(new SpotifyImage(imageUrl, null, null));
        }

        var profile = new SpotifyArtistProfile(
            overview?.Id ?? metadata?.Id ?? spotifyId,
            overview?.Name ?? metadata?.Name ?? "Spotify artist",
            images,
            overview?.Genres ?? new List<string>(),
            overview?.Followers ?? 0,
            overview?.Popularity ?? 0,
            overview?.SourceUrl ?? metadata?.SourceUrl,
            extras?.Biography,
            extras?.Verified,
            extras?.MonthlyListeners,
            extras?.Rank,
            overview?.HeaderImageUrl,
            overview?.Gallery ?? new List<string>(),
            overview?.DiscographyType,
            overview?.TotalAlbums);

        var albums = metadata?.AlbumList.Select(item =>
            new SpotifyAlbum(
                item.Id,
                item.Name,
                null,
                AlbumType,
                item.TotalTracks ?? 0,
                item.ImageUrl is null ? new List<SpotifyImage>() : new List<SpotifyImage> { new SpotifyImage(item.ImageUrl, null, null) },
                item.SourceUrl)).ToList() ?? new List<SpotifyAlbum>();

        await Task.WhenAll(relatedTask, appearsOnTask, topTracksTask);

        var related = relatedTask.Result ?? new List<SpotifyRelatedArtist>();
        var appearsOn = appearsOnTask.Result ?? new List<SpotifyAlbumSummary>();
        var topTracks = topTracksTask.Result ?? new List<SpotifyTrack>();

        var appearsOnAlbums = appearsOn.Select(item => new SpotifyAlbum(
                item.Id,
                item.Name,
                null,
                "appears_on",
                item.TotalTracks ?? 0,
                item.ImageUrl is null ? new List<SpotifyImage>() : new List<SpotifyImage> { new SpotifyImage(item.ImageUrl, null, null) },
                item.SourceUrl))
            .ToList();

        var result = new SpotifyArtistPageResult(true, profile, albums, appearsOnAlbums, topTracks, related);
        var payloadJson = JsonSerializer.Serialize(result, _jsonOptions);
        await _cacheRepository.UpsertAsync("artist", spotifyId, payloadJson, DateTimeOffset.UtcNow, cancellationToken);
        AddActivity("info", $"[spotify-central] data stored: {artistName} (id={spotifyId}).");
        return result;
    }

    private async Task<SpotifyArtistPageResult?> TryHydrateCachedBiographyAsync(
        string spotifyId,
        string artistName,
        SpotifyArtistPageResult? cachedPayload,
        DateTimeOffset fetchedUtc,
        CancellationToken cancellationToken)
    {
        if (cachedPayload is null || !IsPlaceholderBiography(cachedPayload.Artist?.Biography))
        {
            return cachedPayload;
        }

        var extras = await _pathfinderMetadataClient.FetchArtistExtrasAsync(spotifyId, cancellationToken);
        if (extras is null || IsPlaceholderBiography(extras.Biography))
        {
            return cachedPayload;
        }

        var currentArtist = cachedPayload.Artist;
        if (currentArtist is null)
        {
            return cachedPayload;
        }
        var hydratedPayload = cachedPayload with
        {
            Artist = currentArtist with
            {
                Biography = extras.Biography,
                Verified = currentArtist.Verified ?? extras.Verified,
                MonthlyListeners = currentArtist.MonthlyListeners ?? extras.MonthlyListeners,
                Rank = currentArtist.Rank ?? extras.Rank
            }
        };

        if (!JsonSerializer.Serialize(hydratedPayload, _jsonOptions).Equals(
            JsonSerializer.Serialize(cachedPayload, _jsonOptions),
            StringComparison.Ordinal))
        {
            await _cacheRepository.UpsertAsync(
                "artist",
                spotifyId,
                JsonSerializer.Serialize(hydratedPayload, _jsonOptions),
                fetchedUtc,
                cancellationToken);
            AddActivity("info", $"[spotify-central] cache biography rehydrated: {artistName}.");
        }

        return hydratedPayload;
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

    public async Task<SpotifyAlbumDetails?> GetAlbumDetailsAsync(
        string albumId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return null;
        }

        var allowCache = !forceRefresh;
        var cached = await _cacheRepository.TryGetAsync(AlbumType, albumId, cancellationToken);
        if (cached != null)
        {
            if (allowCache)
            {
                return Deserialize<SpotifyAlbumDetails>(cached.PayloadJson);
            }

            AddActivity("info", $"[spotify-central] album cache bypassed due force refresh: {albumId}.");
        }

        var metadata = await _pathfinderMetadataClient.FetchByUrlAsync(
            $"https://open.spotify.com/{AlbumType}/{albumId}",
            cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        var name = metadata.Name;
        var artists = metadata.Subtitle;
        var imageUrl = metadata.ImageUrl;
        var sourceUrl = metadata.SourceUrl;
        var totalTracks = metadata.TotalTracks ?? metadata.TrackList.Count;
        var albumType = metadata.AlbumList.FirstOrDefault(a =>
                a.Id.Equals(albumId, StringComparison.OrdinalIgnoreCase))
            ?.ReleaseType;
        var releaseDate = metadata.AlbumList.FirstOrDefault(a =>
                a.Id.Equals(albumId, StringComparison.OrdinalIgnoreCase))
            ?.ReleaseDate;
        var pathfinderAlbum = metadata.AlbumList.FirstOrDefault(a =>
            a.Id.Equals(albumId, StringComparison.OrdinalIgnoreCase));
        var label = metadata.TrackList
            .Select(track => track.Label)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        label ??= pathfinderAlbum?.Label;
        var tracks = metadata.TrackList;

        var result = new SpotifyAlbumDetails(
            albumId,
            name,
            artists,
            imageUrl,
            sourceUrl,
            releaseDate,
            totalTracks,
            albumType,
            label,
            tracks,
            pathfinderAlbum?.Genres,
            pathfinderAlbum?.ReleaseDatePrecision,
            pathfinderAlbum?.AvailableMarkets,
            pathfinderAlbum?.Copyrights,
            pathfinderAlbum?.CopyrightText,
            pathfinderAlbum?.Popularity,
            pathfinderAlbum?.Review,
            pathfinderAlbum?.RelatedAlbumIds,
            pathfinderAlbum?.OriginalTitle,
            pathfinderAlbum?.VersionTitle,
            pathfinderAlbum?.SalePeriods,
            pathfinderAlbum?.Availability);

        var payloadJson = JsonSerializer.Serialize(result, _jsonOptions);
        await _cacheRepository.UpsertAsync(AlbumType, albumId, payloadJson, DateTimeOffset.UtcNow, cancellationToken);
        QueueAlbumFallbackEnrichment(albumId, result);
        return result;
    }

    private void QueueAlbumFallbackEnrichment(string albumId, SpotifyAlbumDetails baseResult)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var fallback = await _metadataService.FetchAlbumFallbackWithLibrespotAsync(
                    albumId,
                    CancellationToken.None);
                if (fallback is null)
                {
                    return;
                }

                var enriched = ApplyAlbumFallback(baseResult, fallback);
                if (ReferenceEquals(enriched, baseResult))
                {
                    return;
                }

                var payloadJson = JsonSerializer.Serialize(enriched, _jsonOptions);
                await _cacheRepository.UpsertAsync(
                    AlbumType,
                    albumId,
                    payloadJson,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                AddActivity("info", $"[spotify-central] librespot album fallback cached lazily: {albumId}.");
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Spotify album librespot fallback enrichment failed for {AlbumId}", albumId);
                }
            }
        });
    }

    private static SpotifyAlbumDetails ApplyAlbumFallback(
        SpotifyAlbumDetails result,
        SpotifyAlbumSummary fallback)
    {
        var changed = false;

        IReadOnlyList<string>? genres = result.Genres;
        changed |= ReplaceIfEmptyList(ref genres, fallback.Genres);

        var releaseDatePrecision = result.ReleaseDatePrecision;
        changed |= ReplaceIfEmpty(ref releaseDatePrecision, fallback.ReleaseDatePrecision);

        IReadOnlyList<string>? availableMarkets = result.AvailableMarkets;
        changed |= ReplaceIfEmptyList(ref availableMarkets, fallback.AvailableMarkets);

        IReadOnlyList<SpotifyCopyrightInfo>? copyrights = result.Copyrights;
        changed |= ReplaceIfEmptyList(ref copyrights, fallback.Copyrights);

        var copyrightText = result.CopyrightText;
        changed |= ReplaceIfEmpty(ref copyrightText, fallback.CopyrightText);

        var popularity = result.Popularity;
        changed |= ReplaceIfEmpty(ref popularity, fallback.Popularity);

        var review = result.Review;
        changed |= ReplaceIfEmpty(ref review, fallback.Review);

        IReadOnlyList<string>? relatedAlbumIds = result.RelatedAlbumIds;
        changed |= ReplaceIfEmptyList(ref relatedAlbumIds, fallback.RelatedAlbumIds);

        var originalTitle = result.OriginalTitle;
        changed |= ReplaceIfEmpty(ref originalTitle, fallback.OriginalTitle);

        var versionTitle = result.VersionTitle;
        changed |= ReplaceIfEmpty(ref versionTitle, fallback.VersionTitle);

        IReadOnlyList<SpotifySalePeriod>? salePeriods = result.SalePeriods;
        changed |= ReplaceIfEmptyList(ref salePeriods, fallback.SalePeriods);

        IReadOnlyList<SpotifyAvailabilityInfo>? availability = result.Availability;
        changed |= ReplaceIfEmptyList(ref availability, fallback.Availability);

        return changed
            ? result with
            {
                Genres = genres,
                ReleaseDatePrecision = releaseDatePrecision,
                AvailableMarkets = availableMarkets,
                Copyrights = copyrights,
                CopyrightText = copyrightText,
                Popularity = popularity,
                Review = review,
                RelatedAlbumIds = relatedAlbumIds,
                OriginalTitle = originalTitle,
                VersionTitle = versionTitle,
                SalePeriods = salePeriods,
                Availability = availability
            }
            : result;
    }

    private static bool ReplaceIfEmpty(ref string? current, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(fallback))
        {
            return false;
        }

        current = fallback;
        return true;
    }

    private static bool ReplaceIfEmpty(ref int? current, int? fallback)
    {
        if (current.HasValue || !fallback.HasValue)
        {
            return false;
        }

        current = fallback;
        return true;
    }

    private static bool ReplaceIfEmptyList<T>(ref IReadOnlyList<T>? current, IReadOnlyList<T>? fallback)
    {
        if ((current?.Count ?? 0) > 0 || (fallback?.Count ?? 0) == 0)
        {
            return false;
        }

        current = fallback;
        return true;
    }

    private async Task<List<SpotifyTrack>> FetchTopTracksAsync(string spotifyId, CancellationToken cancellationToken)
    {
        try
        {
            var tracks = await _pathfinderMetadataClient.FetchArtistTopTracksAsync(spotifyId, cancellationToken);
            if (tracks.Count == 0)
            {
                return new List<SpotifyTrack>();
            }

            var results = new List<SpotifyTrack>();
            foreach (var track in tracks)
            {
                var id = track.Id;
                var name = track.Name;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var albumImages = string.IsNullOrWhiteSpace(track.ImageUrl)
                    ? new List<SpotifyImage>()
                    : new List<SpotifyImage> { new SpotifyImage(track.ImageUrl, null, null) };

                results.Add(new SpotifyTrack(
                    id,
                    name,
                    track.DurationMs ?? 0,
                    0,
                    null,
                    track.SourceUrl,
                    albumImages,
                    track.Album,
                    track.ReleaseDate));
            }

            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddActivity("warn", $"[spotify-central] top tracks fetch failed: {ex.Message}");
            return new List<SpotifyTrack>();
        }
    }

    private T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify central cache deserialize failed.");
            return default;
        }
    }

    private void AddActivity(string level, string message)
    {
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, level, message));
    }
}
