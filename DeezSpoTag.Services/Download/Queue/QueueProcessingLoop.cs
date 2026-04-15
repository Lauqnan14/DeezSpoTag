using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Queue;

public static class QueueProcessingLoop
{
    public static async Task RunAsync(
        string name,
        Func<CancellationToken, Task> work,
        ILogger logger,
        TimeSpan idleDelay,
        CancellationToken stoppingToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("{QueueName} queue background service started", name);        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await work(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error in {QueueName} queue background service", name);
                }

                await Task.Delay(idleDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException ex)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(ex, "{QueueName} queue background service cancelled", name);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error in {QueueName} queue background service", name);
        }
        finally
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("{QueueName} queue background service stopped", name);            }
        }
    }
}
