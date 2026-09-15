using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Core.Security;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>
/// One resolved Audiomack artist profile snapshot: the normalized location and the
/// raw biography (<c>bio</c>) taken from the same matched artist object. Either half
/// may be null. A non-null snapshot means the page was fetched and the artist object
/// matched; a null snapshot is a miss (unavailable page, parse miss, or no data).
/// </summary>
public sealed record AudiomackArtistProfile(AudiomackLocationResult? Location, string? Biography);

/// <summary>
/// Resolves an artist's location (city + country) and biography from the artist's
/// public Audiomack profile. The Audiomack artist identity is resolved through the
/// first-party web search API (anonymous: no cookies, no user tokens) and
/// persisted in artist_source (source "audiomack") so later loads — and user
/// corrections from the library artist page — target an exact profile instead
/// of re-guessing a slug from the artist name. Lookups are keyed by the
/// canonical slug and cached in memory plus persisted through
/// <see cref="ArtistPageCacheRepository"/> so a restart does not re-fetch. A
/// miss (no hometown and no bio set) is cached as well: an unknown profile never
/// triggers repeated fetching, while a failed fetch is deliberately NOT cached so
/// a transient outage self-heals on the next call.
/// </summary>
public sealed class AudiomackArtistLocationService
{
    public const string LocationCacheSource = "audiomack-location";
    private const string ArtistPageBaseUrl = "https://audiomack.com/";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromDays(7);

    /// <summary>Songs fetched to learn a search candidate's album catalogue.</summary>
    private const int CandidateCatalogueSearchLimit = 25;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ArtistPageCacheRepository _artistPageCache;
    private readonly AudiomackApiClient _apiClient;
    private readonly LibraryRepository? _libraryRepository;
    private readonly ILogger<AudiomackArtistLocationService> _logger;
    private readonly ConcurrentDictionary<string, (AudiomackArtistProfile? Profile, DateTimeOffset FetchedUtc)> _memoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    public AudiomackArtistLocationService(
        IHttpClientFactory httpClientFactory,
        ArtistPageCacheRepository artistPageCache,
        AudiomackApiClient apiClient,
        ILogger<AudiomackArtistLocationService> logger,
        LibraryRepository? libraryRepository = null)
    {
        _httpClientFactory = httpClientFactory;
        _artistPageCache = artistPageCache;
        _apiClient = apiClient;
        _logger = logger;
        _libraryRepository = libraryRepository;
    }

    /// <summary>
    /// Resolve the location for a library artist: prefers the stored Audiomack
    /// mapping in artist_source (set once via the anonymous search API, correctable
    /// by the user from the library artist page), discovers it only when absent,
    /// and never re-searches behind a stored value.
    /// </summary>
    public async Task<AudiomackLocationResult?> ResolveAsync(long artistId, string? artistName, CancellationToken cancellationToken = default)
        => (await ResolveProfileAsync(artistId, artistName, cancellationToken).ConfigureAwait(false))?.Location;

    /// <summary>Name-based location resolution for flows without a library artist id.</summary>
    public async Task<AudiomackLocationResult?> ResolveAsync(string? artistName, CancellationToken cancellationToken = default)
        => (await ResolveProfileAsync(artistName, cancellationToken).ConfigureAwait(false))?.Location;

    /// <summary>Biography for a library artist, or null when the profile carries none.</summary>
    public async Task<string?> ResolveBiographyAsync(long artistId, string? artistName, CancellationToken cancellationToken = default)
        => (await ResolveProfileAsync(artistId, artistName, cancellationToken).ConfigureAwait(false))?.Biography;

    /// <summary>
    /// Full profile (location + biography) for a library artist, sharing one fetch
    /// and one cache entry so a page load never fetches the profile twice. The
    /// artist's held albums are loaded from the library and used to confirm an
    /// Audiomack search candidate: a same-named artist whose catalogue does not
    /// contain them is rejected (and logged) instead of being accepted blindly.
    /// </summary>
    public Task<AudiomackArtistProfile?> ResolveProfileAsync(long artistId, string? artistName, CancellationToken cancellationToken = default)
        => ResolveProfileAsync(artistId, artistName, expectedAlbums: null, cancellationToken);

    /// <summary>
    /// Profile resolution with an explicit album cross-check list. Passing
    /// <paramref name="expectedAlbums"/> as null loads the albums the library holds
    /// for the artist; passing an empty list explicitly disables the album
    /// cross-check (name-only), which is the caller's responsibility to justify.
    /// </summary>
    public async Task<AudiomackArtistProfile?> ResolveProfileAsync(
        long artistId,
        string? artistName,
        IReadOnlyList<string>? expectedAlbums,
        CancellationToken cancellationToken = default)
    {
        var storedSlug = await TryGetStoredSlugAsync(artistId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(storedSlug))
        {
            // The stored mapping is authoritative (auto-discovered once, or user-set).
            // A null here is a cached negative for that slug; rediscovery would burn a
            // search on every page load, so it stops here — the user can correct the
            // mapping from the library page instead.
            var stored = await ResolveProfileBySlugAsync(storedSlug, artistName, candidate: null, cancellationToken).ConfigureAwait(false);
            return stored.Profile;
        }

        var heldAlbums = expectedAlbums ?? await LoadExpectedAlbumsAsync(artistId, cancellationToken).ConfigureAwait(false);

        var candidate = await _apiClient.SearchArtistAsync(artistName, cancellationToken).ConfigureAwait(false);
        if (candidate == null)
        {
            // Nothing to cross-check against: fall back to the name-derived slug and
            // let the page parser's own name check guard it. This is explicit, not a
            // silent text-search acceptance.
            var guessedSlug = BuildUrlSlug(artistName);
            if (guessedSlug == null)
            {
                return null;
            }

            _logger.LogInformation(
                "Audiomack artist search returned no candidate for {ArtistName}; falling back to slug guess {Slug}",
                LogSanitizer.OneLine(artistName),
                LogSanitizer.OneLine(guessedSlug));
            var guessed = await ResolveProfileBySlugAsync(guessedSlug, artistName, candidate: null, cancellationToken).ConfigureAwait(false);
            if (guessed.Profile != null)
            {
                await TryStoreSlugAsync(artistId, guessedSlug, cancellationToken).ConfigureAwait(false);
            }

            return guessed.Profile;
        }

        var candidateAlbums = heldAlbums.Count == 0
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : await TryResolveCandidateAlbumTitlesAsync(candidate, cancellationToken).ConfigureAwait(false);
        var decision = AudiomackArtistMatcher.Confirm(candidate.Name, artistName, heldAlbums, candidateAlbums);
        if (!decision.Accepted)
        {
            // Observable rejection: a wrong match is diagnosable instead of a silent null.
            _logger.LogWarning(
                "Audiomack artist candidate rejected for {ArtistName} (slug={Slug}): {Reason}",
                LogSanitizer.OneLine(artistName),
                LogSanitizer.OneLine(candidate.UrlSlug),
                decision.Reason);
            return null;
        }

        if (decision.Outcome == AudiomackArtistMatchOutcome.AcceptedWithoutAlbumCrossCheck)
        {
            _logger.LogInformation(
                "Audiomack artist candidate for {ArtistName} accepted by name only: {Reason}",
                LogSanitizer.OneLine(artistName),
                decision.Reason);
        }

        var resolved = await ResolveProfileBySlugAsync(candidate.UrlSlug, artistName, candidate, cancellationToken).ConfigureAwait(false);
        if (resolved.CandidateRejected)
        {
            return null;
        }

        // Persist the identity once it is validated (search name/album match, or the
        // page parse confirmed the artist name) — including when Audiomack simply has
        // no profile data, so the search cost is paid once.
        await TryStoreSlugAsync(artistId, candidate.UrlSlug, cancellationToken).ConfigureAwait(false);
        return resolved.Profile;
    }

    /// <summary>Name-based profile resolution for flows without a library artist id.</summary>
    public async Task<AudiomackArtistProfile?> ResolveProfileAsync(string? artistName, CancellationToken cancellationToken = default)
    {
        var slug = BuildUrlSlug(artistName);
        if (slug == null)
        {
            return null;
        }

        var resolved = await ResolveProfileBySlugAsync(slug, artistName, candidate: null, cancellationToken).ConfigureAwait(false);
        return resolved.Profile;
    }

    internal static string? BuildUrlSlug(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var lowered = artistName.Trim().ToLowerInvariant();
        var slugChars = lowered
            .Select(ch => char.IsLetterOrDigit(ch)
                ? ch
                : ch is ' ' or '-' or '.' or '_' or '/' ? '-' : '\0')
            .Where(ch => ch != '\0');
        var slug = new string(slugChars.ToArray());
        slug = System.Text.RegularExpressions.Regex.Replace(slug, "-{2,}", "-").Trim('-');
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>
    /// Result of resolving a slug: the profile (may be null for a miss) plus whether the
    /// page confirmed the search candidate's id was wrong, in which case nothing was
    /// cached or persisted and the caller must not store the slug.
    /// </summary>
    private sealed record ProfileResolution(AudiomackArtistProfile? Profile, bool CandidateRejected);

    private async Task<ProfileResolution> ResolveProfileBySlugAsync(
        string slug,
        string? artistName,
        AudiomackArtistCandidate? candidate,
        CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(slug, out var cached) && DateTimeOffset.UtcNow - cached.FetchedUtc <= MemoryCacheTtl)
        {
            return new ProfileResolution(cached.Profile, false);
        }

        var (persistentFound, persistentProfile) = await TryReadCachedProfileAsync(slug, cancellationToken).ConfigureAwait(false);
        if (persistentFound)
        {
            _memoryCache[slug] = (persistentProfile, DateTimeOffset.UtcNow);
            return new ProfileResolution(persistentProfile, false);
        }

        if (string.IsNullOrWhiteSpace(artistName))
        {
            // The page parser cross-checks the artist name; without one a wrong
            // profile could be accepted, so nothing is fetched or cached.
            return new ProfileResolution(null, false);
        }

        AudiomackArtistProfile? resolved = null;
        AudiomackArtistPageInfo? info = null;
        var fetched = false;
        try
        {
            (fetched, resolved, info) = await TryFetchProfileAsync(slug, artistName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Audiomack artist profile fetch failed (slug={Slug})",
                LogSanitizer.OneLine(slug));
        }

        if (!fetched)
        {
            // Unavailable page or network failure: not an authoritative "no data"
            // answer, so nothing is cached and the next call retries.
            return new ProfileResolution(null, false);
        }

        // A text-search candidate's id must be confirmed against Audiomack's own page
        // before the mapping is trusted; a present-but-different id is a hard rejection.
        if (candidate != null && info != null && !AudiomackArtistMatcher.ArtistIdMatches(candidate.Id, info.RawArtistId))
        {
            _logger.LogWarning(
                "Audiomack artist id mismatch for {ArtistName}: search id {SearchId} but page id {PageId} (slug={Slug})",
                LogSanitizer.OneLine(artistName),
                candidate.Id,
                LogSanitizer.OneLine(info.RawArtistId),
                LogSanitizer.OneLine(slug));
            return new ProfileResolution(null, true);
        }

        _memoryCache[slug] = (resolved, DateTimeOffset.UtcNow);
        await WriteCachedProfileAsync(slug, resolved, cancellationToken).ConfigureAwait(false);
        return new ProfileResolution(resolved, false);
    }

    private async Task<(bool Fetched, AudiomackArtistProfile? Profile, AudiomackArtistPageInfo? Info)> TryFetchProfileAsync(
        string slug,
        string artistName,
        CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FetchTimeout);
        using var response = await httpClient.GetAsync(ArtistPageBaseUrl + slug, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (false, null, null);
        }

        var html = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        var info = AudiomackArtistPageParser.TryExtractArtistPageInfo(html, slug, artistName);
        if (info == null)
        {
            // The page loaded but no object matched (or the matched artist has no
            // location and no bio). Authoritative negative.
            return (true, null, null);
        }

        var location = AudiomackLocationNormalizer.Normalize(info.RawLocation);
        var biography = string.IsNullOrWhiteSpace(info.RawBiography) ? null : info.RawBiography.Trim();
        if (location == null && biography == null)
        {
            return (true, null, info);
        }

        return (true, new AudiomackArtistProfile(location, biography), info);
    }

    private async Task<string?> TryGetStoredSlugAsync(long artistId, CancellationToken cancellationToken)
    {
        if (_libraryRepository == null || !_libraryRepository.IsConfigured)
        {
            return null;
        }

        try
        {
            return await _libraryRepository.GetArtistSourceIdAsync(artistId, AudiomackApiClient.SourceName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Audiomack source lookup failed for artist {ArtistId}", artistId);
            return null;
        }
    }

    private async Task TryStoreSlugAsync(long artistId, string slug, CancellationToken cancellationToken)
    {
        if (_libraryRepository == null || !_libraryRepository.IsConfigured)
        {
            return;
        }

        try
        {
            await _libraryRepository.UpsertArtistSourceIdAsync(artistId, AudiomackApiClient.SourceName, slug, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Audiomack source persist failed for artist {ArtistId}", artistId);
        }
    }

    /// <summary>
    /// The albums the library holds for the artist, used to confirm a search
    /// candidate. A missing/disabled repository, a query failure or an artist with
    /// no albums all yield an empty list, which disables the album cross-check
    /// (name-only) rather than rejecting a real artist.
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadExpectedAlbumsAsync(long artistId, CancellationToken cancellationToken)
    {
        if (_libraryRepository == null || !_libraryRepository.IsConfigured || artistId <= 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            var albums = await _libraryRepository.GetArtistAlbumsAsync(artistId, cancellationToken).ConfigureAwait(false);
            return albums
                .Select(album => album.Title)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Audiomack expected-album lookup failed for artist {ArtistId}", artistId);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The album titles Audiomack's own catalogue exposes for a search candidate,
    /// learned from song-search results restricted to the candidate artist. An
    /// empty list means the catalogue was unavailable (which enables the explicit
    /// name-only fallback); it never means "mismatch".
    /// </summary>
    private async Task<IReadOnlyList<string>> TryResolveCandidateAlbumTitlesAsync(
        AudiomackArtistCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            var songs = await _apiClient
                .SearchSongsAsync(candidate.Name, CandidateCatalogueSearchLimit, cancellationToken)
                .ConfigureAwait(false);
            var titles = new List<string>();
            foreach (var song in songs)
            {
                if (!BelongsToCandidate(song, candidate) || string.IsNullOrWhiteSpace(song.Album))
                {
                    continue;
                }

                var title = song.Album.Trim();
                if (!titles.Any(existing => string.Equals(existing, title, StringComparison.OrdinalIgnoreCase)))
                {
                    titles.Add(title);
                }
            }

            return titles;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "Audiomack candidate catalogue lookup failed ({ArtistName})",
                LogSanitizer.OneLine(candidate.Name));
            return Array.Empty<string>();
        }
    }

    private static bool BelongsToCandidate(AudiomackSongCandidate song, AudiomackArtistCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.UrlSlug)
            && !string.IsNullOrWhiteSpace(song.ArtistSlug)
            && string.Equals(song.ArtistSlug.Trim(), candidate.UrlSlug.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return AudiomackApiClient.NameMatches(song.UploaderName ?? song.Artist, candidate.Name);
    }

    private async Task<(bool Found, AudiomackArtistProfile? Profile)> TryReadCachedProfileAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        var entry = await _artistPageCache.TryGetAsync(LocationCacheSource, slug, cancellationToken).ConfigureAwait(false);
        if (entry == null || !_artistPageCache.IsUsable(entry.FetchedUtc))
        {
            return (false, null);
        }

        try
        {
            using var document = JsonDocument.Parse(entry.PayloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (true, null);
            }

            AudiomackLocationResult? location = null;
            if (root.TryGetProperty("raw_location", out var rawLocationElement)
                && rawLocationElement.ValueKind == JsonValueKind.String)
            {
                location = AudiomackLocationNormalizer.Normalize(rawLocationElement.GetString());
            }

            string? biography = null;
            if (root.TryGetProperty("raw_biography", out var rawBiographyElement)
                && rawBiographyElement.ValueKind == JsonValueKind.String)
            {
                var value = rawBiographyElement.GetString();
                biography = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            // Neither half present: negative cache entry for this slug.
            return location == null && biography == null
                ? (true, null)
                : (true, new AudiomackArtistProfile(location, biography));
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    private async Task WriteCachedProfileAsync(string slug, AudiomackArtistProfile? profile, CancellationToken cancellationToken)
    {
        var payload = profile == null
            ? "{}"
            : JsonSerializer.Serialize(new
            {
                city = profile.Location?.City,
                country = profile.Location?.Country,
                country_code = profile.Location?.CountryCode,
                location_source = "audiomack",
                raw_location = profile.Location?.RawLocation,
                biography = profile.Biography,
                raw_biography = profile.Biography
            });

        await _artistPageCache.UpsertAsync(LocationCacheSource, slug, payload, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }
}