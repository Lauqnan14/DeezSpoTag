using DeezSpoTag.Services.Download;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/exists")]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public class LibraryExistsApiController : ControllerBase
{
    private readonly DownloadDedupeService _dedupeService;

    public LibraryExistsApiController(DownloadDedupeService dedupeService)
    {
        _dedupeService = dedupeService;
    }

    public sealed record LibraryExistenceRequest(
        string Id,
        string? Source,
        string? SourceId,
        string? Isrc,
        string? TrackTitle,
        string? ArtistName,
        string? AlbumTitle,
        int? DurationMs);

    [HttpPost]
    public async Task<IActionResult> Check([FromBody] IReadOnlyList<LibraryExistenceRequest> requests, CancellationToken cancellationToken)
    {
        if (requests == null || requests.Count == 0)
        {
            return Ok(Array.Empty<object>());
        }

        if (!_dedupeService.IsLibraryConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var response = new object[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            var decision = await _dedupeService.CheckLibraryPresenceAsync(
                BuildDedupeRequest(requests[i]),
                cancellationToken);
            response[i] = new { id = requests[i].Id, exists = !decision.Allowed };
        }

        return Ok(response);
    }

    private static DownloadDedupeRequest BuildDedupeRequest(LibraryExistenceRequest request)
    {
        var source = NormalizeSource(request.Source);
        var sourceId = string.IsNullOrWhiteSpace(request.SourceId) ? null : request.SourceId.Trim();
        return new DownloadDedupeRequest
        {
            Isrc = request.Isrc,
            DeezerTrackId = string.Equals(source, "deezer", StringComparison.OrdinalIgnoreCase) ? sourceId : null,
            SpotifyTrackId = string.Equals(source, "spotify", StringComparison.OrdinalIgnoreCase) ? sourceId : null,
            AppleTrackId = IsAppleSource(source) ? sourceId : null,
            QobuzTrackId = string.Equals(source, "qobuz", StringComparison.OrdinalIgnoreCase) ? sourceId : null,
            TidalTrackId = string.Equals(source, "tidal", StringComparison.OrdinalIgnoreCase) ? sourceId : null,
            AmazonTrackId = IsAmazonSource(source) ? sourceId : null,
            TrackTitle = request.TrackTitle ?? string.Empty,
            TrackArtist = request.ArtistName ?? string.Empty,
            Album = request.AlbumTitle,
            DurationMs = request.DurationMs
        };
    }

    private static string NormalizeSource(string? source)
        => string.IsNullOrWhiteSpace(source) ? string.Empty : source.Trim().ToLowerInvariant();

    private static bool IsAmazonSource(string source)
        => string.Equals(source, "amazon", StringComparison.OrdinalIgnoreCase)
           || string.Equals(source, "amazonmusic", StringComparison.OrdinalIgnoreCase)
           || string.Equals(source, "amazonMusic", StringComparison.OrdinalIgnoreCase);

    private static bool IsAppleSource(string source)
        => string.Equals(source, "apple", StringComparison.OrdinalIgnoreCase)
           || string.Equals(source, "applemusic", StringComparison.OrdinalIgnoreCase)
           || string.Equals(source, "apple-music", StringComparison.OrdinalIgnoreCase);
}
