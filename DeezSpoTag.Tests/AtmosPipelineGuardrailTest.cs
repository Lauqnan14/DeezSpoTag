using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AtmosPipelineGuardrailTest
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MultiQualitySettings_MigratesLegacyAtmosFallbackFlagsToCanonicalSetting(
        bool searchFallback,
        bool downloadFallback)
    {
        var json = $$"""
            {
              "atmosSearchFallback": {{searchFallback.ToString().ToLowerInvariant()}},
              "atmosDownloadFallback": {{downloadFallback.ToString().ToLowerInvariant()}}
            }
            """;

        var settings = JsonSerializer.Deserialize<MultiQualityDownloadSettings>(json);

        Assert.NotNull(settings);
        Assert.True(settings!.AtmosFallbackEnabled);

        var serialized = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Assert.Contains("\"atmosFallbackEnabled\":true", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("atmosSearchFallback", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("atmosDownloadFallback", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsApi_MigratesLegacyAtmosFallbackFlagsBeforeMerging()
    {
        var incoming = JsonNode.Parse("""
            {
              "multiQuality": {
                "atmosSearchFallback": true,
                "atmosDownloadFallback": false
              }
            }
            """)!.AsObject();
        var method = typeof(SettingsApiController).GetMethod(
            "NormalizeIncomingAliases",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("NormalizeIncomingAliases was not found.");

        method.Invoke(null, [incoming]);

        var multiQuality = Assert.IsType<JsonObject>(incoming["multiQuality"]);
        Assert.True(multiQuality["atmosFallbackEnabled"]!.GetValue<bool>());
        Assert.False(multiQuality.ContainsKey("atmosSearchFallback"));
        Assert.False(multiQuality.ContainsKey("atmosDownloadFallback"));
    }

    [Fact]
    public void MultiQualitySettings_CanonicalJsonRoundTripPreservesEverySetting()
    {
        var expected = new MultiQualityDownloadSettings
        {
            Enabled = true,
            SecondaryEnabled = true,
            PrimaryDestinationFolderId = 12,
            SecondaryDestinationFolderId = 34,
            AtmosEngine = "tidal",
            AtmosFallbackEnabled = true
        };

        var json = JsonSerializer.Serialize(expected);
        var actual = JsonSerializer.Deserialize<MultiQualityDownloadSettings>(json);

        Assert.NotNull(actual);
        Assert.Equal(expected.Enabled, actual!.Enabled);
        Assert.Equal(expected.SecondaryEnabled, actual.SecondaryEnabled);
        Assert.Equal(expected.PrimaryDestinationFolderId, actual.PrimaryDestinationFolderId);
        Assert.Equal(expected.SecondaryDestinationFolderId, actual.SecondaryDestinationFolderId);
        Assert.Equal(expected.AtmosEngine, actual.AtmosEngine);
        Assert.Equal(expected.AtmosFallbackEnabled, actual.AtmosFallbackEnabled);
    }

    [Fact]
    public void QueueSettingsSnapshot_MigratesLegacyAtmosFallbackForPersistedDownloads()
    {
        var payload = JsonNode.Parse("""
            {
              "sourceSettingsSnapshot": {
                "multiQuality": {
                  "atmosDownloadFallback": true
                }
              }
            }
            """)!.AsObject();

        var snapshot = QueueSourceSettingsSnapshot.ReadFromPayload(payload);

        Assert.NotNull(snapshot?.MultiQuality);
        Assert.True(snapshot!.MultiQuality!.AtmosFallbackEnabled);
    }

    [Fact]
    public void AtmosProviderOrder_StartsWithSelectedProviderAndUsesOnlyAtmosQualities()
    {
        var settings = new DeezSpoTagSettings
        {
            MultiQuality = new MultiQualityDownloadSettings { AtmosFallbackEnabled = true }
        };

        var sources = DownloadSourceOrder.ResolveAtmosSources(
            settings,
            preferredEngine: "tidal");

        Assert.Equal(
            ["tidal|DOLBY_ATMOS", "apple|ATMOS", "amazon|DOLBY_ATMOS"],
            sources);
    }

    [Fact]
    public void AtmosProviderOrder_RespectsCustomEngineAndQualitySelection()
    {
        var settings = new DeezSpoTagSettings
        {
            DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault()
        };
        settings.DownloadEngineOrder.Enabled = true;
        settings.MultiQuality = new MultiQualityDownloadSettings { AtmosFallbackEnabled = true };
        foreach (var engine in settings.DownloadEngineOrder.Engines)
        {
            engine.Enabled = engine.Engine is "tidal" or "amazon";
            foreach (var quality in engine.Qualities)
            {
                quality.Enabled = quality.Quality == "DOLBY_ATMOS";
            }
        }

        var sources = DownloadSourceOrder.ResolveAtmosSources(
            settings,
            preferredEngine: "amazon");

        Assert.Equal(["amazon|DOLBY_ATMOS", "tidal|DOLBY_ATMOS"], sources);
    }

    [Fact]
    public void AtmosProviderOrder_DoesNotUseAnotherProviderWhenFallbackIsDisabled()
    {
        var settings = new DeezSpoTagSettings
        {
            MultiQuality = new MultiQualityDownloadSettings { AtmosFallbackEnabled = false }
        };

        var sources = DownloadSourceOrder.ResolveAtmosSources(
            settings,
            preferredEngine: "amazon");

        Assert.Equal(["amazon|DOLBY_ATMOS"], sources);
    }

    [Fact]
    public void EnhancementAndWatchlist_UseCanonicalMultiProviderAtmosAdmission()
    {
        var enhancement = ReadSource("DeezSpoTag.Web/Services/QualityScannerService.cs");
        var watchlist = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");

        Assert.DoesNotContain("FindAppleAtmosMatchAsync", enhancement, StringComparison.Ordinal);
        Assert.Contains("PreferredEngine = \"auto\"", enhancement, StringComparison.Ordinal);
        Assert.Contains("allowAutomaticSecondaryQuality: false", enhancement, StringComparison.Ordinal);

        Assert.DoesNotContain("CreateAtmosOnlyIntent", watchlist, StringComparison.Ordinal);
        Assert.Contains("CreateAtmosIntent", watchlist, StringComparison.Ordinal);
        Assert.Contains("QobuzId = sourceIntent.QobuzId", watchlist, StringComparison.Ordinal);
        Assert.Contains("TidalId = sourceIntent.TidalId", watchlist, StringComparison.Ordinal);
        Assert.Contains("AmazonId = sourceIntent.AmazonId", watchlist, StringComparison.Ordinal);
        Assert.Contains("PreferredEngine = DownloadSourceCatalog.Auto", watchlist, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeAtmosResolution_VerifiesEveryAtmosProvider()
    {
        var fallback = ReadSource("DeezSpoTag.Services/Download/Fallback/EngineFallbackSearchService.cs");
        var intent = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        Assert.Contains("ResolveAtmosTrackAsync", fallback, StringComparison.Ordinal);
        Assert.Contains("ResolveAmazonAtmosFallbackTrackAsync", fallback, StringComparison.Ordinal);
        Assert.Contains("IsAtmosAvailableAsync", fallback, StringComparison.Ordinal);
        Assert.Contains("TryEnqueueAppleAtmosAsync", intent, StringComparison.Ordinal);
        Assert.Contains("TryEnqueueTidalAtmosAsync", intent, StringComparison.Ordinal);
        Assert.Contains("TryEnqueueAmazonAtmosAsync", intent, StringComparison.Ordinal);
        Assert.DoesNotContain("IsAppleAtmosQuality", intent, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueAtmos_SkipsTracksAlreadyInTheAtmosLibraryAndHonoursTheRunDestination()
    {
        var scanner = ReadSource("DeezSpoTag.Web/Services/QualityScannerService.cs");
        var workflows = ReadSource("DeezSpoTag.Web/Services/AutoTagService.EnhancementWorkflows.cs");

        // Dedupe consults the Atmos library itself, not just the download queue: a track whose
        // Atmos copy already sits in the Atmos destination is not queued again.
        Assert.Contains("atmos_already_in_library", scanner, StringComparison.Ordinal);
        Assert.Contains("IsSameTrackAsAtmosCopy", scanner, StringComparison.Ordinal);
        Assert.Contains("GetQualityScanTracksAsync", scanner, StringComparison.Ordinal);

        // The run's picked destination wins; the folders typed Atmos on the Folder tab decide
        // when the run picked none, and the global multi-quality destination is only the fallback.
        Assert.Contains("var atmosDestinationFolderId = request.AtmosDestinationFolderId;", scanner, StringComparison.Ordinal);
        Assert.Contains("FallbackAtmosDestinationFolderId: fallbackAtmosDestinationFolderId,", scanner, StringComparison.Ordinal);
        Assert.Contains("IsAtmosDestinationFolder", scanner, StringComparison.Ordinal);
        Assert.Contains("atmos_destination_ambiguous", scanner, StringComparison.Ordinal);
        Assert.Contains("AtmosDestinationFolderId = atmosDestinationFolderId,", workflows, StringComparison.Ordinal);
        Assert.Contains("ResolveAtmosDestinationFolderId(qualityChecks)", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueAtmosPicker_AppearsOnlyWhenMoreThanOneAtmosFolderIsTyped()
    {
        var view = ReadSource("DeezSpoTag.Web/Views/AutoTag/Index.cshtml");
        var scripts = ReadSource("DeezSpoTag.Web/wwwroot/js/autotag.js");

        // The picker lives in the checks card next to "Queue Atmos alternatives", hidden until needed.
        Assert.Contains("enhancementAtmosDestinationGroup", view, StringComparison.Ordinal);
        Assert.Contains("enhancementAtmosDestinationFolder", view, StringComparison.Ordinal);

        // Only folders typed Atmos are offered, and a single one needs no choice: the server uses it.
        Assert.Contains("desiredQuality.includes(\"atmos\")", scripts, StringComparison.Ordinal);
        Assert.Contains("atmosFolders.length <= 1", scripts, StringComparison.Ordinal);

        // The chosen folder travels with the quality checks config the runner reads.
        Assert.Contains(
            "qualityChecks.atmosDestinationFolderId = readEnhancementAtmosDestinationFolderId();",
            scripts,
            StringComparison.Ordinal);

        // A stored choice survives a reload even though the option list is fetched asynchronously.
        Assert.Contains("state.config?.enhancement?.qualityChecks?.atmosDestinationFolderId", scripts, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
