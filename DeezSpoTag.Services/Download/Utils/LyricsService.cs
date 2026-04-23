using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models.Settings;
using System.Linq;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;
using DeezSpoTag.Services.Download.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Services.Download.Utils;

public sealed class LyricsProviderOptions
{
    public LrclibLyricsProviderOptions? Lrclib { get; init; }
}

public sealed class LrclibLyricsProviderOptions
{
    public int? DurationToleranceSeconds { get; init; }
    public bool? UseDurationHint { get; init; }
    public bool? SearchFallback { get; init; }
    public bool? PreferSynced { get; init; }
}

/// <summary>
/// Enhanced lyrics service implementing refreezer's dual API approach
/// Provides robust lyrics fetching with Pipe API primary and GW API fallback
/// </summary>
public class LyricsService
{
    private readonly ILogger<LyricsService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly JwtTokenService _jwtTokenService;
    private readonly AuthenticatedDeezerService _authenticatedDeezerService;
    private readonly DeezSpoTag.Services.Apple.AppleLyricsService _appleLyricsService;
    private readonly LrclibLyricsService _lrclibLyricsService;
    private readonly SongLinkResolver? _songLinkResolver;
    private readonly DeezerClient? _deezerClient;
    private string? _cachedGwToken;
    private DateTime _cachedGwTokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _spotifyTokenGate = new(1, 1);
    private readonly SemaphoreSlim _musixmatchTokenGate = new(1, 1);
    private string? _cachedSpotifyAccessToken;
    private DateTimeOffset _cachedSpotifyAccessTokenExpiry = DateTimeOffset.MinValue;
    private string? _cachedSpotifyAccessTokenKey;
    private string? _cachedMusixmatchUserToken;
    private const int GwTokenTtlMinutes = 45;
    private const string DefaultSpotifyWebPlayerUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36";
    private const string AppleProvider = "apple";
    private const string DeezerProvider = "deezer";
    private const string SpotifyProvider = "spotify";
    private const string LrclibProvider = "lrclib";
    private const string MusixmatchProvider = "musixmatch";
    private const string ApplicationJson = "application/json";
    private const string LyricsClientName = "LyricsService";
    private const string UserAgentHeader = "User-Agent";
    private const string AuthorityHeader = "authority";
    private const string CookieHeader = "cookie";
    private const string LyricsType = "lyrics";
    private const string UnsyncedLyricsType = "unsynced-lyrics";
    private const string SyllableLyricsType = "syllable-lyrics";
    private const string DeezerUrlKey = "deezer";
    private const string SpotifyUrlKey = "spotify";
    private const string MessagePropertyName = "message";
    private const string SpotifyDataDir = "spotify";
    private const string BlobsDir = "blobs";
    private const string HttpsScheme = "https";
    private const string SpotifyOpenHost = "open.spotify.com";
    private static readonly string SpotifyOpenBaseUrl = BuildAuthorityUrl(SpotifyOpenHost);
    private static readonly string SpotifyOpenRootUrl = BuildRootUrl(SpotifyOpenHost);
    private const string SpotifyOpenTokenPath = "/api/token";
    private const string SpotifyOpenFallbackTokenPath = "/get_access_token";
    private static readonly string DeezerPipeApiUrl = BuildUrl("pipe.deezer.com", "/api/");
    private static readonly string DeezerGwUserDataUrl = BuildUrl("www.deezer.com", "/ajax/gw-light.php?method=deezer.getUserData&input=3&api_version=1.0&api_token=null");
    private static readonly string[] DefaultLyricsProviderOrder = [AppleProvider, DeezerProvider, SpotifyProvider, LrclibProvider, MusixmatchProvider];

    private static string BuildAuthorityUrl(string host)
    {
        return new UriBuilder(HttpsScheme, host).Uri.GetLeftPart(UriPartial.Authority);
    }

    private static string BuildRootUrl(string host)
    {
        return $"{BuildAuthorityUrl(host)}/";
    }

    private static string BuildUrl(string host, string pathAndQuery)
    {
        return $"{BuildAuthorityUrl(host)}{pathAndQuery}";
    }

    private sealed class LyricsResolutionState
    {
        public string? Arl { get; set; }
        public string? TtmlFallback { get; set; }
        public LyricsBase? ResolvedLyrics { get; set; }
        public bool DeezerAttempted { get; set; }
        public bool DeezerMissingAuth { get; set; }
    }

    public LyricsService(
        ILogger<LyricsService> logger,
        IHttpClientFactory httpClientFactory,
        JwtTokenService jwtTokenService,
        AuthenticatedDeezerService authenticatedDeezerService,
        DeezSpoTag.Services.Apple.AppleLyricsService appleLyricsService,
        LrclibLyricsService lrclibLyricsService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _jwtTokenService = jwtTokenService;
        _authenticatedDeezerService = authenticatedDeezerService;
        _appleLyricsService = appleLyricsService;
        _lrclibLyricsService = lrclibLyricsService;
        _songLinkResolver = serviceProvider.GetService<SongLinkResolver>();
        _deezerClient = serviceProvider.GetService<DeezerClient>();
    }

    /// <summary>
    /// Resolve lyrics for a track using current settings and authentication.
    /// </summary>
    public Task<LyricsBase?> ResolveLyricsAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken = default)
        => ResolveLyricsAsync(track, settings, providerOptions: null, cancellationToken);

    public async Task<LyricsBase?> ResolveLyricsAsync(
        Track track,
        DeezSpoTagSettings settings,
        LyricsProviderOptions? providerOptions,
        CancellationToken cancellationToken = default)
    {
        if (track == null)
        {
            _logger.LogWarning("ResolveLyricsAsync called with null track");
            return null;
        }

        var shouldFetch = ShouldHandleLyricsBySettings(settings);
        if (!shouldFetch)
        {
            return null;
        }

        var requiresTtmlForOutput = ShouldPrioritizeTtml(settings);
        var providers = ResolveLyricsProviders(settings);

        var state = new LyricsResolutionState();

        foreach (var provider in providers)
        {
            var providerLyrics = await TryResolveProviderSafelyAsync(provider, track, settings, providerOptions, state, cancellationToken);
            if (providerLyrics == null || !providerLyrics.IsLoaded())
            {
                continue;
            }

            MergeProviderLyrics(state, providerLyrics);
            if (ShouldReturnResolvedLyrics(state, requiresTtmlForOutput))
            {
                return state.ResolvedLyrics;
            }
        }

        if (state.ResolvedLyrics?.IsLoaded() == true)
        {
            if (!string.IsNullOrWhiteSpace(state.TtmlFallback) && string.IsNullOrWhiteSpace(state.ResolvedLyrics.TtmlLyrics))
            {
                state.ResolvedLyrics.TtmlLyrics = state.TtmlFallback;
            }

            return state.ResolvedLyrics;
        }

        if (!string.IsNullOrWhiteSpace(state.TtmlFallback))
        {
            return new LyricsSource { TtmlLyrics = state.TtmlFallback };
        }

        if (state.DeezerAttempted && state.DeezerMissingAuth && string.IsNullOrEmpty(state.Arl))
        {
            return LyricsNew.CreateError("No ARL available for lyrics fetching");
        }

        return LyricsNew.CreateError("No lyrics available from configured providers");
    }

    private async Task<LyricsBase?> TryResolveProviderSafelyAsync(
        string provider,
        Track track,
        DeezSpoTagSettings settings,
        LyricsProviderOptions? providerOptions,
        LyricsResolutionState state,
        CancellationToken cancellationToken)
    {
        try
        {
            return await TryResolveProviderLyricsAsync(provider, track, settings, providerOptions, state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Lyrics provider {Provider} threw an exception for track {TrackId}, advancing to next provider",
                provider,
                track.Id);
            return null;
        }
    }

    private static bool ShouldReturnResolvedLyrics(LyricsResolutionState state, bool requiresTtmlForOutput)
    {
        if (!requiresTtmlForOutput)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(state.ResolvedLyrics?.TtmlLyrics);
    }

    private async Task<LyricsBase?> TryResolveProviderLyricsAsync(
        string provider,
        Track track,
        DeezSpoTagSettings settings,
        LyricsProviderOptions? providerOptions,
        LyricsResolutionState state,
        CancellationToken cancellationToken)
    {
        return provider switch
        {
            AppleProvider => await ResolveLoadedLyricsOrNullAsync(
                () => _appleLyricsService.ResolveLyricsForTrackAsync(track, settings, cancellationToken)),
            DeezerProvider => await TryResolveDeezerProviderLyricsAsync(track, settings, state, cancellationToken),
            SpotifyProvider => await ResolveLoadedLyricsOrNullAsync(
                () => ResolveSpotifyLyricsAsync(track, settings, cancellationToken)),
            LrclibProvider => await ResolveLoadedLyricsOrNullAsync(
                () => _lrclibLyricsService.ResolveLyricsAsync(
                    track,
                    BuildLrclibRequestOptions(providerOptions?.Lrclib),
                    cancellationToken)),
            MusixmatchProvider => await ResolveLoadedLyricsOrNullAsync(
                () => ResolveMusixmatchLyricsAsync(track, cancellationToken)),
            _ => LogUnknownLyricsProvider(provider)
        };
    }

    private static async Task<LyricsBase?> ResolveLoadedLyricsOrNullAsync(Func<Task<LyricsBase>> resolver)
    {
        LyricsBase? lyrics = await resolver();
        if (lyrics is null)
        {
            return null;
        }

        return lyrics.IsLoaded() ? lyrics : null;
    }

    private LyricsBase? LogUnknownLyricsProvider(string provider)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Unknown lyrics provider {Provider} configured in fallback order", provider);        }
        return null;
    }

    private async Task<LyricsBase?> TryResolveDeezerProviderLyricsAsync(
        Track track,
        DeezSpoTagSettings settings,
        LyricsResolutionState state,
        CancellationToken cancellationToken)
    {
        state.DeezerAttempted = true;
        var deezerTrackId = await ResolveDeezerLyricsTrackIdAsync(track, settings, cancellationToken);
        if (string.IsNullOrWhiteSpace(deezerTrackId))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Skipping Deezer lyrics lookup because no Deezer track id could be resolved for track {TrackId}",
                    track.Id);            }
            return null;
        }

        state.Arl ??= await _authenticatedDeezerService.GetArlAsync();
        if (string.IsNullOrWhiteSpace(state.Arl))
        {
            state.Arl = settings.Arl;
        }
        if (string.IsNullOrEmpty(state.Arl))
        {
            state.DeezerMissingAuth = true;
            _logger.LogWarning(
                "No ARL available for Deezer lyrics fetch for Deezer track id {DeezerTrackId}",
                deezerTrackId);
            return null;
        }

        var sid = await _authenticatedDeezerService.GetSidAsync();
        var deezerLyrics = await GetLyricsAsync(deezerTrackId, state.Arl, sid, cancellationToken);
        return deezerLyrics.IsLoaded() ? deezerLyrics : null;
    }

    private static void MergeProviderLyrics(LyricsResolutionState state, LyricsBase providerLyrics)
    {
        if (!string.IsNullOrWhiteSpace(providerLyrics.TtmlLyrics))
        {
            state.TtmlFallback = providerLyrics.TtmlLyrics;
        }

        if (!string.IsNullOrWhiteSpace(state.TtmlFallback) && string.IsNullOrWhiteSpace(providerLyrics.TtmlLyrics))
        {
            providerLyrics.TtmlLyrics = state.TtmlFallback;
        }

        if (state.ResolvedLyrics == null)
        {
            state.ResolvedLyrics = providerLyrics;
            return;
        }

        MergeLyricsData(state.ResolvedLyrics, providerLyrics);
    }

    private static bool ShouldPrioritizeTtml(DeezSpoTagSettings settings)
    {
        if (settings.SyncedLyrics)
        {
            return true;
        }

        var outputFormat = NormalizeLyricsOutputFormat(settings.LrcFormat);
        return outputFormat is "ttml" or "both";
    }

    private static LrclibLyricsService.LrclibRequestOptions? BuildLrclibRequestOptions(
        LrclibLyricsProviderOptions? options)
    {
        if (options == null)
        {
            return null;
        }

        return new LrclibLyricsService.LrclibRequestOptions
        {
            DurationToleranceSeconds = options.DurationToleranceSeconds ?? 10,
            UseDurationHint = options.UseDurationHint ?? true,
            SearchFallback = options.SearchFallback ?? true,
            PreferSynced = options.PreferSynced ?? true
        };
    }

    private static void MergeLyricsData(LyricsBase target, LyricsBase candidate)
    {
        if (target == null || candidate == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(target.TtmlLyrics) && !string.IsNullOrWhiteSpace(candidate.TtmlLyrics))
        {
            target.TtmlLyrics = candidate.TtmlLyrics;
        }

        if (!HasLyricsLines(target.SyncedLyrics) && HasLyricsLines(candidate.SyncedLyrics))
        {
            target.SyncedLyrics = candidate.SyncedLyrics;
        }

        if (string.IsNullOrWhiteSpace(target.UnsyncedLyrics) && !string.IsNullOrWhiteSpace(candidate.UnsyncedLyrics))
        {
            target.UnsyncedLyrics = candidate.UnsyncedLyrics;
        }

        if (string.IsNullOrWhiteSpace(target.Writers) && !string.IsNullOrWhiteSpace(candidate.Writers))
        {
            target.Writers = candidate.Writers;
        }

        if (string.IsNullOrWhiteSpace(target.Copyright) && !string.IsNullOrWhiteSpace(candidate.Copyright))
        {
            target.Copyright = candidate.Copyright;
        }
    }

    private static bool HasLyricsLines(List<SynchronizedLyric>? lyricsLines)
    {
        return lyricsLines != null && lyricsLines.Count > 0;
    }

    private static List<string> ResolveLyricsProviders(DeezSpoTagSettings settings)
    {
        return ProviderOrderResolver.Resolve(
            settings.LyricsFallbackEnabled,
            settings.LyricsFallbackOrder,
            DefaultLyricsProviderOrder,
            NormalizeLyricsProviderToken);
    }

    private static string NormalizeLyricsProviderToken(string? provider)
    {
        var normalized = (provider ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "itunes" => AppleProvider,
            "applemusic" => AppleProvider,
            "apple-music" => AppleProvider,
            "apple_music" => AppleProvider,
            "apple music" => AppleProvider,
            "music.apple" => AppleProvider,
            "lrcget" => LrclibProvider,
            "lrc-get" => LrclibProvider,
            "lrc_get" => LrclibProvider,
            "lrclib" => LrclibProvider,
            "musixmatch" => MusixmatchProvider,
            _ => normalized
        };
    }

    private async Task<LyricsBase> ResolveMusixmatchLyricsAsync(Track track, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Title))
        {
            return LyricsNew.CreateError("Track title is required for Musixmatch lyrics");
        }

        var artist = ResolveMusixmatchArtist(track);
        if (string.IsNullOrWhiteSpace(artist))
        {
            return LyricsNew.CreateError("Track artist is required for Musixmatch lyrics");
        }

        var body = await FetchMusixmatchLyricsPayloadAsync(track.Title, artist, cancellationToken);
        if (body == null)
        {
            return LyricsNew.CreateError("No Musixmatch lyrics payload");
        }

        var output = new LyricsSource();
        if (TryReadMusixmatchRichsync(body, out var richsyncLines) && richsyncLines.Count > 0)
        {
            output.SyncedLyrics = richsyncLines;
            return output;
        }

        if (TryReadMusixmatchSubtitles(body, out var subtitleLines) && subtitleLines.Count > 0)
        {
            output.SyncedLyrics = subtitleLines;
            return output;
        }

        if (TryReadMusixmatchUnsynced(body, out var unsyncedLyrics))
        {
            output.UnsyncedLyrics = unsyncedLyrics;
            return output;
        }

        return LyricsNew.CreateError("No lyrics available from Musixmatch");
    }

    private static string ResolveMusixmatchArtist(Track track)
    {
        if (track.MainArtist is { Name: { Length: > 0 } mainArtistName } && !string.IsNullOrWhiteSpace(mainArtistName))
        {
            return mainArtistName;
        }

        if (track.Artists?.Count > 0)
        {
            return string.Join(", ", track.Artists.Where(static name => !string.IsNullOrWhiteSpace(name)));
        }

        if (track.Artist.TryGetValue("Main", out var mainArtists) && mainArtists.Count > 0)
        {
            return string.Join(", ", mainArtists.Where(static name => !string.IsNullOrWhiteSpace(name)));
        }

        return track.ArtistString;
    }

    private async Task<MusixmatchMacroCallsBody?> FetchMusixmatchLyricsPayloadAsync(
        string title,
        string artist,
        CancellationToken cancellationToken,
        int retryCount = 0)
    {
        using var document = await GetMusixmatchJsonAsync(
            "macro.subtitles.get",
            new Dictionary<string, string>
            {
                ["format"] = "json",
                ["namespace"] = "lyrics_richsynced",
                ["optional_calls"] = "track.richsync",
                ["subtitle_format"] = "lrc",
                ["q_artist"] = artist,
                ["q_track"] = title
            },
            cancellationToken);

        if (document == null)
        {
            return null;
        }

        if (TryReadMusixmatchRootStatus(document.RootElement, out var statusCode)
            && statusCode == (int)HttpStatusCode.Unauthorized)
        {
            _cachedMusixmatchUserToken = null;
            if (retryCount >= 3)
            {
                return null;
            }

            var delay = TimeSpan.FromSeconds(Math.Pow(2, retryCount + 3));
            await Task.Delay(delay, cancellationToken);
            return await FetchMusixmatchLyricsPayloadAsync(title, artist, cancellationToken, retryCount + 1);
        }

        return ParseMusixmatchMacroCallsBody(document.RootElement);
    }

    private async Task<JsonDocument?> GetMusixmatchJsonAsync(
        string action,
        Dictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        await EnsureMusixmatchTokenAsync(action, cancellationToken);

        query["app_id"] = "web-desktop-app-v1.0";
        if (!string.IsNullOrWhiteSpace(_cachedMusixmatchUserToken))
        {
            query["usertoken"] = _cachedMusixmatchUserToken!;
        }

        query["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var queryString = string.Join("&", query.Select(static kvp => kvp.Key + "=" + Uri.EscapeDataString(kvp.Value)));
        var url = $"https://apic-desktop.musixmatch.com/ws/1.1/{action}?{queryString}";

        using var client = _httpClientFactory.CreateClient(LyricsClientName);
        if (!client.DefaultRequestHeaders.Contains(AuthorityHeader))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(AuthorityHeader, "apic-desktop.musixmatch.com");
        }

        if (!client.DefaultRequestHeaders.Contains(CookieHeader))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(CookieHeader, "AWSELBCORS=0; AWSELB=0");
        }

        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Musixmatch request {Action} failed with status {StatusCode}", action, response.StatusCode);            }
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private MusixmatchMacroCallsBody? ParseMusixmatchMacroCallsBody(JsonElement root)
    {
        if (!TryGetMusixmatchMacroCallsElement(root, out var macroCallsElement))
        {
            return null;
        }

        var body = new MusixmatchMacroCallsBody
        {
            MacroCalls = new Dictionary<string, MusixmatchMacroCallResponse>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var macroCall in macroCallsElement.EnumerateObject())
        {
            body.MacroCalls[macroCall.Name] = ParseMusixmatchMacroCallResponse(macroCall.Value, macroCall.Name);
        }

        return body;
    }

    private MusixmatchMacroCallResponse ParseMusixmatchMacroCallResponse(JsonElement macroCallValue, string macroCallName)
    {
        var response = new MusixmatchMacroCallResponse
        {
            Message = new MusixmatchMacroCallMessage()
        };
        if (!macroCallValue.TryGetProperty(MessagePropertyName, out var message)
            || !message.TryGetProperty("body", out var bodyElement))
        {
            return response;
        }

        if (bodyElement.ValueKind == JsonValueKind.Null)
        {
            return response;
        }

        if (bodyElement.ValueKind != JsonValueKind.Object)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Musixmatch macro body for {MacroCall} returned {BodyKind}; skipping strict body mapping.",
                    macroCallName,
                    bodyElement.ValueKind);
            }

            return response;
        }

        try
        {
            response.Message.Body = bodyElement.Deserialize<MusixmatchBody>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Musixmatch macro body parsing failed for {MacroCall}; continuing with partial response.",
                macroCallName);
        }

        return response;
    }

    private static bool TryReadMusixmatchRootStatus(JsonElement root, out int statusCode)
    {
        statusCode = default;
        if (!root.TryGetProperty(MessagePropertyName, out var message)
            || !message.TryGetProperty("header", out var header)
            || !header.TryGetProperty("status_code", out var statusCodeElement)
            || statusCodeElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return statusCodeElement.TryGetInt32(out statusCode);
    }

    private static bool TryGetMusixmatchMacroCallsElement(JsonElement root, out JsonElement macroCallsElement)
    {
        macroCallsElement = default;
        if (!root.TryGetProperty(MessagePropertyName, out var message)
            || !message.TryGetProperty("body", out var body)
            || !body.TryGetProperty("macro_calls", out var macroCalls)
            || macroCalls.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        macroCallsElement = macroCalls;
        return true;
    }

    private async Task EnsureMusixmatchTokenAsync(string action, CancellationToken cancellationToken)
    {
        if (string.Equals(action, "token.get", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_cachedMusixmatchUserToken))
        {
            return;
        }

        await _musixmatchTokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedMusixmatchUserToken))
            {
                return;
            }

            using var tokenDocument = await FetchMusixmatchTokenDocumentAsync(cancellationToken);
            var root = tokenDocument.RootElement;
            if (!root.TryGetProperty(MessagePropertyName, out var message)
                || !message.TryGetProperty("header", out var header)
                || !header.TryGetProperty("status_code", out var statusCodeElement))
            {
                return;
            }

            if (statusCodeElement.GetInt32() == (int)HttpStatusCode.Unauthorized)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                return;
            }

            if (!message.TryGetProperty("body", out var body)
                || !body.TryGetProperty("user_token", out var tokenElement))
            {
                return;
            }

            _cachedMusixmatchUserToken = tokenElement.GetString();
        }
        finally
        {
            _musixmatchTokenGate.Release();
        }
    }

    private async Task<JsonDocument> FetchMusixmatchTokenDocumentAsync(CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>
        {
            ["user_language"] = "en",
            ["app_id"] = "web-desktop-app-v1.0",
            ["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
        };

        var queryString = string.Join("&", query.Select(static kvp => kvp.Key + "=" + Uri.EscapeDataString(kvp.Value)));
        var url = $"https://apic-desktop.musixmatch.com/ws/1.1/token.get?{queryString}";

        using var client = _httpClientFactory.CreateClient(LyricsClientName);
        if (!client.DefaultRequestHeaders.Contains(AuthorityHeader))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(AuthorityHeader, "apic-desktop.musixmatch.com");
        }

        if (!client.DefaultRequestHeaders.Contains(CookieHeader))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(CookieHeader, "AWSELBCORS=0; AWSELB=0");
        }

        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static bool TryReadMusixmatchUnsynced(MusixmatchMacroCallsBody body, out string? unsyncedLyrics)
    {
        unsyncedLyrics = null;
        if (!TryGetMusixmatchCallBody(body, "track.lyrics.get", out var callBody))
        {
            return false;
        }

        var lyricsBody = callBody.Lyrics?.LyricsBody;
        if (string.IsNullOrWhiteSpace(lyricsBody))
        {
            return false;
        }

        if (string.Equals(lyricsBody.Trim(), "instrumental", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        unsyncedLyrics = lyricsBody;
        return true;
    }

    private static bool TryReadMusixmatchSubtitles(MusixmatchMacroCallsBody body, out List<SynchronizedLyric> lines)
    {
        lines = new List<SynchronizedLyric>();
        if (!TryGetMusixmatchCallBody(body, "track.subtitles.get", out var callBody))
        {
            return false;
        }

        var subtitleBody = callBody.SubtitleList?.FirstOrDefault()?.Subtitle?.SubtitleBody;
        if (string.IsNullOrWhiteSpace(subtitleBody))
        {
            return false;
        }

        foreach (var line in subtitleBody.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length < 11 || line[0] != '[')
            {
                continue;
            }

            var closingBracketIndex = line.IndexOf(']');
            if (closingBracketIndex <= 0)
            {
                continue;
            }

            var timestampRaw = line[1..closingBracketIndex];
            if (!TryParseLrcTimestampMilliseconds(timestampRaw, out var milliseconds))
            {
                continue;
            }

            var text = line[(closingBracketIndex + 1)..].Trim();
            lines.Add(new SynchronizedLyric(text, SynchronizedLyric.BuildLrcTimestamp(milliseconds), milliseconds));
        }

        return lines.Count > 0;
    }

    private static bool TryReadMusixmatchRichsync(MusixmatchMacroCallsBody body, out List<SynchronizedLyric> lines)
    {
        lines = new List<SynchronizedLyric>();
        if (!TryGetMusixmatchCallBody(body, "track.richsync.get", out var callBody))
        {
            return false;
        }

        var richsyncBody = callBody.Richsync?.RichsyncBody;
        if (string.IsNullOrWhiteSpace(richsyncBody))
        {
            return false;
        }

        List<MusixmatchRichsyncLine>? richsyncLines;
        try
        {
            richsyncLines = JsonSerializer.Deserialize<List<MusixmatchRichsyncLine>>(richsyncBody);
        }
        catch (JsonException)
        {
            return false;
        }

        if (richsyncLines == null || richsyncLines.Count == 0)
        {
            return false;
        }

        foreach (var richsyncLine in richsyncLines)
        {
            if (richsyncLine.Ts < 0 || string.IsNullOrWhiteSpace(richsyncLine.Text))
            {
                continue;
            }

            var milliseconds = (int)Math.Round(richsyncLine.Ts * 1000d);
            lines.Add(new SynchronizedLyric(
                richsyncLine.Text.Trim(),
                SynchronizedLyric.BuildLrcTimestamp(milliseconds),
                milliseconds));
        }

        return lines.Count > 0;
    }

    private static bool TryParseLrcTimestampMilliseconds(string value, out int milliseconds)
    {
        milliseconds = 0;
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var minutes))
        {
            return false;
        }

        var secondsParts = parts[1].Split('.', StringSplitOptions.TrimEntries);
        if (secondsParts.Length != 2
            || !int.TryParse(secondsParts[0], out var seconds)
            || !int.TryParse(secondsParts[1], out var hundredths))
        {
            return false;
        }

        milliseconds = (minutes * 60 * 1000) + (seconds * 1000) + (hundredths * 10);
        return true;
    }

    private static bool TryGetMusixmatchCallBody(
        MusixmatchMacroCallsBody body,
        string key,
        out MusixmatchBody callBody)
    {
        callBody = new MusixmatchBody();
        if (body.MacroCalls == null
            || !body.MacroCalls.TryGetValue(key, out var response)
            || response?.Message?.Body == null)
        {
            return false;
        }

        callBody = response.Message.Body;
        return true;
    }

    private sealed class MusixmatchMacroCallsBody
    {
        [JsonPropertyName("macro_calls")]
        public Dictionary<string, MusixmatchMacroCallResponse>? MacroCalls { get; set; }
    }

    private sealed class MusixmatchMacroCallResponse
    {
        [JsonPropertyName("message")]
        public MusixmatchMacroCallMessage? Message { get; set; }
    }

    private sealed class MusixmatchMacroCallMessage
    {
        [JsonPropertyName("body")]
        public MusixmatchBody? Body { get; set; }
    }

    private sealed class MusixmatchBody
    {
        [JsonPropertyName("lyrics")]
        public MusixmatchLyrics? Lyrics { get; set; }

        [JsonPropertyName("subtitle_list")]
        public List<MusixmatchSubtitleWrap>? SubtitleList { get; set; }

        [JsonPropertyName("richsync")]
        public MusixmatchRichsync? Richsync { get; set; }
    }

    private sealed class MusixmatchLyrics
    {
        [JsonPropertyName("lyrics_body")]
        public string? LyricsBody { get; set; }
    }

    private sealed class MusixmatchSubtitleWrap
    {
        [JsonPropertyName("subtitle")]
        public MusixmatchSubtitle? Subtitle { get; set; }
    }

    private sealed class MusixmatchSubtitle
    {
        [JsonPropertyName("subtitle_body")]
        public string? SubtitleBody { get; set; }
    }

    private sealed class MusixmatchRichsync
    {
        [JsonPropertyName("richsync_body")]
        public string? RichsyncBody { get; set; }
    }

    private sealed class MusixmatchRichsyncLine
    {
        [JsonPropertyName("ts")]
        public float Ts { get; set; }

        [JsonPropertyName("x")]
        public string? Text { get; set; }
    }

    private async Task<string?> ResolveDeezerLyricsTrackIdAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (TryResolveDeezerTrackIdFromTrack(track, out var deezerTrackId))
        {
            return deezerTrackId;
        }

        var songLinkTrackId = await TryResolveDeezerTrackIdFromSongLinkAsync(track, settings, cancellationToken);
        if (!string.IsNullOrWhiteSpace(songLinkTrackId))
        {
            return songLinkTrackId;
        }

        var isrcTrackId = await TryResolveDeezerTrackIdByIsrcAsync(track);
        if (!string.IsNullOrWhiteSpace(isrcTrackId))
        {
            return isrcTrackId;
        }

        return null;
    }

    private static bool TryResolveDeezerTrackIdFromTrack(Track track, out string? deezerTrackId)
    {
        return TrackIdNormalization.TryResolveDeezerTrackId(track, out deezerTrackId, track.LyricsId);
    }

    private async Task<string?> TryResolveDeezerTrackIdFromSongLinkAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (_songLinkResolver == null)
        {
            return null;
        }

        try
        {
            SongLinkResult? songLink = null;
            var userCountry = string.IsNullOrWhiteSpace(settings.DeezerCountry) ? null : settings.DeezerCountry;

            if (!string.IsNullOrWhiteSpace(track.DownloadURL))
            {
                songLink = await _songLinkResolver.ResolveByUrlAsync(track.DownloadURL, userCountry, cancellationToken);
            }

            if (songLink == null
                && string.Equals(track.Source, SpotifyProvider, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(track.SourceId))
            {
                songLink = await _songLinkResolver.ResolveSpotifyTrackAsync(track.SourceId, cancellationToken);
            }

            if (songLink == null
                && track.Urls != null
                && track.Urls.TryGetValue(SpotifyUrlKey, out var spotifyUrl)
                && !string.IsNullOrWhiteSpace(spotifyUrl))
            {
                songLink = await _songLinkResolver.ResolveByUrlAsync(spotifyUrl, userCountry, cancellationToken);
            }

            if (songLink != null)
            {
                if (TrackIdNormalization.TryNormalizeDeezerTrackId(songLink.DeezerId, out var deezerTrackId))
                {
                    return deezerTrackId;
                }

                if (TrackIdNormalization.TryNormalizeDeezerTrackId(songLink.DeezerUrl, out deezerTrackId))
                {
                    return deezerTrackId;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "SongLink resolution failed for Deezer lyrics track id lookup for track {TrackId}", track.Id);            }
        }

        return null;
    }

    private async Task<string?> TryResolveDeezerTrackIdByIsrcAsync(Track track)
    {
        if (_deezerClient == null || string.IsNullOrWhiteSpace(track.ISRC))
        {
            return null;
        }

        try
        {
            var deezerTrack = await _deezerClient.GetTrackByIsrcAsync(track.ISRC);
            if (TrackIdNormalization.TryNormalizeDeezerTrackId(deezerTrack?.Id, out var deezerTrackId))
            {
                return deezerTrackId;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Unable to resolve Deezer track id via ISRC {Isrc}", track.ISRC);            }
        }

        return null;
    }

    private async Task<LyricsBase> ResolveSpotifyLyricsAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var spotifyTrackId = await ResolveSpotifyLyricsTrackIdAsync(track, settings, cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifyTrackId))
        {
            return CreateLyricsError("Unable to resolve Spotify track ID for lyrics.");
        }

        var authContext = await ResolveSpotifyAuthContextAsync(cancellationToken);
        if (authContext is null)
        {
            return CreateLyricsError("Spotify auth is not available for lyrics.");
        }

        var accessToken = await ResolveSpotifyWebPlayerAccessTokenAsync(authContext, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return CreateLyricsError("Unable to obtain Spotify web player token for lyrics.");
        }

        foreach (var url in BuildSpotifyLyricsUrls(spotifyTrackId, settings))
        {
            var payload = await TryFetchSpotifyLyricsPayloadAsync(url, accessToken, authContext.UserAgent, cancellationToken);
            if (payload is null)
            {
                continue;
            }

            var parsed = ParseSpotifyLyricsPayload(payload.Value);
            if (parsed.IsLoaded())
            {
                return parsed;
            }
        }

        return CreateLyricsError($"Spotify lyrics not available for track {spotifyTrackId}.");
    }

    private async Task<string?> ResolveSpotifyLyricsTrackIdAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (TryResolveSpotifyTrackIdFromTrack(track, out var spotifyTrackId))
        {
            return spotifyTrackId;
        }

        var songLinkTrackId = await TryResolveSpotifyTrackIdFromSongLinkAsync(track, settings, cancellationToken);
        if (!string.IsNullOrWhiteSpace(songLinkTrackId))
        {
            return songLinkTrackId;
        }

        return null;
    }

    private static bool TryResolveSpotifyTrackIdFromTrack(Track track, out string? spotifyTrackId)
    {
        return TrackIdNormalization.TryResolveSpotifyTrackId(track, out spotifyTrackId);
    }

    private async Task<string?> TryResolveSpotifyTrackIdFromSongLinkAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (_songLinkResolver == null)
        {
            return null;
        }

        SongLinkResult? songLink = null;
        var userCountry = string.IsNullOrWhiteSpace(settings.DeezerCountry) ? null : settings.DeezerCountry;

        if (!string.IsNullOrWhiteSpace(track.DownloadURL))
        {
            songLink = await _songLinkResolver.ResolveByUrlAsync(track.DownloadURL, userCountry, cancellationToken);
        }

        if (songLink == null
            && string.Equals(track.Source, DeezerProvider, StringComparison.OrdinalIgnoreCase)
            && (TrackIdNormalization.TryNormalizeDeezerTrackId(track.SourceId, out var deezerTrackId)
                || TrackIdNormalization.TryNormalizeDeezerTrackId(track.Id, out deezerTrackId)))
        {
            songLink = await _songLinkResolver.ResolveByDeezerTrackIdAsync(deezerTrackId!, cancellationToken);
        }

        if (songLink == null
            && track.Urls != null
            && track.Urls.TryGetValue(DeezerUrlKey, out var deezerUrl)
            && !string.IsNullOrWhiteSpace(deezerUrl))
        {
            songLink = await _songLinkResolver.ResolveByUrlAsync(deezerUrl, userCountry, cancellationToken);
        }

        if (songLink != null)
        {
            if (TrackIdNormalization.TryNormalizeSpotifyTrackId(songLink.SpotifyId, out var spotifyTrackId))
            {
                return spotifyTrackId;
            }

            if (TrackIdNormalization.TryNormalizeSpotifyTrackId(songLink.SpotifyUrl, out spotifyTrackId))
            {
                return spotifyTrackId;
            }
        }

        return null;
    }

    private async Task<SpotifyAuthContext?> ResolveSpotifyAuthContextAsync(CancellationToken cancellationToken)
    {
        var state = await TryLoadSpotifyAuthStateAsync(cancellationToken);
        if (state is null)
        {
            return null;
        }

        var defaultUserAgent = string.IsNullOrWhiteSpace(state.UserAgent)
            ? DefaultSpotifyWebPlayerUserAgent
            : state.UserAgent;

        if (!string.IsNullOrWhiteSpace(state.SpDc))
        {
            return new SpotifyAuthContext(state.SpDc, defaultUserAgent);
        }

        foreach (var rawBlobPath in state.BlobPaths)
        {
            var fromBlob = await TryExtractSpotifyAuthContextFromBlobAsync(rawBlobPath, defaultUserAgent, cancellationToken);
            if (fromBlob is not null)
            {
                return fromBlob;
            }
        }

        return null;
    }

    private async Task<SpotifyAuthState?> TryLoadSpotifyAuthStateAsync(CancellationToken cancellationToken)
    {
        var dataRoot = ResolveSpotifyDataRoot();
        var statePath = Path.Join(dataRoot, "autotag", "spotify.json");
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(statePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var spotify = doc.RootElement;
            if (spotify.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var spDc = TryReadJsonString(spotify, "webPlayerSpDc");
            var userAgent = TryReadJsonString(spotify, "webPlayerUserAgent");
            var activeAccount = TryReadJsonString(spotify, "activeAccount");
            var blobPaths = ReadSpotifyBlobPaths(spotify, activeAccount);

            return new SpotifyAuthState(spDc, userAgent, blobPaths);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read Spotify auth state from {Path}", statePath);            }
            return null;
        }
    }

    private static List<string> ReadSpotifyBlobPaths(JsonElement spotify, string? activeAccount)
    {
        var blobPaths = new List<string>();
        if (!spotify.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
        {
            return blobPaths;
        }

        AppendActiveAccountBlobPath(accounts, activeAccount, blobPaths);
        AppendRemainingBlobPaths(accounts, blobPaths);
        return blobPaths;
    }

    private static void AppendActiveAccountBlobPath(JsonElement accounts, string? activeAccount, List<string> blobPaths)
    {
        if (string.IsNullOrWhiteSpace(activeAccount))
        {
            return;
        }

        foreach (var account in accounts.EnumerateArray())
        {
            var accountName = TryReadJsonString(account, "name");
            if (!string.Equals(accountName, activeAccount, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var activeBlobPath = TryReadJsonString(account, "blobPath");
            if (!string.IsNullOrWhiteSpace(activeBlobPath))
            {
                blobPaths.Add(activeBlobPath);
            }
        }
    }

    private static void AppendRemainingBlobPaths(JsonElement accounts, List<string> blobPaths)
    {
        foreach (var blobPath in accounts.EnumerateArray()
                     .Select(account => TryReadJsonString(account, "blobPath"))
                     .Where(static path => !string.IsNullOrWhiteSpace(path))
                     .Cast<string>())
        {
            if (blobPaths.Any(existing => string.Equals(existing, blobPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            blobPaths.Add(blobPath);
        }
    }

    private async Task<SpotifyAuthContext?> TryExtractSpotifyAuthContextFromBlobAsync(
        string rawBlobPath,
        string fallbackUserAgent,
        CancellationToken cancellationToken)
    {
        var blobPath = ResolveSpotifyBlobPath(rawBlobPath);
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(blobPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("auth_type", out _) && root.TryGetProperty("auth_data", out _))
            {
                return null;
            }

            if (!root.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var spDc = ReadSpotifyCookies(cookies);

            if (string.IsNullOrWhiteSpace(spDc))
            {
                return null;
            }

            var userAgent = TryReadJsonString(root, "userAgent");
            if (string.IsNullOrWhiteSpace(userAgent))
            {
                userAgent = fallbackUserAgent;
            }

            return new SpotifyAuthContext(spDc, userAgent!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to parse Spotify blob payload at {Path}", blobPath);            }
            return null;
        }
    }

    private static string? ReadSpotifyCookies(JsonElement cookies)
    {
        string? spDc = null;
        foreach (var cookie in cookies.EnumerateArray())
        {
            var name = TryReadJsonString(cookie, "name");
            var value = TryReadJsonString(cookie, "value");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (name.Equals("sp_dc", StringComparison.OrdinalIgnoreCase))
            {
                spDc = value;
            }
        }

        return spDc;
    }

    private async Task<string?> ResolveSpotifyWebPlayerAccessTokenAsync(
        SpotifyAuthContext context,
        CancellationToken cancellationToken)
    {
        var cacheKey = context.SpDc;
        if (string.Equals(cacheKey, _cachedSpotifyAccessTokenKey, StringComparison.Ordinal)
            && DateTimeOffset.UtcNow < _cachedSpotifyAccessTokenExpiry
            && !string.IsNullOrWhiteSpace(_cachedSpotifyAccessToken))
        {
            return _cachedSpotifyAccessToken;
        }

        await _spotifyTokenGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(cacheKey, _cachedSpotifyAccessTokenKey, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow < _cachedSpotifyAccessTokenExpiry
                && !string.IsNullOrWhiteSpace(_cachedSpotifyAccessToken))
            {
                return _cachedSpotifyAccessToken;
            }

            var response = await FetchSpotifyWebPlayerTokenAsync(context, cancellationToken);
            if (response is null || string.IsNullOrWhiteSpace(response.AccessToken))
            {
                return null;
            }

            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(45);
            if (response.ExpiresAtUnixMs.HasValue && response.ExpiresAtUnixMs.Value > 0)
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(response.ExpiresAtUnixMs.Value).AddMinutes(-2);
            }

            _cachedSpotifyAccessToken = response.AccessToken;
            _cachedSpotifyAccessTokenExpiry = expiresAt;
            _cachedSpotifyAccessTokenKey = cacheKey;
            return response.AccessToken;
        }
        finally
        {
            _spotifyTokenGate.Release();
        }
    }

    private async Task<SpotifyTokenResponse?> FetchSpotifyWebPlayerTokenAsync(
        SpotifyAuthContext context,
        CancellationToken cancellationToken)
    {
        var cookieContainer = new CookieContainer();
        cookieContainer.Add(new Cookie("sp_dc", context.SpDc, "/", ".spotify.com")
        {
            Secure = true,
            HttpOnly = true
        });

        using var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(context.UserAgent)
            ? DefaultSpotifyWebPlayerUserAgent
            : context.UserAgent);

        await WarmSpotifyWebPlayerSessionAsync(client, cancellationToken);

        var (totp, version) = SpotifyWebPlayerTotp.Generate();
        if (!string.IsNullOrWhiteSpace(totp))
        {
            var apiTokenUrl =
                $"{SpotifyOpenBaseUrl}{SpotifyOpenTokenPath}?reason=init&productType=web-player&totp={totp}&totpVer={version}&totpServer={totp}";
            var primary = await RequestSpotifyWebPlayerTokenAsync(client, apiTokenUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(primary?.AccessToken))
            {
                return primary;
            }
        }

        var fallbackUrl = $"{SpotifyOpenBaseUrl}{SpotifyOpenFallbackTokenPath}?reason=transport&productType=web_player";
        var fallback = await RequestSpotifyWebPlayerTokenAsync(client, fallbackUrl, cancellationToken);
        return !string.IsNullOrWhiteSpace(fallback?.AccessToken) ? fallback : null;
    }

    private async Task<SpotifyTokenResponse?> RequestSpotifyWebPlayerTokenAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(ApplicationJson);
            request.Headers.Referrer = new Uri(SpotifyOpenRootUrl);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var accessToken = TryReadJsonString(root, "accessToken");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            long? expiresAt = null;
            if (root.TryGetProperty("accessTokenExpirationTimestampMs", out var expiry) &&
                expiry.TryGetInt64(out var expiryMs))
            {
                expiresAt = expiryMs;
            }

            bool? isAnonymous = null;
            if (root.TryGetProperty("isAnonymous", out var anon))
            {
                isAnonymous = anon.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
            }

            var country = TryReadJsonString(root, "country");
            return new SpotifyTokenResponse(accessToken, expiresAt, country, isAnonymous);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify web player token request failed for {Url}", url);            }
            return null;
        }
    }

    private static async Task WarmSpotifyWebPlayerSessionAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SpotifyOpenBaseUrl);
            request.Headers.Accept.ParseAdd("text/html");
            request.Headers.Referrer = new Uri(SpotifyOpenRootUrl);
            using var response = await client.SendAsync(request, cancellationToken);
            _ = response.Content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort warmup only.
        }
    }

    private async Task<JsonElement?> TryFetchSpotifyLyricsPayloadAsync(
        string url,
        string accessToken,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(LyricsClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.ParseAdd(ApplicationJson);
            request.Headers.Referrer = new Uri(SpotifyOpenRootUrl);
            request.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");
            request.Headers.TryAddWithoutValidation("origin", SpotifyOpenBaseUrl);
            if (!string.IsNullOrWhiteSpace(userAgent))
            {
                request.Headers.TryAddWithoutValidation(UserAgentHeader, userAgent);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify lyrics request failed for {Url}", url);            }
            return null;
        }
    }

    private static LyricsSource ParseSpotifyLyricsPayload(JsonElement payload)
    {
        var lyrics = new LyricsSource();
        var source = payload;
        if (payload.TryGetProperty(LyricsType, out var nestedLyrics) &&
            nestedLyrics.ValueKind == JsonValueKind.Object)
        {
            source = nestedLyrics;
        }

        var syncType = TryReadJsonString(source, "syncType") ?? TryReadJsonString(payload, "syncType");
        var isSynced = string.Equals(syncType, "LINE_SYNCED", StringComparison.OrdinalIgnoreCase);

        if (source.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            var unsyncedLines = new List<string>();
            foreach (var line in lines.EnumerateArray())
            {
                ProcessSpotifyLyricsLine(lyrics, line, isSynced, unsyncedLines);
            }

            if (unsyncedLines.Count > 0)
            {
                lyrics.UnsyncedLyrics = string.Join('\n', unsyncedLines);
            }
        }

        if (!lyrics.IsLoaded())
        {
            var plain = TryReadJsonString(source, "text");
            if (!string.IsNullOrWhiteSpace(plain))
            {
                lyrics.UnsyncedLyrics = plain;
            }
        }

        if (!lyrics.IsLoaded())
        {
            lyrics.SetErrorMessage("Spotify lyrics payload contained no usable lines.");
        }

        return lyrics;
    }

    private static void ProcessSpotifyLyricsLine(
        LyricsSource lyrics,
        JsonElement line,
        bool isSynced,
        List<string> unsyncedLines)
    {
        if (line.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var words = ReadSpotifyLineWords(line);
        if (string.IsNullOrWhiteSpace(words))
        {
            return;
        }

        unsyncedLines.Add(words);
        if (!isSynced)
        {
            return;
        }

        var syncedLyric = BuildSyncedSpotifyLine(line, words);
        if (syncedLyric != null)
        {
            lyrics.SyncedLyrics?.Add(syncedLyric);
        }
    }

    private static string ReadSpotifyLineWords(JsonElement line)
    {
        var words = TryReadJsonString(line, "words")
            ?? TryReadJsonString(line, "text")
            ?? string.Empty;
        return words.Replace("\r", string.Empty).TrimEnd();
    }

    private static SynchronizedLyric? BuildSyncedSpotifyLine(JsonElement line, string words)
    {
        var startMs = TryParseMilliseconds(line, "startTimeMs");
        if (!startMs.HasValue || startMs.Value < 0)
        {
            return null;
        }

        var endMs = TryParseMilliseconds(line, "endTimeMs");
        var duration = endMs.HasValue && endMs.Value > startMs.Value
            ? endMs.Value - startMs.Value
            : 0;
        return new SynchronizedLyric(
            words,
            SynchronizedLyric.BuildLrcTimestamp(startMs.Value),
            startMs.Value,
            duration);
    }

    private static int? TryParseMilliseconds(JsonElement line, string property)
    {
        if (!line.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static IEnumerable<string> BuildSpotifyLyricsUrls(string spotifyTrackId, DeezSpoTagSettings settings)
    {
        var markets = new List<string> { "from_token" };
        if (!string.IsNullOrWhiteSpace(settings.DeezerCountry))
        {
            var country = settings.DeezerCountry.Trim().ToUpperInvariant();
            if (country.Length == 2 && !markets.Contains(country, StringComparer.OrdinalIgnoreCase))
            {
                markets.Add(country);
            }
        }

        foreach (var market in markets)
        {
            yield return $"https://spclient.wg.spotify.com/color-lyrics/v2/track/{spotifyTrackId}?format=json&market={market}";
            yield return $"https://spclient.wg.spotify.com/lyrics/v1/track/{spotifyTrackId}?format=json&market={market}";
        }
    }

    private static string ResolveSpotifyDataRoot()
    {
        return AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir());
    }

    private static string? ResolveSpotifyBlobPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var trimmed = rawPath.Trim();
        if (File.Exists(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        var dataRoot = ResolveSpotifyDataRoot();
        var candidates = new List<string>();
        if (Path.IsPathRooted(trimmed))
        {
            var fileName = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                candidates.Add(Path.Join(dataRoot, SpotifyDataDir, BlobsDir, fileName));
            }
        }
        else
        {
            candidates.Add(Path.Join(dataRoot, trimmed));
            candidates.Add(Path.Join(dataRoot, SpotifyDataDir, BlobsDir, trimmed));
            var fileName = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                candidates.Add(Path.Join(dataRoot, SpotifyDataDir, BlobsDir, fileName));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? TryReadJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static LyricsSource CreateLyricsError(string message)
    {
        var lyrics = new LyricsSource();
        lyrics.SetErrorMessage(message);
        return lyrics;
    }

    private sealed record SpotifyAuthState(string? SpDc, string? UserAgent, List<string> BlobPaths);
    private sealed record SpotifyAuthContext(string SpDc, string UserAgent);
    private sealed record SpotifyTokenResponse(string AccessToken, long? ExpiresAtUnixMs, string? Country, bool? IsAnonymous);

    /// <summary>
    /// Get lyrics using refreezer's dual API approach
    /// Primary: Pipe API with GraphQL, Fallback: Legacy GW API
    /// </summary>
    public async Task<LyricsBase> GetLyricsAsync(string trackId, string arl, string? sid = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(trackId))
        {
            _logger.LogWarning("Track ID is null or empty");
            return LyricsNew.CreateError("Track ID is required");
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Fetching lyrics for track {TrackId}", trackId);        }

        // Primary: Try Pipe API with GraphQL
        var lyricsFromPipe = await GetLyricsFromPipeApiAsync(trackId, arl, sid, cancellationToken);

        if (lyricsFromPipe.IsLoaded())
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Successfully fetched lyrics from Pipe API for track {TrackId}", trackId);            }
            return lyricsFromPipe;
        }

        if (!string.IsNullOrEmpty(lyricsFromPipe.ErrorMessage))
        {
            _logger.LogWarning("Pipe API failed for track {TrackId}: {Error}", trackId, lyricsFromPipe.ErrorMessage);
        }

        // Fallback: Try legacy GW API
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Falling back to GW API for track {TrackId}", trackId);        }
        var lyricsFromGw = await GetLyricsFromGwApiAsync(trackId, arl, sid, cancellationToken);

        if (lyricsFromGw.IsLoaded())
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Successfully fetched lyrics from GW API for track {TrackId}", trackId);            }
            return lyricsFromGw;
        }

        if (!string.IsNullOrEmpty(lyricsFromGw.ErrorMessage))
        {
            _logger.LogWarning("GW API also failed for track {TrackId}: {Error}", trackId, lyricsFromGw.ErrorMessage);
        }

        // Both APIs failed, return the Pipe API error (usually more informative)
        _logger.LogError("Both Pipe API and GW API failed for track {TrackId}", trackId);
        return lyricsFromPipe;
    }

    /// <summary>
    /// Get lyrics from modern Pipe API using GraphQL
    /// </summary>
    private async Task<LyricsNew> GetLyricsFromPipeApiAsync(string trackId, string arl, string? sid, CancellationToken cancellationToken)
    {
        try
        {
            // Get JWT token for authentication
            var jwtToken = await _jwtTokenService.GetJsonWebTokenAsync(arl, sid, cancellationToken);
            if (string.IsNullOrEmpty(jwtToken))
            {
                return LyricsNew.CreateError("Failed to obtain JWT token for Pipe API");
            }

            // Create GraphQL query
            var queryString = """
                query SynchronizedTrackLyrics($trackId: String!) {
                  track(trackId: $trackId) {
                    id
                    isExplicit
                    lyrics {
                      id
                      copyright
                      text
                      writers
                      synchronizedLines {
                        lrcTimestamp
                        line
                        milliseconds
                        duration
                      }
                    }
                  }
                }
                """;

            var requestBody = new
            {
                operationName = "SynchronizedTrackLyrics",
                variables = new { trackId },
                query = queryString
            };

            using var httpClient = _httpClientFactory.CreateClient(LyricsClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, DeezerPipeApiUrl);

            // Set headers
            request.Headers.Add(UserAgentHeader, "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/96.0.4664.110 Safari/537.36");
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Authorization", $"Bearer {jwtToken}");

            // Set cookies
            var cookieValue = $"arl={arl}";
            if (!string.IsNullOrEmpty(sid))
            {
                cookieValue += $"; sid={sid}";
            }
            request.Headers.Add("Cookie", cookieValue);

            // Set content
            var jsonContent = JsonSerializer.Serialize(requestBody);
            request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, ApplicationJson);

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return LyricsNew.CreateError($"Pipe API request failed with status: {response.StatusCode}");
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrEmpty(responseContent))
            {
                return LyricsNew.CreateError("Empty response from Pipe API");
            }

            // Parse response
            using var jsonDoc = JsonDocument.Parse(responseContent);
            return new LyricsNew(jsonDoc.RootElement);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Pipe API response for track {TrackId}", trackId);
            return LyricsNew.CreateError($"Failed to parse Pipe API response: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed for Pipe API track {TrackId}", trackId);
            return LyricsNew.CreateError($"Pipe API request failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error in Pipe API for track {TrackId}", trackId);
            return LyricsNew.CreateError($"Unexpected Pipe API error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get lyrics from legacy GW API as fallback
    /// </summary>
    private async Task<LyricsClassic> GetLyricsFromGwApiAsync(string trackId, string arl, string? sid, CancellationToken cancellationToken)
    {
        try
        {
            // First get track data to access lyrics - EXACT PORT: Use SNG_ID like deemix
            var trackData = await CallGwApiAsync("deezer.pageTrack", $"{{\"SNG_ID\": \"{trackId}\"}}", arl, sid, cancellationToken);

            if (trackData == null)
            {
                return LyricsClassic.CreateError("Failed to get track data from GW API");
            }

            // Check if track data has lyrics
            if (trackData.HasValue && trackData.Value.TryGetProperty("results", out var resultsElement) &&
                resultsElement.TryGetProperty("LYRICS", out var lyricsElement))
            {
                return new LyricsClassic(lyricsElement);
            }

            // Try direct lyrics API call - EXACT PORT: Use SNG_ID like deemix
            var lyricsData = await CallGwApiAsync("song.getLyrics", $"{{\"SNG_ID\": \"{trackId}\"}}", arl, sid, cancellationToken);

            if (lyricsData == null)
            {
                return LyricsClassic.CreateError("No lyrics data from GW API");
            }

            if (lyricsData.HasValue && lyricsData.Value.TryGetProperty("results", out var lyricsResultsElement))
            {
                return new LyricsClassic(lyricsResultsElement);
            }

            return LyricsClassic.CreateError("No lyrics found in GW API response");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error in GW API lyrics fetch for track {TrackId}", trackId);
            return LyricsClassic.CreateError($"GW API error: {ex.Message}");
        }
    }

    /// <summary>
    /// Call Deezer GW API
    /// </summary>
    private async Task<JsonElement?> CallGwApiAsync(string method, string body, string arl, string? sid, CancellationToken cancellationToken)
    {
        try
        {
            var apiToken = await GetGwTokenAsync(arl, sid, cancellationToken);
            if (RequiresGwApiToken(method) && string.IsNullOrWhiteSpace(apiToken))
            {
                _logger.LogWarning("Unable to obtain GW token for method {Method}", method);
                return null;
            }

            var url = $"https://www.deezer.com/ajax/gw-light.php?method={method}&input=3&api_version=1.0&api_token={apiToken ?? "null"}";
            using var httpClient = _httpClientFactory.CreateClient(LyricsClientName);
            using var request = BuildGwApiRequest(url, body, arl, sid);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var root = await ParseGwApiResponseAsync(response, cancellationToken);
            if (!root.HasValue)
            {
                return null;
            }

            var retried = await TryRetryGwApiCallOnInvalidTokenAsync(root.Value, method, body, arl, sid, cancellationToken);
            return retried ?? root;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error calling GW API method {Method}", method);
            return null;
        }
    }

    private static bool RequiresGwApiToken(string method)
    {
        return !string.Equals(method, "deezer.getUserData", StringComparison.Ordinal);
    }

    private static HttpRequestMessage BuildGwApiRequest(string url, string body, string arl, string? sid)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add(UserAgentHeader, "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/96.0.4664.110 Safari/537.36");
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        request.Headers.Add("Cookie", BuildGwCookie(arl, sid));
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, ApplicationJson);
        return request;
    }

    private static string BuildGwCookie(string arl, string? sid)
    {
        return string.IsNullOrEmpty(sid)
            ? $"arl={arl}"
            : $"arl={arl}; sid={sid}";
    }

    private async Task<JsonElement?> ParseGwApiResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GW API request failed with status: {StatusCode}", response.StatusCode);
            return null;
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrEmpty(responseContent))
        {
            return null;
        }

        using var jsonDoc = JsonDocument.Parse(responseContent);
        return jsonDoc.RootElement.Clone();
    }

    private async Task<JsonElement?> TryRetryGwApiCallOnInvalidTokenAsync(
        JsonElement root,
        string method,
        string body,
        string arl,
        string? sid,
        CancellationToken cancellationToken)
    {
        if (!IsInvalidGwTokenError(root))
        {
            return null;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("GW token invalid, refreshing token for {Method}", method);
        }

        _cachedGwToken = null;
        _cachedGwTokenExpiry = DateTime.MinValue;
        var refreshed = await GetGwTokenAsync(arl, sid, cancellationToken, forceRefresh: true);
        if (string.IsNullOrWhiteSpace(refreshed))
        {
            return null;
        }

        return await CallGwApiAsync(method, body, arl, sid, cancellationToken);
    }

    private static bool IsInvalidGwTokenError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var errorElement) || errorElement.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        var errorText = errorElement.ToString();
        return !string.IsNullOrWhiteSpace(errorText)
               && errorText.Contains("VALID_TOKEN_REQUIRED", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> GetGwTokenAsync(string arl, string? sid, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_cachedGwToken) && DateTime.UtcNow < _cachedGwTokenExpiry)
        {
            return _cachedGwToken;
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient(LyricsClientName);
            var url = DeezerGwUserDataUrl;
            using var request = new HttpRequestMessage(HttpMethod.Post, url);

            request.Headers.Add(UserAgentHeader, "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/96.0.4664.110 Safari/537.36");
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            var cookieValue = $"arl={arl}";
            if (!string.IsNullOrEmpty(sid))
            {
                cookieValue += $"; sid={sid}";
            }
            request.Headers.Add("Cookie", cookieValue);
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, ApplicationJson);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GW token bootstrap failed: {StatusCode}", response.StatusCode);
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(responseContent))
            {
                return null;
            }

            using var jsonDoc = JsonDocument.Parse(responseContent);
            var root = jsonDoc.RootElement;
            if (root.TryGetProperty("results", out var results) &&
                results.TryGetProperty("checkForm", out var tokenElement))
            {
                var token = tokenElement.GetString();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    _cachedGwToken = token;
                    _cachedGwTokenExpiry = DateTime.UtcNow.AddMinutes(GwTokenTtlMinutes);
                    return token;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to obtain GW token");
        }

        return null;
    }

    /// <summary>
    /// Validate and prepare lyrics for .lrc file creation
    /// Only creates .lrc files for synchronized lyrics
    /// </summary>
    public bool ShouldCreateLrcFile(LyricsBase lyrics)
    {
        if (lyrics == null)
        {
            _logger.LogDebug("No lyrics provided for LRC validation");
            return false;
        }

        if (!string.IsNullOrEmpty(lyrics.ErrorMessage))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Lyrics have error message, skipping LRC creation: {Error}", lyrics.ErrorMessage);            }
            return false;
        }

        if (!lyrics.IsSynced())
        {
            _logger.LogDebug("Lyrics are not synchronized, skipping LRC creation");
            return false;
        }

        if (!HasLyricsLines(lyrics.SyncedLyrics))
        {
            _logger.LogDebug("No synchronized lyrics lines found, skipping LRC creation");
            return false;
        }

        var syncedLyrics = lyrics.SyncedLyrics ?? new List<SynchronizedLyric>();
        var validLines = syncedLyrics.Count(l => l.IsValid());
        if (validLines < 1)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Insufficient valid synchronized lyrics lines ({Count}), skipping LRC creation", validLines);            }
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Lyrics validation passed, LRC file can be created with {Count} lines", validLines);        }
        return true;
    }

    /// <summary>
    /// Generate LRC content from lyrics with metadata
    /// </summary>
    public string GenerateLrcContent(LyricsBase lyrics, string? title = null, string? artist = null, string? album = null)
    {
        if (!ShouldCreateLrcFile(lyrics))
        {
            return string.Empty;
        }

        return lyrics.GenerateLrcContent(title, artist, album);
    }

    private sealed record LegacyLyricsPaths(string LrcPath, string TtmlPath, string TxtPath);

    private async Task<LyricsBase?> TryResolveCompatibilityLyricsAsync(string trackId, CancellationToken cancellationToken)
    {
        var arl = await _authenticatedDeezerService.GetArlAsync();
        if (string.IsNullOrEmpty(arl))
        {
            _logger.LogWarning("No ARL available for lyrics fetching for track {TrackId}", trackId);
            return null;
        }

        var sid = await _authenticatedDeezerService.GetSidAsync();
        var lyrics = await GetLyricsAsync(trackId, arl, sid, cancellationToken);
        if (lyrics == null || !string.IsNullOrEmpty(lyrics.ErrorMessage))
        {
            _logger.LogWarning("Failed to fetch lyrics for track {TrackId}: {Error}", trackId, lyrics?.ErrorMessage ?? "Unknown error");
            return null;
        }

        return lyrics;
    }

    private static LegacyLyricsPaths BuildLegacyLyricsPaths(string filePath, string filename)
    {
        return new LegacyLyricsPaths(
            LrcPath: Path.Join(filePath, $"{filename}.lrc"),
            TtmlPath: Path.Join(filePath, $"{filename}.ttml"),
            TxtPath: Path.Join(filePath, $"{filename}.txt"));
    }

    private async Task<bool> SaveLegacyRichLyricsAsync(
        LyricsBase lyrics,
        Track track,
        LegacyLyricsPaths paths,
        CancellationToken cancellationToken)
    {
        var savedRichLyrics = false;
        if (lyrics.IsSynced())
        {
            var lrcContent = GenerateLrcContent(lyrics, track.Title, track.MainArtist?.Name, track.Album?.Title);
            if (!string.IsNullOrEmpty(lrcContent))
            {
                await System.IO.File.WriteAllTextAsync(paths.LrcPath, lrcContent, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully saved synchronized lyrics to {LrcPath}", paths.LrcPath);
                }

                savedRichLyrics = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(lyrics.TtmlLyrics))
        {
            await System.IO.File.WriteAllTextAsync(paths.TtmlPath, lyrics.TtmlLyrics, cancellationToken);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Successfully saved TTML lyrics to {TtmlPath}", paths.TtmlPath);
            }

            savedRichLyrics = true;
        }

        return savedRichLyrics;
    }

    private async Task SaveLegacyUnsyncedLyricsAsync(
        LyricsBase lyrics,
        Track track,
        LegacyLyricsPaths paths,
        bool hasRichLyrics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(lyrics.UnsyncedLyrics))
        {
            return;
        }

        var hasExistingRichLyrics = System.IO.File.Exists(paths.LrcPath) || System.IO.File.Exists(paths.TtmlPath);
        if (hasRichLyrics || hasExistingRichLyrics)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Skipping unsynchronized lyrics for track {TrackId} because LRC or TTML exists.", track.Id);
            }

            return;
        }

        await System.IO.File.WriteAllTextAsync(paths.TxtPath, lyrics.UnsyncedLyrics, cancellationToken);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Successfully saved unsynchronized lyrics to {TxtPath}", paths.TxtPath);
        }
    }

    /// <summary>
    /// Save lyrics to file (Downloader compatibility method)
    /// </summary>
    public async Task SaveLyricsAsync(Track track, string filePath, string filename, CancellationToken cancellationToken = default)
    {
        try
        {
            var lyrics = await TryResolveCompatibilityLyricsAsync(track.Id, cancellationToken);
            if (lyrics == null)
            {
                return;
            }

            var paths = BuildLegacyLyricsPaths(filePath, filename);
            var hasRichLyrics = await SaveLegacyRichLyricsAsync(lyrics, track, paths, cancellationToken);
            await SaveLegacyUnsyncedLyricsAsync(lyrics, track, paths, hasRichLyrics, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error saving lyrics for track {TrackId}", track.Id);
        }
    }

    /// <summary>
    /// Save lyrics to file using priority implementation
    /// Priority: .lrc for synchronized lyrics, .txt for unsynchronized lyrics as fallback
    /// </summary>
    public async Task SaveLyricsAsync(
        Track track,
        (string FilePath, string Filename, string ExtrasPath, string CoverPath, string ArtistPath) paths,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("SaveLyricsAsync called for track {TrackId}, SaveLyrics: {SaveLyrics}, SyncedLyrics: {SyncedLyrics}",
                track.Id, settings.SaveLyrics, settings.SyncedLyrics);        }

        // Check if lyrics saving is enabled (either general lyrics or synced lyrics)
        if (!ShouldHandleLyricsBySettings(settings))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Lyrics saving disabled for track {TrackId}", track.Id);            }
            return;
        }

        try
        {
            var lyrics = await ResolveLyricsAsync(track, settings, cancellationToken);
            if (lyrics == null)
            {
                _logger.LogWarning("Lyrics resolution returned null for track {TrackId}", track.Id);
                return;
            }

            await SaveLyricsAsync(lyrics, track, paths, settings, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error saving lyrics for track {TrackId}", track.Id);
        }
    }

    /// <summary>
    /// Save lyrics to file using already-fetched lyrics data.
    /// </summary>
    public async Task SaveLyricsAsync(
        LyricsBase lyrics,
        Track track,
        (string FilePath, string Filename, string ExtrasPath, string CoverPath, string ArtistPath) paths,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("SaveLyricsAsync (prefetched) called for track {TrackId}, SaveLyrics: {SaveLyrics}, SyncedLyrics: {SyncedLyrics}",
                track.Id, settings.SaveLyrics, settings.SyncedLyrics);        }

        if (!ShouldHandleLyricsBySettings(settings))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Lyrics saving disabled for track {TrackId}", track.Id);            }
            return;
        }

        if (lyrics == null || !string.IsNullOrEmpty(lyrics.ErrorMessage))
        {
            _logger.LogWarning("Failed to fetch lyrics for track {TrackId}: {Error}", track.Id, lyrics?.ErrorMessage ?? "Unknown error");
            return;
        }

        var saveState = new LyricsSaveState(paths, settings);
        var overwriteSidecar = ShouldOverwriteLyricsSidecar(settings);
        saveState.HadExistingLrc = System.IO.File.Exists(saveState.LrcPath);
        saveState.HadExistingTtml = System.IO.File.Exists(saveState.TtmlPath);
        saveState.HadExistingTxt = System.IO.File.Exists(saveState.TxtPath);

        await TrySaveSyncedLrcAsync(lyrics, track, settings, overwriteSidecar, saveState, cancellationToken);
        await TrySaveLrcFromTtmlAsync(lyrics, overwriteSidecar, saveState, cancellationToken);
        EnsureTtmlFromSyncedLyricsWhenRequested(lyrics, track, settings);
        await TrySaveTtmlAsync(lyrics, settings, overwriteSidecar, saveState, cancellationToken);
        await TrySaveUnsyncedTxtAsync(lyrics, track, settings, overwriteSidecar, saveState, cancellationToken);
        RemoveTxtWhenRichLyricsExist(saveState);

        if (!saveState.SavedLyrics)
        {
            _logger.LogWarning("No lyrics saved for track {TrackId} - SaveLyrics: {SaveLyrics}, SyncedLyrics: {SyncedLyrics}, HasSynced: {HasSynced}, HasUnsynced: {HasUnsynced}",
                track.Id, settings.SaveLyrics, settings.SyncedLyrics, lyrics.IsSynced(), !string.IsNullOrEmpty(lyrics.UnsyncedLyrics));
        }
    }

    private sealed class LyricsSaveState((string FilePath, string Filename, string ExtrasPath, string CoverPath, string ArtistPath) paths, DeezSpoTagSettings settings)
    {
        public string LrcPath { get; } = Path.Join(paths.FilePath, $"{paths.Filename}.lrc");
        public string TtmlPath { get; } = Path.Join(paths.FilePath, $"{paths.Filename}.ttml");
        public string TxtPath { get; } = Path.Join(paths.FilePath, $"{paths.Filename}.txt");
        public bool RichOutputRequested { get; } = ShouldSaveSyncedLrc(settings) || ShouldOutputTtmlBySettings(settings);
        public bool HadExistingLrc { get; set; }
        public bool HadExistingTtml { get; set; }
        public bool HadExistingTxt { get; set; }
        public bool SavedLyrics { get; set; }
        public bool SavedLrc { get; set; }
        public bool SavedTtml { get; set; }
    }

    private async Task TrySaveSyncedLrcAsync(
        LyricsBase lyrics,
        Track track,
        DeezSpoTagSettings settings,
        bool overwriteSidecar,
        LyricsSaveState state,
        CancellationToken cancellationToken)
    {
        if (!ShouldSaveSyncedLrc(settings))
        {
            return;
        }
        if (!lyrics.IsSynced())
        {
            return;
        }

        try
        {
            var lrcContent = GenerateLrcContent(lyrics, track.Title, track.MainArtist?.Name, track.Album?.Title);
            if (string.IsNullOrEmpty(lrcContent))
            {
                _logger.LogWarning("Generated LRC content is empty for track {TrackId}", track.Id);
                return;
            }

            if (!overwriteSidecar && state.HadExistingLrc)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Keeping existing LRC sidecar at {LrcPath}", state.LrcPath);                }
            }
            else
            {
                await System.IO.File.WriteAllTextAsync(state.LrcPath, lrcContent, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully saved synchronized lyrics to {LrcPath}", state.LrcPath);                }
            }
            state.SavedLyrics = true;
            state.SavedLrc = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error downloading synchronized lyrics.");
        }
    }

    private async Task TrySaveLrcFromTtmlAsync(
        LyricsBase lyrics,
        bool overwriteSidecar,
        LyricsSaveState state,
        CancellationToken cancellationToken)
    {
        if (state.SavedLrc || !state.RichOutputRequested || string.IsNullOrWhiteSpace(lyrics.TtmlLyrics))
        {
            return;
        }

        try
        {
            var lrcFromTtml = DeezSpoTag.Services.Apple.AppleLyricsService.ConvertTtmlToLrcPublic(lyrics.TtmlLyrics);
            if (string.IsNullOrWhiteSpace(lrcFromTtml))
            {
                return;
            }

            if (!overwriteSidecar && state.HadExistingLrc)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Keeping existing LRC sidecar at {LrcPath}", state.LrcPath);                }
            }
            else
            {
                await System.IO.File.WriteAllTextAsync(state.LrcPath, lrcFromTtml, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully saved LRC (from TTML) to {LrcPath}", state.LrcPath);                }
            }
            state.SavedLyrics = true;
            state.SavedLrc = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error converting TTML to LRC.");
        }
    }

    private void EnsureTtmlFromSyncedLyricsWhenRequested(LyricsBase lyrics, Track track, DeezSpoTagSettings settings)
    {
        if (!ShouldOutputTtmlBySettings(settings)
            || !string.IsNullOrWhiteSpace(lyrics.TtmlLyrics)
            || !lyrics.IsSynced())
        {
            return;
        }

        var synthesizedTtml = TryBuildTtmlFromSyncedLyrics(lyrics);
        if (string.IsNullOrWhiteSpace(synthesizedTtml))
        {
            return;
        }

        lyrics.TtmlLyrics = synthesizedTtml;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Synthesized TTML lyrics from synced lines for track {TrackId}", track.Id);        }
    }

    private async Task TrySaveTtmlAsync(
        LyricsBase lyrics,
        DeezSpoTagSettings settings,
        bool overwriteSidecar,
        LyricsSaveState state,
        CancellationToken cancellationToken)
    {
        if (!ShouldSaveTtml(settings, lyrics))
        {
            return;
        }

        try
        {
            if (!overwriteSidecar && state.HadExistingTtml)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Keeping existing TTML sidecar at {TtmlPath}", state.TtmlPath);                }
            }
            else
            {
                await System.IO.File.WriteAllTextAsync(state.TtmlPath, lyrics.TtmlLyrics!, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully saved TTML lyrics to {TtmlPath}", state.TtmlPath);                }
            }
            state.SavedLyrics = true;
            state.SavedTtml = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error downloading TTML lyrics.");
        }
    }

    private async Task TrySaveUnsyncedTxtAsync(
        LyricsBase lyrics,
        Track track,
        DeezSpoTagSettings settings,
        bool overwriteSidecar,
        LyricsSaveState state,
        CancellationToken cancellationToken)
    {
        if (!ShouldSavePlainLyrics(settings) || string.IsNullOrEmpty(lyrics.UnsyncedLyrics))
        {
            return;
        }

        if (ShouldSkipUnsyncedTxtWrite(state))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Skipping unsynchronized lyrics for track {TrackId} because LRC or TTML exists.", track.Id);            }
            return;
        }

        await TryWriteUnsyncedTxtAsync(lyrics.UnsyncedLyrics, overwriteSidecar, state, cancellationToken);
    }

    private static bool ShouldSkipUnsyncedTxtWrite(LyricsSaveState state)
    {
        var hasExistingRichLyrics = state.SavedLrc
            || state.SavedTtml
            || state.HadExistingLrc
            || state.HadExistingTtml
            || System.IO.File.Exists(state.LrcPath)
            || System.IO.File.Exists(state.TtmlPath);
        return state.RichOutputRequested && (state.SavedLrc || state.SavedTtml || hasExistingRichLyrics);
    }

    private async Task TryWriteUnsyncedTxtAsync(
        string unsyncedLyrics,
        bool overwriteSidecar,
        LyricsSaveState state,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!overwriteSidecar && state.HadExistingTxt)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Keeping existing TXT lyrics sidecar at {TxtPath}", state.TxtPath);                }
            }
            else
            {
                await System.IO.File.WriteAllTextAsync(state.TxtPath, unsyncedLyrics, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully saved unsynchronized lyrics to {TxtPath}", state.TxtPath);                }
            }
            state.SavedLyrics = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error downloading unsynchronized lyrics.");
        }
    }

    private void RemoveTxtWhenRichLyricsExist(LyricsSaveState state)
    {
        if (!state.RichOutputRequested
            || !(state.SavedLrc || state.SavedTtml || state.HadExistingLrc || state.HadExistingTtml)
            || !System.IO.File.Exists(state.TxtPath))
        {
            return;
        }

        try
        {
            System.IO.File.Delete(state.TxtPath);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Removed TXT lyrics sidecar after rich-lyrics upgrade at {TxtPath}", state.TxtPath);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed removing TXT lyrics sidecar after upgrade at {TxtPath}", state.TxtPath);            }
        }
    }

    private static bool ShouldOverwriteLyricsSidecar(DeezSpoTagSettings settings)
    {
        var overwritePolicy = string.IsNullOrWhiteSpace(settings.OverwriteFile)
            ? "y"
            : settings.OverwriteFile.Trim().ToLowerInvariant();
        return overwritePolicy is "y" or "t";
    }

    private static bool ShouldSavePlainLyrics(DeezSpoTagSettings settings)
    {
        return IsLyricsGateEnabled(settings)
            && IsLyricsTypeSelected(settings, UnsyncedLyricsType)
            && settings.SaveLyrics;
    }

    private static bool ShouldSaveSyncedLrc(DeezSpoTagSettings settings)
    {
        var outputFormat = NormalizeLyricsOutputFormat(settings.LrcFormat);
        return IsLyricsGateEnabled(settings)
            && outputFormat is "lrc" or "both"
            && (IsLyricsTypeSelected(settings, LyricsType)
                || IsLyricsTypeSelected(settings, SyllableLyricsType));
    }

    private static bool ShouldSaveTtml(DeezSpoTagSettings settings, LyricsBase lyrics)
    {
        if (!ShouldOutputTtmlBySettings(settings))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(lyrics?.TtmlLyrics);
    }

    private static bool ShouldOutputTtmlBySettings(DeezSpoTagSettings settings)
    {
        if (!IsLyricsGateEnabled(settings))
        {
            return false;
        }

        var outputFormat = NormalizeLyricsOutputFormat(settings.LrcFormat);
        return outputFormat is "ttml" or "both";
    }

    private static string? TryBuildTtmlFromSyncedLyrics(LyricsBase lyrics)
    {
        var syncedLines = lyrics.SyncedLyrics?
            .Where(line => line != null && line.IsValid() && !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line!.Milliseconds)
            .ToList();
        if (syncedLines == null || syncedLines.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.AppendLine("<tt xmlns=\"http://www.w3.org/ns/ttml\">");
        builder.AppendLine("  <body>");
        builder.AppendLine("    <div>");

        for (var i = 0; i < syncedLines.Count; i++)
        {
            var line = syncedLines[i]!;
            var beginMs = Math.Max(0, line.Milliseconds);
            var endMs = beginMs + 4000;
            if (i + 1 < syncedLines.Count)
            {
                var nextStart = Math.Max(beginMs + 1, syncedLines[i + 1]!.Milliseconds);
                endMs = Math.Max(beginMs + 1, nextStart);
            }

            var text = WebUtility.HtmlEncode(line.Text!.Trim());
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var begin = FormatTtmlTimestamp(beginMs);
            var end = FormatTtmlTimestamp(endMs);
            builder.AppendLine($"      <p begin=\"{begin}\" end=\"{end}\">{text}</p>");
        }

        builder.AppendLine("    </div>");
        builder.AppendLine("  </body>");
        builder.AppendLine("</tt>");
        return builder.ToString();
    }

    private static string FormatTtmlTimestamp(int milliseconds)
    {
        var clamped = Math.Max(0, milliseconds);
        var ts = TimeSpan.FromMilliseconds(clamped);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    private static string NormalizeLyricsOutputFormat(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "lrc" => "lrc",
            "ttml" => "ttml",
            "both" => "both",
            LyricsType => "both",
            "lrc+ttml" => "both",
            "ttml+lrc" => "both",
            _ => "both"
        };
    }

    private static bool ShouldHandleLyricsBySettings(DeezSpoTagSettings settings)
    {
        return LyricsSettingsPolicy.CanFetchLyrics(settings);
    }

    private static bool IsLyricsGateEnabled(DeezSpoTagSettings settings)
    {
        return LyricsSettingsPolicy.IsLyricsGateEnabled(settings);
    }

    private static bool IsLyricsTypeSelected(DeezSpoTagSettings settings, string type)
    {
        return ParseSelectedLyricsTypes(settings).Contains(type);
    }

    private static HashSet<string> ParseSelectedLyricsTypes(DeezSpoTagSettings settings)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var normalized in (settings.LrcType ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(NormalizeLyricsTypeToken)
                     .Where(static token => !string.IsNullOrWhiteSpace(token)))
        {
            selected.Add(normalized);
        }

        if (selected.Count == 0)
        {
            selected.Add(LyricsType);
            selected.Add(SyllableLyricsType);
            selected.Add(UnsyncedLyricsType);
        }

        return selected;
    }

    private static string NormalizeLyricsTypeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        return token.Trim().ToLowerInvariant() switch
        {
            LyricsType => LyricsType,
            "synced-lyrics" => LyricsType,
            SyllableLyricsType => SyllableLyricsType,
            "time-synced-lyrics" => SyllableLyricsType,
            "timesynced-lyrics" => SyllableLyricsType,
            "time_synced_lyrics" => SyllableLyricsType,
            "syllablelyrics" => SyllableLyricsType,
            UnsyncedLyricsType => UnsyncedLyricsType,
            "unsyncedlyrics" => UnsyncedLyricsType,
            "unsynced" => UnsyncedLyricsType,
            "unsynchronized-lyrics" => UnsyncedLyricsType,
            "unsynchronised-lyrics" => UnsyncedLyricsType,
            _ => string.Empty
        };
    }
}
