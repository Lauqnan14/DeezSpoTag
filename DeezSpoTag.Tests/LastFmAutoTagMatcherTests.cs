using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LastFmAutoTagMatcherTests
{
    [Fact]
    public async Task MatchAsync_LoadsCentralCredentialAndClassifiesWeightedTags()
    {
        var handler = new StubHandler("""
        {"toptags":{"@attr":{"artist":"Cher","track":"Believe"},"tag":[
          {"name":"pop","count":100},{"name":"dance pop","count":"70"},
          {"name":"happy","count":50},{"name":"driving","count":40},
          {"name":"female vocalists","count":90},{"name":"british","count":80},
          {"name":"weak tag","count":2}]}}
        """);
        var (matcher, auth) = CreateMatcher(handler);
        await auth.UpdateAsync(state => state.LastFm = new LastFmAuth { ApiKey = "central-key" });

        var result = await matcher.MatchAsync(
            new AutoTagAudioInfo { Artist = "Cher", Title = "Believe" },
            new LastFmConfig { MaxTags = 12, MinTagCount = 10, MinRelativeWeight = .15 },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(["Pop"], result!.Track.Genres);
        Assert.Equal(["Dance Pop"], result.Track.Styles);
        Assert.Equal("Happy", result.Track.Mood);
        Assert.DoesNotContain(result.Track.Genres, value => value.Contains("British", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("api_key=central-key", handler.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchAsync_RejectsAutocorrectedDifferentIdentity()
    {
        var handler = new StubHandler("""
        {"toptags":{"@attr":{"artist":"Different Artist","track":"Believe"},"tag":[{"name":"pop","count":100}]}}
        """);
        var (matcher, auth) = CreateMatcher(handler);
        await auth.UpdateAsync(state => state.LastFm = new LastFmAuth { ApiKey = "central-key" });

        var result = await matcher.MatchAsync(
            new AutoTagAudioInfo { Artist = "Cher", Title = "Believe" },
            new LastFmConfig(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveMatchAsync_UsesSavedCredentialAndLastFmApi()
    {
        if (Environment.GetEnvironmentVariable("LASTFM_LIVE_TEST") != "1") return;

        var repoRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "../../../../"));
        var workerRoot = Path.Join(repoRoot, "DeezSpoTag.Workers");
        var env = new TestEnvironment { ContentRootPath = workerRoot, WebRootPath = workerRoot };
        var keys = Path.Join(workerRoot, "Data", "security", "data-protection-keys");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keys), builder => builder.SetApplicationName("DeezSpoTag"));
        var auth = new PlatformAuthService(env, NullLogger<PlatformAuthService>.Instance, provider);
        var saved = await auth.LoadAsync();
        Assert.False(string.IsNullOrWhiteSpace(saved.LastFm?.ApiKey), "No decryptable saved Last.fm API key was available for the live test.");

        var matcher = new LastFmMatcher(new StubFactory(new HttpClientHandler()), auth, NullLogger<LastFmMatcher>.Instance);
        var result = await matcher.MatchAsync(
            new AutoTagAudioInfo { Artist = "Cher", Title = "Believe" },
            new LastFmConfig { MaxTags = 12, MinTagCount = 10, MinRelativeWeight = .15 },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Track.Genres, value => value.Equals("Pop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveMatchAsync_ExercisesStyleAndMoodClassification()
    {
        if (Environment.GetEnvironmentVariable("LASTFM_LIVE_TEST") != "1") return;
        var (matcher, auth) = CreateLiveMatcher();
        Assert.False(string.IsNullOrWhiteSpace((await auth.LoadAsync()).LastFm?.ApiKey));
        var config = new LastFmConfig { MaxTags = 30, MinTagCount = 1, MinRelativeWeight = 0.01 };

        var style = await matcher.MatchAsync(
            new AutoTagAudioInfo { Artist = "Nirvana", Title = "Smells Like Teen Spirit" }, config, CancellationToken.None);
        var moodCandidates = new[]
        {
            (Artist: "Pharrell Williams", Title: "Happy"),
            (Artist: "Radiohead", Title: "No Surprises"),
            (Artist: "Massive Attack", Title: "Teardrop"),
            (Artist: "The Cure", Title: "Pictures of You")
        };
        AutoTagMatchResult? mood = null;
        foreach (var candidate in moodCandidates)
        {
            var result = await matcher.MatchAsync(
                new AutoTagAudioInfo { Artist = candidate.Artist, Title = candidate.Title }, config, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(result?.Track.Mood))
            {
                mood = result;
                break;
            }
        }
        Assert.NotEmpty(style?.Track.Styles ?? []);
        Assert.False(string.IsNullOrWhiteSpace(mood?.Track.Mood));
    }

    private static (LastFmMatcher Matcher, PlatformAuthService Auth) CreateLiveMatcher()
    {
        var repoRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "../../../../"));
        var workerRoot = Path.Join(repoRoot, "DeezSpoTag.Workers");
        var env = new TestEnvironment { ContentRootPath = workerRoot, WebRootPath = workerRoot };
        var keys = Path.Join(workerRoot, "Data", "security", "data-protection-keys");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keys), builder => builder.SetApplicationName("DeezSpoTag"));
        var auth = new PlatformAuthService(env, NullLogger<PlatformAuthService>.Instance, provider);
        return (new LastFmMatcher(new StubFactory(new HttpClientHandler()), auth, NullLogger<LastFmMatcher>.Instance), auth);
    }

    private static (LastFmMatcher Matcher, PlatformAuthService Auth) CreateMatcher(HttpMessageHandler handler)
    {
        var root = Path.Join(Path.GetTempPath(), $"lastfm-autotag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var env = new TestEnvironment { ContentRootPath = root, WebRootPath = root };
        var auth = new PlatformAuthService(env, NullLogger<PlatformAuthService>.Instance,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Join(root, "keys"))));
        var matcher = new LastFmMatcher(new StubFactory(handler), auth, NullLogger<LastFmMatcher>.Instance);
        return (matcher, auth);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
