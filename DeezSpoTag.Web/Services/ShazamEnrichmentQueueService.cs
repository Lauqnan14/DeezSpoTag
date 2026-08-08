using System.Threading.Channels;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.Extensions.Caching.Memory;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Work item describing a recognized capture whose discovery lookups still have to run.
/// </summary>
public sealed record ShazamEnrichmentRequest(
    ShazamRecognitionInfo Recognition,
    string? Query,
    string CapturePhase,
    string CaptureAttempt,
    string LogoSessionId,
    string ClientRequestId);

/// <summary>
/// Runs Shazam result enrichment off the live-capture request path.
///
/// Track, related and search lookups each spawn a Python discovery process. Awaiting them
/// inline kept the user on the "Searching" overlay for seconds after the match was already
/// known, so recognition now responds immediately and the enriched payload is published to
/// the result cache here. The results page reads that cache by client request id.
/// </summary>
public sealed class ShazamEnrichmentQueueService : BackgroundService
{
    private const int MaxQueuedRequests = 64;
    private const int MaxDiscoveryResults = 20;
    private static readonly TimeSpan ResultCacheDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResultSlidingExpiration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EnrichmentBudget = TimeSpan.FromSeconds(45);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<ShazamEnrichmentQueueService> _logger;

    // Wait (rather than drop) so a saturated queue surfaces as a failed TryWrite: the
    // caller then marks the payload final instead of letting the client poll for an
    // enrichment that will never arrive.
    private readonly Channel<ShazamEnrichmentRequest> _channel =
        Channel.CreateBounded<ShazamEnrichmentRequest>(new BoundedChannelOptions(MaxQueuedRequests)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public ShazamEnrichmentQueueService(
        IServiceScopeFactory scopeFactory,
        IMemoryCache memoryCache,
        ILogger<ShazamEnrichmentQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    public bool TryEnqueue(ShazamEnrichmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsAnonymousRequestId(request.ClientRequestId))
        {
            // Without a request id the results page has no way to collect the enrichment.
            return false;
        }

        return _channel.Writer.TryWrite(request);
    }

    public void StoreResult(string clientRequestId, object payload)
    {
        if (IsAnonymousRequestId(clientRequestId))
        {
            return;
        }

        _memoryCache.Set(
            BuildCacheKey(clientRequestId),
            payload,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ResultCacheDuration,
                SlidingExpiration = ResultSlidingExpiration,
                Size = 1
            });
    }

    public bool TryGetResult(string clientRequestId, out object? payload)
    {
        if (IsAnonymousRequestId(clientRequestId))
        {
            payload = null;
            return false;
        }

        return _memoryCache.TryGetValue(BuildCacheKey(clientRequestId), out payload);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await EnrichAsync(request, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Shazam enrichment exceeded its budget for clientRequestId={ClientRequestId}.",
                        request.ClientRequestId);
                }
                catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
                {
                    _logger.LogWarning(
                        ex,
                        "Shazam enrichment failed for clientRequestId={ClientRequestId}.",
                        request.ClientRequestId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task EnrichAsync(ShazamEnrichmentRequest request, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var discovery = scope.ServiceProvider.GetRequiredService<ShazamDiscoveryService>();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        budget.CancelAfter(EnrichmentBudget);
        var cancellationToken = budget.Token;

        var trackId = request.Recognition.TrackId;
        ShazamTrackCard? track = null;
        IReadOnlyList<ShazamTrackCard> related = Array.Empty<ShazamTrackCard>();

        // Search runs alongside the catalog lookups rather than as a fallback: the results
        // page renders it as its own section, so skipping it would drop page content.
        var searchTask = SafeSearchTracksAsync(discovery, request.Query, cancellationToken);

        if (!string.IsNullOrWhiteSpace(trackId))
        {
            var trackTask = SafeGetTrackAsync(discovery, trackId, cancellationToken);
            var relatedTask = SafeGetRelatedTracksAsync(discovery, trackId, cancellationToken);
            await Task.WhenAll(trackTask, relatedTask, searchTask);
            track = await trackTask;
            related = await relatedTask;
        }

        var searchResults = await searchTask;

        var payload = ShazamRecognitionApiController.BuildMatchPayload(
            new ShazamRecognitionApiController.ShazamLogoMatchPayload(
                Recognition: request.Recognition,
                Query: request.Query,
                Track: track,
                Related: related,
                SearchResults: searchResults,
                CapturePhase: request.CapturePhase,
                CaptureAttempt: request.CaptureAttempt,
                LogoSessionId: request.LogoSessionId,
                ClientRequestId: request.ClientRequestId));

        StoreResult(request.ClientRequestId, payload);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Shazam enrichment published: clientRequestId={ClientRequestId}, trackResolved={TrackResolved}, relatedCount={RelatedCount}, searchResultCount={SearchResultCount}.",
                request.ClientRequestId,
                track != null,
                related.Count,
                searchResults.Count);
        }
    }

    private async Task<ShazamTrackCard?> SafeGetTrackAsync(
        ShazamDiscoveryService discovery,
        string trackId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await discovery.GetTrackAsync(trackId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Shazam track enrichment lookup failed for trackId {TrackId}.", trackId);
            return null;
        }
    }

    private async Task<IReadOnlyList<ShazamTrackCard>> SafeGetRelatedTracksAsync(
        ShazamDiscoveryService discovery,
        string trackId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await discovery.GetRelatedTracksAsync(trackId, MaxDiscoveryResults, offset: 0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Shazam related-track lookup failed for trackId {TrackId}.", trackId);
            return Array.Empty<ShazamTrackCard>();
        }
    }

    private async Task<IReadOnlyList<ShazamTrackCard>> SafeSearchTracksAsync(
        ShazamDiscoveryService discovery,
        string? query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ShazamTrackCard>();
        }

        try
        {
            return await discovery.SearchTracksAsync(query, MaxDiscoveryResults, offset: 0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Shazam search lookup failed during enrichment.");
            return Array.Empty<ShazamTrackCard>();
        }
    }

    private static bool IsAnonymousRequestId(string? clientRequestId)
        => string.IsNullOrWhiteSpace(clientRequestId)
            || string.Equals(clientRequestId, "none", StringComparison.OrdinalIgnoreCase);

    private static string BuildCacheKey(string clientRequestId)
        => $"shazam:logo-result:{clientRequestId}";
}
