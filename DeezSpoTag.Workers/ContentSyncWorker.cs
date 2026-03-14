using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.PlaylistSync;

namespace DeezSpoTag.Workers;

/// <summary>
/// Periodic sync between platforms (Spotify, Plex, etc.)
/// Handles automatic synchronization of playlists and libraries
/// </summary>
public class ContentSyncWorker : BackgroundService
{
    private readonly ILogger<ContentSyncWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _syncInterval = TimeSpan.FromHours(6); // Sync every 6 hours

    public ContentSyncWorker(
        ILogger<ContentSyncWorker> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Content sync worker started - sync interval: {SyncInterval}", _syncInterval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PerformSyncOperationsAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error during content sync operations");
                }

                // Wait for next sync interval
                try
                {
                    await Task.Delay(_syncInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "Content sync worker cancelled");
        }
        finally
        {
            _logger.LogInformation("Content sync worker stopped");
        }
    }

    private async Task PerformSyncOperationsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting content sync operations");

        using var scope = _serviceProvider.CreateScope();
        
        try
        {
            // Get sync orchestrator service
            var syncOrchestrator = scope.ServiceProvider.GetService<SyncOrchestrator>();
            if (syncOrchestrator != null)
            {
                await syncOrchestrator.PerformScheduledSyncAsync(cancellationToken);
            }
            else
            {
                _logger.LogDebug("SyncOrchestrator service not available - sync operations skipped");
            }

            // Get Plex sync service
            var plexSyncService = scope.ServiceProvider.GetService<PlexSyncService>();
            if (plexSyncService != null)
            {
                await plexSyncService.SyncLibraryAsync(cancellationToken);
            }
            else
            {
                _logger.LogDebug("PlexSyncService not available - Plex sync skipped");
            }

            _logger.LogInformation("Content sync operations completed successfully");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during content sync operations");
        }
    }
}
