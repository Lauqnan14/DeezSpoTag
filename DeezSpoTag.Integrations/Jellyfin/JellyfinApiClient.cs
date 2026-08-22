using DeezSpoTag.Integrations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Integrations.Jellyfin;

public class JellyfinApiClient
{
    private const int PlaylistWriteBatchSize = 100;
    private const string EmbyTokenHeader = "X-Emby-Token";
    private const string OverviewProperty = "Overview";
    private const string RecursiveQuerySegment = "?Recursive=true";
    private const string RecursiveQueryParameter = "&Recursive=true";
    private const int JellyfinTimeTicksPerMillisecond = 10_000;
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

    public async Task<JellyfinUserInfo?> ResolveUserAsync(
        string serverUrl,
        string apiKey,
        string? username = null,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(serverUrl, apiKey, cancellationToken);
        if (currentUser is not null)
        {
            return currentUser;
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            var byId = await GetUserByIdAsync(serverUrl, apiKey, userId, cancellationToken);
            if (byId is not null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            var byName = await GetUserByNameAsync(serverUrl, apiKey, username, cancellationToken);
            if (byName is not null)
            {
                return byName;
            }
        }

        return null;
    }

    public async Task<JellyfinUserInfo?> GetUserByIdAsync(
        string serverUrl,
        string apiKey,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, $"/Users/{Uri.EscapeDataString(userId)}"));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JellyfinUserInfo>(cancellationToken: cancellationToken);
    }

    public async Task<JellyfinUserInfo?> GetUserByNameAsync(
        string serverUrl,
        string apiKey,
        string username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, "/Users"));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var users = await response.Content.ReadFromJsonAsync<List<JellyfinUserInfo>>(cancellationToken: cancellationToken);
        if (users is null || users.Count == 0)
        {
            return null;
        }

        return users.FirstOrDefault(user =>
            !string.IsNullOrWhiteSpace(user.Name)
            && string.Equals(user.Name, username, StringComparison.OrdinalIgnoreCase));
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

    public async Task<List<JellyfinLibrarySection>> GetLibrariesAsync(
        string serverUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return new List<JellyfinLibrarySection>();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, "/Library/VirtualFolders"));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<JellyfinLibrarySection>();
        }

        var libraries = await response.Content.ReadFromJsonAsync<List<JellyfinLibrarySection>>(cancellationToken: cancellationToken);
        return libraries ?? new List<JellyfinLibrarySection>();
    }

    public async Task<List<JellyfinMediaItem>> GetLibraryItemsAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string libraryId,
        int offset = 0,
        int? maxItems = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(libraryId))
        {
            return new List<JellyfinMediaItem>();
        }

        var items = new List<JellyfinMediaItem>();
        var startIndex = Math.Max(offset, 0);
        var remaining = Math.Clamp(maxItems.GetValueOrDefault(200), 1, 2000);
        const int maxPageSize = 200;
        var query = new StringBuilder();

        while (true)
        {
            var pageSize = Math.Min(maxPageSize, remaining);
            query.Clear();
            query.Append($"/Users/{Uri.EscapeDataString(userId)}/Items");
            query.Append($"?ParentId={Uri.EscapeDataString(libraryId)}");
            query.Append(RecursiveQueryParameter);
            query.Append("&SortBy=SortName");
            query.Append("&SortOrder=Ascending");
            query.Append("&IncludeItemTypes=Movie,Series");
            query.Append($"&Limit={pageSize}");
            query.Append($"&StartIndex={startIndex}");

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, query.ToString()));
            request.Headers.Add(EmbyTokenHeader, apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            var payload = await response.Content.ReadFromJsonAsync<JellyfinItemsResponse>(cancellationToken: cancellationToken);
            var page = payload?.Items ?? new List<JellyfinMediaItem>();
            if (page.Count == 0)
            {
                break;
            }

            items.AddRange(page);
            startIndex += page.Count;
            remaining -= page.Count;

            if (page.Count < pageSize || remaining <= 0)
            {
                break;
            }
        }

        return items;
    }

    public async Task<List<JellyfinMediaItem>> GetLibraryRecentlyAddedItemsAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string libraryId,
        int? maxItems = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(libraryId))
        {
            return new List<JellyfinMediaItem>();
        }

        var pageSize = Math.Clamp(maxItems.GetValueOrDefault(100), 1, 200);
        var query = new StringBuilder();
        query.Append($"/Users/{Uri.EscapeDataString(userId)}/Items");
        query.Append($"?ParentId={Uri.EscapeDataString(libraryId)}");
        query.Append(RecursiveQueryParameter);
        query.Append("&SortBy=DateCreated");
        query.Append("&SortOrder=Descending");
        query.Append("&IncludeItemTypes=Movie,Series");
        query.Append($"&Limit={pageSize}");
        query.Append("&StartIndex=0");
        return await SendItemsRequestAsync(serverUrl, apiKey, query.ToString(), cancellationToken);
    }

    public async Task<List<JellyfinMediaItem>> GetShowSeasonsAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string showId,
        CancellationToken cancellationToken = default)
    {
        return await GetUserChildItemsAsync(serverUrl, apiKey, userId, showId, "Season", cancellationToken);
    }

    public async Task<List<JellyfinMediaItem>> GetSeasonEpisodesAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string seasonId,
        CancellationToken cancellationToken = default)
    {
        return await GetUserChildItemsAsync(serverUrl, apiKey, userId, seasonId, "Episode", cancellationToken);
    }

    public async Task<IReadOnlyList<string>> FindArtistIdsAsync(string serverUrl, string apiKey, string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artistName))
        {
            return Array.Empty<string>();
        }

        var normalizedArtistName = artistName.Trim();
        var url = BuildUrl(serverUrl, $"/Artists?SearchTerm={Uri.EscapeDataString(normalizedArtistName)}&Limit=200");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<string>();
        }

        var payload = await response.Content.ReadFromJsonAsync<JellyfinArtistsResponse>(cancellationToken: cancellationToken);
        var items = payload?.Items ?? new List<JellyfinArtistItem>();
        var exactMatches = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Id)
                           && !string.IsNullOrWhiteSpace(item.Name)
                           && string.Equals(item.Name.Trim(), normalizedArtistName, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (exactMatches.Count > 0)
        {
            return exactMatches;
        }

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(item => item.Id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> FindArtistIdAsync(string serverUrl, string apiKey, string artistName, CancellationToken cancellationToken = default)
    {
        var matches = await FindArtistIdsAsync(serverUrl, apiKey, artistName, cancellationToken);
        return matches.Count > 0 ? matches[0] : null;
    }

    public async Task<List<JellyfinAudioTrack>> SearchTracksAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string searchTerm,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(searchTerm))
        {
            return new List<JellyfinAudioTrack>();
        }

        var query = new StringBuilder();
        query.Append($"/Users/{Uri.EscapeDataString(userId)}/Items");
        query.Append(RecursiveQuerySegment);
        query.Append("&IncludeItemTypes=Audio");
        query.Append("&Fields=Path,RunTimeTicks,AlbumArtists,Artists,Album");
        query.Append("&Limit=25");
        query.Append($"&SearchTerm={Uri.EscapeDataString(searchTerm)}");

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, query.ToString()));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<JellyfinAudioTrack>();
        }

        var payload = await response.Content.ReadFromJsonAsync<JellyfinItemsResponse>(cancellationToken: cancellationToken);
        var items = payload?.Items ?? new List<JellyfinMediaItem>();
        return items
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(static item => new JellyfinAudioTrack(
                item.Id!,
                item.Name ?? string.Empty,
                ResolveArtistText(item),
                item.Album,
                item.RunTimeTicks.HasValue
                    ? (int?)Math.Min(item.RunTimeTicks.Value / JellyfinTimeTicksPerMillisecond, int.MaxValue)
                    : null,
                item.Path))
            .ToList();
    }

    public async Task<List<JellyfinAudioTrack>> GetAudioTracksAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string? libraryId = null,
        int offset = 0,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId))
        {
            return new List<JellyfinAudioTrack>();
        }

        var normalizedOffset = Math.Max(0, offset);
        var normalizedLimit = Math.Clamp(limit, 1, 1000);
        var query = new StringBuilder();
        query.Append($"/Users/{Uri.EscapeDataString(userId)}/Items");
        query.Append(RecursiveQuerySegment);
        query.Append("&IncludeItemTypes=Audio");
        query.Append("&Fields=Path,RunTimeTicks,AlbumArtists,Artists,Album");
        query.Append($"&Limit={normalizedLimit}");
        query.Append($"&StartIndex={normalizedOffset}");
        if (!string.IsNullOrWhiteSpace(libraryId))
        {
            query.Append($"&ParentId={Uri.EscapeDataString(libraryId)}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, query.ToString()));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<JellyfinAudioTrack>();
        }

        var payload = await response.Content.ReadFromJsonAsync<JellyfinItemsResponse>(cancellationToken: cancellationToken);
        return payload?.Items?
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(static item => new JellyfinAudioTrack(
                item.Id!,
                item.Name ?? string.Empty,
                ResolveArtistText(item),
                item.Album,
                item.RunTimeTicks.HasValue
                    ? (int?)Math.Min(item.RunTimeTicks.Value / JellyfinTimeTicksPerMillisecond, int.MaxValue)
                    : null,
                item.Path))
            .ToList() ?? new List<JellyfinAudioTrack>();
    }

    public async Task<List<JellyfinHistoryItem>> GetAudioPlayHistoryAsync(
        string serverUrl,
        string apiKey,
        string userId,
        int limit = 500,
        CancellationToken cancellationToken = default)
        => await GetAudioPlayHistoryInternalAsync(
            serverUrl,
            apiKey,
            userId,
            libraryId: null,
            limit,
            cancellationToken);

    public async Task<List<JellyfinHistoryItem>> GetAudioPlayHistoryAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string libraryId,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            return new List<JellyfinHistoryItem>();
        }

        return await GetAudioPlayHistoryInternalAsync(
            serverUrl,
            apiKey,
            userId,
            libraryId.Trim(),
            limit,
            cancellationToken);
    }

    private async Task<List<JellyfinHistoryItem>> GetAudioPlayHistoryInternalAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string? libraryId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId))
        {
            return new List<JellyfinHistoryItem>();
        }

        var query = new StringBuilder();
        query.Append($"/Users/{Uri.EscapeDataString(userId)}/Items");
        query.Append(RecursiveQuerySegment);
        query.Append("&IncludeItemTypes=Audio");
        query.Append("&Filters=IsPlayed");
        query.Append("&SortBy=DatePlayed");
        query.Append("&SortOrder=Descending");
        query.Append("&Fields=Path,RunTimeTicks,UserData,Artists,Album");
        query.Append($"&Limit={Math.Clamp(limit, 1, 2000)}");
        if (!string.IsNullOrWhiteSpace(libraryId))
        {
            query.Append($"&ParentId={Uri.EscapeDataString(libraryId)}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, query.ToString()));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<JellyfinHistoryItem>();
        }

        var payload = await response.Content.ReadFromJsonAsync<JellyfinItemsResponse>(cancellationToken: cancellationToken);
        var items = payload?.Items ?? new List<JellyfinMediaItem>();
        return items
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id) && item.UserData?.LastPlayedDate is not null)
            .Select(static item => new JellyfinHistoryItem(
                item.Id!,
                item.Name ?? string.Empty,
                ResolveArtistText(item),
                item.Album ?? string.Empty,
                item.Path,
                item.UserData!.LastPlayedDate!.Value,
                item.RunTimeTicks.HasValue
                    ? (int?)Math.Min(item.RunTimeTicks.Value / JellyfinTimeTicksPerMillisecond, int.MaxValue)
                    : null))
            .ToList();
    }

    public async Task<string?> FindPlaylistIdByNameAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string playlistName,
        CancellationToken cancellationToken = default)
    {
        var lookup = await FindPlaylistIdByNameResult(serverUrl, apiKey, userId, playlistName, cancellationToken);
        return lookup.Status == TargetLookupStatus.Success ? lookup.Value : null;
    }

    public async Task<TargetPlaylistLookup<string>> FindPlaylistIdByNameResult(
        string serverUrl,
        string apiKey,
        string userId,
        string playlistName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(playlistName))
        {
            return TargetPlaylistLookup<string>.Missing();
        }

        var query = new StringBuilder();
        query.Append($"/Users/{Uri.EscapeDataString(userId)}/Items");
        query.Append(RecursiveQuerySegment);
        query.Append("&IncludeItemTypes=Playlist");
        query.Append("&Limit=200");
        query.Append($"&SearchTerm={Uri.EscapeDataString(playlistName)}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, query.ToString()));
            request.Headers.Add(EmbyTokenHeader, apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var classified = TargetLookupClassifier.FromHttpStatus(response.StatusCode);
            if (classified != TargetLookupStatus.Success)
            {
                return new TargetPlaylistLookup<string>(classified, null, statusCode);
            }

            var payload = await response.Content.ReadFromJsonAsync<JellyfinItemsResponse>(cancellationToken: cancellationToken);
            var items = payload?.Items ?? new List<JellyfinMediaItem>();
            var exactMatch = items.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.Id)
                && string.Equals(item.Name, playlistName, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(exactMatch?.Id)
                ? TargetPlaylistLookup<string>.Missing(statusCode)
                : TargetPlaylistLookup<string>.Found(exactMatch.Id, statusCode);
        }
        catch (Exception ex) when (TargetLookupClassifier.IsTransientTransport(ex, cancellationToken))
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }
    }

    public async Task<List<JellyfinMediaItem>> GetPlaylistsAsync(
        string serverUrl,
        string apiKey,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId))
        {
            return new List<JellyfinMediaItem>();
        }

        var query = new StringBuilder();
        query.Append($"/Users/{Uri.EscapeDataString(userId)}/Items");
        query.Append(RecursiveQuerySegment);
        query.Append("&IncludeItemTypes=Playlist");
        query.Append("&SortBy=SortName");
        query.Append("&SortOrder=Ascending");
        query.Append("&Limit=500");

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, query.ToString()));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<JellyfinMediaItem>();
        }

        var payload = await response.Content.ReadFromJsonAsync<JellyfinItemsResponse>(cancellationToken: cancellationToken);
        return payload?.Items ?? new List<JellyfinMediaItem>();
    }

    public async Task<string?> CreatePlaylistAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string playlistName,
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(playlistName))
        {
            return null;
        }

        var normalizedItemIds = itemIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var firstBatch = normalizedItemIds.Take(PlaylistWriteBatchSize).ToList();
        var ids = string.Join(",", firstBatch);

        var query = new StringBuilder();
        query.Append("/Playlists");
        query.Append($"?UserId={Uri.EscapeDataString(userId)}");
        query.Append($"&Name={Uri.EscapeDataString(playlistName)}");
        query.Append("&MediaType=Audio");
        if (!string.IsNullOrWhiteSpace(ids))
        {
            query.Append($"&Ids={Uri.EscapeDataString(ids)}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(serverUrl, query.ToString()));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(idElement.GetString()))
                {
                    var createdId = idElement.GetString();
                    return await AddRemainingPlaylistItemsAsync(
                        serverUrl,
                        apiKey,
                        userId,
                        createdId!,
                        normalizedItemIds,
                        firstBatch.Count,
                        cancellationToken)
                        ? createdId
                        : null;
                }
            }
            catch (JsonException)
            {
                // Ignore parse failures and fallback to list lookup.
            }
        }

        var playlistId = await FindPlaylistIdByNameAsync(serverUrl, apiKey, userId, playlistName, cancellationToken);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        return await AddRemainingPlaylistItemsAsync(
            serverUrl,
            apiKey,
            userId,
            playlistId,
            normalizedItemIds,
            firstBatch.Count,
            cancellationToken)
            ? playlistId
            : null;
    }

    public async Task<List<JellyfinPlaylistEntry>> GetPlaylistEntriesAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(playlistId))
        {
            return new List<JellyfinPlaylistEntry>();
        }

        var query = $"/Playlists/{Uri.EscapeDataString(playlistId)}/Items?UserId={Uri.EscapeDataString(userId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, query));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<JellyfinPlaylistEntry>();
        }

        var payload = await response.Content.ReadFromJsonAsync<JellyfinItemsResponse>(cancellationToken: cancellationToken);
        var items = payload?.Items ?? new List<JellyfinMediaItem>();
        return items
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(static item => new JellyfinPlaylistEntry(
                item.Id!,
                string.IsNullOrWhiteSpace(item.PlaylistItemId) ? item.Id! : item.PlaylistItemId!))
            .ToList();
    }

    public async Task<bool> AddPlaylistItemsAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(playlistId))
        {
            return false;
        }

        var normalizedItemIds = itemIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedItemIds.Count == 0)
        {
            return true;
        }

        for (var offset = 0; offset < normalizedItemIds.Count; offset += PlaylistWriteBatchSize)
        {
            var ids = string.Join(",", normalizedItemIds.Skip(offset).Take(PlaylistWriteBatchSize));
            var query = new StringBuilder();
            query.Append($"/Playlists/{Uri.EscapeDataString(playlistId)}/Items");
            query.Append($"?UserId={Uri.EscapeDataString(userId)}");
            query.Append($"&Ids={Uri.EscapeDataString(ids)}");

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(serverUrl, query.ToString()));
            request.Headers.Add(EmbyTokenHeader, apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> AddRemainingPlaylistItemsAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyList<string> itemIds,
        int offset,
        CancellationToken cancellationToken)
        => offset >= itemIds.Count
            || await AddPlaylistItemsAsync(
                serverUrl,
                apiKey,
                userId,
                playlistId,
                itemIds.Skip(offset).ToList(),
                cancellationToken);

    public async Task<bool> RemovePlaylistEntriesAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyCollection<string> entryIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(playlistId))
        {
            return false;
        }

        var normalizedEntryIds = entryIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedEntryIds.Count == 0)
        {
            return true;
        }

        // Chunk the same way AddPlaylistItemsAsync does -- a single DELETE carrying every entry
        // ID for a large playlist (300+ tracks) produces a query string long enough that Jellyfin
        // (or an intermediate proxy) rejects the request outright, which previously made clearing
        // large playlists fail unconditionally.
        for (var offset = 0; offset < normalizedEntryIds.Count; offset += PlaylistWriteBatchSize)
        {
            var ids = string.Join(",", normalizedEntryIds.Skip(offset).Take(PlaylistWriteBatchSize));
            var query = new StringBuilder();
            query.Append($"/Playlists/{Uri.EscapeDataString(playlistId)}/Items");
            query.Append($"?UserId={Uri.EscapeDataString(userId)}");
            query.Append($"&EntryIds={Uri.EscapeDataString(ids)}");

            using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUrl(serverUrl, query.ToString()));
            request.Headers.Add(EmbyTokenHeader, apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
        }

        return true;
    }

    public async Task<bool> UpdateItemPrimaryImageFromUrlAsync(
        string serverUrl,
        string apiKey,
        string itemId,
        string imageUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(itemId)
            || string.IsNullOrWhiteSpace(imageUrl))
        {
            return false;
        }

        using var imageRequest = new HttpRequestMessage(HttpMethod.Get, imageUrl);
        using var imageResponse = await _httpClient.SendAsync(
            imageRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!imageResponse.IsSuccessStatusCode)
        {
            return false;
        }

        var mediaType = imageResponse.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            mediaType = GetImageContentTypeFromUrl(imageUrl);
        }

        var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        return await UpdateItemPrimaryImageAsync(serverUrl, apiKey, itemId, imageBytes, mediaType, cancellationToken);
    }

    public async Task<bool> UpdateItemPrimaryImageFromFileAsync(
        string serverUrl,
        string apiKey,
        string itemId,
        string imagePath,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(itemId)
            || string.IsNullOrWhiteSpace(imagePath)
            || !File.Exists(imagePath))
        {
            return false;
        }

        var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        return await UpdateItemPrimaryImageAsync(
            serverUrl,
            apiKey,
            itemId,
            imageBytes,
            string.IsNullOrWhiteSpace(contentType) ? GetImageContentTypeFromUrl(imagePath) : contentType,
            cancellationToken);
    }

    public async Task<bool> VerifyItemPrimaryImageFromFileAsync(
        string serverUrl,
        string apiKey,
        string itemId,
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(itemId)
            || string.IsNullOrWhiteSpace(imagePath)
            || !File.Exists(imagePath))
        {
            return false;
        }

        var expectedBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        if (expectedBytes.Length == 0)
        {
            return false;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUrl(serverUrl,
                $"/Items/{Uri.EscapeDataString(itemId)}/Images/Primary?tag={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var storedBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(expectedBytes),
            SHA256.HashData(storedBytes));
    }

    public async Task<bool> UpdateArtistImageAsync(string serverUrl, string apiKey, string artistId, string imagePath, CancellationToken cancellationToken = default)
    {
        if (!CanUploadArtistAsset(serverUrl, apiKey, artistId, imagePath))
        {
            return false;
        }

        var url = BuildUrl(serverUrl, $"/Items/{artistId}/Images/Primary");
        return await UploadImageAsync(url, apiKey, imagePath, cancellationToken);
    }

    public async Task<bool> UpdateArtistBackdropAsync(string serverUrl, string apiKey, string artistId, string imagePath, CancellationToken cancellationToken = default)
    {
        if (!CanUploadArtistAsset(serverUrl, apiKey, artistId, imagePath))
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
        return await UpdateItemOverviewAsync(serverUrl, apiKey, artistId, biography, cancellationToken);
    }

    public async Task<bool> UpdateItemOverviewAsync(
        string serverUrl,
        string apiKey,
        string itemId,
        string overview,
        CancellationToken cancellationToken = default)
    {
        return await UpdateItemMetadataAsync(serverUrl, apiKey, itemId, name: null, overview, cancellationToken);
    }

    public async Task<bool> UpdateItemMetadataAsync(
        string serverUrl,
        string apiKey,
        string itemId,
        string? name,
        string? overview,
        CancellationToken cancellationToken = default)
        => await UpdateItemMetadataAsync(serverUrl, apiKey, userId: null, itemId, name, overview, cancellationToken);

    public async Task<bool> UpdateItemMetadataAsync(
        string serverUrl,
        string apiKey,
        string? userId,
        string itemId,
        string? name,
        string? overview,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(itemId)
            || (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(overview)))
        {
            return false;
        }

        var getUrl = BuildUrl(
            serverUrl,
            string.IsNullOrWhiteSpace(userId)
                ? $"/Items/{Uri.EscapeDataString(itemId)}"
                : $"/Users/{Uri.EscapeDataString(userId)}/Items/{Uri.EscapeDataString(itemId)}");
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, getUrl);
        getRequest.Headers.Add(EmbyTokenHeader, apiKey);
        using var getResponse = await _httpClient.SendAsync(getRequest, cancellationToken);
        if (!getResponse.IsSuccessStatusCode)
        {
            return false;
        }

        var itemJson = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(itemJson);
        using var ms = new MemoryStream();
        await using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (!string.IsNullOrWhiteSpace(name) && property.NameEquals("Name"))
            {
                writer.WriteString("Name", name);
            }
            else if (!string.IsNullOrWhiteSpace(overview) && property.NameEquals(OverviewProperty))
            {
                writer.WriteString(OverviewProperty, overview);
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        if (!string.IsNullOrWhiteSpace(name) && !doc.RootElement.TryGetProperty("Name", out _))
        {
            writer.WriteString("Name", name);
        }

        if (!string.IsNullOrWhiteSpace(overview) && !doc.RootElement.TryGetProperty(OverviewProperty, out _))
        {
            writer.WriteString(OverviewProperty, overview);
        }

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);

        var updatedJson = Encoding.UTF8.GetString(ms.ToArray());
        var postUrl = BuildUrl(serverUrl, $"/Items/{itemId}");
        using var postRequest = new HttpRequestMessage(HttpMethod.Post, postUrl)
        {
            Content = new StringContent(updatedJson, Encoding.UTF8, "application/json")
        };
        postRequest.Headers.Add(EmbyTokenHeader, apiKey);
        using var postResponse = await _httpClient.SendAsync(postRequest, cancellationToken);
        return postResponse.IsSuccessStatusCode;
    }

    public async Task<JellyfinMediaItem?> GetItemAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var lookup = await GetItemResult(serverUrl, apiKey, userId, itemId, cancellationToken);
        return lookup.Status == TargetLookupStatus.Success ? lookup.Value : null;
    }

    public async Task<TargetPlaylistLookup<JellyfinMediaItem>> GetItemResult(
        string serverUrl,
        string apiKey,
        string userId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(itemId))
        {
            return TargetPlaylistLookup<JellyfinMediaItem>.Missing();
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BuildUrl(
                    serverUrl,
                    $"/Users/{Uri.EscapeDataString(userId)}/Items/{Uri.EscapeDataString(itemId)}"));
            request.Headers.Add(EmbyTokenHeader, apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var classified = TargetLookupClassifier.FromHttpStatus(response.StatusCode);
            if (classified != TargetLookupStatus.Success)
            {
                return new TargetPlaylistLookup<JellyfinMediaItem>(classified, null, statusCode);
            }

            var item = await response.Content.ReadFromJsonAsync<JellyfinMediaItem>(cancellationToken: cancellationToken);
            return string.IsNullOrWhiteSpace(item?.Id)
                ? TargetPlaylistLookup<JellyfinMediaItem>.Missing(statusCode)
                : TargetPlaylistLookup<JellyfinMediaItem>.Found(item, statusCode);
        }
        catch (Exception ex) when (TargetLookupClassifier.IsTransientTransport(ex, cancellationToken))
        {
            return TargetPlaylistLookup<JellyfinMediaItem>.Unavailable();
        }
    }

    public const int MaxPlaylistMovesPerJob = 20;

    public static int ClampPlaylistMoveIndex(int newIndex, int count)
        => count <= 0 ? 0 : Math.Clamp(newIndex, 0, count - 1);

    public async Task<JellyfinPlaylistMoveResult> MovePlaylistItemAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string playlistId,
        string playlistEntryId,
        int newIndex,
        int itemCount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(playlistId)
            || string.IsNullOrWhiteSpace(playlistEntryId)
            || itemCount <= 0)
        {
            return new JellyfinPlaylistMoveResult(JellyfinPlaylistMoveStatus.NotSupported, null);
        }

        var clampedIndex = ClampPlaylistMoveIndex(newIndex, itemCount);
        var query = new StringBuilder();
        query.Append($"/Playlists/{Uri.EscapeDataString(playlistId)}/Items/{Uri.EscapeDataString(playlistEntryId)}/Move/{clampedIndex}");
        if (!string.IsNullOrWhiteSpace(userId))
        {
            query.Append($"?UserId={Uri.EscapeDataString(userId)}");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(serverUrl, query.ToString()));
            request.Headers.Add(EmbyTokenHeader, apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new JellyfinPlaylistMoveResult(JellyfinPlaylistMoveStatus.Moved, statusCode);
            }

            if (statusCode is 408 or 429 or >= 502)
            {
                return new JellyfinPlaylistMoveResult(JellyfinPlaylistMoveStatus.Transient, statusCode);
            }

            return new JellyfinPlaylistMoveResult(JellyfinPlaylistMoveStatus.NotSupported, statusCode);
        }
        catch (Exception ex) when (TargetLookupClassifier.IsTransientTransport(ex, cancellationToken))
        {
            return new JellyfinPlaylistMoveResult(JellyfinPlaylistMoveStatus.Transient, null);
        }
    }

    public async Task<JellyfinPlaylistMoveResult> ReorderPlaylistItemsAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyList<string> intendedItemIds,
        IReadOnlyList<JellyfinPlaylistEntry> currentEntries,
        CancellationToken cancellationToken = default)
    {
        var current = currentEntries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.ItemId) && !string.IsNullOrWhiteSpace(entry.PlaylistEntryId))
            .ToList();
        var desired = MapIntendedPlaylistEntries(intendedItemIds, current);
        if (desired.Count == 0 || desired.Count != intendedItemIds.Count(static id => !string.IsNullOrWhiteSpace(id)))
        {
            return new JellyfinPlaylistMoveResult(JellyfinPlaylistMoveStatus.Moved, null);
        }

        var moves = 0;
        for (var targetIndex = desired.Count - 1; targetIndex >= 0 && moves < MaxPlaylistMovesPerJob; targetIndex--)
        {
            var entryId = desired[targetIndex].PlaylistEntryId;
            var currentIndex = current.FindIndex(entry =>
                string.Equals(entry.PlaylistEntryId, entryId, StringComparison.OrdinalIgnoreCase));
            if (currentIndex < 0 || currentIndex == targetIndex)
            {
                continue;
            }

            var newIndex = ClampPlaylistMoveIndex(targetIndex, current.Count);
            var moved = await MovePlaylistItemAsync(
                serverUrl,
                apiKey,
                userId,
                playlistId,
                entryId,
                newIndex,
                current.Count,
                cancellationToken);
            if (moved.Status != JellyfinPlaylistMoveStatus.Moved)
            {
                return moved;
            }

            var item = current[currentIndex];
            current.RemoveAt(currentIndex);
            current.Insert(Math.Min(newIndex, current.Count), item);
            moves++;
        }

        return new JellyfinPlaylistMoveResult(JellyfinPlaylistMoveStatus.Moved, 204);
    }

    internal static IReadOnlyList<JellyfinPlaylistEntry> MapIntendedPlaylistEntries(
        IReadOnlyList<string> intendedItemIds,
        IReadOnlyList<JellyfinPlaylistEntry> currentEntries)
    {
        var remaining = currentEntries.ToList();
        var mapped = new List<JellyfinPlaylistEntry>();
        foreach (var itemId in intendedItemIds.Where(static id => !string.IsNullOrWhiteSpace(id)))
        {
            var matchIndex = remaining.FindIndex(entry =>
                string.Equals(entry.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
            if (matchIndex < 0)
            {
                continue;
            }

            mapped.Add(remaining[matchIndex]);
            remaining.RemoveAt(matchIndex);
        }

        return mapped;
    }

    private async Task<bool> UpdateItemPrimaryImageAsync(
        string serverUrl,
        string apiKey,
        string itemId,
        byte[] imageBytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (imageBytes.Length == 0)
        {
            return false;
        }

        using var uploadContent = new StringContent(
            Convert.ToBase64String(imageBytes),
            Encoding.UTF8,
            string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType);
        using var uploadRequest = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUrl(serverUrl, $"/Items/{Uri.EscapeDataString(itemId)}/Images/Primary"))
        {
            Content = uploadContent
        };
        uploadRequest.Headers.Add(EmbyTokenHeader, apiKey);
        using var uploadResponse = await _httpClient.SendAsync(uploadRequest, cancellationToken);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            return false;
        }

        using var verifyRequest = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUrl(
                serverUrl,
                $"/Items/{Uri.EscapeDataString(itemId)}/Images/Primary?tag={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));
        verifyRequest.Headers.Add(EmbyTokenHeader, apiKey);
        using var verifyResponse = await _httpClient.SendAsync(
            verifyRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!verifyResponse.IsSuccessStatusCode)
        {
            return false;
        }

        var storedBytes = await verifyResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(imageBytes),
            SHA256.HashData(storedBytes));
    }

    private async Task<bool> UploadImageAsync(string url, string apiKey, string imagePath, CancellationToken cancellationToken)
    {
        var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        if (imageBytes.Length == 0)
        {
            return false;
        }

        using var content = new StringContent(
            Convert.ToBase64String(imageBytes),
            Encoding.UTF8,
            GetImageContentType(imagePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        request.Headers.Add(EmbyTokenHeader, apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private async Task<List<JellyfinMediaItem>> GetUserChildItemsAsync(
        string serverUrl,
        string apiKey,
        string userId,
        string parentId,
        string itemType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(parentId)
            || string.IsNullOrWhiteSpace(itemType))
        {
            return new List<JellyfinMediaItem>();
        }

        var query = new StringBuilder();
        query.Append($"/Users/{Uri.EscapeDataString(userId)}/Items");
        query.Append($"?ParentId={Uri.EscapeDataString(parentId)}");
        query.Append("&Recursive=false");
        query.Append("&SortBy=SortName");
        query.Append("&SortOrder=Ascending");
        query.Append($"&IncludeItemTypes={Uri.EscapeDataString(itemType)}");
        query.Append("&Fields=IndexNumber,ParentIndexNumber,ProductionYear,ImageTags");
        return await SendItemsRequestAsync(serverUrl, apiKey, query.ToString(), cancellationToken);
    }

    private async Task<List<JellyfinMediaItem>> SendItemsRequestAsync(
        string serverUrl,
        string apiKey,
        string queryPath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, queryPath));
        request.Headers.Add(EmbyTokenHeader, apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<JellyfinMediaItem>();
        }

        var payload = await response.Content.ReadFromJsonAsync<JellyfinItemsResponse>(cancellationToken: cancellationToken);
        return payload?.Items ?? new List<JellyfinMediaItem>();
    }

    private static string BuildUrl(string baseUrl, string path)
    {
        return $"{baseUrl.TrimEnd('/')}{path}";
    }

    private static string ResolveArtistText(JellyfinMediaItem item)
    {
        if (item.AlbumArtists is { Count: > 0 })
        {
            return string.Join(", ", item.AlbumArtists
                .Select(static value => value.Name)
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        }

        if (item.Artists is { Count: > 0 })
        {
            return string.Join(", ", item.Artists.Where(static value => !string.IsNullOrWhiteSpace(value)));
        }

        return string.Empty;
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

    private static string GetImageContentTypeFromUrl(string imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            return "image/jpeg";
        }

        return GetImageContentType(uri.LocalPath);
    }

    private static bool CanUploadArtistAsset(string serverUrl, string apiKey, string artistId, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artistId))
        {
            return false;
        }

        return File.Exists(imagePath);
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

    [JsonPropertyName("Name")]
    public string? Name { get; set; }
}

public sealed class JellyfinLibrarySection
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("CollectionType")]
    public string? CollectionType { get; set; }

    [JsonPropertyName("ItemId")]
    public string? Id { get; set; }

    [JsonPropertyName("Guid")]
    public string? Guid { get; set; }

    public string? LibraryId => string.IsNullOrWhiteSpace(Id) ? Guid : Id;
}

public sealed class JellyfinItemsResponse
{
    [JsonPropertyName("Items")]
    public List<JellyfinMediaItem>? Items { get; set; }
}

public sealed class JellyfinMediaItem
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("ProductionYear")]
    public int? ProductionYear { get; set; }

    [JsonPropertyName("IndexNumber")]
    public int? IndexNumber { get; set; }

    [JsonPropertyName("ParentIndexNumber")]
    public int? ParentIndexNumber { get; set; }

    [JsonPropertyName("ImageTags")]
    public Dictionary<string, string>? ImageTags { get; set; }

    [JsonPropertyName("BackdropImageTags")]
    public List<string>? BackdropImageTags { get; set; }

    [JsonPropertyName("RunTimeTicks")]
    public long? RunTimeTicks { get; set; }

    [JsonPropertyName("Artists")]
    public List<string>? Artists { get; set; }

    [JsonPropertyName("AlbumArtists")]
    public List<JellyfinNamedItem>? AlbumArtists { get; set; }

    [JsonPropertyName("PlaylistItemId")]
    public string? PlaylistItemId { get; set; }

    [JsonPropertyName("Album")]
    public string? Album { get; set; }

    [JsonPropertyName("Path")]
    public string? Path { get; set; }

    [JsonPropertyName("UserData")]
    public JellyfinUserData? UserData { get; set; }
}

public sealed class JellyfinNamedItem
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Id")]
    public string? Id { get; set; }
}

public sealed class JellyfinUserData
{
    [JsonPropertyName("LastPlayedDate")]
    public DateTimeOffset? LastPlayedDate { get; set; }
}

public sealed record JellyfinAudioTrack(
    string Id,
    string Name,
    string Artist,
    string? Album,
    int? DurationMs,
    string? FilePath = null);

public sealed record JellyfinPlaylistEntry(
    string ItemId,
    string PlaylistEntryId);

public enum JellyfinPlaylistMoveStatus
{
    Moved,
    NotSupported,
    Transient
}

public sealed record JellyfinPlaylistMoveResult(
    JellyfinPlaylistMoveStatus Status,
    int? HttpStatusCode);

public sealed record JellyfinHistoryItem(
    string ItemId,
    string Title,
    string Artist,
    string Album,
    string? FilePath,
    DateTimeOffset PlayedAtUtc,
    int? DurationMs);
