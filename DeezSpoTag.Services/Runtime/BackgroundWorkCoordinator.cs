namespace DeezSpoTag.Services.Runtime;

public sealed class BackgroundWorkCoordinator
{
    public static readonly TimeSpan DefaultStartupGracePeriod = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultWatcherDegradedPeriod = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly object _stateLock = new();
    private DateTimeOffset? _watcherDegradedUntilUtc;
    private string _watcherDegradedReason = string.Empty;

    public BackgroundWorkCoordinator()
        : this(TimeProvider.System)
    {
    }

    public BackgroundWorkCoordinator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        _startedAtUtc = _timeProvider.GetUtcNow();
    }

    public DateTimeOffset StartupGraceEndsAtUtc => _startedAtUtc.Add(DefaultStartupGracePeriod);

    public bool IsStartupGraceActive => _timeProvider.GetUtcNow() < StartupGraceEndsAtUtc;

    public BackgroundWorkSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            var now = _timeProvider.GetUtcNow();
            var startupGraceActive = now < StartupGraceEndsAtUtc;
            var watcherDegraded = _watcherDegradedUntilUtc.HasValue && _watcherDegradedUntilUtc.Value > now;
            return new BackgroundWorkSnapshot(
                startupGraceActive,
                StartupGraceEndsAtUtc,
                watcherDegraded,
                watcherDegraded ? _watcherDegradedUntilUtc : null,
                watcherDegraded ? _watcherDegradedReason : string.Empty);
        }
    }

    public async Task WaitForStartupGraceAsync(CancellationToken cancellationToken)
    {
        var delay = StartupGraceEndsAtUtc - _timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(delay, cancellationToken);
    }

    public bool CanRunLibraryWatchers()
    {
        lock (_stateLock)
        {
            return !_watcherDegradedUntilUtc.HasValue || _watcherDegradedUntilUtc.Value <= _timeProvider.GetUtcNow();
        }
    }

    public void MarkLibraryWatchersDegraded(string reason)
    {
        lock (_stateLock)
        {
            _watcherDegradedUntilUtc = _timeProvider.GetUtcNow().Add(DefaultWatcherDegradedPeriod);
            _watcherDegradedReason = string.IsNullOrWhiteSpace(reason)
                ? "Library realtime watchers are temporarily disabled."
                : reason.Trim();
        }
    }
}

public sealed record BackgroundWorkSnapshot(
    bool StartupGraceActive,
    DateTimeOffset StartupGraceEndsAtUtc,
    bool LibraryWatchersDegraded,
    DateTimeOffset? LibraryWatchersDegradedUntilUtc,
    string LibraryWatchersDegradedReason);
