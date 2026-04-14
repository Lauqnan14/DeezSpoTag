using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/availability")]
[Authorize]
public sealed class AvailabilityApiController : ControllerBase
{
    private readonly DownloadIntentService _downloadIntentService;

    public AvailabilityApiController(DownloadIntentService downloadIntentService)
    {
        _downloadIntentService = downloadIntentService;
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
        if (string.IsNullOrWhiteSpace(input.SpotifyId)
            && string.IsNullOrWhiteSpace(input.Url)
            && string.IsNullOrWhiteSpace(input.Isrc)
            && string.IsNullOrWhiteSpace(input.NormalizedDeezerId))
        {
            return new { error = "spotifyId, url, isrc, or deezerId is required." };
        }

        var intent = BuildLookupIntent(input);
        var lookup = await _downloadIntentService.LookupAvailabilityAsync(intent, cancellationToken);

        var spotifyId = LooksLikeSpotifyId(lookup.SpotifyId) ? lookup.SpotifyId : input.SpotifyId;
        var deezer = !string.IsNullOrWhiteSpace(lookup.DeezerUrl);
        var tidal = !string.IsNullOrWhiteSpace(lookup.TidalUrl);
        var amazon = !string.IsNullOrWhiteSpace(lookup.AmazonUrl);
        var qobuz = !string.IsNullOrWhiteSpace(lookup.QobuzUrl);
        var apple = !string.IsNullOrWhiteSpace(lookup.AppleMusicUrl);

        return new
        {
            available = deezer || tidal || amazon || qobuz || apple,
            spotifyId,
            spotifyUrl = lookup.SpotifyUrl,
            isrc = lookup.Isrc,
            deezer,
            deezerUrl = lookup.DeezerUrl,
            tidal,
            tidalUrl = lookup.TidalUrl,
            amazon,
            amazonUrl = lookup.AmazonUrl,
            qobuz,
            qobuzUrl = lookup.QobuzUrl,
            apple,
            appleUrl = lookup.AppleMusicUrl
        };
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
            AppleId = request.AppleId,
            Title = request.Title,
            Artist = request.Artist,
            DurationMs = request.DurationMs
        };
    }

    private static DownloadIntent BuildLookupIntent(AvailabilityInput input)
    {
        var sourceUrl = input.Url;
        if (string.IsNullOrWhiteSpace(sourceUrl) && !string.IsNullOrWhiteSpace(input.NormalizedDeezerId))
        {
            sourceUrl = $"https://www.deezer.com/track/{input.NormalizedDeezerId}";
        }

        return new DownloadIntent
        {
            SourceUrl = sourceUrl ?? string.Empty,
            SpotifyId = input.SpotifyId ?? string.Empty,
            DeezerId = input.NormalizedDeezerId ?? string.Empty,
            Isrc = input.Isrc ?? string.Empty,
            Title = input.Title ?? string.Empty,
            Artist = input.Artist ?? string.Empty,
            DurationMs = input.DurationMs ?? 0,
            AppleId = input.AppleId ?? string.Empty
        };
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

        return value.All(ch => char.IsAsciiLetterOrDigit(ch));
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
        public string? AppleId { get; init; }
        public string? Title { get; init; }
        public string? Artist { get; init; }
        public int? DurationMs { get; init; }
    }
}
