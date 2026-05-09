using System;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplayDurationParsingTests
{
    private static readonly MethodInfo ParseDurationMsMethod =
        typeof(BoomplayMetadataService).GetMethod(
            "ParseDurationMs",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParseDurationMs not found.");

    private static readonly MethodInfo TryApplySongDetailFieldMethod =
        typeof(BoomplayMetadataService).GetMethod(
            "TryApplySongDetailField",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.TryApplySongDetailField not found.");

    [Theory]
    [InlineData("3:45", 225000)]
    [InlineData("03:45", 225000)]
    [InlineData("1:02:03", 3723000)]
    [InlineData("PT3M45S", 225000)]
    [InlineData("225000", 225000)]
    public void ParseDurationMs_UsesCorrectUnits(string raw, int expectedMilliseconds)
    {
        var actual = Assert.IsType<int>(ParseDurationMsMethod.Invoke(null, new object?[] { raw }));

        Assert.Equal(expectedMilliseconds, actual);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("length")]
    [InlineData("track duration")]
    public void TryApplySongDetailField_AppliesDurationLabels(string label)
    {
        var track = new BoomplayTrackMetadata();

        TryApplySongDetailFieldMethod.Invoke(null, new object?[] { track, label, "3:45" });

        Assert.Equal(225000, track.DurationMs);
    }
}
