using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Services.Download.Shared.Utils;

internal static class SongLinkClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<string> ResolvePlatformUrlAsync(
        HttpClient client,
        string spotifyId,
        string platform,
        CancellationToken cancellationToken)
    {
        var spotifyUrl = $"https://open.spotify.com/track/{spotifyId}";
        var apiUrl = $"https://api.song.link/v1-alpha.1/links?url={WebUtility.UrlEncode(spotifyUrl)}";

        using var response = await client.GetAsync(apiUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"song.link failed ({(int)response.StatusCode})");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var payload = JsonSerializer.Deserialize<SongLinkResponse>(body, SerializerOptions);
        if (payload?.LinksByPlatform == null || !payload.LinksByPlatform.TryGetValue(platform, out var link) || string.IsNullOrWhiteSpace(link.Url))
        {
            throw new InvalidOperationException($"{platform} link not found");
        }

        return link.Url;
    }

    private sealed class SongLinkResponse
    {
        [JsonPropertyName("linksByPlatform")]
        public Dictionary<string, SongLinkLink> LinksByPlatform { get; set; } = new();
    }

    private sealed class SongLinkLink
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }
}
