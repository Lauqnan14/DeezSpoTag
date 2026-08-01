using System.Security.Cryptography;
using System.Text.Json;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Metadata.Qobuz;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistArtworkCatalogService
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(20);
    private const string CandidateRole = "candidate";
    private readonly LibraryRepository _repository;
    private readonly SpotifyArtistService _spotify;
    private readonly DeezerClient _deezer;
    private readonly ITidalAccessTokenProvider _tidalTokens;
    private readonly QobuzArtistService _qobuz;
    private readonly LastFmArtistImageService _lastFm;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<ArtistArtworkCatalogService> _logger;
    private readonly string _cacheRoot;

    public ArtistArtworkCatalogService(
        LibraryRepository repository,
        SpotifyArtistService spotify,
        DeezerClient deezer,
        ITidalAccessTokenProvider tidalTokens,
        QobuzArtistService qobuz,
        LastFmArtistImageService lastFm,
        IHttpClientFactory httpClients,
        IWebHostEnvironment environment,
        ILogger<ArtistArtworkCatalogService> logger)
    {
        _repository = repository;
        _spotify = spotify;
        _deezer = deezer;
        _tidalTokens = tidalTokens;
        _qobuz = qobuz;
        _lastFm = lastFm;
        _httpClients = httpClients;
        _logger = logger;
        _cacheRoot = Path.Join(AppDataPaths.GetDataRoot(environment), "library-artist-images", "providers");
    }

    public async Task<ArtistArtworkCatalogResult> GetAsync(long artistId, CancellationToken cancellationToken)
    {
        var artist = await _repository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return new ArtistArtworkCatalogResult(artistId, string.Empty, Array.Empty<ArtistArtworkVisual>(), Array.Empty<ArtistArtworkProviderResult>());
        }

        var providerResults = Array.Empty<ArtistArtworkProviderResult>();

        var cached = await _repository.GetArtistArtworkCacheAsync(artist.Id, cancellationToken);
        var visuals = cached
            .Where(item => !item.UserBlocked && !item.TextArtBlocked && !string.IsNullOrWhiteSpace(item.LocalPath) && File.Exists(item.LocalPath))
            .GroupBy(item => item.ContentHash ?? item.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(item => new ArtistArtworkVisual(
                item.Source ?? "unknown",
                item.Identity,
                item.OriginalUrl,
                item.LocalPath!,
                BuildLocalUrl(item.LocalPath!),
                item.Width,
                item.Height))
            .ToList();
        return new ArtistArtworkCatalogResult(artist.Id, artist.Name, visuals, providerResults);
    }

    public async Task<IReadOnlyList<ArtistArtworkProviderResult>> RefreshAsync(
        long artistId,
        string artistName,
        string? localImagePath,
        CancellationToken cancellationToken,
        string? onlyProvider = null,
        bool forceProviderRefresh = false)
    {
        var existing = await _repository.GetArtistArtworkCacheAsync(artistId, cancellationToken);
        var staleBefore = DateTimeOffset.UtcNow.AddDays(-7);
        bool NeedsRefresh(string provider) => forceProviderRefresh || !existing.Any(item =>
            string.Equals(item.Source, provider, StringComparison.OrdinalIgnoreCase)
            && !item.UserBlocked
            && !item.TextArtBlocked
            && !string.IsNullOrWhiteSpace(item.LocalPath)
            && File.Exists(item.LocalPath)
            && DateTimeOffset.TryParse(item.LastSeenAt, out var seen)
            && seen >= staleBefore);
        bool Includes(string provider) => string.IsNullOrWhiteSpace(onlyProvider)
            || string.Equals(provider, onlyProvider, StringComparison.OrdinalIgnoreCase)
            || string.Equals(onlyProvider, "apple", StringComparison.OrdinalIgnoreCase)
               && string.Equals(provider, "itunes", StringComparison.OrdinalIgnoreCase);

        var providers = new List<Task<ProviderResolution>>
        {
            ResolveLocalAsync(artistId, localImagePath, cancellationToken)
        };
        if (Includes("spotify") && NeedsRefresh("spotify")) providers.Add(RunProviderAsync(artistId, "spotify", token => ResolveSpotifyAsync(artistId, artistName, token), cancellationToken));
        if (Includes("deezer") && NeedsRefresh("deezer")) providers.Add(RunProviderAsync(artistId, "deezer", token => ResolveDeezerAsync(artistId, artistName, token), cancellationToken));
        if (Includes("itunes") && NeedsRefresh("itunes")) providers.Add(RunProviderAsync(artistId, "itunes", token => ResolveItunesAsync(artistName, token), cancellationToken));
        if (Includes("tidal") && NeedsRefresh("tidal")) providers.Add(RunProviderAsync(artistId, "tidal", token => ResolveTidalAsync(artistId, token), cancellationToken));
        if (Includes("qobuz") && NeedsRefresh("qobuz")) providers.Add(RunProviderAsync(artistId, "qobuz", token => ResolveQobuzAsync(artistId, token), cancellationToken));
        if (Includes("lastfm") && NeedsRefresh("lastfm")) providers.Add(RunProviderAsync(artistId, "lastfm", token => ResolveLastFmAsync(artistName, token), cancellationToken));
        var resolutions = await Task.WhenAll(providers);
        var results = new List<ArtistArtworkProviderResult>(resolutions.Length);
        foreach (var resolution in resolutions)
        {
            if (resolution.Candidates.Count == 0)
            {
                var localCached = string.Equals(resolution.Provider, "local", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(resolution.Message);
                results.Add(new ArtistArtworkProviderResult(resolution.Provider, localCached, localCached ? 1 : 0, resolution.Message));
                continue;
            }

            var cached = 0;
            foreach (var candidate in resolution.Candidates)
            {
                if (await CacheCandidateAsync(artistId, candidate, cancellationToken) is not null)
                {
                    cached++;
                }
            }
            results.Add(new ArtistArtworkProviderResult(resolution.Provider, cached > 0, cached, cached > 0 ? null : "No valid artwork could be cached."));
        }

        return results;
    }

    private async Task<ProviderResolution> RunProviderAsync(
        long artistId,
        string provider,
        Func<CancellationToken, Task<IReadOnlyList<RemoteCandidate>>> resolve,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProviderTimeout);
        try
        {
            return new ProviderResolution(provider, await resolve(timeout.Token), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderResolution(provider, Array.Empty<RemoteCandidate>(), "Timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Artist artwork provider {Provider} failed for artist {ArtistId}.", provider, artistId);
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

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveSpotifyAsync(long artistId, string artistName, CancellationToken token)
    {
        var page = await _spotify.GetArtistPageAsync(artistId, artistName, false, false, token);
        if (page?.Artist is null) return Array.Empty<RemoteCandidate>();

        var candidates = page.Artist.Images
            .Where(image => !string.IsNullOrWhiteSpace(image.Url))
            .Select(image => new RemoteCandidate("spotify", $"spotify:{image.Url}", image.Url!, image.Width, image.Height))
            .ToList();
        AddSpotifyCandidate(candidates, page.Artist.HeaderImageUrl);
        foreach (var galleryUrl in page.Artist.Gallery)
        {
            AddSpotifyCandidate(candidates, galleryUrl);
        }

        return candidates
            .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static void AddSpotifyCandidate(List<RemoteCandidate> candidates, string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            candidates.Add(new RemoteCandidate("spotify", $"spotify:{url}", url, null, null));
        }
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveDeezerAsync(long artistId, string artistName, CancellationToken token)
    {
        var stored = await _repository.GetArtistSourceIdAsync(artistId, "deezer", token);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            var url = await ArtworkFallbackHelper.TryResolveDeezerArtistImageByArtistIdAsync(_deezer, stored, 1200, _logger, token);
            return string.IsNullOrWhiteSpace(url) ? Array.Empty<RemoteCandidate>() : new[] { new RemoteCandidate("deezer", $"deezer:{stored}", url!, null, null) };
        }

        var search = await _deezer.SearchArtistAsync(artistName, new ApiOptions { Limit = 10, Strict = true }).WaitAsync(token);
        foreach (var raw in search.Data ?? Array.Empty<object>())
        {
            var obj = raw as JObject ?? (raw is JToken tokenValue ? tokenValue as JObject : null);
            var name = obj?["name"]?.Value<string>()?.Trim();
            var id = obj?["id"]?.ToString()?.Trim();
            if (!string.Equals(name, artistName.Trim(), StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(id)) continue;
            var url = obj?["picture_xl"]?.Value<string>() ?? obj?["picture_big"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(url)) continue;
            await _repository.UpsertArtistSourceIdAsync(artistId, "deezer", id, token);
            return new[] { new RemoteCandidate("deezer", $"deezer:{id}", url, null, null) };
        }
        return Array.Empty<RemoteCandidate>();
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveItunesAsync(string artistName, CancellationToken token)
    {
        var url = await AppleQueueHelpers.ResolveItunesArtistImageAsync(_httpClients, artistName, 1200, _logger, token);
        return string.IsNullOrWhiteSpace(url) ? Array.Empty<RemoteCandidate>() : new[] { new RemoteCandidate("itunes", $"itunes:{url}", url, null, null) };
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveQobuzAsync(long artistId, CancellationToken token)
    {
        var stored = await _repository.GetArtistSourceIdAsync(artistId, "qobuz", token);
        if (!int.TryParse(stored, out var id) || id <= 0) return Array.Empty<RemoteCandidate>();
        var artist = await _qobuz.GetArtistWithDiscographyAsync(id, "us-en", token);
        var url = FirstNonEmpty(artist?.Image?.Mega, artist?.Image?.ExtraLarge, artist?.Image?.Large, artist?.Image?.Medium);
        return string.IsNullOrWhiteSpace(url) ? Array.Empty<RemoteCandidate>() : new[] { new RemoteCandidate("qobuz", $"qobuz:{id}", url!, null, null) };
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveTidalAsync(long artistId, CancellationToken token)
    {
        var stored = await _repository.GetArtistSourceIdAsync(artistId, "tidal", token);
        if (string.IsNullOrWhiteSpace(stored)) return Array.Empty<RemoteCandidate>();
        var accessToken = await _tidalTokens.GetAccessTokenAsync(token);
        var country = await _tidalTokens.GetCountryCodeAsync(token) ?? "US";
        var url = $"https://openapi.tidal.com/v2/artists/{Uri.EscapeDataString(stored)}?countryCode={Uri.EscapeDataString(country)}&include=profileArt";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClients.CreateClient().SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return Array.Empty<RemoteCandidate>();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!TryFindTidalArtworkHref(document.RootElement, out var href)) return Array.Empty<RemoteCandidate>();
        return new[] { new RemoteCandidate("tidal", $"tidal:{stored}", href, null, null) };
    }

    private async Task<IReadOnlyList<RemoteCandidate>> ResolveLastFmAsync(string artistName, CancellationToken token)
        => (await _lastFm.SearchArtistImagesAsync(artistName, 8, token))
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .Select(x => new RemoteCandidate("lastfm", $"lastfm:{x.Url}", x.Url, null, null)).ToList();

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
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true) return null;
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
            await _repository.UpsertArtistArtworkCacheAsync(new ArtistArtworkCacheUpsertInput(
                artistId, CandidateRole, identity, provider, originalUrl, Path.GetFullPath(path), hash,
                image.Width, image.Height, "not_scanned", null, false, false), token);
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
        foreach (var item in included.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "artworks" ||
                !item.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("files", out var files)) continue;
            foreach (var file in files.EnumerateArray())
            {
                if (file.TryGetProperty("href", out var value) && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    href = value.GetString()!;
                    return true;
                }
            }
        }
        return false;
    }

    private static string BuildLocalUrl(string path) => $"/api/library/image?path={Uri.EscapeDataString(Path.GetFullPath(path))}&size=640";
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private sealed record RemoteCandidate(string Provider, string Identity, string Url, int? Width, int? Height);
    private sealed record ProviderResolution(string Provider, IReadOnlyList<RemoteCandidate> Candidates, string? Message);
}

public sealed record ArtistArtworkCatalogResult(long ArtistId, string ArtistName, IReadOnlyList<ArtistArtworkVisual> Visuals, IReadOnlyList<ArtistArtworkProviderResult> Providers);
public sealed record ArtistArtworkVisual(string Source, string Identity, string? OriginalUrl, string Path, string Url, int? Width, int? Height);
public sealed record ArtistArtworkProviderResult(string Provider, bool Success, int CachedCount, string? Message);
