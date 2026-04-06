using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Crypto;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwTrack = DeezSpoTag.Core.Models.Deezer.GwTrack;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/deezer/stream")]
[Authorize]
public class DeezerStreamApiController : ControllerBase
{
    private const string Mp3128Format = "MP3_128";
    private const string Mp3320Format = "MP3_320";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PlaybackContextTtl = TimeSpan.FromMinutes(30);
    private static readonly SemaphoreSlim LoginGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, CachedPlaybackContext> PlaybackContextCache =
        new(StringComparer.Ordinal);

    private readonly DeezerClient _deezerClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DeezerGatewayService _gatewayService;
    private readonly ILoginStorageService _loginStorage;
    private readonly DecryptionStreamProcessor _streamProcessor;
    private readonly ILogger<DeezerStreamApiController> _logger;

    public DeezerStreamApiController(
        DeezerClient deezerClient,
        IHttpClientFactory httpClientFactory,
        DeezerGatewayService gatewayService,
        ILoginStorageService loginStorage,
        DecryptionStreamProcessor streamProcessor,
        ILogger<DeezerStreamApiController> logger)
    {
        _deezerClient = deezerClient;
        _httpClientFactory = httpClientFactory;
        _gatewayService = gatewayService;
        _loginStorage = loginStorage;
        _streamProcessor = streamProcessor;
        _logger = logger;
    }

    public static void ClearPlaybackContextCache()
    {
        PlaybackContextCache.Clear();
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> StreamTrack(
        string id,
        [FromQuery] DeezerStreamQuery query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = "Track id is required." });
        }

        if (string.Equals(query.Type, "episode", StringComparison.OrdinalIgnoreCase))
        {
            return await StreamEpisodeAsync(id, cancellationToken);
        }

        var authenticatedSessionAvailable = false;
        await EnsureLoggedInAsync();
        authenticatedSessionAvailable = _deezerClient.LoggedIn;

        if (authenticatedSessionAvailable && await TryStreamAuthenticatedTrackAsync(
                id,
                query.Quality,
                query.StreamTrackId,
                query.TrackToken,
                query.Md5Origin,
                query.MediaVersion,
                cancellationToken))
        {
            return new EmptyResult();
        }

        if (!authenticatedSessionAvailable)
        {
            return Unauthorized(new { error = "Deezer login required for full-track playback." });
        }

        return NotFound(new { error = "Full track stream unavailable." });
    }

    [HttpGet("context/{id}")]
    public async Task<IActionResult> GetTrackPlaybackContext(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = "Track id is required." });
        }

        await EnsureLoggedInAsync();
        if (!_deezerClient.LoggedIn)
        {
            return Unauthorized(new { available = false, reasonCode = "auth_required", error = "Deezer login required." });
        }

        var context = await ResolvePlaybackContextAsync(id, forceRefresh: false);
        if (context == null)
        {
            return Ok(new { available = false, reasonCode = "context_unavailable" });
        }

        return Ok(new
        {
            available = true,
            deezerId = context.DeezerId,
            streamTrackId = context.StreamTrackId,
            trackToken = context.TrackToken,
            md5origin = context.Md5Origin,
            mv = context.MediaVersion
        });
    }

    private async Task<bool> TryStreamAuthenticatedTrackAsync(
        string id,
        int? qualityHint,
        string? hintedStreamTrackId,
        string? hintedTrackToken,
        string? hintedMd5Origin,
        string? hintedMediaVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = TryBuildHintedPlaybackContext(
                id,
                hintedStreamTrackId,
                hintedTrackToken,
                hintedMd5Origin,
                hintedMediaVersion);

            if (context is null || string.IsNullOrWhiteSpace(context.TrackToken))
            {
                return false;
            }

            var format = ResolvePreviewFormat(qualityHint);
            var (resolvedContext, mediaResult) = await FetchMediaResultAsync(id, context, format, cancellationToken);
            context = resolvedContext;

            if (string.IsNullOrWhiteSpace(mediaResult.Url))
            {
                return false;
            }

            var track = new DeezSpoTag.Core.Models.Track
            {
                Id = context.StreamTrackId,
                Title = context.Title,
                TrackToken = context.TrackToken
            };

            var downloadObject = new SingleDownloadObject
            {
                Track = track,
                Title = context.Title,
                Bitrate = format == Mp3320Format ? 320 : 128
            };

            Response.ContentType = "audio/mpeg";
            await Response.StartAsync(cancellationToken);
            await _streamProcessor.StreamTrackToStreamAsync(
                Response.Body,
                track,
                mediaResult.Url,
                downloadObject,
                listener: null,
                cancellationToken: cancellationToken);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Authenticated Deezer stream failed for track TrackId");
            if (Response.HasStarted)
            {
                HttpContext.Abort();
                return true;
            }

            Response.ContentType = null;
            return false;
        }
    }

    private async Task<(DeezerPlaybackContext context, DeezerMediaResult mediaResult)> FetchMediaResultAsync(
        string deezerId,
        DeezerPlaybackContext context,
        string format,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mediaResult = await _deezerClient.GetTrackUrlWithStatusAsync(context.TrackToken, format);
        if (!string.IsNullOrWhiteSpace(mediaResult.Url) || mediaResult.ErrorCode != 2001)
        {
            return (context, mediaResult);
        }

        var refreshedContext = await ResolvePlaybackContextFromTrackListDataAsync(
            lookupId: context.StreamTrackId,
            fallbackDeezerId: deezerId,
            cancellationToken: cancellationToken);
        if (refreshedContext is null || string.IsNullOrWhiteSpace(refreshedContext.TrackToken))
        {
            return (context, mediaResult);
        }

        cancellationToken.ThrowIfCancellationRequested();
        mediaResult = await _deezerClient.GetTrackUrlWithStatusAsync(refreshedContext.TrackToken, format);
        CachePlaybackContext(refreshedContext);
        return (refreshedContext, mediaResult);
    }

    private async Task<DeezerPlaybackContext?> ResolvePlaybackContextFromTrackListDataAsync(
        string lookupId,
        string fallbackDeezerId,
        CancellationToken cancellationToken)
    {
        var normalizedLookupId = NormalizeOptional(lookupId);
        var normalizedFallbackDeezerId = NormalizeOptional(fallbackDeezerId);

        if (string.IsNullOrWhiteSpace(normalizedLookupId) || !normalizedLookupId.All(char.IsDigit))
        {
            return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tracks = await _deezerClient.Gw.GetTracksAsync(new List<string> { normalizedLookupId });
            var refreshedTrack = tracks.FirstOrDefault(t => t != null && !string.IsNullOrWhiteSpace(t.TrackToken));
            if (refreshedTrack == null)
            {
                return null;
            }

            var refreshedDeezerId = refreshedTrack.SngId > 0
                ? refreshedTrack.SngId.ToString(CultureInfo.InvariantCulture)
                : normalizedFallbackDeezerId;
            if (string.IsNullOrWhiteSpace(refreshedDeezerId))
            {
                refreshedDeezerId = normalizedLookupId;
            }
            var refreshedStreamTrackId = ResolveStreamTrackId(refreshedTrack);
            if (string.IsNullOrWhiteSpace(refreshedStreamTrackId))
            {
                refreshedStreamTrackId = refreshedDeezerId;
            }

            var context = new DeezerPlaybackContext(
                refreshedDeezerId,
                refreshedStreamTrackId,
                refreshedTrack.TrackToken,
                NormalizeOptional(refreshedTrack.Md5Origin),
                refreshedTrack.MediaVersion > 0
                    ? refreshedTrack.MediaVersion.ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                NormalizeOptional(refreshedTrack.SngTitle));
            CachePlaybackContext(context);
            return context;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve Deezer playback context via track list data for track {TrackId}", normalizedLookupId);
            return null;
        }
    }

    private async Task<DeezerPlaybackContext?> ResolvePlaybackContextAsync(
        string deezerId,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deezerId) || !deezerId.All(char.IsDigit))
        {
            return null;
        }

        if (!forceRefresh && TryGetCachedPlaybackContext(deezerId, out var cached))
        {
            return cached;
        }

        return await ResolvePlaybackContextFromTrackListDataAsync(
            lookupId: deezerId,
            fallbackDeezerId: deezerId,
            cancellationToken: cancellationToken);
    }

    private static DeezerPlaybackContext? TryBuildHintedPlaybackContext(
        string deezerId,
        string? streamTrackId,
        string? trackToken,
        string? md5Origin,
        string? mediaVersion)
    {
        var normalizedStreamTrackId = NormalizeOptional(streamTrackId);
        var normalizedTrackToken = NormalizeOptional(trackToken);
        if (string.IsNullOrWhiteSpace(normalizedStreamTrackId) || string.IsNullOrWhiteSpace(normalizedTrackToken))
        {
            return null;
        }

        var context = new DeezerPlaybackContext(
            deezerId,
            normalizedStreamTrackId,
            normalizedTrackToken,
            NormalizeOptional(md5Origin),
            NormalizeOptional(mediaVersion),
            string.Empty);
        CachePlaybackContext(context);
        return context;
    }

    private string ResolvePreviewFormat(int? qualityHint)
    {
        if (qualityHint.HasValue && qualityHint.Value <= 1)
        {
            return Mp3128Format;
        }

        return _deezerClient.CanStreamAtBitrate(3) ? Mp3320Format : Mp3128Format;
    }

    private static bool TryGetCachedPlaybackContext(string deezerId, out DeezerPlaybackContext context)
    {
        context = default!;
        if (!PlaybackContextCache.TryGetValue(deezerId, out var entry))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - entry.CachedAt > PlaybackContextTtl)
        {
            PlaybackContextCache.TryRemove(deezerId, out _);
            return false;
        }

        context = entry.Context;
        return true;
    }

    private static void CachePlaybackContext(DeezerPlaybackContext context)
    {
        if (string.IsNullOrWhiteSpace(context.DeezerId) || string.IsNullOrWhiteSpace(context.TrackToken))
        {
            return;
        }

        PlaybackContextCache[context.DeezerId] = new CachedPlaybackContext(context, DateTimeOffset.UtcNow);
    }

    private static string ResolveStreamTrackId(GwTrack track)
    {
        if (track.FallbackId.HasValue && track.FallbackId.Value > 0)
        {
            return track.FallbackId.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (TryExtractFallbackId(track.Fallback, out var fallbackId) && fallbackId > 0)
        {
            return fallbackId.ToString(CultureInfo.InvariantCulture);
        }

        return track.SngId > 0 ? track.SngId.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static bool TryExtractFallbackId(object? fallback, out int fallbackId)
    {
        fallbackId = 0;
        if (fallback == null)
        {
            return false;
        }

        switch (fallback)
        {
            case int intValue when intValue > 0:
                fallbackId = intValue;
                return true;
            case long longValue when longValue > 0 && longValue <= int.MaxValue:
                fallbackId = (int)longValue;
                return true;
            case string stringValue when int.TryParse(stringValue, out var parsed) && parsed > 0:
                fallbackId = parsed;
                return true;
            case JObject obj:
            {
                var sngId = obj.Value<string>("SNG_ID") ?? obj.Value<string>("id");
                if (int.TryParse(sngId, out var parsedObj) && parsedObj > 0)
                {
                    fallbackId = parsedObj;
                    return true;
                }
                break;
            }
            case JValue value:
            {
                if (int.TryParse(value.ToString(), out var parsedValue) && parsedValue > 0)
                {
                    fallbackId = parsedValue;
                    return true;
                }
                break;
            }
        }

        return false;
    }

    private static string NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private sealed record DeezerPlaybackContext(
        string DeezerId,
        string StreamTrackId,
        string TrackToken,
        string Md5Origin,
        string MediaVersion,
        string Title);

    private sealed record CachedPlaybackContext(
        DeezerPlaybackContext Context,
        DateTimeOffset CachedAt);

    public sealed class DeezerStreamQuery
    {
        [FromQuery]
        public string? Type { get; set; }

        [FromQuery(Name = "q")]
        public int? Quality { get; set; }

        [FromQuery]
        public string? StreamTrackId { get; set; }

        [FromQuery]
        public string? TrackToken { get; set; }

        [FromQuery(Name = "md5origin")]
        public string? Md5Origin { get; set; }

        [FromQuery(Name = "mv")]
        public string? MediaVersion { get; set; }
    }

    private async Task<IActionResult> StreamEpisodeAsync(string id, CancellationToken cancellationToken)
    {
        var streamUrl = await ResolveEpisodeStreamUrlAsync(id, cancellationToken);
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return NotFound(new { error = "Episode stream unavailable." });
        }

        try
        {
            using var response = await _httpClientFactory
                .CreateClient("DeezSpoTagDownload")
                .GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Episode stream failed." });
            }

            Response.ContentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await stream.CopyToAsync(Response.Body, cancellationToken);
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to stream Deezer episode EpisodeId");
            if (Response.HasStarted)
            {
                HttpContext.Abort();
                return new EmptyResult();
            }

            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Episode stream failed." });
        }
    }

    private async Task<string?> ResolveEpisodeStreamUrlAsync(string episodeId, CancellationToken cancellationToken)
    {
        string? showId = null;
        try
        {
            using var response = await _httpClientFactory
                .CreateClient("DeezSpoTagDownload")
                .GetAsync($"https://api.deezer.com/episode/{episodeId}", cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _))
            {
                var gatewayFallback = await ResolveEpisodeStreamUrlFromGatewayAsync(episodeId);
                if (!string.IsNullOrWhiteSpace(gatewayFallback))
                {
                    return gatewayFallback;
                }
            }
            else
            {
                var directUrl = GetJsonString(root, "direct_stream_url")
                                ?? GetJsonString(root, "direct_url")
                                ?? GetJsonString(root, "url");

                if (!DeezerEpisodeStreamResolver.IsDeezerEpisodePage(directUrl ?? string.Empty))
                {
                    return directUrl;
                }

                showId = GetJsonString(root, "show_id");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve episode stream URL for EpisodeId");
        }

        var gatewayStream = await ResolveEpisodeStreamUrlFromGatewayAsync(episodeId);
        if (!string.IsNullOrWhiteSpace(gatewayStream))
        {
            return gatewayStream;
        }

        var pageMetadata = await ResolveEpisodeMetadataFromPublicPageAsync(episodeId, cancellationToken);
        var pageDirectUrl = GetDictString(pageMetadata, "EPISODE_DIRECT_STREAM_URL")
                            ?? GetDictString(pageMetadata, "direct_stream_url")
                            ?? GetDictString(pageMetadata, "EPISODE_URL")
                            ?? GetDictString(pageMetadata, "url");
        if (!string.IsNullOrWhiteSpace(pageDirectUrl) && !DeezerEpisodeStreamResolver.IsDeezerEpisodePage(pageDirectUrl))
        {
            return pageDirectUrl;
        }

        showId ??= GetDictString(pageMetadata, "SHOW_ID")
                  ?? GetDictString(pageMetadata, "show_id");
        showId ??= await ResolveEpisodeShowIdAsync(episodeId);
        return await ResolveEpisodeStreamUrlFromShowAsync(showId, episodeId);
    }

    private async Task<string?> ResolveEpisodeStreamUrlFromGatewayAsync(string episodeId)
    {
        try
        {
            var page = await _gatewayService.GetEpisodePageAsync(episodeId);
            var results = page["results"] as JObject ?? page;
            var episode = results["EPISODE"] as JObject
                          ?? results["episode"] as JObject
                          ?? results;

            var streamUrl = episode?.Value<string>("EPISODE_DIRECT_STREAM_URL")
                            ?? episode?.Value<string>("EPISODE_URL");

            return DeezerEpisodeStreamResolver.IsDeezerEpisodePage(streamUrl ?? string.Empty) ? null : streamUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve episode stream URL via gateway for EpisodeId");
            return null;
        }
    }

    private async Task<string?> ResolveEpisodeShowIdAsync(string episodeId)
    {
        try
        {
            var page = await _gatewayService.GetEpisodePageAsync(episodeId);
            var results = page["results"] as JObject ?? page;
            var episode = results["EPISODE"] as JObject
                          ?? results["episode"] as JObject
                          ?? results;
            var showId = episode?.Value<string>("SHOW_ID")
                         ?? episode?.Value<string>("show_id");
            return string.IsNullOrWhiteSpace(showId) ? null : showId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve show id for Deezer episode EpisodeId");
            return null;
        }
    }

    private async Task<string?> ResolveEpisodeStreamUrlFromShowAsync(string? showId, string episodeId)
    {
        if (string.IsNullOrWhiteSpace(showId))
        {
            return null;
        }

        try
        {
            var showPage = await _gatewayService.GetShowPageAsync(showId);
            return DeezerEpisodeStreamResolver.ResolveStreamUrl(
                showPage,
                episodeId,
                includeLinkFallback: true,
                rejectDeezerEpisodePages: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve episode stream URL via show page for EpisodeId");
        }

        return null;
    }

    private static string? GetJsonString(System.Text.Json.JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }

    private async Task<Dictionary<string, object>> ResolveEpisodeMetadataFromPublicPageAsync(
        string episodeId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClientFactory
                .CreateClient("DeezSpoTagDownload")
                .GetAsync($"https://www.deezer.com/episode/{episodeId}", cancellationToken);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var match = Regex.Match(
                html,
                @"window\.__DZR_APP_STATE__\s*=\s*(\{.*?\})\s*</script>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase,
                RegexTimeout);
            if (!match.Success)
            {
                return new Dictionary<string, object>();
            }

            var appStateJson = match.Groups[1].Value;
            return JsonSerializer.Deserialize<Dictionary<string, object>>(appStateJson) ?? new Dictionary<string, object>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve episode metadata from public page for EpisodeId");
            return new Dictionary<string, object>();
        }
    }

    private static string? GetDictString(Dictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            string stringValue => stringValue,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            JsonElement element when element.ValueKind == JsonValueKind.Number => element.GetRawText(),
            _ => value.ToString()
        };
    }

    private async Task EnsureLoggedInAsync()
    {
        if (_deezerClient.LoggedIn)
        {
            return;
        }

        await LoginGate.WaitAsync();
        try
        {
            if (_deezerClient.LoggedIn)
            {
                return;
            }

            var loginData = await _loginStorage.LoadLoginCredentialsAsync();
            var arl = loginData?.Arl;
            if (string.IsNullOrWhiteSpace(arl))
            {
                return;
            }

            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    ClearPlaybackContextCache();
                    await _deezerClient.LoginViaArlAsync(arl);
                    if (_deezerClient.LoggedIn)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (attempt >= maxAttempts)
                    {
                        _logger.LogWarning(ex, "Failed to auto-login Deezer client for streaming.");
                        return;
                    }
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt));
                }
            }
        }
        finally
        {
            LoginGate.Release();
        }
    }
}
