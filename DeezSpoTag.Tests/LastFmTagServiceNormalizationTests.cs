using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http;
using Xunit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Tests;

public sealed class LastFmTagServiceNormalizationTests
{
    [Fact]
    public async Task GetTrackTagsAsync_NormalizesArtistAndTitle_InSinglePrimaryRequest()
    {
        Uri? capturedUri = null;
        var factory = new StubHttpClientFactory(new StubHttpMessageHandler(request =>
        {
            capturedUri = request.RequestUri;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"toptags\":{\"tag\":[{\"name\":\"afrobeats\",\"count\":100}]}}")
            };
        }));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lastfm:ApiKey"] = "unit-test-key"
            })
            .Build();

        var env = new StubWebHostEnvironment();
        var auth = CreatePlatformAuthService(env);
        var service = new LastFmTagService(factory, config, auth, NullLogger<LastFmTagService>.Instance);

        var tags = await service.GetTrackTagsAsync("Davido feat. Chris Brown", "With You (feat. Omah Lay) [Remix]", CancellationToken.None);

        Assert.NotNull(tags);
        Assert.Contains("afrobeats", tags!);
        Assert.NotNull(capturedUri);
        var requestText = capturedUri!.ToString();
        Assert.Contains("artist=Davido", requestText, StringComparison.Ordinal);
        var queryMap = ParseQuery(capturedUri.Query);
        Assert.True(queryMap.TryGetValue("track", out var trackParam));
        Assert.Equal("With You", trackParam);
        Assert.DoesNotContain("feat", requestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remix", requestText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTrackTagsAsync_UsesCachedCanonicalKey_ForEquivalentInputs()
    {
        var calls = 0;
        var factory = new StubHttpClientFactory(new StubHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"toptags\":{\"tag\":[{\"name\":\"pop\",\"count\":50}]}}")
            };
        }));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lastfm:ApiKey"] = "unit-test-key"
            })
            .Build();

        var env = new StubWebHostEnvironment();
        var auth = CreatePlatformAuthService(env);
        var service = new LastFmTagService(factory, config, auth, NullLogger<LastFmTagService>.Instance);

        var first = await service.GetTrackTagsAsync("Chris Brown", "Run It! (feat. Juelz Santana)", CancellationToken.None);
        var second = await service.GetTrackTagsAsync("Chris Brown", "Run It!", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, calls);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static PlatformAuthService CreatePlatformAuthService(IWebHostEnvironment environment)
    {
        var keyDirectory = Path.Join(Path.GetTempPath(), $"deezspotag-lastfm-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyDirectory);
        return new PlatformAuthService(
            environment,
            NullLogger<PlatformAuthService>.Instance,
            DataProtectionProvider.Create(new DirectoryInfo(keyDirectory)));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = query.TrimStart('?');
        foreach (var segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Split('=', 2, StringSplitOptions.None);
            var key = Uri.UnescapeDataString(pair[0]);
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace("+", " ", StringComparison.Ordinal)) : string.Empty;
            result[key] = value;
        }

        return result;
    }
}
