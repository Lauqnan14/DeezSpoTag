using System;
using System.Collections.Generic;
using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Newtonsoft.Json;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplayClassificationMetadataTests
{
    private static readonly MethodInfo ParseSongHtml = typeof(BoomplayMetadataService).GetMethod(
        "ParseSongHtml", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParseSongHtml not found.");

    private static readonly MethodInfo ApplyStreamTags = typeof(BoomplayMetadataService).GetMethod(
        "ApplyStreamTags", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ApplyStreamTags not found.");

    private static readonly MethodInfo ParseOfficialSongMetadata = typeof(BoomplayMetadataService).GetMethod(
        "ParseOfficialSongMetadata", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParseOfficialSongMetadata not found.");

    private static readonly MethodInfo ParseMoodPlaylistCatalog = typeof(BoomplayMetadataService).GetMethod(
        "ParseMoodPlaylistCatalog", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParseMoodPlaylistCatalog not found.");

    private static readonly MethodInfo MapSingleTrack = typeof(BoomplayApiController).GetMethod(
        "MapSingleTrack", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayApiController.MapSingleTrack not found.");

    [Fact]
    public void ParseSongHtml_ExtractsGenreMoodAndDetailTagsWithProvenance()
    {
        const string html = """
        <html><head><meta property="og:title" content="Test Song" /></head><body>
          <section class="songDetailInfo"><ul>
            <li>Genre:<span>Afro Soul / R&amp;B</span></li>
            <li>Mood:<span>Happy, Energetic</span></li>
            <li>Language:<span>English</span></li>
          </ul></section>
        </body></html>
        """;

        var track = Assert.IsType<BoomplayTrackMetadata>(
            ParseSongHtml.Invoke(null, new object?[] { "256487581", html, "https://www.boomplay.com/songs/256487581" }));

        Assert.Equal(new[] { "Afro Soul", "R&B" }, track.Genres);
        Assert.Equal(new[] { "Happy", "Energetic" }, track.Moods);
        Assert.Equal("English", track.Tags["language"]);
        Assert.Equal("html", track.FieldSources["genres"]);
        Assert.Equal("html", track.FieldSources["moods"]);
    }

    [Fact]
    public void ApplyStreamTags_OverridesGenreAndRecordsOptionalMood()
    {
        var track = new BoomplayTrackMetadata { Genres = new List<string> { "Pop" } };
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TCON"] = "Afrobeats; Afro Pop",
            ["TMOO"] = "Uplifting / Party",
            ["TLAN"] = "eng"
        };

        ApplyStreamTags.Invoke(null, new object?[] { track, tags });

        Assert.Equal(new[] { "Afrobeats", "Afro Pop" }, track.Genres);
        Assert.Equal(new[] { "Uplifting", "Party" }, track.Moods);
        Assert.Equal("stream", track.FieldSources["genres"]);
        Assert.Equal("stream", track.FieldSources["moods"]);
    }

    [Fact]
    public void SingleTrackResponse_ExposesSameClassificationShapeAsOtherTracks()
    {
        var track = new BoomplayTrackMetadata
        {
            Id = "256487581",
            Genres = new List<string> { "Afro Soul" },
            Moods = new List<string> { "Happy" },
            Tags = new Dictionary<string, string> { ["language"] = "English" },
            FieldSources = new Dictionary<string, string> { ["genres"] = "html" }
        };

        var payload = MapSingleTrack.Invoke(null, new object?[] { track });
        var json = JsonConvert.SerializeObject(payload);

        Assert.Contains("\"genres\":[\"Afro Soul\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"moods\":[\"Happy\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"fieldSources\":{\"genres\":\"html\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveBoomplaySong_ParsesCurrentHtmlAndOfficialApi()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BOOMPLAY_LIVE_TEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        const string songId = "256487581";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BoomplayMetadataLiveTest/1.0");

        var html = await client.GetStringAsync($"https://www.boomplay.com/songs/{songId}");
        var parsedHtml = Assert.IsType<BoomplayTrackMetadata>(ParseSongHtml.Invoke(
            null, new object?[] { songId, html, $"https://www.boomplay.com/songs/{songId}" }));

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.boomplaymusic.com/BoomPlayer/music/getMusicInfo?musicID={songId}");
        request.Headers.TryAddWithoutValidation("x-boomplay-ref", "Boomplay_ANDROID");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var official = Assert.IsType<BoomplayTrackMetadata>(
            ParseOfficialSongMetadata.Invoke(null, new object?[] { document.RootElement }));

        Assert.Equal(songId, parsedHtml.Id);
        Assert.NotEmpty(parsedHtml.Genres);
        Assert.Equal("html", parsedHtml.FieldSources["genres"]);
        Assert.Equal(songId, official.Id);
        Assert.False(string.IsNullOrWhiteSpace(official.Title));
        Assert.False(string.IsNullOrWhiteSpace(official.Artist));
        Assert.True(official.DurationMs > 0);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveBoomplayMoodCatalog_MapsEditorialMoodPlaylistToTracks()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BOOMPLAY_LIVE_TEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BoomplayMetadataLiveTest/1.0");
        var catalogHtml = await client.GetStringAsync("https://www.boomplay.com/more/home/moods-and-activities");
        var catalog = Assert.IsAssignableFrom<IReadOnlyList<(string Id, string Name)>>(
            ParseMoodPlaylistCatalog.Invoke(null, new object?[] { catalogHtml }));
        var happyFriday = Assert.Single(catalog, static item => item.Name == "Happy Friday");

        var playlistHtml = await client.GetStringAsync($"https://www.boomplay.com/playlists/{happyFriday.Id}");
        Assert.Contains("/songs/", playlistHtml, StringComparison.Ordinal);
    }
}
