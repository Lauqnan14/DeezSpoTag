using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ShazamLogoRecognitionGuardrailTests
{
    [Fact]
    public void LogoCapture_UsesSingleSessionWithFastAndFinalAttempts()
    {
        var root = ResolveRepoRoot();
        var scriptPath = Path.Join(root, "DeezSpoTag.Web", "wwwroot", "js", "shazam-listen.js");
        Assert.True(File.Exists(scriptPath), $"Missing Shazam logo script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);

        Assert.Contains("activeLogoSession", source, StringComparison.Ordinal);
        Assert.Contains("completeLogoSession", source, StringComparison.Ordinal);
        Assert.Contains("runLogoRecognitionAttempt(sessionId, 'quick'", source, StringComparison.Ordinal);
        Assert.Contains("runLogoRecognitionAttempt(sessionId, 'final'", source, StringComparison.Ordinal);
        Assert.Contains("phase: 'logo'", source, StringComparison.Ordinal);
        Assert.Contains("attempt: phase", source, StringComparison.Ordinal);
        Assert.Contains("logoSessionId: `logo-${sessionId}`", source, StringComparison.Ordinal);
        Assert.Contains("activeQuickProbeController.abort();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LogoRecognitionApi_ReturnsSessionAndAttemptMetadataWithPayloads()
    {
        var root = ResolveRepoRoot();
        var controllerPath = Path.Join(root, "DeezSpoTag.Web", "Controllers", "Api", "ShazamApiController.cs");
        Assert.True(File.Exists(controllerPath), $"Missing Shazam API controller: {controllerPath}");

        var source = File.ReadAllText(controllerPath);

        Assert.Contains("[FromForm] string? captureAttempt", source, StringComparison.Ordinal);
        Assert.Contains("[FromForm] string? logoSessionId", source, StringComparison.Ordinal);
        Assert.Contains("\"logo\" => \"logo\"", source, StringComparison.Ordinal);
        Assert.Contains("captureAttempt,", source, StringComparison.Ordinal);
        Assert.Contains("logoSessionId,", source, StringComparison.Ordinal);
        Assert.Contains("related = relatedList", source, StringComparison.Ordinal);
        Assert.Contains("var similarList = MergeSimilarCards(relatedList, searchList, track, recognition);", source, StringComparison.Ordinal);
        Assert.Contains("AddCards(related, cards, seen, matchedIdentity);", source, StringComparison.Ordinal);
        Assert.Contains("AddCards(searchResults, cards, seen, matchedIdentity);", source, StringComparison.Ordinal);
        Assert.Contains("similar = similarList", source, StringComparison.Ordinal);
        Assert.Contains("searchResults = searchList", source, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"logo-result/{clientRequestId}\")]", source, StringComparison.Ordinal);
        Assert.Contains("CacheLogoResult(clientRequestId, payload);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShazamResults_RendersCachedLogoPayloadWithoutCompetingDiscoveryLookups()
    {
        var root = ResolveRepoRoot();
        var viewPath = Path.Join(root, "DeezSpoTag.Web", "Views", "Shazam", "Results.cshtml");
        Assert.True(File.Exists(viewPath), $"Missing Shazam results view: {viewPath}");

        var source = File.ReadAllText(viewPath);

        Assert.Contains("let effectiveTrackId = normalizeText(trackId);", source, StringComparison.Ordinal);
        Assert.Contains("payload?.track?.id || payload?.recognition?.trackId", source, StringComparison.Ordinal);
        Assert.Contains("fetchLogoResultPayload", source, StringComparison.Ordinal);
        Assert.Contains("`/api/shazam/logo-result/${encodeURIComponent(requestId)}`", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/shazam/track/", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/shazam/related/", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/shazam/search", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LogoCapture_NavigatesWithServerResultRequestId()
    {
        var root = ResolveRepoRoot();
        var scriptPath = Path.Join(root, "DeezSpoTag.Web", "wwwroot", "js", "shazam-listen.js");
        Assert.True(File.Exists(scriptPath), $"Missing Shazam logo script: {scriptPath}");

        var source = File.ReadAllText(scriptPath);

        Assert.Contains("const requestId = payload?.clientRequestId;", source, StringComparison.Ordinal);
        Assert.Contains("params.set('requestId', requestId);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LogoRecognitionPayload_MergesRelatedAndSearchResultsIntoSimilar()
    {
        var method = typeof(DeezSpoTag.Web.Controllers.Api.ShazamRecognitionApiController)
            .GetMethod("BuildMatchPayload", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var recognition = new ShazamRecognitionInfo
        {
            TrackId = "match-1",
            Title = "Matched Song",
            Artist = "Matched Artist"
        };
        var matchedTrack = CreateCard("match-1", "Matched Song", "Matched Artist");
        var related = new[]
        {
            CreateCard("match-1", "Matched Song", "Matched Artist"),
            CreateCard("related-1", "Related Song", "Related Artist")
        };
        var search = new[]
        {
            CreateCard("related-1", "Related Song", "Related Artist"),
            CreateCard("search-1", "Search Song", "Search Artist")
        };

        var payload = method!.Invoke(null, new object?[]
        {
            recognition,
            "Matched Song Matched Artist",
            matchedTrack,
            related,
            search,
            "logo",
            "final",
            "logo-1",
            "request-1"
        });

        Assert.NotNull(payload);
        var similar = payload!.GetType().GetProperty("similar")!.GetValue(payload) as System.Collections.IEnumerable;
        Assert.NotNull(similar);

        var ids = similar!.Cast<ShazamTrackCard>().Select(card => card.Id).ToArray();
        Assert.Equal(new[] { "related-1", "search-1" }, ids);
    }

    private static ShazamTrackCard CreateCard(string id, string title, string artist) =>
        new(
            id,
            title,
            artist,
            Album: null,
            Genre: null,
            Label: null,
            ReleaseDate: null,
            ArtworkUrl: null,
            Url: null,
            AppleMusicUrl: null,
            SpotifyUrl: null,
            Isrc: null,
            DurationMs: null,
            Key: null,
            Language: null,
            Composer: null,
            Lyricist: null,
            Publisher: null,
            TrackNumber: null,
            DiscNumber: null,
            Explicit: null,
            AlbumAdamId: null,
            ArtistIds: new(),
            ArtistAdamIds: new(),
            Tags: new());

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
