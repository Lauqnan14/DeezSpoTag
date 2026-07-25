using System;
using System.IO;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AtmosPipelineGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void AtmosProviderOrder_StartsWithSelectedProviderAndUsesOnlyAtmosQualities()
    {
        var settings = new DeezSpoTagSettings();

        var sources = DownloadSourceOrder.ResolveAtmosSources(
            settings,
            preferredEngine: "tidal",
            includeFallbackEngines: true);

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
            preferredEngine: "amazon",
            includeFallbackEngines: true);

        Assert.Equal(["amazon|DOLBY_ATMOS", "tidal|DOLBY_ATMOS"], sources);
    }

    [Fact]
    public void AtmosProviderOrder_DoesNotUseAnotherProviderWhenFallbackIsDisabled()
    {
        var settings = new DeezSpoTagSettings();

        var sources = DownloadSourceOrder.ResolveAtmosSources(
            settings,
            preferredEngine: "amazon",
            includeFallbackEngines: false);

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

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
