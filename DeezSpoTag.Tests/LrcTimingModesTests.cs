using DeezSpoTag.Core.Models.Settings;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LrcTimingModesTests
{
    [Theory]
    [InlineData(null, true, LrcTimingModes.PreferEnhanced)]
    [InlineData("", false, LrcTimingModes.Line)]
    [InlineData("word-enhanced", true, LrcTimingModes.WordEnhanced)]
    [InlineData("line", true, LrcTimingModes.Line)]
    [InlineData("prefer-enhanced-else-line", false, LrcTimingModes.PreferEnhanced)]
    public void Normalize_MapsKnownValues(string? value, bool preferEnhancedFallback, string expected)
    {
        Assert.Equal(expected, LrcTimingModes.Normalize(value, preferEnhancedFallback));
    }

    [Fact]
    public void ImpliesEnhanced_IsFalseOnlyForLine()
    {
        Assert.False(LrcTimingModes.ImpliesEnhanced(LrcTimingModes.Line));
        Assert.True(LrcTimingModes.ImpliesEnhanced(LrcTimingModes.PreferEnhanced));
        Assert.True(LrcTimingModes.ImpliesEnhanced(LrcTimingModes.WordEnhanced));
    }

    [Fact]
    public void RequiresWordTiming_IsTrueOnlyForWordEnhanced()
    {
        Assert.True(LrcTimingModes.RequiresWordTiming(LrcTimingModes.WordEnhanced));
        Assert.False(LrcTimingModes.RequiresWordTiming(LrcTimingModes.PreferEnhanced));
        Assert.False(LrcTimingModes.RequiresWordTiming(LrcTimingModes.Line));
    }
}
