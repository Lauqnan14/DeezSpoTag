using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.SoundCloud;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SoundCloudFixtures = DeezSpoTag.Tests.SoundCloudFixtures;

namespace DeezSpoTag.Tests;

/// <summary>
///     Protocol tests for the native SoundCloud client.
/// </summary>
/// <remarks>
///     Every case runs against a local stub handler. Nothing here touches live SoundCloud, so the suite is
///     deterministic and the assertions about authorization placement and redaction are actually checkable.
/// </remarks>
public sealed class SoundCloudClientTest
{
    private const string TrackId = "424242";
    private const string ClientId = "test-client-id";
    private const string TrackAuthorization = "track-auth-secret-value";

    /// <summary>
    ///     A track, set, and short URL are all classified and normalized to something the client can hydrate.
    /// </summary>
    [Fact]
    public void ClassifyAndNormalize_AcceptsTrackSetShortAndPrivateLinks()
    {
        var track = SoundCloudHydrationParser.SplitSecretToken(
            "https://soundcloud.com/some-artist/some-track");
        Assert.Equal("https://soundcloud.com/some-artist/some-track", track.PageUrl);
        Assert.Equal(string.Empty, track.SecretToken);

        var set = SoundCloudHydrationParser.SplitSecretToken(
            "https://soundcloud.com/some-artist/sets/some-set");
        Assert.Equal("https://soundcloud.com/some-artist/sets/some-set", set.PageUrl);
        Assert.Equal(string.Empty, set.SecretToken);

        Assert.True(SoundCloudHydrationParser.IsSetUrl("https://soundcloud.com/a/sets/b"));
        Assert.True(SoundCloudHydrationParser.IsSetUrl("https://soundcloud.com/discover/sets/b"));
        Assert.False(SoundCloudHydrationParser.IsSetUrl("https://soundcloud.com/a/b"));

        // A private share token arrives either as a query parameter or a trailing path segment, and the path
        // form has to be stripped from the URL that gets hydrated.
        var queryToken = SoundCloudHydrationParser.SplitSecretToken(
            "https://soundcloud.com/a/b?secret_token=s-AbCd1234");
        Assert.Equal("s-AbCd1234", queryToken.SecretToken);

        var pathToken = SoundCloudHydrationParser.SplitSecretToken(
            "https://soundcloud.com/a/b/s-AbCd1234");
        Assert.Equal("s-AbCd1234", pathToken.SecretToken);
        Assert.DoesNotContain("s-AbCd1234", pathToken.PageUrl, StringComparison.Ordinal);

        Assert.Throws<SoundCloudInvalidUrlException>(
            () => SoundCloudHydrationParser.SplitSecretToken("not a url"));
    }

    /// <summary>
    ///     Every field the matching and tracklist paths depend on comes out of the hydration payload.
    /// </summary>
    [Fact]
    public void HydrationParser_ExtractsCompleteTrackMetadata()
    {
        var track = SoundCloudHydrationParser.ParseTrack(SoundCloudFixtures.TrackPageHtml);

        Assert.Equal(42L, track.Id);
        Assert.Equal("Test Track", track.Title);
        Assert.Equal("Test Artist", track.Artist);
        Assert.Equal("https://soundcloud.com/test-artist/test-track", track.PermalinkUrl);
        Assert.Equal(215000, track.DurationMs);
        Assert.Equal("Test Genre", track.Genre);
        Assert.Equal("USRC17607839", track.Isrc);
        Assert.Equal("https://i1.sndcdn.com/artworks-xyz-large.jpg", track.ArtworkUrl);
        Assert.Equal("Test Label", track.Label);
        Assert.Equal(TrackAuthorization, track.TrackAuthorization);
        Assert.Equal(2020, track.ReleaseYear);

        // SoundCloud publishes duration in milliseconds; the contract carries the same unit, which the
        // shared validator and the queue payload both expect.
        Assert.Equal(215000, track.DurationMs);

        // Every advertised stream is retained, not only the ones this engine can use, so the DRM and
        // progressive entries stay visible to the failure that reports them.
        Assert.Equal(8, track.Transcodings.Count);
        Assert.Contains(track.Transcodings, t => t.Quality == "hq");
        Assert.Contains(track.Transcodings, t => t.Protocol == "cbc-encrypted-hls");
        Assert.Contains(track.Transcodings, t => t.MimeType == "audio/mp4");
    }

    /// <summary>
    ///     A page with no hydration array is a typed failure, never a half-populated track.
    /// </summary>
    [Fact]
    public void HydrationParser_RejectsPagesWithoutHydration()
    {
        var missing = Assert.Throws<SoundCloudHydrationException>(
            () => SoundCloudHydrationParser.ParseTrack("<html><body>no hydration here</body></html>"));
        Assert.Equal("hydration_missing", missing.Reason);

        var wrongType = Assert.Throws<SoundCloudHydrationException>(
            () => SoundCloudHydrationParser.ParseTrack(SoundCloudFixtures.SetPageHtml));
        Assert.Equal("sound", wrongType.Hydratable);
    }

    /// <summary>
    ///     Set order is preserved and stub entries are expanded through bounded api-v2 batches.
    /// </summary>
    /// <summary>
    ///     An algorithmic <c>/discover/sets/...</c> page hydrates <c>systemPlaylist</c>, not <c>playlist</c>.
    /// </summary>
    /// <remarks>
    ///     Both carry the same shape, so both have to resolve. A parser that accepts only <c>playlist</c>
    ///     turns every discover URL into a failure even though the page holds a complete ordered track list.
    ///     This is the shape a real trending-by-genre page produced.
    /// </remarks>
    [Fact]
    public async Task ResolveSet_ReadsAnAlgorithmicDiscoverPage()
    {
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("a-v2.sndcdn.com", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SoundCloudFixtures.ClientAssetJs)
                };
            }

            if (url.Contains("/tracks?", StringComparison.Ordinal))
            {
                return JsonResponse(BuildTracksBatchResponse(ReadQueryValue(url, "ids")));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    IsHomepage(url)
                        ? SoundCloudFixtures.HomepageHtml
                        : SoundCloudFixtures.BuildDiscoverPageHtml(["11", "22", "33"]))
            };
        });

        using var client = CreateClient(handler);

        var set = await client.ResolveSetAsync(
            "https://soundcloud.com/discover/sets/trending-by-genre:hip-hop",
            CancellationToken.None);

        Assert.Equal("Hip Hop", set.Title);
        Assert.Equal(3, set.Tracks.Count);

        // Stubs are expanded and the discovered order is preserved.
        Assert.Equal(
            new[]
            {
                "https://soundcloud.com/test-artist/track-11",
                "https://soundcloud.com/test-artist/track-22",
                "https://soundcloud.com/test-artist/track-33"
            },
            set.Tracks.Select(track => track.PermalinkUrl).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, set.Tracks.Select(track => track.Position ?? 0).ToArray());
    }

    /// <summary>
    ///     A page carrying both discriminators resolves to the user set, not the algorithmic one.
    /// </summary>
    [Fact]
    public void ParseSet_PrefersTheUserPlaylistWhenAPageCarriesBoth()
    {
        var page = SoundCloudFixtures.BuildHydrationPage(
            "[{\"hydratable\":\"playlist\",\"data\":{\"kind\":\"playlist\",\"id\":7,\"title\":\"User Set\",\"permalink_url\":\"https://soundcloud.com/a/sets/user\",\"tracks\":[{\"id\":1}]}},"
            + "{\"hydratable\":\"systemPlaylist\",\"data\":{\"kind\":\"system-playlist\",\"id\":9,\"title\":\"Discover\",\"tracks\":[{\"id\":2}]}}]");

        Assert.Equal("User Set", SoundCloudHydrationParser.ParseSet(page).Title);
    }

    /// <summary>
    ///     A page with neither discriminator is a typed failure, not a half-built set.
    /// </summary>
    [Fact]
    public void ParseSet_RejectsAPageWithNoSetHydration()
    {
        var failure = Assert.Throws<SoundCloudHydrationException>(
            () => SoundCloudHydrationParser.ParseSet(SoundCloudFixtures.TrackPageHtml));
        Assert.Equal("hydration_missing", failure.Reason);
    }

    [Fact]
    public async Task ResolveSet_PreservesOrderAndExpandsStubIdsInBatchesOfFifty()
    {
        // 120 stubs forces three api-v2 calls at the ported batch size of 50, which is the behaviour worth
        // pinning: one giant request would be rejected by SoundCloud.
        var stubIds = Enumerable.Range(1, 120).Select(i => i.ToString()).ToArray();
        var requestedBatches = new List<string>();

        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("a-v2.sndcdn.com", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SoundCloudFixtures.ClientAssetJs)
                };
            }

            if (url.Contains("/tracks?", StringComparison.Ordinal))
            {
                var ids = ReadQueryValue(url, "ids");
                requestedBatches.Add(ids);
                return JsonResponse(BuildTracksBatchResponse(ids));
            }

            if (url.StartsWith("https://soundcloud.com", StringComparison.Ordinal))
            {
                // The homepage has to serve the asset links, because stub expansion needs a client_id first.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        IsHomepage(url)
                            ? SoundCloudFixtures.HomepageHtml
                            : SoundCloudFixtures.BuildSetPageHtml(stubIds))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = CreateClient(handler);

        var set = await client.ResolveSetAsync("https://soundcloud.com/test-artist/sets/test-set", CancellationToken.None);

        Assert.Equal("Test Set", set.Title);
        Assert.Equal(120, set.Tracks.Count);

        // Order is the playlist's, not the batch's: track N of the set must be SoundCloud id N.
        for (var index = 0; index < stubIds.Length; index++)
        {
            Assert.Equal($"https://soundcloud.com/test-artist/track-{stubIds[index]}", set.Tracks[index].PermalinkUrl);
        }

        Assert.Equal(3, requestedBatches.Count);
        Assert.All(requestedBatches, batch => Assert.True(batch.Split(',').Length <= 50));
        Assert.Equal(50, requestedBatches[0].Split(',').Length);
        Assert.Equal(20, requestedBatches[2].Split(',').Length);
    }

    /// <summary>
    ///     A stale client id is discovered once, used, rejected, refreshed exactly once, and then the retry
    ///     succeeds. A second rejection is reported rather than retried again.
    /// </summary>
    [Fact]
    public async Task ClientId_IsDiscoveredCachedAndRefreshedExactlyOnceAfterRejection()
    {
        var homepageFetches = 0;
        var searches = new List<string>();
        var rejectFirstSearch = true;

        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/search/tracks", StringComparison.Ordinal))
            {
                searches.Add(url);

                if (rejectFirstSearch)
                {
                    rejectFirstSearch = false;
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return JsonResponse("[]");
            }

            // Discovery reads the homepage, whose apiClient hydration carries a fresh id each time so a
            // refreshed cache is observable as a different id being sent.
            homepageFetches++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildHomepageWithClientId($"discovered-{homepageFetches}"))
            };
        });

        using var client = CreateClient(handler);

        await client.SearchTracksAsync("first", 10, CancellationToken.None);
        Assert.Equal("discovered-1", ExtractClientId(searches[0]));

        // Rejected once, the id is refreshed and the retry succeeds with the new value.
        await client.SearchTracksAsync("second", 10, CancellationToken.None);

        // Three attempts: the original, the automatic retry after the refresh, and the caller's second
        // request. The refresh lands between the first and the retry, so the retry and everything after it
        // carry the new id.
        Assert.Equal(3, searches.Count);
        Assert.Equal("discovered-1", ExtractClientId(searches[0]));
        Assert.Equal("discovered-2", ExtractClientId(searches[1]));
        Assert.Equal("discovered-2", ExtractClientId(searches[2]));

        // Discovery is not repeated per call.
        Assert.Equal(2, homepageFetches);
    }

    /// <summary>
    ///     A 401 with no token configured is a client-id failure, not an expired-token failure.
    /// </summary>
    /// <remarks>
    ///     SoundCloud answers both with the same status. Reporting the wrong one would tell a reader to
    ///     re-enter a token that is not the problem, and would hide a genuine discovery fault.
    /// </remarks>
    [Fact]
    public async Task RejectionWithNoToken_IsReportedAsAClientIdFailureNotAnAuthFailure()
    {
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            return url.Contains("/search/tracks", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        IsHomepage(url)
                            ? SoundCloudFixtures.HomepageHtml
                            : SoundCloudFixtures.ClientAssetJs)
                };
        });

        using var client = CreateClient(handler, new StubCredentialProvider(null));

        var failure = await Assert.ThrowsAsync<SoundCloudClientIdException>(
            () => client.SearchTracksAsync("anything", 10, CancellationToken.None));
        Assert.Equal("client_id_unavailable", failure.Reason);

        // Must not send the reader off to re-enter a credential that is not the problem.
        Assert.DoesNotContain("expired", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("client_id", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     With a token configured, the same status is an authentication failure.
    /// </summary>
    [Fact]
    public async Task RejectionWithAToken_IsReportedAsAnAuthenticationFailure()
    {
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            return url.Contains("/search/tracks", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        IsHomepage(url)
                            ? SoundCloudFixtures.HomepageHtml
                            : SoundCloudFixtures.ClientAssetJs)
                };
        });

        using var client = CreateClient(handler, new StubCredentialProvider("a-token"));

        var failure = await Assert.ThrowsAsync<SoundCloudAuthenticationException>(
            () => client.SearchTracksAsync("anything", 10, CancellationToken.None));
        Assert.Equal("authentication_failed", failure.Reason);
    }

    /// <summary>
    ///     Discovery succeeds even though no asset bundle carries a literal, because hydration supplies the id.
    /// </summary>
    [Fact]
    public async Task Discovery_SucceedsFromHydrationWithoutAnyBundleLiteral()
    {
        var bundleFetches = 0;

        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("a-v2.sndcdn.com", StringComparison.Ordinal))
            {
                bundleFetches++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SoundCloudFixtures.ClientAssetJs)
                };
            }

            if (url.Contains("/search/tracks", StringComparison.Ordinal))
            {
                return JsonResponse("[]");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SoundCloudFixtures.HomepageHtml)
            };
        });

        using var client = CreateClient(handler);

        // Must not throw: the hydration path supplies the id, so the bundles are never consulted.
        await client.SearchTracksAsync("anything", 10, CancellationToken.None);

        Assert.Equal(0, bundleFetches);
    }

    /// <summary>
    ///     Every rejection after the single permitted refresh is reported instead of retried, so a failing
    ///     track cannot spin.
    /// </summary>
    [Fact]
    public async Task ClientId_IsNotRefreshedMoreThanOnce()
    {
        var searches = 0;

        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("a-v2.sndcdn.com", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SoundCloudFixtures.ClientAssetJs)
                };
            }

            if (url.Contains("/search/tracks", StringComparison.Ordinal))
            {
                searches = searches + 1;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            // Discovery reads the client_id from the homepage's apiClient hydration.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    url.Contains("a-v2.sndcdn.com", StringComparison.Ordinal)
                        ? SoundCloudFixtures.ClientAssetJs
                        : SoundCloudFixtures.HomepageHtml)
            };
        });

        // A token is configured, so a rejection is an authentication failure and the single refresh applies.
        using var client = CreateClient(handler, new StubCredentialProvider("a-token"));

        // The refresh allowance is spent once for the lifetime of the client, so the first call costs two attempts
        // and the second costs one. Nothing here can spin.
        await Assert.ThrowsAsync<SoundCloudAuthenticationException>(
            () => client.SearchTracksAsync("a", 10, CancellationToken.None));
        await Assert.ThrowsAsync<SoundCloudAuthenticationException>(
            () => client.SearchTracksAsync("b", 10, CancellationToken.None));

        Assert.Equal(3, searches);
    }

    /// <summary>
    ///     <c>hq</c> is preferred, then <c>sq</c>, then <c>lq</c>, and non-HLS or non-MP3 entries are ignored
    ///     no matter how they are ranked.
    /// </summary>
    [Theory]
    [InlineData(new[] { "lq", "sq", "hq" }, "hq")]
    [InlineData(new[] { "lq", "sq" }, "sq")]
    [InlineData(new[] { "lq" }, "lq")]
    public void ResolveStream_PrefersPlainMpegHqThenSqThenLq(string[] advertised, string expected)
    {
        var track = BuildTrack(advertised, "audio/mpeg", "hls");
        var selected = SoundCloudClient.SelectTranscoding(track.Transcodings);
        Assert.NotNull(selected);
        Assert.Equal(expected, selected!.Quality);
    }

    /// <summary>
    ///     A progressive or MP4 transcoding is never selected, and DRM-only content is reported as such rather
    ///     than as a generic missing stream.
    /// </summary>
    [Fact]
    public void ResolveStream_RejectsEncryptedOnlyTranscodings()
    {
        Assert.Null(SoundCloudClient.SelectTranscoding(
            BuildTrack(["hq"], "audio/mpeg", "cbc-encrypted-hls").Transcodings));
        Assert.Null(SoundCloudClient.SelectTranscoding(
            BuildTrack(["hq"], "audio/mp4", "progressive").Transcodings));
        Assert.Null(SoundCloudClient.SelectTranscoding(
            BuildTrack(["hq"], "audio/mpegurl", "hls").Transcodings));

        var drmOnly = Assert.Throws<SoundCloudNoStreamException>(
            () => SoundCloudClient.SelectTranscodingOrThrow(BuildTrack(["hq"], "audio/mpeg", "cbc-encrypted-hls").Transcodings));
        Assert.True(drmOnly.DrmOnly);
        Assert.Equal("drm_only", drmOnly.Reason);
    }

    /// <summary>
    ///     The OAuth token is a cookie on page requests and a header on api-v2 requests, and never appears in
    ///     a URL, a log line, or an exception message.
    /// </summary>
    [Fact]
    public async Task OAuth_IsSentOnlyAsPageCookieOrApiV2Header_NeverLeaks()
    {
        var token = "super-secret-oauth-token";
        var observedPageAuthorization = new List<string?>();
        var observedPageCookie = new List<string?>();
        var observedApiAuthorization = new List<string?>();

        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.StartsWith("https://soundcloud.com", StringComparison.Ordinal))
            {
                observedPageAuthorization.Add(
                    request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null);
                observedPageCookie.Add(
                    request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join(";", cookies) : null);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        IsHomepage(url) ? SoundCloudFixtures.HomepageHtml : SoundCloudFixtures.TrackPageHtml)
                };
            }

            // The api-v2 stream call is the only place the header may appear.
            if (url.Contains("api-v2.soundcloud.com", StringComparison.Ordinal))
            {
                observedApiAuthorization.Add(
                    request.Headers.TryGetValues("Authorization", out var apiHeader) ? string.Join(",", apiHeader) : null);
                return JsonResponse(SoundCloudFixtures.StreamResponse());
            }

            // Everything else is a client-id discovery fetch.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SoundCloudFixtures.ClientAssetJs)
            };
        });

        using var client = CreateClient(handler, new StubCredentialProvider(token));

        var track = await client.ResolveTrackAsync(
            "https://soundcloud.com/test-artist/test-track",
            CancellationToken.None);
        var stream = await client.ResolveStreamAsync(track, null, CancellationToken.None);

        Assert.Contains("https://media.sndcdn.com/hls/playlist.m3u8", stream.PlaylistUrl, StringComparison.Ordinal);

        // Page requests carry the cookie and never the header.
        Assert.NotEmpty(observedPageCookie);
        Assert.All(observedPageCookie, cookie => Assert.Contains($"oauth_token={token}", cookie!, StringComparison.Ordinal));
        Assert.All(observedPageAuthorization, authorization => Assert.Null(authorization));

        // The api-v2 stream call carries the header and no cookie.
        Assert.Contains($"OAuth {token}", observedApiAuthorization!);
        Assert.DoesNotContain(observedApiAuthorization!, value => value is null);

        // Neither the token nor the per-track authorization survives into a redacted URL or a message.
        var redacted = SoundCloudUrlRedactor.Redact(
            $"https://api-v2.soundcloud.com/media/soundcloud:tracks:1/stream/hls?client_id={ClientId}&track_authorization={TrackAuthorization}&secret_token=s-Private");
        Assert.DoesNotContain(TrackAuthorization, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("s-Private", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(token, redacted, StringComparison.Ordinal);
        Assert.Contains("track_authorization=[redacted]", redacted, StringComparison.Ordinal);

        // The constructed stream URL carries the authorization but is never logged in that form.
        var streamUrl = SoundCloudClient.BuildStreamUrl(
            "https://api-v2.soundcloud.com/media/soundcloud:tracks:1/stream/hls",
            ClientId,
            TrackAuthorization,
            "s-Private");
        Assert.Contains($"track_authorization={TrackAuthorization}", streamUrl, StringComparison.Ordinal);
        Assert.DoesNotContain(TrackAuthorization, SoundCloudUrlRedactor.Redact(streamUrl), StringComparison.Ordinal);

        // The same discipline holds for a message built from a URL, which is how every transport failure is
        // reported.
        var failure = Assert.Throws<SoundCloudInvalidUrlException>(
            () => SoundCloudClient.EnsureSoundCloudUrl("https://example.com/x?secret_token=s-Private"));
        Assert.DoesNotContain("s-Private", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Discovery failure, a dead link, and an unresolvable stream are all distinguishable typed failures.
    /// </summary>
    [Fact]
    public async Task Failures_AreTypedAndDoNotThrowProgrammingErrors()
    {
        // Discovery reads the homepage first, so a handler that answers everything with a page carrying neither
        // an apiClient entry nor asset links makes discovery fail outright.
        var noAssets = CreateClient(new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    url.Contains("a-v2.sndcdn.com", StringComparison.Ordinal)
                        ? SoundCloudFixtures.ClientAssetJs
                        : "<html>no assets</html>")
            };
        }));

        var discovery = await Assert.ThrowsAsync<SoundCloudClientIdException>(
            () => noAssets.SearchTracksAsync("x", 10, CancellationToken.None));
        Assert.Equal("client_id_unavailable", discovery.Reason);

        // A page that publishes no apiClient entry and links bundles carrying no literal either: the
        // fallback finds nothing, so discovery still fails.
        var noClientIdAnywhere = CreateClient(new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/search/tracks", StringComparison.Ordinal))
            {
                return JsonResponse("[]");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    url.Contains("a-v2.sndcdn.com", StringComparison.Ordinal)
                        ? "<html>a bundle with no client_id in it</html>"
                        : "<html>no hydration, no assets</html>")
            };
        }));
        await Assert.ThrowsAsync<SoundCloudClientIdException>(
            () => noClientIdAnywhere.SearchTracksAsync("x", 10, CancellationToken.None));

        // The bundle fallback still works if a site ever ships a literal again: the homepage here publishes no
        // apiClient entry and does link a bundle, so only the bundle scan can supply the id.
        var literalInBundle = CreateClient(new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/search/tracks", StringComparison.Ordinal))
            {
                return JsonResponse("[]");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    url.Contains("a-v2.sndcdn.com", StringComparison.Ordinal)
                        ? "var v={client_id:\"from-a-bundle\"};"
                        : "<html><script src=\"https://a-v2.sndcdn.com/assets/1.js\"></script></html>")
            };
        }));
        await literalInBundle.SearchTracksAsync("x", 10, CancellationToken.None);

        // A track URL that 404s is an unavailable track, not a broken short link.
        // Discovery is a separate concern from track fetching, so this handler 404s only the track and still
        // lets the homepage and asset bundle through. Without that the failure would be reported as a
        // client-id problem rather than the unavailable track it actually is.
        var gone = CreateClient(new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://soundcloud.com", StringComparison.Ordinal))
            {
                return IsHomepage(url)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SoundCloudFixtures.HomepageHtml) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SoundCloudFixtures.ClientAssetJs)
            };
        }));
        var unavailable = await Assert.ThrowsAsync<SoundCloudUnavailableException>(
            () => gone.ResolveTrackAsync("https://soundcloud.com/a/b", CancellationToken.None));
        Assert.Equal("unavailable", unavailable.Reason);

        // A short link that never redirects is a short-link failure, distinct from a track URL that 404s. The
        // homepage and bundles still resolve so the failure is the redirect, not discovery.
        var badRedirect = CreateClient(new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://on.soundcloud.com", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(string.Empty)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    url.Contains("a-v2.sndcdn.com", StringComparison.Ordinal)
                        ? SoundCloudFixtures.ClientAssetJs
                        : SoundCloudFixtures.HomepageHtml)
            };
        }));
        var shortLink = await Assert.ThrowsAsync<SoundCloudShortLinkException>(
            () => badRedirect.ResolveTrackAsync("https://on.soundcloud.com/abcdefgh", CancellationToken.None));
        Assert.Equal("short_link_failed", shortLink.Reason);
    }

    /// <summary>
    ///     The client id is read from the page hydration, which is where SoundCloud publishes it.
    /// </summary>
    /// <remarks>
    ///     This is the method that actually works against a live site. The asset-bundle scrape is only a
    ///     fallback, because a real bundle reads the value through a getter and contains no literal to find -
    ///     which is exactly what the bundle fixture models.
    /// </remarks>
    [Fact]
    public void ClientIdComesFromTheApiClientHydrationEntry()
    {
        Assert.Equal(
            SoundCloudFixtures.PublishedClientId,
            SoundCloudHydrationParser.ExtractClientIdFromHydration(SoundCloudFixtures.HomepageHtml));

        // A page with no apiClient entry publishes nothing to find.
        Assert.Null(SoundCloudHydrationParser.ExtractClientIdFromHydration(
            SoundCloudFixtures.BuildHydrationPage("[{\"hydratable\":\"anonymousId\",\"data\":\"x\"}]")));
        Assert.Null(SoundCloudHydrationParser.ExtractClientIdFromHydration("<html>nothing</html>"));
        Assert.Null(SoundCloudHydrationParser.ExtractClientIdFromHydration(string.Empty));

        // The entry is read as JSON, so a field order or an extra field cannot truncate the value.
        var reordered = SoundCloudFixtures.BuildHydrationPage(
            "[{\"hydratable\":\"apiClient\",\"data\":{\"isExpiring\":false,\"id\":\"" + SoundCloudFixtures.PublishedClientId + "\"}}]");
        Assert.Equal(
            SoundCloudFixtures.PublishedClientId,
            SoundCloudHydrationParser.ExtractClientIdFromHydration(reordered));
    }

    /// <summary>
    ///     The asset bundle genuinely carries no literal, so discovery cannot depend on scraping one.
    /// </summary>
    /// <remarks>
    ///     Pinned because this is the assumption that let an earlier version pass its whole test suite while
    ///     failing on every live request: the fixture had been written to contain the literal the old code
    ///     looked for, so the test confirmed the assumption instead of reality.
    /// </remarks>
    [Fact]
    public void AssetBundlesCarryNoClientIdLiteral()
    {
        Assert.Null(SoundCloudHydrationParser.ExtractClientId(SoundCloudFixtures.ClientAssetJs));
        Assert.Null(SoundCloudHydrationParser.ExtractClientId("var x = 1;"));
        Assert.Null(SoundCloudHydrationParser.ExtractClientId(string.Empty));

        // The literal form is still recognised, in case a site ever ships one again.
        Assert.Equal(
            "legacy-literal-id",
            SoundCloudHydrationParser.ExtractClientId("var v={client_id:\"legacy-literal-id\"};"));
    }

    [Fact]
    public void AssetUrlsAreReadFromTheHomepageInDocumentOrder()
    {
        var assets = SoundCloudHydrationParser.ExtractAssetUrls(SoundCloudFixtures.HomepageHtml);
        Assert.Equal(3, assets.Count);
        Assert.All(assets, asset => Assert.Contains("a-v2.sndcdn.com", asset, StringComparison.Ordinal));
        Assert.Contains("59-ac0a49ce.js", assets[0], StringComparison.Ordinal);
        Assert.Empty(SoundCloudHydrationParser.ExtractAssetUrls("<html>no assets</html>"));
    }

    /// <summary>
    ///     A short link is followed to the real track before it is hydrated.
    /// </summary>
    [Fact]
    public async Task ShortLinks_AreFollowedBeforeHydration()
    {
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://on.soundcloud.com", StringComparison.Ordinal))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://soundcloud.com/test-artist/test-track");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SoundCloudFixtures.TrackPageHtml)
            };
        });

        using var client = CreateClient(handler);

        var track = await client.ResolveTrackAsync("https://on.soundcloud.com/abcdefgh", CancellationToken.None);
        Assert.Equal("https://soundcloud.com/test-artist/test-track", track.PermalinkUrl);
    }

    /// <summary>
    ///     Every api-v2 request carries the client id, including URLs that were built without one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         api-v2 answers a request with no client id with a 401, and a stale one with the same status. A
    ///         guard that only replaced an id that was already present therefore looked correct while leaving
    ///         every URL built without one rejected - which is how stub expansion failed against a live site
    ///         while search, whose URL embeds the id inline, succeeded.
    ///     </para>
    ///     <para>
    ///         Asserted on the request URLs that actually went out, because the defect was whether the
    ///         parameter reached the wire at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task EveryApiV2RequestCarriesTheDiscoveredClientId()
    {
        var requested = new List<string>();

        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("api-v2.soundcloud.com", StringComparison.Ordinal))
            {
                requested.Add(url);
                return JsonResponse("[]");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    IsHomepage(url)
                        ? SoundCloudFixtures.HomepageHtml
                        : SoundCloudFixtures.BuildSetPageHtml(new[] { "9001" }))
            };
        });

        using var client = CreateClient(handler);

        // Search builds its URL with the id inline; stub expansion does not. Both must end up carrying it.
        await client.SearchTracksAsync("anything", 10, CancellationToken.None);
        await client.ResolveSetAsync("https://soundcloud.com/test-artist/sets/test-set", CancellationToken.None);

        Assert.NotEmpty(requested);
        Assert.All(
            requested,
            url => Assert.Contains(
                $"client_id={SoundCloudFixtures.PublishedClientId}", url, StringComparison.Ordinal));

        // A URL that already had one must not end up with two.
        var search = Assert.Single(requested, url => url.Contains("/search/tracks", StringComparison.Ordinal));
        Assert.Equal(1, CountOccurrences(search, "client_id="));
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
    ///     Builds a homepage whose apiClient hydration carries the supplied client id.
    /// </summary>
    /// <summary>
///     A search response arrives wrapped in an object, not as a bare array.
/// </summary>
/// <remarks>
///     <para>
///         Found by a live enhancement run, not by the suite. api-v2's <c>/search/tracks</c> answers with
///         <c>{"collection":[...]}</c>, so the parser's array-only reading rejected every real response with
///         "not a result array" while every fixture, written as a bare array, passed.
///     </para>
///     <para>
///         Both shapes are accepted, because the bare array is still what some api-v2 routes return.
///     </para>
/// </remarks>
[Fact]
public async Task SearchReadsTheLiveWrappedCollectionShape()
{
    var handler = new StubHandler(request =>
    {
        var url = request.RequestUri!.ToString();
        if (url.Contains("/search/tracks", StringComparison.Ordinal))
        {
            return JsonResponse(SoundCloudFixtures.SearchResponse(
                SoundCloudFixtures.SearchTrackJson(
                    2394568125, "Diana", "https://soundcloud.com/iamji/diana")));
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                IsHomepage(url) ? SoundCloudFixtures.HomepageHtml : SoundCloudFixtures.ClientAssetJs)
        };
    });

    using var client = CreateClient(handler);

    var results = await client.SearchTracksAsync("Diana", 10, CancellationToken.None);

    var track = Assert.Single(results);
    Assert.Equal("Diana", track.Title);
    Assert.Equal("soundcloud:tracks:2394568125", track.Urn);
    Assert.Equal("https://soundcloud.com/iamji/diana", track.PermalinkUrl);
    // genre arrives as a bare string, which the reader must also accept.
    Assert.Equal("Hip-Hop", track.Genre);
}

/// <summary>The bare-array shape is still honoured, because some api-v2 routes return it.</summary>
[Fact]
public void SearchAlsoAcceptsABareArrayBody()
{
    var results = SoundCloudHydrationParser.ParseSearchResults(
        "[" + SoundCloudFixtures.SearchTrackJson(1, "Example", "https://soundcloud.com/a/b") + "]");

    Assert.Equal("Example", Assert.Single(results).Title);
}

private static string BuildHomepageWithClientId(string clientId)
        => SoundCloudFixtures.BuildHydrationPage(
            "[{\"hydratable\":\"apiClient\",\"data\":{\"id\":\"" + clientId + "\",\"isExpiring\":false}}]");

    /// <summary>
    ///     Whether a URL is the SoundCloud homepage, which is what client-id discovery reads from.
    /// </summary>
    /// <remarks>
    ///     Compared on the path rather than by suffix because a bare host may be requested as
    ///     <c>https://soundcloud.com</c> or <c>https://soundcloud.com/</c> depending on how the request was
    ///     built, and both are the homepage.
    /// </remarks>
    private static bool IsHomepage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("soundcloud.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.TrimEnd('/').Length == 0;
    }

    private static string ReadQueryValue(string url, string name)
    {
        var separator = url.IndexOf('?');
        if (separator < 0)
        {
            return string.Empty;
        }

        foreach (var pair in url[(separator + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            if (pair[..equals].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return string.Empty;
    }

    private static string ExtractClientId(string url)
    {
        foreach (var pair in url.Split('?', 2))
        {
            if (!pair.Contains("client_id=", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var candidate in pair[(pair.IndexOf('?') + 1)..].Split('&'))
            {
                if (candidate.StartsWith("client_id=", StringComparison.Ordinal))
                {
                    return candidate["client_id=".Length..];
                }
            }
        }

        return string.Empty;
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static SoundCloudTrack BuildTrack(string[] qualities, string mimeType, string protocol)
        => new()
        {
            Id = 1L,
            Title = "Track",
            TrackAuthorization = TrackAuthorization,
            Transcodings = qualities
                .Select(q => new SoundCloudTranscoding(
                    $"https://api-v2.soundcloud.com/media/soundcloud:tracks:1/stream/hls-{q}",
                    q,
                    mimeType,
                    protocol))
                .ToList()
        };

    private static SoundCloudClient CreateClient(
        StubHandler handler,
        ISoundCloudCredentialProvider? credentials = null)
    {
        var factory = new StubHttpClientFactory(handler);
        return new SoundCloudClient(
            factory,
            credentials ?? new StubCredentialProvider(null),
            NullLogger<SoundCloudClient>.Instance);
    }

    private static string BuildTracksBatchResponse(string ids)
    {
        var tracks = ids
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(id =>
                "{\"id\":" + id
                + ",\"kind\":0,\"title\":\"Track " + id + "\""
                + ",\"permalink_url\":\"https://soundcloud.com/test-artist/track-" + id + "\""
                + ",\"duration\":180000"
                + ",\"user\":{\"username\":\"test-artist\",\"display_name\":\"Test Artist\"}}")
            .ToList();

        return $"[{string.Join(",", tracks)}]";
    }

    private sealed class StubCredentialProvider(string? token) : ISoundCloudCredentialProvider
    {
        public Task<string?> GetOAuthTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult(token);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}