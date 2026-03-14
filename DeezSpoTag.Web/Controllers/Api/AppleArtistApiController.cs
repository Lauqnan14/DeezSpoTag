using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Linq;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/apple/artist")]
[Authorize]
public sealed class AppleArtistApiController : ControllerBase
{
    private static readonly bool AppleDisabled = ReadAppleDisabled();
    private const string AppleSource = "apple";
    private const string MusicVideosType = "music-videos";
    private const string DefaultStorefront = "us";
    private const string DefaultLanguage = "en-US";
    private const string NameField = "name";
    private const string UrlField = "url";
    private const string ImageField = "image";
    private const string ArtistField = "artist";
    private const string SourceField = "source";
    private const string TypeField = "type";
    private const string PreviewUrlField = "previewUrl";
    private const string ReleaseDateField = "releaseDate";
    private const string AttributesField = "attributes";
    private const string ArtistNameField = "artistName";
    private const string AppleIdField = "appleId";
    private const string AppleUrlField = "appleUrl";
    private const string HasAtmosField = "hasAtmos";
    private const string AtmosDetectionField = "atmosDetection";
    private const string AudioTraitsField = "audioTraits";
    private const string CatalogDetection = "catalog";
    private const string UnavailableDetection = "unavailable";
    private readonly AppleMusicCatalogService _catalog;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly AppleCatalogVideoAtmosEnricher _appleCatalogVideoAtmosEnricher;
    private readonly ILogger<AppleArtistApiController> _logger;
    public AppleArtistApiController(
        AppleMusicCatalogService catalog,
        DeezSpoTagSettingsService settingsService,
        AppleCatalogVideoAtmosEnricher appleCatalogVideoAtmosEnricher,
        ILogger<AppleArtistApiController> logger)
    {
        _catalog = catalog;
        _settingsService = settingsService;
        _appleCatalogVideoAtmosEnricher = appleCatalogVideoAtmosEnricher;
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

    private IActionResult? ValidateArtistRequest(string id)
    {
        if (AppleDisabled)
        {
            return StatusCode(503, new { error = "Apple Music is disabled." });
        }

        return string.IsNullOrWhiteSpace(id)
            ? BadRequest("id is required")
            : null;
    }

    private static void NormalizePageArgs(ref int limit, ref int offset)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);
    }

    private string GetStorefront()
    {
        var settings = _settingsService.LoadSettings();
        return string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
            ? DefaultStorefront
            : settings.AppleMusic!.Storefront;
    }

    [HttpGet]
    public async Task<IActionResult> GetArtistInfo(
        [FromQuery] string id,
        CancellationToken cancellationToken = default)
    {
        if (ValidateArtistRequest(id) is { } validationError)
        {
            return validationError;
        }

        try
        {
            using var doc = await _catalog.GetArtistAsync(id, GetStorefront(), DefaultLanguage, cancellationToken);
            var root = doc.RootElement;
            if (!AppleCatalogJsonHelper.TryGetDataArray(root, out var dataArr)
                || dataArr.GetArrayLength() == 0)
            {
                return Ok(new { appleId = id, name = string.Empty, image = string.Empty });
            }

            var item = dataArr[0];
            var attrs = item.TryGetProperty(AttributesField, out var a) ? a : default;
            var name = attrs.TryGetProperty(NameField, out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var image = AppleCatalogJsonHelper.ResolveArtwork(attrs);
            return Ok(new { appleId = id, name, image });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple artist info fetch failed for Id");
            return StatusCode(500, new { error = "Apple artist info failed." });
        }
    }

    [HttpGet("albums")]
    public async Task<IActionResult> GetAlbums(
        [FromQuery] string id,
        [FromQuery] int limit = 25,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (ValidateArtistRequest(id) is { } validationError)
        {
            return validationError;
        }

        NormalizePageArgs(ref limit, ref offset);

        try
        {
            using var doc = await _catalog.GetArtistAlbumsAsync(id, GetStorefront(), language: DefaultLanguage, limit, offset, cancellationToken);
            var root = doc.RootElement;
            if (!AppleCatalogJsonHelper.TryGetDataArray(root, out var dataArr))
            {
                return Ok(new { albums = Array.Empty<object>() });
            }

            var albums = new List<object>();
            foreach (var item in dataArr.EnumerateArray())
            {
                var attrs = item.TryGetProperty(AttributesField, out var a) ? a : default;
                albums.Add(new
                {
                    source = AppleSource,
                    appleId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                    appleUrl = attrs.TryGetProperty(UrlField, out var urlEl) ? urlEl.GetString() ?? "" : "",
                    name = attrs.TryGetProperty(NameField, out var nameEl) ? nameEl.GetString() ?? "" : "",
                    artist = attrs.TryGetProperty(ArtistNameField, out var artistEl) ? artistEl.GetString() ?? "" : "",
                    image = AppleCatalogJsonHelper.ResolveArtwork(attrs)
                });
            }

            return Ok(new { albums });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple artist albums fetch failed");
            return StatusCode(500, new { error = "Apple artist albums failed." });
        }
    }

    [HttpGet("videos")]
    public async Task<IActionResult> GetVideos(
        [FromQuery] string id,
        [FromQuery] int limit = 25,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (ValidateArtistRequest(id) is { } validationError)
        {
            return validationError;
        }

        NormalizePageArgs(ref limit, ref offset);

        try
        {
            using var doc = await _catalog.GetArtistMusicVideosAsync(id, GetStorefront(), language: DefaultLanguage, limit, offset, cancellationToken);
            var root = doc.RootElement;
            if (!AppleCatalogJsonHelper.TryGetDataArray(root, out var dataArr))
            {
                return Ok(BuildEmptyVideosResponse());
            }

            var videos = BuildVideosPayload(dataArr);
            await EnrichVideoAtmosAsync(videos, id, cancellationToken);
            return Ok(BuildVideosResponse(root, videos));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple artist videos fetch failed");
            return StatusCode(500, new { error = "Apple artist videos failed." });
        }
    }

    private static object BuildEmptyVideosResponse() => new { videos = Array.Empty<object>(), hasMoreVideos = false };

    private static List<Dictionary<string, object?>> BuildVideosPayload(JsonElement dataArr)
    {
        var videos = new List<Dictionary<string, object?>>();
        foreach (var item in dataArr.EnumerateArray())
        {
            videos.Add(BuildVideoItem(item));
        }

        return videos;
    }

    private static Dictionary<string, object?> BuildVideoItem(JsonElement item)
    {
        var attrs = item.TryGetProperty(AttributesField, out var a) ? a : default;
        var audioTraits = AppleCatalogJsonHelper.ReadStringArray(attrs, AudioTraitsField);
        var hasAtmosCatalog = audioTraits.Any(t => t.Contains("atmos", StringComparison.OrdinalIgnoreCase));
        return new Dictionary<string, object?>
        {
            [SourceField] = AppleSource,
            [TypeField] = MusicVideosType,
            [AppleIdField] = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
            [AppleUrlField] = attrs.TryGetProperty(UrlField, out var urlEl) ? urlEl.GetString() ?? "" : "",
            [NameField] = attrs.TryGetProperty(NameField, out var nameEl) ? nameEl.GetString() ?? "" : "",
            [ArtistField] = attrs.TryGetProperty(ArtistNameField, out var artistEl) ? artistEl.GetString() ?? "" : "",
            [ImageField] = AppleCatalogJsonHelper.ResolveArtwork(attrs),
            ["isVideo"] = true,
            [PreviewUrlField] = AppleCatalogJsonHelper.ReadPreviewUrl(attrs),
            ["durationMs"] = attrs.TryGetProperty("durationInMillis", out var durationEl) ? durationEl.GetInt64() : 0,
            [ReleaseDateField] = attrs.TryGetProperty(ReleaseDateField, out var releaseEl) ? releaseEl.GetString() ?? "" : "",
            [AudioTraitsField] = audioTraits,
            ["hasAtmosCatalog"] = hasAtmosCatalog,
            [HasAtmosField] = hasAtmosCatalog,
            [AtmosDetectionField] = hasAtmosCatalog ? CatalogDetection : UnavailableDetection
        };
    }

    private async Task EnrichVideoAtmosAsync(
        List<Dictionary<string, object?>> videos,
        string artistId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _appleCatalogVideoAtmosEnricher.EnrichAsync(
                videos,
                "Apple artist video Atmos details lookup failed for {AppleId}",
                cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Apple artist video Atmos enrichment timed out for {ArtistId}", artistId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple artist video Atmos enrichment failed for {ArtistId}", artistId);
        }
    }

    private static object BuildVideosResponse(JsonElement root, List<Dictionary<string, object?>> videos)
    {
        return new
        {
            videos = videos.Cast<object>().ToList(),
            hasMoreVideos = AppleCatalogJsonHelper.RootHasNext(root)
        };
    }

}
