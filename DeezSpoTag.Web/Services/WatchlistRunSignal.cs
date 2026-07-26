namespace DeezSpoTag.Web.Services;

[Flags]
public enum WatchlistWakeReason
{
    None = 0,
    ScheduledRefresh = 1,
    Reconciliation = 2,
    Finalization = 4,
    TargetSync = 8
}

public sealed class WatchlistRunSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private int _pendingReasons;

    public bool IsPending => Volatile.Read(ref _pendingReasons) != 0;

    public bool Request(WatchlistWakeReason reason = WatchlistWakeReason.Reconciliation)
    {
        if (reason == WatchlistWakeReason.None)
        {
            return false;
        }

        while (true)
        {
            var observed = Volatile.Read(ref _pendingReasons);
            var updated = observed | (int)reason;
            if (Interlocked.CompareExchange(ref _pendingReasons, updated, observed) != observed)
            {
                continue;
            }

            if (observed == 0)
            {
                _signal.Release();
                return true;
            }

            return false;
        }
    }

    public async Task<WatchlistWakeReason> WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var signalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(delay, cancellationToken);
        var signalTask = _signal.WaitAsync(signalCancellation.Token);
        var completed = await Task.WhenAny(delayTask, signalTask);
        if (completed == signalTask)
        {
            await signalTask;
            return (WatchlistWakeReason)Interlocked.Exchange(ref _pendingReasons, 0);
        }

        signalCancellation.Cancel();
        try
        {
            await signalTask;
            return (WatchlistWakeReason)Interlocked.Exchange(ref _pendingReasons, 0);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The poll interval won. Cancel its losing signal waiter so it cannot
            // consume a later explicit trigger.
        }
        await delayTask;
        return WatchlistWakeReason.ScheduledRefresh;
    }
}
