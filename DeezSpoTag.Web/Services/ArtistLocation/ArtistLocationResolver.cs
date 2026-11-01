using System.Text.Json;
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
/// <item>Apple Music, using the verified artist's explicit origin;</item>
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
    /// Creates a resolver over the default source order: Apple Music, MusicBrainz, then
    /// Audiomack as the fallback.
    /// </summary>
    /// <param name="musicBrainz">The default source.</param>
    /// <param name="audiomack">The fallback.</param>
    public ArtistLocationResolver(
        ILogger<ArtistLocationResolver> logger,
        IArtistLocationSource? musicBrainz = null,
        IArtistLocationSource? audiomack = null,
        ArtistLocationOverrideStore? overrides = null,
        IArtistLocationSource? apple = null)
    {
        _logger = logger;
        _sources = new[] { apple, musicBrainz, audiomack }.OfType<IArtistLocationSource>().ToArray();
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

    /// <summary>Refresh-only resolution: preserve selected-source failures; automatic resolution tries each independent source.</summary>
    public async Task<ArtistLocationResult?> ResolveRequiredAsync(long artistId, string artistName,
        string? onlySource, ArtistMetadataProviderGate gate, CancellationToken cancellationToken)
    {
        var manual = await ResolveManualAsync(artistId, cancellationToken).ConfigureAwait(false);
        if (manual is not null) return manual;
        var failures = new List<Exception>();
        var auto = string.IsNullOrWhiteSpace(onlySource) || onlySource.Equals("auto", StringComparison.OrdinalIgnoreCase);
        foreach (var source in _sources.Where(source => string.IsNullOrWhiteSpace(onlySource)
                     || onlySource.Equals("auto", StringComparison.OrdinalIgnoreCase)
                     || source.SourceName.Equals(onlySource, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                // Apple identity matching already uses this gate; its adapter gates the
                // subsequent profile request separately, avoiding recursive acquisition.
                var result = source is AppleSource apple
                    ? await apple.ResolveRequiredAsync(artistId, artistName, cancellationToken)
                    : await gate.RunRequiredAsync(source.SourceName,
                        token => source is MusicBrainzArtistLocationService musicBrainz
                            ? musicBrainz.ResolveRequiredAsync(artistId, artistName, token)
                            : source is AudiomackSource audiomack
                                ? audiomack.ResolveRequiredAsync(artistId, artistName, token)
                                : source.ResolveAsync(artistId, artistName, token), cancellationToken).ConfigureAwait(false);
                if (result is not null) return result with { ArtistId = artistId, ArtistName = artistName };
            }
            catch (Exception ex) when (auto && !cancellationToken.IsCancellationRequested)
            {
                failures.Add(ex);
                _logger.LogWarning(ex, "Artist location source {Source} failed; trying the next source.", source.SourceName);
            }
        }
        if (failures.Count > 0) throw new AggregateException("Artist location providers remain unresolved.", failures);
        return null;
    }

    public async Task<ArtistLocationResult?> ResolveMatchedArtistAsync(string provider, string providerArtistId,
        string artistName, string? trackUrl, CancellationToken cancellationToken)
    {
        foreach (var source in _sources)
        {
            if (provider is "apple" or "itunes" && source is AppleSource apple)
                return await SafeResolveAsync("apple", () => apple.ResolveMatchedAsync(providerArtistId, artistName, cancellationToken), cancellationToken);
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
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
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

        public async Task<ArtistLocationResult?> ResolveRequiredAsync(long artistId, string artistName, CancellationToken token)
        {
            var profile = await _inner.ResolveProfileRequiredAsync(artistId, artistName, token).ConfigureAwait(false);
            var location = profile?.Location;
            return location is null ? null : new ArtistLocationResult(location.RawLocation, location.City, location.Country, location.CountryCode, location.Source)
            {
                Region = location.Region, Hometown = location.Hometown, SourceReference = profile!.SourceReference,
                RetrievedAt = profile.RetrievedAt, KnownAt = profile.RetrievedAt,
                ResolutionMethod = "matched-audiomack-profile", LocationMeaning = "profile-location"
            };
        }

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

    public const string LocationPolicyVersion = "apple-first-v1";

    private sealed class AppleSource(AppleArtistBiographyService service, LibraryRepository repository,
        ArtistPageCacheRepository cache, ArtistArtworkCatalogService catalog, ArtistAliasService aliases,
        ArtistMetadataProviderGate gate) : IArtistLocationSource
    {
        public string SourceName => "apple";

        public Task<ArtistLocationResult?> ResolveAsync(long artistId, string? name, CancellationToken token = default)
            => ResolveCoreAsync(artistId, name, false, token);

        public Task<ArtistLocationResult?> ResolveRequiredAsync(long artistId, string name, CancellationToken token)
            => ResolveCoreAsync(artistId, name, true, token);

        private async Task<ArtistLocationResult?> ResolveCoreAsync(long artistId, string? name, bool required, CancellationToken token)
        {
            if (artistId <= 0 || string.IsNullOrWhiteSpace(name)) return null;
            var key = artistId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var names = await aliases.GetGroupNamesAsync(name, token);
            if (names.Count == 0) names = new[] { name };
            var ids = await GetVerifiedIdsAsync(artistId, token);
            if (ids.Count == 0)
            {
                await catalog.EnsureProviderIdentityAsync(artistId, name, "apple", names, token);
                ids = await GetVerifiedIdsAsync(artistId, token);
            }
            if (required && ids.Count == 0)
                throw new InvalidOperationException("Apple artist identity is unresolved; location work is retained.");
            if (!required)
            {
                var retained = await cache.TryGetAsync("apple-location", key, token);
                if (retained is not null && cache.IsFresh(retained.FetchedUtc))
                {
                    try
                    {
                        using var payload = JsonDocument.Parse(retained.PayloadJson);
                        var saved = payload.RootElement;
                        if (saved.GetProperty("policy").GetString() == LocationPolicyVersion
                            && ids.Contains(saved.GetProperty("appleId").GetString()))
                            return saved.GetProperty("location").Deserialize<ArtistLocationResult>();
                    }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
                }
            }
            var failures = new List<Exception>();
            foreach (var id in ids)
            {
                try
                {
                    var profile = await gate.RunRequiredAsync("apple", ct => service.ResolveByArtistIdAsync(id, name, ct,
                        allowArtistPageScrape: false, propagatePageErrors: required), token);
                    if (profile?.Location is not { } location || !names.Any(n => string.Equals(n.Trim(), profile.Name.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
                    location = location with { ArtistId = artistId, ArtistName = name };
                    await cache.UpsertRequiredAsync("apple-location", key,
                        JsonSerializer.Serialize(new { policy = LocationPolicyVersion, appleId = id, location }), DateTimeOffset.UtcNow, token);
                    return location;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    failures.Add(ex);
                    if (ArtistMetadataProviderGate.IsRateLimited(ex) || ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }) throw;
                }
            }
            if (failures.Count > 0) throw new AggregateException("Apple artist location remains unresolved.", failures);
            return null;
        }

        private async Task<IReadOnlyList<string>> GetVerifiedIdsAsync(long artistId, CancellationToken token)
            => (await repository.GetArtistSourceIdentitiesAsync(artistId, "apple", token))
                .Where(identity => identity.VerificationState is "verified" or "user_confirmed")
                .Select(identity => identity.SourceId).Distinct(StringComparer.Ordinal).ToArray();

        public Task<ArtistLocationResult?> ResolveMatchedAsync(string id, string name, CancellationToken token)
            => gate.RunRequiredAsync("apple", async ct =>
            {
                var profile = await service.ResolveByArtistIdAsync(id, name, ct, allowArtistPageScrape: false);
                var names = await aliases.GetGroupNamesAsync(name, ct);
                return profile is not null && (names.Count > 0 ? names : new[] { name }).Any(n => string.Equals(n.Trim(), profile.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                    ? profile.Location : null;
            }, token);
    }

    public static IArtistLocationSource CreateAppleSource(AppleArtistBiographyService service, LibraryRepository repository,
        ArtistPageCacheRepository cache, ArtistArtworkCatalogService catalog, ArtistAliasService aliases, ArtistMetadataProviderGate gate)
        => new AppleSource(service, repository, cache, catalog, aliases, gate);

    /// <summary>
    /// Builds the resolver's Audiomack fallback.
    /// </summary>
    public static IArtistLocationSource CreateAudiomackSource(AudiomackArtistLocationService service)
        => new AudiomackSource(service);
}
