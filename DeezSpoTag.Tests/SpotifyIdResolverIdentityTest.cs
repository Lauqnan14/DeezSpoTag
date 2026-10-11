using System;
using System.IO;
using System.Reflection;
using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyIdResolverIdentityTest
{
    [Theory]
    [InlineData("Song (Live)", "Artist", 180000, null)]
    [InlineData("Song", "Different Artist", 180000, null)]
    [InlineData("Song", "Artist", 250000, null)]
    [InlineData("Song", "Artist", null, null)]
    [InlineData("Song", "Artist", 180000, "USABC1234568")]
    public void SpotifyStrictSelection_RejectsRecordingConflicts(string title, string artist, int? duration, string? isrc)
    {
        var candidate = new DeezSpoTag.Web.Services.SpotifySearchItem("id", title, "track", "", null, artist, duration, Isrc: isrc);
        Assert.Null(DeezSpoTag.Web.Services.SpotifyIdResolver.SelectVerifiedCandidate([candidate], "Song", "Artist", null, "USABC1234567", 180000));
    }

    [Fact]
    public void SpotifyStrictSelection_RejectsTiedRecordingCandidates()
    {
        var method = typeof(DeezSpoTag.Web.Services.SpotifyIdResolver).GetMethod("SelectVerifiedCandidate", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var items = new System.Collections.Generic.List<DeezSpoTag.Web.Services.SpotifySearchItem> { new("one", "Song", "track", "", null, "Artist", 180000), new("two", "Song", "track", "", null, "Artist", 180000) };
        Assert.Null(method!.Invoke(null, [items, "Song", "Artist", null, null, 180000]));
    }

    [Fact]
    public void SpotifyDownloadResolver_DoesNotUseRemovedSearchEndpoint()
    {
        var removedResolverPath = ResolveRepoCandidatePath("DeezSpoTag.Services", "Download", "Spotify", "SpotifyIdResolver.cs");
        var registrationSource = ReadSource("DeezSpoTag.Services", "Download", "DownloadServiceExtensions.cs");

        Assert.False(File.Exists(removedResolverPath));
        Assert.Contains("TryAddSingleton<ISpotifyIdResolver, NullSpotifyIdResolver>", registrationSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleton<ISpotifyIdResolver, SpotifyIdResolver>", registrationSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://play.qobuz.com/track/166186694", "166186694")]
    [InlineData("https://open.qobuz.com/track/166186694", "166186694")]
    [InlineData("https://www.qobuz.com/us-en/track/166186694", "166186694")]
    public void QobuzDownloadController_ExtractsTrackIdFromDirectQobuzUrl(string url, string expected)
    {
        var method = typeof(QobuzDownloadApiController).GetMethod(
            "TryExtractQobuzTrackId",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, [url]);

        Assert.Equal(expected, result);
    }

    private static string ReadSource(params string[] relativeParts)
        => File.ReadAllText(ResolveRepoPath(relativeParts));

    private static string ResolveRepoPath(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(relativeParts));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate source file.", Path.Combine(relativeParts));
    }

    private static string ResolveRepoCandidatePath(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var marker = Path.Combine(dir.FullName, "DeezSpoTag.Web", "DeezSpoTag.Web.csproj");
            if (File.Exists(marker))
            {
                return Path.Combine(dir.FullName, Path.Combine(relativeParts));
            }

            dir = dir.Parent;
        }

        return Path.Combine(relativeParts);
    }
}
