namespace DeezSpoTag.Web.Services;

public sealed class WatchlistRunSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private int _pending;

    public bool IsPending => Volatile.Read(ref _pending) != 0;

    public bool Request()
    {
        var accepted = Interlocked.Exchange(ref _pending, 1) == 0;
        if (accepted)
        {
            _signal.Release();
        }

        return accepted;
    }

    public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var signalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(delay, cancellationToken);
        var signalTask = _signal.WaitAsync(signalCancellation.Token);
        var completed = await Task.WhenAny(delayTask, signalTask);
        if (completed == signalTask)
        {
            await signalTask;
            Interlocked.Exchange(ref _pending, 0);
            return;
        }

        signalCancellation.Cancel();
        try
        {
            await signalTask;
            Interlocked.Exchange(ref _pending, 0);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The poll interval won. Cancel its losing signal waiter so it cannot
            // consume a later explicit trigger.
        }
        await delayTask;
    }
}
