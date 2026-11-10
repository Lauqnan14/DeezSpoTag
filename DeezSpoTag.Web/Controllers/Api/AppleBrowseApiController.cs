using DeezSpoTag.Core.Security;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Authorize]
[Route("api/apple/browse")]
public sealed class AppleBrowseApiController(
    AppleMusicCatalogService catalog,
    DeezSpoTagSettingsService settingsService,
    ILogger<AppleBrowseApiController> logger) : ControllerBase
{
    [HttpGet("categories")]
    public Task<IActionResult> Categories(CancellationToken cancellationToken) => Browse("search", true, false, cancellationToken);

    [HttpGet("sections")]
    public Task<IActionResult> Sections(CancellationToken cancellationToken) => Browse("new", false, true, cancellationToken,
        refresh: HttpContext?.Request.Query["refresh"].ToString() is "1" or "true" or "yes");

    [HttpGet("page")]
    public Task<IActionResult> Page([FromQuery] string path, CancellationToken cancellationToken) => Browse(path, false, false, cancellationToken);

    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] string id, [FromQuery] string type, CancellationToken cancellationToken)
    {
        if (AppleCatalogJsonHelper.IsAppleDisabledByEnvironment())
            return StatusCode(503, new { success = false, error = "Apple Music is disabled." });
        if (type is not ("track" or "video") || string.IsNullOrWhiteSpace(id)
            || !Regex.IsMatch(id, "^[0-9]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
            return BadRequest(new { success = false, error = "Unsupported Apple Music preview resource." });
        try
        {
            var settings = settingsService.LoadSettings();
            var storefront = await catalog.ResolveStorefrontAsync(settings.AppleMusic?.Storefront, settings.AppleMusic?.MediaUserToken, cancellationToken);
            using var resource = type == "video"
                ? await catalog.GetMusicVideoAsync(id, storefront, "en-US", cancellationToken)
                : await catalog.GetSongAsync(id, storefront, "en-US", cancellationToken);
            var url = AppleCatalogJsonHelper.TryGetDataArray(resource.RootElement, out var data)
                && data.GetArrayLength() > 0 && data[0].TryGetProperty("attributes", out var attributes)
                    ? AppleCatalogJsonHelper.ReadPreviewUrl(attributes) : null;
            return Ok(new { success = true, storefront, previewUrl = url });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Apple browse preview unavailable for {Id}", LogSanitizer.OneLine(id));
            return StatusCode(502, new { success = false, error = "Apple Music preview is unavailable." });
        }
    }

    private async Task<IActionResult> Browse(string path, bool categoriesOnly, bool homeOnly, CancellationToken cancellationToken, bool refresh = false)
    {
        if (AppleCatalogJsonHelper.IsAppleDisabledByEnvironment())
            return StatusCode(503, new { success = false, error = "Apple Music is disabled." });
        try
        {
            var settings = settingsService.LoadSettings();
            // Homepage follows the same market as trending songs. Account storefront wins; an
            // explicit non-US Apple storefront wins; otherwise the app market is used. US is the
            // last resort, including when the selected market feed is unusable.
            var storefront = await catalog.ResolveBrowseStorefrontAsync(settings.AppleMusic?.Storefront, settings.DeezerCountry, settings.AppleMusic?.MediaUserToken, cancellationToken, BrowseAccountLookupBudget);
            const string language = "en-US";
            // Apple occasionally returns destinations carrying another storefront. Rebind to the resolved store.
            path = Regex.Replace(path, @"^/[a-zA-Z]{2}/", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            var validation = categoriesOnly || homeOnly ? BrowseContentValidation(categoriesOnly, homeOnly) : null;
            var loaded = await LoadBrowsePageAsync(path, storefront, language, cancellationToken, refresh, validation, homeOnly || categoriesOnly);
            storefront = loaded.Storefront;
            using var document = loaded.Document;
            var sections = AppleCatalogJsonHelper.MapBrowseSections(document.RootElement, categoriesOnly);
            if (homeOnly)
            {
                var selected = new JsonArray();
                foreach (var section in sections.Where(s => !string.IsNullOrEmpty(s?["role"]?.GetValue<string>()))) selected.Add(section!.DeepClone());
                sections = selected;
            }
            foreach (var item in sections.SelectMany(s => (s?["items"] as JsonArray)?.OfType<JsonObject>() ?? []))
            {
                if (item["target"]?.GetValue<string>() is { Length: > 0 } target)
                {
                    var relative = target.StartsWith("https://music.apple.com/", StringComparison.Ordinal)
                        ? new Uri(target).AbsolutePath : target;
                    item["target"] = Regex.Replace(relative, @"^/[a-zA-Z]{2}/", "/", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                }
            }
            var categories = new JsonArray();
            if (categoriesOnly)
                foreach (var item in sections.SelectMany(s => (s?["items"] as JsonArray) ?? [])) categories.Add(item!.DeepClone());
            return Ok(new { success = true, storefront, sections, categories });
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Apple Music browse request timed out for {Path}", LogSanitizer.OneLine(path));
            return StatusCode(504, new { success = false, error = "Apple Music browse request timed out. Please try again." });
        }
        catch (ArgumentException exception) { return BadRequest(new { success = false, error = exception.Message }); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Apple Music browse request failed for {Path}", LogSanitizer.OneLine(path));
            return StatusCode(502, new { success = false, error = "Apple Music browse content is unavailable." });
        }
    }

    /// <summary>
    /// Budget for the public browse account lookup. Successful account resolution keeps
    /// precedence; when the lookup cannot complete within the budget, browse continues with
    /// the user's configured storefront instead of timing the whole page out.
    /// </summary>
    private static readonly TimeSpan BrowseAccountLookupBudget = TimeSpan.FromSeconds(3);

    private async Task<(JsonDocument Document, string Storefront)> LoadBrowsePageAsync(
        string path,
        string storefront,
        string language,
        CancellationToken cancellationToken,
        bool refresh,
        Func<JsonDocument, bool>? validation,
        bool allowUsFallback)
    {
        try
        {
            return (await catalog.GetBrowsePageAsync(path, storefront, language, cancellationToken, refresh, validation), storefront);
        }
        catch (Exception exception) when (allowUsFallback
            && !storefront.Equals("us", StringComparison.OrdinalIgnoreCase)
            && exception is not ArgumentException
            && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(exception, "Apple Music {Storefront} browse feed is unusable; falling back to the US feed for {Path}", storefront, LogSanitizer.OneLine(path));
            return (await catalog.GetBrowsePageAsync(path, "us", language, cancellationToken, refresh, validation), "us");
        }
    }

    /// <summary>
    /// Builds the validation callback handed to the catalog service so unusable Apple payloads
    /// are rejected before they are reported or cached. Uses the existing browse mapper: home
    /// responses need at least one useful item in a mapped home role, categories need at least
    /// one useful mapped category item; legitimate partial feeds remain valid.
    /// </summary>
    private static Func<JsonDocument, bool> BrowseContentValidation(bool categoriesOnly, bool homeOnly)
    {
        return document =>
        {
            JsonArray sections;
            try
            {
                sections = AppleCatalogJsonHelper.MapBrowseSections(document.RootElement, categoriesOnly);
            }
            catch (JsonException)
            {
                return false;
            }
            if (!homeOnly)
            {
                return sections.Any(section => (section?["items"] as JsonArray)?.Count > 0);
            }
            return sections.Any(section =>
                !string.IsNullOrEmpty(section?["role"]?.GetValue<string>())
                && (section["items"] as JsonArray)?.Count > 0);
        };
    }
}
