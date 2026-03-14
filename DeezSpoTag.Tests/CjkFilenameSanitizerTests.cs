using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class CjkFilenameSanitizerTests
{
    [Fact]
    public void ContainsCjk_DetectsJapaneseAndExtendedHan()
    {
        Assert.True(CjkFilenameSanitizer.ContainsCjk("東京"));
        Assert.True(CjkFilenameSanitizer.ContainsCjk("𠀋"));
        Assert.False(CjkFilenameSanitizer.ContainsCjk("Artist Name"));
    }

    [Fact]
    public void SanitizeSegment_PreservesCjkWhileReplacingInvalidCharacters()
    {
        var input = "東京:ライブ/2026?";
        var result = CjkFilenameSanitizer.SanitizeSegment(
            input,
            fallback: "Unknown",
            replacement: "_",
            collapseWhitespace: true,
            trimTrailingDotsAndSpaces: true);

        Assert.Equal("東京_ライブ_2026_", result);
    }

    [Fact]
    public void TruncateByRuneCount_DoesNotSplitSurrogatePairs()
    {
        var input = "A𠀋B";
        var result = CjkFilenameSanitizer.TruncateByRuneCount(input, 2);
        Assert.Equal("A𠀋", result);
    }
}
