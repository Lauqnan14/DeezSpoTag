using System;
using System.Reflection;
using System.Text;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Tidal;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TidalStereoQualitySeparationTests
{
    [Theory]
    [InlineData("LOW", "LOW")]
    [InlineData("HIGH", "HIGH")]
    [InlineData("LOSSLESS", "LOSSLESS")]
    [InlineData("HI_RES", "HI_RES")]
    [InlineData("HI_RES_LOSSLESS", "HI_RES_LOSSLESS")]
    [InlineData("MAX_HI_RES", "HI_RES_LOSSLESS")]
    [InlineData("ATMOS", "DOLBY_ATMOS")]
    [InlineData("DOLBY_ATMOS", "DOLBY_ATMOS")]
    public void TidalRequestBuilder_PreservesDistinctFallbackTier(string inputQuality, string expectedQueueQuality)
    {
        var item = new TidalQueueItem { Quality = inputQuality };
        var settings = new DeezSpoTagSettings { TidalQuality = "LOSSLESS" };

        var request = TidalRequestBuilder.BuildRequest(item, settings);

        Assert.Equal(expectedQueueQuality, request.Quality);
    }

    [Fact]
    public void TidalRequestBuilder_UsesConfiguredTidalQualityWhenPayloadHasNoQuality()
    {
        var item = new TidalQueueItem();
        var settings = new DeezSpoTagSettings { TidalQuality = "HI_RES" };

        var request = TidalRequestBuilder.BuildRequest(item, settings);

        Assert.Equal("HI_RES", request.Quality);
    }

    [Theory]
    [InlineData("LOSSLESS", "LOSSLESS")]
    [InlineData("HI_RES", "HI_RES")]
    [InlineData("HI_RES_LOSSLESS", "HI_RES_LOSSLESS")]
    public void TidalApiRequestQuality_PreservesStereoFallbackStep(string inputQuality, string expectedRequestQuality)
    {
        var type = typeof(DeezSpoTag.Services.Download.QualityCatalog).Assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.TidalStereoQuality",
            throwOnError: true)!;
        var method = type.GetMethod(
            "ToTidalRequestQuality",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null)!;

        var requestQuality = (string)method.Invoke(null, [inputQuality])!;

        Assert.Equal(expectedRequestQuality, requestQuality);
    }

    [Theory]
    [InlineData("HI_RES", "audio/mp4", "mp4a.40.2", 44100, 0)]
    [InlineData("HI_RES", "audio/flac", "flac", 44100, 16)]
    [InlineData("HI_RES_LOSSLESS", "audio/flac", "flac", 96000, 24)]
    [InlineData("LOSSLESS", "audio/flac", "flac", 96000, 24)]
    public void TidalManifestGate_RejectsWrongStereoQualityBeforeDownload(
        string requestedQuality,
        string mimeType,
        string codec,
        int sampleRate,
        int bitDepth)
    {
        var exception = Assert.Throws<TargetInvocationException>(() =>
            EnsureManifestMatchesRequest(
                BuildDashManifestCandidate(mimeType, codec, sampleRate, bitDepth),
                requestedQuality));

        Assert.Contains("Tidal manifest quality mismatch", exception.InnerException?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("LOSSLESS", "audio/flac", "flac", 44100, 16)]
    [InlineData("HI_RES", "audio/flac", "flac", 96000, 24)]
    [InlineData("HI_RES_LOSSLESS", "audio/flac", "flac", 192000, 24)]
    public void TidalManifestGate_AcceptsMatchingStereoQualityBeforeDownload(
        string requestedQuality,
        string mimeType,
        string codec,
        int sampleRate,
        int bitDepth)
    {
        EnsureManifestMatchesRequest(
            BuildDashManifestCandidate(mimeType, codec, sampleRate, bitDepth),
            requestedQuality);
    }

    private static void EnsureManifestMatchesRequest(string candidate, string requestedQuality)
    {
        var method = typeof(TidalDownloadService).GetMethod(
            "EnsureTidalManifestMatchesRequestedQuality",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [candidate, requestedQuality, false]);
    }

    private static string BuildDashManifestCandidate(
        string mimeType,
        string codec,
        int sampleRate,
        int bitDepth)
    {
        var bitDepthAttribute = bitDepth > 0 ? $" bitDepth=\"{bitDepth}\"" : string.Empty;
        var manifest = $"""
            <MPD>
              <Period>
                <AdaptationSet mimeType="{mimeType}" contentType="audio">
                  <Representation bandwidth="1000000" codecs="{codec}" audioSamplingRate="{sampleRate}"{bitDepthAttribute}>
                    <SegmentTemplate initialization="https://media.example/init.mp4" media="https://media.example/segment-$Number$.m4s" startNumber="1">
                      <SegmentTimeline><S d="1" /></SegmentTimeline>
                    </SegmentTemplate>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        return "MANIFEST:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest));
    }
}
