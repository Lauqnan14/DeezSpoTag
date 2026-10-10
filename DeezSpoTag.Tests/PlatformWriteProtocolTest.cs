using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Apple;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Integrations.Spotify;
using DeezSpoTag.Integrations.Tidal;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Pins the protocol details that make a platform write actually reach the provider.
/// <para>
/// Every assertion here corresponds to a way a destination call can look successful while doing
/// nothing, or can read as "empty" and cause a mirror pass to delete a playlist. The shared shape
/// across these is that the provider's API disagrees with the assumption the adapter was written
/// on, and nothing in the response says so.
/// </para>
/// </summary>
public sealed class PlatformWriteProtocolTest
{
    [Theory]
    [InlineData("qobuz", "description")]
    [InlineData("qobuz", "")]
    [InlineData("tidal", "description")]
    [InlineData("tidal", "")]
    [InlineData("applemusic", "description")]
    [InlineData("applemusic", "")]
    public async Task MetadataDescriptionUsesTheProviderUpdateProtocol(string service, string description)
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK,
            service == "qobuz" ? "{\"id\":42}" : "{\"data\":{\"id\":\"42\"}}"));
        IPlaylistSyncTarget target = service switch
        {
            "qobuz" => new QobuzPlaylistSyncTarget(Transport(handler), _ => Task.FromResult<QobuzTargetCredentials?>(new("app", "token", "user"))),
            "tidal" => Tidal(handler),
            _ => Apple(handler)
        };
        var result = await target.WriteMetadataAsync(new("42", "Road", description, new(new byte[] { 1 }, "image/jpeg")), CancellationToken.None);
        Assert.Equal(PlaylistMetadataFieldStatus.Applied, result.DescriptionStatus);
        Assert.Equal(PlaylistMetadataFieldStatus.Unsupported, result.ArtworkStatus);
        var request = Assert.Single(handler.Requests);
        if (service == "qobuz")
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("/playlist/update", request.Url);
            Assert.Contains("playlist_id=42", request.Body!);
            Assert.Contains("description=" + description, request.Body!);
        }
        else
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Contains("/playlists/42", request.Url);
            Assert.Contains("\"description\":\"" + description + "\"", request.Body!);
            Assert.DoesNotContain("isPublic", request.Body!);
            Assert.DoesNotContain("accessType", request.Body!);
        }
    }

    [Theory]
    [InlineData("qobuz", 401)]
    [InlineData("tidal", 403)]
    [InlineData("applemusic", 429)]
    [InlineData("applemusic", 503)]
    [InlineData("qobuz", 200)]
    public async Task MetadataRejectionOrMalformedResponseDoesNotBecomeApplied(string service, int status)
    {
        var handler = new RecordingHandler((_, _) => ((HttpStatusCode)status, "{\"error\":\"sensitive-provider-data\"}"));
        IPlaylistSyncTarget target = service switch
        {
            "qobuz" => new QobuzPlaylistSyncTarget(Transport(handler), _ => Task.FromResult<QobuzTargetCredentials?>(new("app", "token", "user"))),
            "tidal" => Tidal(handler),
            _ => Apple(handler)
        };
        var result = await target.WriteMetadataAsync(new("42", "Road", "Updated", null), CancellationToken.None);
        Assert.Equal(PlaylistMetadataFieldStatus.Failed, result.DescriptionStatus);
        Assert.Equal(PlaylistMetadataFieldStatus.NotRequested, result.ArtworkStatus);
        Assert.DoesNotContain("sensitive-provider-data", result.Message ?? "");
    }

    [Theory]
    [InlineData("deezer")]
    public async Task MetadataWithNoVerifiedCurrentCredentialProtocolIsExplicitlyUnsupported(string service)
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("No speculative metadata requests"));
        IPlaylistSyncTarget target = service == "spotify" ? Spotify(handler) : Deezer(handler);
        var result = await target.WriteMetadataAsync(new("42", "Road", "Description", new(new byte[] { 1 }, "image/jpeg")), CancellationToken.None);
        Assert.Equal(PlaylistMetadataFieldStatus.Unsupported, result.DescriptionStatus);
        Assert.Equal(PlaylistMetadataFieldStatus.Unsupported, result.ArtworkStatus);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("Updated")]
    [InlineData("")]
    public async Task SpotifyMetadataUsesTheExistingWebSessionAndRegisteredPicture(string description)
    {
        var handler = new RecordingHandler((request, _) => (HttpStatusCode.OK,
            request.RequestUri!.Host == "image-upload.spotify.com" ? "{\"uploadToken\":\"upload-one\"}"
            : request.RequestUri!.AbsolutePath.EndsWith("register-image") ? "{\"picture\":\"picture-one\"}"
            : "{\"revision\":\"revision-one\"}"));
        var result = await Spotify(handler).WriteMetadataAsync(new("spotify:playlist:42", "Road", description,
            new(new byte[] { 1, 2, 3 }, "image/jpeg")), CancellationToken.None);
        Assert.Equal(PlaylistMetadataFieldStatus.Applied, result.DescriptionStatus);
        Assert.Equal(PlaylistMetadataFieldStatus.Applied, result.ArtworkStatus);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("https://image-upload.spotify.com/v4/playlist", handler.Requests[0].Url);
        Assert.Equal("image/jpeg", handler.Requests[0].ContentType);
        Assert.Contains("/playlist/v2/playlist/42/register-image", handler.Requests[1].Url);
        Assert.Contains("\"uploadToken\":\"upload-one\"", handler.Requests[1].Body!);
        Assert.Contains("/playlist/v2/playlist/42/changes", handler.Requests[2].Url);
        Assert.Contains("\"deltas\"", handler.Requests[2].Body!);
        Assert.Contains("\"picture\":\"picture-one\"", handler.Requests[2].Body!);
        Assert.Contains(description.Length == 0 ? "LIST_DESCRIPTION" : "\"description\":\"Updated\"", handler.Requests[2].Body!);
        Assert.DoesNotContain("api.spotify.com", string.Join(" ", handler.Requests.Select(r => r.Url)));
    }

    [Fact]
    public async Task SpotifyImageRejectionDoesNotSuppressTheDescription()
    {
        var handler = new RecordingHandler((request, _) => request.RequestUri!.Host == "image-upload.spotify.com"
            ? (HttpStatusCode.Forbidden, "{}") : (HttpStatusCode.OK, "{\"revision\":\"r\"}"));
        var result = await Spotify(handler).WriteMetadataAsync(new("42", "Road", "Updated", new(new byte[] { 1 }, "image/jpeg")), CancellationToken.None);
        Assert.Equal(PlaylistMetadataFieldStatus.Applied, result.DescriptionStatus);
        Assert.Equal(PlaylistMetadataFieldStatus.Failed, result.ArtworkStatus);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("description", handler.Requests[1].Body!);
        Assert.DoesNotContain("picture-one", handler.Requests[1].Body!);
    }

    [Theory]
    [InlineData("spotify")]
    [InlineData("deezer")]
    [InlineData("qobuz")]
    [InlineData("tidal")]
    [InlineData("applemusic")]
    public async Task UnrequestedMetadataMakesNoProviderRequest(string service)
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Unrequested metadata must not write"));
        IPlaylistSyncTarget target = service switch
        {
            "spotify" => Spotify(handler), "deezer" => Deezer(handler), "tidal" => Tidal(handler), "applemusic" => Apple(handler),
            _ => new QobuzPlaylistSyncTarget(Transport(handler), _ => Task.FromResult<QobuzTargetCredentials?>(new("app", "token", "user")))
        };
        var result = await target.WriteMetadataAsync(new("42", "Road", null, null), CancellationToken.None);
        Assert.True(result.Complete);
        Assert.Equal(PlaylistMetadataFieldStatus.NotRequested, result.DescriptionStatus);
        Assert.Equal(PlaylistMetadataFieldStatus.NotRequested, result.ArtworkStatus);
        Assert.Empty(handler.Requests);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, (HttpStatusCode, string)> _respond;
        public List<(HttpMethod Method, string Url, string? Body, string? ContentType)> Requests { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((
                request.Method,
                request.RequestUri!.ToString(),
                body,
                request.Content?.Headers.ContentType?.MediaType));

            var (status, content) = _respond(request, body);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static TargetApiTransport Transport(RecordingHandler handler)
        => new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) }, TimeSpan.Zero)
        {
            SkipPacing = true,
            MaxAttempts = 1,
        };

    private static SpotifyPlaylistSyncTarget Spotify(RecordingHandler handler)
        => new(
            new SpotifyPlaylistWriteClient(
                new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) },
                _ => Task.FromResult<SpotifyPlaylistWriteClient.SpotifyWebPlayerSession?>(
                    new SpotifyPlaylistWriteClient.SpotifyWebPlayerSession("token", "client-token", "1.2.3"))),
            _ => Task.FromResult<SpotifyTargetCredentials?>(
                new SpotifyTargetCredentials("token", "refresh", "client", "secret")));

    private static DeezerPlaylistSyncTarget Deezer(RecordingHandler handler)
        => new(Transport(handler), _ => Task.FromResult<DeezerTargetSession?>(
            new DeezerTargetSession("token", "42")));

    private static AppleMusicPlaylistSyncTarget Apple(RecordingHandler handler)
        => new(Transport(handler), _ => Task.FromResult<AppleTargetCredentials?>(
            new AppleTargetCredentials("bearer", "mut", "us")));

    private static TidalPlaylistSyncTarget Tidal(RecordingHandler handler)
        => new(Transport(handler), _ => Task.FromResult<TidalTargetCredentials?>(
            new TidalTargetCredentials("token", "US")));

    /// <summary>
    /// A single complete page. <c>meta.total</c> matches the rows on the page so the adapter's
    /// paging terminates here; a page claiming more rows than it returns would send the read back
    /// for a second page, which is a different scenario and is covered separately.
    /// </summary>
    private const string TwoTrackApplePage = """
        {"data":[
          {"id":"p.1","attributes":{"playParameters":{"catalogId":"i.abc"}}},
          {"id":"p.2","attributes":{"playParameters":{"catalogId":"i.def"}}}],
         "meta":{"total":2}}
        """;

    // ------------------------------------------------------------------ Spotify: JSON bodies

    [Fact]
    public async Task Spotify_CreatesWithAnAttributeOperationAndFilesThePlaylistIntoTheLibrary()
    {
        // Creation is not one of the persisted-query operations. The name travels as an attribute
        // update, and a playlist the web player created is NOT in the account's library until a
        // second call files it - an unfiled playlist is invisible, so the next pass would not find
        // it and would create a second one under the same name.
        var handler = new RecordingHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/playlist/v2/playlist", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"uri":"spotify:playlist:new-pl"}""");
            }

            return request.RequestUri.AbsolutePath.EndsWith("/rootlist", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """{"revision":"rev-1"}""")
                : (HttpStatusCode.OK, """{"me":{"profile":{"username":"edzoh"}}}""");
        });

        var created = await Spotify(handler).CreatePlaylistAsync("Roadtrip", null, CancellationToken.None);

        Assert.Equal("new-pl", created);

        var create = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post
            && r.Url.EndsWith("/playlist/v2/playlist", StringComparison.Ordinal));
        Assert.Equal("application/json", create.ContentType);
        Assert.Contains("\"name\":\"Roadtrip\"", create.Body!, StringComparison.Ordinal);

        Assert.Contains(handler.Requests, r => r.Url.Contains("new-pl", StringComparison.Ordinal)
            || (r.Body?.Contains("spotify:playlist:new-pl", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task Spotify_AddressesRemovalByOccurrenceIdRatherThanTrackId()
    {
        // A removal addressed by track id takes out every copy of that track. The uid is the only
        // handle that can retire one occurrence, so it is what the call has to carry.
        var handler = new RecordingHandler((_, body) =>
            body?.Contains("fetchPlaylistContents", StringComparison.Ordinal) == true
                ? (HttpStatusCode.OK, """{"playlistV2":{"content":{"totalCount":1,"items":[{"uid":"uid-old1","itemV2":{"data":{"uri":"spotify:track:old1"}}}]}}}""")
                : (HttpStatusCode.OK, "{}"));

        await Spotify(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", Array.Empty<string>(), new[] { "old1" }, false),
            CancellationToken.None);

        var remove = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post
            && r.Body!.Contains("removeFromPlaylist", StringComparison.Ordinal));
        Assert.Contains("uid-old1", remove.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spotify_AppendModeSendsOnlyTheMissingTracks()
    {
        // Spotify permits duplicates, so re-sending ids the playlist already holds appends a second
        // copy of them. Each pass would grow the playlist rather than converge.
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK, "{}"));

        await Spotify(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "a", "b", "c" }, new[] { "a", "b" }, true),
            CancellationToken.None);

        var adds = handler.Requests.Where(r => r.Method == HttpMethod.Post
            && r.Body!.Contains("addToPlaylist", StringComparison.Ordinal)).ToList();
        Assert.Single(adds);
        Assert.Contains("spotify:track:c", adds[0].Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("spotify:track:a", adds[0].Body!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Deezer: auth + error envelope

    [Fact]
    public async Task Deezer_CarriesTheTokenOnTheQueryString()
    {
        // Deezer's API authenticates with an access_token query parameter. The Authorization header
        // is ignored, so the call returns the public unauthenticated view instead of the account's.
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK, """{"data":[]}"""));

        await Deezer(handler).ReadItemIdsAsync("123", CancellationToken.None);

        Assert.Contains("access_token=token", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deezer_AnExpiredTokenReturnedAsHttp200IsUnreadableNotEmpty()
    {
        // Deezer reports failures in the body with HTTP 200 and no data array. Read as "empty", a
        // mirror pass would delete every track on the destination.
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK,
            """{"error":{"type":"OAuthException","code":200,"message":"invalid oauth token"}}"""));

        var read = await Deezer(handler).ReadItemIdsAsync("123", CancellationToken.None);

        Assert.False(read.Success);
    }

    [Fact]
    public async Task Deezer_ARejectedAddReturnedAsHttp200IsAFailure()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK,
            """{"error":{"type":"OAuthException","code":200,"message":"invalid oauth token"}}"""));

        var result = await Deezer(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("123", "Roadtrip", new[] { "t1" }, Array.Empty<string>(), true),
            CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Deezer_AppendModeSendsOnlyTheMissingTracks()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK, "{}"));

        await Deezer(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("123", "Roadtrip", new[] { "a", "b" }, new[] { "a" }, true),
            CancellationToken.None);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("songs=b", post.Body!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Apple: two identities

    [Fact]
    public async Task Apple_DoesNotTreatLibrarySongIdsAsCatalogIds()
    {
        // The two are different namespaces. A read that reports library ids, compared against the
        // catalog ids a write wants, matches nothing - so a mirror pass would decide every existing
        // track is unwanted and delete the whole playlist.
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK, TwoTrackApplePage));

        var read = await Apple(handler).ReadItemIdsAsync("p.pl", CancellationToken.None);

        Assert.True(read.Success);
        // Membership is the library id, which is what a removal addresses.
        Assert.Equal(new[] { "p.1", "p.2" }, read.ItemIds);
        Assert.DoesNotContain("i.abc", read.ItemIds);
    }

    [Fact]
    public async Task Apple_DoesNotRemoveTracksThatAreStillWanted()
    {
        // The caller sends catalog ids. Matching those against the catalog ids the read carries -
        // not the library ids - is what stops a wanted track being removed.
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK, TwoTrackApplePage));

        var result = await Apple(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("p.pl", "Roadtrip", new[] { "i.abc" }, new[] { "p.1", "p.2" }, false),
            CancellationToken.None);

        Assert.True(result.Success, result.Message);

        // i.abc is wanted, so the row holding it (p.1) must survive. i.def is not wanted, so p.2
        // is the only row that may be removed.
        var deletes = handler.Requests.Where(r => r.Method == HttpMethod.Delete).ToList();
        Assert.Single(deletes);
        Assert.Contains("p.2", deletes[0].Url, StringComparison.Ordinal);
        Assert.DoesNotContain("library-songs=p.1", deletes[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apple_RemovesOnlyTheRowWhoseTrackIsNoLongerWanted()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK, TwoTrackApplePage));

        await Apple(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("p.pl", "Roadtrip", new[] { "i.abc" }, new[] { "p.1", "p.2" }, false),
            CancellationToken.None);

        var delete = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Contains("ids[library-songs]=p.2", delete.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apple_AppendModeSendsOnlyTheMissingCatalogIds()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK, TwoTrackApplePage));

        await Apple(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("p.pl", "Roadtrip", new[] { "i.abc", "i.xyz" }, new[] { "p.1", "p.2" }, true),
            CancellationToken.None);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("i.xyz", post.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("i.abc\"", post.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apple_AnEntryWithNoCatalogIdIsNotSilentlyTreatedAsUnwanted()
    {
        // Apple omits the catalog id for a track it can no longer match. Without one, the row cannot
        // be compared to the wanted list, and treating it as unwanted would delete a track the user
        // put there.
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK,
            """{"data":[{"id":"p.1","attributes":{}}],"meta":{"total":1}}"""));

        await Apple(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("p.pl", "Roadtrip", new[] { "i.abc" }, new[] { "p.1" }, false),
            CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    // ------------------------------------------------------------------ TIDAL / Qobuz duplicates

    [Fact]
    public async Task Tidal_AppendModeSendsOnlyTheMissingTracks()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK,
            """{"data":[{"id":"a","meta":{"itemId":"i1"}},{"id":"b","meta":{"itemId":"i2"}}]}"""));

        await Tidal(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "a", "b", "c" }, new[] { "a", "b" }, true),
            CancellationToken.None);

        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Single(posts);
        Assert.Contains("\"c\"", posts[0].Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qobuz_AppendModeSendsOnlyTheMissingTracks()
    {
        var handler = new RecordingHandler((request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("playlist/get", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """
                    {"tracks":{"total":2,"items":[
                      {"id":"a","playlist_track_id":"pa"},
                      {"id":"b","playlist_track_id":"pb"}]}}
                    """)
                : (HttpStatusCode.OK, "{}"));

        await QobuzPlaylistSyncTargetFactory(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "a", "b", "c" }, new[] { "a", "b" }, true),
            CancellationToken.None);

        var adds = handler.Requests.Where(r => r.Url.Contains("addTracks", StringComparison.Ordinal)).ToList();
        Assert.Single(adds);
    }

    // ------------------------------------------------------- mirror mode never re-adds

    // Mirror mode governs REMOVAL only. The reference computes the additions the same way in both
    // modes - the wanted list minus what the destination already holds - so a writer that sent the
    // whole list whenever removals were allowed would re-append every track on every pass and a
    // playlist mirroring itself would grow forever instead of converging. Each of these is the same
    // assertion against one destination.

    [Fact]
    public async Task Spotify_MirrorModeSendsOnlyTheMissingTracks()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK, "{}"));

        await Spotify(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "a", "b", "c" }, new[] { "a", "b" }, false),
            CancellationToken.None);

        var adds = handler.Requests.Where(r => r.Method == HttpMethod.Post
            && r.Body!.Contains("addToPlaylist", StringComparison.Ordinal)).ToList();
        Assert.Single(adds);
        Assert.Contains("spotify:track:c", adds[0].Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("spotify:track:a", adds[0].Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deezer_MirrorModeSendsOnlyTheMissingTracks()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK, "{}"));

        await Deezer(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("123", "Roadtrip", new[] { "a", "b" }, new[] { "a" }, false),
            CancellationToken.None);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("songs=b", post.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("songs=a", post.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apple_MirrorModeSendsOnlyTheMissingCatalogIds()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK, TwoTrackApplePage));

        await Apple(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("p.pl", "Roadtrip", new[] { "i.abc", "i.xyz" }, new[] { "p.1", "p.2" }, false),
            CancellationToken.None);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("i.xyz", post.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("i.abc\"", post.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tidal_MirrorModeSendsOnlyTheMissingTracks()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK,
            """{"data":[{"id":"a","meta":{"itemId":"i1"}},{"id":"b","meta":{"itemId":"i2"}}]}"""));

        await Tidal(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "a", "b", "c" }, new[] { "a", "b" }, false),
            CancellationToken.None);

        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Single(posts);
        Assert.Contains("\"id\":\"c\"", posts[0].Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":\"a\"", posts[0].Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qobuz_MirrorModeSendsOnlyTheMissingTracks()
    {
        var handler = new RecordingHandler((request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("playlist/get", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """
                    {"tracks":{"total":2,"items":[
                      {"id":"a","playlist_track_id":"pa"},
                      {"id":"b","playlist_track_id":"pb"}]}}
                    """)
                : (HttpStatusCode.OK, "{}"));

        await QobuzPlaylistSyncTargetFactory(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "a", "b", "c" }, new[] { "a", "b" }, false),
            CancellationToken.None);

        var adds = handler.Requests.Where(r => r.Url.Contains("addTracks", StringComparison.Ordinal)).ToList();
        Assert.Single(adds);
        Assert.Contains("track_ids=c", adds[0].Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("track_ids=a", adds[0].Body!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A playlist the account genuinely holds nothing in must read as empty, not as a failure. The
    /// two answers look identical from the outside and mean opposite things: an empty read is a
    /// playlist that needs filling, a failed one must block every removal.
    /// </summary>
    [Fact]
    public async Task Tidal_AnEmptyPlaylistReadsAsEmptyRatherThanAsAFailedRead()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.OK,
            """{"data":[],"links":{"next":null},"meta":{"total":0}}"""));

        var read = await Tidal(handler).ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.True(read.Success);
        Assert.Empty(read.ItemIds);
    }

    private static QobuzPlaylistSyncTarget QobuzPlaylistSyncTargetFactory(RecordingHandler handler)
        => new(Transport(handler), _ => Task.FromResult<QobuzTargetCredentials?>(
            new QobuzTargetCredentials("app-id", "user-token", "42")));
}
