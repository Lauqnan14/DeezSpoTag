using System.Security.Cryptography;
using System.Text.Json;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Metadata.Qobuz;
using DeezSpoTag.Services.Settings;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;

namespace DeezSpoTag.Web.Services;

public sealed partial class ArtistArtworkCatalogService
{
    private const string CandidateRole = "candidate";

    /// <summary>
    ///     The engine ids below are aliased to the one canonical definition.
    /// </summary>
    /// <remarks>
    ///     Every use is either a switch that selects a resolver or a key an artist source id is
    ///     stored and read back under. A copy that drifted from the vocabulary the rows were
    ///     written with would fall through its switch to the default arm and read back no id, so the
    ///     engine would silently contribute no artwork at all. The names are kept local because they
    ///     read better at the dispatch sites; the value is defined once.
    /// </remarks>
    private const string AppleSource = DownloadTagSourceHelper.AppleSource;

    private const string DeezerSource = DownloadTagSourceHelper.DeezerSource;

    private const string SpotifySource = DownloadTagSourceHelper.SpotifySource;

    private const string TidalSource = DownloadTagSourceHelper.TidalSource;

    private const string QobuzSource = DownloadTagSourceHelper.QobuzSource;

    /// <summary>
    /// Artist image sources offered by the artwork-order control, in its default order. Kept in
    /// step with ARTIST_ARTWORK_SOURCE_ORDER in autotag.js.
    /// </summary>
    private static readonly string[] DefaultArtistArtworkSourceOrder =
        [AppleSource, DeezerSource, SpotifySource, "lastfm"];
    private readonly LibraryRepository _repository;
    private readonly SpotifyArtistService _spotify;
    private readonly DeezerClient _deezer;
    private readonly ITidalAccessTokenProvider _tidalTokens;
    private readonly QobuzArtistService _qobuz;
    private readonly IQobuzMetadataService _qobuzMetadata;
    private readonly AppleArtistBiographyService _apple;
    private readonly LastFmArtistImageService _lastFm;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<ArtistArtworkCatalogService> _logger;
    private readonly string _cacheRoot;

    private readonly DeezSpoTagSettingsService? _settingsService;

    private int ResolveRequestSize(string provider)
    {
        // Artist artwork always asks for the largest size the provider serves, so this is
        // independent of the album artwork size settings.
        return DeezSpoTag.Services.Download.Shared.ArtworkSizePolicy.ResolveRequestSize(
            AppleQueueHelpers.ArtistArtworkSize,
            provider);
    }

    public ArtistArtworkCatalogService(
        LibraryRepository repository,
        SpotifyArtistService spotify,
        DeezerClient deezer,
        ITidalAccessTokenProvider tidalTokens,
        QobuzArtistService qobuz,
        IQobuzMetadataService qobuzMetadata,
        AppleArtistBiographyService apple,
        LastFmArtistImageService lastFm,
        IHttpClientFactory httpClients,
        IWebHostEnvironment environment,
        ILogger<ArtistArtworkCatalogService> logger,
        DeezSpoTagSettingsService? settingsService = null)
    {
        _settingsService = settingsService;
        _repository = repository;
        _spotify = spotify;
        _deezer = deezer;
        _tidalTokens = tidalTokens;
        _qobuz = qobuz;
        _qobuzMetadata = qobuzMetadata;
        _apple = apple;
        _lastFm = lastFm;
        _httpClients = httpClients;
        _logger = logger;
        _cacheRoot = Path.Join(AppDataPaths.GetDataRoot(environment), "library-artist-images", "providers");
    }

    public async Task<ArtistArtworkCatalogResult> GetAsync(
        long artistId,
        CancellationToken cancellationToken,
        bool excludeTextArt = true)
    {
        var artist = await _repository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return new ArtistArtworkCatalogResult(artistId, string.Empty, Array.Empty<ArtistArtworkVisual>(), Array.Empty<ArtistArtworkProviderResult>());
        }

        var providerResults = Array.Empty<ArtistArtworkProviderResult>();

        var cached = await _repository.GetArtistArtworkCacheAsync(artist.Id, cancellationToken);
        var visuals = cached
            .Where(item => !item.UserBlocked
                && (!excludeTextArt || !item.TextArtBlocked)
                && !string.IsNullOrWhiteSpace(item.LocalPath)
                && File.Exists(item.LocalPath))
            .GroupBy(item => item.ContentHash ?? item.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(item => new ArtistArtworkVisual(
                item.Source ?? "unknown",
                item.Identity,
                item.OriginalUrl,
                item.LocalPath!,
                BuildLocalUrl(item.LocalPath!),
                item.Width,
                item.Height,
                item.ContentHash))
            .ToList();

        // Once an image is cached on disk it must always be selectable, regardless
        // of what the metadata rotation or a platform refresh did to the catalog
        // rows. Merge every cached file under the artist's provider folders that
        // the catalog does not already list. Explicitly blocked images stay out.
        visuals.AddRange(ScanUncataloguedVisuals(artist.Id, visuals, cached, excludeTextArt));
        return new ArtistArtworkCatalogResult(artist.Id, artist.Name, visuals, providerResults);
    }

    private static readonly string[] CachedImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif"];

    private List<ArtistArtworkVisual> ScanUncataloguedVisuals(
        long artistId,
        List<ArtistArtworkVisual> existing,
        IReadOnlyList<ArtistArtworkCacheDto> catalogEntries,
        bool excludeTextArt)
    {
        var knownPaths = existing
            .Select(item => string.IsNullOrWhiteSpace(item.Path) ? string.Empty : Path.GetFullPath(item.Path))
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Honour blocks recorded against a hash, identity or path even when the
        // catalog row itself is no longer listed.
        var blockedKeys = catalogEntries
            .Where(item => item.UserBlocked || (excludeTextArt && item.TextArtBlocked))
            .SelectMany(item =>
            {
                var localPath = string.IsNullOrWhiteSpace(item.LocalPath)
                    ? null
                    : Path.GetFullPath(item.LocalPath);
                return new[]
                {
                    item.ContentHash,
                    item.Identity,
                    string.IsNullOrWhiteSpace(item.LocalPath) ? null : Path.GetFileName(item.LocalPath),
                    string.IsNullOrWhiteSpace(item.LocalPath) ? null : Path.GetFileNameWithoutExtension(item.LocalPath),
                    localPath
                };
            })
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scanned = new List<ArtistArtworkVisual>();
        if (!Directory.Exists(_cacheRoot))
        {
            return scanned;
        }

        foreach (var providerDir in Directory.EnumerateDirectories(_cacheRoot))
        {
            var artistDir = Path.Join(providerDir, artistId.ToString());
            if (!Directory.Exists(artistDir))
            {
                continue;
            }

            var provider = Path.GetFileName(providerDir);
            foreach (var file in Directory.EnumerateFiles(artistDir))
            {
                var extension = Path.GetExtension(file);
                var fullPath = Path.GetFullPath(file);
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (!CachedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                    || knownPaths.Contains(fullPath)
                    || blockedKeys.Contains(fileName)
                    || blockedKeys.Contains(Path.GetFileName(file))
                    || blockedKeys.Contains(fullPath)
                    || BlockedByContentHash(fullPath, blockedKeys))
                {
                    continue;
                }

                scanned.Add(new ArtistArtworkVisual(
                    provider,
                    file,
                    null,
                    file,
                    BuildLocalUrl(file),
                    null,
                    null,
                    fileName));
            }
        }

        return scanned
            .OrderBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool BlockedByContentHash(string fullPath, HashSet<string> blockedKeys)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return blockedKeys.Contains(hash);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ArtistArtworkProviderResult>> RefreshAsync(
        long artistId,
        string artistName,
        string? localImagePath,
        CancellationToken cancellationToken,
        string? onlyProvider = null,
        bool forceProviderRefresh = false,
        ArtistMetadataProviderGate? providerGate = null,
        bool includeGallery = true,
        bool allowArtistPageScrape = true,
        IReadOnlyList<string>? artistLookupNames = null)
    {
        // Ordered provider lookup names for an Artist Alias artist (canonical/preferred first,
        // then aliases). Null or a single entry keeps every existing caller on today's path.
        var lookupNames = NormalizeLookupNames(artistName, artistLookupNames);
        var rematchedProviders = await EnsureMatchedSourceIdsAsync(artistId, artistName, cancellationToken, lookupNames);
        var existing = await _repository.GetArtistArtworkCacheAsync(artistId, cancellationToken);
        var staleBefore = DateTimeOffset.UtcNow.AddDays(-7);
        bool HasFreshCachedArtwork(string provider) => existing.Any(item =>
            string.Equals(item.Source, provider, StringComparison.OrdinalIgnoreCase)
            && !item.UserBlocked
            && !item.TextArtBlocked
            && !string.IsNullOrWhiteSpace(item.LocalPath)
            && File.Exists(item.LocalPath)
            && DateTimeOffset.TryParse(item.LastSeenAt, out var seen)
            && seen >= staleBefore);
        if (!forceProviderRefresh)
        {
            // When a provider is about to be queried anyway (stale/absent cache), the
            // stored platform id is revalidated against local album titles first.
            await RevalidateStoredPlatformArtistIdsAsync(
                artistId,
                artistName,
                rematchedProviders,
                provider => !HasFreshCachedArtwork(provider),
                cancellationToken);
        }

        bool NeedsRefresh(string provider) => forceProviderRefresh
            || rematchedProviders.Contains(provider)
            || !HasFreshCachedArtwork(provider);
        bool Includes(string provider) => string.IsNullOrWhiteSpace(onlyProvider)
            || string.Equals(provider, onlyProvider, StringComparison.OrdinalIgnoreCase)
            || string.Equals(onlyProvider, AppleSource, StringComparison.OrdinalIgnoreCase)
               && string.Equals(provider, "itunes", StringComparison.OrdinalIgnoreCase);

        var gate = providerGate ?? new ArtistMetadataProviderGate(_logger);
        var resolutions = new List<ProviderResolution>
        {
            await ResolveLocalAsync(artistId, localImagePath, cancellationToken)
        };

        // Query the remote providers in the order the user configured. The artist order is
        // independent of the album order, but only carries providers the artwork-order control
        // actually offers; anything the user did not select is not queried at all.
        foreach (var provider in ResolveArtistArtworkSourceOrder())
        {
            await AddRemoteProviderAsync(
                resolutions,
                gate,
                provider,
                Includes,
                NeedsRefresh,
                token => RunProviderAsync(
                    artistId,
                    provider,
                    inner => ResolveRemoteProviderAsync(provider, artistId, artistName, includeGallery, allowArtistPageScrape, inner, lookupNames),
                    token),
                cancellationToken);
        }
        var results = new List<ArtistArtworkProviderResult>(resolutions.Count);
        foreach (var resolution in resolutions)
        {
            if (resolution.Candidates.Count == 0)
            {
                if (resolution.Skipped)
                {
                    results.Add(new ArtistArtworkProviderResult(
                        resolution.Provider,
                        true,
                        0,
                        resolution.Message));
                    continue;
                }

                var localCached = string.Equals(resolution.Provider, "local", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(resolution.Message);
                results.Add(new ArtistArtworkProviderResult(resolution.Provider, localCached, localCached ? 1 : 0, resolution.Message));
                continue;
            }

            results.Add(new ArtistArtworkProviderResult(
                resolution.Provider,
                resolution.CachedCount > 0,
                resolution.CachedCount,
                resolution.CachedCount > 0 ? null : resolution.Message ?? "No valid artwork could be cached."));
        }

        return results;
    }

    /// <summary>
    /// The remote providers to query, in the user's configured order.
    ///
    /// The artwork-order control offers artist image sources independently of the album cover
    /// sources, and only those sources are queried. When no artist order is stored the album
    /// order is inherited, matching every other artwork selection stage.
    /// </summary>
    private IReadOnlyList<string> ResolveArtistArtworkSourceOrder()
    {
        var settings = _settingsService?.LoadSettings();
        if (settings is null)
        {
            return DefaultArtistArtworkSourceOrder;
        }

        return ArtworkFallbackHelper.ResolveArtistOrder(settings);
    }

    private Task<IReadOnlyList<RemoteCandidate>> ResolveRemoteProviderAsync(
        string provider,
        long artistId,
        string artistName,
        bool includeGallery,
        bool allowArtistPageScrape,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? lookupNames = null)
        => provider switch
        {
            SpotifySource => ResolveSpotifyAsync(artistId, artistName, includeGallery, cancellationToken),
            DeezerSource => ResolveDeezerAsync(artistId, artistName, cancellationToken),
            AppleSource or "itunes" => ResolveItunesAsync(artistId, artistName, allowArtistPageScrape, cancellationToken, lookupNames),
            TidalSource => ResolveTidalAsync(artistId, cancellationToken),
            QobuzSource => ResolveQobuzAsync(artistId, cancellationToken),
            "lastfm" => ResolveLastFmAsync(artistName, includeGallery, cancellationToken, lookupNames),
            _ => Task.FromResult<IReadOnlyList<RemoteCandidate>>(Array.Empty<RemoteCandidate>())
        };

    /// <summary>
    /// Canonical name first, then the Artist Alias names, deduplicated case-insensitively.
    /// Callers that pass nothing get a single-entry list and therefore behave exactly as before.
    /// </summary>
    private static IReadOnlyList<string> NormalizeLookupNames(string artistName, IReadOnlyList<string>? artistLookupNames)
    {
        var canonical = (artistName ?? string.Empty).Trim();
        if (artistLookupNames is not { Count: > 1 })
        {
            return [canonical];
        }

        var ordered = new List<string>(artistLookupNames.Count) { canonical };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canonical };
        foreach (var trimmed in artistLookupNames
                     .Select(name => (name ?? string.Empty).Trim())
                     .Where(trimmed => trimmed.Length > 0 && seen.Add(trimmed)))
        {
            ordered.Add(trimmed);
        }

        return ordered;
    }

    private async Task AddRemoteProviderAsync(
        List<ProviderResolution> resolutions,
        ArtistMetadataProviderGate gate,
        string provider,
        Func<string, bool> includes,
        Func<string, bool> needsRefresh,
        Func<CancellationToken, Task<ProviderResolution>> run,
        CancellationToken cancellationToken)
    {
        if (!includes(provider) || !needsRefresh(provider))
        {
            if (includes(provider))
            {
                // Make the freshness skip visible instead of silently omitting the provider.
                _logger.LogInformation(
                    "Artist artwork provider {Provider} skipped; cached artwork is fresh.",
                    provider);
                resolutions.Add(new ProviderResolution(
                    provider,
                    Array.Empty<RemoteCandidate>(),
                    "skipped; cached artwork is fresh",
                    0,
                    Skipped: true));
            }

            return;
        }

        if (gate.IsUnavailable(provider))
        {
            resolutions.Add(new ProviderResolution(provider, Array.Empty<RemoteCandidate>(), ArtistMetadataProviderGate.UnavailableMessage));
            return;
        }

        var resolution = await gate.RunAsync(provider, run, cancellationToken);
        resolutions.Add(resolution ?? new ProviderResolution(provider, Array.Empty<RemoteCandidate>(), ArtistMetadataProviderGate.UnavailableMessage));
    }

    private async Task<ProviderResolution> RunProviderAsync(
        long artistId,
        string provider,
        Func<CancellationToken, Task<IReadOnlyList<RemoteCandidate>>> resolve,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await resolve(cancellationToken);
            var cached = 0;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await CacheCandidateAsync(artistId, candidate, cancellationToken) is not null)
                {
                    cached++;
                }
            }

            return new ProviderResolution(
                provider,
                candidates,
                cached == 0 && candidates.Count > 0 ? "No valid artwork could be cached." : null,
                cached);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ArtistMetadataProviderGate.IsRateLimited(ex))
        {
            _logger.LogWarning(
                ex,
                "Artist artwork provider {Provider} failed for artist {ArtistId}.",
                provider,
                artistId);
            return new ProviderResolution(provider, Array.Empty<RemoteCandidate>(), ex.Message);
        }
    }

    private async Task<ProviderResolution> ResolveLocalAsync(long artistId, string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new ProviderResolution("local", Array.Empty<RemoteCandidate>(), "No local artist artwork.");
        }
        var cached = await CacheLocalAsync(artistId, "local", $"local:{Path.GetFullPath(path)}", path, null, cancellationToken);
        return new ProviderResolution("local", Array.Empty<RemoteCandidate>(), cached is null ? "Local artwork is invalid." : null);
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveSpotifyAsync(
        long artistId,
        string artistName,
        bool includeGallery,
        CancellationToken token)
    {
        var page = await _spotify.GetArtistPageAsync(
            artistId,
            artistName,
            false,
            false,
            token,
            includeDeezerLinking: false,
            includeDiscography: false);
        if (page?.Artist is null) return Array.Empty<RemoteCandidate>();

        var candidates = page.Artist.Images
            .Where(image => !string.IsNullOrWhiteSpace(image.Url))
            .Select(image => new RemoteCandidate(SpotifySource, $"spotify:profile:{image.Url}", image.Url, image.Width, image.Height))
            .ToList();
        AddSpotifyCandidate(candidates, page.Artist.HeaderImageUrl, "header");
        if (includeGallery)
        {
            foreach (var galleryUrl in page.Artist.Gallery)
            {
                AddSpotifyCandidate(candidates, galleryUrl, "gallery");
            }
        }

        return candidates
            .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static void AddSpotifyCandidate(List<RemoteCandidate> candidates, string? url, string kind)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            candidates.Add(new RemoteCandidate(SpotifySource, $"spotify:{kind}:{url}", url, null, null));
        }
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveDeezerAsync(long artistId, string artistName, CancellationToken token)
    {
        var deezerId = await _repository.GetArtistSourceIdAsync(artistId, DeezerSource, token);
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return Array.Empty<RemoteCandidate>();
        }

        var url = await ArtworkFallbackHelper.TryResolveDeezerArtistImageByArtistIdAsync(_deezer, deezerId, ResolveRequestSize(DeezerSource), _logger, token);
        return string.IsNullOrWhiteSpace(url) ? Array.Empty<RemoteCandidate>() : new[] { new RemoteCandidate(DeezerSource, $"deezer:{deezerId}", url, null, null) };
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveItunesAsync(
        long artistId,
        string artistName,
        bool allowArtistPageScrape,
        CancellationToken token,
        IReadOnlyList<string>? lookupNames = null)
    {
        var appleId = await _repository.GetArtistSourceIdAsync(artistId, AppleSource, token);
        if (!string.IsNullOrWhiteSpace(appleId))
        {
            var resolved = await _apple.ResolveByArtistIdAsync(appleId, artistName, token, allowArtistPageScrape: false);
            if (!string.IsNullOrWhiteSpace(resolved?.Image))
            {
                return new[] { new RemoteCandidate("itunes", $"apple:{appleId}", resolved.Image, null, null) };
            }
        }

        var titles = await LoadLocalTitlesAsync(artistId, token);
        if (titles.Count > 0)
        {
            return Array.Empty<RemoteCandidate>();
        }

        foreach (var name in lookupNames is { Count: > 1 } ? lookupNames : [artistName])
        {
            var url = await AppleQueueHelpers.ResolveItunesArtistImageAsync(
                _httpClients,
                name,
                ResolveRequestSize(AppleSource),
                _logger,
                token,
                allowArtistPageScrape);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return new[] { new RemoteCandidate("itunes", $"itunes:{url}", url, null, null) };
            }
        }

        return Array.Empty<RemoteCandidate>();
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveQobuzAsync(long artistId, CancellationToken token)
    {
        var stored = await _repository.GetArtistSourceIdAsync(artistId, QobuzSource, token);
        if (!int.TryParse(stored, out var id) || id <= 0) return Array.Empty<RemoteCandidate>();
        var artist = await _qobuz.GetArtistAsync(id, "us-en", token);
        var url = FirstNonEmpty(artist?.Image?.Mega, artist?.Image?.ExtraLarge, artist?.Image?.Large, artist?.Image?.Medium);
        return string.IsNullOrWhiteSpace(url) ? Array.Empty<RemoteCandidate>() : new[] { new RemoteCandidate(QobuzSource, $"qobuz:{id}", url, null, null) };
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveTidalAsync(long artistId, CancellationToken token)
    {
        var stored = await _repository.GetArtistSourceIdAsync(artistId, TidalSource, token);
        if (string.IsNullOrWhiteSpace(stored)) return Array.Empty<RemoteCandidate>();
        var accessToken = await _tidalTokens.GetAccessTokenAsync(token);
        var country = await _tidalTokens.GetCountryCodeAsync(token) ?? "US";
        var url = $"https://openapi.tidal.com/v2/artists/{Uri.EscapeDataString(stored)}?countryCode={Uri.EscapeDataString(country)}&include=profileArt";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClients.CreateClient().SendAsync(request, token);
        ArtistMetadataProviderGate.ThrowIfRateLimited(response);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "Tidal profileArt lookup rejected with HTTP {StatusCode} for artist {ArtistId}; check Tidal credentials/openapi access.",
                    (int)response.StatusCode,
                    artistId);
            }

            return Array.Empty<RemoteCandidate>();
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!TryFindTidalArtworkHref(document.RootElement, out var href)) return Array.Empty<RemoteCandidate>();
        return new[] { new RemoteCandidate(TidalSource, $"tidal:{stored}", href, null, null) };
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveLastFmAsync(
        string artistName,
        bool includeGallery,
        CancellationToken token,
        IReadOnlyList<string>? lookupNames = null)
    {
        // Last.fm is name-only: it has no persisted identity, so each known lookup name is
        // attempted until one yields artwork. Each attempt keeps Last.fm's own returned-name
        // validation, so an autocorrected redirect to an unrelated artist is rejected instead
        // of ending the search before the alias is tried.
        foreach (var name in lookupNames is { Count: > 1 } ? lookupNames : [artistName])
        {
            var candidates = await _lastFm.SearchArtistImagesAsync(name, includeGallery ? 8 : 1, token);
            var mapped = candidates
                .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .Select(x => new RemoteCandidate("lastfm", $"lastfm:{x.Url}", x.Url, null, null))
                .ToList();
            if (mapped.Count > 0)
            {
                return mapped;
            }
        }

        return Array.Empty<RemoteCandidate>();
    }

    private async Task<string?> CacheCandidateAsync(long artistId, RemoteCandidate candidate, CancellationToken token)
    {
        if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var directory = Path.Join(_cacheRoot, candidate.Provider, "artists", artistId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var urlHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
        var existing = Directory.GetFiles(directory, $"{urlHash}.*").FirstOrDefault(File.Exists);
        if (existing is not null) return await CacheLocalAsync(artistId, candidate.Provider, candidate.Identity, existing, candidate.Url, token);
        var temp = Path.Join(directory, $".{urlHash}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var response = await _httpClients.CreateClient().GetAsync(uri, token);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            {
                return null;
            }
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(temp)) { await input.CopyToAsync(output, token); }
            using var image = await Image.LoadAsync(temp, token);
            if (image.Width < 128 || image.Height < 128) return null;
            var extension = ImageFileExtensionResolver.ResolveStandardImageExtension(response.Content.Headers.ContentType.MediaType, uri.AbsoluteUri);
            var final = Path.Join(directory, $"{urlHash}{extension}");
            File.Move(temp, final, true);
            return await CacheLocalAsync(artistId, candidate.Provider, candidate.Identity, final, candidate.Url, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to cache {Provider} artwork for artist {ArtistId}.", candidate.Provider, artistId);
            return null;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task<string?> CacheLocalAsync(long artistId, string provider, string identity, string path, string? originalUrl, CancellationToken token)
    {
        try
        {
            using var image = await Image.LoadAsync(path, token);
            if (image.Width < 128 || image.Height < 128) return null;
            await using var hashStream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, token)).ToLowerInvariant();
            var textArtBlocked = ArtistArtworkTextInspector.LikelyContainsOverlayText(image);
            await _repository.UpsertArtistArtworkCacheAsync(new ArtistArtworkCacheUpsertInput(
                artistId, CandidateRole, identity, provider, originalUrl, Path.GetFullPath(path), hash,
                image.Width, image.Height, "heuristic", null, textArtBlocked, false), token);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Invalid cached artist artwork {Path}.", path);
            return null;
        }
    }

    private static bool TryFindTidalArtworkHref(JsonElement root, out string href)
    {
        href = string.Empty;
        if (!root.TryGetProperty("included", out var included) || included.ValueKind != JsonValueKind.Array) return false;
        foreach (var files in included.EnumerateArray()
            .Where(item => item.TryGetProperty("type", out var type)
                && type.GetString() == "artworks"
                && item.TryGetProperty("attributes", out var attributes)
                && attributes.TryGetProperty("files", out _))
            .Select(item => item.GetProperty("attributes").GetProperty("files")))
        {
            foreach (var file in files.EnumerateArray().Where(file => file.TryGetProperty("href", out var value)
                && !string.IsNullOrWhiteSpace(value.GetString())))
            {
                href = file.GetProperty("href").GetString()!;
                return true;
            }
        }
        return false;
    }

    private static string BuildLocalUrl(string path) => $"/api/library/image?path={Uri.EscapeDataString(Path.GetFullPath(path))}&size=640";
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private sealed record RemoteCandidate(string Provider, string Identity, string Url, int? Width, int? Height);
    private sealed record ProviderResolution(string Provider, IReadOnlyList<RemoteCandidate> Candidates, string? Message, int CachedCount = 0, bool Skipped = false);
}

public sealed record ArtistArtworkCatalogResult(long ArtistId, string ArtistName, IReadOnlyList<ArtistArtworkVisual> Visuals, IReadOnlyList<ArtistArtworkProviderResult> Providers);
public sealed record ArtistArtworkVisual(string Source, string Identity, string? OriginalUrl, string Path, string Url, int? Width, int? Height, string? ContentHash = null);
public sealed record ArtistArtworkProviderResult(string Provider, bool Success, int CachedCount, string? Message);
