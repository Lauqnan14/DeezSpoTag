using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Tidal;

public sealed class TidalQueueBackgroundService : EngineQueueBackgroundService<TidalEngineProcessor>
{
    public TidalQueueBackgroundService(
        DownloadQueueRepository queueRepository,
        TidalEngineProcessor processor,
        DeezSpoTagSettingsService settingsService,
        IDownloadQueueExecutionGate executionGate,
        DownloadQueueWakeSignal queueWakeSignal,
        ILogger<TidalQueueBackgroundService> logger)
        : base(queueRepository, processor, settingsService, executionGate, queueWakeSignal, logger)
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
