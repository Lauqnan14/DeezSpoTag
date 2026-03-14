using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleQueueBackgroundService : EngineQueueBackgroundService<AppleEngineProcessor>
{
    public AppleQueueBackgroundService(
        DownloadQueueRepository queueRepository,
        AppleEngineProcessor processor,
        DeezSpoTagSettingsService settingsService,
        ILogger<AppleQueueBackgroundService> logger)
        : base(queueRepository, processor, settingsService, logger)
    {
    }

    protected override string EngineKey => "apple";

    protected override string EngineName => "Apple";

    protected override Task ProcessQueueItemAsync(
        AppleEngineProcessor processor,
        DownloadQueueItem next,
        CancellationToken stoppingToken)
    {
        return processor.ProcessQueueItemAsync(next, stoppingToken);
    }
}
