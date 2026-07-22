using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryArtistImageQueueDependencies(
    LibraryRepository repository,
    LibraryConfigStore configStore,
    ArtistArtworkCatalogService artworkCatalog,
    ArtistMetadataCacheRefreshService cacheRefreshService,
    DeezSpoTagSettingsService settingsService,
    IWebHostEnvironment environment)
{
    public LibraryRepository Repository { get; } = repository;
    public LibraryConfigStore ConfigStore { get; } = configStore;
    public ArtistArtworkCatalogService ArtworkCatalog { get; } = artworkCatalog;
    public ArtistMetadataCacheRefreshService CacheRefreshService { get; } = cacheRefreshService;
    public DeezSpoTagSettingsService SettingsService { get; } = settingsService;
    public IWebHostEnvironment Environment { get; } = environment;
}
