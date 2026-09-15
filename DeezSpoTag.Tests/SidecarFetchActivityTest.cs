using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SidecarFetchActivityTest
{
    public static TheoryData<SidecarFetchWork, string?> Cases => new()
    {
        { new(false, false, false, false), null },
        { new(true, false, false, false), "Fetching album artwork" },
        { new(false, true, false, false), "Fetching animated artwork" },
        { new(false, false, true, false), "Fetching artist artwork" },
        { new(false, false, false, true), "Fetching lyrics" },
        { new(true, true, false, false), "Fetching album artwork and animated artwork" },
        { new(true, false, true, true), "Fetching album artwork, artist artwork and lyrics" },
        { new(true, true, true, true), "Fetching album artwork, animated artwork, artist artwork and lyrics" }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Describe_UsesDownloadOrderingAndWording(SidecarFetchWork work, string? expected)
        => Assert.Equal(expected, SidecarFetchActivity.Describe(work));

    [Theory]
    [InlineData(LyricsSidecarWorkKind.None, null)]
    [InlineData(LyricsSidecarWorkKind.FetchMissing, "Fetching lyrics")]
    [InlineData(LyricsSidecarWorkKind.UpgradeLrcToWord, "Updating lyrics")]
    [InlineData(LyricsSidecarWorkKind.RewriteTtmlToWord, "Updating TTML")]
    [InlineData(LyricsSidecarWorkKind.RemoveLineSyncedTtml, "Removing line-synced TTML")]
    [InlineData(LyricsSidecarWorkKind.UpgradeLrcToWord | LyricsSidecarWorkKind.RewriteTtmlToWord, "Updating lyrics and TTML")]
    public void DescribeLyricsWork_UsesEnhancementWording(LyricsSidecarWorkKind work, string? expected)
        => Assert.Equal(expected, SidecarFetchActivity.DescribeLyricsWork(work));

    [Fact]
    public void DescribeEnhancement_CombinesArtworkWithLyricsAction()
    {
        Assert.Equal(
            "Fetching album artwork, animated artwork and lyrics",
            SidecarFetchActivity.DescribeEnhancement(true, true, LyricsSidecarWorkKind.FetchMissing));
        Assert.Equal(
            "Fetching album artwork, animated artwork and updating lyrics",
            SidecarFetchActivity.DescribeEnhancement(true, true, LyricsSidecarWorkKind.UpgradeLrcToWord));
        Assert.Equal(
            "Updating lyrics",
            SidecarFetchActivity.DescribeEnhancement(false, false, LyricsSidecarWorkKind.UpgradeLrcToWord));
        Assert.Equal(
            "Removing line-synced TTML",
            SidecarFetchActivity.DescribeEnhancement(false, false, LyricsSidecarWorkKind.RemoveLineSyncedTtml));
        Assert.Equal(
            "Fetching album artwork",
            SidecarFetchActivity.DescribeEnhancement(true, false, LyricsSidecarWorkKind.None));
    }
}
