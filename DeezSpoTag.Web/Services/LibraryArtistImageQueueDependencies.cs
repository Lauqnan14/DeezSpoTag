using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistImageProviderServices(
    AppleMusicCatalogService appleCatalogService,
    ISpotifyArtworkResolver spotifyArtworkResolver,
    ILastFmArtistImageResolver lastFmArtistImageResolver,
    DeezerClient deezerClient,
    IHttpClientFactory httpClientFactory)
{
    public AppleMusicCatalogService AppleCatalogService { get; } = appleCatalogService;
    public ISpotifyArtworkResolver SpotifyArtworkResolver { get; } = spotifyArtworkResolver;
    public ILastFmArtistImageResolver LastFmArtistImageResolver { get; } = lastFmArtistImageResolver;
    public DeezerClient DeezerClient { get; } = deezerClient;
    public IHttpClientFactory HttpClientFactory { get; } = httpClientFactory;
}

public sealed class LibraryArtistImageQueueDependencies(
    LibraryRepository repository,
    LibraryConfigStore configStore,
    ArtistImageProviderServices providers,
    DeezSpoTagSettingsService settingsService,
    IWebHostEnvironment environment)
{
    public LibraryRepository Repository { get; } = repository;
    public LibraryConfigStore ConfigStore { get; } = configStore;
    public ArtistImageProviderServices Providers { get; } = providers;
    public DeezSpoTagSettingsService SettingsService { get; } = settingsService;
    public IWebHostEnvironment Environment { get; } = environment;
}
