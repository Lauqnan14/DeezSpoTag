using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Integrations.Navidrome;

public sealed class NavidromeApiClient
{
    private const string ClientName = "DeezSpoTag";
    private const string ApiVersion = "1.16.1";
    private readonly HttpClient _httpClient;

    public NavidromeApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<NavidromeSystemInfo?> PingAsync(
        string serverUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<NavidromePingResponse>(
            serverUrl,
            username,
            password,
            "ping",
            Array.Empty<KeyValuePair<string, string?>>(),
            cancellationToken);
        return response?.SubsonicResponse?.Status is "ok"
            ? new NavidromeSystemInfo(
                response.SubsonicResponse.ServerVersion,
                response.SubsonicResponse.Type ?? "Navidrome")
            : null;
    }

    public async Task<List<NavidromeAudioTrack>> SearchTracksAsync(
        string serverUrl,
        string username,
        string password,
        string searchTerm,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return new List<NavidromeAudioTrack>();
        }

        var response = await SendAsync<NavidromeSearchResponse>(
            serverUrl,
            username,
            password,
            "search3",
            new[]
            {
                new KeyValuePair<string, string?>("query", searchTerm),
                new KeyValuePair<string, string?>("songCount", "25"),
                new KeyValuePair<string, string?>("albumCount", "0"),
                new KeyValuePair<string, string?>("artistCount", "0")
            },
            cancellationToken);
        return response?.SubsonicResponse?.SearchResult?.Songs?
            .Where(static song => !string.IsNullOrWhiteSpace(song.Id))
            .Select(static song => new NavidromeAudioTrack(
                song.Id!,
                song.Title ?? string.Empty,
                song.Artist ?? string.Empty,
                song.Duration.HasValue ? song.Duration.Value * 1000 : null))
            .ToList() ?? new List<NavidromeAudioTrack>();
    }

    public async Task<List<NavidromePlaylistSummary>> GetPlaylistsAsync(
        string serverUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<NavidromePlaylistsResponse>(
            serverUrl,
            username,
            password,
            "getPlaylists",
            Array.Empty<KeyValuePair<string, string?>>(),
            cancellationToken);
        return response?.SubsonicResponse?.Playlists?.Playlists?
            .Where(static playlist => !string.IsNullOrWhiteSpace(playlist.Id))
            .Select(static playlist => new NavidromePlaylistSummary(
                playlist.Id!,
                playlist.Name ?? playlist.Id!,
                playlist.SongCount))
            .ToList() ?? new List<NavidromePlaylistSummary>();
    }

    public async Task<bool> StartScanAsync(
        string serverUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<NavidromePingResponse>(
            serverUrl,
            username,
            password,
            "startScan",
            Array.Empty<KeyValuePair<string, string?>>(),
            cancellationToken);
        return response?.SubsonicResponse?.Status is "ok";
    }

    public async Task<string?> FindPlaylistIdByNameAsync(
        string serverUrl,
        string username,
        string password,
        string playlistName,
        CancellationToken cancellationToken = default)
    {
        var playlists = await GetPlaylistsAsync(serverUrl, username, password, cancellationToken);
        return playlists.FirstOrDefault(playlist =>
            string.Equals(playlist.Name, playlistName, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    public async Task<string?> CreateOrUpdatePlaylistAsync(
        string serverUrl,
        string username,
        string password,
        string playlistName,
        IReadOnlyCollection<string> songIds,
        string? existingPlaylistId,
        bool appendMissingOnly,
        CancellationToken cancellationToken = default)
    {
        var playlistId = string.IsNullOrWhiteSpace(existingPlaylistId)
            ? await FindPlaylistIdByNameAsync(serverUrl, username, password, playlistName, cancellationToken)
            : existingPlaylistId.Trim();

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            var create = await SendAsync<NavidromePlaylistUpdateResponse>(
                serverUrl,
                username,
                password,
                "createPlaylist",
                BuildPlaylistParameters(null, playlistName, songIds),
                cancellationToken);
            return create?.SubsonicResponse?.Playlist?.Id;
        }

        var targetIds = songIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (appendMissingOnly)
        {
            var currentIds = (await GetPlaylistEntriesAsync(serverUrl, username, password, playlistId, cancellationToken))
                .Select(static entry => entry.ItemId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            targetIds = targetIds.Where(id => !currentIds.Contains(id)).ToList();
            if (targetIds.Count == 0)
            {
                return playlistId;
            }
        }

        await SendAsync<NavidromePlaylistUpdateResponse>(
            serverUrl,
            username,
            password,
            appendMissingOnly ? "updatePlaylist" : "createPlaylist",
            BuildPlaylistParameters(playlistId, playlistName, targetIds),
            cancellationToken);
        return playlistId;
    }

    public async Task<List<NavidromePlaylistEntry>> GetPlaylistEntriesAsync(
        string serverUrl,
        string username,
        string password,
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<NavidromePlaylistResponse>(
            serverUrl,
            username,
            password,
            "getPlaylist",
            new[] { new KeyValuePair<string, string?>("id", playlistId) },
            cancellationToken);
        return response?.SubsonicResponse?.Playlist?.Entries?
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Id))
            .Select(static entry => new NavidromePlaylistEntry(entry.Id!))
            .ToList() ?? new List<NavidromePlaylistEntry>();
    }

    private async Task<T?> SendAsync<T>(
        string serverUrl,
        string username,
        string password,
        string method,
        IEnumerable<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password))
        {
            return default;
        }

        var requestUrl = BuildUrl(serverUrl, method, username, password, parameters);
        using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private static string BuildUrl(
        string serverUrl,
        string method,
        string username,
        string password,
        IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var salt = Guid.NewGuid().ToString("N");
        var token = ToMd5Hex(password + salt);
        var query = new List<KeyValuePair<string, string?>>
        {
            new("u", username),
            new("t", token),
            new("s", salt),
            new("v", ApiVersion),
            new("c", ClientName),
            new("f", "json")
        };
        query.AddRange(parameters.Where(static parameter => !string.IsNullOrWhiteSpace(parameter.Value)));
        var encoded = string.Join("&", query.Select(static item =>
            $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));
        return $"{serverUrl.TrimEnd('/')}/rest/{method}.view?{encoded}";
    }

    private static List<KeyValuePair<string, string?>> BuildPlaylistParameters(
        string? playlistId,
        string playlistName,
        IReadOnlyCollection<string> songIds)
    {
        var parameters = new List<KeyValuePair<string, string?>>();
        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            parameters.Add(new KeyValuePair<string, string?>("playlistId", playlistId));
        }
        parameters.Add(new KeyValuePair<string, string?>("name", playlistName));
        parameters.AddRange(songIds.Select(static id => new KeyValuePair<string, string?>("songId", id)));
        return parameters;
    }

    private static string ToMd5Hex(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record NavidromeSystemInfo(string? Version, string ServerName);
public sealed record NavidromeAudioTrack(string Id, string Title, string Artist, int? DurationMs);
public sealed record NavidromePlaylistSummary(string Id, string Name, int? TrackCount);
public sealed record NavidromePlaylistEntry(string ItemId);

file class NavidromeBaseResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }
    [JsonPropertyName("version")]
    public string? ServerVersion { get; set; }
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

file sealed class NavidromePingResponse
{
    [JsonPropertyName("subsonic-response")]
    public NavidromeBaseResponse? SubsonicResponse { get; set; }
}

file sealed class NavidromeSearchResponse
{
    [JsonPropertyName("subsonic-response")]
    public NavidromeSearchSubsonicResponse? SubsonicResponse { get; set; }
}

file sealed class NavidromeSearchSubsonicResponse : NavidromeBaseResponse
{
    [JsonPropertyName("searchResult3")]
    public NavidromeSearchResult? SearchResult { get; set; }
}

file sealed class NavidromeSearchResult
{
    [JsonPropertyName("song")]
    public List<NavidromeSong>? Songs { get; set; }
}

file sealed class NavidromePlaylistsResponse
{
    [JsonPropertyName("subsonic-response")]
    public NavidromePlaylistsSubsonicResponse? SubsonicResponse { get; set; }
}

file sealed class NavidromePlaylistsSubsonicResponse : NavidromeBaseResponse
{
    [JsonPropertyName("playlists")]
    public NavidromePlaylistsContainer? Playlists { get; set; }
}

file sealed class NavidromePlaylistsContainer
{
    [JsonPropertyName("playlist")]
    public List<NavidromePlaylist>? Playlists { get; set; }
}

file sealed class NavidromePlaylistResponse
{
    [JsonPropertyName("subsonic-response")]
    public NavidromePlaylistSubsonicResponse? SubsonicResponse { get; set; }
}

file sealed class NavidromePlaylistUpdateResponse
{
    [JsonPropertyName("subsonic-response")]
    public NavidromePlaylistSubsonicResponse? SubsonicResponse { get; set; }
}

file sealed class NavidromePlaylistSubsonicResponse : NavidromeBaseResponse
{
    [JsonPropertyName("playlist")]
    public NavidromePlaylist? Playlist { get; set; }
}

file sealed class NavidromePlaylist
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("songCount")]
    public int? SongCount { get; set; }
    [JsonPropertyName("entry")]
    public List<NavidromeSong>? Entries { get; set; }
}

file sealed class NavidromeSong
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("artist")]
    public string? Artist { get; set; }
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }
}
