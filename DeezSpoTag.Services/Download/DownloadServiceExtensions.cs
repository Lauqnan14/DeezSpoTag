using DeezSpoTag.Services.Crypto;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Conversion;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Metadata;
using DeezSpoTag.Services.Metadata.Qobuz;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download;

/// <summary>
/// Service registration extensions for download engine
/// </summary>
public static class DownloadServiceExtensions
{
    private const string DesktopChromeUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36";
    private static readonly TimeSpan LongDownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Register all download engine services
    /// </summary>
    public static IServiceCollection AddDownloadEngine(this IServiceCollection services)
    {
        services.AddDeezSpoTagQueue();
        services.AddMemoryCache();
        services.AddSingleton<LibraryRepository>();
        services.AddScoped<IMetadataResolverRegistry, MetadataResolverRegistry>();
        services.AddSingleton<Download.Queue.DownloadQueueRepository>();
        services.AddSingleton<DeezSpoTagSettingsService>();
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<DeezSpoTagSettingsService>());
        services.AddSingleton<IDeezSpoTagListener, DeezSpoTagListener>();
        services.AddSingleton<PlatformCapabilitiesStore>();

        // Core download services (Deezer engine removed)
        services.AddSingleton<Queue.DownloadCancellationRegistry>();
        services.AddSingleton<IActivityLogWriter, NullActivityLogWriter>();
        services.TryAddSingleton<Queue.DownloadRetryScheduler>(sp =>
            new Queue.DownloadRetryScheduler(
                sp.GetRequiredService<Queue.DownloadQueueRepository>(),
                sp.GetRequiredService<DeezSpoTagSettingsService>(),
                sp.GetRequiredService<IActivityLogWriter>(),
                sp.GetRequiredService<IDeezSpoTagListener>(),
                sp.GetRequiredService<ILogger<Queue.DownloadRetryScheduler>>(),
                sp.GetRequiredService<Queue.DownloadCancellationRegistry>()));
        services.AddSingleton<ISpotifyIdResolver, SpotifyIdResolver>();
        // Keep null resolver as a fallback only. Web host may register a real resolver earlier.
        services.TryAddSingleton<ISpotifyArtworkResolver, NullSpotifyArtworkResolver>();
        services.AddSingleton<AppleMusicCatalogService>();
        services.AddSingleton<Download.Apple.IAppleWrapperStatusProvider, Download.Apple.NullAppleWrapperStatusProvider>();
        services.AddSingleton<DeezerPipeService>();

        // Utility services
        services.AddScoped<EnhancedPathTemplateProcessor>();
        services.AddScoped<TrackDownloader>();
        services.AddScoped<BitrateSelector>();
        services.AddScoped<ImageDownloader>();
        services.AddScoped<AudioTagger>();
        services.AddScoped<FfmpegConversionService>();
        services.AddSingleton<IFolderConversionSettingsOverlay, FolderConversionSettingsOverlay>();
        services.AddScoped<DeezSpoTag.Services.Download.Objects.DownloadObjectGenerator>();
        services.AddSingleton<DownloadMoveService>();
        services.AddScoped<Download.Utils.TrackEnrichmentService>();
        services.AddScoped<SearchFallbackService>();
        services.AddSingleton<SongLinkPersistentCacheStore>();
        services.AddSingleton<SongLinkResolver>();
        services.AddSingleton<EngineFallbackCoordinator>();
        services.AddSingleton<DeezerIsrcResolver>();
        services.AddSingleton<Download.Tidal.TidalApiProviderSource>();

        services.AddOptions<QobuzApiConfig>();
        services.AddHttpClient<IQobuzApiClient, QobuzApiClient>();
        services.AddSingleton<QobuzArtistService>();
        services.AddSingleton<IQobuzMetadataService, QobuzMetadataService>();
        services.AddSingleton<QobuzTrackResolver>();

        // Crypto services
        services.AddScoped<CryptoService>();
        // Note: DecryptionStreamProcessor is registered in ServiceCollectionExtensions with HttpClient

        // Settings service is registered in DeezSpoTagServiceExtensions

        // EXACT deezspotag implementation: got with https: { rejectUnauthorized: false }
        services.AddHttpClient<DeezSpoTag.Services.Downloader.DeezerDownloadService>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(DesktopChromeUserAgent);
            client.Timeout = LongDownloadTimeout;
        }).ConfigurePrimaryHttpMessageHandler(CreatePermissiveHandler);

        services.AddHttpClient("DeezSpoTagDownload", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(DesktopChromeUserAgent);
            client.Timeout = LongDownloadTimeout; // Long timeout for large downloads
        }).ConfigurePrimaryHttpMessageHandler(CreatePermissiveHandler);

        services.AddHttpClient("ImageDownload", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(DesktopChromeUserAgent);
            client.Timeout = TimeSpan.FromMinutes(5);
        }).ConfigurePrimaryHttpMessageHandler(CreatePermissiveHandler);

        services.AddHttpClient("SongLink", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient("TidalProviderList", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(DesktopChromeUserAgent);
        });

        services.AddHttpClient("SpotifyPublic", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        });

        return services;
    }

    private static HttpClientHandler CreatePermissiveHandler(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            SslProtocols = System.Security.Authentication.SslProtocols.None
        };
        TlsPolicy.ApplyIfAllowed(handler, configuration);
        return handler;
    }
}
