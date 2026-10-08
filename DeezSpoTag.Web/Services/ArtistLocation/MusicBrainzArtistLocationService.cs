using System.Collections.Concurrent;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services.AutoTag;

namespace DeezSpoTag.Web.Services.ArtistLocation;

/// <summary>
/// Resolves an artist's location from MusicBrainz.
/// </summary>
/// <remarks>
/// <para>
/// Three routes, cheapest first, and the first that answers wins:
/// </para>
/// <list type="number">
/// <item>an MBID already stored against the artist;</item>
/// <item>an MBID read from the artist's own file tags;</item>
/// <item>a name search, which is only trusted with album overlap.</item>
/// </list>
///
/// <para>
/// Routes 1 and 2 are identity lookups: an MBID names exactly one artist, so
/// they need no cross-check. Route 3 is a text search over a crowd-sourced index
/// and is the only ambiguous one, which is why it requires album overlap and
/// why there is deliberately no name-only escape from that requirement.
/// </para>
/// </remarks>
public sealed class MusicBrainzArtistLocationService : IArtistLocationSource
{
    /// <summary>Stored in <c>artist_source</c> alongside the other provider identities.</summary>
    public const string MusicBrainzSourceName = "musicbrainz";

    /// <summary>
    /// How many search hits to consider. A handful is enough to see a
    /// same-name collision; more only adds requests against a rate-limited API.
    /// </summary>
    private const int SearchCandidateLimit = 10;

    /// <summary>
    /// Release groups fetched per candidate for the overlap check. A library
    /// artist with more albums than this is scored against a subset, which can
    /// only understate the overlap — it can never manufacture a false match.
    /// </summary>
    private const int ReleaseGroupLimit = 100;

    private readonly MusicBrainzClient _client;
    private readonly LibraryRepository? _repository;
    private readonly ILogger<MusicBrainzArtistLocationService> _logger;
    private readonly ConcurrentDictionary<long, ArtistLocationResult?> _memoryCache = new();

    public MusicBrainzArtistLocationService(
        MusicBrainzClient client,
        ILogger<MusicBrainzArtistLocationService> logger,
        LibraryRepository? repository = null)
    {
        _client = client;
        _logger = logger;
        _repository = repository;
    }

    public string SourceName => MusicBrainzSourceName;

    public void InvalidateArtist(long artistId) => _memoryCache.TryRemove(artistId, out _);

    public async Task<ArtistLocationResult?> ResolveAsync(
        long artistId,
        string? artistName,
        CancellationToken cancellationToken = default)
    {
        if (_memoryCache.TryGetValue(artistId, out var cached))
        {
            return cached;
        }

        var result = await ResolveCoreAsync(artistId, artistName, cancellationToken).ConfigureAwait(false);
        _memoryCache[artistId] = result;
        return result;
    }

    public async Task<ArtistLocationResult?> ResolveMatchedArtistAsync(string mbid, string artistName, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(mbid, out _)) return null;
        var artist = await _client.GetArtistAsync(mbid, cancellationToken).ConfigureAwait(false);
        if (artist is null || !string.Equals(artist.Id, mbid, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(artist.Name?.Trim(), artistName.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return await ObserveLocationAsync(artist, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArtistLocationResult?> ObserveLocationAsync(MusicBrainzArtist artist, CancellationToken cancellationToken)
    {
        // Artist responses can omit area types. Resolve the exact area identity,
        // rather than guessing that every begin-area is a city.
        if (artist.BeginArea is { Type: null, Id.Length: > 0 } begin)
            artist.BeginArea = await _client.GetAreaAsync(begin.Id, cancellationToken).ConfigureAwait(false) ?? begin;
        if (artist.Area is { Type: null, Id.Length: > 0 } area)
            artist.Area = await _client.GetAreaAsync(area.Id, cancellationToken).ConfigureAwait(false) ?? area;
        var location = ToLocation(artist);
        var observedAt = DateTimeOffset.UtcNow;
        return location is null ? null : location with
        { ArtistName = artist.Name, RetrievedAt = observedAt, KnownAt = observedAt, ResolutionMethod = "artist-id" };
    }

    private async Task<ArtistLocationResult?> ResolveCoreAsync(
        long artistId,
        string? artistName,
        CancellationToken cancellationToken)
    {
        // Route 1: a mapping we already trust. A miss here is not a rejection, so
        // the search still runs; a hit is authoritative and stops the search.
        var storedMbid = await TryGetStoredMbidAsync(artistId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(storedMbid))
        {
            var fromStored = await ResolveByMbidAsync(storedMbid, cancellationToken).ConfigureAwait(false);
            if (fromStored is not null)
            {
                return fromStored;
            }
        }

        // Route 2: the artist's own tags. AutoTag already stamps
        // MUSICBRAINZ_ARTISTID, so this usually answers without any search.
        var taggedMbid = await TryGetTaggedMbidAsync(artistId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(taggedMbid) && !string.Equals(taggedMbid, storedMbid, StringComparison.OrdinalIgnoreCase))
        {
            var fromTag = await ResolveByMbidAsync(taggedMbid, cancellationToken).ConfigureAwait(false);
            if (fromTag is not null)
            {
                await TryStoreMbidAsync(artistId, taggedMbid, cancellationToken).ConfigureAwait(false);
                return fromTag;
            }
        }

        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        // Route 3: the only ambiguous route, and the only one that costs requests.
        return await ResolveByNameSearchAsync(artistId, artistName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns an artist into a location by identity. No album cross-check: an MBID
    /// identifies exactly one artist, so there is no same-name ambiguity to rule out.
    /// </summary>
    private async Task<ArtistLocationResult?> ResolveByMbidAsync(
        string mbid,
        CancellationToken cancellationToken)
    {
        MusicBrainzArtist? artist;
        try
        {
            artist = await _client.GetArtistAsync(mbid, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MusicBrainz artist lookup failed for {Mbid}.", mbid);
            return null;
        }

        return artist is null ? null : await ObserveLocationAsync(artist, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The name-search route, which must be corroborated by album overlap.
    /// </summary>
    /// <remarks>
    /// A same-name artist is the failure mode this guards. A search hit is only
    /// considered when it shares at least one album with the library, and among
    /// the hits that do, the one sharing the most wins. Zero overlap is a
    /// rejection, not a weak accept.
    /// </remarks>
    private async Task<ArtistLocationResult?> ResolveByNameSearchAsync(
        long artistId,
        string artistName,
        CancellationToken cancellationToken)
    {
        ArtistSearchResults? results;
        try
        {
            results = await _client
                .SearchArtistsAsync(artistName, SearchCandidateLimit, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MusicBrainz artist search failed for {ArtistName}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistName));
            return null;
        }

        if (results is null || results.Artists.Count == 0)
        {
            _logger.LogInformation("MusicBrainz artist search returned nothing for {ArtistName}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistName));
            return null;
        }

        // An artist with no albums cannot be cross-checked, and under the overlap
        // rule that means no match. Saying so explicitly keeps this from reading
        // as a coverage bug later.
        var heldAlbums = await LoadHeldAlbumTitlesAsync(artistId, cancellationToken).ConfigureAwait(false);
        if (ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(heldAlbums))
        {
            return await SelectByAlbumOverlapAsync(artistId, artistName, heldAlbums, results.Artists, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "MusicBrainz artist search for {ArtistName} not corroborated: the library holds no albums, "
            + "and name search requires album overlap. Falling back.",
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistName));
        return null;
    }

    private async Task<ArtistLocationResult?> SelectByAlbumOverlapAsync(
        long artistId,
        string artistName,
        IReadOnlyList<string> heldAlbums,
        IReadOnlyList<MusicBrainzArtist> candidates,
        CancellationToken cancellationToken)
    {
        var best = (Location: default(ArtistLocationResult?), Overlap: -1, NameMatches: false, Mbid: string.Empty);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id))
            {
                continue;
            }

            // The name has to be plausible before spending a request on the
            // catalogue, but it is not sufficient on its own.
            var nameMatches = ArtistIdentityTextNormalizer.NamesEquivalent(artistName, candidate.Name)
                || (candidate.Aliases?.Any(alias => ArtistIdentityTextNormalizer.NamesEquivalent(artistName, alias.Name)) ?? false);
            if (!nameMatches)
            {
                continue;
            }

            var candidateAlbums = await LoadCandidateAlbumTitlesAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
            var score = ArtistIdentityTextNormalizer.ScoreAlbumOverlap(heldAlbums, candidateAlbums);
            if (!score.HasOverlap)
            {
                _logger.LogInformation(
                    "MusicBrainz candidate {Mbid} for {ArtistName} shares no album with the library; not a match.",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(candidate.Id),
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistName));
                continue;
            }

            // More shared albums wins. Ties break on MBID so the same artist
            // always resolves the same way, which matters because a result is cached.
            if (score.MatchedCount > best.Overlap
                || (score.MatchedCount == best.Overlap && string.CompareOrdinal(candidate.Id, best.Mbid) < 0))
            {
                var location = ToLocation(candidate);
                var observedAt = DateTimeOffset.UtcNow;
                best = (location is null ? null : location with
                { RetrievedAt = observedAt, KnownAt = observedAt, ResolutionMethod = "name-and-album-overlap" },
                    score.MatchedCount, nameMatches, candidate.Id);
            }
        }

        if (best.Location is null)
        {
            _logger.LogInformation(
                "No MusicBrainz candidate for {ArtistName} shared an album with the library. Falling back.",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistName));
            return null;
        }

        _logger.LogInformation(
            "MusicBrainz matched {ArtistName} to {Mbid} on {Overlap} shared album(s).",
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistName),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(best.Mbid),
            best.Overlap);

        await TryStoreMbidAsync(artistId, best.Mbid, cancellationToken).ConfigureAwait(false);
        return best.Location;
    }

    /// <summary>
    /// Maps MusicBrainz's area model onto the page's city/country pair.
    /// </summary>
    /// <remarks>
    /// MusicBrainz types its areas, and the type is what makes this safe:
    /// <c>begin-area</c> is a birth or formation place and is usually city-level,
    /// while <c>area</c> is the artist's main association and is usually the
    /// country. A city is therefore only taken from a city-level area, so a
    /// country name can never end up in the city field.
    ///
    /// The country code comes straight from MusicBrainz's own ISO 3166-1 value
    /// rather than being inferred from a name, so it needs no country lookup
    /// table and cannot be wrong.
    /// </remarks>
    internal static ArtistLocationResult? ToLocation(MusicBrainzArtist artist)
    {
        ArgumentNullException.ThrowIfNull(artist);

        var countryCode = NormalizeCountryCode(artist.CountryCode);
        var city = artist.BeginArea is { IsCityLevel: true, Name.Length: > 0 } begin
            ? begin.Name.Trim()
            : null;

        string? country = artist.Area is { IsCountryLevel: true, Name.Length: > 0 } area
            ? area.Name.Trim()
            : null;

        if (city is null && country is null && countryCode is null)
        {
            // MusicBrainz knows this artist but not where they are from. Returning
            // null lets the next source try; a blank location would hide that.
            return null;
        }

        var raw = string.Join(", ", new[] { city, country }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return new ArtistLocationResult(raw.Length == 0 ? null : raw, city, country, countryCode, MusicBrainzSourceName)
        { SourceReference = string.IsNullOrWhiteSpace(artist.Id) ? null : $"https://musicbrainz.org/artist/{artist.Id}",
          LocationMeaning = "begin-area-city-and-associated-country" };
    }

    private static string? NormalizeCountryCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().ToUpperInvariant();
        return trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && char.IsAsciiLetter(trimmed[1])
            ? trimmed
            : null;
    }

    private async Task<IReadOnlyList<string>> LoadHeldAlbumTitlesAsync(
        long artistId,
        CancellationToken cancellationToken)
    {
        if (_repository is null || !_repository.IsConfigured || artistId <= 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            var albums = await _repository.GetArtistAlbumsAsync(artistId, cancellationToken).ConfigureAwait(false);
            return albums
                .Select(album => album.Title)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title.Trim())
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Album lookup failed for artist {ArtistId}.", artistId);
            return Array.Empty<string>();
        }
    }

    private async Task<IReadOnlyList<string>> LoadCandidateAlbumTitlesAsync(
        string mbid,
        CancellationToken cancellationToken)
    {
        try
        {
            var groups = await _client
                .GetArtistReleaseGroupsAsync(mbid, ReleaseGroupLimit, cancellationToken)
                .ConfigureAwait(false);
            if (groups is null)
            {
                return Array.Empty<string>();
            }

            return groups.ReleaseGroups
                .Select(group => group.Title)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title.Trim())
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "MusicBrainz release-group lookup failed for {Mbid}.", mbid);
            return Array.Empty<string>();
        }
    }

    private async Task<string?> TryGetStoredMbidAsync(long artistId, CancellationToken cancellationToken)
    {
        if (_repository is null || !_repository.IsConfigured)
        {
            return null;
        }

        try
        {
            return await _repository
                .GetArtistSourceIdAsync(artistId, MusicBrainzSourceName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MusicBrainz source lookup failed for artist {ArtistId}.", artistId);
            return null;
        }
    }

    private async Task TryStoreMbidAsync(long artistId, string mbid, CancellationToken cancellationToken)
    {
        if (_repository is null || !_repository.IsConfigured)
        {
            return;
        }

        try
        {
            await _repository
                .UpsertArtistSourceIdAsync(artistId, MusicBrainzSourceName, mbid, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Caching the identity is an optimisation; failing to persist it only
            // costs another search next time, so it must not fail the resolution.
            _logger.LogWarning(ex, "MusicBrainz identity persist failed for artist {ArtistId}.", artistId);
        }
    }

    private async Task<string?> TryGetTaggedMbidAsync(long artistId, CancellationToken cancellationToken)
    {
        if (_repository is null || !_repository.IsConfigured)
        {
            return null;
        }

        try
        {
            return await _repository
                .GetArtistMusicBrainzArtistIdAsync(artistId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MusicBrainz tag lookup failed for artist {ArtistId}.", artistId);
            return null;
        }
    }
}
