using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Amazon;

public sealed class AmazonQueueBackgroundService : EngineQueueBackgroundService<AmazonEngineProcessor>
{
    public AmazonQueueBackgroundService(
        DownloadQueueRepository queueRepository,
        AmazonEngineProcessor processor,
        DeezSpoTagSettingsService settingsService,
        IDownloadQueueExecutionGate executionGate,
        DownloadQueueWakeSignal queueWakeSignal,
        ILogger<AmazonQueueBackgroundService> logger)
        : base(queueRepository, processor, settingsService, executionGate, queueWakeSignal, logger)
    {
    }

    protected override string EngineKey => "amazon";

    protected override string EngineName => "Amazon";

    protected override Task ProcessQueueItemAsync(
        AmazonEngineProcessor processor,
        DownloadQueueItem next,
        CancellationToken stoppingToken)
    {
        return processor.ProcessQueueItemAsync(next, stoppingToken);
    }
}
