using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.YouTube;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The YouTube Music destination's wire behaviour.
/// <para>
/// The two things pinned here are the ones a wrong implementation gets silently wrong. The Data API
/// has no batch field for adding to a playlist, so a payload shaped like
/// <c>contentDetails.videoIds</c> is accepted and adds nothing - the sync reports success against a
/// playlist that never changed. And the read is the only thing a mirror deletes against, so a page
/// chain that stops early must read as unreadable rather than as a short playlist.
/// </para>
/// </summary>
public sealed class YouTubeMusicSyncProtocolTest
{
    [Theory]
    [InlineData("")]
    [InlineData("Updated")]
    public async Task MetadataUpdatePreservesTheExistingTitleAndSnippetProperties(string description)
    {
        var handler = new RecordingHandler((method, _) => (HttpStatusCode.OK, method == HttpMethod.Get
            ? "{\"items\":[{\"id\":\"pl-1\",\"snippet\":{\"title\":\"Existing title\",\"description\":\"Old\",\"defaultLanguage\":\"en\",\"localized\":{\"title\":\"Read only\"}}}]}"
            : "{\"id\":\"pl-1\"}"));
        var result = await Client(handler).WritePlaylistMetadataAsync("token", new("pl-1", "Source title", description, null), CancellationToken.None);
        Assert.Equal(PlaylistMetadataFieldStatus.Applied, result.DescriptionStatus);
        Assert.Equal(PlaylistMetadataFieldStatus.NotRequested, result.ArtworkStatus);
        var put = Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Put));
        Assert.Contains("playlists?part=snippet", put.Url);
        Assert.Contains("\"title\":\"Existing title\"", put.Body!);
        Assert.Contains("\"description\":\"" + description + "\"", put.Body!);
        Assert.Contains("defaultLanguage", put.Body!);
        Assert.DoesNotContain("localized", put.Body!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArtworkUsesPlaylistImagesMultipartUploadAndHeroType(bool existing)
    {
        var handler = new RecordingHandler((method, url) => (HttpStatusCode.OK,
            method == HttpMethod.Get ? existing ? "{\"items\":[{\"id\":\"img-1\",\"snippet\":{\"type\":\"hero\"}}]}" : "{\"items\":[]}"
            : "{\"id\":\"img-1\"}"));
        var result = await Client(handler).WritePlaylistMetadataAsync("token", new("pl-1", "Road", null,
            new(new byte[] { 1, 2, 3 }, "image/jpeg")), CancellationToken.None);
        Assert.Equal(PlaylistMetadataFieldStatus.Applied, result.ArtworkStatus);
        var upload = Assert.Single(handler.Requests.Where(r => r.Method == (existing ? HttpMethod.Put : HttpMethod.Post)));
        Assert.Contains("/upload/youtube/v3/playlistImages?uploadType=multipart&part=snippet", upload.Url);
        Assert.Contains("\"type\":\"hero\"", upload.Body!);
        Assert.Contains("\"playlistId\":\"pl-1\"", upload.Body!);
        Assert.Contains("image/jpeg", upload.Body!);
        if (existing) Assert.Contains("\"id\":\"img-1\"", upload.Body!);
    }

    [Fact]
    public async Task AnUnreadableArtworkListingIsNotTreatedAsAnEmptyListing()
    {
        var handler = new RecordingHandler((_, _) => (HttpStatusCode.Forbidden, "{\"error\":\"sensitive\"}"));
        var result = await Client(handler).WritePlaylistMetadataAsync("token", new("pl-1", "Road", null,
            new(new byte[] { 1 }, "image/jpeg")), CancellationToken.None);
        Assert.Equal(PlaylistMetadataFieldStatus.Failed, result.ArtworkStatus);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("sensitive", result.Message ?? "");
    }

    [Fact]
    public async Task AnIncompleteArtworkListingDoesNotCreateAnotherImage()
    {
        var handler = new RecordingHandler((method, _) => (HttpStatusCode.OK,
            method == HttpMethod.Get ? "{\"items\":[],\"nextPageToken\":\"page-2\"}" : "{\"id\":\"img-1\"}"));
        var result = await Client(handler).WritePlaylistMetadataAsync("token", new("pl-1", "Road", null,
            new(new byte[] { 1 }, "image/jpeg")), CancellationToken.None);
        Assert.Equal(PlaylistMetadataFieldStatus.Failed, result.ArtworkStatus);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DescriptionRejectionDoesNotSuppressArtwork()
    {
        var handler = new RecordingHandler((method, url) => url!.Contains("/playlists?", StringComparison.Ordinal)
            ? (HttpStatusCode.Forbidden, "{}")
            : (HttpStatusCode.OK, method == HttpMethod.Get ? "{\"items\":[]}" : "{\"id\":\"img-1\"}"));
        var result = await Client(handler).WritePlaylistMetadataAsync("token", new("pl-1", "Road", "Updated",
            new(new byte[] { 1 }, "image/jpeg")), CancellationToken.None);
        Assert.Equal(PlaylistMetadataFieldStatus.Failed, result.DescriptionStatus);
        Assert.Equal(PlaylistMetadataFieldStatus.Applied, result.ArtworkStatus);
    }

    private const string PlaylistItemsUrl = "https://www.googleapis.com/youtube/v3/playlistItems?part=snippet";

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpMethod, string?, (HttpStatusCode, string)> _respond;

        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

        public RecordingHandler(Func<HttpMethod, string?, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));

            var (status, content) = _respond(request.Method, request.RequestUri!.ToString());
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Pacing is a real one-second delay between adds in production; it is off here so the suite stays fast.</summary>
    private static YouTubeDataApiClient Client(RecordingHandler handler)
        => new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) })
        {
            AddPacing = TimeSpan.Zero,
        };

    private static YouTubeMusicPlaylistSyncTarget Target(RecordingHandler handler)
        => new(Client(handler), static _ => Task.FromResult<string?>("token"));

    private static string Page(IReadOnlyList<(string ItemId, string VideoId)> items, string? next)
    {
        var rows = string.Join(',', items.Select(item =>
            "{\"id\":\"" + item.ItemId
            + "\",\"snippet\":{\"title\":\"T\",\"resourceId\":{\"kind\":\"youtube#video\",\"videoId\":\""
            + item.VideoId + "\"}}}"));
        return next is null
            ? "{\"items\":[" + rows + "]}"
            : "{\"items\":[" + rows + "],\"nextPageToken\":\"" + next + "\"}";
    }

    [Fact]
    public async Task Add_SendsOneRequestPerVideoCarryingAResourceId()
    {
        // playlistItems.insert takes exactly one item. There is no batch field, so a payload that
        // listed several videos at once is accepted by the endpoint and adds nothing at all.
        var handler = new RecordingHandler(static (_, _) => (HttpStatusCode.OK, "{}"));

        var ok = await Client(handler)
            .AddPlaylistItemsAsync("token", "pl-1", new[] { "v1", "v2" }, CancellationToken.None);

        Assert.True(ok);
        var posts = handler.Requests.Where(static r => r.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, posts.Count);
        Assert.All(posts, post => Assert.Equal(PlaylistItemsUrl, post.Url));
        Assert.Contains("playlistId\":\"pl-1\"", posts[0].Body!, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"youtube#video\"", posts[0].Body!, StringComparison.Ordinal);
        Assert.Contains("\"videoId\":\"v1\"", posts[0].Body!, StringComparison.Ordinal);
        Assert.Contains("\"videoId\":\"v2\"", posts[1].Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("videoIds", posts[0].Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPlaylistItems_FollowsThePageTokenUntilTheWholeMembershipIsRead()
    {
        // The target asks for the whole membership, so maxResults is a target rather than a page
        // size. Reading only the first page would mirror against half a playlist and delete the rest.
        var handler = new RecordingHandler((_, url) => url!.Contains("pageToken=t2", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, Page(new[] { ("pi3", "v3"), ("pi4", "v4") }, next: null))
            : (HttpStatusCode.OK, Page(new[] { ("pi1", "v1"), ("pi2", "v2") }, next: "t2")));

        var items = await Client(handler)
            .GetPlaylistItemsAsync("token", "pl-1", int.MaxValue, cancellationToken: CancellationToken.None);

        Assert.NotNull(items);
        Assert.Equal(new[] { "v1", "v2", "v3", "v4" }, items!.Select(static item => item.VideoId));
        Assert.Contains(handler.Requests, r => r.Url.Contains("pageToken=t2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPlaylistItems_APageThatFailsAfterTheFirstIsUnreadableNotEmpty()
    {
        // Pages collected so far is a truncated membership. The engine cannot tell that apart from
        // a genuinely short playlist, and mirroring against it deletes every track that was missed.
        var handler = new RecordingHandler((_, url) => url!.Contains("pageToken=t2", StringComparison.Ordinal)
            ? (HttpStatusCode.InternalServerError, "{}")
            : (HttpStatusCode.OK, Page(new[] { ("pi1", "v1") }, next: "t2")));

        Assert.Null(await Client(handler)
            .GetPlaylistItemsAsync("token", "pl-1", int.MaxValue, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task ReadItemIds_ReturnsPlaylistItemIdsRatherThanVideoIds()
    {
        // The removal path addresses playlist ITEM ids; that is the only handle a delete accepts.
        // Returning video ids here would make every removal address the wrong thing.
        var handler = new RecordingHandler(static (_, _) =>
            (HttpStatusCode.OK, Page(new[] { ("pi1", "v1"), ("pi2", "v2") }, next: null)));

        var read = await Target(handler).ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.True(read.Success);
        Assert.Equal(new[] { "pi1", "pi2" }, read.ItemIds);
    }

    [Fact]
    public async Task WriteMembership_MirrorModeAddsOnlyTheVideoThePlaylistDoesNotHold()
    {
        // Mirror mode governs removal. YouTube permits duplicates, so re-sending ids the playlist
        // already holds appends a second copy of everything and the pass never converges.
        var handler = new RecordingHandler(static (method, _) => method == HttpMethod.Get
            ? (HttpStatusCode.OK, Page(new[] { ("pi1", "v1"), ("pi2", "v2") }, next: null))
            : (HttpStatusCode.OK, "{}"));

        var result = await Target(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Road", new[] { "v1", "v2", "v3" }, new[] { "pi1", "pi2" }, false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(0, result.RemovedCount);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("\"videoId\":\"v3\"", post.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, static r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task WriteMembership_FailsRatherThanMirroringAgainstAnUnreadablePlaylist()
    {
        // The membership has to be re-read in mirror mode even though the caller supplied one,
        // because what the caller holds is playlist item ids while the write diffs on video ids.
        // A read that fails must stop the pass rather than be treated as "the playlist is empty".
        var handler = new RecordingHandler(static (_, _) => (HttpStatusCode.InternalServerError, "{}"));

        var result = await Target(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Road", new[] { "v1" }, Array.Empty<string>(), false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(handler.Requests, static r => r.Method == HttpMethod.Delete);
        Assert.DoesNotContain(handler.Requests, static r => r.Method == HttpMethod.Post);
    }
}
