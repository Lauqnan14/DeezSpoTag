using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services.Audiomack;

namespace DeezSpoTag.Web.Services.ArtistLocation;

/// <summary>
/// Decides where an artist's location comes from.
/// </summary>
/// <remarks>
/// <para>
/// One place holds the precedence, so the artist page and the standalone location
/// route can never disagree, and a new source only has to be added here.
/// </para>
///
/// <para>The order is:</para>
/// <list type="number">
/// <item>a manual override the user typed on the artist page;</item>
/// <item>MusicBrainz, which identifies an artist by MBID and is authoritative
/// where it has an answer;</item>
/// <item>Audiomack, the fallback, which covers the many artists MusicBrainz has
/// no geography for.</item>
/// </list>
///
/// <para>
/// A source returning null means "I do not know" and the next one is tried. It
/// never means "there is no location", which is why no source is allowed to
/// return an empty result in place of a null one.
/// </para>
/// </remarks>
public sealed class ArtistLocationResolver
{
    /// <summary>
    /// Reported as the source when the value came from the user's own edit. The
    /// artist page keys its "is this editable" behaviour off this exact string,
    /// so it must not change.
    /// </summary>
    public const string ManualSourceName = "manual";

    private readonly ArtistLocationOverrideStore? _overrides;
    private readonly IReadOnlyList<IArtistLocationSource> _sources;
    private readonly ILogger<ArtistLocationResolver> _logger;

    /// <summary>
    /// Creates a resolver over the default source order: MusicBrainz first, with
    /// Audiomack as the fallback.
    /// </summary>
    /// <param name="musicBrainz">The default source.</param>
    /// <param name="audiomack">The fallback.</param>
    public ArtistLocationResolver(
        ILogger<ArtistLocationResolver> logger,
        IArtistLocationSource? musicBrainz = null,
        IArtistLocationSource? audiomack = null,
        ArtistLocationOverrideStore? overrides = null)
    {
        _logger = logger;
        _sources = new[] { musicBrainz, audiomack }.OfType<IArtistLocationSource>().ToArray();
        _overrides = overrides;
    }

    public ArtistLocationResolver(ILogger<ArtistLocationResolver> logger,
        IReadOnlyList<IArtistLocationSource> sources, ArtistLocationOverrideStore? overrides = null)
    {
        _logger = logger;
        _sources = sources.ToArray();
        _overrides = overrides;
    }

    /// <summary>
    /// Resolves the location for an artist, or null when no source knows one.
    /// </summary>
    public void InvalidateMusicBrainzArtist(long artistId)
    {
        foreach (var source in _sources.OfType<MusicBrainzArtistLocationService>())
            source.InvalidateArtist(artistId);
    }

    public async Task<ArtistLocationResult?> ResolveAsync(
        long artistId,
        string? artistName,
        CancellationToken cancellationToken = default)
    {
        var manual = await ResolveManualAsync(artistId, cancellationToken).ConfigureAwait(false);
        if (manual is not null) return Bind(manual);
        foreach (var source in _sources)
        {
            var result = await SafeResolveAsync(source.SourceName,
                () => source.ResolveAsync(artistId, artistName, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (result is not null) return Bind(result);
        }
        return null;

        ArtistLocationResult Bind(ArtistLocationResult result) => result with
        {
            ArtistId = artistId > 0 ? artistId : null,
            ArtistName = artistName
        };
    }

    public async Task<ArtistLocationResult?> ResolveMatchedArtistAsync(string provider, string providerArtistId,
        string artistName, string? trackUrl, CancellationToken cancellationToken)
    {
        foreach (var source in _sources)
        {
            if (provider == "musicbrainz" && source is MusicBrainzArtistLocationService musicBrainz)
                return await SafeResolveAsync(provider, () => musicBrainz.ResolveMatchedArtistAsync(providerArtistId, artistName, cancellationToken), cancellationToken);
            if (provider == "audiomack" && source is AudiomackSource audiomack && trackUrl is not null)
                return await SafeResolveAsync(provider, () => audiomack.ResolveMatchedArtistAsync(providerArtistId, artistName, trackUrl, cancellationToken), cancellationToken);
        }
        return null;
    }

    private async Task<ArtistLocationResult?> ResolveManualAsync(long artistId, CancellationToken cancellationToken)
    {
        if (_overrides is null)
        {
            return null;
        }

        try
        {
            var existing = await _overrides.GetAsync(artistId, cancellationToken).ConfigureAwait(false);

            // An override that clears both fields is a deletion, not an override:
            // it must fall through rather than blank the page.
            if (existing is null
                || (string.IsNullOrWhiteSpace(existing.City) && string.IsNullOrWhiteSpace(existing.Country)))
            {
                return null;
            }

            return new ArtistLocationResult(
                null,
                existing.City,
                existing.Country,
                existing.CountryCode,
                ManualSourceName)
            {
                SourceReference = $"artist-location-override:{artistId}",
                KnownAt = existing.UpdatedAt,
                ResolutionMethod = "manual-override",
                LocationMeaning = "user-specified-artist-location"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Manual artist location lookup failed for artist {ArtistId}.", artistId);
            return null;
        }
    }

    /// <summary>
    /// Runs one source, containing its failures.
    /// </summary>
    /// <remarks>
    /// A source that throws must not take the page down, and must not stop the
    /// next source from being tried. A location is decoration on a page, so the
    /// worst outcome is a missing one.
    /// </remarks>
    private async Task<ArtistLocationResult?> SafeResolveAsync(
        string sourceName,
        Func<Task<ArtistLocationResult?>> resolve,
        CancellationToken cancellationToken)
    {
        try
        {
            return await resolve().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist location source {Source} failed; trying the next one.", sourceName);
            return null;
        }
    }

    /// <summary>
    /// Adapts the Audiomack service to the shared source interface.
    /// </summary>
    /// <remarks>
    /// Audiomack's own types keep their name and shape, so its cache entries and
    /// its existing tests stay valid. The adaptation happens here, at the single
    /// boundary, instead of by renaming a type that forty tests reference.
    /// </remarks>
    private sealed class AudiomackSource : IArtistLocationSource
    {
        private readonly AudiomackArtistLocationService _inner;

        public AudiomackSource(AudiomackArtistLocationService inner) => _inner = inner;

        public string SourceName => AudiomackApiClient.SourceName;

        public async Task<ArtistLocationResult?> ResolveMatchedArtistAsync(string artistId, string artistName, string trackUrl, CancellationToken cancellationToken)
        {
            var profile = await _inner.ResolveMatchedProfileAsync(artistId, artistName, trackUrl, cancellationToken).ConfigureAwait(false);
            var location = profile?.Location;
            return location is null ? null : new ArtistLocationResult(location.RawLocation, location.City, location.Country, location.CountryCode, location.Source)
            {
                ArtistName = artistName, Region = location.Region, Hometown = location.Hometown,
                SourceReference = profile!.SourceReference, RetrievedAt = profile.RetrievedAt, KnownAt = profile.RetrievedAt,
                ResolutionMethod = "matched-audiomack-profile-id", LocationMeaning = "profile-location"
            };
        }

        public async Task<ArtistLocationResult?> ResolveAsync(
            long artistId,
            string? artistName,
            CancellationToken cancellationToken = default)
        {
            var profile = await _inner.ResolveProfileAsync(artistId, artistName, cancellationToken).ConfigureAwait(false);
            var location = profile?.Location;
            return location is null
                ? null
                : new ArtistLocationResult(
                    location.RawLocation,
                    location.City,
                    location.Country,
                    location.CountryCode,
                    location.Source) {
                    Region = location.Region, Hometown = location.Hometown,
                    SourceReference = profile!.SourceReference, RetrievedAt = profile.RetrievedAt,
                    KnownAt = profile.RetrievedAt, ResolutionMethod = "matched-audiomack-profile",
                    LocationMeaning = "profile-location"
                };
        }
    }

    /// <summary>
    /// Builds the resolver's Audiomack fallback.
    /// </summary>
    public static IArtistLocationSource CreateAudiomackSource(AudiomackArtistLocationService service)
        => new AudiomackSource(service);
}
