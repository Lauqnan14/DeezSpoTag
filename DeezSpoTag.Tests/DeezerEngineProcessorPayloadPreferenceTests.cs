using System.Reflection;
using DeezSpoTag.Services.Download.Deezer;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeezerEngineProcessorPayloadPreferenceTests
{
    [Theory]
    [InlineData("spotify", true)]
    [InlineData("deezer", false)]
    [InlineData("qobuz", false)]
    [InlineData("apple", false)]
    [InlineData(null, false)]
    public void ShouldPreferPayloadMetadata_OnlyWhenResolvedDownloadTagSourceIsSpotify(string? resolvedDownloadTagSource, bool expected)
    {
        var method = typeof(DeezerEngineProcessor).GetMethod(
            "ShouldPreferPayloadMetadata",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = Assert.IsType<bool>(method!.Invoke(null, new object?[] { resolvedDownloadTagSource }));
        Assert.Equal(expected, actual);
    }
}
