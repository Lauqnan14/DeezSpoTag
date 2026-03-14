using System.IO.Compression;
using System.Text.Json;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Web.Services;

public sealed class DeezerArtistImageService
{
    private static readonly string[] DeezerImageProperties = { "picture_xl", "picture_big", "picture_medium", "picture" };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeezerArtistImageService> _logger;

    public DeezerArtistImageService(IHttpClientFactory httpClientFactory, ILogger<DeezerArtistImageService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> DownloadArtistImageAsync(long artistId, string artistName, string targetDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient("DeezerClient");
        var searchUrl = $"https://api.deezer.com/search/artist?q={Uri.EscapeDataString(artistName)}";
        using var searchResponse = await client.GetAsync(searchUrl, cancellationToken);
        if (!searchResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Deezer artist search failed for {ArtistName}: {StatusCode}", artistName, searchResponse.StatusCode);
            return null;
        }

        var searchContent = await ReadResponseContentAsync(searchResponse, cancellationToken);
        using var searchDoc = JsonDocument.Parse(searchContent);
        if (!searchDoc.RootElement.TryGetProperty("data", out var searchData) || searchData.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? bestMatch = null;
        foreach (var item in searchData.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name) && name.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            {
                bestMatch = item;
                break;
            }
        }

        if (bestMatch is null)
        {
            bestMatch = searchData.EnumerateArray().FirstOrDefault();
            if (bestMatch?.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
        }

        var imageUrl = ExtractImageUrl(bestMatch.Value);
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        using var imageResponse = await client.GetAsync(imageUrl, cancellationToken);
        if (!imageResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Deezer image fetch failed for {ArtistName}: {StatusCode}", artistName, imageResponse.StatusCode);
            return null;
        }

        Directory.CreateDirectory(targetDirectory);
        var extension = ResolveExtension(imageResponse.Content.Headers.ContentType?.MediaType);
        var fileName = $"{artistId}{extension}";
        var targetPath = Path.Join(targetDirectory, fileName);
        await using var targetStream = File.Create(targetPath);
        await imageResponse.Content.CopyToAsync(targetStream, cancellationToken);
        return targetPath;
    }

    private static string? ExtractImageUrl(JsonElement artist)
    {
        foreach (var propertyName in DeezerImageProperties)
        {
            if (TryGetAllowedImageUrl(artist, propertyName, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryGetAllowedImageUrl(JsonElement artist, string propertyName, out string? value)
    {
        value = null;
        if (!artist.TryGetProperty(propertyName, out var imageElement) || imageElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = imageElement.GetString();
        if (!DeezerImageUrlValidator.IsAllowedDeezerImageUrl(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static string ResolveExtension(string? mediaType)
        => mediaType switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".jpg"
        };

    private static async Task<string> ReadResponseContentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        if (response.Content.Headers.ContentEncoding.Any(encoding => string.Equals(encoding, "br", StringComparison.OrdinalIgnoreCase)))
        {
            using var input = new MemoryStream(bytes);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(brotli);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        if (response.Content.Headers.ContentEncoding.Any(encoding => string.Equals(encoding, "deflate", StringComparison.OrdinalIgnoreCase)))
        {
            using var input = new MemoryStream(bytes);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(deflate);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
