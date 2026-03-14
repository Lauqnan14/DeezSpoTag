using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/quicktag/tag-sources")]
[Authorize]
public sealed class QuickTagTagSourcesApiController : ControllerBase
{
    private readonly QuickTagTagSourceService _tagSources;
    private readonly IHttpClientFactory _httpClientFactory;

    public QuickTagTagSourcesApiController(
        QuickTagTagSourceService tagSources,
        IHttpClientFactory httpClientFactory)
    {
        _tagSources = tagSources;
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] QuickTagTagSourceSearchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _tagSources.SearchAsync(request ?? new QuickTagTagSourceSearchRequest(), cancellationToken);
            return Ok(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("detail")]
    public async Task<IActionResult> Detail(
        [FromQuery] string provider,
        [FromQuery] string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = "Provider and id are required." });
        }

        try
        {
            var detail = await _tagSources.GetDetailAsync(provider, id, cancellationToken);
            if (detail == null)
            {
                return NotFound(new { error = "Could not fetch details for the selected item." });
            }

            return Ok(detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("cover")]
    public async Task<IActionResult> Cover(
        [FromQuery] string provider,
        [FromQuery] string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = "Provider and id are required." });
        }

        var detail = await _tagSources.GetDetailAsync(provider, id, cancellationToken);
        if (detail == null || string.IsNullOrWhiteSpace(detail.CoverUrl))
        {
            return NotFound(new { error = "No cover available for this source item." });
        }

        if (!Uri.TryCreate(detail.CoverUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(new { error = "Invalid cover URL." });
        }

        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode, new { error = "Failed to fetch source cover." });
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Source did not return an image." });
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return File(bytes, contentType);
    }
}
