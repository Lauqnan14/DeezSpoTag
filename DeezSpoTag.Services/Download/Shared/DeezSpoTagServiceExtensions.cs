using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Shared.Advanced;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared;

/// <summary>
/// Service registration extensions for deezspotag queue system
/// Phase 4 Implementation: Advanced features and performance optimization
/// </summary>
public static class DeezSpoTagServiceExtensions
{
    /// <summary>
    /// Add deezspotag queue services to the service collection
    /// </summary>
    public static IServiceCollection AddDeezSpoTagQueue(this IServiceCollection services)
    {
        services.AddSingleton<DownloadQueueRepository>();
        services.AddSingleton<DownloadQueueRecoveryService>();
        services.TryAddSingleton<IDownloadTagSettingsResolver, NullDownloadTagSettingsResolver>();
        services.AddSingleton<PostDownloadTaskScheduler>();
        services.AddSingleton<IPostDownloadTaskScheduler>(sp => sp.GetRequiredService<PostDownloadTaskScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<PostDownloadTaskScheduler>());

        // Settings service
        // PHASE 3: Enhanced settings service with complete deezspotag configuration
        services.AddSingleton<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>>();
            return new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(logger);
        });
        services.AddSingleton<ISettingsService>(provider => provider.GetRequiredService<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>());
        
        // Concurrency limit service for account-based download limits
        services.AddSingleton<ConcurrencyLimitService>();
        
        // Core deezspotag services - singleton to keep a single shared queue processor
        services.AddSingleton<IDeezSpoTagAppFactory, DeezSpoTagAppFactory>();
        services.AddSingleton<DeezSpoTagApp>(provider =>
        {
            var factory = provider.GetRequiredService<IDeezSpoTagAppFactory>();
            return factory.CreateDeezSpoTagApp(provider);
        });
        
        // Bitrate selection service (moved to Download.Utils)
        services.AddScoped<DeezSpoTag.Services.Download.Utils.BitrateSelector>();
        services.AddSingleton<EngineProcessorCommonDependencies>();
        
        services.AddScoped<QualityFallbackManager>();
        services.AddScoped<CommandExecutionService>();
        services.AddScoped<DeezSpoTagLoggingService>();
        
        // Phase 4: Advanced features and performance optimization
        // PHASE 4: Advanced features and performance optimization - COMPLETED
        services.AddSingleton<DeezSpoTag.Services.Download.Shared.Advanced.AdvancedConcurrencyManager>();
        services.AddSingleton<DeezSpoTag.Services.Download.Shared.Performance.PerformanceOptimizationService>();
        services.AddScoped<DeezSpoTag.Services.Download.Shared.Validation.DeezSpoTagIntegrationValidator>();
        services.AddScoped<EnhancedQueuePersistenceService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<EnhancedQueuePersistenceService>>();
            var configFolder = Path.Join(
                AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir()),
                "deezspotag");
            return new EnhancedQueuePersistenceService(logger, configFolder);
        });
        // Async queue processor factory
        services.AddTransient(typeof(AsyncQueueProcessor<>));
        
        // CRITICAL FIX: Use basic listener here - SignalR listener will be registered in Web project
        // NOTE: IDeezSpoTagListener is registered in the Web project as SignalRDeezSpoTagListener
        
        // CRITICAL FIX: Add the actual download engine
        // DeezSpoTagDownloader is created per-download, not registered as service
        
        // Unified queue processor handles all engines.
        services.AddScoped<DeezSpoTag.Services.Download.Deezer.DeezerEngineProcessor>();
        services.AddScoped<IQueueEngineProcessor, DeezSpoTag.Services.Download.Deezer.DeezerEngineProcessor>();
        services.AddScoped<IQueueEngineProcessor, DeezSpoTag.Services.Download.Apple.AppleEngineProcessor>();
        services.AddScoped<IQueueEngineProcessor, DeezSpoTag.Services.Download.Qobuz.QobuzEngineProcessor>();
        services.AddScoped<IQueueEngineProcessor, DeezSpoTag.Services.Download.Tidal.TidalEngineProcessor>();
        services.AddScoped<IQueueEngineProcessor, DeezSpoTag.Services.Download.Amazon.AmazonEngineProcessor>();
        
        return services;
    }
}


/// <summary>
/// Background service for deezspotag queue processing
/// </summary>
public class DeezSpoTagQueueBackgroundService : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly ILogger<DeezSpoTagQueueBackgroundService> _logger;
    private readonly DeezSpoTagApp _deezSpoTagApp;
    private readonly DownloadQueueRecoveryService _recoveryService;

    public DeezSpoTagQueueBackgroundService(
        DeezSpoTagApp deezSpoTagApp,
        DownloadQueueRecoveryService recoveryService,
        ILogger<DeezSpoTagQueueBackgroundService> logger)
    {
        _deezSpoTagApp = deezSpoTagApp;
        _recoveryService = recoveryService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return QueueProcessingLoop.RunAsync(
            "DeezSpoTag",
            async token =>
            {
                await _recoveryService.RecoverStaleRunningTasksAsync(token);

                var queuedCount = await _deezSpoTagApp.GetQueuedCountAsync();
                if (queuedCount > 0)
                {
                    _logger.LogDebug("Background service checking queue - {QueueCount} items pending", queuedCount);
                    try
                    {
                        await _deezSpoTagApp.EnsureQueueProcessorRunningAsync();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Error starting queue processor from background service");
                    }
                }
            },
            _logger,
            TimeSpan.FromSeconds(10),
            stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Skipping queue clear on shutdown; queue persists until user clears it");
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// Factory interface for creating DeezSpoTagApp instances
/// </summary>
public interface IDeezSpoTagAppFactory
{
    DeezSpoTagApp CreateDeezSpoTagApp(IServiceProvider serviceProvider);
}

/// <summary>
/// Factory implementation for creating DeezSpoTagApp instances with proper service provider handling
/// </summary>
public class DeezSpoTagAppFactory : IDeezSpoTagAppFactory
{
    private readonly IServiceProvider _rootServiceProvider;

    public DeezSpoTagAppFactory(IServiceProvider rootServiceProvider)
    {
        _rootServiceProvider = rootServiceProvider;
    }

    public DeezSpoTagApp CreateDeezSpoTagApp(IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<DeezSpoTagApp>>();
        var settingsService = serviceProvider.GetRequiredService<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>();
        var listener = serviceProvider.GetRequiredService<IDeezSpoTagListener>();
        var retryScheduler = serviceProvider.GetRequiredService<Queue.DownloadRetryScheduler>();
        var queueRepository = serviceProvider.GetRequiredService<DownloadQueueRepository>();
        var cancellationRegistry = serviceProvider.GetRequiredService<DownloadCancellationRegistry>();
        
        // Use the root service provider to avoid disposal issues when creating new scopes
        return new DeezSpoTagApp(logger, settingsService, listener, retryScheduler, queueRepository, cancellationRegistry, _rootServiceProvider);
    }
}
