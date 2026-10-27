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
    private readonly ArtistMetadataProviderGate _providerGate;
    private readonly ArtistPageCacheRepository? _completionCache;

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
        DeezSpoTagSettingsService? settingsService = null,
        ArtistMetadataProviderGate? providerGate = null, ArtistPageCacheRepository? completionCache = null)
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
        _providerGate = providerGate ?? new ArtistMetadataProviderGate(logger);
        _completionCache = completionCache;
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
        var rejected = await GetRejectedArtworkKeysAsync(artist.Id, cancellationToken);
        cached = cached.Where(item => !rejected.Contains(item.Identity)
            && (item.LocalPath is null || !rejected.Contains(Path.GetFullPath(item.LocalPath)))
            && (item.ContentHash is null || !rejected.Contains(item.ContentHash))).ToList();
        var blockedHashes = cached.Where(item => item.UserBlocked && !string.IsNullOrWhiteSpace(item.ContentHash))
            .Select(item => item.ContentHash!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usable = cached.Where(item => !item.UserBlocked && !blockedHashes.Contains(item.ContentHash ?? "")
            && IsUsableArtworkFile(item.LocalPath)
            && (string.IsNullOrWhiteSpace(item.OriginalUrl) || DeezerImageUrlValidator.IsAllowedDeezerImageUrl(item.OriginalUrl))).ToList();
        var visuals = usable
            .GroupBy(item => item.ContentHash ?? item.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(item => new ArtistArtworkVisual(item.Source ?? "unknown", item.Identity, item.OriginalUrl,
                item.LocalPath!, BuildLocalUrl(item.LocalPath!), item.Width, item.Height, item.ContentHash))
            .ToList();

        // Disk-only legacy images participate in the same eligibility decision as catalog rows.
        visuals.AddRange(ScanUncataloguedVisuals(artist.Id, visuals, cached, excludeTextArt: false, rejected));
        visuals = visuals.GroupBy(item => item.ContentHash ?? item.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToList();
        if (excludeTextArt)
        {
            var textFree = visuals.Where(visual => !ContainsText(visual)).ToList();
            if (textFree.Count > 0) visuals = textFree;
        }
        return new ArtistArtworkCatalogResult(artist.Id, artist.Name, visuals, providerResults);

        bool ContainsText(ArtistArtworkVisual visual)
        {
            var known = usable.FirstOrDefault(item => string.Equals(item.LocalPath, visual.Path, StringComparison.OrdinalIgnoreCase));
            if (known is not null) return known.TextArtBlocked;
            try
            {
                using var image = Image.Load(visual.Path);
                return ArtistArtworkTextInspector.LikelyContainsOverlayText(image);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnknownImageFormatException or InvalidImageContentException)
            { return true; }
        }
    }

    internal static bool IsUsableArtworkFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            || DeezerImageUrlValidator.IsArtistPlaceholderFile(path)) return false;
        try
        {
            using var image = Image.Load(path);
            return image.Width >= 128 && image.Height >= 128;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnknownImageFormatException or InvalidImageContentException)
        { return false; }
    }

    private static readonly string[] CachedImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif"];

    private List<ArtistArtworkVisual> ScanUncataloguedVisuals(
        long artistId,
        List<ArtistArtworkVisual> existing,
        IReadOnlyList<ArtistArtworkCacheDto> catalogEntries,
        bool excludeTextArt, HashSet<string> rejected)
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

        blockedKeys.UnionWith(rejected);
        var scanned = new List<ArtistArtworkVisual>();
        if (!Directory.Exists(_cacheRoot))
        {
            return scanned;
        }

        foreach (var providerDir in Directory.EnumerateDirectories(_cacheRoot))
        {
            var artistDir = Path.Join(providerDir, "artists", artistId.ToString());
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
                    || !IsUsableArtworkFile(fullPath))
                {
                    continue;
                }

                var contentHash = TryReadContentHash(fullPath);
                if (contentHash is null || blockedKeys.Contains(contentHash)) continue;
                scanned.Add(new ArtistArtworkVisual(
                    provider,
                    file,
                    null,
                    file,
                    BuildLocalUrl(file),
                    null,
                    null,
                    contentHash));
            }
        }

        return scanned
            .OrderBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TryReadContentHash(string fullPath)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return hash;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
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
        var lookupNames = NormalizeLookupNames(artistName, artistLookupNames);
        var gate = providerGate ?? _providerGate;
        var results = new List<ArtistArtworkProviderResult>();
        await ResolveLocalAsync(artistId, localImagePath, cancellationToken);
        foreach (var provider in GetRequestedProviders(onlyProvider))
        {
            try
            {
                if (!forceProviderRefresh && await HasCompleteArtworkAsync(artistId, provider, cancellationToken))
                {
                    results.Add(new(provider, true, 0, "Cached artwork collection is complete.",
                        new(provider, "artwork", ArtistCacheWorkStatus.Succeeded)));
                    continue;
                }
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { provider };
                var rematched = await EnsureMatchedSourceIdsAsync(artistId, artistName, cancellationToken, lookupNames, allowed, gate);
                await gate.RunRequiredAsync(provider, async token =>
                {
                    await RevalidateStoredPlatformArtistIdsAsync(artistId, artistName, rematched,
                        source => allowed.Contains(source), token);
                    return true;
                }, cancellationToken);
                var resolution = await gate.RunRequiredAsync(provider,
                    token => RunProviderAsync(artistId, provider,
                        inner => ResolveRemoteProviderAsync(provider, artistId, artistName, includeGallery,
                            allowArtistPageScrape, inner, lookupNames, forceMetadata: true), token), cancellationToken);
                var outcome = resolution is null
                    ? new ArtistCacheWorkOutcome(provider, "artwork", ArtistCacheWorkStatus.Failed)
                    : new(provider, "artwork", resolution.Message is null
                        ? (resolution.Candidates.Count == 0 ? ArtistCacheWorkStatus.NoData : ArtistCacheWorkStatus.Succeeded)
                        : ArtistCacheWorkStatus.Failed, resolution.Candidates.Count, resolution.CachedCount, resolution.Message);
                results.Add(new(provider, outcome.IsComplete, outcome.CachedCount, resolution?.Message, outcome));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var status = ex switch
                {
                    TimeoutException => ArtistCacheWorkStatus.TimedOut,
                    HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => ArtistCacheWorkStatus.AuthenticationRequired,
                    _ when ArtistMetadataProviderGate.IsRateLimited(ex) => ArtistCacheWorkStatus.Deferred,
                    _ => ArtistCacheWorkStatus.Failed
                };
                _logger.LogWarning(ex, "Artist {ArtistId} artwork from {Provider} remains unresolved.", artistId, provider);
                results.Add(new(provider, false, 0, ex.Message, new(provider, "artwork", status, ReasonCode: ex.GetType().Name)));
            }
        }
        return results;
    }

    private async Task<HashSet<string>> GetRejectedArtworkKeysAsync(long artistId, CancellationToken token)
    {
        if (_completionCache is null) return new(StringComparer.OrdinalIgnoreCase);
        var row = await _completionCache.TryGetRequiredAsync("artist-artwork-rejected", artistId.ToString(), token);
        if (row is null) return new(StringComparer.OrdinalIgnoreCase);
        return new(JsonSerializer.Deserialize<string[]>(row.PayloadJson) ?? [], StringComparer.OrdinalIgnoreCase);
    }

    private Task RejectProvenSourceArtworkAsync(long artistId, string provider, string sourceId, CancellationToken token)
        => RejectSourceArtworkAsync(_repository, _completionCache
            ?? throw new InvalidOperationException("Artwork identity evidence storage is unavailable."), artistId, provider, sourceId, new HashSet<string>(), token);

    internal static async Task RejectSourceArtworkAsync(LibraryRepository repository, ArtistPageCacheRepository cache,
        long artistId, string provider, string sourceId, IReadOnlySet<string> rejectedUrls, CancellationToken token)
    {
        var saved = await cache.TryGetRequiredAsync("artist-artwork-rejected", artistId.ToString(), token);
        var rejected = new HashSet<string>(saved is null ? [] : JsonSerializer.Deserialize<string[]>(saved.PayloadJson) ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var item in (await repository.GetArtistArtworkCacheAsync(artistId, token))
            .Where(item => NormalizeArtworkProvider(item.Source ?? "") == NormalizeArtworkProvider(provider)
                && (item.Identity == $"{provider}:{sourceId}" || item.Identity.StartsWith($"{provider}:{sourceId}:", StringComparison.OrdinalIgnoreCase)
                    || item.OriginalUrl is not null && rejectedUrls.Contains(item.OriginalUrl))))
        {
            rejected.Add(item.Identity);
            if (!string.IsNullOrWhiteSpace(item.LocalPath)) rejected.Add(Path.GetFullPath(item.LocalPath));
            if (!string.IsNullOrWhiteSpace(item.ContentHash)) rejected.Add(item.ContentHash);
        }
        await cache.UpsertRequiredAsync("artist-artwork-rejected", artistId.ToString(),
            JsonSerializer.Serialize(rejected), DateTimeOffset.UtcNow, token);
    }

    public async Task EnsureProviderIdentityAsync(long artistId, string artistName, string provider,
        IReadOnlyList<string> names, CancellationToken token)
    {
        if (provider is "lastfm" or "audiomack") return;
        if ((await _repository.GetArtistSourceIdentitiesAsync(artistId, provider, token))
            .Any(identity => identity.VerificationState is "verified" or "user_confirmed")) return;
        await EnsureMatchedSourceIdsAsync(artistId, artistName, token, names,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { provider }, _providerGate);
    }

    public IReadOnlyList<string> GetRequestedProviders(string? onlyProvider)
        => ResolveArtistArtworkSourceOrder().Select(NormalizeArtworkProvider)
            .Where(provider => string.IsNullOrWhiteSpace(onlyProvider)
                || provider == NormalizeArtworkProvider(onlyProvider))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string NormalizeArtworkProvider(string provider)
        => provider.Trim().ToLowerInvariant() is "itunes" ? "apple" : provider.Trim().ToLowerInvariant();

    public async Task<bool> HasCompleteArtworkAsync(long artistId, string provider, CancellationToken token)
    {
        if (_completionCache is null) return false;
        var row = await _completionCache.TryGetRequiredAsync("artist-artwork-observation", $"{artistId}:{NormalizeArtworkProvider(provider)}", token);
        if (row is null || row.FetchedUtc < DateTimeOffset.UtcNow.AddDays(-7)) return false;
        ArtistArtworkObservation? observation;
        try { observation = JsonSerializer.Deserialize<ArtistArtworkObservation>(row.PayloadJson); }
        catch (JsonException) { return false; }
        if (observation is not { Version: 1 }) return false;
        foreach (var path in observation.Paths)
        {
            if (!File.Exists(path) || DeezerImageUrlValidator.IsArtistPlaceholderFile(path)) return false;
            try { using var image = await Image.LoadAsync(path, token); if (image.Width < 128 || image.Height < 128) return false; }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not Microsoft.Data.Sqlite.SqliteException) { return false; }
        }
        return true;
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
        IReadOnlyList<string>? lookupNames = null, bool forceMetadata = false)
        => provider switch
        {
            SpotifySource => ResolveSpotifyAsync(artistId, artistName, includeGallery, cancellationToken, forceMetadata),
            DeezerSource => ResolveDeezerAsync(artistId, artistName, cancellationToken),
            AppleSource or "itunes" => ResolveItunesAsync(artistId, artistName, allowArtistPageScrape, cancellationToken, lookupNames),
            TidalSource => ResolveTidalAsync(artistId, cancellationToken),
            QobuzSource => ResolveQobuzAsync(artistId, cancellationToken),
            "lastfm" => ResolveLastFmAsync(artistName, includeGallery, cancellationToken, lookupNames,
                async (available, ct) =>
                {
                    foreach (var image in available)
                        await CacheCandidateAsync(artistId, new RemoteCandidate("lastfm", $"lastfm:{image.Url}", image.Url, null, null), ct);
                }),
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

    private async Task<ProviderResolution> RunProviderAsync(
        long artistId,
        string provider,
        Func<CancellationToken, Task<IReadOnlyList<RemoteCandidate>>> resolve,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await resolve(cancellationToken);
            var paths = new List<string>();
            var rejected = new List<string>();
            var failures = new Dictionary<string, string>();
            async Task SaveObservation() => await (_completionCache
                ?? throw new InvalidOperationException("Artwork completion storage is unavailable."))
                .UpsertRequiredAsync("artist-artwork-observation", $"{artistId}:{NormalizeArtworkProvider(provider)}",
                    JsonSerializer.Serialize(new ArtistArtworkObservation(1, paths, candidates.Select(item => item.Identity).ToArray(), rejected, failures)),
                    DateTimeOffset.UtcNow, cancellationToken);
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? path;
                try { path = await CacheCandidateAsync(artistId, candidate, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures[candidate.Identity] = ex.GetType().Name;
                    await SaveObservation();
                    throw;
                }
                if (path is not null) paths.Add(path);
                else { rejected.Add(candidate.Identity); failures[candidate.Identity] = "invalid-or-disallowed-image"; }
            }
            await SaveObservation();
            return new ProviderResolution(provider, candidates, null, paths.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ArtistMetadataProviderGate.IsRateLimited(ex))
        {
            _logger.LogWarning(
                ex,
                "Artist artwork provider {Provider} failed for artist {ArtistId}.",
                provider,
                artistId);
            throw;
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
        CancellationToken token, bool forceMetadata = false)
    {
        var page = await _spotify.GetArtistPageAsync(
            artistId,
            artistName,
            forceMetadata,
            false,
            token,
            includeDeezerLinking: false,
            includeDiscography: false);
        if (page?.Artist is null) throw new InvalidOperationException("Spotify artist metadata could not be verified.");

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

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveStoredProfilesAsync(long artistId, string provider,
        Func<string, Task<IReadOnlyList<RemoteCandidate>>> resolve, CancellationToken token)
    {
        var candidates = new List<RemoteCandidate>();
        foreach (var identity in await _repository.GetArtistSourceIdentitiesAsync(artistId, provider, token))
        {
            token.ThrowIfCancellationRequested();
            if (identity.VerificationState is not ("verified" or "user_confirmed")) continue;
            try { candidates.AddRange(await resolve(identity.SourceId)); }
            catch (Exception ex) when (ex is not OperationCanceledException && !ArtistMetadataProviderGate.IsRateLimited(ex))
            { _logger.LogWarning(ex, "Artwork lookup failed for {Provider} profile {SourceId}.", provider, identity.SourceId); throw; }
        }
        return candidates.GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
    }

    private Task<IReadOnlyList<RemoteCandidate>> ResolveDeezerAsync(long artistId, string artistName, CancellationToken token)
        => ResolveStoredProfilesAsync(artistId, DeezerSource, async id =>
        {
            var url = await ArtworkFallbackHelper.TryResolveDeezerArtistImageByArtistIdAsync(_deezer, id, ResolveRequestSize(DeezerSource), _logger, token, required: true);
            return string.IsNullOrWhiteSpace(url) ? Array.Empty<RemoteCandidate>() : new[] { new RemoteCandidate(DeezerSource, $"deezer:{id}", url, null, null) };
        }, token);

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveItunesAsync(
        long artistId,
        string artistName,
        bool allowArtistPageScrape,
        CancellationToken token,
        IReadOnlyList<string>? lookupNames = null)
    {
        var stored = await ResolveStoredProfilesAsync(artistId, AppleSource, async appleId =>
        {
            var resolved = await _apple.ResolveByArtistIdAsync(appleId, artistName, token, allowArtistPageScrape: false);
            return string.IsNullOrWhiteSpace(resolved?.Image) ? Array.Empty<RemoteCandidate>()
                : new[] { new RemoteCandidate("itunes", $"apple:{appleId}", resolved.Image, null, null) };
        }, token);
        if (stored.Count > 0) return stored;

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

    private Task<IReadOnlyList<RemoteCandidate>> ResolveQobuzAsync(long artistId, CancellationToken token)
        => ResolveStoredProfilesAsync(artistId, QobuzSource, async stored =>
        {
            if (!int.TryParse(stored, out var id) || id <= 0) return Array.Empty<RemoteCandidate>();
            var artist = await _qobuz.GetArtistAsync(id, "us-en", token, forceRefresh: true);
            var url = FirstNonEmpty(artist?.Image?.Mega, artist?.Image?.ExtraLarge, artist?.Image?.Large, artist?.Image?.Medium);
            return string.IsNullOrWhiteSpace(url) ? Array.Empty<RemoteCandidate>() : new[] { new RemoteCandidate(QobuzSource, $"qobuz:{id}", url, null, null) };
        }, token);

    private Task<IReadOnlyList<RemoteCandidate>> ResolveTidalAsync(long artistId, CancellationToken token)
        => ResolveStoredProfilesAsync(artistId, TidalSource, id => ResolveTidalProfileAsync(id, token), token);

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveTidalProfileAsync(string stored, CancellationToken token)
    {
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
                    "Tidal profileArt lookup rejected with HTTP {StatusCode} for profile {SourceId}; check Tidal credentials/openapi access.",
                    (int)response.StatusCode,
                    stored);
            }

            response.EnsureSuccessStatusCode();
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!TryFindTidalArtworkHref(document.RootElement, out var href)) return Array.Empty<RemoteCandidate>();
        return new[] { new RemoteCandidate(TidalSource, $"tidal:{stored}", href, null, null) };
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveLastFmAsync(
        string artistName,
        bool includeGallery,
        CancellationToken token,
        IReadOnlyList<string>? lookupNames = null,
        Func<IReadOnlyList<LastFmArtistImageCandidate>, CancellationToken, Task>? retainCandidates = null)
    {
        // Last.fm is name-only: it has no persisted identity, so each known lookup name is
        // collected independently. Each attempt keeps Last.fm's own returned-name
        // validation, so an autocorrected redirect to an unrelated artist is rejected instead
        // of ending the search before the alias is tried.
        var all = new List<RemoteCandidate>();
        foreach (var name in lookupNames is { Count: > 1 } ? lookupNames : [artistName])
        {
            var candidates = await _lastFm.SearchArtistImagesAsync(name, includeGallery ? 8 : 1, token, propagateRateLimits: true,
                retainCandidates: retainCandidates);
            var mapped = candidates
                .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .Select(x => new RemoteCandidate("lastfm", $"lastfm:{x.Url}", x.Url, null, null))
                .ToList();
            all.AddRange(mapped);
        }

        return all.GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
    }

    private async Task<string?> CacheCandidateAsync(long artistId, RemoteCandidate candidate, CancellationToken token)
    {
        if (!DeezerImageUrlValidator.IsAllowedDeezerImageUrl(candidate.Url)) return null;
        if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var directory = Path.Join(_cacheRoot, candidate.Provider, "artists", artistId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var urlHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
        string? existing = null;
        foreach (var path in Directory.GetFiles(directory, $"{urlHash}.*").Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
        {
            existing ??= path;
            var valid = await CacheLocalAsync(artistId, candidate.Provider, candidate.Identity, path, candidate.Url, token);
            if (valid is not null) return valid;
        }
        var temp = Path.Join(directory, $".{urlHash}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var response = await _httpClients.CreateClient().GetAsync(uri, token);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } finalUri && !DeezerImageUrlValidator.IsAllowedDeezerImageUrl(finalUri.AbsoluteUri)
                || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            {
                return null;
            }
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(temp)) { await input.CopyToAsync(output, token); }
            using var image = await Image.LoadAsync(temp, token);
            if (image.Width < 128 || image.Height < 128 || DeezerImageUrlValidator.IsArtistPlaceholder(image)) return null;
            var extension = ImageFileExtensionResolver.ResolveStandardImageExtension(response.Content.Headers.ContentType.MediaType, uri.AbsoluteUri);
            var final = Path.Join(directory, existing is null ? $"{urlHash}{extension}" : $"{urlHash}.{Guid.NewGuid():N}{extension}");
            File.Move(temp, final, overwrite: false);
            return await CacheLocalAsync(artistId, candidate.Provider, candidate.Identity, final, candidate.Url, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to cache {Provider} artwork for artist {ArtistId}.", candidate.Provider, artistId);
            throw;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task<string?> CacheLocalAsync(long artistId, string provider, string identity, string path, string? originalUrl, CancellationToken token)
    {
        try
        {
            using var image = await Image.LoadAsync(path, token);
            if (image.Width < 128 || image.Height < 128 || DeezerImageUrlValidator.IsArtistPlaceholder(image)) return null;
            await using var hashStream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, token)).ToLowerInvariant();
            var textArtBlocked = ArtistArtworkTextInspector.LikelyContainsOverlayText(image);
            // A provider can change bytes/URL while retaining its profile identity. Keep the
            // old version and its block association instead of orphaning a blocked disk file.
            var prior = (await _repository.GetArtistArtworkCacheAsync(artistId, token))
                .FirstOrDefault(item => item.Identity == identity && item.Role == CandidateRole);
            if (prior is not null && !string.Equals(prior.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
                identity = $"{identity}:image:{hash}";
            await _repository.UpsertArtistArtworkCacheAsync(new ArtistArtworkCacheUpsertInput(
                artistId, CandidateRole, identity, provider, originalUrl, Path.GetFullPath(path), hash,
                image.Width, image.Height, "heuristic", null, textArtBlocked, false), token);
            return path;
        }
        catch (Exception ex) when (ex is SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException or IOException)
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
public sealed record ArtistArtworkProviderResult(string Provider, bool Success, int CachedCount, string? Message,
    ArtistCacheWorkOutcome? Outcome = null);
public sealed record ArtistArtworkObservation(int Version, IReadOnlyList<string> Paths,
    IReadOnlyList<string> CandidateIdentities, IReadOnlyList<string> RejectedIdentities,
    IReadOnlyDictionary<string, string>? CandidateReasons = null);
