using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net;
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
    private const string EditorialNotesField = "editorialNotes";
    private const string StandardField = "standard";
    private const string ShortField = "short";
    private const string CatalogDetection = "catalog";
    private const string UnavailableDetection = "unavailable";
    private const string JsonLdScriptType = "application/ld+json";
    private const string MusicGroupType = "MusicGroup";
    private const string DescriptionField = "description";
    private const string TypeJsonField = "@type";
    private readonly AppleMusicCatalogService _catalog;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly AppleCatalogVideoAtmosEnricher _appleCatalogVideoAtmosEnricher;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppleArtistBiographyService _artistBiographyService;
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
        IHttpClientFactory httpClientFactory,
        AppleArtistBiographyService artistBiographyService,
        ILogger<AppleArtistApiController> logger)
    {
        _catalog = catalog;
        _settingsService = settingsService;
        _appleCatalogVideoAtmosEnricher = appleCatalogVideoAtmosEnricher;
        _httpClientFactory = httpClientFactory;
        _artistBiographyService = artistBiographyService;
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
            var result = await _artistBiographyService.ResolveByArtistIdAsync(id, null, cancellationToken);
            return Ok(new
            {
                appleId = id,
                name = result?.Name ?? string.Empty,
                image = result?.Image ?? string.Empty,
                biography = result?.Biography ?? string.Empty
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple artist info fetch failed for Id");
            return StatusCode(500, new { error = "Apple artist info failed." });
        }
    }

    private async Task<string?> ResolveAppleArtistBiographyAsync(
        string id,
        string artistName,
        string storefront,
        JsonElement attributes,
        CancellationToken cancellationToken)
    {
        var editorialNotes = ResolveEditorialNotes(attributes);
        if (!string.IsNullOrWhiteSpace(editorialNotes))
        {
            return editorialNotes;
        }

        return await ResolveAppleArtistPageBiographyAsync(id, artistName, storefront, cancellationToken);
    }

    private async Task<string?> ResolveAppleArtistPageBiographyAsync(
        string id,
        string artistName,
        string storefront,
        CancellationToken cancellationToken)
    {
        var url = BuildAppleArtistPageUrl(id, storefront);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 AppleWebKit/537.36 Chrome/125 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Apple artist biography page returned HTTP {StatusCode} for artist Id", (int)response.StatusCode);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return ResolveAppleArtistPageBiography(html, id, artistName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple artist biography page fetch failed for artist Id");
            return null;
        }
    }

    private static string BuildAppleArtistPageUrl(string id, string storefront)
    {
        var normalizedStorefront = string.IsNullOrWhiteSpace(storefront) ? DefaultStorefront : storefront.Trim();
        return $"https://music.apple.com/{Uri.EscapeDataString(normalizedStorefront)}/artist/{Uri.EscapeDataString(id)}";
    }

    private static string? ResolveAppleArtistPageBiography(string html, string id, string artistName)
    {
        foreach (var json in EnumerateJsonLdScripts(html))
        {
            var decoded = WebUtility.HtmlDecode(json);
            try
            {
                using var doc = JsonDocument.Parse(decoded);
                var resolvedDescription = ResolveMusicGroupDescription(doc.RootElement, id, artistName);
                if (!string.IsNullOrWhiteSpace(resolvedDescription))
                {
                    return resolvedDescription;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateJsonLdScripts(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            yield break;
        }

        var searchIndex = 0;
        while (searchIndex < html.Length)
        {
            var scriptStart = html.IndexOf("<script", searchIndex, StringComparison.OrdinalIgnoreCase);
            if (scriptStart < 0)
            {
                yield break;
            }

            var openEnd = html.IndexOf('>', scriptStart);
            if (openEnd < 0)
            {
                yield break;
            }

            var openTag = html[scriptStart..openEnd];
            var closeStart = html.IndexOf("</script>", openEnd + 1, StringComparison.OrdinalIgnoreCase);
            if (closeStart < 0)
            {
                yield break;
            }

            searchIndex = closeStart + "</script>".Length;
            if (openTag.IndexOf(JsonLdScriptType, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            yield return html[(openEnd + 1)..closeStart].Trim();
        }
    }

    private static string? ResolveMusicGroupDescription(JsonElement root, string id, string artistName)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var itemDescription = ResolveMusicGroupDescription(item, id, artistName);
                if (!string.IsNullOrWhiteSpace(itemDescription))
                {
                    return itemDescription;
                }
            }

            return null;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !JsonStringEquals(root, TypeJsonField, MusicGroupType)
            || !ArtistPageMatches(root, id, artistName))
        {
            return null;
        }

        return TryGetNonEmptyString(root, DescriptionField, out var description)
            ? NormalizeBiography(description)
            : null;
    }

    private static bool ArtistPageMatches(JsonElement root, string id, string artistName)
    {
        if (TryGetNonEmptyString(root, "url", out var url)
            && url.Contains($"/{id}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryGetNonEmptyString(root, NameField, out var name))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(artistName)
            && name.Equals(artistName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool JsonStringEquals(JsonElement root, string propertyName, string expected)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(property.GetString(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBiography(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static string? ResolveEditorialNotes(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object
            || !attributes.TryGetProperty(EditorialNotesField, out var notes)
            || notes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetNonEmptyString(notes, StandardField, out var standard))
        {
            return standard;
        }

        return TryGetNonEmptyString(notes, ShortField, out var shortNote)
            ? shortNote
            : null;
    }

    private static bool TryGetNonEmptyString(JsonElement obj, string propertyName, out string value)
    {
        value = string.Empty;
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
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
            _logger.LogWarning(ex, "Apple artist video Atmos enrichment timed out for {ArtistId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple artist video Atmos enrichment failed for {ArtistId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(artistId));
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
