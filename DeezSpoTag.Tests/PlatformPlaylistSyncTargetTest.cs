using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.Spotify;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the platform playlist destinations built on <see cref="TargetApiTransport"/>.
/// <para>
/// The behaviour worth protecting is not the happy path - it is what happens when a provider
/// misbehaves. A read that fails must never look like an empty playlist, a removal must not run
/// when the read is suspect, and a track that appears twice must not be silently halved on a
/// provider that can only delete by catalog id. Those are the failures that destroy a user's
/// playlist, so they are asserted directly rather than inferred from a passing sync.
/// </para>
/// </summary>
public sealed class PlatformPlaylistSyncTargetTest
{
    [Theory]
    [InlineData("jellyfin", true, "pl-1")]
    [InlineData("jellyfin", false, "pl-1")]
    [InlineData("jellyfin", true, null)]
    [InlineData("jellyfin", false, null)]
    [InlineData("jellyfin", true, "  ")]
    [InlineData("jellyfin", false, "  ")]
    [InlineData("navidrome", true, "pl-1")]
    [InlineData("navidrome", false, "pl-1")]
    [InlineData("navidrome", true, null)]
    [InlineData("navidrome", false, null)]
    [InlineData("navidrome", true, "  ")]
    [InlineData("navidrome", false, "  ")]
    public async Task LibraryLookupRequiresAValidReturnedId(string platform, bool byId, string? returnedId)
    {
        var id = System.Text.Json.JsonSerializer.Serialize(returnedId);
        var item = "{\"Id\":" + id + ",\"Name\":\"Roadtrip\"}";
        string body;
        if (platform == "jellyfin")
        {
            body = byId ? item : "{\"Items\":[" + item + "]}";
        }
        else if (byId)
        {
            body = "{\"subsonic-response\":{\"status\":\"ok\",\"playlist\":{\"id\":" + id + ",\"name\":\"Roadtrip\"}}}";
        }
        else
        {
            body = "{\"subsonic-response\":{\"status\":\"ok\",\"playlists\":{\"playlist\":[{\"id\":" + id + ",\"name\":\"Roadtrip\"}]}}}";
        }
        using var handler = new FakeHandler((_, _) => (HttpStatusCode.OK, body));
        using var http = new HttpClient(handler);
        var target = LibraryTarget(platform, http);
        var result = await target.FindPlaylistAsync(byId ? "requested-id" : null, "Roadtrip", default);
        Assert.Equal(string.IsNullOrWhiteSpace(returnedId) ? TargetLookupStatus.NotFound : TargetLookupStatus.Success, result.Status);
        Assert.Equal(string.IsNullOrWhiteSpace(returnedId) ? null : returnedId, result.Value);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData("jellyfin", true)]
    [InlineData("jellyfin", false)]
    [InlineData("navidrome", true)]
    [InlineData("navidrome", false)]
    public async Task LibraryLookupRetainsTransientFailures(string platform, bool byId)
    {
        using var handler = new FakeHandler((_, _) => (HttpStatusCode.ServiceUnavailable, "{}"));
        using var http = new HttpClient(handler);
        var result = await LibraryTarget(platform, http).FindPlaylistAsync(byId ? "requested-id" : null, "Roadtrip", default);
        Assert.Equal(TargetLookupStatus.Transient, result.Status);
        Assert.Equal(503, result.HttpStatusCode);
        Assert.Null(result.Value);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    private static IPlaylistSyncTarget LibraryTarget(string platform, HttpClient http)
        => platform == "jellyfin"
            ? new DeezSpoTag.Integrations.Jellyfin.JellyfinPlaylistSyncTarget(
                new DeezSpoTag.Integrations.Jellyfin.JellyfinApiClient(http),
                _ => Task.FromResult<DeezSpoTag.Integrations.Jellyfin.JellyfinTargetConnection?>(new("http://jellyfin.local", "key", "user")))
            : new DeezSpoTag.Integrations.Navidrome.NavidromePlaylistSyncTarget(
                new DeezSpoTag.Integrations.Navidrome.NavidromeApiClient(http),
                _ => Task.FromResult<DeezSpoTag.Integrations.Navidrome.NavidromeTargetConnection?>(new("http://navidrome.local", "user", "pass")));

    [Theory]
    [InlineData(null, "client", false)]
    [InlineData("", "client", false)]
    [InlineData("token", "", false)]
    [InlineData("token", "client", true)]
    public void SpotifySessionValidationRequiresBothTokens(string? accessToken, string clientToken, bool expected)
    {
        var session = accessToken is null ? null : new SpotifyPlaylistWriteClient.SpotifyWebPlayerSession(accessToken, clientToken, "version");
        Assert.Equal(expected, SpotifyPlaylistWriteClient.SpotifyWebPlayerSession.IsUsable(session));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> _respond;
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));

            // A transport failure is simulated by throwing, which is what the transport has to
            // survive as well as an HTTP error.
            if (_respond(request, body) == (HttpStatusCode.ServiceUnavailable, "__throw__"))
            {
                throw new HttpRequestException("simulated transport failure");
            }

            var (status, content) = _respond(request, body);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static TargetApiTransport Transport(FakeHandler handler)
        => new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) }, TimeSpan.Zero)
        {
            SkipPacing = true,
            MaxAttempts = 1,
        };

    /// <summary>
    /// The Spotify destination speaks the web player's own protocol rather than the official Web
    /// API, so its responses are persisted-query payloads rather than REST resources. A single
    /// complete contents page, with <c>totalCount</c> agreeing with the rows on it so the read
    /// terminates on the first page.
    /// </summary>
    private static string ContentsPage(params string[] trackUris)
        => "{\"playlistV2\":{\"content\":{\"totalCount\":" + trackUris.Length + ",\"items\":["
           + string.Join(",", trackUris.Select(uri =>
               "{\"uid\":\"uid-" + uri["spotify:track:".Length..] + "\",\"itemV2\":{\"data\":{\"uri\":\"" + uri + "\"}}}"))
           + "]}}}";

    private static SpotifyPlaylistSyncTarget Spotify(
        Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond)
        => Spotify(new FakeHandler(respond));

    private static SpotifyPlaylistSyncTarget Spotify(FakeHandler handler, string token = "token")
        => new(
            new SpotifyPlaylistWriteClient(
                new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) },
                _ => Task.FromResult<SpotifyPlaylistWriteClient.SpotifyWebPlayerSession?>(
                    new SpotifyPlaylistWriteClient.SpotifyWebPlayerSession(token, "client-token", "1.2.3"))),
            _ => Task.FromResult<SpotifyTargetCredentials?>(new SpotifyTargetCredentials(token)));

    private static DeezerPlaylistSyncTarget Deezer(FakeHandler handler, string token = "token")
        => new(Transport(handler), _ => Task.FromResult<DeezerTargetSession?>(new DeezerTargetSession(token, "42")));

    // ---------------------------------------------------------------- read safety

    [Fact]
    public async Task Spotify_AReadThatFails_IsUnreadableRatherThanEmpty()
    {
        var target = Spotify((_, _) => (HttpStatusCode.ServiceUnavailable, "{}"));
        var read = await target.ReadItemIdsAsync("pl-1", CancellationToken.None);

        // An empty read would make the engine treat the destination as holding nothing and delete
        // every track on it.
        Assert.False(read.Success);
        Assert.Empty(read.ItemIds);
    }

    [Fact]
    public async Task Spotify_AnUnreachableProvider_IsUnreadableRatherThanEmpty()
    {
        var target = Spotify((_, _) => (HttpStatusCode.ServiceUnavailable, "__throw__"));
        var read = await target.ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.False(read.Success);
    }

    [Fact]
    public async Task Deezer_AReadThatFails_IsUnreadableRatherThanEmpty()
    {
        var target = Deezer(new FakeHandler((_, _) => (HttpStatusCode.InternalServerError, "{}")));
        var read = await target.ReadItemIdsAsync("123", CancellationToken.None);

        Assert.False(read.Success);
    }

    [Fact]
    public async Task Deezer_AShortPagedRead_IsUnreadableRatherThanAPartialMembership()
    {
        // The provider advertises 10 tracks and then returns none. Reporting the empty page as the
        // membership would make the caller remove all ten.
        var target = Deezer(new FakeHandler((_, _) => (HttpStatusCode.OK, """{"total":10,"next":"x","data":[]}""")));
        var read = await target.ReadItemIdsAsync("123", CancellationToken.None);

        Assert.False(read.Success);
    }

    [Fact]
    public async Task Spotify_AReadPaginatesUntilTheProviderStopsOfferingMore()
    {
        // The offset travels in the persisted-query variables rather than the URL, so the second
        // page is requested when the payload asks for offset 2.
        var handler = new FakeHandler((_, body) =>
            body.Contains("\"offset\":2", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, ContentsPage())
                : (HttpStatusCode.OK, ContentsPage("spotify:track:t1", "spotify:track:t2")));

        var read = await Spotify(handler).ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.True(read.Success);
        Assert.Equal(new[] { "t1", "t2" }, read.ItemIds);
    }

    [Fact]
    public async Task Spotify_ALocalFileWithNoCatalogId_IsSkippedRatherThanRecorded()
    {
        // A local file has no spotify:track: uri, so there is nothing to address it by later.
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK,
            """{"playlistV2":{"content":{"totalCount":2,"items":[{"uid":"u1","itemV2":{"data":{"uri":"spotify:track:t1"}}},{"uid":"u2","itemV2":{"data":{"uri":"spotify:local"}}}]}}}"""));

        var read = await Spotify(handler).ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.True(read.Success);
        Assert.Equal(new[] { "t1" }, read.ItemIds);
    }

    [Fact]
    public async Task Spotify_APageThatStopsShortOfItsOwnTotal_IsUnreadableRatherThanAPartialMembership()
    {
        // The provider claims ten entries and then returns an empty page with eight still to come.
        // Reporting what arrived as the membership would make the caller remove the other eight.
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK,
            """{"playlistV2":{"content":{"totalCount":10,"items":[]}}}"""));

        var read = await Spotify(handler).ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.False(read.Success);
    }

    [Fact]
    public async Task Spotify_APageWithNoTotalCount_IsUnreadable()
    {
        // Without a total there is nothing to say the page was complete, so a short page would look
        // like the whole playlist.
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK,
            """{"playlistV2":{"content":{"items":[{"uid":"u1","itemV2":{"data":{"uri":"spotify:track:t1"}}}]}}}"""));

        var read = await Spotify(handler).ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.False(read.Success);
    }

    // ---------------------------------------------------------------- write safety

    [Fact]
    public async Task Spotify_AppendMode_NeverRemoves()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK, "{}"));
        var result = await Spotify(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "t1" }, new[] { "old1", "old2" }, true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.RemovedCount);
        Assert.DoesNotContain(handler.Requests, r => OperationOf(r) == "removeFromPlaylist");
    }

    [Fact]
    public async Task Spotify_MirrorMode_RemovesOnlyWhatIsNoLongerWanted()
    {
        // The removal names the occurrence uid read alongside the track. That is what makes a single
        // copy retirable: a removal addressed by track id would take out every copy of the track.
        var handler = new FakeHandler((request, body) =>
            OperationOf(body) == "fetchPlaylistContents"
                ? (HttpStatusCode.OK, ContentsPage("spotify:track:t1", "spotify:track:old1"))
                : (HttpStatusCode.OK, "{}"));

        var result = await Spotify(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "t1" }, new[] { "t1", "old1" }, false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.RemovedCount);
        var removals = handler.Requests.Where(r => OperationOf(r) == "removeFromPlaylist").ToList();
        Assert.Single(removals);
        Assert.Contains("uid-old1", removals[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("uid-t1", removals[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spotify_RemovesEveryCopyOfATrackTheSourceNoLongerWants()
    {
        // Unlike the "duplicated track that is still wanted" case, a track that is gone from the
        // source entirely should lose all of its copies - that is what the source asked for.
        var handler = new FakeHandler((request, body) =>
            OperationOf(body) == "fetchPlaylistContents"
                ? (HttpStatusCode.OK, ContentsPage("spotify:track:t1", "spotify:track:gone", "spotify:track:gone"))
                : (HttpStatusCode.OK, "{}"));

        var result = await Spotify(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "t1" }, new[] { "t1", "gone", "gone" }, false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.RemovedCount);
    }

    [Fact]
    public async Task Spotify_StopsAtTheFirstFailedAddSoOrderIsNotInverted()
    {
        // Spotify stamps added-at per request. Continuing past a rejection would give the tracks
        // after it earlier stamps than the ones that belong before it, and there is no positional
        // insert to undo that. The first add succeeds and the second is rejected, so exactly two
        // adds may be attempted - the third must never be sent.
        var posts = 0;
        var handler = new FakeHandler((request, body) =>
        {
            if (OperationOf(body) != "addToPlaylist")
            {
                return (HttpStatusCode.OK, "{}");
            }

            return ++posts == 2
                ? (HttpStatusCode.BadGateway, "{}")
                : (HttpStatusCode.OK, "{}");
        });

        var result = await Spotify(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "a", "b", "c" }, Array.Empty<string>(), true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, posts);
    }

    [Fact]
    public async Task Spotify_RefusesToRemoveWhenAnEntryHasNoOccurrenceId()
    {
        // Without a uid the only handle left is the track id, and a removal by track id takes out
        // every copy - including one the user added deliberately. Refusing is the only safe answer.
        var handler = new FakeHandler((request, body) =>
            OperationOf(body) == "fetchPlaylistContents"
                ? (HttpStatusCode.OK,
                    """{"playlistV2":{"content":{"totalCount":1,"items":[{"itemV2":{"data":{"uri":"spotify:track:dup"}}}]}}}""")
                : (HttpStatusCode.OK, "{}"));

        var result = await Spotify(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", Array.Empty<string>(), new[] { "dup" }, false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(handler.Requests, r => OperationOf(r) == "removeFromPlaylist");
    }

    [Fact]
    public async Task Spotify_RefusesToRemoveAnythingWhenThePlaylistCannotBeRead()
    {
        // The uids only exist in the occurrence read. Removing without them would address the
        // tracks by id, so an unreadable playlist must remove nothing at all.
        var handler = new FakeHandler((_, _) => (HttpStatusCode.ServiceUnavailable, "{}"));
        var result = await Spotify(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "t1" }, new[] { "old1" }, false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(handler.Requests, r => OperationOf(r) == "removeFromPlaylist");
    }

    [Fact]
    public async Task Deezer_RefusesToRemoveADuplicatedTrackBecauseItsDeleteIsByCatalogId()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK, "{}"));
        var result = await Deezer(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("123", "Roadtrip", Array.Empty<string>(), new[] { "dup", "dup" }, false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Deezer_AddsOneTrackPerRequestInOrder()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK, "{}"));
        var result = await Deezer(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("123", "Roadtrip", new[] { "a", "b", "c" }, Array.Empty<string>(), true),
            CancellationToken.None);

        Assert.True(result.Success);
        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Equal(3, posts.Count);
        Assert.Contains("songs=a", posts[0].Body, StringComparison.Ordinal);
        Assert.Contains("songs=b", posts[1].Body, StringComparison.Ordinal);
        Assert.Contains("songs=c", posts[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deezer_AnExpiredTokenIsReportedAsAnAuthProblemRatherThanAnEmptyPlaylist()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.Unauthorized, "{}"));
        var read = await Deezer(handler).ReadItemIdsAsync("123", CancellationToken.None);

        Assert.False(read.Success);
    }

    [Fact]
    public async Task NeitherAdapterWritesAnythingWhenTheReadCouldNotBeTrusted()
    {
        // The engine is responsible for not calling Write after a failed read; this pins the
        // adapter's half of that contract - a failed read leaves no side effect to undo.
        var handler = new FakeHandler((_, _) => (HttpStatusCode.BadGateway, "{}"));
        var read = await Spotify(handler).ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.False(read.Success);
        Assert.DoesNotContain(handler.Requests, r => r.Body!.Contains("addToPlaylist", StringComparison.Ordinal)
            || r.Body!.Contains("removeFromPlaylist", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- identity and kind

    [Fact]
    public void BothAdaptersReportThemselvesAsPlatforms()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK, "{}"));
        Assert.Equal(PlaylistTargetKind.Platform, Spotify(handler).TargetKind);
        Assert.Equal(PlaylistTargetKind.Platform, Deezer(handler).TargetKind);
    }

    [Fact]
    public void TheRegistryKeepsBothApartAndNeverLetsThemCollide()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK, "{}"));
        var registry = new PlaylistSyncTargetRegistry(new IPlaylistSyncTarget[] { Spotify(handler), Deezer(handler) });

        Assert.NotNull(registry.Find("spotify"));
        Assert.NotNull(registry.Find("deezer"));
        // Case-insensitive, because a stored target id is user-influenced.
        Assert.NotNull(registry.Find("Spotify"));
        Assert.Null(registry.Find("amazonmusic"));
    }

    [Fact]
    public async Task FindingByNameFindsAPlaylistAnEarlierPassCreated()
    {
        var handler = new FakeHandler((request, body) =>
            OperationOf(body) == "libraryV3"
                ? (HttpStatusCode.OK,
                    """{"me":{"libraryV3":{"totalCount":1,"items":[{"item":{"data":{"__typename":"Playlist","uri":"spotify:playlist:created-1","name":"Roadtrip","currentUserCapabilities":{"canEditItems":true}}}}]}}}""")
                : (HttpStatusCode.OK, "{}"));

        var found = await Spotify(handler).FindPlaylistAsync(null, "roadtrip", CancellationToken.None);

        Assert.Equal(TargetLookupStatus.Success, found.Status);
        Assert.Equal("created-1", found.Value);
    }

    [Fact]
    public async Task FindingByNameRefusesToAnswerWhenTheLibraryListingIsIncomplete()
    {
        // A row that did not resolve could be the playlist being looked for. Treating its absence
        // as authoritative is what makes a pass create a second playlist under the same name.
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK,
            """{"me":{"libraryV3":{"totalCount":1,"items":[{"item":{"data":{"__typename":"Artist","uri":"spotify:artist:x"}}}]}}}"""));

        var found = await Spotify(handler).FindPlaylistAsync(null, "roadtrip", CancellationToken.None);

        Assert.Equal(TargetLookupStatus.Transient, found.Status);
        Assert.Null(found.Value);
    }

    [Fact]
    public async Task FindingByNameIgnoresDeletedTombstonesAndFolderRows()
    {
        // Both are expected rows in a flattened library page and say nothing about completeness, so
        // they must not turn a complete listing into a refusal.
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK,
            """{"me":{"libraryV3":{"totalCount":3,"items":[{"item":{"data":{"__typename":"NotFound"}}},{"item":{"data":{"__typename":"Folder"}}},{"item":{"data":{"__typename":"Playlist","uri":"spotify:playlist:created-1","name":"Roadtrip","currentUserCapabilities":{"canEditItems":true}}}}]}}}"""));

        var found = await Spotify(handler).FindPlaylistAsync(null, "roadtrip", CancellationToken.None);

        Assert.Equal(TargetLookupStatus.Success, found.Status);
        Assert.Equal("created-1", found.Value);
    }

    [Fact]
    public async Task FindingByNamePrefersTheEditablePlaylistWhenTwoShareAName()
    {
        // A followed playlist is readable but not writable. Choosing it would make every write fail
        // against a playlist the account can see but not change.
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK,
            """{"me":{"libraryV3":{"totalCount":2,"items":[{"item":{"data":{"__typename":"Playlist","uri":"spotify:playlist:followed","name":"Roadtrip","currentUserCapabilities":{"canEditItems":false}}}},{"item":{"data":{"__typename":"Playlist","uri":"spotify:playlist:owned","name":"Roadtrip","currentUserCapabilities":{"canEditItems":true}}}}]}}}"""));

        var found = await Spotify(handler).FindPlaylistAsync(null, "roadtrip", CancellationToken.None);

        Assert.Equal("owned", found.Value);
    }

    [Fact]
    public async Task FindingByKnownIdReadsThePlaylistRatherThanTheWholeLibrary()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK,
            """{"playlistV2":{"uri":"spotify:playlist:pl-1","name":"Roadtrip"}}"""));

        var found = await Spotify(handler).FindPlaylistAsync("pl-1", "Roadtrip", CancellationToken.None);

        Assert.Equal(TargetLookupStatus.Success, found.Status);
        Assert.Equal("pl-1", found.Value);
        Assert.DoesNotContain(handler.Requests, r => OperationOf(r) == "libraryV3");
    }

    /// <summary>
    /// The persisted-query operation a request carried, or null. Takes the body the handler already
    /// read rather than reading the content again, so nothing here has to block on the content stream.
    /// </summary>
    private static string? OperationOf(string body) => ReadOperation(body);

    private static string? OperationOf((HttpMethod Method, string Url, string Body) recorded)
        => ReadOperation(recorded.Body);

    private static string? ReadOperation(string body)
    {
        const string marker = "\"operationName\":\"";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = body.IndexOf('"', start);
        return end < 0 ? null : body[start..end];
    }

    /// <summary>The distinct persisted-query operations a handler saw, in call order.</summary>
    private static List<string> Operations(FakeHandler handler)
        => handler.Requests.Select(OperationOf).Where(static op => op is not null).Select(static op => op!).ToList();
}
