using DeezSpoTag.Services.Download.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/availability")]
[Authorize]
public sealed class AvailabilityApiController : ControllerBase
{
    private readonly SongLinkResolver _songLinkResolver;
    private readonly DeezSpoTag.Integrations.Deezer.DeezerClient _deezerClient;
    private readonly DeezSpoTag.Services.Apple.AppleMusicCatalogService _appleCatalogService;
    private readonly DeezSpoTag.Services.Settings.ISettingsService _settingsService;
    private readonly DeezSpoTag.Services.Download.Tidal.TidalDownloadService _tidalDownloadService;
    private readonly ILogger<AvailabilityApiController> _logger;

    public AvailabilityApiController(
        SongLinkResolver songLinkResolver,
        DeezSpoTag.Integrations.Deezer.DeezerClient deezerClient,
        DeezSpoTag.Services.Apple.AppleMusicCatalogService appleCatalogService,
        DeezSpoTag.Services.Settings.ISettingsService settingsService,
        DeezSpoTag.Services.Download.Tidal.TidalDownloadService tidalDownloadService,
        ILogger<AvailabilityApiController> logger)
    {
        _songLinkResolver = songLinkResolver;
        _deezerClient = deezerClient;
        _appleCatalogService = appleCatalogService;
        _settingsService = settingsService;
        _tidalDownloadService = tidalDownloadService;
        _logger = logger;
    }

    [HttpGet("spotify")]
    public async Task<IActionResult> GetSpotifyAvailability(
        [FromQuery] AvailabilityLookupRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await ResolveAvailabilityAsync(request, cancellationToken));
    }

    [HttpGet("deezer")]
    public async Task<IActionResult> GetDeezerAvailability(
        [FromQuery] AvailabilityLookupRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await ResolveAvailabilityAsync(request, cancellationToken));
    }

    [HttpGet("apple")]
    public async Task<IActionResult> GetAppleAvailability(
        [FromQuery] AvailabilityLookupRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await ResolveAvailabilityAsync(request, cancellationToken));
    }

    private async Task<object> ResolveAvailabilityAsync(
        AvailabilityLookupRequest request,
        CancellationToken cancellationToken)
    {
        var input = BuildAvailabilityInput(request);
        var resolvedIsrc = await ResolveIsrcAsync(input, cancellationToken);
        var platformUrls = await ResolvePlatformUrlsAsync(input, resolvedIsrc, cancellationToken);

        var primaryResolution = await ResolvePrimarySongLinkAsync(input, cancellationToken);
        if (primaryResolution.ImmediateResponse is not null)
        {
            return primaryResolution.ImmediateResponse;
        }

        var result = EnsureDeezerFallbackResult(input, primaryResolution.Result);
        ApplyResolvedIsrc(result, resolvedIsrc);
        await TryPopulateDeezerUrlFromIsrcAsync(result, cancellationToken);

        if (result == null)
        {
            return BuildPlatformOnlyResponse(resolvedIsrc, platformUrls);
        }

        return BuildResolvedAvailabilityResponse(input, resolvedIsrc, platformUrls, result);
    }

    private static AvailabilityInput BuildAvailabilityInput(AvailabilityLookupRequest request)
    {
        var spotifyId = request.SpotifyId;
        var normalizedDeezerId = NormalizeDeezerId(request.DeezerId);
        if (string.IsNullOrWhiteSpace(normalizedDeezerId)
            && LooksLikeSpotifyId(request.DeezerId)
            && string.IsNullOrWhiteSpace(spotifyId))
        {
            spotifyId = request.DeezerId;
        }

        return new AvailabilityInput
        {
            SpotifyId = spotifyId,
            Url = request.Url,
            Isrc = request.Isrc,
            NormalizedDeezerId = normalizedDeezerId,
            Title = request.Title,
            Artist = request.Artist,
            DurationMs = request.DurationMs
        };
    }

    private async Task<string?> ResolveIsrcAsync(AvailabilityInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedIsrc = string.IsNullOrWhiteSpace(input.Isrc) ? null : input.Isrc;
        if (!string.IsNullOrWhiteSpace(resolvedIsrc)
            || string.IsNullOrWhiteSpace(input.NormalizedDeezerId))
        {
            return resolvedIsrc;
        }

        try
        {
            var track = await _deezerClient.GetTrack(input.NormalizedDeezerId);
            return track?.Isrc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<PlatformAvailabilityUrls> ResolvePlatformUrlsAsync(
        AvailabilityInput input,
        string? resolvedIsrc,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(resolvedIsrc))
        {
            var qobuzUrl = await _songLinkResolver.ResolveQobuzUrlByIsrcAsync(resolvedIsrc, cancellationToken);
            var appleUrl = await TryResolveAppleUrlByIsrcAsync(resolvedIsrc, cancellationToken);
            var tidalUrl = await TryResolveTidalUrlAsync(input, resolvedIsrc, cancellationToken);
            return new PlatformAvailabilityUrls(qobuzUrl, appleUrl, tidalUrl);
        }

        if (!string.IsNullOrWhiteSpace(input.Title) && !string.IsNullOrWhiteSpace(input.Artist))
        {
            var qobuzUrl = await _songLinkResolver.ResolveQobuzUrlByMetadataAsync(
                input.Title,
                input.Artist,
                input.DurationMs,
                cancellationToken);
            return new PlatformAvailabilityUrls(qobuzUrl, null, null);
        }

        return new PlatformAvailabilityUrls(null, null, null);
    }

    private async Task<string?> TryResolveAppleUrlByIsrcAsync(string resolvedIsrc, CancellationToken cancellationToken)
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
                ? "us"
                : settings.AppleMusic.Storefront;
            using var doc = await _appleCatalogService.GetSongByIsrcAsync(resolvedIsrc, storefront, "en-US", cancellationToken);
            return TryExtractAppleSongUrl(doc.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<string?> TryResolveTidalUrlAsync(
        AvailabilityInput input,
        string resolvedIsrc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Title)
            || string.IsNullOrWhiteSpace(input.Artist))
        {
            return null;
        }

        var durationSeconds = input.DurationMs.HasValue && input.DurationMs.Value > 0
            ? (int)Math.Round(input.DurationMs.Value / 1000d)
            : 0;
        return await _tidalDownloadService.ResolveTrackUrlAsync(
            input.Title,
            input.Artist,
            resolvedIsrc,
            durationSeconds,
            cancellationToken);
    }

    private async Task<PrimaryResolutionResult> ResolvePrimarySongLinkAsync(
        AvailabilityInput input,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(input.NormalizedDeezerId))
        {
            var deezerUrl = BuildDeezerTrackUrl(input.NormalizedDeezerId);
            var result = await _songLinkResolver.ResolveByUrlAsync(deezerUrl, cancellationToken);
            if (result != null)
            {
                result.DeezerUrl = deezerUrl;
            }

            return new PrimaryResolutionResult(result, null);
        }

        if (!string.IsNullOrWhiteSpace(input.Url))
        {
            var result = await ResolveByUrlSafeAsync(input.Url, cancellationToken);
            return new PrimaryResolutionResult(result, null);
        }

        if (!string.IsNullOrWhiteSpace(input.SpotifyId))
        {
            var result = await ResolveSpotifySafeAsync(input.SpotifyId, cancellationToken);
            return new PrimaryResolutionResult(result, null);
        }

        if (!string.IsNullOrWhiteSpace(input.Isrc))
        {
            var qobuzUrlByIsrc = await _songLinkResolver.ResolveQobuzUrlByIsrcAsync(input.Isrc, cancellationToken);
            return new PrimaryResolutionResult(
                null,
                new
                {
                    available = !string.IsNullOrWhiteSpace(qobuzUrlByIsrc),
                    isrc = input.Isrc,
                    qobuz = !string.IsNullOrWhiteSpace(qobuzUrlByIsrc),
                    qobuzUrl = qobuzUrlByIsrc
                });
        }

        return new PrimaryResolutionResult(null, new { error = "spotifyId, url, isrc, or deezerId is required." });
    }

    private async Task<SongLinkResult?> ResolveByUrlSafeAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await _songLinkResolver.ResolveByUrlAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "SongLink resolution failed for url {Url}", url);
            return null;
        }
    }

    private async Task<SongLinkResult?> ResolveSpotifySafeAsync(string spotifyId, CancellationToken cancellationToken)
    {
        try
        {
            return await _songLinkResolver.ResolveSpotifyTrackAsync(spotifyId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "SongLink resolution failed for spotifyId {SpotifyId}", spotifyId);
            return null;
        }
    }

    private static SongLinkResult? EnsureDeezerFallbackResult(AvailabilityInput input, SongLinkResult? result)
    {
        if (result != null || string.IsNullOrWhiteSpace(input.NormalizedDeezerId))
        {
            return result;
        }

        return new SongLinkResult
        {
            DeezerUrl = BuildDeezerTrackUrl(input.NormalizedDeezerId)
        };
    }

    private static void ApplyResolvedIsrc(SongLinkResult? result, string? resolvedIsrc)
    {
        if (result == null || string.IsNullOrWhiteSpace(resolvedIsrc))
        {
            return;
        }

        result.Isrc = string.IsNullOrWhiteSpace(result.Isrc) ? resolvedIsrc : result.Isrc;
    }

    private async Task TryPopulateDeezerUrlFromIsrcAsync(SongLinkResult? result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result == null
            || !string.IsNullOrWhiteSpace(result.DeezerUrl)
            || string.IsNullOrWhiteSpace(result.Isrc))
        {
            return;
        }

        try
        {
            var deezerTrack = await _deezerClient.GetTrackByIsrcAsync(result.Isrc);
            if (deezerTrack != null && !string.IsNullOrWhiteSpace(deezerTrack.Id) && deezerTrack.Id != "0")
            {
                result.DeezerUrl = BuildDeezerTrackUrl(deezerTrack.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort fallback.
        }
    }

    private static object BuildPlatformOnlyResponse(string? resolvedIsrc, PlatformAvailabilityUrls urls)
    {
        return new
        {
            available = !string.IsNullOrWhiteSpace(urls.QobuzUrl)
                        || !string.IsNullOrWhiteSpace(urls.AppleUrl)
                        || !string.IsNullOrWhiteSpace(urls.TidalUrl),
            isrc = resolvedIsrc,
            qobuz = !string.IsNullOrWhiteSpace(urls.QobuzUrl),
            qobuzUrl = urls.QobuzUrl,
            apple = !string.IsNullOrWhiteSpace(urls.AppleUrl),
            appleUrl = urls.AppleUrl,
            tidal = !string.IsNullOrWhiteSpace(urls.TidalUrl),
            tidalUrl = urls.TidalUrl
        };
    }

    private static object BuildResolvedAvailabilityResponse(
        AvailabilityInput input,
        string? resolvedIsrc,
        PlatformAvailabilityUrls urls,
        SongLinkResult result)
    {
        var responseSpotifyId = LooksLikeSpotifyId(result.SpotifyId) ? result.SpotifyId : null;
        return new
        {
            available = true,
            spotifyId = responseSpotifyId ?? input.SpotifyId,
            spotifyUrl = result.SpotifyUrl,
            isrc = result.Isrc ?? resolvedIsrc,
            deezer = !string.IsNullOrWhiteSpace(result.DeezerUrl),
            deezerUrl = result.DeezerUrl,
            tidal = !string.IsNullOrWhiteSpace(result.TidalUrl) || !string.IsNullOrWhiteSpace(urls.TidalUrl),
            tidalUrl = result.TidalUrl ?? urls.TidalUrl,
            amazon = !string.IsNullOrWhiteSpace(result.AmazonUrl),
            amazonUrl = result.AmazonUrl,
            qobuz = !string.IsNullOrWhiteSpace(result.QobuzUrl) || !string.IsNullOrWhiteSpace(urls.QobuzUrl),
            qobuzUrl = result.QobuzUrl ?? urls.QobuzUrl,
            apple = !string.IsNullOrWhiteSpace(result.AppleMusicUrl) || !string.IsNullOrWhiteSpace(urls.AppleUrl),
            appleUrl = result.AppleMusicUrl ?? urls.AppleUrl
        };
    }

    private static string BuildDeezerTrackUrl(string deezerId) => $"https://www.deezer.com/track/{deezerId}";

    private static string? TryExtractAppleSongUrl(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }
        if (data.GetArrayLength() == 0)
        {
            return null;
        }
        var first = data[0];
        if (!first.TryGetProperty("attributes", out var attrs))
        {
            return null;
        }
        if (attrs.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return urlProp.GetString();
        }
        return null;
    }

    private static bool LooksLikeSpotifyId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Length != 22)
        {
            return false;
        }

        return value.All(ch =>
            char.IsAsciiLetterOrDigit(ch));
    }

    private static string? NormalizeDeezerId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return long.TryParse(value, out _) ? value : null;
    }

    public sealed class AvailabilityLookupRequest
    {
        public string? SpotifyId { get; set; }
        public string? Url { get; set; }
        public string? Isrc { get; set; }
        public string? DeezerId { get; set; }
        public string? AppleId { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public int? DurationMs { get; set; }
    }

    private sealed class AvailabilityInput
    {
        public string? SpotifyId { get; init; }
        public string? Url { get; init; }
        public string? Isrc { get; init; }
        public string? NormalizedDeezerId { get; init; }
        public string? Title { get; init; }
        public string? Artist { get; init; }
        public int? DurationMs { get; init; }
    }

    private sealed record PlatformAvailabilityUrls(string? QobuzUrl, string? AppleUrl, string? TidalUrl);

    private sealed record PrimaryResolutionResult(SongLinkResult? Result, object? ImmediateResponse);
}
