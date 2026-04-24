using System.Reflection;
using DeezSpoTag.Services.Download.Apple;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppleAtmosVariantSelectionTests
{
    [Fact]
    public void SelectVariant_AtmosProfile_UsesAtmosMediaMetadata()
    {
        var master = new AppleHlsMasterManifest();
        master.Variants.Add(new AppleHlsVariantEntry
        {
            Uri = "https://example.com/audio-stereo.m3u8",
            AudioGroup = "audio-main-1",
            Codecs = "mp4a.40.2",
            AverageBandwidth = 640000
        });
        master.Variants.Add(new AppleHlsVariantEntry
        {
            Uri = "https://example.com/audio-atmos.m3u8",
            AudioGroup = "audio-main-2",
            Codecs = "mp4a.40.2",
            AverageBandwidth = 320000
        });
        master.Media.Add(new AppleHlsMediaEntry
        {
            Type = "AUDIO",
            GroupId = "audio-main-2",
            Name = "English Dolby Atmos",
            Channels = "16/JOC"
        });

        var request = new AppleDownloadRequest
        {
            PreferredProfile = "atmos",
            AtmosMax = 2768
        };

        var selected = InvokeSelectVariant(master, request);
        Assert.NotNull(selected);
        Assert.Equal("https://example.com/audio-atmos.m3u8", selected!.Uri);
    }

    [Theory]
    [InlineData("audio-atmos-768", 2768, true)]
    [InlineData("audio-atmos-6144", 2768, false)]
    [InlineData("audio-atmos-track-2024010101", 2768, true)]
    public void IsMatchingAtmosGroup_UsesSafeBitrateParsing(string audioGroup, int atmosMax, bool expected)
    {
        Assert.Equal(expected, InvokeIsMatchingAtmosGroup(audioGroup, atmosMax));
    }

    private static AppleHlsVariantEntry? InvokeSelectVariant(AppleHlsMasterManifest master, AppleDownloadRequest request)
    {
        var method = typeof(AppleDownloadService).GetMethod(
            "SelectVariant",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [master, request]);
        return result as AppleHlsVariantEntry;
    }

    private static bool InvokeIsMatchingAtmosGroup(string audioGroup, int maxBitrate)
    {
        var method = typeof(AppleDownloadService).GetMethod(
            "IsMatchingAtmosGroup",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [audioGroup, maxBitrate]);
        Assert.IsType<bool>(result);
        return (bool)result;
    }
}
