using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Tidal;

public sealed class TidalQueueBackgroundService : EngineQueueBackgroundService<TidalEngineProcessor>
{
    public TidalQueueBackgroundService(
        DownloadQueueRepository queueRepository,
        TidalEngineProcessor processor,
        DeezSpoTagSettingsService settingsService,
        ILogger<TidalQueueBackgroundService> logger)
        : base(queueRepository, processor, settingsService, logger)
    {
    }

    protected override string EngineKey => "tidal";

    protected override string EngineName => "Tidal";

    protected override Task ProcessQueueItemAsync(
        TidalEngineProcessor processor,
        DownloadQueueItem next,
        CancellationToken stoppingToken)
    {
        return processor.ProcessQueueItemAsync(next, stoppingToken);
    }
}
