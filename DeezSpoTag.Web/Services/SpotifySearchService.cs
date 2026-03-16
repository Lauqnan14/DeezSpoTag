using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Linq;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifySearchService
{
    private const string SpotifyCoverSize640 = "ab67616d0000b273";
    private const string SpotifyCoverSize300 = "ab67616d00001e02";
    private const string SpotifyCoverSize64 = "ab67616d00004851";
    private const string SpotifyCoverSizeMax = "ab67616d000082c1";
    private static readonly string[] SpotifyCoverSizeTokens =
    {
        SpotifyCoverSize640,
        SpotifyCoverSize300,
        SpotifyCoverSize64
    };
    private readonly PlatformAuthService _platformAuthService;
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SpotifyBlobService _blobService;
    private readonly SpotifyUserStateProvider _userStateProvider;
    private readonly ILogger<SpotifySearchService> _logger;
    private readonly LibraryConfigStore _configStore;
    private readonly object _userAgentLock = new();
    private readonly Random _userAgentRandom = new();
    private readonly string _userAgent;

    public SpotifySearchService(
        PlatformAuthService platformAuthService,
        SpotifyPathfinderMetadataClient pathfinderMetadataClient,
        IHttpClientFactory httpClientFactory,
        SpotifyBlobService blobService,
        SpotifyUserStateProvider userStateProvider,
        LibraryConfigStore configStore,
        ILogger<SpotifySearchService> logger)
    {
        _platformAuthService = platformAuthService;
        _pathfinderMetadataClient = pathfinderMetadataClient;
        _httpClientFactory = httpClientFactory;
        _blobService = blobService;
        _userStateProvider = userStateProvider;
        _configStore = configStore;
        _logger = logger;
        _userAgent = SpotifyUserAgentGenerator.BuildRandom(_userAgentRandom, _userAgentLock);
    }

    public async Task<SpotifySearchResponse?> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var pathfinder = await SearchViaPathfinderAsync(query, limit, cancellationToken);
        if (pathfinder != null)
        {
            return pathfinder;
        }

        _logger.LogWarning("Spotify Pathfinder search unavailable; v1 /search fallback skipped to minimize rate limits.");
        return null;
    }

    public async Task<SpotifySearchTypeResponse?> SearchByTypeAsync(string query, string type, int limit, int offset, CancellationToken cancellationToken)
    {
        if (string.Equals(type, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            return new SpotifySearchTypeResponse("playlist", new List<SpotifySearchItem>(), 0);
        }

        var pathfinder = await SearchByTypeViaPathfinderAsync(query, type, limit, offset, cancellationToken);
        if (pathfinder != null)
        {
            return pathfinder;
        }

        _logger.LogWarning("Spotify Pathfinder typed search unavailable; v1 /search fallback skipped to minimize rate limits.");
        return null;
    }

    private async Task<SpotifySearchResponse?> SearchViaPathfinderAsync(string query, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var resolvedLimit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 50);
            var trackTask = _pathfinderMetadataClient.SearchTracksAsync(query, resolvedLimit, cancellationToken);
            var artistTask = SearchArtistsViaPathfinderAsync(query, resolvedLimit, 0, cancellationToken);
            await Task.WhenAll(trackTask, artistTask);

            var tracks = trackTask.Result
                .Take(resolvedLimit)
                .Select(MapPathfinderTrack)
                .ToList();
            var artists = artistTask.Result?.Items
                .Take(resolvedLimit)
                .ToList()
                ?? new List<SpotifySearchItem>();
            var albums = BuildPathfinderAlbumItems(trackTask.Result, resolvedLimit);

            var totals = new Dictionary<string, int>
            {
                ["tracks"] = tracks.Count,
                ["albums"] = albums.Count,
                ["artists"] = artists.Count,
                ["playlists"] = 0
            };

            return new SpotifySearchResponse(tracks, albums, artists, new List<SpotifySearchItem>(), totals);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify Pathfinder search failed.");
            return null;
        }
    }

    private async Task<SpotifySearchTypeResponse?> SearchByTypeViaPathfinderAsync(
        string query,
        string type,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        try
        {
            var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
            var resolvedLimit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 50);
            var resolvedOffset = Math.Max(0, offset);
            var requested = Math.Clamp(resolvedLimit + resolvedOffset, 1, 50);

            switch (normalizedType)
            {
                case "track":
                    {
                        var tracks = await _pathfinderMetadataClient.SearchTracksAsync(query, requested, cancellationToken);
                        var items = tracks
                            .Skip(resolvedOffset)
                            .Take(resolvedLimit)
                            .Select(MapPathfinderTrack)
                            .ToList();
                        return new SpotifySearchTypeResponse("track", items, tracks.Count);
                    }
                case "artist":
                    {
                        var artists = await SearchArtistsViaPathfinderAsync(query, resolvedLimit, resolvedOffset, cancellationToken);
                        if (artists is not null)
                        {
                            return artists;
                        }

                        return new SpotifySearchTypeResponse("artist", new List<SpotifySearchItem>(), 0);
                    }
                case "album":
                    {
                        var tracks = await _pathfinderMetadataClient.SearchTracksAsync(query, requested, cancellationToken);
                        var albums = BuildPathfinderAlbumItems(tracks, requested);
                        var items = albums
                            .Skip(resolvedOffset)
                            .Take(resolvedLimit)
                            .ToList();
                        return new SpotifySearchTypeResponse("album", items, albums.Count);
                    }
                default:
                    return new SpotifySearchTypeResponse(normalizedType, new List<SpotifySearchItem>(), 0);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify Pathfinder typed search failed. type=Type");
            return null;
        }
    }

    private async Task<SpotifySearchTypeResponse?> SearchArtistsViaPathfinderAsync(
        string query,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var resolvedLimit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 50);
        var resolvedOffset = Math.Max(0, offset);
        var requested = Math.Clamp(resolvedLimit + resolvedOffset, 1, 50);
        var artists = await _pathfinderMetadataClient.SearchArtistsAsync(query, requested, cancellationToken);
        var valid = artists
            .Where(artist => LooksLikeSpotifyArtistId(artist.Id))
            .ToList();
        var items = valid
            .Skip(resolvedOffset)
            .Take(resolvedLimit)
            .Select(MapPathfinderArtist)
            .ToList();
        return new SpotifySearchTypeResponse("artist", items, valid.Count);
    }

    private static bool LooksLikeSpotifyArtistId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 22)
        {
            return false;
        }

        return value.All(char.IsLetterOrDigit);
    }

    private static SpotifySearchItem MapPathfinderTrack(SpotifyTrackSummary track)
    {
        string? subtitle;
        if (string.IsNullOrWhiteSpace(track.Album))
        {
            subtitle = track.Artists;
        }
        else if (string.IsNullOrWhiteSpace(track.Artists))
        {
            subtitle = track.Album;
        }
        else
        {
            subtitle = $"{track.Artists} • {track.Album}";
        }

        return new SpotifySearchItem(
            track.Id,
            track.Name,
            "track",
            string.IsNullOrWhiteSpace(track.SourceUrl) ? $"https://open.spotify.com/track/{track.Id}" : track.SourceUrl,
            RewriteSpotifyImageUrl(track.ImageUrl),
            subtitle,
            track.DurationMs);
    }

    private static SpotifySearchItem MapPathfinderArtist(SpotifyPathfinderMetadataClient.SpotifyArtistSearchCandidate artist)
    {
        return new SpotifySearchItem(
            artist.Id,
            artist.Name,
            "artist",
            $"https://open.spotify.com/artist/{artist.Id}",
            RewriteSpotifyImageUrl(artist.ImageUrl),
            null,
            null);
    }

    private static List<SpotifySearchItem> BuildPathfinderAlbumItems(IReadOnlyList<SpotifyTrackSummary> tracks, int limit)
    {
        var items = new List<SpotifySearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            if (items.Count >= limit)
            {
                break;
            }

            var albumId = track.AlbumId;
            var albumName = track.Album;
            if (string.IsNullOrWhiteSpace(albumId) || string.IsNullOrWhiteSpace(albumName))
            {
                continue;
            }

            if (!seen.Add(albumId))
            {
                continue;
            }

            var subtitle = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artists : track.AlbumArtist;
            items.Add(new SpotifySearchItem(
                albumId,
                albumName,
                "album",
                $"https://open.spotify.com/album/{albumId}",
                RewriteSpotifyImageUrl(track.ImageUrl),
                subtitle,
                null));
        }

        return items;
    }

    private async Task<SearchContext?> BuildRequestContextAsync(CancellationToken cancellationToken)
    {
        // Cookie-based web-player auth should always be attempted first for search.
        var webPlayer = await BuildWebPlayerCookieContextAsync(cancellationToken);
        if (webPlayer is not null)
        {
            return webPlayer;
        }

        var fallback = await BuildLibrespotContextAsync(cancellationToken);
        if (fallback is not null)
        {
            return fallback;
        }

        _logger.LogDebug("Spotify search auth unavailable: no web-player or librespot token.");
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "warn",
            "[spotify] search unavailable: no web-player or librespot token."));
        return null;
    }

    private async Task<SearchContext?> BuildWebPlayerCookieContextAsync(CancellationToken cancellationToken)
    {
        try
        {
            var userState = await TryLoadUserSpotifyStateAsync();
            if (userState != null && !string.IsNullOrWhiteSpace(userState.WebPlayerSpDc))
            {
                var userToken = await _blobService.GetWebPlayerAccessTokenFromCookiesAsync(
                    userState.WebPlayerSpDc,
                    userState.WebPlayerSpKey,
                    userState.WebPlayerUserAgent,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(userToken))
                {
                    return null;
                }

                var userMarket = await ResolveMarketAsync();
                _logger.LogDebug("Spotify search auth ready: tokenLen={TokenLen} market={Market} source=web-player", userToken.Length, userMarket);
                return new SearchContext(
                    userToken,
                    userMarket,
                    "webplayer",
                    null,
                    userState.WebPlayerSpDc,
                    userState.WebPlayerSpKey,
                    userState.WebPlayerUserAgent);
            }

            var state = await _platformAuthService.LoadAsync();
            var spotify = state.Spotify;
            if (spotify == null || string.IsNullOrWhiteSpace(spotify.WebPlayerSpDc))
            {
                return null;
            }

            var token = await _blobService.GetWebPlayerAccessTokenFromCookiesAsync(
                spotify.WebPlayerSpDc,
                spotify.WebPlayerSpKey,
                spotify.WebPlayerUserAgent,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var market = await ResolveMarketAsync();
            _logger.LogDebug("Spotify search auth ready: tokenLen={TokenLen} market={Market} source=web-player", token.Length, market);
            return new SearchContext(
                token,
                market,
                "webplayer",
                null,
                spotify.WebPlayerSpDc,
                spotify.WebPlayerSpKey,
                spotify.WebPlayerUserAgent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Spotify web-player credentials.");
            return null;
        }
    }

    private async Task<SearchContext?> BuildLibrespotContextAsync(CancellationToken cancellationToken)
    {
        var blobPath = await TryResolveActiveWebPlayerBlobPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return null;
        }

        // Try web player token first (uses blob cookies directly, no Librespot)
        var webPlayerToken = await _blobService.GetWebPlayerTokenInfoAsync(blobPath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(webPlayerToken?.AccessToken))
        {
            var market = await ResolveMarketAsync();
            _logger.LogDebug(
                "Spotify search auth ready: tokenLen={TokenLen} market={Market} source=webplayer",
                webPlayerToken.AccessToken.Length,
                market);
            return new SearchContext(webPlayerToken.AccessToken, market, "webplayer", blobPath, null, null, null);
        }

        // Fallback to Librespot if web player token fails
        var tokenResult = await _blobService.GetWebApiAccessTokenAsync(blobPath, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(tokenResult.AccessToken))
        {
            return null;
        }

        var fallbackMarket = await ResolveMarketAsync();
        _logger.LogDebug(
            "Spotify search auth ready: tokenLen={TokenLen} market={Market} source=librespot",
            tokenResult.AccessToken.Length,
            fallbackMarket);
        return new SearchContext(tokenResult.AccessToken, fallbackMarket, "librespot", blobPath, null, null, null);
    }

    private async Task<string> ResolveMarketAsync()
    {
        try
        {
            var userState = await TryLoadUserSpotifyStateAsync();
            if (!string.IsNullOrWhiteSpace(userState?.ActiveAccount))
            {
                var account = userState.Accounts.FirstOrDefault(a =>
                    a.Name.Equals(userState.ActiveAccount, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(account?.Region))
                {
                    return account!.Region!;
                }
            }

            var state = await _platformAuthService.LoadAsync();
            var active = state.Spotify?.ActiveAccount;
            if (!string.IsNullOrWhiteSpace(active))
            {
                var account = state.Spotify!.Accounts.FirstOrDefault(a =>
                    a.Name.Equals(active, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(account?.Region))
                {
                    return account!.Region!;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve spotify market from saved account state");
        }

        return "US";
    }

    private async Task<string?> TryResolveActiveWebPlayerBlobPathAsync(CancellationToken cancellationToken)
    {
        try
        {
            var userState = await TryLoadUserSpotifyStateAsync();
            if (!string.IsNullOrWhiteSpace(userState?.ActiveAccount))
            {
                var account = userState.Accounts.FirstOrDefault(a =>
                    a.Name.Equals(userState.ActiveAccount, StringComparison.OrdinalIgnoreCase));
                var blobPath = account?.WebPlayerBlobPath;
                if (!string.IsNullOrWhiteSpace(blobPath)
                    && _blobService.BlobExists(blobPath)
                    && await _blobService.IsWebPlayerBlobAsync(blobPath, cancellationToken))
                {
                    return blobPath;
                }
            }

            var state = await _platformAuthService.LoadAsync();
            var active = state.Spotify?.ActiveAccount;
            if (!string.IsNullOrWhiteSpace(active))
            {
                var account = state.Spotify!.Accounts.FirstOrDefault(a =>
                    a.Name.Equals(active, StringComparison.OrdinalIgnoreCase));
                var blobPath = account?.WebPlayerBlobPath;
                if (!string.IsNullOrWhiteSpace(blobPath)
                    && _blobService.BlobExists(blobPath)
                    && await _blobService.IsWebPlayerBlobAsync(blobPath, cancellationToken))
                {
                    return blobPath;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Spotify blob path.");
        }

        return null;
    }

    private async Task<SpotifyUserAuthState?> TryLoadUserSpotifyStateAsync()
        => await _userStateProvider.TryLoadActiveUserStateAsync();

    private async Task<(HttpStatusCode Status, string? Body, TimeSpan? RetryAfter)> ExecuteRequestAsync(
        HttpClient client,
        string url,
        string token,
        bool allowRetryAfter,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        ApplySpotifyWebHeaders(request);
        _logger.LogDebug("Spotify search request prepared: url={Url} tokenLen={TokenLen}", url, token.Length);

        _logger.LogInformation("Spotify search request: {Url}", url);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        TimeSpan? retryAfter = null;
        if (allowRetryAfter && response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            retryAfter = ParseRetryAfter(response);
            _logger.LogInformation("Spotify search throttled: {Url} retryAfter={DelayMs}ms", url, retryAfter.Value.TotalMilliseconds);
        }

        _logger.LogInformation("Spotify search response: {Url} status={StatusCode}", url, (int)response.StatusCode);
        return (response.StatusCode, body, retryAfter);
    }

    private static TimeSpan ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (int.TryParse(raw, out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds + 1);
            }
        }
        return TimeSpan.FromSeconds(5);
    }

    private void ApplySpotifyWebHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        request.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        request.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        request.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
        request.Headers.TryAddWithoutValidation("Referer", "https://open.spotify.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://open.spotify.com");
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        _logger.LogDebug(
            "Spotify search headers: ua={UserAgent} acceptLang={AcceptLanguage} origin={Origin} referer={Referer} secFetchSite={SecFetchSite}",
            _userAgent,
            "en-US,en;q=0.9",
            "https://open.spotify.com",
            "https://open.spotify.com/",
            "same-origin");
    }

    private static string? RewriteSpotifyImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        return SpotifyCoverSizeTokens.Aggregate(
            url,
            static (current, token) => current.Contains(token, StringComparison.Ordinal)
                ? current.Replace(token, SpotifyCoverSizeMax, StringComparison.Ordinal)
                : current);
    }

    internal sealed record SearchContext(
        string AccessToken,
        string Market,
        string Source,
        string? BlobPath,
        string? WebPlayerSpDc,
        string? WebPlayerSpKey,
        string? WebPlayerUserAgent);

    public sealed record SpotifySearchAuthContext(string AccessToken, string Market, string Source);

    public async Task<SpotifySearchAuthContext?> TryGetAuthContextAsync(CancellationToken cancellationToken)
    {
        var context = await BuildRequestContextAsync(cancellationToken);
        if (context == null)
        {
            return null;
        }

        return new SpotifySearchAuthContext(context.AccessToken, context.Market, context.Source);
    }

    public async Task<SpotifySearchAuthContext?> TryGetLibrespotAuthContextAsync(CancellationToken cancellationToken)
    {
        var context = await BuildLibrespotContextAsync(cancellationToken);
        if (context == null)
        {
            return null;
        }

        return new SpotifySearchAuthContext(context.AccessToken, context.Market, context.Source);
    }

    public async Task<string?> FetchSpotifyJsonAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var (status, body, retryAfter) = await ExecuteRequestAsync(client, url, accessToken, allowRetryAfter: true, cancellationToken);

            if (status == HttpStatusCode.TooManyRequests && retryAfter.HasValue && retryAfter.Value <= TimeSpan.FromSeconds(30))
            {
                _logger.LogInformation("Spotify fetch throttled; waiting {DelaySeconds}s then retrying once: {Url}", retryAfter.Value.TotalSeconds, url);
                await Task.Delay(retryAfter.Value, cancellationToken);
                var retry = await ExecuteRequestAsync(client, url, accessToken, allowRetryAfter: false, cancellationToken);
                status = retry.Status;
                body = retry.Body;
            }

            return status == HttpStatusCode.OK ? body : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify fetch failed: {Url}", url);
            return null;
        }
    }
}

public sealed record SpotifySearchResponse(
    List<SpotifySearchItem> Tracks,
    List<SpotifySearchItem> Albums,
    List<SpotifySearchItem> Artists,
    List<SpotifySearchItem> Playlists,
    Dictionary<string, int> Totals);

public sealed record SpotifySearchTypeResponse(
    string Type,
    List<SpotifySearchItem> Items,
    int Total);

public sealed record SpotifySearchItem(
    string Id,
    string Name,
    string Type,
    string SourceUrl,
    string? ImageUrl,
    string? Subtitle,
    int? DurationMs);
