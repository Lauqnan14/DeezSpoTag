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
    private static readonly bool AppleDisabled = AppleCatalogJsonHelper.IsAppleDisabledByEnvironment();
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

    private enum ArtistPageMode
    {
        Albums,
        Videos
    }

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

    private IActionResult? ValidateAndNormalizeArtistPageRequest(string id, ref int limit, ref int offset)
    {
        if (ValidateArtistRequest(id) is { } validationError)
        {
            return validationError;
        }

        NormalizePageArgs(ref limit, ref offset);
        return null;
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
    public Task<IActionResult> GetAlbums(
        [FromQuery] string id,
        [FromQuery] int limit = 25,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default) =>
        GetArtistPageAsync(id, limit, offset, ArtistPageMode.Albums, cancellationToken);

    [HttpGet("videos")]
    public Task<IActionResult> GetVideos(
        [FromQuery] string id,
        [FromQuery] int limit = 25,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default) =>
        GetArtistPageAsync(id, limit, offset, ArtistPageMode.Videos, cancellationToken);

    private async Task<IActionResult> GetArtistPageAsync(
        string id,
        int limit,
        int offset,
        ArtistPageMode mode,
        CancellationToken cancellationToken)
    {
        if (ValidateAndNormalizeArtistPageRequest(id, ref limit, ref offset) is { } validationError)
        {
            return validationError;
        }

        return mode switch
        {
            ArtistPageMode.Albums => await ExecuteArtistPagedRequestAsync(
                async ct => await _catalog.GetArtistAlbumsAsync(id, GetStorefront(), language: DefaultLanguage, limit, offset, ct),
                _ => Ok(new { albums = Array.Empty<object>() }),
                static (root, dataArr, _) =>
                {
                    var albums = new List<Dictionary<string, object?>>();
                    foreach (var item in dataArr.EnumerateArray())
                    {
                        var attrs = item.TryGetProperty(AttributesField, out var a) ? a : default;
                        albums.Add(BuildArtistMediaCore(item, attrs));
                    }

                    return Task.FromResult<IActionResult>(new OkObjectResult(new { albums }));
                },
                "Apple artist albums fetch failed",
                "Apple artist albums failed.",
                cancellationToken),
            ArtistPageMode.Videos => await ExecuteArtistPagedRequestAsync(
                async ct => await _catalog.GetArtistMusicVideosAsync(id, GetStorefront(), language: DefaultLanguage, limit, offset, ct),
                _ => Ok(BuildEmptyVideosResponse()),
                async (root, dataArr, ct) =>
                {
                    var videos = BuildVideosPayload(dataArr);
                    await EnrichVideoAtmosAsync(videos, id, ct);
                    return Ok(BuildVideosResponse(root, videos));
                },
                "Apple artist videos fetch failed",
                "Apple artist videos failed.",
                cancellationToken),
            _ => Ok(BuildEmptyVideosResponse())
        };
    }

    private async Task<IActionResult> ExecuteArtistPagedRequestAsync(
        Func<CancellationToken, Task<JsonDocument>> requestFactory,
        Func<JsonElement, IActionResult> onDataMissing,
        Func<JsonElement, JsonElement, CancellationToken, Task<IActionResult>> onDataPresent,
        string failureLogMessage,
        string failureResponseMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await requestFactory(cancellationToken);
            var root = doc.RootElement;
            if (!AppleCatalogJsonHelper.TryGetDataArray(root, out var dataArr))
            {
                return onDataMissing(root);
            }

            return await onDataPresent(root, dataArr, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{FailureLogMessage}", failureLogMessage);
            return StatusCode(500, new { error = failureResponseMessage });
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
        var video = BuildArtistMediaCore(item, attrs);
        video[TypeField] = MusicVideosType;
        video["isVideo"] = true;
        video[PreviewUrlField] = AppleCatalogJsonHelper.ReadPreviewUrl(attrs);
        video["durationMs"] = attrs.TryGetProperty("durationInMillis", out var durationEl) ? durationEl.GetInt64() : 0;
        video[ReleaseDateField] = attrs.TryGetProperty(ReleaseDateField, out var releaseEl) ? releaseEl.GetString() ?? "" : "";
        video[AudioTraitsField] = audioTraits;
        video["hasAtmosCatalog"] = hasAtmosCatalog;
        video[HasAtmosField] = hasAtmosCatalog;
        video[AtmosDetectionField] = hasAtmosCatalog ? CatalogDetection : UnavailableDetection;
        return video;
    }

    private static Dictionary<string, object?> BuildArtistMediaCore(JsonElement item, JsonElement attrs)
    {
        return new Dictionary<string, object?>
        {
            [SourceField] = AppleSource,
            [AppleIdField] = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
            [AppleUrlField] = attrs.TryGetProperty(UrlField, out var urlEl) ? urlEl.GetString() ?? "" : "",
            [NameField] = attrs.TryGetProperty(NameField, out var nameEl) ? nameEl.GetString() ?? "" : "",
            [ArtistField] = attrs.TryGetProperty(ArtistNameField, out var artistEl) ? artistEl.GetString() ?? "" : "",
            [ImageField] = AppleCatalogJsonHelper.ResolveArtwork(attrs)
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
