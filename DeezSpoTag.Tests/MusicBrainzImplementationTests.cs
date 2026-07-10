using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MusicBrainzImplementationTests
{
    [Fact]
    public void Descriptor_AdvertisesExistingSharedTagsMusicBrainzCanPopulate()
    {
        var platform = new MusicBrainzPlatform(new StubWebHostEnvironment());

        var descriptor = platform.Describe();

        Assert.Contains(SupportedTag.AlbumArt, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.DiscNumber, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ReleaseDate, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.OtherTags, descriptor.SupportedTags);
    }

    [Theory]
    [InlineData("Sweet Love", "Sweet Love (Instrumental)")]
    [InlineData("Sweet Love", "Sweet Love - Radio Edit")]
    [InlineData("Sweet Love", "Sweet Love (Extended Mix)")]
    [InlineData("Sweet Love (Live)", "Sweet Love")]
    public void VariantGuard_RejectsCandidateWithDifferentVersionIntent(string sourceTitle, string candidateTitle)
    {
        Assert.False(MusicBrainzMatcher.IsVariantCompatible(sourceTitle, candidateTitle));
    }

    [Theory]
    [InlineData("Sweet Love", "Sweet Love")]
    [InlineData("Sweet Love (Instrumental)", "Sweet Love - Instrumental")]
    [InlineData("Sweet Love (Radio Edit)", "Sweet Love - Radio Version")]
    [InlineData("Sweet Love (Extended Mix)", "Sweet Love - Extended Version")]
    public void VariantGuard_AllowsSameVersionIntent(string sourceTitle, string candidateTitle)
    {
        Assert.True(MusicBrainzMatcher.IsVariantCompatible(sourceTitle, candidateTitle));
    }

    [Fact]
    public async Task SearchAsync_UsesConfiguredLimitInRequest()
    {
        Uri? capturedUri = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"recordings":[]}""")
            };
        }));
        var client = new MusicBrainzClient(httpClient, NullLogger<MusicBrainzClient>.Instance);

        _ = await client.SearchAsync("artist title", 7, CancellationToken.None);

        Assert.NotNull(capturedUri);
        Assert.Contains("limit=7", capturedUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_DoesNotSwallowCancellation()
    {
        using var httpClient = new HttpClient(new ThrowingHttpMessageHandler(new OperationCanceledException()));
        var client = new MusicBrainzClient(httpClient, NullLogger<MusicBrainzClient>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.SearchAsync("artist title", 5, CancellationToken.None));
    }

    [Fact]
    public void BuildOtherDictionary_MergesDuplicateRawKeys()
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("BuildOtherDictionary", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.BuildOtherDictionary not found.");
        var track = new MusicBrainzTrack
        {
            Other =
            [
                ("MUSICBRAINZ_RELEASEGROUPID", ["release-group-1"]),
                ("MUSICBRAINZ_RELEASEGROUPID", ["release-group-1", "release-group-2"])
            ]
        };

        var result = (System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>)method.Invoke(null, [track])!;

        Assert.True(result.TryGetValue("MUSICBRAINZ_RELEASEGROUPID", out var values));
        Assert.Equal(["release-group-1", "release-group-2"], values);
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

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
