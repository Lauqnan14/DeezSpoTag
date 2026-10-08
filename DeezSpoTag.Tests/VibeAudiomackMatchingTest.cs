using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services.Audiomack;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// A false Audiomack match is unacceptable for Vibe: candidates are scored against
/// the requested artist/title and only results at or above the confidence gate
/// contribute Audiomack evidence.
/// </summary>
public sealed class VibeAudiomackMatchingTest
{
    private static readonly AudiomackSongCandidate ExactCandidate = new(
        Id: "8526341",
        Title: "Amapiano Nights",
        Artist: "Piano Pusha",
        Album: "Piano Season",
        Genre: "afrosounds",
        Mood: "happy",
        Isrc: null,
        Label: null,
        DurationSeconds: 372,
        ArtworkUrl: null,
        ReleasedDate: null,
        Url: "https://audiomack.com/piano-pusha/song/amapiano-nights",
        UrlSlug: "amapiano-nights",
        ArtistSlug: "piano-pusha",
        UploaderName: "Piano Pusha",
        AlbumId: "771002");

    [Fact]
    public void RecordingIntegrity_UploaderAndUnrequestedGuestsCannotSupplyEvidence()
    {
        Assert.Equal(0d, AudiomackVibeMetadataService.ScoreCandidate(ExactCandidate with { Artist = null, UploaderName = "Piano Pusha" }, "Piano Pusha", "Amapiano Nights"));
        Assert.Equal(0d, AudiomackVibeMetadataService.ScoreCandidate(ExactCandidate with { Featuring = "Other Guest" }, "Piano Pusha", "Amapiano Nights"));
    }

    // ---------------------------------------------------------------------
    // Vibe page enrichment must stay bound to the verified recording: the same
    // extractor and merge the AutoTag matcher uses, plus a 24h in-memory cache
    // that may only ever store validated results.
    // ---------------------------------------------------------------------

    private const string IntendedObjectJson = """
    {"id":9394068,"title":"Always Remember Us This Way","artist":"Lady Gaga","album":"A Star Is Born","genre":"pop","subgenres":["country pop"],"moods":["emotional"],"url":"https://audiomack.com/lady-gaga/song/always-remember-us-this-way","url_slug":"always-remember-us-this-way","artist_slug":"lady-gaga","type":"song","uploader":{"id":"4482381","name":"Lady Gaga","url_slug":"lady-gaga"}}
    """;

    private const string UnrelatedRicherObjectJson = """
    {"id":10781857,"title":"Physical","artist":"Dua Lipa","album":"Future Nostalgia","genre":"dance pop","subgenres":["disco","dance pop"],"moods":["energetic","party"],"usertags":"kpop,motivation","tagdisplay":"K-Pop,Motivation","featuring":"HWASA (화사)","url":"https://audiomack.com/dua-lipa/song/physical","url_slug":"physical","artist_slug":"dua-lipa","type":"song","uploader":{"id":"1097155","name":"Dua Lipa","url_slug":"dua-lipa"}}
    """;

    private const string SearchResponseJson = """
    {"results":[{"id":9394068,"title":"Always Remember Us This Way","artist":"Lady Gaga","album":"A Star Is Born","genre":"pop","url":"https://audiomack.com/lady-gaga/song/always-remember-us-this-way","url_slug":"always-remember-us-this-way","artist_slug":"lady-gaga","type":"song","uploader":{"id":"4482381","name":"Lady Gaga","url_slug":"lady-gaga"}}]}
    """;

    [Fact]
    public async Task Enrichment_RicherRecommendationNeverReachesTheMappedResult()
    {
        var backend = new VibePageBackend(RscPage(UnrelatedRicherObjectJson, IntendedObjectJson));
        var service = new AudiomackVibeMetadataService(CreateApiClient(backend), backend);

        var metadata = await service.FindTrackAsync("Lady Gaga", "Always Remember Us This Way", CancellationToken.None);

        Assert.NotNull(metadata);
        Assert.Equal("9394068", metadata!.TrackId);
        Assert.Equal("Always Remember Us This Way", metadata.Title);
        Assert.Equal("Lady Gaga", Assert.Single(metadata.Artists));
        Assert.Equal(new[] { "country pop" }, metadata.Subgenres);
        Assert.Equal(new[] { "emotional" }, metadata.Moods);
        Assert.DoesNotContain(metadata.Artists, artist => artist.Contains("HWASA", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(metadata.RawTags, tag => tag.Contains("K-Pop", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(metadata.RawTags, tag => tag.Contains("Motivation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(metadata.RawTags, tag => tag.Contains("disco", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Enrichment_UnrelatedPageOverlayContributesNothingButKeepsTheMatch()
    {
        var backend = new VibePageBackend(RscPage(UnrelatedRicherObjectJson));
        var service = new AudiomackVibeMetadataService(CreateApiClient(backend), backend);

        var metadata = await service.FindTrackAsync("Lady Gaga", "Always Remember Us This Way", CancellationToken.None);

        Assert.NotNull(metadata);
        Assert.Equal("9394068", metadata!.TrackId);
        Assert.Equal(new[] { "Lady Gaga" }, metadata.Artists);
        Assert.Empty(metadata.Subgenres);
        Assert.Empty(metadata.Moods);
        Assert.Empty(metadata.RawTags);
    }

    [Fact]
    public async Task Enrichment_SameSongPageEnrichesTheVerifiedRecording()
    {
        const string enriched = """
        {"id":9394068,"title":"Always Remember Us This Way","artist":"Lady Gaga","genre":"pop","subgenres":["country pop","pop"],"moods":["emotional","bittersweet"],"usertags":"sad","tagdisplay":"Sad","url":"https://audiomack.com/lady-gaga/song/always-remember-us-this-way","url_slug":"always-remember-us-this-way","artist_slug":"lady-gaga","type":"song"}
        """;
        var backend = new VibePageBackend(RscPage(enriched));
        var service = new AudiomackVibeMetadataService(CreateApiClient(backend), backend);

        var metadata = await service.FindTrackAsync("Lady Gaga", "Always Remember Us This Way", CancellationToken.None);

        Assert.NotNull(metadata);
        // The verified page supplies the missing taxonomy; its display tag is
        // style evidence like any other creator-set tag.
        Assert.Equal(new[] { "country pop", "pop", "Sad" }, metadata!.Subgenres);
        Assert.Equal(new[] { "emotional", "bittersweet" }, metadata.Moods);
        Assert.Contains("Sad", metadata.RawTags);
        Assert.Equal("9394068", metadata.TrackId);
    }

    [Fact]
    public async Task Enrichment_VersionAndCreditDriftRejectsTheOverlay()
    {
        const string versionDrift = """
        {"id":9394068,"title":"Always Remember Us This Way (Live)","artist":"Lady Gaga","genre":"pop","subgenres":["country pop"],"moods":["emotional"],"url":"https://audiomack.com/lady-gaga/song/always-remember-us-this-way","url_slug":"always-remember-us-this-way","artist_slug":"lady-gaga","type":"song"}
        """;
        var versionBackend = new VibePageBackend(RscPage(versionDrift));
        var versionService = new AudiomackVibeMetadataService(CreateApiClient(versionBackend), versionBackend);

        var versionResult = await versionService.FindTrackAsync("Lady Gaga", "Always Remember Us This Way", CancellationToken.None);
        Assert.NotNull(versionResult);
        Assert.Empty(versionResult!.Subgenres);
        Assert.Empty(versionResult.Moods);

        const string creditDrift = """
        {"id":9394068,"title":"Always Remember Us This Way","artist":"Lady Gaga","genre":"pop","featuring":"HWASA (화사)","subgenres":["country pop"],"moods":["emotional"],"url":"https://audiomack.com/lady-gaga/song/always-remember-us-this-way","url_slug":"always-remember-us-this-way","artist_slug":"lady-gaga","type":"song"}
        """;
        // The caller asked for HWASA, the page credits a different guest: the
        // overlay is discarded rather than remapping a recording nobody asked for.
        const string foreignGuest = """
        {"id":9394068,"title":"Always Remember Us This Way","artist":"Lady Gaga","genre":"pop","featuring":"Someone Else","subgenres":["country pop"],"moods":["emotional"],"url":"https://audiomack.com/lady-gaga/song/always-remember-us-this-way","url_slug":"always-remember-us-this-way","artist_slug":"lady-gaga","type":"song"}
        """;
        var creditBackend = new VibePageBackend(RscPage(foreignGuest));
        var creditService = new AudiomackVibeMetadataService(CreateApiClient(creditBackend), creditBackend);

        var creditResult = await creditService.FindTrackAsync("Lady Gaga", "Always Remember Us This Way (feat. HWASA (화사))", CancellationToken.None);
        Assert.NotNull(creditResult);
        Assert.Empty(creditResult!.Subgenres);
        Assert.Empty(creditResult.Moods);
        Assert.DoesNotContain(creditResult.Artists, artist => artist.Contains("Someone Else", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Enrichment_CachedResultNeverAcquiresTheRecommendationValues()
    {
        var backend = new VibePageBackend(RscPage(UnrelatedRicherObjectJson, IntendedObjectJson));
        var service = new AudiomackVibeMetadataService(CreateApiClient(backend), backend);

        var first = await service.FindTrackAsync("Lady Gaga", "Always Remember Us This Way", CancellationToken.None);
        var second = await service.FindTrackAsync("Lady Gaga", "Always Remember Us This Way", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.TrackId, second!.TrackId);
        Assert.Equal(first.Title, second.Title);
        Assert.Equal(first.Artists, second.Artists);
        Assert.Equal(first.RawTags, second.RawTags);
        Assert.Equal(first.Subgenres, second.Subgenres);
        Assert.Equal(first.Moods, second.Moods);
        // The cache served the second lookup without another network round trip.
        Assert.Equal(1, backend.SearchRequests);
        Assert.Equal(1, backend.PageRequests);
    }

    [Fact]
    public async Task Enrichment_CancellationIsPropagated()
    {
        var backend = new VibePageBackend(RscPage(IntendedObjectJson));
        var service = new AudiomackVibeMetadataService(CreateApiClient(backend), backend);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.FindTrackAsync("Lady Gaga", "Always Remember Us This Way", cts.Token));
    }

    private static string RscPage(params string[] jsonObjects)
    {
        var builder = new System.Text.StringBuilder("<html><body>");
        foreach (var json in jsonObjects)
        {
            builder.Append("<script>self.__next_f.push([1,")
                .Append(System.Text.Json.JsonSerializer.Serialize(json))
                .Append("])</script>");
        }

        return builder.Append("</body></html>").ToString();
    }

    private static AudiomackApiClient CreateApiClient(IHttpClientFactory factory)
        => new(
            factory,
            new AudiomackWebCredentialsProvider(factory, NullLogger<AudiomackWebCredentialsProvider>.Instance),
            NullLogger<AudiomackApiClient>.Instance);

    private sealed class VibePageBackend : IHttpClientFactory
    {
        private const string DiscoveryHtml = """
        <html><body><script>window.env={API_PUBLIC_API_URL:"https://api.audiomack.com/v1/",API_CONSUMER_KEY:"audiomack-web",API_CONSUMER_SECRET:"0123456789abcdef0123456789abcdef"};</script></body></html>
        """;

        private readonly string _pageHtml;

        public VibePageBackend(string pageHtml) => _pageHtml = pageHtml;

        public int SearchRequests { get; private set; }

        public int PageRequests { get; private set; }

        public HttpClient CreateClient(string name) => new(new RoutingHandler(this));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            if (uri.Host.Equals("audiomack.com", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath.Trim('/').Equals("search", StringComparison.OrdinalIgnoreCase))
                {
                    return Text(DiscoveryHtml, "text/html");
                }

                PageRequests++;
                return Text(_pageHtml, "text/html");
            }

            if (uri.Host.Equals("api.audiomack.com", StringComparison.OrdinalIgnoreCase))
            {
                SearchRequests++;
                return Text(SearchResponseJson, "application/json");
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Text(string body, string mediaType)
            => new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType)
            };

        private sealed class RoutingHandler : HttpMessageHandler
        {
            private readonly VibePageBackend _owner;

            public RoutingHandler(VibePageBackend owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                // A real transport observes the token; the stub must too.
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_owner.Respond(request));
            }
        }
    }

    [Fact]
    public void ExactMatch_ScoresAboveTheGate()
    {
        var score = AudiomackVibeMetadataService.ScoreCandidate(ExactCandidate, "Piano Pusha", "Amapiano Nights");
        Assert.True(score >= AudiomackVibeMetadataService.DefaultMinimumMatchConfidence);
    }

    [Fact]
    public void FeaturedArtistAndCasingDifferences_StillMatch()
    {
        var score = AudiomackVibeMetadataService.ScoreCandidate(
            ExactCandidate with { Title = "Amapiano Nights (feat. Guest Vocalist)" },
            "piano pusha",
            "Amapiano Nights feat. Guest Vocalist");
        Assert.True(score >= AudiomackVibeMetadataService.DefaultMinimumMatchConfidence);
    }

    [Fact]
    public void WeakMatch_BelowTheGate_ProducesNoEvidence()
    {
        var score = AudiomackVibeMetadataService.ScoreCandidate(
            ExactCandidate with { Title = "Totally Different Song", Artist = "Other Person" },
            "Piano Pusha",
            "Amapiano Nights");
        Assert.True(score < AudiomackVibeMetadataService.DefaultMinimumMatchConfidence);
    }

    [Fact]
    public void RemixMarker_IsVersionDrift_NotTheSameTrack()
    {
        var score = AudiomackVibeMetadataService.ScoreCandidate(
            ExactCandidate with { Title = "Amapiano Nights (Sped Up Remix)" },
            "Piano Pusha",
            "Amapiano Nights");
        Assert.True(score < AudiomackVibeMetadataService.DefaultMinimumMatchConfidence);
    }

    [Fact]
    public void EmptyCandidate_ScoresZero()
    {
        var score = AudiomackVibeMetadataService.ScoreCandidate(
            ExactCandidate with { Title = "", Artist = null },
            "Piano Pusha",
            "Amapiano Nights");
        Assert.Equal(0d, score);
    }
}
