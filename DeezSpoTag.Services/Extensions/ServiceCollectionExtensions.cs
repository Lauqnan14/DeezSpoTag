using DeezSpoTag.Services.Crypto;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Matching;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Net.Security;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Extensions;

/// <summary>
/// Service collection extensions for DeezSpoTag services
/// </summary>
public static class ServiceCollectionExtensions
{
    private const string UserAgentHeader = "User-Agent";
    private const string LinuxChrome79UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36";
    private const string LinuxChrome96UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/96.0.4664.110 Safari/537.36";
    private const string WindowsChrome91UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36";

    /// <summary>
    /// Add DeezSpoTag services to the service collection
    /// </summary>
    public static IServiceCollection AddDeezSpoTagServices(this IServiceCollection services)
    {
        // Settings services
        // DeezSpoTagSettingsService is registered as Singleton in DownloadServiceExtensions
        // ISettingsService interface will resolve to the singleton instance

        // Crypto services
        services.AddScoped<CryptoService>();

        services.AddScoped<DecryptionStreamProcessor>();

        // Deezer download engine removed.

        // Download utilities
        // BitrateSelector is registered in DownloadServiceExtensions
        // AudioTagger is registered in DownloadServiceExtensions

        // Image download services - configured with proper HttpClient (merged service)
        services.AddScoped<ImageDownloader>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<ImageDownloader>>();
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            return new ImageDownloader(logger, httpClientFactory);
        });

        // Lyrics services
        services.AddSingleton<JwtTokenService>();
        services.AddSingleton<LyricsService>();
        services.AddSingleton<LrclibLyricsService>();
        services.AddSingleton<AppleLyricsService>();

        services.AddSingleton<ArtistPageCacheRepository>();
        services.AddSingleton<SpotifyMetadataCacheRepository>();
        services.AddScoped<TrackMatchService>();

        // Track services (TrackServices.TrackEnrichmentService removed - was duplicate)
        // TrackEnrichmentService is registered in DownloadServiceExtensions

        // EXACT deezspotag implementation: got.stream with https: { rejectUnauthorized: false }
        AddInsecureTlsClient(services, "DeezerDownload", TimeSpan.FromMinutes(10), LinuxChrome79UserAgent);

        // EXACT deezspotag implementation: got.stream with https: { rejectUnauthorized: false }
        AddInsecureTlsClient(services, "TrackDownload", TimeSpan.FromMinutes(15), LinuxChrome79UserAgent);

        services.AddHttpClient("BitrateSelector", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add(UserAgentHeader, WindowsChrome91UserAgent);
        }).ConfigurePrimaryHttpMessageHandler(CreateRedirectingHandler);

        // EXACT deezspotag implementation: got.stream with https: { rejectUnauthorized: false }
        // Don't set User-Agent for ImageDownload here - ImageDownloader sets it per request.
        AddInsecureTlsClient(services, "ImageDownload", TimeSpan.FromSeconds(30));
        AddInsecureTlsClient(services, "JwtTokenService", TimeSpan.FromSeconds(30), LinuxChrome96UserAgent);
        AddInsecureTlsClient(services, "LyricsService", TimeSpan.FromSeconds(30), LinuxChrome96UserAgent);

        // EXACT deezspotag implementation: DeezerClient with SSL bypass for media URLs
        services.AddHttpClient("DeezerClient", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.Add(UserAgentHeader, LinuxChrome79UserAgent);
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            client.DefaultRequestHeaders.Add("Accept", "*/*");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        }).ConfigurePrimaryHttpMessageHandler(CreateRedirectingHandler);

        services.AddHttpClient<DeezerAuthUtils>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add(UserAgentHeader, LinuxChrome79UserAgent);
        });

        return services;
    }

    private static void AddInsecureTlsClient(
        IServiceCollection services,
        string name,
        TimeSpan timeout,
        string? userAgent = null)
    {
        services
            .AddHttpClient(name, client =>
            {
                client.Timeout = timeout;
                if (!string.IsNullOrWhiteSpace(userAgent))
                {
                    client.DefaultRequestHeaders.Add(UserAgentHeader, userAgent);
                }
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                var allowInsecureTls = TlsPolicy.AllowInsecure(configuration);
                return DeezSpoTag.Services.Crypto.DeezSpoTagHttpClientHandler.Create(allowInsecureTls);
            });
    }

    private static HttpClientHandler CreateRedirectingHandler(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseProxy = false,
            PreAuthenticate = false,
            UseDefaultCredentials = false,
            SslProtocols = System.Security.Authentication.SslProtocols.None,
            AutomaticDecompression = System.Net.DecompressionMethods.None
        };
        TlsPolicy.ApplyIfAllowed(handler, configuration);
        return handler;
    }
}
