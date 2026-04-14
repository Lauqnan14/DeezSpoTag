using System.Reflection;
using DeezSpoTag.Services.Download.Deezer;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeezerFallbackBitrateParityTests
{
    [Fact]
    public void ResolveRequestedBitrate_PrefersExplicitPayloadBitrate()
    {
        var payload = new DeezerQueueItem
        {
            Bitrate = 9,
            Quality = "3"
        };

        var resolved = InvokeResolveRequestedBitrate(payload);

        Assert.Equal(9, resolved);
    }

    [Fact]
    public void ResolveRequestedBitrate_UsesNumericQuality_WhenBitrateMissing()
    {
        var payload = new DeezerQueueItem
        {
            Bitrate = 0,
            Quality = "3"
        };

        var resolved = InvokeResolveRequestedBitrate(payload);

        Assert.Equal(3, resolved);
    }

    [Fact]
    public void ResolveRequestedBitrate_ReturnsZero_WhenQualityIsNotNumeric()
    {
        var payload = new DeezerQueueItem
        {
            Bitrate = 0,
            Quality = "LOSSLESS"
        };

        var resolved = InvokeResolveRequestedBitrate(payload);

        Assert.Equal(0, resolved);
    }

    private static int InvokeResolveRequestedBitrate(DeezerQueueItem payload)
    {
        var method = typeof(DeezerEngineProcessor).GetMethod(
            "ResolveRequestedBitrate",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { payload });
        Assert.NotNull(result);
        return (int)result!;
    }
}
