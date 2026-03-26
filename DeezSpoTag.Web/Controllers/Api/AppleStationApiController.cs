using DeezSpoTag.Services.Apple;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/apple/stations")]
[Authorize]
public sealed class AppleStationApiController : ControllerBase
{
    private static readonly bool AppleDisabled = AppleCatalogJsonHelper.IsAppleDisabledByEnvironment();
    private readonly AppleMusicCatalogService _catalog;
    private readonly ILogger<AppleStationApiController> _logger;
    public AppleStationApiController(
        AppleMusicCatalogService catalog,
        ILogger<AppleStationApiController> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetStation(string id, CancellationToken cancellationToken)
    {
        if (AppleDisabled)
        {
            return StatusCode(503, new { error = "Apple Music is disabled." });
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest("id is required");
        }

        try
        {
            using var doc = await _catalog.GetStationAsync(id, storefront: "us", language: "en-US", cancellationToken);
            return Ok(MapStation(doc.RootElement));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple station fetch failed");
            return StatusCode(500, new { error = "Apple station fetch failed." });
        }
    }

    [HttpGet("{id}/next-tracks")]
    public async Task<IActionResult> GetStationNextTracks(string id, [FromQuery] string? mediaUserToken, CancellationToken cancellationToken)
    {
        var validationResult = ValidateStationRequest(id, mediaUserToken, requireMediaUserToken: true);
        if (validationResult is not null)
        {
            return validationResult;
        }

        try
        {
            using var doc = await _catalog.GetStationNextTracksAsync(id, mediaUserToken!, "en-US", cancellationToken);
            return Ok(MapStationTracks(doc.RootElement));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple station next tracks failed");
            return StatusCode(500, new { error = "Apple station next tracks failed." });
        }
    }

    [HttpGet("{id}/assets")]
    public async Task<IActionResult> GetStationAssets(string id, [FromQuery] string? mediaUserToken, CancellationToken cancellationToken)
    {
        var validationResult = ValidateStationRequest(id, mediaUserToken, requireMediaUserToken: true);
        if (validationResult is not null)
        {
            return validationResult;
        }

        try
        {
            using var doc = await _catalog.GetStationAssetsAsync(id, mediaUserToken!, cancellationToken);
            return Ok(doc.RootElement.Clone());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple station assets fetch failed");
            return StatusCode(500, new { error = "Apple station assets fetch failed." });
        }
    }

    private IActionResult? ValidateStationRequest(string id, string? mediaUserToken, bool requireMediaUserToken)
    {
        if (AppleDisabled)
        {
            return StatusCode(503, new { error = "Apple Music is disabled." });
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest("id is required");
        }

        if (requireMediaUserToken && string.IsNullOrWhiteSpace(mediaUserToken))
        {
            return BadRequest("mediaUserToken is required");
        }

        return null;
    }

    private static object MapStation(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            return new { station = default(object) };
        }

        var data = dataArr[0];
        var attrs = data.TryGetProperty("attributes", out var a) ? a : default;

        return new
        {
            station = new
            {
                appleId = data.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                name = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                appleUrl = attrs.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
                image = AppleCatalogJsonHelper.ResolveArtwork(attrs)
            }
        };
    }

    private static object MapStationTracks(JsonElement root)
    {
        var tracks = new List<object>();
        if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
        {
            return new { tracks };
        }

        foreach (var item in dataArr.EnumerateArray())
        {
            var attrs = item.TryGetProperty("attributes", out var a) ? a : default;
            tracks.Add(new
            {
                appleId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                appleUrl = attrs.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
                name = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                artist = attrs.TryGetProperty("artistName", out var artistEl) ? artistEl.GetString() ?? "" : "",
                album = attrs.TryGetProperty("albumName", out var albumEl) ? albumEl.GetString() ?? "" : "",
                image = AppleCatalogJsonHelper.ResolveArtwork(attrs),
                hasAtmos = AppleCatalogJsonHelper.HasAtmos(attrs),
                hasAppleDigitalMaster = AppleCatalogJsonHelper.HasAppleDigitalMaster(attrs)
            });
        }

        return new { tracks };
    }
}
