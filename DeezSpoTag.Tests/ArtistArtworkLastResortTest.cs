using System.Linq;
using System.Reflection;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistArtworkLastResortTest
{
    private static string ReadHelperSource()
    {
        var root = TestSourcePaths.RepositoryRoot;
        return System.IO.File.ReadAllText(System.IO.Path.Join(
            root,
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "DownloadEngineArtworkHelper.cs"));
    }

    [Fact]
    public void AlbumArtworkFallbackIsNotGatedToTheLastProviderInTheOrder()
    {
        var source = ReadHelperSource();

        Assert.DoesNotContain("isLastProvider", source, System.StringComparison.Ordinal);
        Assert.DoesNotContain("allowAlbumArtworkFallback", source, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EveryConfiguredSourceIsExhaustedBeforeTheAlbumArtworkLastResort()
    {
        var source = ReadHelperSource();

        var loopIndex = source.IndexOf(
            "for (var index = 0; index < fallbackOrder.Count; index++)",
            System.StringComparison.Ordinal);
        var lastResortIndex = source.IndexOf(
            "Last resort, only once every configured source has missed",
            System.StringComparison.Ordinal);

        Assert.True(loopIndex > 0, "the portrait loop should exist");
        Assert.True(lastResortIndex > loopIndex, "the album artwork last resort must run after the portrait loop");
    }

    [Fact]
    public void LastResortWalksTheConfiguredOrderAndTakesTheFirstSourceThatCanServe()
    {
        var source = ReadHelperSource();

        var lastResortIndex = source.IndexOf(
            "Last resort, only once every configured source has missed",
            System.StringComparison.Ordinal);
        Assert.True(lastResortIndex > 0);
        var body = source[lastResortIndex..(lastResortIndex + 1400)];

        Assert.Contains("foreach (var source in fallbackOrder)", body, System.StringComparison.Ordinal);
        Assert.Contains("if (albumArtwork != null)", body, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SourcesMissingTheIdTheirAlbumArtworkLookupNeedsAreSkipped()
    {
        var start = FindAlbumArtworkMethodDefinition();
        var body = ReadHelperSource()[start..(start + 3000)];

        Assert.Contains("string.IsNullOrWhiteSpace(request.AppleId)", body, System.StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(request.DeezerId)", body, System.StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(request.SpotifyId)", body, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOrderableProviderCanSupplyTheLastResortAlbumArtwork()
    {
        var start = FindAlbumArtworkMethodDefinition();
        var body = ReadHelperSource()[start..(start + 3000)];

        // Each engine must have a last-resort album-artwork arm. They are referenced through their
        // canonical constants now rather than as repeated literals, so this accepts either spelling.
        // The constant's own declaration is pinned separately, so a name that matched here while
        // holding a different value could not pass.
        foreach (var engine in new[] { "AppleProvider", "DeezerProvider", "SpotifyProvider" })
        {
            Assert.True(
                body.Contains($"\"{engine.Replace("Provider", string.Empty).ToLowerInvariant()}\" =>",
                    System.StringComparison.Ordinal)
                || body.Contains($"{engine} =>", System.StringComparison.Ordinal),
                $"the last-resort album artwork switch must handle {engine}");
        }

        foreach (var declaration in new[]
                 {
                     "private const string AppleProvider = DownloadTagSourceHelper.AppleSource;",
                     "private const string DeezerProvider = DownloadTagSourceHelper.DeezerSource;",
                     "private const string SpotifyProvider = DownloadTagSourceHelper.SpotifySource;",
                 })
        {
            Assert.Contains(declaration, ReadHelperSource(), System.StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Locates the album-artwork helper's definition rather than its call site.
    /// </summary>
    private static int FindAlbumArtworkMethodDefinition()
    {
        var source = ReadHelperSource();
        var start = source.IndexOf(
            "Task<ArtistArtworkResolution?> TryResolveArtistImageFromAlbumArtworkAsync(",
            System.StringComparison.Ordinal);
        Assert.True(start > 0, "the album artwork helper definition should exist");
        return start;
    }

    [Fact]
    public void LastResortArtworkIsTaggedSoItIsNotMistakenForAPortrait()
    {
        Assert.Contains("\"album-artwork-fallback\"", ReadHelperSource(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void AppleArtistLookupNeverSubstitutesAlbumArtworkByDefault()
    {
        var appleSource = System.IO.File.ReadAllText(System.IO.Path.Join(
            TestSourcePaths.RepositoryRoot,
            "DeezSpoTag.Services",
            "Download",
            "Apple",
            "AppleQueueHelpers.cs"));

        Assert.Contains("bool allowAlbumArtwork = false", appleSource, System.StringComparison.Ordinal);
        Assert.Contains("TryExtractArtistArtwork(artistDoc.RootElement, size)", appleSource, System.StringComparison.Ordinal);
    }
}

internal static class TestSourcePaths
{
    public static string RepositoryRoot { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var directory = new System.IO.DirectoryInfo(
            System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory != null && !directory.EnumerateDirectories("DeezSpoTag.Services").Any())
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new System.InvalidOperationException("Repository root not found.");
    }
}
