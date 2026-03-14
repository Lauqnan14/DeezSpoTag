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
    private static readonly bool AppleDisabled = ReadAppleDisabled();
    private readonly AppleMusicCatalogService _catalog;
    private readonly ILogger<AppleStationApiController> _logger;
    public AppleStationApiController(
        AppleMusicCatalogService catalog,
        ILogger<AppleStationApiController> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    private static bool ReadAppleDisabled()
    {
        var value = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_DISABLED");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
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
        if (AppleDisabled)
        {
            return StatusCode(503, new { error = "Apple Music is disabled." });
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest("id is required");
        }

        if (string.IsNullOrWhiteSpace(mediaUserToken))
        {
            return BadRequest("mediaUserToken is required");
        }

        try
        {
            using var doc = await _catalog.GetStationNextTracksAsync(id, mediaUserToken, "en-US", cancellationToken);
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
        if (AppleDisabled)
        {
            return StatusCode(503, new { error = "Apple Music is disabled." });
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest("id is required");
        }

        if (string.IsNullOrWhiteSpace(mediaUserToken))
        {
            return BadRequest("mediaUserToken is required");
        }

        try
        {
            using var doc = await _catalog.GetStationAssetsAsync(id, mediaUserToken, cancellationToken);
            return Ok(doc.RootElement.Clone());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple station assets fetch failed");
            return StatusCode(500, new { error = "Apple station assets fetch failed." });
        }
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
                image = ResolveArtwork(attrs)
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
                image = ResolveArtwork(attrs),
                hasAtmos = HasAtmos(attrs),
                hasAppleDigitalMaster = HasAppleDigitalMaster(attrs)
            });
        }

        return new { tracks };
    }

    private static string ResolveArtwork(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("artwork", out var art) || art.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!art.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var raw = urlEl.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var width = art.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
        var height = art.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        return AppleArtworkRenderHelper.BuildArtworkUrl(raw, width, height);
    }

    private static bool HasAtmos(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("audioTraits", out var traits) || traits.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return traits.EnumerateArray().Any(static trait =>
            trait.ValueKind == JsonValueKind.String
            && trait.GetString()?.IndexOf("atmos", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool HasAppleDigitalMaster(JsonElement attributes)
    {
        if (attributes.TryGetProperty("isAppleDigitalMaster", out var admEl) && admEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return admEl.GetBoolean();
        }

        if (attributes.TryGetProperty("isMasteredForItunes", out var mfiEl) && mfiEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return mfiEl.GetBoolean();
        }

        return false;
    }
}
