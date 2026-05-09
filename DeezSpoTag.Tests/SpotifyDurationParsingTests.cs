using System;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyDurationParsingTests
{
    private static readonly MethodInfo ResolveTrackDurationMsMethod =
        typeof(SpotifyPathfinderMetadataClient).GetMethod(
            "ResolveTrackDurationMs",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpotifyPathfinderMetadataClient.ResolveTrackDurationMs not found.");

    [Theory]
    [InlineData("""{"duration":{"totalMilliseconds":225000}}""", 225000)]
    [InlineData("""{"trackDuration":{"totalMilliseconds":225000}}""", 225000)]
    [InlineData("""{"durationMs":225000}""", 225000)]
    [InlineData("""{"duration_ms":225000}""", 225000)]
    [InlineData("""{"duration":225}""", 225000)]
    [InlineData("""{"duration":225000}""", 225000)]
    public void ResolveTrackDurationMs_NormalizesSpotifyDurationShapes(string json, int expectedDurationMs)
    {
        using var doc = JsonDocument.Parse(json);

        var actual = Assert.IsType<int>(ResolveTrackDurationMsMethod.Invoke(null, new object?[] { doc.RootElement }));

        Assert.Equal(expectedDurationMs, actual);
    }
}
