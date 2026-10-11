using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Integrations.YouTube;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Caching.Memory;

namespace DeezSpoTag.Web.Services;

public sealed class YouTubeBrowseRefreshHostedService(IServiceScopeFactory scopes, ILogger<YouTubeBrowseRefreshHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var categoriesAt = DateTimeOffset.MinValue;
        string? previousMarket = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromHours(4);
            try
            {
                using var scope = scopes.CreateScope();
                var settingsService = scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
                var settings = settingsService.LoadSettings();
                var market = $"{settings.YouTubeMusicCountry}:{settings.DeezerCountry}:{settings.DeezerLanguage}";
                var controller = new YouTubeBrowseApiController(scope.ServiceProvider.GetRequiredService<YouTubeMusicSearchClient>(), settingsService,
                    scope.ServiceProvider.GetRequiredService<IMemoryCache>(), scope.ServiceProvider.GetRequiredService<ILogger<YouTubeBrowseApiController>>(), scope.ServiceProvider.GetService<IWebHostEnvironment>());
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(90));
                await controller.LoadAsync("sections", null, deadline.Token, refresh: true);
                if (market != previousMarket || DateTimeOffset.UtcNow - categoriesAt >= TimeSpan.FromHours(24))
                {
                    await controller.LoadAsync("categories", null, deadline.Token, refresh: true);
                    categoriesAt = DateTimeOffset.UtcNow;
                    previousMarket = market;
                }
                logger.LogInformation("YouTube Music browse background cache refreshed.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(exception) || exception is OperationCanceledException)
            {
                logger.LogWarning(exception, "YouTube Music browse background refresh failed.");
                delay = TimeSpan.FromMinutes(1);
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
