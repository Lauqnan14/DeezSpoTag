using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Controllers;

/// <summary>
/// Controller for serving Deezer images through a proxy
/// This handles the MD5 hash to URL conversion like deezspotag does
/// </summary>
[Route("api/[controller]")]
[Authorize]
public class ImageController : Controller
{
    private static readonly int[] ValidSizes = { 56, 75, 120, 156, 250, 264, 500, 1000, 1200, 1400 };
    private static readonly HashSet<string> ValidTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover",
        "artist",
        "playlist",
        "misc"
    };
    private static readonly Regex Md5Regex = new(
        "^[a-fA-F0-9]{32}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));
    private readonly ILogger<ImageController> _logger;
    private readonly HttpClient _httpClient;

    public ImageController(ILogger<ImageController> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Serve Deezer images by MD5 hash
    /// Route: /api/image/{type}/{md5}/{size?}
    /// Example: /api/image/cover/2e018122cb56986277102d2041a592c8/75
    /// </summary>
    [HttpGet("{type}/{md5}/{size?}")]
    public async Task<IActionResult> GetImage(string type, string md5, int size = 75)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            if (!TryValidateRequest(type, md5, size, out var validationError, out var normalizedType, out var normalizedSize))
            {
                return BadRequest(validationError);
            }

            var deezerUrl = BuildDeezerUrl(normalizedType, md5, normalizedSize);

            _logger.LogDebug("Proxying image request: DeezerUrl");

            // Fetch image from Deezer CDN
            var response = await _httpClient.GetAsync(deezerUrl);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch image from Deezer CDN: StatusCode for URL: DeezerUrl");
                
                // Return a placeholder or 404
                return NotFound("Image not found");
            }

            var imageBytes = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";

            // Cache headers (images rarely change)
            var typedHeaders = Response.GetTypedHeaders();
            typedHeaders.CacheControl = new CacheControlHeaderValue
            {
                Public = true,
                MaxAge = TimeSpan.FromHours(24)
            };
            typedHeaders.ETag = new EntityTagHeaderValue($"\"{md5}_{normalizedSize}\"");

            return File(imageBytes, contentType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error proxying image request for Type/MD5/Size");
            return StatusCode(500, "Error fetching image");
        }
    }

    /// <summary>
    /// Get image URL for a given MD5 and type
    /// Route: /api/image/url/{type}/{md5}/{size?}
    /// Returns JSON with the image URL
    /// </summary>
    [HttpGet("url/{type}/{md5}/{size?}")]
    public IActionResult GetImageUrl(string type, string md5, int size = 75)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            if (!TryValidateRequest(type, md5, size, out var validationError, out var normalizedType, out var normalizedSize))
            {
                return BadRequest(validationError);
            }

            var proxyUrl = $"/api/image/{normalizedType}/{md5}/{normalizedSize}";
            var deezerUrl = BuildDeezerUrl(normalizedType, md5, normalizedSize);

            return Json(new
            {
                success = true,
                url = proxyUrl,
                deezerUrl
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error generating image URL for Type/MD5/Size");
            return StatusCode(500, "Error generating image URL");
        }
    }

    private static string BuildDeezerUrl(string type, string md5, int size)
    {
        return $"https://e-cdns-images.dzcdn.net/images/{type}/{md5}/{size}x{size}-000000-80-0-0.jpg";
    }

    private static bool TryValidateRequest(
        string type,
        string md5,
        int size,
        out string? validationError,
        out string normalizedType,
        out int normalizedSize)
    {
        validationError = null;
        normalizedType = string.Empty;
        normalizedSize = NormalizeSize(size);

        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(md5))
        {
            validationError = "Type and MD5 are required";
            return false;
        }

        if (!ValidTypes.Contains(type))
        {
            validationError = $"Invalid type. Must be one of: {string.Join(", ", ValidTypes)}";
            return false;
        }

        if (!Md5Regex.IsMatch(md5))
        {
            validationError = "Invalid MD5 hash.";
            return false;
        }

        normalizedType = type.ToLowerInvariant();
        return true;
    }

    private static int NormalizeSize(int size)
    {
        return ValidSizes.Contains(size) ? size : 75;
    }
}
