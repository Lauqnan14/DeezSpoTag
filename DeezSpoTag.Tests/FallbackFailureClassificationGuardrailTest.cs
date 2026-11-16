using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class FallbackFailureClassificationGuardrailTest
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void GenericSummaryDoesNotHideTemporaryFailureInSavedProviderOutcomes()
    {
        var failure = new AggregateException("Selected downloads failed",
            new InvalidOperationException("No resolvable URL"),
            new HttpRequestException("Provider HTTP 503", null, HttpStatusCode.ServiceUnavailable));
        Assert.True(FallbackFailureClassifier.IsTemporaryProviderFailure(failure));
    }

    [Theory]
    [InlineData(404, "TRACK_UNAVAILABLE", false, "none", "unavailable")]
    [InlineData(409, "QUALITY_UNAVAILABLE", false, "none", "quality_below_requested")]
    [InlineData(404, "TRACK_UNAVAILABLE", true, "none", "provider_transient")]
    [InlineData(404, "TRACK_UNAVAILABLE", false, "poll_existing", "provider_transient")]
    [InlineData(503, "TRACK_UNAVAILABLE", false, "none", "provider_transient")]
    [InlineData(404, "UPSTREAM_FAILURE", false, "none", "provider_transient")]
    public void OnlyExplicitNonRetryableProviderAbsenceIsConclusive(int status, string code, bool retryable, string mode, string expected)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { code, retryable, retry_mode = mode });
        var failure = new HttpRequestException($"HTTP {status}: {body}", null, (HttpStatusCode)status);
        Assert.Equal(expected, FallbackFailureClassifier.Classify(failure));
        var retained = new HttpRequestException($"HTTP {status}", null, (HttpStatusCode)status);
        FallbackFailureClassifier.AttachProviderErrorContract(retained, body);
        Assert.Equal(expected, FallbackFailureClassifier.Classify(retained));
    }

    [Theory]
    [InlineData("identity_unresolved")]
    [InlineData("unresolved")]
    [InlineData("catalog_lookup_failed")]
    [InlineData("authentication_required")]
    [InlineData("provider_timeout")]
    [InlineData("provider_rate_limited")]
    public void LookupOrOperationalFailureDoesNotConfirmSelectionAbsence(string errorClass)
        // catalog_quality_below_requested is a confirmed quality absence now
        // (QualityOnlyAbsence_CatalogQualityBelowRequested_IsConclusive), so it is no
        // longer pinned as inconclusive here.
        => Assert.False(FallbackFailureClassifier.IsPlanUnavailable(
            [new("step-0", "tidal", "LOSSLESS", [], "isrc")], [new("step-0", "failed", errorClass, "No delivery confirmation")]));

    [Fact]
    public void CorruptedOutcomeIsNotConfirmationOfUnavailability()
        => Assert.False(FallbackFailureClassifier.IsPlanUnavailable(
            [new("step-0", "tidal", "LOSSLESS", [], "isrc")], [null!]));

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

    [Theory]
    [InlineData("tidal", "provider_verification_required", "Tidal (LOSSLESS) download API session needs verification.")]
    [InlineData("qobuz", "provider_verification_required", "Qobuz (LOSSLESS) download API session needs verification.")]
    [InlineData("amazon", "provider_verification_required", "Amazon Music (LOSSLESS) download API session needs verification.")]
    [InlineData("tidal", "authentication_required", "Tidal (LOSSLESS) needs signing in.")]
    public void FallbackSummaryDistinguishesApiVerificationFromAccountSignIn(string engine, string errorClass, string expected)
    {
        var payload = new TidalQueueItem
        {
            FallbackPlan = [new("step-0", engine, "LOSSLESS", [], "mapped_url")],
            FallbackHistory = [new("step-0", "failed", errorClass, "Provider session unavailable")]
        };
        var method = typeof(EngineFallbackCoordinator).GetMethod("BuildExhaustionMessage", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(expected, method.Invoke(null, [payload, engine]));
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

    [Theory]
    [InlineData(401, "authentication_required")]
    [InlineData(403, "authentication_required")]
    [InlineData(429, "provider_rate_limited")]
    [InlineData(503, "catalog_lookup_failed")]
    public void FailedCatalogAccessRetainsItsCause(int status, string expected)
    {
        var failure = new HttpRequestException($"Tidal catalog lookup failed: HTTP {status}.", null, (HttpStatusCode)status);
        Assert.Equal(expected, FallbackFailureClassifier.Classify(failure));
        Assert.Equal(expected, FallbackFailureClassifier.Classify(new InvalidOperationException("Tidal URL download failed", failure)));
    }

    [Theory]
    [InlineData("identity_unresolved: no approved ID", "identity_unresolved")]
    [InlineData("provider_quality_unknown", "provider_quality_unknown")]
    [InlineData("Tidal quality_below_requested", "quality_below_requested")]
    [InlineData("Qobuz public download session is invalid and must be renewed.", "provider_verification_required")]
    [InlineData("Tidal download verification session is unavailable", "provider_verification_required")]
    [InlineData("Amazon Music metadata session could not be initialized: missing csrf.", "authentication_required")]
    public void ResolutionFailuresDoNotBecomeCatalogAbsence(string message, string expected)
        => Assert.Equal(expected, FallbackFailureClassifier.Classify(new InvalidOperationException(message)));

    [Fact]
    public void MixedSourceFailureRetainsRetryableCatalogAccessFailure()
    {
        var exception = new AggregateException("Qobuz catalog lookup failed", new Exception[]
        {
            new HttpRequestException("HTTP 401", null, HttpStatusCode.Unauthorized),
            new HttpRequestException("HTTP 503", null, HttpStatusCode.ServiceUnavailable)
        });
        Assert.Equal("catalog_lookup_failed", FallbackFailureClassifier.Classify(exception));
        Assert.False(FallbackFailureClassifier.IsTerminalErrorClass(FallbackFailureClassifier.Classify(exception)));
    }

    [Fact]
    public void WrappedCatalogNetworkFailureRetainsCatalogOperation()
    {
        var network = new HttpRequestException("network unavailable");
        var catalog = new HttpRequestException("Tidal catalog lookup failed: network unavailable", network);
        Assert.Equal("catalog_lookup_failed", FallbackFailureClassifier.Classify(catalog));
        Assert.Equal("catalog_lookup_failed", FallbackFailureClassifier.Classify(new InvalidOperationException("Tidal URL download failed", catalog)));
        Assert.Equal("catalog_lookup_failed", FallbackFailureClassifier.Classify(new AggregateException("Qobuz catalog lookup failed", network)));
    }

    [Fact]
    public void AggregateOperationalFailuresRemainRecoverableWhileUnexpectedBugsPropagate()
    {
        Assert.True(FallbackFailureClassifier.IsRecoverableProviderFailure(new AggregateException(new HttpRequestException("HTTP 503"))));
        Assert.False(FallbackFailureClassifier.IsRecoverableProviderFailure(new AggregateException(new NullReferenceException())));
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    // ---- Task 2: one evidence rule for unavailable and monitor eligibility ----
    // The evidence rule reads the item's SAVED selection: a disabled plan step's
    // inconclusive outcome must not cast doubt on the enabled steps' absence evidence,
    // and only confirmed absence classes (the catalogue cannot offer the recording or
    // the requested quality) count as conclusive.

    private static QueueSourceSettingsSnapshot SnapshotWith(params (string Engine, string[] Qualities)[] engines)
        => new()
        {
            DownloadEngineOrder = new DownloadEngineOrderSettings
            {
                Enabled = true,
                Engines = engines.Select(pair => new DownloadEngineOrderItem
                {
                    Engine = pair.Engine,
                    Qualities = pair.Qualities.Select(quality => new DownloadEngineQualityItem
                    {
                        Quality = quality
                    }).ToList()
                }).ToList()
            }
        };

    [Fact]
    public void QualityOnlyAbsence_CatalogQualityBelowRequested_IsConclusive()
    {
        var plan = new List<FallbackPlanStep> { new("step-0", "tidal", "HI_RES", [], "isrc") };
        var history = new List<FallbackAttempt>
        {
            new("step-0", "failed", "catalog_quality_below_requested", "Catalog tops out at LOSSLESS")
        };

        Assert.True(FallbackFailureClassifier.IsPlanUnavailable(plan, history));
        Assert.True(FallbackFailureClassifier.IsPlanUnavailable(plan, history, null));
    }

    [Fact]
    public void SavedPlan_DisabledStepInconclusiveOutcome_DoesNotHideAbsenceOfEnabledSteps()
    {
        // Saved selection: qobuz 27 + tidal HI_RES enabled; deezer 9 was disabled after
        // enqueue. The disabled step's last outcome is a timeout; the snapshot-less rule
        // would count that inconclusive outcome against the whole plan and keep a
        // conclusively absent recording "recoverable" forever.
        var snapshot = SnapshotWith(("qobuz", ["27"]), ("tidal", ["HI_RES"]));
        var plan = new List<FallbackPlanStep>
        {
            new("step-0", "qobuz", "27", [], "isrc"),
            new("step-1", "tidal", "HI_RES", [], "isrc"),
            new("step-2", "deezer", "9", [], "mapped_url")
        };
        var history = new List<FallbackAttempt>
        {
            new("step-0", "skipped", "unavailable", "Confirmed missing recording"),
            new("step-1", "skipped", "unavailable", "Confirmed missing recording"),
            new("step-2", "skipped", "provider_timeout", "deezer provider timed out")
        };

        Assert.True(FallbackFailureClassifier.IsPlanUnavailable(plan, history, snapshot));
        Assert.Equal(["step-0", "step-1"],
            FallbackFailureClassifier.EnabledSavedPlanSteps(plan, snapshot).Select(step => step.StepId));
        Assert.Equal(plan, FallbackFailureClassifier.EnabledSavedPlanSteps(plan, null));
    }

    [Fact]
    public void SavedPlan_MixedProviderOutcomes_StayRecoverable()
    {
        var snapshot = SnapshotWith(("qobuz", ["7"]), ("tidal", ["HI_RES"]), ("amazon", ["HD_FLAC"]));
        var plan = new List<FallbackPlanStep>
        {
            new("step-0", "qobuz", "7", [], "isrc"),
            new("step-1", "tidal", "HI_RES", [], "isrc"),
            new("step-2", "amazon", "HD_FLAC", [], "mapped_url")
        };
        var history = new List<FallbackAttempt>
        {
            new("step-0", "skipped", "unavailable", "Confirmed missing recording"),
            new("step-1", "skipped", "provider_timeout", "tidal provider timed out"),
            new("step-2", "skipped", "identity_unresolved", "missing_url")
        };

        Assert.False(FallbackFailureClassifier.IsPlanUnavailable(plan, history, snapshot));
    }

    [Fact]
    public void MissingOutcomeForAnEnabledStep_IsNotConfirmationOfAbsence()
    {
        var snapshot = SnapshotWith(("qobuz", ["7"]), ("tidal", ["HI_RES"]));
        var plan = new List<FallbackPlanStep>
        {
            new("step-0", "qobuz", "7", [], "isrc"),
            new("step-1", "tidal", "HI_RES", [], "isrc")
        };
        var history = new List<FallbackAttempt>
        {
            new("step-0", "skipped", "unavailable", "Confirmed missing recording")
        };

        Assert.False(FallbackFailureClassifier.IsPlanUnavailable(plan, history, snapshot));
    }

    [Fact]
    public void LaterCompletedOutcome_BeatsEarlierAbsenceEvidence()
    {
        var plan = new List<FallbackPlanStep> { new("step-0", "tidal", "HI_RES", [], "isrc") };
        var history = new List<FallbackAttempt>
        {
            new("step-0", "failed", "unavailable", "Confirmed missing recording"),
            new("step-0", "completed", "unavailable", "Delivered after a later retry")
        };

        Assert.False(FallbackFailureClassifier.IsPlanUnavailable(plan, history, null));
    }

    [Theory]
    [InlineData("provider_timeout")]
    [InlineData("identity_unresolved")]
    [InlineData("provider_quality_unknown")]
    [InlineData("unresolved")]
    [InlineData("provider_transient")]
    [InlineData("authentication_required")]
    [InlineData("catalog_lookup_failed")]
    [InlineData("")]
    public void InconclusiveOutcomeClassesAreNeverAbsenceEvidence(string errorClass)
        => Assert.False(FallbackFailureClassifier.IsConclusiveAbsenceClass(errorClass));

    [Theory]
    [InlineData("unavailable")]
    [InlineData("quality_below_requested")]
    [InlineData("catalog_quality_below_requested")]
    public void ConfirmedAbsenceClassesAreConclusive(string errorClass)
        => Assert.True(FallbackFailureClassifier.IsConclusiveAbsenceClass(errorClass));

    [Fact]
    public void EnabledSavedPlanSteps_SeparatesStereoFromAtmosSelection()
    {
        // An enabled engine carries its default quality ladder; a step is disabled when
        // the saved selection explicitly disables that quality (atmos off).
        var snapshot = SnapshotWith(("tidal", ["HI_RES"]));
        snapshot.DownloadEngineOrder!.Engines[0].Qualities.Add(
            new DownloadEngineQualityItem { Quality = "DOLBY_ATMOS", Enabled = false });
        var plan = new List<FallbackPlanStep>
        {
            new("step-0", "tidal", "HI_RES", [], "isrc"),
            new("step-1", "tidal", "DOLBY_ATMOS", [], "isrc")
        };

        Assert.Equal(["step-0"],
            FallbackFailureClassifier.EnabledSavedPlanSteps(plan, snapshot).Select(step => step.StepId));
    }

    [Fact]
    public void EnabledSavedPlanSteps_KeepsEveryStepWhenSelectionOverrideIsOff()
    {
        var snapshot = new QueueSourceSettingsSnapshot
        {
            DownloadEngineOrder = new DownloadEngineOrderSettings
            {
                Enabled = false,
                Engines = [new DownloadEngineOrderItem { Engine = "qobuz", Qualities = [] }]
            }
        };
        var plan = new List<FallbackPlanStep>
        {
            new("step-0", "qobuz", "7", [], "isrc"),
            new("step-1", "tidal", "HI_RES", [], "isrc")
        };

        Assert.Equal(2, FallbackFailureClassifier.EnabledSavedPlanSteps(plan, snapshot).Count);
    }

    [Fact]
    public void SnapshotSelectionOutsideTheSavedProviders_DisablesTheStep()
    {
        var snapshot = SnapshotWith(("deezer", ["9"]));
        var plan = new List<FallbackPlanStep>
        {
            new("step-0", "deezer", "9", [], "mapped_url"),
            new("step-1", "soundcloud", "GENERIC", [], "stream")
        };

        Assert.Equal(["step-0"],
            FallbackFailureClassifier.EnabledSavedPlanSteps(plan, snapshot).Select(step => step.StepId));
    }

    [Fact]
    public void EmptyOrMalformedSavedHistoryIsNeverConfirmation()
    {
        var plan = new List<FallbackPlanStep> { new("step-0", "tidal", "HI_RES", [], "isrc") };

        Assert.False(FallbackFailureClassifier.IsPlanUnavailable(plan, [], null));
        Assert.False(FallbackFailureClassifier.IsPlanUnavailable([null!], [null!], null));
        Assert.False(FallbackFailureClassifier.IsPlanUnavailable(
            [new("", "tidal", "HI_RES", [], "isrc")], [new("", "failed", "unavailable", "")], null));
        Assert.False(FallbackFailureClassifier.IsPlanUnavailable(
            [new("step-0", "tidal", "HI_RES", [], "isrc"), new("step-0", "qobuz", "7", [], "isrc")],
            [new("step-0", "failed", "unavailable", "")], null));
        Assert.False(FallbackFailureClassifier.IsPlanUnavailable(
            [], [new("step-0", "failed", "unavailable", "")], null));
    }
}
