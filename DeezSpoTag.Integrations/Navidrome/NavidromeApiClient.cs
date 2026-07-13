using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Integrations.Navidrome;

public sealed class NavidromeApiClient
{
    private const string ClientName = "DeezSpoTag";
    private const string ApiVersion = "1.16.1";
    private const string NativeAuthorizationHeader = "X-ND-Authorization";
    private const int DefaultHistoryPageSize = 500;
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

    public async Task<List<NavidromeLibrary>> GetLibrariesAsync(
        string serverUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<NavidromeMusicFoldersResponse>(
            serverUrl,
            username,
            password,
            "getMusicFolders",
            Array.Empty<KeyValuePair<string, string?>>(),
            cancellationToken);
        return response?.SubsonicResponse?.MusicFolders?.Folders?
            .Where(static folder => folder.Id.HasValue)
            .Select(static folder => new NavidromeLibrary(
                folder.Id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(folder.Name)
                    ? folder.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : folder.Name!))
            .ToList() ?? new List<NavidromeLibrary>();
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
                song.Duration.HasValue ? song.Duration.Value * 1000 : null,
                song.Path,
                song.MusicFolderId?.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList() ?? new List<NavidromeAudioTrack>();
    }

    public Task<List<NavidromeHistoryItem>> GetPlayHistoryAsync(
        string serverUrl,
        string username,
        string password,
        DateTimeOffset? playedSinceUtc = null,
        int pageSize = DefaultHistoryPageSize,
        CancellationToken cancellationToken = default)
        => GetPlayHistoryInternalAsync(
            serverUrl,
            username,
            password,
            libraryId: null,
            playedSinceUtc,
            pageSize,
            cancellationToken);

    public Task<List<NavidromeHistoryItem>> GetPlayHistoryAsync(
        string serverUrl,
        string username,
        string password,
        string libraryId,
        DateTimeOffset? playedSinceUtc = null,
        int pageSize = DefaultHistoryPageSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            return Task.FromResult(new List<NavidromeHistoryItem>());
        }

        return GetPlayHistoryInternalAsync(
            serverUrl,
            username,
            password,
            libraryId.Trim(),
            playedSinceUtc,
            pageSize,
            cancellationToken);
    }

    private async Task<List<NavidromeHistoryItem>> GetPlayHistoryInternalAsync(
        string serverUrl,
        string username,
        string password,
        string? libraryId,
        DateTimeOffset? playedSinceUtc,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password))
        {
            return new List<NavidromeHistoryItem>();
        }

        var token = await LoginNativeApiAsync(serverUrl, username, password, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new List<NavidromeHistoryItem>();
        }

        var normalizedPageSize = Math.Clamp(pageSize, 1, 5000);
        var normalizedSinceUtc = playedSinceUtc?.ToUniversalTime();
        var history = new List<NavidromeHistoryItem>();
        for (var offset = 0; ; offset += normalizedPageSize)
        {
            var page = await GetNativeSongHistoryPageAsync(
                serverUrl,
                token,
                offset,
                normalizedPageSize,
                libraryId,
                cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            var reachedImportBoundary = false;
            foreach (var song in page)
            {
                if (!song.PlayDate.HasValue)
                {
                    reachedImportBoundary = true;
                    continue;
                }

                var playedAtUtc = song.PlayDate.Value.ToUniversalTime();
                if (normalizedSinceUtc.HasValue && playedAtUtc < normalizedSinceUtc.Value)
                {
                    reachedImportBoundary = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(song.Id))
                {
                    continue;
                }

                history.Add(new NavidromeHistoryItem(
                    song.Id,
                    song.Title ?? string.Empty,
                    song.Artist ?? string.Empty,
                    song.Duration.HasValue ? (int?)Math.Round(song.Duration.Value * 1000d) : null,
                    ResolveNativeSongPath(song.LibraryPath, song.Path),
                    playedAtUtc,
                    song.PlayCount,
                    song.LibraryId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? libraryId));
            }

            if (reachedImportBoundary || page.Count < normalizedPageSize)
            {
                break;
            }
        }

        return history;
    }

    public async Task<List<NavidromeArtistSummary>> SearchArtistsAsync(
        string serverUrl,
        string username,
        string password,
        string searchTerm,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return new List<NavidromeArtistSummary>();
        }

        var response = await SendAsync<NavidromeSearchResponse>(
            serverUrl,
            username,
            password,
            "search3",
            new[]
            {
                new KeyValuePair<string, string?>("query", searchTerm),
                new KeyValuePair<string, string?>("songCount", "0"),
                new KeyValuePair<string, string?>("albumCount", "0"),
                new KeyValuePair<string, string?>("artistCount", "25")
            },
            cancellationToken);
        return response?.SubsonicResponse?.SearchResult?.Artists?
            .Where(static artist => !string.IsNullOrWhiteSpace(artist.Id))
            .Select(static artist => new NavidromeArtistSummary(
                artist.Id!,
                artist.Name ?? artist.Id!,
                artist.CoverArt))
            .ToList() ?? new List<NavidromeArtistSummary>();
    }

    public async Task<IReadOnlyList<string>> FindArtistIdsAsync(
        string serverUrl,
        string username,
        string password,
        string artistName,
        CancellationToken cancellationToken = default)
    {
        var normalizedArtistName = artistName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedArtistName))
        {
            return Array.Empty<string>();
        }

        var artists = await SearchArtistsAsync(serverUrl, username, password, normalizedArtistName, cancellationToken);
        var exactMatches = artists
            .Where(artist => string.Equals(artist.Name.Trim(), normalizedArtistName, StringComparison.OrdinalIgnoreCase))
            .Select(artist => artist.Id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (exactMatches.Count > 0)
        {
            return exactMatches;
        }

        return artists
            .Select(artist => artist.Id.Trim())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<NavidromeArtistInfo?> GetArtistInfoAsync(
        string serverUrl,
        string username,
        string password,
        string artistId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return null;
        }

        var response = await SendAsync<NavidromeArtistInfoResponse>(
            serverUrl,
            username,
            password,
            "getArtistInfo2",
            new[]
            {
                new KeyValuePair<string, string?>("id", artistId),
                new KeyValuePair<string, string?>("count", "0"),
                new KeyValuePair<string, string?>("includeNotPresent", "false")
            },
            cancellationToken);
        var info = response?.SubsonicResponse?.ArtistInfo;
        return info is null
            ? null
            : new NavidromeArtistInfo(
                info.Biography,
                info.SmallImageUrl,
                info.MediumImageUrl,
                info.LargeImageUrl,
                info.LastFmUrl,
                info.MusicBrainzId);
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
                playlist.SongCount,
                playlist.Comment))
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
        CancellationToken cancellationToken = default,
        string? playlistComment = null)
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
            if (create?.SubsonicResponse?.Status is not "ok")
            {
                return null;
            }

            var createdPlaylistId = !string.IsNullOrWhiteSpace(create.SubsonicResponse.Playlist?.Id)
                ? create.SubsonicResponse.Playlist.Id
                : await FindPlaylistIdByNameAsync(serverUrl, username, password, playlistName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(createdPlaylistId)
                && !string.IsNullOrWhiteSpace(playlistComment))
            {
                await UpdatePlaylistMetadataAsync(
                    serverUrl,
                    username,
                    password,
                    createdPlaylistId,
                    playlistName,
                    playlistComment,
                    cancellationToken);
            }

            return createdPlaylistId;
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
        }

        var update = await SendAsync<NavidromePlaylistUpdateResponse>(
            serverUrl,
            username,
            password,
            appendMissingOnly ? "updatePlaylist" : "createPlaylist",
            appendMissingOnly
                ? BuildPlaylistUpdateParameters(playlistId, playlistName, targetIds, playlistComment)
                : BuildPlaylistParameters(playlistId, playlistName, targetIds, playlistComment),
            cancellationToken);
        return update?.SubsonicResponse?.Status is "ok" ? playlistId : null;
    }

    public async Task<bool> UpdatePlaylistMetadataAsync(
        string serverUrl,
        string username,
        string password,
        string playlistId,
        string playlistName,
        string? playlistComment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return false;
        }

        var update = await SendAsync<NavidromePlaylistUpdateResponse>(
            serverUrl,
            username,
            password,
            "updatePlaylist",
            BuildPlaylistUpdateParameters(playlistId, playlistName, Array.Empty<string>(), playlistComment),
            cancellationToken);
        return update?.SubsonicResponse?.Status is "ok";
    }

    public async Task<bool> UpdatePlaylistImageFromFileAsync(
        string serverUrl,
        string username,
        string password,
        string playlistId,
        string imagePath,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return false;
        }

        return await UploadNativeImageAsync(
            serverUrl,
            username,
            password,
            $"/api/playlist/{Uri.EscapeDataString(playlistId)}/image",
            imagePath,
            contentType,
            cancellationToken);
    }

    public async Task<bool> UpdateArtistImageFromFileAsync(
        string serverUrl,
        string username,
        string password,
        string artistId,
        string imagePath,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return false;
        }

        return await UploadNativeImageAsync(
            serverUrl,
            username,
            password,
            $"/api/artist/{Uri.EscapeDataString(artistId)}/image",
            imagePath,
            contentType,
            cancellationToken);
    }

    private async Task<bool> UploadNativeImageAsync(
        string serverUrl,
        string username,
        string password,
        string path,
        string imagePath,
        string? contentType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password)
            || string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(imagePath)
            || !File.Exists(imagePath))
        {
            return false;
        }

        var token = await LoginNativeApiAsync(serverUrl, username, password, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            await using var imageStream = File.OpenRead(imagePath);
            using var imageContent = new StreamContent(imageStream);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? ResolveImageContentType(imagePath) : contentType);
            using var multipart = new MultipartFormDataContent();
            multipart.Add(imageContent, "image", Path.GetFileName(imagePath));

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildNativeUrl(serverUrl, path))
            {
                Content = multipart
            };
            request.Headers.TryAddWithoutValidation(NativeAuthorizationHeader, $"Bearer {token}");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async Task<List<NavidromePlaylistEntry>> GetPlaylistEntriesAsync(
        string serverUrl,
        string username,
        string password,
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        var playlist = await GetPlaylistAsync(serverUrl, username, password, playlistId, cancellationToken);
        return playlist?.Entries?
            .Select(static entry => new NavidromePlaylistEntry(entry.ItemId))
            .ToList() ?? new List<NavidromePlaylistEntry>();
    }

    public async Task<NavidromePlaylistDetails?> GetPlaylistAsync(
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
        var playlist = response?.SubsonicResponse?.Playlist;
        if (string.IsNullOrWhiteSpace(playlist?.Id))
        {
            return null;
        }

        var entries = playlist.Entries?
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Id))
            .Select(static entry => new NavidromePlaylistEntry(entry.Id!))
            .ToList() ?? new List<NavidromePlaylistEntry>();
        return new NavidromePlaylistDetails(
            playlist.Id!,
            playlist.Name ?? playlist.Id!,
            playlist.Comment,
            playlist.SongCount,
            entries);
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
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(requestUrl, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (HttpRequestException)
        {
            return default;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return default;
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                return default;
            }
        }
    }

    private async Task<string?> LoginNativeApiAsync(
        string serverUrl,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                BuildNativeUrl(serverUrl, "/auth/login"),
                new { username, password },
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var login = await response.Content.ReadFromJsonAsync<NavidromeNativeLoginResponse>(cancellationToken: cancellationToken);
            return login?.Token;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<List<NavidromeNativeSong>> GetNativeSongHistoryPageAsync(
        string serverUrl,
        string token,
        int offset,
        int pageSize,
        string? libraryId,
        CancellationToken cancellationToken)
    {
        var queryParts = new List<string>
        {
            $"_start={offset}",
            $"_end={offset + pageSize}",
            "_sort=playDate",
            "_order=DESC"
        };
        if (!string.IsNullOrWhiteSpace(libraryId))
        {
            queryParts.Add($"library_id={Uri.EscapeDataString(libraryId)}");
        }

        var query = string.Join("&", queryParts);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BuildNativeUrl(serverUrl, "/api/song")}?{query}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(NativeAuthorizationHeader, $"Bearer {token}");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new List<NavidromeNativeSong>();
            }

            return await response.Content.ReadFromJsonAsync<List<NavidromeNativeSong>>(
                cancellationToken: cancellationToken) ?? new List<NavidromeNativeSong>();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new List<NavidromeNativeSong>();
        }
        catch (HttpRequestException)
        {
            return new List<NavidromeNativeSong>();
        }
        catch (JsonException)
        {
            return new List<NavidromeNativeSong>();
        }
    }

    private static string? ResolveNativeSongPath(string? libraryPath, string? songPath)
    {
        if (string.IsNullOrWhiteSpace(songPath))
        {
            return null;
        }

        var normalizedSongPath = songPath.Trim();
        if (string.IsNullOrWhiteSpace(libraryPath)
            || normalizedSongPath.StartsWith("/", StringComparison.Ordinal)
            || normalizedSongPath.StartsWith('\\')
            || (normalizedSongPath.Length > 2 && char.IsLetter(normalizedSongPath[0]) && normalizedSongPath[1] == ':'))
        {
            return normalizedSongPath;
        }

        return $"{libraryPath.Trim().TrimEnd('/', '\\')}/{normalizedSongPath.TrimStart('/', '\\')}";
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

    private static string BuildNativeUrl(string serverUrl, string path)
        => $"{serverUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private static List<KeyValuePair<string, string?>> BuildPlaylistParameters(
        string? playlistId,
        string playlistName,
        IReadOnlyCollection<string> songIds,
        string? playlistComment = null)
    {
        var parameters = new List<KeyValuePair<string, string?>>();
        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            parameters.Add(new KeyValuePair<string, string?>("playlistId", playlistId));
        }
        parameters.Add(new KeyValuePair<string, string?>("name", playlistName));
        parameters.Add(new KeyValuePair<string, string?>("comment", playlistComment));
        parameters.AddRange(songIds.Select(static id => new KeyValuePair<string, string?>("songId", id)));
        return parameters;
    }

    private static List<KeyValuePair<string, string?>> BuildPlaylistUpdateParameters(
        string playlistId,
        string playlistName,
        IReadOnlyCollection<string> songIdsToAdd,
        string? playlistComment = null)
    {
        var parameters = new List<KeyValuePair<string, string?>>
        {
            new("playlistId", playlistId),
            new("name", playlistName),
            new("comment", playlistComment)
        };
        parameters.AddRange(songIdsToAdd.Select(static id => new KeyValuePair<string, string?>("songIdToAdd", id)));
        return parameters;
    }

    private static string ResolveImageContentType(string imagePath)
    {
        return Path.GetExtension(imagePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };
    }

    private static string ToMd5Hex(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record NavidromeSystemInfo(string? Version, string ServerName);
public sealed record NavidromeLibrary(string Id, string Name);
public sealed record NavidromeAudioTrack(
    string Id,
    string Title,
    string Artist,
    int? DurationMs,
    string? FilePath = null,
    string? LibraryId = null);
public sealed record NavidromeHistoryItem(
    string ItemId,
    string Title,
    string Artist,
    int? DurationMs,
    string? FilePath,
    DateTimeOffset PlayedAtUtc,
    long PlayCount,
    string? LibraryId = null);
public sealed record NavidromeArtistSummary(string Id, string Name, string? CoverArt = null);
public sealed record NavidromeArtistInfo(
    string? Biography,
    string? SmallImageUrl,
    string? MediumImageUrl,
    string? LargeImageUrl,
    string? LastFmUrl,
    string? MusicBrainzId);
public sealed record NavidromePlaylistSummary(string Id, string Name, int? TrackCount, string? Comment = null);
public sealed record NavidromePlaylistDetails(
    string Id,
    string Name,
    string? Comment,
    int? TrackCount,
    IReadOnlyList<NavidromePlaylistEntry> Entries);
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

file sealed class NavidromeMusicFoldersResponse
{
    [JsonPropertyName("subsonic-response")]
    public NavidromeMusicFoldersSubsonicResponse? SubsonicResponse { get; set; }
}

file sealed class NavidromeMusicFoldersSubsonicResponse : NavidromeBaseResponse
{
    [JsonPropertyName("musicFolders")]
    public NavidromeMusicFoldersContainer? MusicFolders { get; set; }
}

file sealed class NavidromeMusicFoldersContainer
{
    [JsonPropertyName("musicFolder")]
    public List<NavidromeMusicFolder>? Folders { get; set; }
}

file sealed class NavidromeMusicFolder
{
    [JsonPropertyName("id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

file sealed class NavidromeNativeLoginResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

internal sealed class NavidromeNativeSong
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("artist")]
    public string? Artist { get; set; }
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }
    [JsonPropertyName("path")]
    public string? Path { get; set; }
    [JsonPropertyName("libraryPath")]
    public string? LibraryPath { get; set; }
    [JsonPropertyName("libraryId")]
    public int? LibraryId { get; set; }
    [JsonPropertyName("playDate")]
    public DateTimeOffset? PlayDate { get; set; }
    [JsonPropertyName("playCount")]
    public long PlayCount { get; set; }
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
    [JsonPropertyName("artist")]
    public List<NavidromeArtist>? Artists { get; set; }
}

file sealed class NavidromeArtistInfoResponse
{
    [JsonPropertyName("subsonic-response")]
    public NavidromeArtistInfoSubsonicResponse? SubsonicResponse { get; set; }
}

file sealed class NavidromeArtistInfoSubsonicResponse : NavidromeBaseResponse
{
    [JsonPropertyName("artistInfo2")]
    public NavidromeArtistInfoDto? ArtistInfo { get; set; }
}

file sealed class NavidromeArtistInfoDto
{
    [JsonPropertyName("biography")]
    public string? Biography { get; set; }
    [JsonPropertyName("smallImageUrl")]
    public string? SmallImageUrl { get; set; }
    [JsonPropertyName("mediumImageUrl")]
    public string? MediumImageUrl { get; set; }
    [JsonPropertyName("largeImageUrl")]
    public string? LargeImageUrl { get; set; }
    [JsonPropertyName("lastFmUrl")]
    public string? LastFmUrl { get; set; }
    [JsonPropertyName("musicBrainzId")]
    public string? MusicBrainzId { get; set; }
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
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
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
    [JsonPropertyName("path")]
    public string? Path { get; set; }
    [JsonPropertyName("musicFolderId")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? MusicFolderId { get; set; }
}

file sealed class NavidromeArtist
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("coverArt")]
    public string? CoverArt { get; set; }
}
