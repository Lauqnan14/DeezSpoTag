using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryArtistMetadataServices(
    SpotifyArtistService spotifyArtistService,
    ArtistPageCacheRepository artistPageCache,
    SpotifyMetadataCacheRepository spotifyMetadataCache,
    LastFmArtistImageService lastFmArtistImageService,
    ArtistExternalMetadataBackfillService artistExternalMetadataBackfillService,
    ArtistVisualSelectionService artistVisualSelectionService,
    ArtistVisualCacheService artistVisualCacheService,
    IWebHostEnvironment environment)
{
    public SpotifyArtistService SpotifyArtistService { get; } = spotifyArtistService;
    public ArtistPageCacheRepository ArtistPageCache { get; } = artistPageCache;
    public SpotifyMetadataCacheRepository SpotifyMetadataCache { get; } = spotifyMetadataCache;
    public LastFmArtistImageService LastFmArtistImageService { get; } = lastFmArtistImageService;
    public ArtistExternalMetadataBackfillService ArtistExternalMetadataBackfillService { get; } = artistExternalMetadataBackfillService;
    public ArtistVisualSelectionService ArtistVisualSelectionService { get; } = artistVisualSelectionService;
    public ArtistVisualCacheService ArtistVisualCacheService { get; } = artistVisualCacheService;
    public IWebHostEnvironment Environment { get; } = environment;
}
