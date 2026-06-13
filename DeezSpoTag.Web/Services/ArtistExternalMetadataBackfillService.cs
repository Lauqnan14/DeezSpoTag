using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistExternalMetadataBackfillService : BackgroundService
{
    private const int BatchSize = 50;
    private const int TrackEvidenceLimit = 25;
    private const int LastFmCandidateLimit = 8;
    private static readonly TimeSpan RefreshAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CycleDelay = TimeSpan.FromMinutes(30);
    private static readonly HttpClient ImageHttpClient = new();

    private readonly LibraryRepository _repository;
    private readonly AppleArtistBiographyService _appleBiographyService;
    private readonly LastFmArtistImageService _lastFmArtistImageService;
    private readonly ILogger<ArtistExternalMetadataBackfillService> _logger;
    private readonly string _cacheRoot;

    public ArtistExternalMetadataBackfillService(
        LibraryRepository repository,
        AppleArtistBiographyService appleBiographyService,
        LastFmArtistImageService lastFmArtistImageService,
        IWebHostEnvironment environment,
        ILogger<ArtistExternalMetadataBackfillService> logger)
    {
        _repository = repository;
        _appleBiographyService = appleBiographyService;
        _lastFmArtistImageService = lastFmArtistImageService;
        _logger = logger;
        _cacheRoot = Path.Join(AppDataPaths.GetDataRoot(environment), "library-artist-images", "lastfm", "artists");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Artist external metadata backfill cycle failed.");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Artist external metadata backfill cycle failed.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Artist external metadata backfill cycle failed.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Artist external metadata backfill cycle failed.");
            }

            try
            {
                await Task.Delay(CycleDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var staleBefore = DateTimeOffset.UtcNow.Subtract(RefreshAge);
        var artists = await _repository.GetArtistsForExternalMetadataBackfillAsync(staleBefore, BatchSize, cancellationToken);
        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await BackfillAppleBiographyAsync(artist, cancellationToken);
            await BackfillLastFmImagesAsync(artist, cancellationToken);
        }
    }

    public async Task<bool> RefreshArtistAsync(long artistId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured || artistId <= 0)
        {
            return false;
        }

        var artist = await _repository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return false;
        }

        var appleId = await _repository.GetArtistSourceIdAsync(artistId, "apple", cancellationToken);
        var request = new ArtistExternalMetadataBackfillDto(
            artist.Id,
            artist.Name,
            artist.AppleBiography,
            artist.AppleBiographyCheckedAt,
            artist.LastFmImagesCheckedAt,
            appleId);
        await BackfillAppleBiographyAsync(request, cancellationToken);
        await BackfillLastFmImagesAsync(request, cancellationToken);
        return true;
    }

    private async Task BackfillAppleBiographyAsync(
        ArtistExternalMetadataBackfillDto artist,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            AppleArtistBiographyResult? result = null;
            if (!string.IsNullOrWhiteSpace(artist.AppleId))
            {
                result = await _appleBiographyService.ResolveByArtistIdAsync(artist.AppleId, artist.Name, cancellationToken);
            }
            else
            {
                var trackTitles = await _repository.GetArtistTrackTitlesAsync(artist.Id, TrackEvidenceLimit, cancellationToken);
                result = await _appleBiographyService.ResolveByExactArtistNameAndTracksAsync(artist.Name, trackTitles, cancellationToken);
                if (result is not null && !string.IsNullOrWhiteSpace(result.AppleId))
                {
                    await _repository.UpsertArtistSourceIdAsync(artist.Id, "apple", result.AppleId, cancellationToken);
                }
            }

            await _repository.UpdateArtistAppleBiographyAsync(
                artist.Id,
                string.IsNullOrWhiteSpace(result?.Biography) ? null : result.Biography,
                now,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple biography backfill failed for artist {ArtistId}", artist.Id);
            await _repository.UpdateArtistAppleBiographyAsync(artist.Id, null, now, cancellationToken);
        }
    }

    private async Task BackfillLastFmImagesAsync(
        ArtistExternalMetadataBackfillDto artist,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var candidates = await _lastFmArtistImageService.SearchArtistImagesAsync(
                artist.Name,
                LastFmCandidateLimit,
                cancellationToken);
            await SaveLastFmCandidatesAsync(artist.Id, candidates, cancellationToken);
            await _repository.MarkArtistLastFmImagesCheckedAsync(artist.Id, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Last.fm image backfill failed for artist {ArtistId}", artist.Id);
            await _repository.MarkArtistLastFmImagesCheckedAsync(artist.Id, now, cancellationToken);
        }
    }

    private async Task SaveLastFmCandidatesAsync(
        long artistId,
        IReadOnlyList<LastFmArtistImageCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (artistId <= 0)
        {
            return;
        }

        var artistDir = Path.Join(_cacheRoot, artistId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Directory.Exists(artistDir))
        {
            Directory.Delete(artistDir, true);
        }

        if (candidates.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(artistDir);
        var index = 1;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = candidate.Url?.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            try
            {
                using var response = await ImageHttpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var extension = ResolveExtension(response.Content.Headers.ContentType?.MediaType);
                var targetPath = Path.Join(artistDir, $"candidate-{index:000}{extension}");
                await using var targetStream = File.Create(targetPath);
                await response.Content.CopyToAsync(targetStream, cancellationToken);
                index++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to cache Last.fm artist image candidate for artist {ArtistId}", artistId);
            }
        }
    }

    private static string ResolveExtension(string? mediaType)
        => mediaType switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".jpg"
        };
}
