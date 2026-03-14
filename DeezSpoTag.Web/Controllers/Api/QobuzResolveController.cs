using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Services.Metadata.Qobuz;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/qobuz")]
[Authorize]
public sealed class QobuzResolveController : ControllerBase
{
    private readonly IQobuzMetadataService _metadataService;

    public QobuzResolveController(IQobuzMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    [HttpPost("resolve")]
    public async Task<ActionResult<QobuzResolveResult>> ResolveTrack([FromBody] QobuzResolveRequest request)
    {
        var cancellationToken = HttpContext.RequestAborted;

        var isrcResult = await TryResolveByIsrcAsync(request, cancellationToken);
        if (isrcResult != null)
        {
            return Ok(isrcResult);
        }

        var upcResult = await TryResolveByUpcAsync(request, cancellationToken);
        if (upcResult != null)
        {
            return Ok(upcResult);
        }

        var fuzzyResult = await ResolveByFuzzyMatchAsync(request, cancellationToken);
        return Ok(fuzzyResult);
    }

    private async Task<QobuzResolveResult?> TryResolveByIsrcAsync(
        QobuzResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ISRC))
        {
            return null;
        }

        var track = await _metadataService.FindTrackByISRC(request.ISRC, cancellationToken);
        if (track == null)
        {
            return null;
        }

        return new QobuzResolveResult
        {
            Track = track,
            MatchMethod = "ISRC",
            Confidence = 1.0
        };
    }

    private async Task<QobuzResolveResult?> TryResolveByUpcAsync(
        QobuzResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.UPC))
        {
            return null;
        }

        var album = await _metadataService.FindAlbumByUPC(request.UPC, cancellationToken);
        if (album == null)
        {
            return null;
        }

        return new QobuzResolveResult
        {
            Album = album,
            MatchMethod = "UPC",
            Confidence = 0.95
        };
    }

    private async Task<QobuzResolveResult> ResolveByFuzzyMatchAsync(
        QobuzResolveRequest request,
        CancellationToken cancellationToken)
    {
        var query = $"{request.Artist} {request.Title}".Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new QobuzResolveResult();
        }

        var candidates = await _metadataService.SearchTracks(query, cancellationToken);
        var bestMatch = QobuzTrackMatchingService.FindBestMatch(request.Title, request.Artist, null, candidates);
        if (bestMatch == null)
        {
            return new QobuzResolveResult();
        }

        return new QobuzResolveResult
        {
            Track = bestMatch,
            MatchMethod = "Fuzzy",
            Confidence = 0.8
        };
    }
}
