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

        Assert.Contains("BuildPlanSteps(request, settings)", coordinator, StringComparison.Ordinal);
        Assert.Contains("request.FallbackPlan", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildAlternate", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecondaryFallback", coordinator, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
