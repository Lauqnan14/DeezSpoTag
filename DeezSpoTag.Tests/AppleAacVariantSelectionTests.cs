using System.Reflection;
using DeezSpoTag.Services.Download.Apple;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppleAacVariantSelectionTests
{
    [Theory]
    [InlineData("audio-stereo-256", "aac-lc", true)]
    [InlineData("audio-stereo-256", "aac", true)]
    [InlineData("audio-stereo-binaural-256", "aac-lc", false)]
    [InlineData("audio-stereo-downmix-256", "aac-lc", false)]
    [InlineData("audio-stereo-256", "aac-binaural", false)]
    [InlineData("audio-stereo-binaural-256", "aac-binaural", true)]
    [InlineData("audio-stereo-downmix-256", "aac-binaural", false)]
    [InlineData("audio-stereo-256", "aac-downmix", false)]
    [InlineData("audio-stereo-binaural-256", "aac-downmix", false)]
    [InlineData("audio-stereo-downmix-256", "aac-downmix", true)]
    [InlineData("audio-stereo-binaural-256", "AAC_BINAURAL", true)]
    [InlineData("audio-stereo-downmix-256", "AAC_DOWNMIX", true)]
    public void IsMatchingAacGroup_RespectsRequestedVariant(string audioGroup, string aacType, bool expected)
    {
        Assert.Equal(expected, InvokeIsMatchingAacGroup(audioGroup, aacType));
    }

    private static bool InvokeIsMatchingAacGroup(string audioGroup, string aacType)
    {
        var method = typeof(AppleDownloadService).GetMethod(
            "IsMatchingAacGroup",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { audioGroup, aacType });
        Assert.IsType<bool>(result);
        return (bool)result;
    }
}
