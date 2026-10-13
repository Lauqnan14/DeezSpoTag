using DeezSpoTag.Core.Security;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public Task<IActionResult> Sections(CancellationToken cancellationToken) => Browse("new", false, true, cancellationToken);

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

    private async Task<IActionResult> Browse(string path, bool categoriesOnly, bool homeOnly, CancellationToken cancellationToken)
    {
        if (AppleCatalogJsonHelper.IsAppleDisabledByEnvironment())
            return StatusCode(503, new { success = false, error = "Apple Music is disabled." });
        try
        {
            var settings = settingsService.LoadSettings();
            var storefront = await catalog.ResolveStorefrontAsync(settings.AppleMusic?.Storefront, settings.AppleMusic?.MediaUserToken, cancellationToken);
            const string language = "en-US";
            // Apple occasionally returns destinations carrying another storefront. Rebind to the resolved store.
            path = Regex.Replace(path, @"^/[a-zA-Z]{2}/", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            using var document = await catalog.GetBrowsePageAsync(path, storefront, language, cancellationToken);
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
}
