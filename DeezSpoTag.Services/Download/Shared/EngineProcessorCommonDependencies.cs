using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared;

public sealed class EngineProcessorCommonDependencies
{
    private readonly IServiceProvider _serviceProvider;

    public EngineProcessorCommonDependencies(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    internal EngineQueueProcessorHelper.ProcessorDeps CreateProcessorDeps(ILogger logger)
        => new(
            _serviceProvider.GetRequiredService<DownloadQueueRepository>(),
            _serviceProvider.GetRequiredService<DownloadCancellationRegistry>(),
            _serviceProvider.GetRequiredService<DeezSpoTagSettingsService>(),
            _serviceProvider.GetRequiredService<IDeezSpoTagListener>(),
            _serviceProvider.GetRequiredService<DownloadRetryScheduler>(),
            _serviceProvider,
            _serviceProvider.GetRequiredService<EngineFallbackCoordinator>(),
            _serviceProvider.GetRequiredService<IActivityLogWriter>(),
            _serviceProvider.GetRequiredService<IDownloadTagSettingsResolver>(),
            _serviceProvider.GetRequiredService<IFolderConversionSettingsOverlay>(),
            logger);
}
