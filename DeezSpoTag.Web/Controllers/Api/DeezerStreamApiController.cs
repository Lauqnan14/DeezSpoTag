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
    private static readonly TimeSpan PreparedMediaTtl = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, CachedPlaybackContext> PlaybackContextCache =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CachedPreparedMediaResult> PreparedMediaCache =
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
        PreparedMediaCache.Clear();
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
            return Unauthorized(new { available = false, error = "Deezer login required." });
        }

        var context = await ResolvePlaybackContextAsync(id, forceRefresh: false);
        if (context == null)
        {
            return Ok(new { available = false });
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

    [HttpGet("warmup/session")]
    public async Task<IActionResult> WarmSession()
    {
        await EnsureLoggedInAsync();
        return Ok(new
        {
            authenticated = _deezerClient.LoggedIn
        });
    }

    [HttpPost("warmup/context")]
    public async Task<IActionResult> WarmPlaybackContexts([FromBody] DeezerPlaybackWarmupRequest? request)
    {
        await EnsureLoggedInAsync();
        if (!_deezerClient.LoggedIn)
        {
            return Unauthorized(new { authenticated = false, items = Array.Empty<object>() });
        }

        var ids = request?.Ids?
            .Select(static id => NormalizeOptional(id))
            .Where(static id => !string.IsNullOrWhiteSpace(id) && id.All(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .ToArray()
            ?? Array.Empty<string>();

        if (ids.Length == 0)
        {
            return Ok(new
            {
                authenticated = true,
                items = Array.Empty<object>()
            });
        }

        var items = new List<object>(ids.Length);
        foreach (var deezerId in ids)
        {
            var context = await ResolvePlaybackContextAsync(deezerId, forceRefresh: false);
            if (context == null)
            {
                continue;
            }

            await PrimePreparedMediaResultAsync(context.DeezerId, context, ResolvePreviewFormat(qualityHint: null));

            items.Add(new
            {
                available = true,
                deezerId = context.DeezerId,
                streamTrackId = context.StreamTrackId,
                trackToken = context.TrackToken,
                md5origin = context.Md5Origin,
                mv = context.MediaVersion,
                previewUrl = BuildPreparedStreamUrl(
                    context.DeezerId,
                    context,
                    ResolvePreviewFormat(qualityHint: null))
            });
        }

        return Ok(new
        {
            authenticated = true,
            items
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

            context ??= await ResolvePlaybackContextAsync(id, forceRefresh: false);
            if (context is null || string.IsNullOrWhiteSpace(context.TrackToken))
            {
                return false;
            }

            var format = ResolvePreviewFormat(qualityHint);
            var (resolvedContext, mediaResult) = await FetchMediaResultAsync(id, context, format);
            context = resolvedContext;

            if (string.IsNullOrWhiteSpace(mediaResult.Url) && format != Mp3128Format)
            {
                format = Mp3128Format;
                (context, mediaResult) = await FetchMediaResultAsync(id, context, format);
            }

            if (string.IsNullOrWhiteSpace(mediaResult.Url)
                && !string.IsNullOrWhiteSpace(context.Md5Origin)
                && !string.IsNullOrWhiteSpace(context.MediaVersion))
            {
                mediaResult = new DeezerMediaResult
                {
                    Url = DeezSpoTag.Services.Crypto.CryptoService.GenerateCryptedStreamUrl(
                        context.StreamTrackId,
                        context.Md5Origin,
                        context.MediaVersion,
                        format)
                };
            }

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

            var hasRange = TryParseSingleRangeHeader(Request.Headers.Range, out var range);

            await _streamProcessor.StreamTrackToStreamAsync(
                Response.Body,
                track,
                mediaResult.Url,
                downloadObject,
                listener: null,
                startBytes: hasRange ? range.Start : 0,
                endBytes: hasRange ? range.End : null,
                configureResponse: async headers =>
                {
                    Response.StatusCode = headers.StatusCode;
                    Response.ContentType = headers.ContentType;
                    Response.Headers["Accept-Ranges"] = "bytes";
                    if (!string.IsNullOrWhiteSpace(headers.ContentRange))
                    {
                        Response.Headers["Content-Range"] = headers.ContentRange;
                    }
                    if (headers.ContentLength.HasValue && headers.ContentLength.Value >= 0)
                    {
                        Response.ContentLength = headers.ContentLength.Value;
                    }
                    await Response.StartAsync(cancellationToken);
                },
                cancellationToken: cancellationToken);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Authenticated Deezer stream failed for track TrackId");
            return false;
        }
    }

    private async Task<(DeezerPlaybackContext context, DeezerMediaResult mediaResult)> FetchMediaResultAsync(
        string deezerId,
        DeezerPlaybackContext context,
        string format)
    {
        if (TryGetCachedPreparedMediaResult(context.TrackToken, format, out var cachedMediaResult))
        {
            return (context, cachedMediaResult);
        }

        var mediaResult = await _deezerClient.GetTrackUrlWithStatusAsync(context.TrackToken, format);
        CachePreparedMediaResult(context.TrackToken, format, mediaResult);
        if (!string.IsNullOrWhiteSpace(mediaResult.Url) || mediaResult.ErrorCode != 2001)
        {
            return (context, mediaResult);
        }

        var refreshedContext = await ResolvePlaybackContextAsync(deezerId, forceRefresh: true);
        if (refreshedContext is null || string.IsNullOrWhiteSpace(refreshedContext.TrackToken))
        {
            return (context, mediaResult);
        }

        mediaResult = await _deezerClient.GetTrackUrlWithStatusAsync(refreshedContext.TrackToken, format);
        CachePlaybackContext(refreshedContext);
        CachePreparedMediaResult(refreshedContext.TrackToken, format, mediaResult);
        return (refreshedContext, mediaResult);
    }

    private async Task PrimePreparedMediaResultAsync(string deezerId, DeezerPlaybackContext context, string format)
    {
        try
        {
            await FetchMediaResultAsync(deezerId, context, format);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to prime prepared media result for track {TrackId}", deezerId);
        }
    }

    private async Task<DeezerPlaybackContext?> ResolvePlaybackContextAsync(
        string deezerId,
        bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(deezerId) || !deezerId.All(char.IsDigit))
        {
            return null;
        }

        if (!forceRefresh && TryGetCachedPlaybackContext(deezerId, out var cached))
        {
            return cached;
        }

        GwTrack? gwTrack;
        try
        {
            gwTrack = await _deezerClient.Gw.GetTrackWithFallbackAsync(deezerId);
        }
        catch (DeezerGatewayException ex) when (IsMissingSongData(ex))
        {
            _logger.LogDebug(ex, "Deezer playback context not found for track {TrackId}", deezerId);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve Deezer playback context for track {TrackId}", deezerId);
            return null;
        }

        if (gwTrack == null || string.IsNullOrWhiteSpace(gwTrack.TrackToken))
        {
            return null;
        }

        var streamTrackId = ResolveStreamTrackId(gwTrack);
        if (string.IsNullOrWhiteSpace(streamTrackId))
        {
            streamTrackId = deezerId;
        }

        var context = new DeezerPlaybackContext(
            deezerId,
            streamTrackId,
            gwTrack.TrackToken,
            NormalizeOptional(gwTrack.Md5Origin),
            gwTrack.MediaVersion > 0 ? gwTrack.MediaVersion.ToString(CultureInfo.InvariantCulture) : string.Empty,
            NormalizeOptional(gwTrack.SngTitle));
        CachePlaybackContext(context);
        return context;
    }

    private static bool IsMissingSongData(DeezerGatewayException ex)
        => ex.Message.Contains("No song data", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("DATA_ERROR", StringComparison.OrdinalIgnoreCase);

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

    private static string BuildPreparedStreamUrl(string deezerId, DeezerPlaybackContext context, string format)
    {
        var qs = new Dictionary<string, string?>
        {
            ["q"] = string.Equals(format, Mp3320Format, StringComparison.OrdinalIgnoreCase) ? "3" : "1",
            ["streamTrackId"] = context.StreamTrackId,
            ["trackToken"] = context.TrackToken,
            ["md5origin"] = context.Md5Origin,
            ["mv"] = context.MediaVersion
        };

        var query = string.Join("&", qs
            .Where(static kvp => !string.IsNullOrWhiteSpace(kvp.Value))
            .Select(static kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value!)}"));

        return string.IsNullOrWhiteSpace(query)
            ? $"/api/deezer/stream/{Uri.EscapeDataString(deezerId)}"
            : $"/api/deezer/stream/{Uri.EscapeDataString(deezerId)}?{query}";
    }

    private static bool TryGetCachedPreparedMediaResult(string trackToken, string format, out DeezerMediaResult mediaResult)
    {
        mediaResult = DeezerMediaResult.Empty();
        var key = BuildPreparedMediaCacheKey(trackToken, format);
        if (!PreparedMediaCache.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - entry.CachedAt > PreparedMediaTtl)
        {
            PreparedMediaCache.TryRemove(key, out _);
            return false;
        }

        mediaResult = entry.Result;
        return !string.IsNullOrWhiteSpace(mediaResult.Url);
    }

    private static void CachePreparedMediaResult(string trackToken, string format, DeezerMediaResult mediaResult)
    {
        if (string.IsNullOrWhiteSpace(trackToken) || string.IsNullOrWhiteSpace(format) || string.IsNullOrWhiteSpace(mediaResult.Url))
        {
            return;
        }

        PreparedMediaCache[BuildPreparedMediaCacheKey(trackToken, format)] =
            new CachedPreparedMediaResult(mediaResult, DateTimeOffset.UtcNow);
    }

    private static string BuildPreparedMediaCacheKey(string trackToken, string format)
        => $"{format}:{trackToken}";

    private static bool TryParseSingleRangeHeader(string? rangeHeader, out StreamRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return false;
        }

        var match = Regex.Match(rangeHeader, @"bytes=(\d+)-(\d*)", RegexOptions.CultureInvariant, RegexTimeout);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var start))
        {
            return false;
        }

        long? end = null;
        if (!string.IsNullOrWhiteSpace(match.Groups[2].Value) && long.TryParse(match.Groups[2].Value, out var parsedEnd))
        {
            end = parsedEnd;
        }

        range = new StreamRange(start, end);
        return true;
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

    private sealed record CachedPreparedMediaResult(
        DeezerMediaResult Result,
        DateTimeOffset CachedAt);

    private readonly record struct StreamRange(long Start, long? End);

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

    public sealed class DeezerPlaybackWarmupRequest
    {
        public List<string>? Ids { get; set; }
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

                if (!IsDeezerEpisodePage(directUrl ?? string.Empty))
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
        if (!string.IsNullOrWhiteSpace(pageDirectUrl) && !IsDeezerEpisodePage(pageDirectUrl))
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

            return IsDeezerEpisodePage(streamUrl ?? string.Empty) ? null : streamUrl;
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

    private static bool IsDeezerEpisodePage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Contains("deezer.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Contains("/episode", StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureLoggedInAsync()
    {
        if (_deezerClient.LoggedIn)
        {
            return;
        }

        try
        {
            var loginData = await _loginStorage.LoadLoginCredentialsAsync();
            if (!string.IsNullOrWhiteSpace(loginData?.Arl))
            {
                await _deezerClient.LoginViaArlAsync(loginData.Arl);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to auto-login Deezer client for streaming.");
        }
    }
}
