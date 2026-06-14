namespace DeezSpoTag.Services.Download.Shared;

public sealed class DownloadQueueWakeSignal
{
    private readonly SemaphoreSlim _pendingWake = new(0, 1);

    public void Pulse()
    {
        if (_pendingWake.CurrentCount == 0)
        {
            try
            {
                _pendingWake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another producer published a coalesced wake concurrently.
            }
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _pendingWake.WaitAsync(timeout, cancellationToken);
    }
}
