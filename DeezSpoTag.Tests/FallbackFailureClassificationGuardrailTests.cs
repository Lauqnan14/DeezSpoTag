using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class FallbackFailureClassificationGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void EngineFallback_DoesNotTreatGenericDownloadFailuresAsTerminalFallbackExhaustion()
    {
        var coordinator = ReadSource("DeezSpoTag.Services/Download/Fallback/EngineFallbackCoordinator.cs");
        var classifier = ReadSource("DeezSpoTag.Services/Download/Fallback/FallbackFailureClassifier.cs");

        Assert.Contains("=> FallbackFailureClassifier.IsTerminal(attempt);", coordinator, StringComparison.Ordinal);
        Assert.Contains("DownloadFailed => false", classifier, StringComparison.Ordinal);
        Assert.Contains("ProviderTimeout => false", classifier, StringComparison.Ordinal);
        Assert.Contains("ProviderRateLimited => false", classifier, StringComparison.Ordinal);
        Assert.Contains("ProviderVerificationRequired => false", classifier, StringComparison.Ordinal);
        Assert.Contains("ProviderManifestUnavailable => false", classifier, StringComparison.Ordinal);
        Assert.Contains("ProviderTransient => false", classifier, StringComparison.Ordinal);
        Assert.DoesNotContain("\"download_failed\" => true", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineFallback_KeepsQualityAndConfigurationFailuresTerminal()
    {
        var classifier = ReadSource("DeezSpoTag.Services/Download/Fallback/FallbackFailureClassifier.cs");

        Assert.Contains("CatalogQualityBelowRequested => true", classifier, StringComparison.Ordinal);
        Assert.Contains("QualityBelowRequested => true", classifier, StringComparison.Ordinal);
        Assert.Contains("SameEngineBlocked => true", classifier, StringComparison.Ordinal);
        Assert.Contains("Unresolved => true", classifier, StringComparison.Ordinal);
        Assert.Contains("Unsupported => true", classifier, StringComparison.Ordinal);
        Assert.Contains("Unavailable => true", classifier, StringComparison.Ordinal);
        Assert.Contains("NotConfigured => true", classifier, StringComparison.Ordinal);
        Assert.Contains("AuthenticationRequired => true", classifier, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineFailureRecording_UsesSharedFallbackFailureClassifier()
    {
        var shared = ReadSource("DeezSpoTag.Services/Download/Shared/EngineAudioPostDownloadHelper.cs");
        var qobuz = ReadSource("DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs");

        Assert.Contains("FallbackFailureClassifier.Classify(exception)", shared, StringComparison.Ordinal);
        Assert.Contains("FallbackFailureClassifier.Classify(ex)", qobuz, StringComparison.Ordinal);
        Assert.DoesNotContain("\"download_failed\",\n                    exception.Message", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("\"download_failed\",\n                    ex.Message", qobuz, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineFallback_StillUsesOnlyTheExistingFallbackPlan()
    {
        var coordinator = ReadSource("DeezSpoTag.Services/Download/Fallback/EngineFallbackCoordinator.cs");

        Assert.Contains("BuildPlanSteps(request, payloadForSerialization)", coordinator, StringComparison.Ordinal);
        Assert.Contains("request.FallbackPlan", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildAlternate", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecondaryFallback", coordinator, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TidalFallback_DoesNotBlindlyTrustPersistedTidalId()
    {
        var fallbackSearch = ReadSource("DeezSpoTag.Services/Download/Fallback/EngineFallbackSearchService.cs");
        var tidal = ReadSource("DeezSpoTag.Services/Download/Tidal/TidalDownloadService.cs");

        Assert.DoesNotContain("tidal-id", fallbackSearch, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"https://tidal.com/browse/track/{tidalId}\"", fallbackSearch, StringComparison.Ordinal);
        Assert.Contains("ResolveTrackUrlForQualityAsync", fallbackSearch, StringComparison.Ordinal);
        Assert.Contains("TidalTrackCanSatisfyQuality", tidal, StringComparison.Ordinal);
        Assert.Contains("MediaMetadata?.Tags", tidal, StringComparison.Ordinal);
        Assert.Contains("TryResolveStereoCounterpartAsync", tidal, StringComparison.Ordinal);
        Assert.Contains("IsTidalAtmosOnlyTrack", tidal, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "&& !string.Equals(request.Engine, TidalEngine, StringComparison.OrdinalIgnoreCase)",
            fallbackSearch,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FallbackExhaustionMessage_IsConciseWhileHistoryRemainsStructured()
    {
        var coordinator = ReadSource("DeezSpoTag.Services/Download/Fallback/EngineFallbackCoordinator.cs");

        Assert.Contains("Download failed after all enabled sources were tried.", coordinator, StringComparison.Ordinal);
        Assert.Contains("payload.FallbackHistory", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildFallbackExhaustionDetail", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallback outcomes:", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("[failed/", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Tried enabled fallback steps:", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("HasLaterDistinctEngineStep", coordinator, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
