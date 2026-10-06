using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public sealed class LastFmGenreIntelligencePassThroughTest
{
    [Fact]
    public async Task MatchAsync_PreservesNonArtistTagsForGenreIntelligence_AndRemovesArtistIdentity()
    {
        var handler = new StubHandler("""
        {"toptags":{"@attr":{"artist":"Cher","track":"Believe"},"tag":[
          {"name":"pop","count":100},
          {"name":"Cher","count":95},
          {"name":"dance pop","count":80},
          {"name":"female vocalists","count":70},
          {"name":"british","count":65},
          {"name":"driving","count":60},
          {"name":"Harsh EBM","count":55},
          {"name":"happy","count":50}]}}
        """);
        var (matcher, auth) = CreateMatcher(handler);
        await auth.UpdateAsync(state => state.LastFm = new LastFmAuth { ApiKey = "central-key" });

        var result = await matcher.MatchAsync(
            new AutoTagAudioInfo { Artist = "Cher", Artists = ["Cher"], Title = "Believe" },
            new LastFmConfig { MaxTags = 12, MinTagCount = 10, MinRelativeWeight = .15 },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("Pop", result!.Track.Genres);
        Assert.Contains("Dance Pop", result.Track.Styles);
        Assert.Contains("Female Vocalists", result.Track.Styles);
        Assert.Contains("British", result.Track.Styles);
        Assert.Contains("Driving", result.Track.Styles);
        Assert.Contains("Harsh Ebm", result.Track.Styles);
        Assert.Equal("Happy", result.Track.Mood);
        Assert.DoesNotContain(result.Track.Genres, value => value.Equals("Cher", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Track.Styles, value => value.Equals("Cher", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.Equals(result.Track.Mood, "Cher", StringComparison.OrdinalIgnoreCase));
    }

    private static (LastFmMatcher Matcher, PlatformAuthService Auth) CreateMatcher(HttpMessageHandler handler)
    {
        var root = Path.Join(Path.GetTempPath(), $"lastfm-genre-intelligence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var env = new TestEnvironment { ContentRootPath = root, WebRootPath = root };
        var auth = new PlatformAuthService(
            env,
            NullLogger<PlatformAuthService>.Instance,
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
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
