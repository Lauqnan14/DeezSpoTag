using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Shared;
using System.Text.Json;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QobuzQualityFallbackTests
{
    [Fact]
    public void BuildRequest_UsesOnlyTheCurrentPersistedPlanQuality()
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
                QobuzQuality = "6"
            });

        Assert.Equal("27", request.Quality);
        Assert.Null(typeof(QobuzDownloadRequest).GetProperty("AllowQualityFallback"));
        Assert.Null(typeof(QobuzDownloadService).GetMethod(
            "GetQualityFallbackOrder",
            BindingFlags.NonPublic | BindingFlags.Static));
    }

    [Theory]
    [InlineData(16, 44.1, "6")]
    [InlineData(24, 48, "7")]
    [InlineData(24, 96, "7")]
    [InlineData(32, 192, "27")]
    public void MapCatalogQuality_MapsQobuzMetadataToEngineQuality(int bitDepth, double sampleRate, string expected)
    {
        Assert.Equal(expected, InvokeMapCatalogQuality(bitDepth, sampleRate));
    }

    [Theory]
    [InlineData("27", "6", "6")]
    [InlineData("27", "7", "7")]
    [InlineData("27", "27", "27")]
    [InlineData("7", "27", "7")]
    [InlineData("6", "27", "6")]
    public void SelectQualityWithinCatalogCeiling_UsesCatalogAsMaximumQuality(
        string requested,
        string catalog,
        string expected)
    {
        Assert.Equal(expected, InvokeSelectQualityWithinCatalogCeiling(requested, catalog));
    }

    [Fact]
    public void ToQueuePayload_IncludesQobuzCatalogQualityDecisionFields()
    {
        var payload = new QobuzQueueItem
        {
            Id = "queue-1",
            Title = "Track",
            Artist = "Artist",
            Quality = "6",
            QobuzRequestedQuality = "27",
            QobuzResolvedQuality = "6",
            QobuzMaximumBitDepth = 16,
            QobuzMaximumSamplingRate = 44.1,
            QobuzCatalogQuality = "6",
            QobuzQualityDecisionReason = "catalog_quality_lower_than_requested"
        };

        var json = JsonSerializer.Serialize(payload.ToQueuePayload());

        Assert.Contains("\"qobuzRequestedQuality\":\"27\"", json);
        Assert.Contains("\"qobuzResolvedQuality\":\"6\"", json);
        Assert.Contains("\"qobuzMaximumBitDepth\":16", json);
        Assert.Contains("\"qobuzMaximumSamplingRate\":44.1", json);
        Assert.Contains("\"qobuzCatalogQuality\":\"6\"", json);
        Assert.Contains("\"qobuzQualityDecisionReason\":\"catalog_quality_lower_than_requested\"", json);
    }

    [Fact]
    public void ProviderUnknownPath_DoesNotContinueToAudioDownload()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"));
        var source = File.ReadAllText(sourcePath);
        var unknownBranchStart = source.IndexOf(
            "payload.QobuzQualityDecisionReason = \"provider_quality_unknown\";",
            StringComparison.Ordinal);

        Assert.True(unknownBranchStart >= 0);
        var unknownBranchEnd = source.IndexOf(
            "payload.QobuzMaximumBitDepth = resolution.BitDepth",
            unknownBranchStart,
            StringComparison.Ordinal);
        Assert.True(unknownBranchEnd > unknownBranchStart);
        var unknownBranch = source[unknownBranchStart..unknownBranchEnd];

        Assert.Contains("QobuzQualityDecisionResult.Skip", unknownBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("QobuzQualityDecisionResult.Continue", unknownBranch, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"download_url":"https://audio.test/track.flac","quality_label":"6","bit_depth":16,"sampling_rate":44.1}""", 16, 44.1, "6")]
    [InlineData("""{"data":{"url":"https://audio.test/track.flac","quality_label":"7","bit_depth":24,"sampling_rate":96}}""", 24, 96, "7")]
    [InlineData("""{"url":"https://audio.test/track.flac","quality_label":"27","bit_depth":"24","sampling_rate":"192000"}""", 24, 192, "27")]
    public void ProviderQualityResolution_DrivesThePlanWithoutCatalogLookup(
        string json,
        int expectedBitDepth,
        double expectedSamplingRate,
        string expectedQuality)
    {
        var method = typeof(QobuzDownloadService).GetMethod(
            "TryExtractQualityResolution",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { json, "test", 280142894L, "7", "test-provider", null };
        var success = Assert.IsType<bool>(method!.Invoke(null, args));
        var resolution = Assert.IsType<QobuzQualityResolution>(args[5]);

        Assert.True(success);
        Assert.Equal(expectedBitDepth, resolution.BitDepth);
        Assert.Equal(expectedSamplingRate, resolution.SamplingRate);
        Assert.Equal(expectedQuality, resolution.AvailableQualityCode);
        Assert.Equal(expectedQuality, InvokeMapCatalogQuality(resolution.BitDepth, resolution.SamplingRate));
    }

    [Fact]
    public void QobuzProcessor_UsesProviderQualityResolutionAndNotCatalogTrackQuality()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("_qobuzDownloader.ResolveQualityAsync(", source, StringComparison.Ordinal);
        Assert.Contains("request.ResolvedQuality = resolution;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetTrackQuality(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveCatalogQualitySignalAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SixteenBitProviderResult_SkipsHiResButSatisfiesCdLossless()
    {
        const string availableQuality = "6";

        Assert.Equal("6", InvokeSelectQualityWithinCatalogCeiling("7", availableQuality));
        Assert.Equal("6", InvokeSelectQualityWithinCatalogCeiling("6", availableQuality));
        Assert.NotEqual("7", InvokeSelectQualityWithinCatalogCeiling("7", availableQuality));
    }

    [Fact]
    public void ToQueuePayload_IncludesSourceSettingsSnapshot()
    {
        var payload = new QobuzQueueItem
        {
            Id = "queue-1",
            Title = "Track",
            Artist = "Artist",
            Quality = "27",
            SourceSettingsSnapshot = QueueSourceSettingsSnapshot.Capture(new DeezSpoTagSettings
            {
                Service = "custom",
                QobuzQuality = "27",
                FallbackSearch = true,
                DownloadEngineOrder = new DownloadEngineOrderSettings
                {
                    Enabled = true,
                    Engines = new List<DownloadEngineOrderItem>
                    {
                        new()
                        {
                            Engine = "qobuz",
                            Enabled = true,
                            Qualities = new List<DownloadEngineQualityItem>
                            {
                                new() { Quality = "27", Enabled = true }
                            }
                        }
                    }
                }
            })
        };

        var json = JsonSerializer.Serialize(payload.ToQueuePayload());

        Assert.Contains("\"sourceSettingsSnapshot\"", json);
        Assert.Contains("\"Service\":\"custom\"", json);
        Assert.Contains("\"QobuzQuality\":\"27\"", json);
        Assert.Contains("\"FallbackSearch\":true", json);
        Assert.Contains("\"DownloadEngineOrder\"", json);
    }

    private static string InvokeMapCatalogQuality(int bitDepth, double sampleRate)
    {
        var method = typeof(QobuzEngineProcessor).GetMethod(
            "MapCatalogQuality",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { bitDepth, sampleRate });
        return Assert.IsType<string>(result);
    }

    private static string InvokeSelectQualityWithinCatalogCeiling(string requested, string catalog)
    {
        var method = typeof(QobuzEngineProcessor).GetMethod(
            "SelectQualityWithinCatalogCeiling",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { requested, catalog });
        return Assert.IsType<string>(result);
    }

}
