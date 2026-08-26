using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class MediaServerRefreshOutboxService : BackgroundService
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".aiff", ".aif", ".alac", ".m4a", ".m4b", ".mp4",
        ".aac", ".mp3", ".wma", ".ogg", ".opus", ".oga", ".ape", ".wv", ".dsf", ".dff"
    };
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);
    private readonly LibraryRepository _repository;
    private readonly MediaServerLibraryRefreshService _refreshService;
    private readonly ILogger<MediaServerRefreshOutboxService> _logger;
    private readonly WatchlistRunSignal? _watchlistRunSignal;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public MediaServerRefreshOutboxService(
        LibraryRepository repository,
        MediaServerLibraryRefreshService refreshService,
        ILogger<MediaServerRefreshOutboxService> logger,
        WatchlistRunSignal? watchlistRunSignal = null)
    {
        _repository = repository;
        _refreshService = refreshService;
        _logger = logger;
        _watchlistRunSignal = watchlistRunSignal;
    }

    public async Task EnqueueAsync(
        long destinationFolderId,
        IReadOnlyCollection<string> changedFilePaths,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured || destinationFolderId <= 0 || changedFilePaths.Count == 0)
        {
            return;
        }

        var services = await _refreshService.GetConfiguredServicesAsync();
        foreach (var service in services)
        {
            await EnqueueTargetAsync(destinationFolderId, service, changedFilePaths, cancellationToken);
        }

        if (services.Count > 0 && _wakeSignal.CurrentCount == 0)
        {
            _wakeSignal.Release();
        }
    }

    public async Task EnqueueTargetAsync(
        long destinationFolderId,
        string targetService,
        IReadOnlyCollection<string> changedFilePaths,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured
            || destinationFolderId <= 0
            || string.IsNullOrWhiteSpace(targetService)
            || changedFilePaths.Count == 0)
        {
            return;
        }

        var audioPaths = changedFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && AudioExtensions.Contains(Path.GetExtension(path)))
            .Select(static path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (audioPaths.Count == 0)
        {
            return;
        }

        var trackIds = new HashSet<long>();
        foreach (var path in audioPaths)
        {
            var trackId = await _repository.GetTrackIdForFilePathAsync(path, cancellationToken);
            if (trackId is > 0)
            {
                trackIds.Add(trackId.Value);
            }
        }

        await _repository.EnqueueMediaServerRefreshAsync(
            destinationFolderId,
            targetService,
            audioPaths,
            trackIds,
            cancellationToken: cancellationToken);
        if (_wakeSignal.CurrentCount == 0)
        {
            _wakeSignal.Release();
        }
    }

    public async Task<(int Pending, int Processing, int Retry)> GetStatusAsync(
        CancellationToken cancellationToken = default)
        => await _repository.GetMediaServerRefreshOutboxCountsAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueJobsAsync(stoppingToken);
                await _wakeSignal.WaitAsync(IdlePollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Media-server refresh outbox cycle failed; pending jobs will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task ProcessDueJobsAsync(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var jobs = await _repository.ClaimDueMediaServerRefreshesAsync(
                6,
                ProcessingLease,
                _leaseOwner,
                cancellationToken);
            if (jobs.Count == 0)
            {
                return;
            }

            await Task.WhenAll(jobs.Select(job => ProcessJobAsync(job, cancellationToken)));
        }
    }

    private async Task ProcessJobAsync(
        MediaServerRefreshOutboxDto job,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await RefreshAndVerifyRequestedIdentitiesAsync(job, cancellationToken))
            {
                await _repository.CompleteMediaServerRefreshAsync(job.Id, _leaseOwner, cancellationToken);
                _watchlistRunSignal?.Request(WatchlistWakeReason.TargetSync);
                return;
            }

            if (job.AttemptCount == 0)
            {
                var submitted = await _refreshService.RequestLibraryRefreshAsync(
                    job.TargetService,
                    cancellationToken);
                if (!submitted)
                {
                    await RetryAsync(job, $"{job.TargetService} rejected the library refresh request.", cancellationToken);
                    return;
                }

                await RetryAsync(
                    job,
                    $"{job.TargetService} scan submitted; waiting for requested track IDs.",
                    cancellationToken);
                return;
            }

            await RetryAsync(job, $"{job.TargetService} is still indexing one or more requested tracks.", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Media-server refresh failed independently for {Service}, destination folder {DestinationFolderId}.",
                job.TargetService,
                job.DestinationFolderId);
            await RetryAsync(job, ex.Message, cancellationToken);
        }
    }

    private async Task<bool> RefreshAndVerifyRequestedIdentitiesAsync(
        MediaServerRefreshOutboxDto job,
        CancellationToken cancellationToken)
    {
        var trackIds = job.RequestedTrackIds.Where(static id => id > 0).ToHashSet();
        var unresolvedPaths = new List<string>();
        foreach (var filePath in job.ChangedFilePaths)
        {
            var trackId = await _repository.GetTrackIdForFilePathAsync(filePath, cancellationToken);
            if (trackId.HasValue && trackId.Value > 0)
            {
                trackIds.Add(trackId.Value);
            }
            else
            {
                unresolvedPaths.Add(filePath);
            }
        }

        if (unresolvedPaths.Count > 0 || trackIds.Count == 0)
        {
            return false;
        }

        await _refreshService.UpdateTrackMetadataIndexAsync(
            job.TargetService,
            job.DestinationFolderId,
            trackIds,
            cancellationToken);

        var mapped = await _repository.GetMediaServerItemIdsByTrackIdsAsync(
            job.TargetService,
            trackIds.ToList(),
            cancellationToken);
        return trackIds.All(mapped.ContainsKey);
    }

    private async Task RetryAsync(
        MediaServerRefreshOutboxDto job,
        string error,
        CancellationToken cancellationToken)
    {
        var attempt = job.AttemptCount + 1;
        await _repository.RetryMediaServerRefreshAsync(
            job.Id,
            _leaseOwner,
            attempt,
            DateTimeOffset.UtcNow.Add(ResolveIdentityImportRetryDelay(attempt)),
            error,
            cancellationToken);
    }

    internal static TimeSpan ResolveIdentityImportRetryDelay(int attempt)
    {
        if (attempt <= 1)
        {
            return TimeSpan.FromMinutes(2);
        }

        if (attempt == 2)
        {
            return TimeSpan.FromMinutes(3);
        }

        return TimeSpan.FromMinutes(5);
    }
}
