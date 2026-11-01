using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Navidrome;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class NavidromeApiClientPlaylistTest
{
    [Fact]
    public async Task SubsonicReadsReuseNativeJwtAndNeverSendPasswordOrMd5Parameters()
    {
        using var handler = new JwtAuthenticationHandler();
        using var http = new HttpClient(handler);
        var client = new NavidromeApiClient(http);
        Assert.NotNull(await client.PingAsync("http://navidrome.local", "user", "secret"));
        Assert.Equal(TargetLookupStatus.Success, (await client.GetPlaylistsResult("http://navidrome.local", "user", "secret")).Status);
        Assert.Equal(TargetLookupStatus.Success, (await client.GetPlaylistResult("http://navidrome.local", "user", "secret", "playlist-1")).Status);
        Assert.Equal(new[] { "Album" }, await client.GetArtistAlbumTitlesAsync("http://navidrome.local", "user", "secret", "artist-1"));
        var cover = await client.BuildCoverArtRequestUrlAsync("http://navidrome.local", "user", "secret", "cover-1", size: 9999);
        Assert.Equal(handler.Token, GetQueryValues(cover!, "jwt").Single());
        Assert.Equal("2000", GetQueryValues(cover!, "size").Single());
        Assert.Equal(1, handler.LoginCount);
        Assert.Equal(4, handler.SubsonicRequests.Count);
        Assert.All(handler.SubsonicRequests, url =>
        {
            Assert.Equal(handler.Token, GetQueryValues(url, "jwt").Single());
            Assert.Equal("user", GetQueryValues(url, "u").Single());
            Assert.Empty(GetQueryValues(url, "p"));
            Assert.Empty(GetQueryValues(url, "t"));
            Assert.Empty(GetQueryValues(url, "s"));
            Assert.DoesNotContain("secret", url, StringComparison.Ordinal);
        });
        using var body = System.Text.Json.JsonDocument.Parse(Assert.Single(handler.LoginBodies));
        Assert.Equal("user", body.RootElement.GetProperty("username").GetString());
        Assert.Equal("secret", body.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task ConcurrentSubsonicRequestsShareOneJwtLogin()
    {
        using var handler = new JwtAuthenticationHandler { HoldLogin = true };
        using var http = new HttpClient(handler);
        var client = new NavidromeApiClient(http);
        var first = client.PingAsync("http://navidrome.local", "user", "secret");
        await handler.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = Enumerable.Range(0, 20).Select(_ => client.PingAsync("http://navidrome.local", "user", "secret")).ToArray();
        handler.ReleaseLogin.TrySetResult();
        Assert.NotNull(await first);
        Assert.All(await Task.WhenAll(pending), result => Assert.NotNull(result));
        Assert.Equal(1, handler.LoginCount);
        Assert.Equal(21, handler.SubsonicRequests.Count);
    }

    [Theory]
    [InlineData(120, 100)]
    [InlineData(3600, 301)]
    public async Task JwtIsRenewedBeforeExpiryAndHasBoundedCacheLifetime(int validitySeconds, int elapsedSeconds)
    {
        var clock = new JwtTestClock();
        using var handler = new JwtAuthenticationHandler { TokenFactory = index => CreateJwt(clock.GetUtcNow().AddSeconds(validitySeconds), index) };
        using var http = new HttpClient(handler);
        var client = new NavidromeApiClient(http, timeProvider: clock);
        Assert.NotNull(await client.PingAsync("http://navidrome.local", "user", "secret"));
        var firstToken = handler.Token;
        clock.Now = clock.Now.AddSeconds(elapsedSeconds);
        var cover = await client.BuildCoverArtRequestUrlAsync("http://navidrome.local", "user", "secret", "cover-1");
        Assert.Equal(2, handler.LoginCount);
        Assert.NotEqual(firstToken, handler.Token);
        Assert.Equal(handler.Token, GetQueryValues(cover!, "jwt").Single());
        Assert.NotNull(await client.PingAsync("http://navidrome.local", "user", "secret"));
        Assert.Equal(2, handler.LoginCount);
    }

    [Fact]
    public async Task ChangedCredentialsOrServerCannotReuseAnotherJwt()
    {
        using var handler = new JwtAuthenticationHandler();
        using var http = new HttpClient(handler);
        var client = new NavidromeApiClient(http);
        foreach (var (server, user, password) in new[]
                 { ("http://one.local", "user", "first"), ("http://one.local", "user", "second"),
                   ("http://one.local", "other", "second"), ("http://two.local", "other", "second") })
            Assert.NotNull(await client.PingAsync(server, user, password));
        Assert.Equal(4, handler.LoginCount);
        Assert.Equal(4, handler.SubsonicRequests.Select(url => GetQueryValues(url, "jwt").Single()).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitJwtRejectionRenewsOnce(bool applicationError)
    {
        using var handler = new JwtAuthenticationHandler
        {
            Respond = index => index == 1 ? JwtRejection(applicationError) : JwtSuccess()
        };
        using var http = new HttpClient(handler);
        var result = await new NavidromeApiClient(http).StartScanAsync("http://navidrome.local", "user", "secret");
        Assert.True(result);
        Assert.Equal(2, handler.LoginCount);
        Assert.Equal(2, handler.SubsonicRequests.Count);
        Assert.NotEqual(GetQueryValues(handler.SubsonicRequests[0], "jwt").Single(),
            GetQueryValues(handler.SubsonicRequests[1], "jwt").Single());
    }

    [Fact]
    public async Task RepeatedJwtRejectionDoesNotLoop()
    {
        using var handler = new JwtAuthenticationHandler { Respond = _ => JwtRejection(true) };
        using var http = new HttpClient(handler);
        Assert.Null(await new NavidromeApiClient(http).PingAsync("http://navidrome.local", "user", "secret"));
        Assert.Equal(2, handler.LoginCount);
        Assert.Equal(2, handler.SubsonicRequests.Count);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(0)]
    public async Task AmbiguousWriteFailuresAndPermissionDenialsAreNeverReplayed(int status)
    {
        using var handler = new JwtAuthenticationHandler
        {
            Respond = _ => status == 0 ? throw new HttpRequestException("Connection lost after sending")
                : new HttpResponseMessage((HttpStatusCode)status)
        };
        using var http = new HttpClient(handler);
        Assert.False(await new NavidromeApiClient(http).StartScanAsync("http://navidrome.local", "user", "secret"));
        Assert.Equal(1, handler.LoginCount);
        Assert.Single(handler.SubsonicRequests, url => new Uri(url).AbsolutePath == "/rest/startScan.view");
        Assert.Single(handler.SubsonicRequests, url => new Uri(url).AbsolutePath == "/rest/getScanStatus.view");
        Assert.Equal(2, handler.SubsonicRequests.Count);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(503)]
    public async Task FailedNativeLoginNeverSendsASubsonicRequest(int status)
    {
        using var handler = new JwtAuthenticationHandler { LoginStatus = (HttpStatusCode)status };
        using var http = new HttpClient(handler);
        var client = new NavidromeApiClient(http);
        Assert.Null(await client.PingAsync("http://navidrome.local", "user", "secret"));
        Assert.Equal(TargetLookupStatus.Transient, (await client.GetPlaylistsResult("http://navidrome.local", "user", "secret")).Status);
        await Assert.ThrowsAsync<TargetLookupException>(() => client.GetArtistAlbumTitlesAsync(
            "http://navidrome.local", "user", "secret", "artist", requireSuccessfulResponse: true));
        Assert.Empty(handler.SubsonicRequests);
    }

    [Fact]
    public async Task CancellingAQueuedJwtWaitDoesNotCancelTheSharedLoginOrCorruptItsGate()
    {
        using var handler = new JwtAuthenticationHandler { HoldLogin = true };
        using var http = new HttpClient(handler);
        var client = new NavidromeApiClient(http);
        var first = client.PingAsync("http://navidrome.local", "user", "secret");
        await handler.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var second = client.PingAsync("http://navidrome.local", "user", "secret", cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        handler.ReleaseLogin.TrySetResult();
        Assert.NotNull(await first);
        Assert.NotNull(await client.PingAsync("http://navidrome.local", "user", "secret"));
        Assert.Equal(1, handler.LoginCount);
    }

    [Fact]
    public async Task ExpiredLoginJwtIsRejectedBeforeSendingAnySubsonicRequest()
    {
        using var handler = new JwtAuthenticationHandler { TokenFactory = index => CreateJwt(DateTimeOffset.UtcNow.AddHours(-1), index) };
        using var http = new HttpClient(handler);
        Assert.Null(await new NavidromeApiClient(http).PingAsync("http://navidrome.local", "user", "secret"));
        Assert.Empty(handler.SubsonicRequests);
    }

    private sealed class JwtTestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string CreateJwt(DateTimeOffset expiresAt, int generation)
    {
        static string Encode(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("{\"alg\":\"HS256\"}") + "." + Encode("{\"exp\":" + expiresAt.ToUnixTimeSeconds() + ",\"jti\":" + generation + "}") + ".signature";
    }

    private static HttpResponseMessage JwtSuccess()
        => new(HttpStatusCode.OK) { Content = new StringContent("""{"subsonic-response":{"status":"ok","serverVersion":"0.64.2","type":"navidrome","playlists":{"playlist":[]},"playlist":{"id":"playlist-1","name":"List","entry":[]},"artist":{"album":[{"name":"Album"}]}}}""") };

    private static HttpResponseMessage JwtRejection(bool applicationError)
        => applicationError
            ? new(HttpStatusCode.OK) { Content = new StringContent("""{"subsonic-response":{"status":"failed","error":{"code":40,"message":"Invalid JWT"}}}""") }
            : new(HttpStatusCode.Unauthorized);

    private sealed class JwtAuthenticationHandler : HttpMessageHandler
    {
        public string Token { get; private set; } = string.Empty;
        public int LoginCount;
        public List<string> LoginBodies { get; } = new();
        public List<string> SubsonicRequests { get; } = new();
        public Func<int, string> TokenFactory { get; set; } = index => CreateJwt(DateTimeOffset.UtcNow.AddHours(1), index);
        public Func<int, HttpResponseMessage> Respond { get; set; } = _ => JwtSuccess();
        public HttpStatusCode LoginStatus { get; set; } = HttpStatusCode.OK;
        public bool HoldLogin { get; set; }
        public TaskCompletionSource LoginStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLogin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/auth/login")
            {
                var index = Interlocked.Increment(ref LoginCount);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                lock (LoginBodies) LoginBodies.Add(body);
                LoginStarted.TrySetResult();
                if (HoldLogin) await ReleaseLogin.Task.WaitAsync(cancellationToken);
                Token = TokenFactory(index);
                return new HttpResponseMessage(LoginStatus)
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { token = Token }))
                };
            }
            int count;
            lock (SubsonicRequests)
            {
                SubsonicRequests.Add(request.RequestUri.ToString());
                count = SubsonicRequests.Count;
            }
            return Respond(count);
        }
    }

    [Fact]
    public async Task CreateOrUpdatePlaylistAsync_AppendsWithUpdatePlaylistSongIdToAdd()
    {
        using var handler = new NavidromePlaylistHandler();
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var playlistId = await client.CreateOrUpdatePlaylistAsync(
            new NavidromeTargetConnection("http://navidrome.local", "user", "pass"),
            "Gold School",
            new[] { "song-1", "song-2" },
            existingPlaylistId: "playlist-1",
            appendMissingOnly: true,
            CancellationToken.None,
            "A reliable playlist description");

        Assert.Equal("playlist-1", playlistId);
        var update = Assert.Single(handler.RequestedUrls, url => url.Contains("/rest/updatePlaylist.view?", StringComparison.Ordinal));
        Assert.Contains("playlistId=playlist-1", update, StringComparison.Ordinal);
        Assert.Equal("A reliable playlist description", GetQueryValues(update, "comment").Single());
        Assert.Contains("songIdToAdd=song-2", update, StringComparison.Ordinal);
        Assert.DoesNotContain("songId=song-2", update, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateOrUpdatePlaylistAsync_ResolvesCreatedPlaylistIdWhenCreateReturnsEmptySuccess()
    {
        using var handler = new NavidromePlaylistHandler(createReturnsPlaylist: false, playlistExistsInitially: false);
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var playlistId = await client.CreateOrUpdatePlaylistAsync(
            new NavidromeTargetConnection("http://navidrome.local", "user", "pass"),
            "Gold School",
            new[] { "song-1" },
            existingPlaylistId: null,
            appendMissingOnly: false,
            CancellationToken.None,
            "A reliable playlist description");

        Assert.Equal("playlist-1", playlistId);
        var create = Assert.Single(handler.RequestedUrls, url => url.Contains("/rest/createPlaylist.view?", StringComparison.Ordinal));
        Assert.Equal("Gold School", GetQueryValues(create, "name").Single());
        Assert.Equal("song-1", GetQueryValues(create, "songId").Single());
        var metadataUpdate = Assert.Single(handler.RequestedUrls, url => url.Contains("/rest/updatePlaylist.view?", StringComparison.Ordinal));
        Assert.Equal("A reliable playlist description", GetQueryValues(metadataUpdate, "comment").Single());
    }

    [Fact]
    public async Task CreateOrUpdatePlaylistAsync_BatchesLargeOrderedPlaylistWrites()
    {
        using var handler = new NavidromePlaylistHandler();
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);
        var songIds = Enumerable.Range(1, 250).Select(index => $"track-{index}").ToList();

        var playlistId = await client.CreateOrUpdatePlaylistAsync(
            new NavidromeTargetConnection("http://navidrome.local", "user", "pass"),
            "Gold School",
            songIds,
            existingPlaylistId: "playlist-1",
            appendMissingOnly: false,
            CancellationToken.None);

        Assert.Equal("playlist-1", playlistId);
        Assert.DoesNotContain(
            handler.RequestedUrls,
            url => url.Contains("/rest/createPlaylist.view?", StringComparison.Ordinal));
        var appendRequests = handler.RequestedUrls
            .Where(url => url.Contains("/rest/updatePlaylist.view?", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, appendRequests.Count);
        Assert.Equal(100, GetQueryValues(appendRequests[0], "songIdToAdd").Count);
        Assert.Equal(100, GetQueryValues(appendRequests[1], "songIdToAdd").Count);
        Assert.Equal(50, GetQueryValues(appendRequests[2], "songIdToAdd").Count);
        Assert.Equal("track-1", GetQueryValues(appendRequests[0], "songIdToAdd")[0]);
        Assert.Equal("track-250", GetQueryValues(appendRequests[2], "songIdToAdd")[^1]);
    }

    [Fact]
    public async Task UpdatePlaylistImageFromFileAsync_LogsInAndUploadsMultipartImage()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"navidrome-playlist-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
        try
        {
            using var handler = new NavidromePlaylistHandler();
            using var httpClient = new HttpClient(handler);
            var client = new NavidromeApiClient(httpClient);

            var updated = await client.UpdatePlaylistImageFromFileAsync(
                "http://navidrome.local",
                "user",
                "pass",
                "playlist-1",
                imagePath,
                "image/png",
                CancellationToken.None);

            Assert.True(updated);
            Assert.Contains(handler.RequestedUrls, url => url.EndsWith("/auth/login", StringComparison.Ordinal));
            Assert.Contains(handler.RequestedUrls, url => url.EndsWith("/api/playlist/playlist-1/image", StringComparison.Ordinal));
            Assert.Equal("Bearer jwt-token", handler.UploadAuthorization);
            Assert.Contains("Content-Disposition", handler.UploadBody);
            Assert.Contains("image", handler.UploadBody);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task UpdatePlaylistImageFromFileAsync_AcceptsSuccessfulUploadWithoutUnsupportedGet()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"navidrome-playlist-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
        try
        {
            using var handler = new NavidromePlaylistHandler();
            using var httpClient = new HttpClient(handler);
            var client = new NavidromeApiClient(httpClient);

            var updated = await client.UpdatePlaylistImageFromFileAsync(
                "http://navidrome.local",
                "user",
                "pass",
                "playlist-1",
                imagePath,
                "image/png",
                CancellationToken.None);

            Assert.True(updated);
            Assert.Equal(1, handler.RequestedUrls.Count(url => url.Contains("/api/playlist/playlist-1/image", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task UpdatePlaylistImageFromFileAsync_ReportsFailedUpload()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"navidrome-playlist-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        try
        {
            using var handler = new NavidromePlaylistHandler(imageUploadStatusCode: HttpStatusCode.InternalServerError);
            using var httpClient = new HttpClient(handler);
            var client = new NavidromeApiClient(httpClient);

            var updated = await client.UpdatePlaylistImageFromFileAsync(
                "http://navidrome.local", "user", "pass", "playlist-1", imagePath, "image/png", CancellationToken.None);

            Assert.False(updated);
            Assert.Equal(1, handler.RequestedUrls.Count(url => url.Contains("/api/playlist/playlist-1/image", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task SearchArtistsAsync_ReturnsArtistResults()
    {
        using var handler = new NavidromePlaylistHandler();
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var artists = await client.SearchArtistsAsync(
            "http://navidrome.local",
            "user",
            "pass",
            "Alikiba",
            CancellationToken.None);

        var artist = Assert.Single(artists);
        Assert.Equal("artist-1", artist.Id);
        Assert.Equal("Alikiba", artist.Name);
        Assert.Equal("artist-cover-1", artist.CoverArt);
    }

    [Fact]
    public async Task GetPlaylistsAsync_PreservesCoverArtIdentifier()
    {
        using var handler = new NavidromePlaylistHandler();
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var playlists = await client.GetPlaylistsAsync(
            "http://navidrome.local",
            "user",
            "pass",
            CancellationToken.None);

        var playlist = Assert.Single(playlists);
        var coverArt = playlist.GetType().GetProperty("CoverArt")?.GetValue(playlist) as string;
        Assert.Equal("playlist-cover-1", coverArt);
    }

    [Fact]
    public async Task UpdateArtistImageFromFileAsync_LogsInAndUploadsMultipartImage()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"navidrome-artist-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(imagePath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        try
        {
            using var handler = new NavidromePlaylistHandler();
            using var httpClient = new HttpClient(handler);
            var client = new NavidromeApiClient(httpClient);

            var updated = await client.UpdateArtistImageFromFileAsync(
            new NavidromeTargetConnection("http://navidrome.local", "user", "pass"),
            "artist-1",
            imagePath,
            "image/jpeg",
            CancellationToken.None);

            Assert.True(updated);
            Assert.Contains(handler.RequestedUrls, url => url.EndsWith("/auth/login", StringComparison.Ordinal));
            Assert.Contains(handler.RequestedUrls, url => url.EndsWith("/api/artist/artist-1/image", StringComparison.Ordinal));
            Assert.Equal("Bearer jwt-token", handler.UploadAuthorization);
            Assert.Contains("Content-Disposition", handler.UploadBody);
            Assert.Contains("image", handler.UploadBody);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task GetArtistInfoAsync_ReturnsBiographyAndLargeArtistImage()
    {
        using var handler = new NavidromePlaylistHandler();
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var info = await client.GetArtistInfoAsync(
            "http://navidrome.local",
            "user",
            "pass",
            "artist-1",
            CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("Artist biography", info.Biography);
        Assert.Equal("http://navidrome.local/rest/getCoverArt.view?id=artist-1&size=1200", info.LargeImageUrl);
        Assert.Contains(handler.RequestedUrls, url => url.Contains("/rest/getArtistInfo2.view?", StringComparison.Ordinal)
            && url.Contains("id=artist-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateOrUpdatePlaylistAsync_StoredIdNotFoundAndNameListTransientDoesNotCreate()
    {
        using var handler = new NavidromePlaylistHandler(
            playlistExistsInitially: false,
            getPlaylistStatusCode: HttpStatusCode.NotFound,
            getPlaylistsStatusCode: HttpStatusCode.InternalServerError);
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var playlistId = await client.CreateOrUpdatePlaylistAsync(
            new NavidromeTargetConnection("http://navidrome.local", "user", "pass"),
            "Gold School",
            new[] { "song-1" },
            existingPlaylistId: "gone",
            appendMissingOnly: false,
            CancellationToken.None);

        Assert.Null(playlistId);
        Assert.DoesNotContain(
            handler.RequestedUrls,
            url => url.Contains("/rest/createPlaylist.view?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateOrUpdatePlaylistAsync_StoredIdNotFoundAndEmptyNameListCreates()
    {
        using var handler = new NavidromePlaylistHandler(
            playlistExistsInitially: false,
            getPlaylistStatusCode: HttpStatusCode.NotFound);
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var playlistId = await client.CreateOrUpdatePlaylistAsync(
            new NavidromeTargetConnection("http://navidrome.local", "user", "pass"),
            "Gold School",
            new[] { "song-1" },
            existingPlaylistId: "gone",
            appendMissingOnly: false,
            CancellationToken.None,
            "A reliable playlist description");

        Assert.Equal("playlist-1", playlistId);
        Assert.Contains(handler.RequestedUrls, url => url.Contains("/rest/createPlaylist.view?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindPlaylistIdByNameResult_DuplicateNamesDoNotSelectAnArbitraryPlaylist()
    {
        using var handler = new NavidromePlaylistHandler(duplicatePlaylistName: true);
        using var client = new HttpClient(handler);
        var api = new NavidromeApiClient(client);

        var result = await api.FindPlaylistIdByNameResult(
            "http://navidrome.local", "user", "pass", "Gold School");

        Assert.Equal(TargetLookupStatus.Transient, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ReplacePlaylistTracksInOrderAsync_UsesSubsonicRemovalAndAddition()
    {
        using var handler = new NavidromePlaylistHandler(simulatePlaylistOrder: true);
        handler.StoredSongIds.Add("song-2");
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var ordered = await client.ReplacePlaylistTracksInOrderAsync(
            new NavidromeTargetConnection("http://navidrome.local", "user", "pass"),
            "playlist-1",
            "Gold School",
            "A reliable playlist description",
            ["song-2", "song-1"],
            CancellationToken.None);

        Assert.True(ordered);
        Assert.Equal(["song-2", "song-1"], handler.StoredSongIds);
        Assert.Contains(handler.RequestedUrls, url => url.Contains("songIndexToRemove=1", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestedUrls, url => url.EndsWith("/api/playlist/playlist-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplacePlaylistTracksInOrderAsync_FailedUpdateDoesNotReportSuccess()
    {
        using var handler = new NavidromePlaylistHandler(simulatePlaylistOrder: true,
            updatePlaylistStatusCode: HttpStatusCode.InternalServerError);
        handler.StoredSongIds.Add("song-2");
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var ordered = await client.ReplacePlaylistTracksInOrderAsync(
            new NavidromeTargetConnection("http://navidrome.local", "user", "pass"),
            "playlist-1",
            "Gold School",
            null,
            ["song-2", "song-1"],
            CancellationToken.None);

        Assert.False(ordered);
        Assert.Equal(["song-1", "song-2"], handler.StoredSongIds);
    }

    [Fact]
    public async Task ReplacePlaylistTracksInOrderAsync_RejectsAcknowledgedButUnchangedOrder()
    {
        using var handler = new NavidromePlaylistHandler(simulatePlaylistOrder: true, ignorePlaylistUpdates: true);
        handler.StoredSongIds.Add("song-2");
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var ordered = await client.ReplacePlaylistTracksInOrderAsync(
            new NavidromeTargetConnection("http://navidrome.local", "user", "pass"),
            "playlist-1",
            "Gold School",
            null,
            ["song-2", "song-1"],
            CancellationToken.None);

        Assert.False(ordered);
        Assert.Equal(["song-1", "song-2"], handler.StoredSongIds);
    }

    [Fact]
    public async Task ReplacePlaylistTracksInOrderAsync_BatchesLargeReorderWithoutShiftingIndexes()
    {
        using var handler = new NavidromePlaylistHandler(simulatePlaylistOrder: true);
        handler.StoredSongIds.Clear();
        handler.StoredSongIds.AddRange(Enumerable.Range(1, 250).Select(index => $"song-{index}"));
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);
        var desired = handler.StoredSongIds.AsEnumerable().Reverse().ToList();

        var ordered = await client.ReplacePlaylistTracksInOrderAsync(
            new NavidromeTargetConnection("http://navidrome.local", "user", "pass"),
            "playlist-1",
            "Gold School",
            null,
            desired,
            CancellationToken.None);

        Assert.True(ordered);
        Assert.Equal(desired, handler.StoredSongIds);
        Assert.Equal(6, handler.RequestedUrls.Count(url => url.Contains("/rest/updatePlaylist.view?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GetPlaylistAsync_ReturnsDescriptionAndEntries()
    {
        using var handler = new NavidromePlaylistHandler();
        using var httpClient = new HttpClient(handler);
        var client = new NavidromeApiClient(httpClient);

        var playlist = await client.GetPlaylistAsync(
            "http://navidrome.local",
            "user",
            "pass",
            "playlist-1",
            CancellationToken.None);

        Assert.NotNull(playlist);
        Assert.Equal("playlist-1", playlist.Id);
        Assert.Equal("Gold School", playlist.Name);
        Assert.Equal("A reliable playlist description", playlist.Comment);
        Assert.Equal("song-1", Assert.Single(playlist.Entries).ItemId);
    }

    [Fact]
    public async Task SearchTracksAsync_ReturnsEmptyWhenServerReturnsHtml()
    {
        using var httpClient = new HttpClient(new HtmlResponseHandler());
        var client = new NavidromeApiClient(httpClient);

        var results = await client.SearchTracksAsync(
            "http://navidrome.local",
            "user",
            "pass",
            "Gold School",
            CancellationToken.None);

        Assert.Empty(results);
    }

    private sealed class NavidromePlaylistHandler(
        bool createReturnsPlaylist = true,
        bool playlistExistsInitially = true,
        HttpStatusCode getPlaylistStatusCode = HttpStatusCode.OK,
        HttpStatusCode getPlaylistsStatusCode = HttpStatusCode.OK,
        HttpStatusCode loginStatusCode = HttpStatusCode.OK,
        bool duplicatePlaylistName = false,
        bool simulatePlaylistOrder = false,
        HttpStatusCode updatePlaylistStatusCode = HttpStatusCode.OK,
        HttpStatusCode imageUploadStatusCode = HttpStatusCode.OK,
        bool ignorePlaylistUpdates = false) : HttpMessageHandler
    {
        private bool _playlistCreated;
        public List<string> RequestedUrls { get; } = new();
        public List<string> StoredSongIds { get; } = ["song-1"];
        public string UploadAuthorization { get; private set; } = string.Empty;
        public string UploadBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);
            var path = request.RequestUri.AbsolutePath;

            if (path.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                if (loginStatusCode != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(loginStatusCode);
                }

                return await Json("""
                    {
                      "token": "jwt-token"
                    }
                    """);
            }

            if (path.EndsWith("/api/playlist/playlist-1/image", StringComparison.Ordinal))
            {
                return imageUploadStatusCode == HttpStatusCode.OK
                    ? await CaptureImageUploadAsync(request, cancellationToken)
                    : new HttpResponseMessage(imageUploadStatusCode);
            }

            if (path.EndsWith("/api/artist/artist-1/image", StringComparison.Ordinal))
            {
                return await CaptureImageUploadAsync(request, cancellationToken);
            }

            if (path.EndsWith("/search3.view", StringComparison.Ordinal)
                && url.Contains("artistCount=25", StringComparison.Ordinal))
            {
                return await Json("""
                    {
                      "subsonic-response": {
                        "status": "ok",
                        "searchResult3": {
                          "artist": [
                            { "id": "artist-1", "name": "Alikiba", "coverArt": "artist-cover-1" }
                          ]
                        }
                      }
                    }
                    """);
            }

            if (path.EndsWith("/getPlaylists.view", StringComparison.Ordinal))
            {
                if (getPlaylistsStatusCode != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(getPlaylistsStatusCode);
                }

                var playlists = playlistExistsInitially || _playlistCreated
                    ? duplicatePlaylistName ? """
                              "playlist": [
                                { "id": "playlist-1", "name": "Gold School", "songCount": 1 },
                                { "id": "playlist-2", "name": "Gold School", "songCount": 1 }
                              ]
                      """ : """
                              "playlist": [
                                { "id": "playlist-1", "name": "Gold School", "songCount": 1, "coverArt": "playlist-cover-1" }
                              ]
                      """
                    : """
                              "playlist": []
                      """;
                return await Json("""
                    {
                      "subsonic-response": {
                        "status": "ok",
                        "playlists": {
                    """ + playlists + """
                        }
                      }
                    }
                    """);
            }

            if (path.EndsWith("/getPlaylist.view", StringComparison.Ordinal))
            {
                if (getPlaylistStatusCode != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(getPlaylistStatusCode);
                }

                if (simulatePlaylistOrder)
                {
                    var entries = string.Join(",", StoredSongIds.Select(id =>
                        System.Text.Json.JsonSerializer.Serialize(new { id, title = id, artist = "Artist" })));
                    return await Json("{\"subsonic-response\":{\"status\":\"ok\",\"playlist\":{\"id\":\"playlist-1\",\"name\":\"Gold School\",\"entry\":[" + entries + "]}}}");
                }

                return await Json("""
                    {
                      "subsonic-response": {
                        "status": "ok",
                        "playlist": {
                          "id": "playlist-1",
                          "name": "Gold School",
                          "comment": "A reliable playlist description",
                          "entry": [
                            { "id": "song-1", "title": "Song 1", "artist": "Artist" }
                          ]
                        }
                      }
                    }
                    """);
            }

            if (path.EndsWith("/getArtistInfo2.view", StringComparison.Ordinal))
            {
                return await Json("""
                    {
                      "subsonic-response": {
                        "status": "ok",
                        "artistInfo2": {
                          "biography": "Artist biography",
                          "largeImageUrl": "http://navidrome.local/rest/getCoverArt.view?id=artist-1&size=1200",
                          "musicBrainzId": "artist-mbid"
                        }
                      }
                    }
                    """);
            }

            if (path.EndsWith("/createPlaylist.view", StringComparison.Ordinal))
            {
                _playlistCreated = true;
                return await Json(createReturnsPlaylist
                    ? """
                      {
                        "subsonic-response": {
                          "status": "ok",
                          "playlist": { "id": "playlist-1", "name": "Gold School", "songCount": 1 }
                        }
                      }
                      """
                    : """
                      {
                        "subsonic-response": {
                          "status": "ok"
                        }
                      }
                      """);
            }

            if (path.EndsWith("/updatePlaylist.view", StringComparison.Ordinal) && simulatePlaylistOrder)
            {
                if (updatePlaylistStatusCode != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(updatePlaylistStatusCode);
                }
                if (!ignorePlaylistUpdates)
                {
                    foreach (var index in GetQueryValues(url, "songIndexToRemove")
                        .Select(int.Parse).OrderDescending())
                    {
                        StoredSongIds.RemoveAt(index);
                    }
                    StoredSongIds.AddRange(GetQueryValues(url, "songIdToAdd"));
                }
                return await Json("""{"subsonic-response":{"status":"ok"}}""");
            }

            if (path.EndsWith("/updatePlaylist.view", StringComparison.Ordinal))
            {
                var query = request.RequestUri.Query;
                if (GetQueryValues(url, "songIdToAdd").Contains("song-2", StringComparer.Ordinal))
                {
                    Assert.Contains("songIdToAdd=song-2", query, StringComparison.Ordinal);
                    Assert.DoesNotContain("songId=song-2", query, StringComparison.Ordinal);
                    if (!StoredSongIds.Contains("song-2", StringComparer.Ordinal))
                    {
                        StoredSongIds.Add("song-2");
                    }
                }
                return await Json("""
                    {
                      "subsonic-response": {
                        "status": "ok"
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private async Task<HttpResponseMessage> CaptureImageUploadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UploadAuthorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            if (request.Headers.TryGetValues("X-ND-Authorization", out var nativeAuth))
            {
                UploadAuthorization = nativeAuth.Single();
            }
            UploadBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await Json("""
                {
                  "status": "ok"
                }
                """);
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
    }

    private sealed class HtmlResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>not json</html>")
            });
    }

    private static List<string> GetQueryValues(string url, string name)
    {
        var uri = new Uri(url);
        return uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), name, StringComparison.Ordinal))
            .Select(parts => Uri.UnescapeDataString(parts[1]))
            .ToList();
    }
}
