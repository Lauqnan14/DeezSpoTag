using DeezSpoTag.Core.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackIdentityTrustTest
{
    [Fact]
    public void SuppliedResolverConversion_UsesCreditsQueryAndDiceSelectionBeforeValidation()
    {
        var query = DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient.BuildConversionQuery("Song!", "Provided to YouTube by Label\n\nSong · Artist\n\nAlbum\n\n℗ Label");
        Assert.Equal("Song Artist Album", query);
        query = "Song Artist";
        Assert.Equal(1, DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient.ConversionScore("Song", "Artist", query));
        Assert.Equal(2d / 3, DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient.ConversionScore("abcd", null, "abce"), 10);
        Assert.Equal(0, DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient.ConversionScore("XX", null, "YY"));
        var candidates = new[] { new ConversionLink("Song", "Other", "wrong"), new ConversionLink("Song", "Artist", "correct") };
        var selected = DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient.ConvertCandidate(candidates, query, item => item.Title, item => item.Artist, item => item.Id);
        Assert.Equal("correct", selected?.Id);
        Assert.Null(DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient.ConvertCandidate(new[] { candidates[1], candidates[1] with { Id = "ambiguous" } }, query, item => item.Title, item => item.Artist, item => item.Id));
    }

    [Theory]
    [InlineData(true, "Mbebe (Live)")]
    [InlineData(false, "Artist - Mbebe (Official Music Video)")]
    public void VerifiedCatalogMetadata_ReplacesYouTubePresentationOnly(bool youtube, string expectedTitle)
    {
        var type = typeof(DeezSpoTag.Web.Services.TrackIdentityResolver).GetNestedType("IdentityState", System.Reflection.BindingFlags.NonPublic)!;
        var state = System.Activator.CreateInstance(type, true)!;
        type.GetProperty("IsYouTubeSource")!.SetValue(state, youtube);
        type.GetProperty("Title")!.SetValue(state, "Artist - Mbebe (Official Music Video)");
        type.GetProperty("Artist")!.SetValue(state, "Artist");
        type.GetProperty("DurationMs")!.SetValue(state, 167000);
        var matched = System.Activator.CreateInstance(type, true)!;
        type.GetProperty("IsYouTubeSource")!.SetValue(matched, youtube);
        type.GetMethod("SetMatchedMetadata")!.Invoke(matched, ["Mbebe (Live)", "Artist", "Mbebe", "VV0002604900", 166737]);
        type.GetMethod("MergeMissingFrom")!.Invoke(state, [matched]);
        Assert.Equal("Artist - Mbebe (Official Music Video)", type.GetProperty("Title")!.GetValue(state));
        var result = (DeezSpoTag.Services.Download.Identity.TrackIdentityResolution)type.GetMethod("ToResolution")!.Invoke(state, [new[] { new DeezSpoTag.Services.Download.Identity.PlatformIdentityCandidate("qobuz", "123", "https://play.qobuz.com/track/123", "verified", true) }])!;
        Assert.Equal(expectedTitle, result.Title);
        var intent = new DeezSpoTag.Services.Download.Shared.Models.DownloadIntent
        {
            SourceUrl = youtube ? "https://music.youtube.com/watch?v=dQw4w9WgXcQ" : "https://www.deezer.com/track/123",
            Title = "Artist - Mbebe (Official Music Video)", Artist = "Artist"
        };
        typeof(DeezSpoTag.Web.Services.DownloadIntentService).GetMethod("ApplyResolvedIdentity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [intent, result]);
        Assert.Equal(expectedTitle, intent.Title);
        if (youtube) { Assert.Equal("Mbebe", result.Album); Assert.Equal(166737, result.DurationMs); Assert.Equal("VV0002604900", result.Isrc); }
    }

    [Fact]
    public void UnmatchedYouTubeRefresh_DoesNotOverwriteVerifiedDownloadMetadata()
    {
        var intent = new DeezSpoTag.Services.Download.Shared.Models.DownloadIntent { SourceUrl = "https://music.youtube.com/watch?v=dQw4w9WgXcQ", Title = "Mbebe", Artist = "Artist", Album = "Mbebe", DurationMs = 166737 };
        var type = typeof(DeezSpoTag.Web.Services.TrackIdentityResolver).GetNestedType("IdentityState", System.Reflection.BindingFlags.NonPublic)!;
        var state = System.Activator.CreateInstance(type, true)!;
        type.GetProperty("Title")!.SetValue(state, "Artist - Mbebe (Official Music Video)");
        type.GetProperty("DurationMs")!.SetValue(state, 167000);
        var result = type.GetMethod("ToResolution")!.Invoke(state, [System.Array.Empty<DeezSpoTag.Services.Download.Identity.PlatformIdentityCandidate>()]);
        typeof(DeezSpoTag.Web.Services.DownloadIntentService).GetMethod("ApplyResolvedIdentity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [intent, result]);
        Assert.Equal("Mbebe", intent.Title); Assert.Equal("Mbebe", intent.Album); Assert.Equal(166737, intent.DurationMs);
    }

    private sealed record ConversionLink(string Title, string Artist, string Id);

    [Fact]
    public async System.Threading.Tasks.Task YouTubeDownloadIdentity_HydratesProviderMetadataAndClearsForeignBrowserIds()
    {
        using var http = new System.Net.Http.HttpClient(new MetadataHandler());
        var resolver = new DeezSpoTag.Web.Services.TrackIdentityResolver(null!, null!, null!, null!, null!, null!, null!, null!, null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Web.Services.TrackIdentityResolver>.Instance,
            new DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient(http));
        var result = await resolver.ResolveAsync(new DeezSpoTag.Services.Download.Identity.TrackIdentityResolutionRequest(
            "ytmusic", "https://music.youtube.com/watch?v=dQw4w9WgXcQ", "Forged title", "Forged artist", "Forged album", "USABC1234567", 1000,
            SpotifyId: "dQw4w9WgXcQ", DeezerId: "123", QobuzId: "456", TargetPlatforms: ["none"]), System.Threading.CancellationToken.None);
        Assert.Equal("Song", result.Title);
        Assert.Equal("Artist", result.Artist);
        Assert.Equal(180000, result.DurationMs);
        Assert.Null(result.SpotifyId); Assert.Null(result.DeezerId); Assert.Null(result.QobuzId); Assert.Null(result.Isrc);
    }

    [Fact]
    public async System.Threading.Tasks.Task YouTubeMetadata_UsesExactQueueCreditsWhenDescriptionHasNone()
    {
        using var http = new System.Net.Http.HttpClient(new MetadataHandler(true));
        var client = new DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient(http);
        var track = await client.GetSongMetadataAsync("dQw4w9WgXcQ", System.Threading.CancellationToken.None);
        Assert.Equal("Artist", track.Artist);
        Assert.Equal(180, track.DurationSeconds);
        Assert.Contains("Artist", track.ConversionQuery);
    }

    [Fact]
    public async System.Threading.Tasks.Task YouTubeMetadata_RejectsQueueDurationMismatch()
    {
        using var http = new System.Net.Http.HttpClient(new MetadataHandler(true, "4:00"));
        var client = new DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient(http);
        await Assert.ThrowsAsync<System.InvalidOperationException>(() => client.GetSongMetadataAsync("dQw4w9WgXcQ", System.Threading.CancellationToken.None));
    }

    [Fact]
    public async System.Threading.Tasks.Task YouTubeMetadata_MissingPlayerDetailsUsesExactQueueRecording()
    {
        using var http = new System.Net.Http.HttpClient(new MetadataHandler(missingDetails: true));
        var track = await new DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient(http)
            .GetSongMetadataAsync("dQw4w9WgXcQ", System.Threading.CancellationToken.None);
        Assert.Equal("dQw4w9WgXcQ", track.VideoId);
        Assert.Equal("Song", track.Title);
        Assert.Equal("Artist", track.Artist);
        Assert.Equal(180, track.DurationSeconds);
        Assert.Contains("Artist", track.ConversionQuery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task YouTubeMetadata_RejectsMismatchedRecording(bool missingDetails)
    {
        using var http = new System.Net.Http.HttpClient(new MetadataHandler(missingDetails: missingDetails, wrongId: true));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            new DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient(http)
                .GetSongMetadataAsync("dQw4w9WgXcQ", System.Threading.CancellationToken.None));
    }

    [Fact]
    public async System.Threading.Tasks.Task YouTubeMetadata_ProductionCreditsCannotOverrideQueueRecording()
    {
        using var http = new System.Net.Http.HttpClient(new MetadataHandler(productionCredits: true));
        var track = await new DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient(http)
            .GetSongMetadataAsync("dQw4w9WgXcQ", System.Threading.CancellationToken.None);
        Assert.Equal("Song", track.Title);
        Assert.Equal("Artist", track.Artist);
        Assert.DoesNotContain("Screenwriter", track.ConversionQuery);
        Assert.DoesNotContain("Official Music Video", track.ConversionQuery);
    }

    private sealed class MetadataHandler(bool missingCredits = false, string queueDuration = "3:00", bool missingDetails = false, bool wrongId = false, bool productionCredits = false) : System.Net.Http.HttpMessageHandler
    {
        protected override async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/get_queue"))
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("""
                {"queueDatas":[{"content":{"playlistPanelVideoRenderer":{"videoId":"dQw4w9WgXcQ","title":{"runs":[{"text":"Song"}]},"longBylineText":{"runs":[{"text":"Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"UCartist"}}},{"text":" • "},{"text":"2M views"},{"text":" • "},{"text":"2K likes"}]},"lengthText":{"runs":[{"text":"3:00"}]}}}}]}
                """.Replace("3:00", queueDuration).Replace("dQw4w9WgXcQ", wrongId ? "abcdefghijk" : "dQw4w9WgXcQ")) };
            if (missingDetails) return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("{}") };
            if (wrongId) return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("{\"videoDetails\":{\"videoId\":\"abcdefghijk\"}}") };
            if (productionCredits)
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("""
                {"videoDetails":{"videoId":"dQw4w9WgXcQ","title":"Artist - Song (Official Music Video)","lengthSeconds":"180","shortDescription":"Production · Screenwriter\nDirector: Someone"}}
                """) };
            if (missingCredits)
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("""
                {"videoDetails":{"videoId":"dQw4w9WgXcQ","title":"Song","author":"Unrelated uploader","lengthSeconds":"180","shortDescription":"Official music video"}}
                """) };
            if (request.RequestUri!.AbsolutePath.EndsWith("/player"))
            {
                using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("WEB", body.RootElement.GetProperty("context").GetProperty("client").GetProperty("clientName").GetString());
            }
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(request.RequestUri!.AbsolutePath.EndsWith("/search") ? """
            {"contents":{"sectionListRenderer":{"contents":[{"musicCardShelfRenderer":{"title":{"runs":[{"text":"Song"}]},"navigationEndpoint":{"watchEndpoint":{"videoId":"dQw4w9WgXcQ"}},"subtitle":{"runs":[{"text":"Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"UCartist"}}},{"text":" • "},{"text":"Album","navigationEndpoint":{"browseEndpoint":{"browseId":"MPREalbum"}}},{"text":" • "},{"text":"3:00"}]}}}]}}}
            """ : """
            {"videoDetails":{"videoId":"dQw4w9WgXcQ","title":"Song","author":"Unrelated uploader","lengthSeconds":"180","shortDescription":"Provided to YouTube by Label\n\nSong · Artist\n\n℗ Label"}}
            """) };
        }
    }

    [Theory]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com.evil.test/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&v=xxxxxxxxxxx", null)]
    [InlineData("https://music.youtube.com/watch?v=123", null)]
    public void YouTubeIdentity_RequiresExactProviderVideoUrl(string url, string? expected)
        => Assert.Equal(expected, DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient.GetVideoId(url));

    [Theory]
    [InlineData("/library/Music/Karun/01 - 01 - 01 - SLIDE.flac", true)]
    [InlineData("01 - 01 - Dai Dai.flac", true)]
    [InlineData("00 - 01 - Ahere.m4a", true)]
    [InlineData("/library/Music/Karun/Catch A Vibe/01 - Catch A Vibe.flac", false)]
    [InlineData("Kiss Me.flac", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void RepeatedNumericFilenamePrefix_MatchesCorruptLibraryNames(string? path, bool expected)
    {
        Assert.Equal(expected, TrackIdentityTrust.HasRepeatedNumericFilenamePrefix(path));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("unknown", true)]
    [InlineData("Unknown Artist", true)]
    [InlineData("untitled", true)]
    [InlineData("track", true)]
    [InlineData("audio", true)]
    [InlineData("official audio", true)]
    [InlineData("a", true)]
    [InlineData("SLIDE", false)]
    [InlineData("Karun", false)]
    public void WeakMetadata_CoversLibraryAndRunnerTokens(string? value, bool expected)
    {
        Assert.Equal(expected, TrackIdentityTrust.IsWeakMetadataValue(value));
    }

    [Fact]
    public void UntrustedIdentity_WhenPrefixOrWeakTags()
    {
        Assert.True(TrackIdentityTrust.IsUntrustedIdentity(
            "SLIDE",
            "Karun",
            "/library/Music/01 - 01 - 01 - SLIDE.flac"));
        Assert.True(TrackIdentityTrust.IsUntrustedIdentity("unknown", "Karun", "song.flac"));
        Assert.True(TrackIdentityTrust.IsUntrustedIdentity("SLIDE", null, "song.flac"));
        Assert.False(TrackIdentityTrust.IsUntrustedIdentity("SLIDE", "Karun", "/library/Music/Karun/SLIDE.flac"));
    }
}
