using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared;

internal static class QueueItemExecutionHelper
{
    internal static async Task<bool> ExecuteAsync<T>(
        QueueItem<T> item,
        Func<T, Task> worker,
        ILogger? logger)
    {
        var succeeded = false;
        try
        {
            await worker(item.Data);
            succeeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Error processing queue item");
        }
        finally
        {
            InvokeCallback(item.Callback, logger);
        }

        return succeeded;
    }

    private static void InvokeCallback(Action? callback, ILogger? logger)
    {
        try
        {
            callback?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Error in queue item callback");
        }
    }
}
