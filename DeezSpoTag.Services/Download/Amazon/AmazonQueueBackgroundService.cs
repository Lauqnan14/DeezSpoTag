using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Amazon;

public sealed class AmazonQueueBackgroundService : EngineQueueBackgroundService<AmazonEngineProcessor>
{
    public AmazonQueueBackgroundService(
        DownloadQueueRepository queueRepository,
        AmazonEngineProcessor processor,
        DeezSpoTagSettingsService settingsService,
        ILogger<AmazonQueueBackgroundService> logger)
        : base(queueRepository, processor, settingsService, logger)
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
