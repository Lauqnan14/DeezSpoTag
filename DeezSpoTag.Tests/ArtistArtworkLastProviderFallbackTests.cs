using System.Linq;
using System.Reflection;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistArtworkLastProviderFallbackTests
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
    public void AlbumArtworkFallbackIsGatedToTheLastProviderInTheOrder()
    {
        var source = ReadHelperSource();

        Assert.Contains("var isLastProvider = index == fallbackOrder.Count - 1;", source, System.StringComparison.Ordinal);
        Assert.Contains("allowAlbumArtworkFallback: isLastProvider", source, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EarlierProvidersReturnTheirPortraitResultWithoutAlbumFallback()
    {
        var source = ReadHelperSource();

        Assert.Contains(
            "if (portrait != null || !allowAlbumArtworkFallback)",
            source,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOrderableProviderCanSupplyTheLastResortAlbumArtwork()
    {
        var source = ReadHelperSource();
        var start = source.IndexOf("TryResolveArtistImageFromAlbumArtworkAsync(", System.StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = source[start..(start + 2000)];

        Assert.Contains("\"apple\" =>", body, System.StringComparison.Ordinal);
        Assert.Contains("\"deezer\" =>", body, System.StringComparison.Ordinal);
        Assert.Contains("\"spotify\" =>", body, System.StringComparison.Ordinal);
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
