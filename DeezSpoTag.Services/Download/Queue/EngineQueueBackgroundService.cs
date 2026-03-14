using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Queue;

public abstract class EngineQueueBackgroundService<TProcessor> : BackgroundService
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);

    private readonly DownloadQueueRepository _queueRepository;
    private readonly TProcessor _processor;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger _logger;

    protected EngineQueueBackgroundService(
        DownloadQueueRepository queueRepository,
        TProcessor processor,
        DeezSpoTagSettingsService settingsService,
        ILogger logger)
    {
        _queueRepository = queueRepository;
        _processor = processor;
        _settingsService = settingsService;
        _logger = logger;
    }

    protected abstract string EngineKey { get; }

    protected abstract string EngineName { get; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return QueueProcessingLoop.RunAsync(
            EngineName,
            ProcessNextAsync,
            _logger,
            _pollInterval,
            stoppingToken);
    }

    private async Task ProcessNextAsync(CancellationToken stoppingToken)
    {
        var settings = _settingsService.LoadSettings();
        var newestFirst = string.Equals(settings.QueueOrder, "recent", StringComparison.OrdinalIgnoreCase);
        var next = await _queueRepository.DequeueNextAsync(EngineKey, newestFirst, stoppingToken);
        if (next == null)
        {
            return;
        }

        await ProcessQueueItemAsync(_processor, next, stoppingToken);
    }

    protected abstract Task ProcessQueueItemAsync(
        TProcessor processor,
        DownloadQueueItem next,
        CancellationToken stoppingToken);
}
