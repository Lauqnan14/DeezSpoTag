using System.Linq;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ShazamSongDetailsTest
{
    [Theory]
    [InlineData("Songwriter")]
    [InlineData("Songwriter(s)")]
    [InlineData("Writers")]
    [InlineData("Written By")]
    public void GenericWritingMetadataDoesNotBecomeComposer(string label)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            key = "42", title = "Song", subtitle = "Artist",
            sections = new[] { new { type = "SONG", metadata = new[] { new { title = label, text = "Writer Person" } } } }
        }));
        var parse = typeof(ShazamDiscoveryService).GetMethod("ParseTrack", BindingFlags.NonPublic | BindingFlags.Static)!;
        var card = (ShazamTrackCard)parse.Invoke(null, [json.RootElement])!;
        Assert.Null(card.Composer);
        var recognition = (ShazamRecognitionInfo)typeof(ShazamRecognitionService)
            .GetMethod("BuildInfo", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [card])!;
        Assert.Null(recognition.Composer);
        Assert.Contains(card.Tags, entry => entry.Key.StartsWith("SHAZAM_META_", StringComparison.Ordinal) && entry.Value.Contains("Writer Person"));
    }

    [Fact]
    public void ExplicitComposerAndLyricistRemainDistinct()
    {
        using var json = JsonDocument.Parse("""
            {"key":"42","title":"Song","subtitle":"Artist","sections":[{"type":"SONG","metadata":[
            {"title":"Composer","text":"Composer Person"},{"title":"Lyricist","text":"Lyricist Person"}]}]}
            """);
        var parse = typeof(ShazamDiscoveryService).GetMethod("ParseTrack", BindingFlags.NonPublic | BindingFlags.Static)!;
        var card = (ShazamTrackCard)parse.Invoke(null, [json.RootElement])!;
        Assert.Equal("Composer Person", card.Composer);
        Assert.Equal("Lyricist Person", card.Lyricist);
    }

    [Fact]
    public void RecognizedSongwritersFlowToTheirOwnSharedCreditField()
    {
        var recognized = new ShazamRecognitionInfo
        {
            Composer = "Composer Person",
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SHAZAM_META_SONGWRITER"] = ["Writer One", "Writer Two"]
            }
        };
        var track = new AutoTagTrack();
        typeof(ShazamMatcher).GetMethod("AddShazamOtherFields", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [track, recognized]);
        Assert.Equal(["Writer One", "Writer Two"], track.Other["songwriter"]);
        Assert.Equal(["Composer Person"], track.Other["composer"]);
        Assert.False(track.Other.ContainsKey("lyricist"));
    }
    internal static ShazamTrackCard SourceCard() => new(
        "53080365", "Rolling In the Deep", "Adele", "21", null, null, null, null, null,
        "https://music.apple.com/us/album/21/1544491232?i=1544491233", null, "GBBKS1000335",
        228000, null, null, null, null, null, null, null, null, null, [], [], new());

    internal static string SongPage(string pageId = "1544491233", string key = "53080365",
        string isrc = "GBBKS1000335", string title = "Rolling In the Deep", string artist = "Adele",
        int duration = 228000, string analysis = "{\"bpm\":104,\"melodicness\":0.98,\"energy\":0.69}", bool nestedCreditBody = false, string creditLabel = "Songwriter, Producer")
    {
        var main = JsonSerializer.Serialize(new { track = new { type = "shazam-songs", id = key,
            _adamId = "403037877", attributes = new { title, artist, isrc, durationInMillis = duration } } });
        main = main[..^1] + ",\"audioAnalysis\":" + analysis + "}";
        // References and frame IDs deliberately differ from the live page. Ignore credits for another song.
        var credits = JsonSerializer.Serialize(new object[] { "$", "div", "person", new {
            dataTestId = "creditPerson", songId = "1544491233", providerName = "credits",
            children = nestedCreditBody ? new object[] { "$La3", "$La4" } : new object[] { "$La1", "$La2" } } });
        var other = credits.Replace("1544491233", "999");
        var flight = "2:I[9,[],\"component\"]\n:HL[\"/font.woff2\",\"font\"]\n1:" + main + "\na1:[\"$\",\"div\",null,{\"children\":\"Writer Person\"}]\n"
            + "a2:[\"$\",\"div\",null,{\"children\":\"Songwriter, Producer\"}]\n"
            + "a3:[\"$\",\"div\",null,{\"children\":[\"$La1\",\"$La2\"]}]\n"
            + "a4:[\"$\",\"span\",null,{\"aria-hidden\":true,\"children\":[\"$\",\"svg\",null,{}]}]\n"
            + "3:" + credits + "\n4:" + other + "\n";
        flight = flight.Replace(JsonSerializer.Serialize("Songwriter, Producer"), JsonSerializer.Serialize(creditLabel));
        var cut = flight.Length / 2;
        return "<link href=\"https://www.shazam.com/song/" + pageId + "/song\" rel=\"canonical\">"
            + "<script>self.__next_f.push(" + JsonSerializer.Serialize(new object[] { 1, flight[..cut] }) + ")</script>"
            + "<script>self.__next_f.push(" + JsonSerializer.Serialize(new object[] { 1, flight[cut..] }) + ")</script>";
    }

    private static ShazamTrackCard? ParsePage(string page)
    {
        var method = typeof(ShazamDiscoveryService).GetMethod("ParseSongDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (ShazamTrackCard?)method.Invoke(null, [SourceCard(), page]);
    }

    [Fact]
    public void SongPageBindsAnalysisAndReferenceResolvedCreditsToVerifiedRecording()
    {
        var result = ParsePage(SongPage());
        Assert.NotNull(result);
        var details = result.GetType().GetProperty("SongDetails")!.GetValue(result) as AutoTagTrack;
        Assert.NotNull(details);
        Assert.Equal(104L, details.Bpm);
        Assert.Equal(0.98, details.Melodicness);
        Assert.Equal(0.69, details.Energy);
        Assert.Null(details.Acousticness);
        Assert.Equal(["Writer Person"], details.Other["songwriter"]);
        Assert.Equal(["Writer Person"], details.Other["producer"]);
        Assert.False(details.Other.ContainsKey("composer"));
        Assert.Equal(SourceCard().AppleMusicUrl, result.AppleMusicUrl);
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("key")]
    [InlineData("isrc")]
    [InlineData("title")]
    [InlineData("artist")]
    [InlineData("duration")]
    [InlineData("version")]
    public void SongPageRejectsConflictingRecordingEvenWhenHttpSucceeded(string conflict)
    {
        var page = SongPage(pageId: conflict == "canonical" ? "999" : "1544491233",
            key: conflict == "key" ? "999" : "53080365", isrc: conflict == "isrc" ? "USUG11904206" : "GBBKS1000335",
            title: conflict == "title" ? "Another Song" : conflict == "version" ? "Rolling In the Deep (Live)" : "Rolling In the Deep",
            artist: conflict == "artist" ? "Another Artist" : "Adele", duration: conflict == "duration" ? 260000 : 228000);
        Assert.Null(ParsePage(page));
    }

    [Theory]
    [InlineData("{\"bpm\":-1,\"melodicness\":1.1,\"energy\":-0.1}")]
    [InlineData("{\"bpm\":1e40,\"melodicness\":\"NaN\",\"energy\":null}")]
    public void InvalidOrMissingAnalysisNeverManufacturesZero(string analysis)
    {
        var result = ParsePage(SongPage(analysis: analysis));
        Assert.NotNull(result); // valid role-labelled credits remain usable
        var details = (AutoTagTrack)result.GetType().GetProperty("SongDetails")!.GetValue(result)!;
        Assert.Null(details.Bpm);
        Assert.Null(details.Melodicness);
        Assert.Null(details.Energy);
    }

    [Fact]
    public void MalformedOrUnassociatedPageIsRejected()
    {
        Assert.Null(ParsePage("<html>no song here</html>"));
        Assert.Null(ParsePage(SongPage().Replace("shazam-songs", "recommended-songs")));
    }

    [Fact]
    public async System.Threading.Tasks.Task RetrievalCoalescesRequestsAndPersistsOnlyVerifiedDetails()
    {
        using var fixture = new DetailsFixture();
        var calls = Enumerable.Range(0, 5).Select(_ => fixture.Service.GetSongDetailsAsync(SourceCard())).ToArray();
        var result = await System.Threading.Tasks.Task.WhenAll(calls);
        Assert.All(result, card => Assert.Equal(104, card!.SongDetails!.Bpm));
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.Single(System.IO.Directory.GetFiles(fixture.AppDataRoot, "*.json", System.IO.SearchOption.AllDirectories));
        var secondService = fixture.CreateService();
        Assert.Equal(.98, (await secondService.GetSongDetailsAsync(SourceCard()))!.SongDetails!.Melodicness);
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.All(fixture.Handler.Urls, uri => Assert.Equal("https://www.shazam.com/song/1544491233/rolling-in-the-deep", uri));
        Assert.Contains("en-US", fixture.Handler.Language);
        Assert.True(fixture.Handler.HasClientHint);
    }

    [Fact]
    public async System.Threading.Tasks.Task BadPageIsNotNegativeCachedAndRefreshFailurePreservesGoodData()
    {
        using var fixture = new DetailsFixture();
        fixture.Handler.Body = SongPage(key: "999");
        Assert.Null(await fixture.Service.GetSongDetailsAsync(SourceCard()));
        Assert.False(System.IO.Directory.Exists(System.IO.Path.Join(fixture.AppDataRoot, "shazam-song-details")));
        fixture.Handler.Body = SongPage();
        Assert.NotNull(await fixture.Service.GetSongDetailsAsync(SourceCard()));
        fixture.Handler.Body = SongPage(analysis: "{\"energy\":0.8}");
        var partial = await fixture.Service.GetSongDetailsAsync(SourceCard(), refresh: true);
        Assert.Equal(104, partial!.SongDetails!.Bpm);
        Assert.Equal(.98, partial.SongDetails.Melodicness);
        Assert.Equal(.8, partial.SongDetails.Energy);
        fixture.Handler.Body = SongPage(pageId: "999");
        var retained = await fixture.Service.GetSongDetailsAsync(SourceCard(), refresh: true);
        Assert.Equal(.8, retained!.SongDetails!.Energy);
        Assert.Equal(4, fixture.Handler.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task TransientResponseIsRetriedOnceAndParentCancellationPropagates()
    {
        using var fixture = new DetailsFixture();
        fixture.Handler.FirstUnavailable = true;
        Assert.NotNull(await fixture.Service.GetSongDetailsAsync(SourceCard()));
        Assert.Equal(2, fixture.Handler.Calls);
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GetSongDetailsAsync(SourceCard(), cancelled.Token));
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task MissingAssociationDoesNotGuessNumericNamespaces()
    {
        using var fixture = new DetailsFixture();
        Assert.Null(await fixture.Service.GetSongDetailsAsync(SourceCard() with { AppleMusicUrl = null }));
        Assert.Equal(0, fixture.Handler.Calls);
    }


    [Theory]
    [InlineData("Piano", "performer", "Writer Person [Piano]")]
    [InlineData("Assistant Recording Engineer", "assistantEngineer", "Writer Person")]
    [InlineData("Session Caterer", "involvedPeople", "Session Caterer: Writer Person")]
    public void ExplicitInstrumentsAndUnknownRolesArePreservedWithoutGuessing(string label, string key, string value)
    {
        var result = ParsePage(SongPage(creditLabel: label));
        Assert.NotNull(result);
        Assert.Equal([value], result.SongDetails!.Other[key]);
        Assert.False(result.SongDetails.Other.ContainsKey("composer"));
    }

    [Fact]
    public async System.Threading.Tasks.Task TimeoutIsOptionalAndActualParentCancellationStillPropagates()
    {
        using var fixture = new DetailsFixture();
        fixture.Handler.Delay = 200;
        fixture.Client.Timeout = TimeSpan.FromMilliseconds(20);
        Assert.Null(await fixture.Service.GetSongDetailsAsync(SourceCard()));
        using var second = new DetailsFixture();
        second.Handler.Delay = 200;
        using var cancel = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.Service.GetSongDetailsAsync(SourceCard(), cancel.Token));
    }
    [Fact]
    public async System.Threading.Tasks.Task CorrectDocumentTransportDecodesCompressedFramesIncludingPreloadHints()
    {
        using var fixture = new DetailsFixture();
        fixture.Handler.Compressed = true;
        var result = await fixture.Service.GetSongDetailsAsync(SourceCard());
        Assert.NotNull(result);
        Assert.Equal(.98, result.SongDetails!.Melodicness);
        Assert.True(fixture.Handler.CorrectTransport);
    }

    [Fact]
    public void NestedCreditBodyKeepsPersonAndRoleSeparateAndIgnoresDecoration()
    {
        var result = ParsePage(SongPage(nestedCreditBody: true));
        Assert.NotNull(result);
        Assert.Equal(["Writer Person"], result.SongDetails!.Other["songwriter"]);
        Assert.Equal(["Writer Person"], result.SongDetails.Other["producer"]);
    }

    private sealed class DetailsHandler : System.Net.Http.HttpMessageHandler
    {
        public int Calls;
        public int Delay = 40;
        public bool Compressed;
        public bool CorrectTransport;
        public string Body = SongPage();
        public bool FirstUnavailable;
        public bool HasClientHint;
        public string Language = "";
        public List<string> Urls = [];
        protected override async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken token)
        {
            System.Threading.Interlocked.Increment(ref Calls);
            Urls.Add(request.RequestUri!.AbsoluteUri);
            HasClientHint = request.Headers.Contains("Sec-CH-UA");
            CorrectTransport = request.Version.Major == 2 && request.VersionPolicy == System.Net.Http.HttpVersionPolicy.RequestVersionExact
                && request.Headers.AcceptEncoding.Any(encoding => encoding.Value == "gzip") && request.Headers.Accept.Any(accept => accept.MediaType == "text/html");
            Language = string.Join(",", request.Headers.AcceptLanguage);
            await System.Threading.Tasks.Task.Delay(Delay, token);
            System.Net.Http.HttpContent content = new System.Net.Http.StringContent(Body);
            if (Compressed)
            {
                using var bytes = new System.IO.MemoryStream();
                using (var zip = new System.IO.Compression.GZipStream(bytes, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                { var raw = System.Text.Encoding.UTF8.GetBytes(Body); zip.Write(raw); }
                content = new System.Net.Http.ByteArrayContent(bytes.ToArray());
                content.Headers.ContentEncoding.Add("gzip");
            }
            return new System.Net.Http.HttpResponseMessage(FirstUnavailable && Calls == 1
                ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class DetailsFixture : Microsoft.AspNetCore.Hosting.IWebHostEnvironment, IAppDataRootOverride, IDisposable
    {
        public string AppDataRoot { get; } = System.IO.Path.Join(System.IO.Path.GetTempPath(), "shazam-details-" + Guid.NewGuid().ToString("N"));
        public DetailsHandler Handler { get; } = new();
        public ShazamDiscoveryService Service { get; }
        public System.Net.Http.HttpClient Client { get; }
        public DetailsFixture()
        {
            Client = new System.Net.Http.HttpClient(Handler);
            System.IO.Directory.CreateDirectory(AppDataRoot);
            Service = CreateService();
        }
        public ShazamDiscoveryService CreateService() => new(Client,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ShazamDiscoveryService>.Instance, this);
        public void Dispose() { Client.Dispose(); System.IO.Directory.Delete(AppDataRoot, true); }
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

}
