using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services.ArtistLocation;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryArtistMetadataServices(
    SpotifyArtistService spotifyArtistService,
    ArtistPageCacheRepository artistPageCache,
    SpotifyMetadataCacheRepository spotifyMetadataCache,
    LastFmArtistImageService lastFmArtistImageService,
    ArtistVisualSelectionService artistVisualSelectionService,
    IWebHostEnvironment environment,
    Audiomack.AudiomackArtistLocationService audiomackArtistLocation,
    ArtistLocationOverrideStore locationOverrides,
    ArtistLocationResolver locationResolver)
{
    public SpotifyArtistService SpotifyArtistService { get; } = spotifyArtistService;
    public ArtistPageCacheRepository ArtistPageCache { get; } = artistPageCache;
    public SpotifyMetadataCacheRepository SpotifyMetadataCache { get; } = spotifyMetadataCache;
    public LastFmArtistImageService LastFmArtistImageService { get; } = lastFmArtistImageService;
    public ArtistVisualSelectionService ArtistVisualSelectionService { get; } = artistVisualSelectionService;
    public IWebHostEnvironment Environment { get; } = environment;
    public Audiomack.AudiomackArtistLocationService AudiomackArtistLocation { get; } = audiomackArtistLocation;

    /// <summary>
    /// The single place the location precedence is decided. Held here rather than
    /// injected into each controller so every caller shares one decision point.
    /// </summary>
    public ArtistLocationResolver LocationResolver { get; } = locationResolver;

    public ArtistLocationOverrideStore LocationOverrides { get; } = locationOverrides;
}
