using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/artists")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class LibraryArtistLastFmVisualsApiController : ControllerBase
{
    private readonly LastFmArtistImageService _lastFmArtistImageService;
    private readonly IWebHostEnvironment _environment;

    public LibraryArtistLastFmVisualsApiController(LibraryArtistMetadataServices metadataServices)
    {
        _lastFmArtistImageService = metadataServices.LastFmArtistImageService;
        _environment = metadataServices.Environment;
    }

    [HttpGet("lastfm-visuals")]
    public async Task<IActionResult> GetLastFmVisuals(
        [FromQuery] string? artistName,
        [FromQuery] long? artistId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return Ok(Array.Empty<object>());
        }

        var results = new List<object>();
        if (artistId is > 0)
        {
            results.AddRange(GetCachedLastFmVisuals(artistId.Value));
        }

        var candidates = await _lastFmArtistImageService.SearchArtistImagesAsync(artistName, 8, cancellationToken);
        results.AddRange(candidates.Select(candidate => new
        {
            source = candidate.Source,
            label = candidate.Label,
            url = candidate.Url,
            imageUrl = candidate.Url,
            name = candidate.Label
        }));

        return Ok(results);
    }

    [HttpGet("lastfm-biography")]
    public async Task<IActionResult> GetLastFmBiography(
        [FromQuery] string? artistName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return BadRequest(new { available = false, error = "Artist name is required." });
        }

        var biography = await _lastFmArtistImageService.GetArtistBiographyAsync(artistName, cancellationToken);
        if (biography is null)
        {
            return Ok(new { available = false, artistName, biography = string.Empty });
        }

        return Ok(new
        {
            available = true,
            artistName = biography.Name,
            biography = biography.Biography
        });
    }

    private object[] GetCachedLastFmVisuals(long artistId)
    {
        var cacheDir = Path.GetFullPath(Path.Join(
            AppDataPaths.GetDataRoot(_environment),
            "library-artist-images",
            "lastfm",
            "artists",
            artistId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (!Directory.Exists(cacheDir))
        {
            return Array.Empty<object>();
        }

        var cacheRoot = Path.GetFullPath(Path.Join(AppDataPaths.GetDataRoot(_environment), "library-artist-images", "lastfm"));
        return Directory.GetFiles(cacheDir, "candidate-*.*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Where(path => path.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new
            {
                source = "lastfm",
                label = "Last.fm cached",
                url = $"/api/library/image?path={Uri.EscapeDataString(path)}&size=640",
                imageUrl = $"/api/library/image?path={Uri.EscapeDataString(path)}&size=640",
                path,
                name = "Last.fm cached"
            })
            .Cast<object>()
            .ToArray();
    }
}
