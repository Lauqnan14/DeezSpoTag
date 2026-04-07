using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Qobuz;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QobuzQualityFallbackTests
{
    [Fact]
    public void BuildRequest_EnablesQualityFallback_WhenServiceIsQobuz()
    {
        var request = QobuzRequestBuilder.BuildRequest(
            new QobuzQueueItem
            {
                Title = "Track",
                Artist = "Artist",
                Album = "Album",
                Quality = "27"
            },
            new DeezSpoTagSettings
            {
                Service = "qobuz",
                FallbackBitrate = true,
                QobuzQuality = "27"
            });

        Assert.True(request.AllowQualityFallback);
        Assert.Equal("27", request.Quality);
    }

    [Fact]
    public void BuildRequest_DisablesQualityFallback_WhenServiceIsAuto()
    {
        var request = QobuzRequestBuilder.BuildRequest(
            new QobuzQueueItem
            {
                Title = "Track",
                Artist = "Artist",
                Album = "Album",
                Quality = "27"
            },
            new DeezSpoTagSettings
            {
                Service = "auto",
                FallbackBitrate = true,
                QobuzQuality = "27"
            });

        Assert.False(request.AllowQualityFallback);
        Assert.Equal("27", request.Quality);
    }

    [Fact]
    public void GetQualityFallbackOrder_UsesQobuzHiResThenLowerTiers_WhenEnabled()
    {
        var order = InvokeGetQualityFallbackOrder("27", allowQualityFallback: true);

        Assert.Equal(new[] { "27", "7", "6" }, order);
    }

    [Fact]
    public void GetQualityFallbackOrder_RespectsDisabledFallback_WhenDisabled()
    {
        var order = InvokeGetQualityFallbackOrder("27", allowQualityFallback: false);

        Assert.Equal(new[] { "27" }, order);
    }

    [Fact]
    public void GetQualityFallbackOrder_UsesCDFallbackForMidTierQuality_WhenEnabled()
    {
        var order = InvokeGetQualityFallbackOrder("7", allowQualityFallback: true);

        Assert.Equal(new[] { "7", "6" }, order);
    }

    private static List<string> InvokeGetQualityFallbackOrder(string quality, bool allowQualityFallback)
    {
        var method = typeof(QobuzDownloadService).GetMethod(
            "GetQualityFallbackOrder",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { quality, allowQualityFallback });
        Assert.NotNull(result);

        return Assert.IsAssignableFrom<List<string>>(result);
    }
}
