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
    internal static readonly TimeSpan IdentityImportDeadline = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan IdentityImportPollInterval = TimeSpan.FromSeconds(20);
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

        await QueueMissingFolderIdentitiesAsync(cancellationToken);
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

    private async Task QueueMissingFolderIdentitiesAsync(CancellationToken cancellationToken)
    {
        var services = await _refreshService.GetConfiguredServicesAsync();
        if (services.Count == 0)
        {
            return;
        }

        var folders = await _repository.GetFoldersAsync(cancellationToken);
        var existing = await _repository.GetMediaServerRefreshOutboxSummariesAsync(cancellationToken);
        var busy = existing
            .Where(static row => row.Status is "pending" or "retry" or "processing")
            .Select(static row => $"{row.FolderId}:{row.Service}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failed = existing
            .Where(static row => row.Status == "failed")
            .Select(static row => $"{row.FolderId}:{row.Service}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders.Where(static folder => folder.Enabled && folder.Id > 0))
        {
            foreach (var service in services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = $"{folder.Id}:{service}";
                if (busy.Contains(key) || failed.Contains(key))
                {
                    continue;
                }

                var coverage = (await _repository.GetTargetServerIdentityCoverageAsync(
                    [service],
                    folder.Id,
                    cancellationToken)).FirstOrDefault();
                if (coverage is null || coverage.MissingTracks <= 0)
                {
                    continue;
                }

                var tracks = await _repository.GetTargetServerIdentityLocalTracksAsync(
                    service,
                    folder.Id,
                    cancellationToken);
                var missingIds = tracks
                    .Where(static track => string.IsNullOrWhiteSpace(track.TargetItemId))
                    .Select(static track => track.TrackId)
                    .ToList();
                if (missingIds.Count == 0)
                {
                    continue;
                }

                var refreshFiles = await _repository.GetMediaServerIdentityRefreshFilesAsync(
                    missingIds,
                    service,
                    cancellationToken);
                var paths = refreshFiles
                    .Select(static item => item.FilePath)
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .ToList();
                if (paths.Count == 0)
                {
                    continue;
                }

                await EnqueueTargetAsync(folder.Id, service, paths, cancellationToken);
            }
        }
    }

    private async Task ProcessJobAsync(
        MediaServerRefreshOutboxDto job,
        CancellationToken cancellationToken)
    {
        try
        {
            // A job for a server that is no longer configured can never complete:
            // fail it terminally instead of retrying forever.
            var configuredServices = await _refreshService.GetConfiguredServicesAsync();
            if (!configuredServices.Contains(job.TargetService, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Media-server refresh job {JobId} for {Service} failed: the server is not configured.",
                    job.Id,
                    job.TargetService);
                await _repository.FailMediaServerRefreshAsync(
                    job.Id,
                    _leaseOwner,
                    $"{job.TargetService} is not configured; job abandoned.",
                    cancellationToken);
                return;
            }

            job = await _repository.GetMediaServerRefreshOutboxAsync(job.Id, cancellationToken) ?? job;
            var now = DateTimeOffset.UtcNow;
            var deadlineUtc = job.DeadlineUtc ?? now.Add(IdentityImportDeadline);
            if (now >= deadlineUtc)
            {
                await _repository.FailMediaServerRefreshAsync(
                    job.Id,
                    _leaseOwner,
                    $"{job.TargetService} did not expose the requested track IDs within {IdentityImportDeadline.TotalMinutes:0} minutes.",
                    cancellationToken);
                return;
            }

            if (job.ScanSubmittedUtc is null)
            {
                var submitted = await _refreshService.RequestLibraryRefreshAsync(
                    job.TargetService,
                    cancellationToken);
                if (!submitted)
                {
                    await RetryScanSubmissionAsync(
                        job,
                        $"{job.TargetService} rejected the library refresh request.",
                        cancellationToken);
                    return;
                }

                await _repository.MarkMediaServerRefreshScanSubmittedAsync(
                    job.Id,
                    _leaseOwner,
                    now,
                    cancellationToken);
                await RetryAsync(
                    job,
                    $"{job.TargetService} scan submitted; waiting for the server to finish indexing.",
                    job.ChangedFilePaths,
                    job.RequestedTrackIds,
                    cancellationToken);
                return;
            }

            if (await _refreshService.IsLibraryScanRunningAsync(job.TargetService, cancellationToken))
            {
                await RetryAsync(
                    job,
                    $"Waiting for the {job.TargetService} scan to finish indexing.",
                    job.ChangedFilePaths,
                    job.RequestedTrackIds,
                    cancellationToken);
                return;
            }

            var verification = await RefreshAndVerifyRequestedIdentitiesAsync(job, cancellationToken);
            if (verification.NewMappings > 0)
            {
                _watchlistRunSignal?.Request(WatchlistWakeReason.TargetSync);
            }

            if (verification.IsComplete)
            {
                var coverage = (await _repository.GetTargetServerIdentityCoverageAsync(
                    [job.TargetService],
                    job.DestinationFolderId,
                    cancellationToken)).FirstOrDefault();
                if (coverage is { MissingTracks: > 0 })
                {
                    await RetryAsync(
                        job,
                        $"{job.TargetService} still missing {coverage.MissingTracks} library track IDs.",
                        job.ChangedFilePaths,
                        job.RequestedTrackIds,
                        cancellationToken);
                    return;
                }

                await _repository.CompleteMediaServerRefreshAsync(job.Id, _leaseOwner, cancellationToken);
                return;
            }

            await RetryAsync(
                job,
                verification.Error,
                verification.RemainingPaths,
                verification.RemainingTrackIds,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Media-server refresh job {JobId} for {Service} was interrupted; it will resume.",
                job.Id,
                job.TargetService);
            try
            {
                await RetryAsync(
                    job,
                    "Interrupted; will resume waiting for the server index.",
                    job.ChangedFilePaths,
                    job.RequestedTrackIds,
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to persist interruption for media-server refresh job {JobId}.", job.Id);
            }

            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Media-server refresh failed independently for {Service}, destination folder {DestinationFolderId}.",
                job.TargetService,
                job.DestinationFolderId);
            if (job.ScanSubmittedUtc is null)
            {
                await RetryScanSubmissionAsync(job, ex.Message, cancellationToken);
            }
            else
            {
                await RetryAsync(
                    job,
                    ex.Message,
                    job.ChangedFilePaths,
                    job.RequestedTrackIds,
                    cancellationToken);
            }
        }
    }

    private async Task<IdentityVerificationResult> RefreshAndVerifyRequestedIdentitiesAsync(
        MediaServerRefreshOutboxDto job,
        CancellationToken cancellationToken)
    {
        var trackIds = job.RequestedTrackIds.Where(static id => id > 0).ToHashSet();
        var unresolvedPaths = new List<string>();
        var trackIdByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in job.ChangedFilePaths)
        {
            var trackId = await _repository.GetTrackIdForFilePathAsync(filePath, cancellationToken);
            if (trackId.HasValue && trackId.Value > 0)
            {
                trackIds.Add(trackId.Value);
                trackIdByPath[filePath] = trackId.Value;
            }
            else
            {
                unresolvedPaths.Add(filePath);
            }
        }

        if (trackIds.Count == 0)
        {
            _logger.LogWarning(
                "Media-server refresh job {JobId} for {Service} has no locally resolvable track IDs; {UnresolvedPathCount} paths were skipped independently.",
                job.Id,
                job.TargetService,
                unresolvedPaths.Count);
            return new IdentityVerificationResult(
                IsComplete: false,
                NewMappings: 0,
                RemainingPaths: job.ChangedFilePaths,
                RemainingTrackIds: [],
                Error: $"{job.TargetService} could not resolve local track IDs for the queued library paths.");
        }

        var before = await _repository.GetMediaServerItemIdsByTrackIdsAsync(
            job.TargetService,
            trackIds.ToList(),
            cancellationToken);
        var fetch = await _refreshService.FetchTargetIdentitiesAsync(
            job.TargetService,
            job.DestinationFolderId,
            resetFirst: false,
            cancellationToken,
            requestedTrackIds: trackIds.ToList());
        if (!fetch.Success)
        {
            return new IdentityVerificationResult(
                IsComplete: false,
                NewMappings: 0,
                RemainingPaths: job.ChangedFilePaths,
                RemainingTrackIds: trackIds.Order().ToList(),
                Error: fetch.Error ?? $"{job.TargetService} target identity fetch failed.");
        }

        var mapped = await _repository.GetMediaServerItemIdsByTrackIdsAsync(
            job.TargetService,
            trackIds.ToList(),
            cancellationToken);
        var remainingTrackIds = trackIds
            .Where(trackId => !mapped.ContainsKey(trackId))
            .Order()
            .ToList();
        var remainingSet = remainingTrackIds.ToHashSet();
        var remainingPaths = unresolvedPaths
            .Concat(trackIdByPath
                .Where(entry => remainingSet.Contains(entry.Value))
                .Select(static entry => entry.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (remainingTrackIds.Count == 0 && unresolvedPaths.Count > 0)
        {
            _logger.LogWarning(
                "Media-server refresh job {JobId} for {Service} completed valid track identities while skipping {UnresolvedPathCount} independently unresolved paths.",
                job.Id,
                job.TargetService,
                unresolvedPaths.Count);
        }

        return new IdentityVerificationResult(
            IsComplete: remainingTrackIds.Count == 0 && unresolvedPaths.Count == 0,
            NewMappings: CountChangedMappings(before, mapped),
            RemainingPaths: remainingPaths,
            RemainingTrackIds: remainingTrackIds,
            Error: $"{job.TargetService} is still missing {remainingTrackIds.Count} requested track IDs.");
    }

    internal static int CountChangedMappings(
        IReadOnlyDictionary<long, string> before,
        IReadOnlyDictionary<long, string> after)
        => after.Count(entry => !before.TryGetValue(entry.Key, out var previousId)
            || !string.Equals(previousId, entry.Value, StringComparison.Ordinal));

    private async Task RetryAsync(
        MediaServerRefreshOutboxDto job,
        string error,
        IReadOnlyCollection<string> remainingPaths,
        IReadOnlyCollection<long> remainingTrackIds,
        CancellationToken cancellationToken)
    {
        var attempt = job.AttemptCount + 1;
        var nextAttemptUtc = DateTimeOffset.UtcNow.Add(ResolveIdentityImportRetryDelay(attempt));
        if (job.DeadlineUtc is { } deadline && nextAttemptUtc > deadline)
        {
            nextAttemptUtc = deadline;
        }

        await _repository.RetryMediaServerRefreshAsync(
            job.Id,
            _leaseOwner,
            attempt,
            nextAttemptUtc,
            error,
            remainingPaths,
            remainingTrackIds,
            cancellationToken);
    }

    private async Task RetryScanSubmissionAsync(
        MediaServerRefreshOutboxDto job,
        string error,
        CancellationToken cancellationToken)
    {
        await _repository.RetryMediaServerRefreshAsync(
            job.Id,
            _leaseOwner,
            attemptCount: 0,
            DateTimeOffset.UtcNow.Add(IdentityImportPollInterval),
            error,
            job.ChangedFilePaths,
            job.RequestedTrackIds,
            cancellationToken);
    }

    internal static TimeSpan ResolveIdentityImportRetryDelay(int attempt)
        => IdentityImportPollInterval;

    private sealed record IdentityVerificationResult(
        bool IsComplete,
        int NewMappings,
        IReadOnlyList<string> RemainingPaths,
        IReadOnlyList<long> RemainingTrackIds,
        string Error);
}
