namespace DeezSpoTag.Services.Runtime;

public sealed class BackgroundWorkCoordinator
{
    public static readonly TimeSpan DefaultStartupGracePeriod = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider;
    private readonly object _stateLock = new();
    private readonly TaskCompletionSource _backgroundWorkersReleased =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset? _watcherDegradedUntilUtc;
    private string _watcherDegradedReason = string.Empty;
    private DateTimeOffset? _applicationStartedAtUtc;
    private DateTimeOffset? _startupGraceEndsAtUtc;
    private DateTimeOffset? _backgroundWorkersReleasedAtUtc;

    public BackgroundWorkCoordinator()
        : this(TimeProvider.System)
    {
    }

    public BackgroundWorkCoordinator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
    }

    public DateTimeOffset? ApplicationStartedAtUtc
    {
        get
        {
            lock (_stateLock)
            {
                return _applicationStartedAtUtc;
            }
        }
    }

    public DateTimeOffset StartupGraceEndsAtUtc
    {
        get
        {
            lock (_stateLock)
            {
                return _startupGraceEndsAtUtc ?? DateTimeOffset.MaxValue;
            }
        }
    }

    public bool IsStartupGraceActive => !_backgroundWorkersReleased.Task.IsCompleted;

    public bool BackgroundWorkersReleased => _backgroundWorkersReleased.Task.IsCompletedSuccessfully;

    public BackgroundWorkSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            var now = _timeProvider.GetUtcNow();
            var startupGraceActive = !_backgroundWorkersReleased.Task.IsCompleted;
            var watcherDegraded = _watcherDegradedUntilUtc.HasValue && _watcherDegradedUntilUtc.Value > now;
            return new BackgroundWorkSnapshot(
                startupGraceActive,
                _applicationStartedAtUtc,
                _startupGraceEndsAtUtc,
                _backgroundWorkersReleasedAtUtc,
                watcherDegraded,
                watcherDegraded ? _watcherDegradedUntilUtc : null,
                watcherDegraded ? _watcherDegradedReason : string.Empty);
        }
    }

    public void MarkApplicationStarted(TimeSpan? gracePeriod = null)
    {
        var delay = gracePeriod ?? DefaultStartupGracePeriod;
        lock (_stateLock)
        {
            if (_applicationStartedAtUtc.HasValue)
            {
                return;
            }

            _applicationStartedAtUtc = _timeProvider.GetUtcNow();
            _startupGraceEndsAtUtc = _applicationStartedAtUtc.Value.Add(delay);
        }

        if (delay <= TimeSpan.Zero)
        {
            ReleaseBackgroundWorkers();
            return;
        }

        _ = ReleaseBackgroundWorkersAfterDelayAsync(delay);
    }

    public Task WaitForStartupGraceAsync(CancellationToken cancellationToken)
    {
        return _backgroundWorkersReleased.Task.WaitAsync(cancellationToken);
    }

    public void ReleaseBackgroundWorkers()
    {
        lock (_stateLock)
        {
            _backgroundWorkersReleasedAtUtc ??= _timeProvider.GetUtcNow();
        }

        _backgroundWorkersReleased.TrySetResult();
    }

    private async Task ReleaseBackgroundWorkersAfterDelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _timeProvider);
            ReleaseBackgroundWorkers();
        }
        catch (OperationCanceledException)
        {
            // No cancellation token is supplied; keep this for TimeProvider implementations that may throw.
        }
    }

    public bool CanRunLibraryWatchers()
    {
        lock (_stateLock)
        {
            return BackgroundWorkersReleased
                && (!_watcherDegradedUntilUtc.HasValue || _watcherDegradedUntilUtc.Value <= _timeProvider.GetUtcNow());
        }
    }

    public void MarkLibraryWatchersDegraded(string reason)
    {
        lock (_stateLock)
        {
            _watcherDegradedUntilUtc = DateTimeOffset.MaxValue;
            _watcherDegradedReason = string.IsNullOrWhiteSpace(reason)
                ? "Library realtime watchers are temporarily disabled."
                : reason.Trim();
        }
    }
}

public sealed record BackgroundWorkSnapshot(
    bool StartupGraceActive,
    DateTimeOffset? ApplicationStartedAtUtc,
    DateTimeOffset? StartupGraceEndsAtUtc,
    DateTimeOffset? BackgroundWorkersReleasedAtUtc,
    bool LibraryWatchersDegraded,
    DateTimeOffset? LibraryWatchersDegradedUntilUtc,
    string LibraryWatchersDegradedReason);
