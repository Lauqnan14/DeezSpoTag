using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace DeezSpoTag.Web.Controllers.Api;

/// <summary>
/// Serves Spotify cover art through the app instead of letting the browser fetch it directly
/// from the Spotify CDN.
///
/// The Tracklist page used to hand raw https://i.scdn.co/image/... URLs to the browser. When the
/// client cannot reach that CDN the covers fail with net::ERR_TIMED_OUT, and the browser also
/// learns which images are being viewed. Proxying them keeps the fetch server-side.
///
/// Only hosts on the allow-list below can be fetched, so this cannot be used as an open proxy,
/// and redirects are never followed so an allowed host cannot bounce the request elsewhere.
/// </summary>
[Route("api/externalimages")]
[ApiController]
[Authorize]
[DisableRateLimiting]
public class ExternalImagesApiController : ControllerBase
{
    /// <summary>
    /// Hosts that may be proxied. Both serve the Spotify image CDN.
    /// </summary>
    private static readonly HashSet<string> AllowedImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "i.scdn.co",
        "image-cdn-ak.spotifycdn.com"
    };

    private const int MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Name of the registered client that refuses to follow redirects.
    /// </summary>
    public const string HttpClientName = "external-images-proxy";

    private readonly IHttpClientFactory _httpClientFactory;

    public ExternalImagesApiController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Route: /api/externalimages?url=https%3A%2F%2Fi.scdn.co%2Fimage%2F...
    /// </summary>
    [HttpGet]
    [ResponseCache(Duration = 2592000, Location = ResponseCacheLocation.Any, NoStore = false)]
    public async Task<IActionResult> Get([FromQuery] string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest("url is required.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var target))
        {
            return BadRequest("url must be an absolute URL.");
        }

        if (!string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !AllowedImageHosts.Contains(target.Host))
        {
            return BadRequest("Host is not allowed.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(
            target,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode);
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > MaxImageBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue
        {
            Public = true,
            MaxAge = TimeSpan.FromDays(30)
        };

        return File(bytes, contentType);
    }
}
