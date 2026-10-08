using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class FallbackFailureClassificationGuardrailTest
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

    /// <summary>
    /// The two things the shortcut above cannot be allowed to cost us: that an Atmos request and a
    /// stereo request stay on separate paths, and that the variant is checked again at the moment
    /// the audio is actually fetched. Both are asserted here because both are load-bearing for
    /// "download the variant that was asked for", and neither is covered by the rule above.
    /// </summary>
    [Fact]
    public void TidalVariantChoice_IsMadeOnceAndCheckedAgainAtDownloadTime()
    {
        var fallbackSearch = ReadSource("DeezSpoTag.Services/Download/Fallback/EngineFallbackSearchService.cs");
        var tidal = ReadSource("DeezSpoTag.Services/Download/Tidal/TidalDownloadService.cs");

        // One decision, two paths, and the decision is made on the request itself rather than on
        // whatever id happens to be available. An Atmos request is resolved as Atmos; everything
        // else goes through the quality gate. There is no third path.
        Assert.Contains("if (IsAtmosRequest(request))", fallbackSearch, StringComparison.Ordinal);
        Assert.Contains("ResolveAtmosTrackAsync(", fallbackSearch, StringComparison.Ordinal);
        Assert.Contains("ResolveTrackUrlForQualityAsync(", fallbackSearch, StringComparison.Ordinal);

        // The stereo path refuses an Atmos-only id and asks for the stereo sibling on the same
        // album rather than quietly returning the Atmos one.
        Assert.Contains("private static bool IsTidalAtmosOnlyTrack", tidal, StringComparison.Ordinal);
        Assert.Contains("TryResolveStereoCounterpartAsync(", tidal, StringComparison.Ordinal);

        // Resolution is not the last word. The manifest is re-checked when the audio is fetched, so
        // a variant that changed between resolving and downloading is still caught. This is what
        // lets the same-engine-url shortcut above stay cheap without becoming a hole.
        const string manifestGate = "EnsureTidalManifestMatchesRequestedQuality(";
        Assert.Contains($"private static void {manifestGate}", tidal, StringComparison.Ordinal);

        // Declared and actually called. A guard that exists but is only ever defined would leave
        // the download path unchecked while still satisfying a "Contains the method" assertion.
        var gateUses = tidal.Split(manifestGate).Length - 1;
        Assert.True(
            gateUses >= 3,
            $"The manifest must be re-checked wherever audio is fetched, not only where it is declared. Found {gateUses} mentions.");
    }

    [Fact]
    public void FallbackExhaustionMessage_IsConciseWhileHistoryRemainsStructured()
    {
        // The pinned summary line changed: the item's error is now one short line naming what happened, because
        // "Download failed after all enabled sources were tried" was half the line and repeated the status beside
        // it. Everything this test actually guards - no dump of outcomes on the item, structured history
        // preserved - is unchanged and still asserted below.
        var coordinator = ReadSource("DeezSpoTag.Services/Download/Fallback/EngineFallbackCoordinator.cs");

        Assert.Contains("BuildExhaustionMessage", coordinator, StringComparison.Ordinal);
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
