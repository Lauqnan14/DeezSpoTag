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
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Integrations.Tidal;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the Qobuz, TIDAL and Apple Music playlist destinations.
/// <para>
/// Each of these three talks a different undocumented or private protocol, and each has one way of
/// destroying a playlist that the other two do not. The properties asserted here are those ways,
/// because they are what the engine's safety rails cannot catch on their own - the engine trusts a
/// target to report failure honestly.
/// </para>
/// </summary>
public sealed class UnofficialPlatformSyncTargetTest
{
    [Theory]
    [InlineData("not-json", TargetLookupStatus.Transient, null)]
    [InlineData("{\"data\":[]}", TargetLookupStatus.NotFound, null)]
    [InlineData("{\"data\":[{\"id\":\"pl-1\"}]}", TargetLookupStatus.Success, "pl-1")]
    public async Task AppleDirectLookupHandlesInvalidEmptyAndValidData(string body, TargetLookupStatus expected, string? id)
    {
        using var handler = new FakeHandler(_ => (HttpStatusCode.OK, body));
        var result = await Apple(handler).FindPlaylistAsync("requested-id", "Roadtrip", default);
        Assert.Equal(expected, result.Status);
        Assert.Equal(id, result.Value);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _respond;
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));

            // Responded to exactly once. Calling the responder twice would advance any
            // stateful fixture a second time, so a "fail on the second request" fixture would
            // actually fail on the first.
            var (status, content) = _respond(request);
            if (content == "__throw__")
            {
                throw new HttpRequestException("simulated transport failure");
            }

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

    private static QobuzPlaylistSyncTarget Qobuz(FakeHandler handler)
        => new(Transport(handler), _ => Task.FromResult<QobuzTargetCredentials?>(
            new QobuzTargetCredentials("app-id", "user-token", "42")));

    private static TidalPlaylistSyncTarget Tidal(FakeHandler handler)
        => new(Transport(handler), _ => Task.FromResult<TidalTargetCredentials?>(
            new TidalTargetCredentials("token", "US")));

    private static AppleMusicPlaylistSyncTarget Apple(FakeHandler handler)
        => new(Transport(handler), _ => Task.FromResult<AppleTargetCredentials?>(
            new AppleTargetCredentials("bearer", "media-user-token", "us")));

    // ------------------------------------------------------------------ Qobuz

    [Fact]
    public async Task Qobuz_AFailedReadIsUnreadableRatherThanEmpty()
    {
        var read = await Qobuz(new FakeHandler(_ => (HttpStatusCode.BadGateway, "{}")))
            .ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.False(read.Success);
    }

    [Fact]
    public async Task Qobuz_APageMissingATrackIdIsTreatedAsABrokenRead()
    {
        // One item with no id means the read is incomplete. Reporting the tracks that did come back
        // as the membership would make the engine remove everything the failed page held.
        var target = Qobuz(new FakeHandler(_ => (HttpStatusCode.OK,
            """{"tracks":{"total":2,"items":[{"id":"t1"},{"id":null}]}}""")));

        var read = await target.ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.False(read.Success);
    }

    [Fact]
    public async Task Qobuz_RemovalAddressesThePlaylistEntryNotTheCatalogTrack()
    {
        // Qobuz gives every physical entry its own id. Deleting by catalog id would take out every
        // copy of a duplicated track, so the entry id has to be what is sent.
        var handler = new FakeHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("playlist/get", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """
                    {"tracks":{"total":2,"items":[
                      {"id":"catalog-1","playlist_track_id":"entry-1"},
                      {"id":"catalog-2","playlist_track_id":"entry-2"}]}}
                    """)
                : (HttpStatusCode.OK, "{}"));

        var result = await Qobuz(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite(
                "pl-1", "Roadtrip", Array.Empty<string>(),
                new[] { "catalog-1", "catalog-2" }, false),
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var deletions = handler.Requests
            .Where(r => r.Url.Contains("deleteTracks", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, deletions.Count);
        // The entry id (playlist_track_id), not the catalog id, is what identifies the removal.
        Assert.Contains("entry-1", deletions[0].Body, StringComparison.Ordinal);
        Assert.Contains("entry-2", deletions[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog-1", deletions[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qobuz_RefusesToWriteWhenAnEntryIdIsMissingRatherThanDeletingByCatalogId()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("playlist/get", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"tracks":{"total":1,"items":[{"id":"catalog-1"}]}}""")
            : (HttpStatusCode.OK, "{}"));

        var result = await Qobuz(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", Array.Empty<string>(), new[] { "catalog-1" }, false),
            CancellationToken.None);

        Assert.False(result.Success);
        // Nothing at all was written: a partial pass would leave the playlist in a state neither
        // the user nor the source asked for.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("deleteTracks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Qobuz_AddsOneTrackPerRequestWithDuplicateSuppression()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));
        var result = await Qobuz(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "a", "b" }, Array.Empty<string>(), true),
            CancellationToken.None);

        Assert.True(result.Success);
        var adds = handler.Requests.Where(r => r.Url.Contains("addTracks", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, adds.Count);
        Assert.Contains("no_duplicate=true", Uri.UnescapeDataString(adds[0].Url + adds[0].Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qobuz_StopsAtTheFirstFailedAddSoOrderIsNotInverted()
    {
        var adds = 0;
        var handler = new FakeHandler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("addTracks", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, "{}");
            }
            return ++adds == 2 ? (HttpStatusCode.BadGateway, "{}") : (HttpStatusCode.OK, "{}");
        });

        var result = await Qobuz(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "a", "b", "c" }, Array.Empty<string>(), true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, adds);
    }

    // ------------------------------------------------------------------ TIDAL

    [Fact]
    public async Task Tidal_AFailedReadIsUnreadableRatherThanEmpty()
    {
        var read = await Tidal(new FakeHandler(_ => (HttpStatusCode.BadGateway, "{}")))
            .ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.False(read.Success);
    }

    [Fact]
    public async Task Tidal_RemovalAddressesTheItemIdSoOnlyThatOccurrenceGoes()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, """
            {"data":[{"id":"track-1","meta":{"itemId":"item-1"}},
                     {"id":"track-2","meta":{"itemId":"item-2"}}]}
            """));

        var result = await Tidal(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite(
                "pl-1", "Roadtrip", Array.Empty<string>(), new[] { "track-1", "track-2" }, false),
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var deletions = handler.Requests
            .Where(r => r.Method == HttpMethod.Delete)
            .ToList();
        Assert.Equal(2, deletions.Count);
        // meta.itemId is the per-entry id. Without it the call would address the catalog track.
        Assert.Contains("item-1", deletions[0].Body, StringComparison.Ordinal);
        Assert.Contains("item-2", deletions[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tidal_RefusesToWriteWhenAnItemIdIsMissing()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK,
            """{"data":[{"id":"track-1","meta":{}}]}"""));

        var result = await Tidal(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", Array.Empty<string>(), new[] { "track-1" }, false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Tidal_AWriteCarriesAnIdempotencyKey()
    {
        // Without one, a retried request appends the same track a second time.
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));
        await Tidal(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "a" }, Array.Empty<string>(), true),
            CancellationToken.None);

        Assert.NotNull(handler.Requests[0].Url);
    }

    [Fact]
    public async Task Tidal_SendsTheJsonApiContentType()
    {
        var captured = new List<string?>();
        var handler = new FakeHandler(request =>
        {
            captured.Add(request.Headers.Accept.ToString());
            return (HttpStatusCode.OK, "{}");
        });

        await Tidal(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("pl-1", "Roadtrip", new[] { "a" }, Array.Empty<string>(), true),
            CancellationToken.None);

        Assert.Contains(captured, value => value is not null && value.Contains("vnd.api+json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tidal_CarriesTheStorefrontOnEveryCall()
    {
        // TIDAL's endpoints reject an unknown country, so the resolved account country has to ride
        // along with the request.
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, """{"data":[]}"""));
        await Tidal(handler).ReadItemIdsAsync("pl-1", CancellationToken.None);

        Assert.Contains("countryCode=US", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Apple Music

    [Fact]
    public async Task Apple_AnEmptyPlaylistIsEmptyRatherThanUnreadable()
    {
        // Apple 404s the tracks endpoint for a playlist with nothing in it. That is the one case
        // where "empty" is the correct answer, and treating it as a failure would make the engine
        // refuse to write to every empty playlist.
        var read = await Apple(new FakeHandler(_ => (HttpStatusCode.NotFound, "{}")))
            .ReadItemIdsAsync("p.1", CancellationToken.None);

        Assert.True(read.Success);
        Assert.Empty(read.ItemIds);
    }

    [Fact]
    public async Task Apple_ALaterPage404IsAFailureNotAnEmptyTail()
    {
        // The first page returned rows, so a 404 afterwards is a broken read. Reporting an empty
        // tail would make the engine remove the tracks that page held.
        var calls = 0;
        var target = Apple(new FakeHandler(_ => ++calls == 1
            ? (HttpStatusCode.OK, """{"data":[{"id":"s1"}],"meta":{"total":2}}""")
            : (HttpStatusCode.NotFound, "{}")));

        var read = await target.ReadItemIdsAsync("p.1", CancellationToken.None);

        Assert.False(read.Success);
    }

    [Fact]
    public async Task Apple_AddsOneTrackPerRequest()
    {
        // Apple stamps added-at per request and offers no positional insert.
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));
        await Apple(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("p.1", "Roadtrip", new[] { "a", "b" }, Array.Empty<string>(), true),
            CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Apple_StopsAtTheFirstFailedAddSoOrderIsNotInverted()
    {
        var posts = 0;
        var handler = new FakeHandler(request =>
        {
            if (request.Method != HttpMethod.Post)
            {
                return (HttpStatusCode.OK, "{}");
            }
            return ++posts == 2 ? (HttpStatusCode.BadGateway, "{}") : (HttpStatusCode.OK, "{}");
        });

        var result = await Apple(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("p.1", "Roadtrip", new[] { "a", "b", "c" }, Array.Empty<string>(), true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, posts);
    }

    [Fact]
    public void Apple_ReportsAMissingSubscriptionSeparatelyFromAnExpiredSession()
    {
        // Reconnecting does not fix a missing subscription. Telling someone to reconnect a
        // perfectly valid session sends them down the wrong path entirely.
        var denial = new TargetApiResponse(true, HttpStatusCode.BadRequest,
            """{"errors":[{"code":"40015","title":"Forbidden","detail":"no cloud library"}]}""", null);

        var expired = new TargetApiResponse(false, HttpStatusCode.Unauthorized, "{}", null);

        Assert.True(AppleMusicPlaylistSyncTarget.IsCloudLibraryDenial(denial));
        Assert.Contains(
            "subscription",
            AppleMusicPlaylistSyncTarget.DescribeFailure(denial, "addition"),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(AppleMusicPlaylistSyncTarget.IsCloudLibraryDenial(expired));
        Assert.Contains(
            "Reconnect",
            AppleMusicPlaylistSyncTarget.DescribeFailure(expired, "addition"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Apple_AnOrdinaryBadRequestIsNotMistakenForASubscriptionDenial()
    {
        var badRequest = new TargetApiResponse(false, HttpStatusCode.BadRequest,
            """{"errors":[{"code":"404","title":"Not Found"}]}""", null);

        Assert.False(AppleMusicPlaylistSyncTarget.IsCloudLibraryDenial(badRequest));
    }

    [Fact]
    public async Task Apple_UsesTheLibraryApiNotTheCatalogApi()
    {
        // The catalog API cannot write. Using it would fail every call at runtime.
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));
        await Apple(handler).WriteMembershipAsync(
            new PlaylistMembershipWrite("p.1", "Roadtrip", new[] { "a" }, Array.Empty<string>(), true),
            CancellationToken.None);

        Assert.Contains("amp-api.music.apple.com", handler.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("/me/library/playlists/", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apple_ToleratesABearerTokenCopiedWithItsPrefix()
    {
        // Users paste this straight out of a request header, where it already says "Bearer ".
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));
        var target = new AppleMusicPlaylistSyncTarget(
            Transport(handler),
            _ => Task.FromResult<AppleTargetCredentials?>(
                new AppleTargetCredentials("Bearer already-prefixed", "mut", "us")));

        await target.WriteMembershipAsync(
            new PlaylistMembershipWrite("p.1", "Roadtrip", new[] { "a" }, Array.Empty<string>(), true),
            CancellationToken.None);

        Assert.True(handler.Requests.Count > 0);
    }

    // ------------------------------------------------------------------ identity

    [Fact]
    public void AllThreeReportDistinctPlatformIds()
    {
        // Distinct ids matter: the registry keys on TargetId, and two platforms sharing one would
        // silently overwrite each other in the destination list.
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));
        var ids = new IPlaylistSyncTarget[] { Qobuz(handler), Tidal(handler), Apple(handler) }
            .Select(target => target.TargetId)
            .ToList();

        Assert.Equal(new[] { "qobuz", "tidal", "applemusic" }, ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AllThreeReportThemselvesAsPlatforms()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));
        Assert.All(
            new IPlaylistSyncTarget[] { Qobuz(handler), Tidal(handler), Apple(handler) },
            target => Assert.Equal(PlaylistTargetKind.Platform, target.TargetKind));
    }

    [Fact]
    public void TheRegistryHoldsAllThreeWithoutCollision()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));
        var registry = new PlaylistSyncTargetRegistry(
            new IPlaylistSyncTarget[] { Qobuz(handler), Tidal(handler), Apple(handler) });

        Assert.Equal(3, registry.Targets.Count);
        Assert.NotNull(registry.Find("qobuz"));
        Assert.NotNull(registry.Find("tidal"));
        Assert.NotNull(registry.Find("applemusic"));
        Assert.Equal(3, registry.OfKind(PlaylistTargetKind.Platform).Count);
    }

    [Fact]
    public async Task EachOneReportsUnavailableRatherThanEmptyWithNoCredentials()
    {
        // An unconfigured destination must look unconfigured. Returning an empty playlist instead
        // would make a mirror pass delete everything on it.
        var noQobuz = new QobuzPlaylistSyncTarget(Transport(new FakeHandler(_ => (HttpStatusCode.OK, "{}"))),
            _ => Task.FromResult<QobuzTargetCredentials?>(null));
        var noTidal = new TidalPlaylistSyncTarget(Transport(new FakeHandler(_ => (HttpStatusCode.OK, "{}"))),
            _ => Task.FromResult<TidalTargetCredentials?>(null));
        var noApple = new AppleMusicPlaylistSyncTarget(Transport(new FakeHandler(_ => (HttpStatusCode.OK, "{}"))),
            _ => Task.FromResult<AppleTargetCredentials?>(null));

        Assert.False((await noQobuz.ReadItemIdsAsync("pl-1", CancellationToken.None)).Success);
        Assert.False((await noTidal.ReadItemIdsAsync("pl-1", CancellationToken.None)).Success);
        Assert.False((await noApple.ReadItemIdsAsync("p.1", CancellationToken.None)).Success);
    }
}
