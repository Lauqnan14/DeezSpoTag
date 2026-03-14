using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Qobuz;

public sealed class QobuzQueueBackgroundService : EngineQueueBackgroundService<QobuzEngineProcessor>
{
    public QobuzQueueBackgroundService(
        DownloadQueueRepository queueRepository,
        QobuzEngineProcessor processor,
        DeezSpoTagSettingsService settingsService,
        ILogger<QobuzQueueBackgroundService> logger)
        : base(queueRepository, processor, settingsService, logger)
    {
    }

    protected override string EngineKey => "qobuz";

    protected override string EngineName => "Qobuz";

    protected override Task ProcessQueueItemAsync(
        QobuzEngineProcessor processor,
        DownloadQueueItem next,
        CancellationToken stoppingToken)
    {
        return processor.ProcessQueueItemAsync(next, stoppingToken);
    }
}
