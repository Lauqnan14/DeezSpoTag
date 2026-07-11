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
        Assert.Contains(SupportedTag.RecordingId, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ArtistId, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.AlbumArtistId, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ReleaseGroupId, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.AlbumId, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ReleaseStatus, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ReleaseCountry, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.Barcode, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.Media, descriptor.SupportedTags);
    }

    [Fact]
    public void PicardStyleMetadata_IsExposedAsGenericTagTogglesAndWrites()
    {
        var repoRoot = ResolveRepoRoot();
        var autoTagJs = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var runner = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs"));
        var matcher = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "AutoTag", "MusicBrainzMatcher.cs"));
        var canonicalizer = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "TaggingProfileCanonicalizer.cs"));
        var downloadConverter = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "DownloadTagSettingsConverter.cs"));
        var audioTagger = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Services", "Download", "Utils", "AudioTagger.cs"));
        var deezerPlatform = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "AutoTag", "DeezerPlatform.cs"));
        var spotifyPlatform = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "AutoTag", "SpotifyPlatform.cs"));
        var itunesPlatform = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "AutoTag", "ITunesPlatform.cs"));

        foreach (var tag in new[]
        {
            "recordingId",
            "artistId",
            "albumArtistId",
            "releaseGroupId",
            "albumId",
            "releaseStatus",
            "releaseCountry",
            "barcode",
            "media"
        })
        {
            Assert.Contains($"tag: \"{tag}\"", autoTagJs, StringComparison.Ordinal);
            Assert.Contains($"\"{tag}\"", autoTagJs, StringComparison.Ordinal);
            Assert.Contains($"new(\"{tag}\"", canonicalizer, StringComparison.Ordinal);
        }

        Assert.Contains("RecordingId = recording.Id", matcher, StringComparison.Ordinal);
        Assert.Contains("ReleaseGroupId = release.ReleaseGroup.Id", matcher, StringComparison.Ordinal);
        Assert.Contains("track.ReleaseStatus = release.Status", matcher, StringComparison.Ordinal);
        Assert.Contains("track.ReleaseCountry = release.Country", matcher, StringComparison.Ordinal);
        Assert.Contains("track.Media = release.Media", matcher, StringComparison.Ordinal);

        Assert.Contains("private const string RecordingIdRawTag = \"RECORDINGID\";", runner, StringComparison.Ordinal);
        Assert.Contains("WriteSingleRawTag(tagWriteContext, context, RecordingIdTag, SupportedTag.RecordingId, RecordingIdRawTag", runner, StringComparison.Ordinal);
        Assert.Contains("WriteSingleRawTag(tagWriteContext, context, ReleaseGroupIdTag, SupportedTag.ReleaseGroupId, ReleaseGroupIdRawTag", runner, StringComparison.Ordinal);
        Assert.Contains("SetRaw(tagWriteContext, MediaRawTag, SupportedTag.Media, context.SourceTrack.Media);", runner, StringComparison.Ordinal);
        Assert.Contains("FirstClassRawOtherTags", runner, StringComparison.Ordinal);

        Assert.Contains("RecordingId = UsesDownload(config.RecordingId)", downloadConverter, StringComparison.Ordinal);
        Assert.Contains("AlbumId = UsesDownload(config.AlbumId)", downloadConverter, StringComparison.Ordinal);
        Assert.Contains("SetCustomFrameIfPresent(tag, \"TXXX\", RecordingIdUpperTag", audioTagger, StringComparison.Ordinal);
        Assert.Contains("SetVorbisCommentIf(tag, save.RecordingId, RecordingIdUpperTag", audioTagger, StringComparison.Ordinal);
        Assert.Contains("SetAtlAdditionalFieldIf(file, save.RecordingId, RecordingIdUpperTag", audioTagger, StringComparison.Ordinal);
        Assert.Contains("Add(\"recordingId\", !string.IsNullOrWhiteSpace(ResolveRecordingId(track)))", audioTagger, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveMetadataValue", audioTagger, StringComparison.Ordinal);
        Assert.Contains("\"recordingId\"", deezerPlatform, StringComparison.Ordinal);
        Assert.Contains("\"artistId\"", deezerPlatform, StringComparison.Ordinal);
        Assert.Contains("\"albumArtistId\"", deezerPlatform, StringComparison.Ordinal);
        Assert.DoesNotContain("\"releaseGroupId\"", deezerPlatform, StringComparison.Ordinal);
        Assert.DoesNotContain("\"releaseStatus\"", deezerPlatform, StringComparison.Ordinal);
        Assert.DoesNotContain("\"releaseCountry\"", deezerPlatform, StringComparison.Ordinal);
        Assert.DoesNotContain("\"media\"", deezerPlatform, StringComparison.Ordinal);
        Assert.Contains("\"recordingId\"", spotifyPlatform, StringComparison.Ordinal);
        Assert.Contains("\"albumId\"", spotifyPlatform, StringComparison.Ordinal);
        Assert.Contains("\"recordingId\"", itunesPlatform, StringComparison.Ordinal);
        Assert.Contains("\"artistId\"", itunesPlatform, StringComparison.Ordinal);
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

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null
            && !(File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(current.FullName, "DeezSpoTag.Web"))))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
