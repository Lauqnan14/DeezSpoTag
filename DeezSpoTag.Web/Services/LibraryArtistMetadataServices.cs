using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryArtistMetadataServices(
    SpotifyArtistService spotifyArtistService,
    ArtistPageCacheRepository artistPageCache,
    SpotifyMetadataCacheRepository spotifyMetadataCache,
    LastFmArtistImageService lastFmArtistImageService,
    ArtistVisualSelectionService artistVisualSelectionService,
    IWebHostEnvironment environment,
    Audiomack.AudiomackArtistLocationService audiomackArtistLocation,
    ArtistLocationOverrideStore locationOverrides)
{
    public SpotifyArtistService SpotifyArtistService { get; } = spotifyArtistService;
    public ArtistPageCacheRepository ArtistPageCache { get; } = artistPageCache;
    public SpotifyMetadataCacheRepository SpotifyMetadataCache { get; } = spotifyMetadataCache;
    public LastFmArtistImageService LastFmArtistImageService { get; } = lastFmArtistImageService;
    public ArtistVisualSelectionService ArtistVisualSelectionService { get; } = artistVisualSelectionService;
    public IWebHostEnvironment Environment { get; } = environment;
    public Audiomack.AudiomackArtistLocationService AudiomackArtistLocation { get; } = audiomackArtistLocation;
    public ArtistLocationOverrideStore LocationOverrides { get; } = locationOverrides;
}
