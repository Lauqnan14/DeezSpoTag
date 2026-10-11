using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class AppleBrowseRefreshHostedService(
    AppleMusicCatalogService catalog,
    DeezSpoTagSettingsService settingsService,
    ILogger<AppleBrowseRefreshHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var categoriesRefreshedAt = DateTimeOffset.MinValue;
        string? categoriesStorefront = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromHours(4);
            try
            {
                if (!AppleCatalogJsonHelper.IsAppleDisabledByEnvironment())
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(60));
                    var settings = settingsService.LoadSettings();
                    var storefront = await catalog.ResolveStorefrontAsync(settings.AppleMusic?.Storefront, settings.AppleMusic?.MediaUserToken, deadline.Token);
                    using var home = await catalog.GetBrowsePageAsync("new", storefront, "en-US", deadline.Token, refresh: true);
                    if (categoriesStorefront != storefront || DateTimeOffset.UtcNow - categoriesRefreshedAt >= TimeSpan.FromHours(24))
                    {
                        using var categories = await catalog.GetBrowsePageAsync("search", storefront, "en-US", deadline.Token, refresh: true);
                        categoriesRefreshedAt = DateTimeOffset.UtcNow;
                        categoriesStorefront = storefront;
                        logger.LogInformation("Apple browse category cache refreshed for {Storefront}; next refresh in 24 hours.", storefront);
                    }
                    logger.LogInformation("Apple browse background cache refreshed for {Storefront}.", storefront);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Apple browse background refresh timed out.");
                delay = TimeSpan.FromSeconds(30);
            }
            catch (Exception exception) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(exception))
            {
                logger.LogWarning(exception, "Apple browse background cache refresh failed.");
                delay = TimeSpan.FromSeconds(30);
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
