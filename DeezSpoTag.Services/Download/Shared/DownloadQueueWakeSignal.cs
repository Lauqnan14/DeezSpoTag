namespace DeezSpoTag.Services.Download.Shared;

public sealed class DownloadQueueWakeSignal
{
    private readonly object _lock = new();
    private TaskCompletionSource _nextWake = CreateWakeSource();

    public void Pulse()
    {
        TaskCompletionSource wake;
        lock (_lock)
        {
            wake = _nextWake;
            _nextWake = CreateWakeSource();
        }

        wake.TrySetResult();
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (_lock)
        {
            waitTask = _nextWake.Task;
        }

        return WaitCoreAsync(waitTask, timeout, cancellationToken);
    }

    private static async Task WaitCoreAsync(Task waitTask, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var delayTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(waitTask, delayTask);
        if (completed == delayTask)
        {
            await delayTask;
        }
    }

    private static TaskCompletionSource CreateWakeSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
