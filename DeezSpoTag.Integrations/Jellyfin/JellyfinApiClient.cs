using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Integrations.Jellyfin;

public class JellyfinApiClient
{
    private const string EmbyTokenHeader = "X-Emby-Token";
    private const string OverviewProperty = "Overview";
    private readonly HttpClient _httpClient;

    public JellyfinApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<JellyfinSystemInfo?> GetSystemInfoAsync(string serverUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, "/System/Info"));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JellyfinSystemInfo>(cancellationToken: cancellationToken);
    }

    public async Task<JellyfinUserInfo?> GetCurrentUserAsync(string serverUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, "/Users/Me"));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JellyfinUserInfo>(cancellationToken: cancellationToken);
    }

    public async Task<bool> RefreshLibraryAsync(string serverUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(serverUrl, "/Library/Refresh"));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<string?> FindArtistIdAsync(string serverUrl, string apiKey, string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var url = BuildUrl(serverUrl, $"/Artists?SearchTerm={Uri.EscapeDataString(artistName)}&Limit=1");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<JellyfinArtistsResponse>(cancellationToken: cancellationToken);
        return payload?.Items?.FirstOrDefault()?.Id;
    }

    public async Task<bool> UpdateArtistImageAsync(string serverUrl, string apiKey, string artistId, string imagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artistId))
        {
            return false;
        }

        if (!File.Exists(imagePath))
        {
            return false;
        }

        var url = BuildUrl(serverUrl, $"/Items/{artistId}/Images/Primary");
        await using var stream = File.OpenRead(imagePath);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(GetImageContentType(imagePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        request.Headers.Add(EmbyTokenHeader, apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateArtistBackdropAsync(string serverUrl, string apiKey, string artistId, string imagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artistId))
        {
            return false;
        }

        if (!File.Exists(imagePath))
        {
            return false;
        }

        var url = BuildUrl(serverUrl, $"/Items/{artistId}/Images/Backdrop/0");
        var uploaded = await UploadImageAsync(url, apiKey, imagePath, cancellationToken);
        if (uploaded)
        {
            return true;
        }

        // Jellyfin builds differ on Backdrop upload route support.
        var fallbackUrl = BuildUrl(serverUrl, $"/Items/{artistId}/Images/Backdrop?Index=0");
        return await UploadImageAsync(fallbackUrl, apiKey, imagePath, cancellationToken);
    }

    public async Task<bool> UpdateArtistOverviewAsync(string serverUrl, string apiKey, string artistId, string biography, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artistId) || string.IsNullOrWhiteSpace(biography))
        {
            return false;
        }

        // GET the current item so we can preserve all existing metadata fields.
        var getUrl = BuildUrl(serverUrl, $"/Items/{artistId}");
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, getUrl);
        getRequest.Headers.Add("X-Emby-Token", apiKey);
        using var getResponse = await _httpClient.SendAsync(getRequest, cancellationToken);
        if (!getResponse.IsSuccessStatusCode)
        {
            return false;
        }

        var itemJson = await getResponse.Content.ReadAsStringAsync(cancellationToken);

        // Re-serialize with the Overview field replaced.
        using var doc = JsonDocument.Parse(itemJson);
        using var ms = new MemoryStream();
        await using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == OverviewProperty)
            {
                writer.WriteString(OverviewProperty, biography);
            }
            else
            {
                prop.WriteTo(writer);
            }
        }
        if (!doc.RootElement.TryGetProperty(OverviewProperty, out _))
        {
            writer.WriteString(OverviewProperty, biography);
        }
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);

        var updatedJson = Encoding.UTF8.GetString(ms.ToArray());
        var postUrl = BuildUrl(serverUrl, $"/Items/{artistId}");
        using var postRequest = new HttpRequestMessage(HttpMethod.Post, postUrl)
        {
            Content = new StringContent(updatedJson, Encoding.UTF8, "application/json")
        };
        postRequest.Headers.Add("X-Emby-Token", apiKey);
        using var postResponse = await _httpClient.SendAsync(postRequest, cancellationToken);
        return postResponse.IsSuccessStatusCode;
    }

    private async Task<bool> UploadImageAsync(string url, string apiKey, string imagePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(imagePath);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(GetImageContentType(imagePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        request.Headers.Add(EmbyTokenHeader, apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static string BuildUrl(string baseUrl, string path)
    {
        return $"{baseUrl.TrimEnd('/')}{path}";
    }

    private static string GetImageContentType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
    }
}

public sealed class JellyfinSystemInfo
{
    [JsonPropertyName("ServerName")]
    public string? ServerName { get; set; }

    [JsonPropertyName("Version")]
    public string? Version { get; set; }
}

public sealed class JellyfinUserInfo
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }
}

public sealed class JellyfinArtistsResponse
{
    [JsonPropertyName("Items")]
    public List<JellyfinArtistItem>? Items { get; set; }
}

public sealed class JellyfinArtistItem
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }
}
