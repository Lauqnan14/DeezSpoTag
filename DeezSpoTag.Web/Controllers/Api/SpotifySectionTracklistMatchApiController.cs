using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/spotify/tracklist")]
public sealed class SpotifySectionTracklistMatchApiController : ControllerBase
{
    private const string SectionType = "section";
    private readonly SpotifyTracklistService _tracklistService;
    private readonly ISettingsService _settingsService;

    public SpotifySectionTracklistMatchApiController(
        SpotifyTracklistService tracklistService,
        ISettingsService settingsService)
    {
        _tracklistService = tracklistService;
        _settingsService = settingsService;
    }

    [HttpPost("section/match")]
    public async Task<IActionResult> MatchSectionTracks(
        [FromBody] SpotifySectionMatchRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        var sectionKey = NormalizeSectionKey(request.SectionKey);
        if (string.IsNullOrWhiteSpace(sectionKey))
        {
            return BadRequest(new { error = "sectionKey is required." });
        }

        var sourceTracks = request.Tracks ?? [];
        if (sourceTracks.Count == 0)
        {
            return Ok(new { available = false });
        }

        var normalizedTracks = sourceTracks
            .Select(NormalizeTrack)
            .Where(track => !string.IsNullOrWhiteSpace(track.SourceUrl))
            .Take(256)
            .ToList();
        if (normalizedTracks.Count == 0)
        {
            return Ok(new { available = false });
        }

        var settings = _settingsService.LoadSettings();
        var allowFallbackSearch = !settings.StrictSpotifyDeezerMode;
        var result = await _tracklistService.BuildMatchedTracksAsync(
            SectionType,
            sectionKey,
            normalizedTracks,
            allowFallbackSearch,
            cancellationToken);

        var immediateMatches = result.Tracks
            .Where(track => IsNumericDeezerId(track.Id))
            .Select(track => new
            {
                index = track.Index,
                deezerId = track.Id,
                spotifyId = TrackIdNormalization.ExtractSpotifyTrackIdFromUrl(track.Link) ?? string.Empty,
                status = "matched",
                reason = "cached_or_immediate",
                attempt = 1
            })
            .ToList();

        return Ok(new
        {
            available = true,
            matching = result.PendingCount > 0
                ? new { token = result.MatchToken, pending = result.PendingCount }
                : null,
            matches = immediateMatches
        });
    }

    private static bool IsNumericDeezerId(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.All(char.IsDigit);
    }

    private static SpotifyTrackSummary NormalizeTrack(SpotifySectionTrackInput input)
    {
        var sourceUrl = (input.Link ?? string.Empty).Trim();
        var spotifyId = TrackIdNormalization.ExtractSpotifyTrackIdFromUrl(sourceUrl) ?? string.Empty;
        var title = (input.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = !string.IsNullOrWhiteSpace(spotifyId) ? spotifyId : "Track";
        }

        var artists = (input.Artist ?? string.Empty).Trim();
        var album = (input.Album ?? string.Empty).Trim();
        var isrc = (input.Isrc ?? string.Empty).Trim();
        var durationMs = input.DurationMs > 0 ? input.DurationMs : (int?)null;

        return new SpotifyTrackSummary(
            Id: spotifyId,
            Name: title,
            Artists: artists,
            Album: album,
            DurationMs: durationMs,
            SourceUrl: sourceUrl,
            ImageUrl: null,
            Isrc: string.IsNullOrWhiteSpace(isrc) ? null : isrc);
    }

    private static string NormalizeSectionKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value.Trim().ToLowerInvariant();
        if (cleaned.Length > 96)
        {
            cleaned = cleaned[..96];
        }

        return new string(cleaned.Where(ch =>
                (ch >= 'a' && ch <= 'z')
                || (ch >= '0' && ch <= '9')
                || ch == '-'
                || ch == '_'
                || ch == '.')
            .ToArray());
    }

    public sealed class SpotifySectionMatchRequest
    {
        public string? SectionKey { get; set; }
        public List<SpotifySectionTrackInput>? Tracks { get; set; }
    }

    public sealed class SpotifySectionTrackInput
    {
        public string? Link { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Isrc { get; set; }
        public int DurationMs { get; set; }
    }
}
