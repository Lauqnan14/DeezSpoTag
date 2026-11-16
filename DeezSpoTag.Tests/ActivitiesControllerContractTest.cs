using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Web.Controllers;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ActivitiesControllerContractTest
{
    [Theory]
    [InlineData("provider_verification_required", "Session verification needed")]
    [InlineData("authentication_required", "Session verification needed")]
    [InlineData("quality_below_requested", "Quality unavailable")]
    [InlineData("unavailable", "Unavailable among the enabled services")]
    [InlineData("provider_transient", null)]
    [InlineData("catalog_lookup_failed", null)]
    [InlineData("identity_unresolved", null)]
    [InlineData("unresolved", null)]
    [InlineData("provider_timeout", null)]
    public void DownloadFailureMessage_UsesOnlyApprovedCopy(string reason, string? expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            FallbackPlan = new[] { new { StepId = "step-0", Engine = "qobuz", Quality = "7", RequiredInputs = Array.Empty<string>(), ResolutionStrategy = "isrc" } },
            FallbackHistory = new[] { new { StepId = "step-0", Status = "failed", ErrorClass = reason, Detail = "technical diagnostic" } }
        });
        var method = typeof(ActivitiesController).GetMethod("BuildDownloadFailureMessage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expected, method.Invoke(null, [json, "failed", "technical diagnostic", null, null, null, null]));
        Assert.Null(method.Invoke(null, [json, "complete", "technical diagnostic", null, null, null, null]));
        Assert.Null(method.Invoke(null, [json, "canceled", "technical diagnostic", null, null, null, null]));
    }

    [Fact]
    public void DownloadFailureMessage_UnknownOutcomesDoNotClaimUnavailability()
    {
        var method = typeof(ActivitiesController).GetMethod("BuildDownloadFailureMessage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Null(method.Invoke(null, ["{}", "unavailable", "Track not found", null, null, null, null]));
        Assert.Null(method.Invoke(null, ["not json", "failed", "HTTP 503", null, null, null, null]));
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));
        Assert.Contains("const displayedError = task.failureMessage;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("const displayedError = task.error;", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("provider_transient", null)]
    [InlineData("identity_unresolved", null)]
    [InlineData("quality_below_requested", "Quality unavailable")]
    [InlineData("unavailable", "Unavailable among the enabled services")]
    [InlineData("authentication_required", "Session verification needed")]
    public void DownloadFailureMessage_RequiresConclusiveResultsAcrossTheSelection(string secondReason, string? expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            FallbackPlan = new[]
            {
                new { StepId = "step-0", Engine = "qobuz", Quality = "7", RequiredInputs = Array.Empty<string>(), ResolutionStrategy = "isrc" },
                new { StepId = "step-1", Engine = "tidal", Quality = "LOSSLESS", RequiredInputs = Array.Empty<string>(), ResolutionStrategy = "isrc" }
            },
            FallbackHistory = new[]
            {
                new { StepId = "step-0", Status = "failed", ErrorClass = "unavailable", Detail = "confirmed" },
                new { StepId = "step-1", Status = "failed", ErrorClass = "authentication_required", Detail = "old result" },
                new { StepId = "step-1", Status = "failed", ErrorClass = secondReason, Detail = "latest result" }
            }
        });
        var method = typeof(ActivitiesController).GetMethod("BuildDownloadFailureMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, method.Invoke(null, [json, "failed", "technical diagnostic", null, null, null, null]));
    }

    [Fact]
    public void DownloadFailureMessage_IgnoresDisabledSourcesInSavedSettings()
    {
        var settings = new DeezSpoTagSettings();
        settings.DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault();
        settings.DownloadEngineOrder.Enabled = true;
        foreach (var engine in settings.DownloadEngineOrder.Engines) engine.Enabled = false;
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            SourceSettingsSnapshot = DeezSpoTag.Services.Download.Shared.QueueSourceSettingsSnapshot.Capture(settings),
            FallbackPlan = new[] { new { StepId = "step-0", Engine = "qobuz", Quality = "7", RequiredInputs = Array.Empty<string>(), ResolutionStrategy = "isrc" } },
            FallbackHistory = new[] { new { StepId = "step-0", Status = "failed", ErrorClass = "authentication_required", Detail = "disabled source" } }
        });
        var method = typeof(ActivitiesController).GetMethod("BuildDownloadFailureMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Null(method.Invoke(null, [json, "failed", "technical diagnostic", null, null, null, null]));
    }

    // ---- Task 3: "Session verification needed" must respect the recorded session state ----
    // The NAS stranded row still showed "Session verification needed" although all three
    // signed sessions report authenticated: the message preferred any auth-class outcome
    // without checking whether a session verification had already been recorded after the
    // failure. A verification recorded at/after the failure already had its chance to
    // recover the row, so claiming the session needs verification again is misleading.
    // Verification is tracked per provider: an Amazon verification must not silence a
    // Tidal failure, and a session that is currently usable counts as recovered even when
    // its last recorded verification predates the failure.

    private const string AuthOutcomePayload = """
        {
          "FallbackPlan": [
            {"StepId": "step-0", "Engine": "qobuz", "Quality": "27", "RequiredInputs": [], "ResolutionStrategy": "isrc"}
          ],
          "FallbackHistory": [
            {"StepId": "step-0", "Status": "skipped", "ErrorClass": "authentication_required", "Detail": "qobuz catalog lookup failed: 403 (Forbidden)"}
          ]
        }
        """;

    private static IReadOnlyDictionary<string, DateTimeOffset?> Verified(Dictionary<string, DateTimeOffset?> entries)
        => new Dictionary<string, DateTimeOffset?>(entries, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void DownloadFailureMessage_VerificationRecordedAfterTheFailure_DoesNotClaimSessionVerification()
    {
        var method = typeof(ActivitiesController).GetMethod("BuildDownloadFailureMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var verifiedAt = failedAt.AddMinutes(5);

        Assert.Null(method.Invoke(null,
        [
            AuthOutcomePayload, "failed", "qobuz catalog lookup failed: 403", failedAt,
            Verified(new() { ["qobuz"] = verifiedAt }), new HashSet<string>(StringComparer.OrdinalIgnoreCase), "qobuz"
        ]));
    }

    [Fact]
    public void DownloadFailureMessage_NoVerificationRecordYet_StillClaimsSessionVerification()
    {
        var method = typeof(ActivitiesController).GetMethod("BuildDownloadFailureMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var noSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal("Session verification needed",
            method.Invoke(null,
            [
                AuthOutcomePayload, "failed", "qobuz catalog lookup failed: 403", failedAt,
                Verified(new() { ["qobuz"] = (DateTimeOffset?)null }), noSlugs, "qobuz"
            ]));
        Assert.Equal("Session verification needed",
            method.Invoke(null, [AuthOutcomePayload, "failed", "qobuz catalog lookup failed: 403", null, null, null, null]));
    }

    [Fact]
    public void DownloadFailureMessage_VerificationPredatingTheFailure_StillClaimsSessionVerification()
    {
        var method = typeof(ActivitiesController).GetMethod("BuildDownloadFailureMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var verifiedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var failedAt = verifiedAt.AddMinutes(5);

        Assert.Equal("Session verification needed",
            method.Invoke(null,
            [
                AuthOutcomePayload, "failed", "qobuz catalog lookup failed: 403", failedAt,
                Verified(new() { ["qobuz"] = verifiedAt }), new HashSet<string>(StringComparer.OrdinalIgnoreCase), "qobuz"
            ]));
    }

    [Fact]
    public void DownloadFailureMessage_VerificationOnAnotherProvider_DoesNotSuppressThisProvider()
    {
        // Verifying Amazon must not silence a failure caused by Tidal: only the providers the
        // failure involved count, and every one of them must be recovered.
        var method = typeof(ActivitiesController).GetMethod("BuildDownloadFailureMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var verifiedAt = failedAt.AddMinutes(5);
        var json = """
            {
              "FallbackPlan": [
                {"StepId": "step-0", "Engine": "qobuz", "Quality": "7", "RequiredInputs": [], "ResolutionStrategy": "isrc"},
                {"StepId": "step-1", "Engine": "tidal", "Quality": "LOSSLESS", "RequiredInputs": [], "ResolutionStrategy": "isrc"}
              ],
              "FallbackHistory": [
                {"StepId": "step-0", "Status": "failed", "ErrorClass": "unavailable", "Detail": "confirmed"},
                {"StepId": "step-1", "Status": "failed", "ErrorClass": "authentication_required", "Detail": "tidal session rejected"}
              ]
            }
            """;

        Assert.Equal("Session verification needed",
            method.Invoke(null,
            [
                json, "failed", "tidal session rejected", failedAt,
                Verified(new() { ["qobuz"] = verifiedAt, ["amazon"] = verifiedAt }),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                "tidal"
            ]));

        // With Tidal verified as well, all involved providers are recovered.
        Assert.Null(method.Invoke(null,
        [
            json, "failed", "tidal session rejected", failedAt,
            Verified(new() { ["qobuz"] = verifiedAt, ["tidal"] = verifiedAt }),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            "tidal"
        ]));
    }

    [Fact]
    public void DownloadFailureMessage_OnlyTheVerificationFailedProviderMustBeRecovered()
    {
        // Attribution goes to the step that actually demanded verification: the plan's Tidal
        // step failed with a plain unavailability, so Tidal's session state is irrelevant to
        // the verification warning. A verification on Qobuz alone recovers the warning.
        var method = typeof(ActivitiesController).GetMethod("BuildDownloadFailureMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var verifiedAt = failedAt.AddMinutes(5);
        var json = """
            {
              "FallbackPlan": [
                {"StepId": "step-0", "Engine": "qobuz", "Quality": "6", "RequiredInputs": [], "ResolutionStrategy": "isrc"},
                {"StepId": "step-1", "Engine": "tidal", "Quality": "LOSSLESS", "RequiredInputs": [], "ResolutionStrategy": "isrc"}
              ],
              "FallbackHistory": [
                {"StepId": "step-0", "Status": "failed", "ErrorClass": "authentication_required", "Detail": "qobuz session rejected"},
                {"StepId": "step-1", "Status": "failed", "ErrorClass": "unavailable", "Detail": "tidal has no copy"}
              ]
            }
            """;

        Assert.Null(method.Invoke(null,
        [
            json, "failed", "qobuz session rejected", failedAt,
            Verified(new() { ["qobuz"] = verifiedAt }),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            "qobuz"
        ]));
    }

    [Fact]
    public void DownloadFailureMessage_CurrentlyUsableSession_SuppressesEvenWhenVerifiedBeforeTheFailure()
    {
        // An older but still-valid session keeps the provider reachable right now: it had its
        // chance to recover this failure and the provider is usable, so the stale-verification
        // warning would mislead the user.
        var method = typeof(ActivitiesController).GetMethod("BuildDownloadFailureMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var verifiedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var failedAt = verifiedAt.AddMinutes(5);

        Assert.Null(method.Invoke(null,
        [
            AuthOutcomePayload, "failed", "qobuz catalog lookup failed: 403", failedAt,
            Verified(new() { ["qobuz"] = verifiedAt }),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qobuz" },
            "qobuz"
        ]));
    }

    [Fact]
    public void BuildQueuePayload_PassesTheItemFailureTimeAndRecordedVerification()
    {
        var buildQueuePayload = typeof(ActivitiesController).GetMethod(
            "BuildQueuePayload", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(buildQueuePayload);

        var failedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var item = FailureItem("failed", "qobuz catalog lookup failed: 403 (Forbidden)", AuthOutcomePayload) with
        {
            UpdatedAt = failedAt
        };
        var verifiedAt = failedAt.AddMinutes(5);

        var payload = (Dictionary<string, object>)buildQueuePayload!.Invoke(null,
        [
            item,
            new DeezSpoTagSettings(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Verified(new() { ["qobuz"] = verifiedAt }),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        ])!;

        // The verification already happened after this failure; the card must not claim the
        // session needs verification again.
        Assert.True(payload.TryGetValue("failureMessage", out var failureMessage));
        Assert.Null(failureMessage);
    }

    [Fact]
    public void ActivitiesQualityLabels_ScopeSoulseekMappingsToSoulseek()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));
        Assert.Contains("function mapBitrate(bitrate, engine)", source, StringComparison.Ordinal);
        Assert.Contains("if (normalizeEngine(engine) === 'soulseek')", source, StringComparison.Ordinal);
        Assert.Contains("return soulseekQualityMap[upper] || qualityMap[upper] || bitrate.replace(/_/g, ' ');", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesQualityLabels_UseDownloaderAndRawQualityAcrossQueueUpdates()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));
        Assert.Contains("mapBitrate(rawQuality, resolvedEngine)", source, StringComparison.Ordinal);
        Assert.Contains("mapBitrate(nextRawQuality, nextEngine || task.engine)", source, StringComparison.Ordinal);
        Assert.Contains("String(task?.rawQuality || task?.quality || '')", source, StringComparison.Ordinal);
        Assert.Contains("mapBitrate(rawQuality, task?.engine)", source, StringComparison.Ordinal);
        var resolver = source[source.IndexOf("function isUnusableQueueQualityLabel", StringComparison.Ordinal)..source.IndexOf("function resolveQueueItemTitle", StringComparison.Ordinal)];
        Assert.Contains("quality unverified", resolver, StringComparison.OrdinalIgnoreCase);
        var delivered = resolver.IndexOf("deliveredQuality", StringComparison.Ordinal);
        var requested = resolver.IndexOf("'requestedQuality'", StringComparison.Ordinal);
        Assert.True(delivered >= 0 && requested > delivered, "The badge must prefer the delivered file quality, then the queued quality.");
    }

    [Fact]
    public void CancelDownloadRequest_RejectsEmptyUuid()
    {
        var request = new CancelDownloadRequest { Uuid = string.Empty };
        var validationResults = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            validationResults,
            validateAllProperties: true);

        Assert.False(isValid);
    }

    [Fact]
    public void BuildQueuePayload_IncludesPersistedErrorField()
    {
        var buildQueuePayload = typeof(ActivitiesController).GetMethod(
            "BuildQueuePayload",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildQueuePayload);

        var queueItem = new DownloadQueueItem(
            Id: 1,
            QueueUuid: "task-1",
            Engine: "deezer",
            ArtistName: "Artist",
            TrackTitle: "Track",
            Isrc: null,
            DeezerTrackId: null,
            DeezerAlbumId: null,
            DeezerArtistId: null,
            SpotifyTrackId: null,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: null,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: null,
            DestinationFolderId: null,
            QualityRank: null,
            QueueOrder: null,
            Status: "failed",
            PayloadJson: """
                {
                  "PrefetchArtworkStatus": "fetching",
                  "lyricsArtifacts": {
                    "revision": 12,
                    "status": "resolved",
                    "resolvedFormats": ["ttml"]
                  }
                }
                """,
            Progress: null,
            Downloaded: null,
            Failed: null,
            Error: "Network timeout",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var payload = (Dictionary<string, object>)buildQueuePayload!.Invoke(
            null,
            [queueItem, new DeezSpoTagSettings(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), null, null])!;

        Assert.True(payload.TryGetValue("error", out var error));
        Assert.Equal("Network timeout", error);
        Assert.Equal("fetching", payload["prefetchArtworkStatus"]);
        Assert.True(payload.ContainsKey("lyricsArtifacts"));
        Assert.False(payload.ContainsKey("prefetchLyricsStatus"));
    }

    // ---- Task 2: monitor eligibility requires saved absence evidence, not error substrings ----

    private const string ConclusiveEvidencePayload = """
        {
          "FallbackPlan": [
            {"StepId": "step-0", "Engine": "qobuz", "Quality": "27", "RequiredInputs": [], "ResolutionStrategy": "isrc"}
          ],
          "FallbackHistory": [
            {"StepId": "step-0", "Status": "skipped", "ErrorClass": "unavailable", "Detail": "Confirmed missing recording"}
          ]
        }
        """;

    private const string InconclusiveEvidencePayload = """
        {
          "FallbackPlan": [
            {"StepId": "step-0", "Engine": "qobuz", "Quality": "27", "RequiredInputs": [], "ResolutionStrategy": "isrc"}
          ],
          "FallbackHistory": [
            {"StepId": "step-0", "Status": "skipped", "ErrorClass": "provider_timeout", "Detail": "qobuz provider timed out"}
          ]
        }
        """;

    private static DownloadQueueItem FailureItem(string status, string error, string payloadJson) => new(
        Id: 1, QueueUuid: "monitor-evidence", Engine: "qobuz", ArtistName: "Artist",
        TrackTitle: "Track", Isrc: null, DeezerTrackId: null, DeezerAlbumId: null,
        DeezerArtistId: null, SpotifyTrackId: null, SpotifyAlbumId: null,
        SpotifyArtistId: null, AppleTrackId: null, AppleAlbumId: null,
        AppleArtistId: null, DurationMs: null, DestinationFolderId: null,
        QualityRank: null, QueueOrder: null, ContentType: "stereo",
        FinalizationStatus: null, EnrichmentStatus: null, Status: status,
        PayloadJson: payloadJson, Progress: 0, Downloaded: 0, Failed: 0,
        Error: error, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void FailedItemWithUnavailableSubstringError_IsNotMonitorableWithoutEvidence()
    {
        // A "failed" row whose error text merely mentions unavailability is exactly how a
        // timeout or auth failure got mis-filed as a catalogue miss; only rows actually
        // persisted as unavailable with saved evidence are monitorable.
        var isMonitorable = typeof(ActivitiesController).GetMethod(
            "IsMonitorableUnavailableActivityItem", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(isMonitorable);

        var item = FailureItem("failed", "Tidal says the track is unavailable for this account.",
            InconclusiveEvidencePayload);
        Assert.False((bool)isMonitorable!.Invoke(null, [item])!);
    }

    [Fact]
    public void UnavailableStatusWithoutConclusiveSavedEvidence_IsNotMonitorable()
    {
        var isMonitorable = typeof(ActivitiesController).GetMethod(
            "IsMonitorableUnavailableActivityItem", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(isMonitorable);

        var item = FailureItem("unavailable", "qobuz provider timed out", InconclusiveEvidencePayload);
        Assert.False((bool)isMonitorable!.Invoke(null, [item])!);

        var evidenceless = FailureItem("unavailable", "qobuz had no copy", "{}");
        Assert.False((bool)isMonitorable.Invoke(null, [evidenceless])!);
    }

    [Fact]
    public void UnavailableStatusWithConclusiveSavedEvidence_IsMonitorable()
    {
        var isMonitorable = typeof(ActivitiesController).GetMethod(
            "IsMonitorableUnavailableActivityItem", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(isMonitorable);

        var item = FailureItem("unavailable", "Qobuz (27) had no copy of this track.", ConclusiveEvidencePayload);
        Assert.True((bool)isMonitorable!.Invoke(null, [item])!);
    }

    [Fact]
    public void BuildQueuePayload_FailedSubstringRowStaysFailedAndUnmonitorable()
    {
        var buildQueuePayload = typeof(ActivitiesController).GetMethod(
            "BuildQueuePayload", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(buildQueuePayload);

        var item = FailureItem("failed", "Qobuz says the track is unavailable.", InconclusiveEvidencePayload);
        var payload = (Dictionary<string, object>)buildQueuePayload!.Invoke(null,
            [item, new DeezSpoTagSettings(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), null, null])!;

        Assert.Equal("failed", payload["status"]);
        Assert.False((bool)payload["canMonitorUnavailable"]!);
    }

    [Fact]
    public void BuildQueuePayload_UnavailableConclusiveRowOffersMonitoring()
    {
        var buildQueuePayload = typeof(ActivitiesController).GetMethod(
            "BuildQueuePayload", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(buildQueuePayload);

        var item = FailureItem("unavailable", "Qobuz (27) had no copy of this track.", ConclusiveEvidencePayload);
        var payload = (Dictionary<string, object>)buildQueuePayload!.Invoke(null,
            [item, new DeezSpoTagSettings(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), null, null])!;

        Assert.Equal("unavailable", payload["status"]);
        Assert.True((bool)payload["canMonitorUnavailable"]!);
    }

    [Fact]
    public void WatchlistRowsWithEvidenceStillUseWatchlistUnavailableTracking()
    {
        var canMonitor = typeof(ActivitiesController).GetMethod(
            "CanMonitorUnavailableItem", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(canMonitor);

        var item = FailureItem("unavailable", "Qobuz (27) had no copy of this track.", ConclusiveEvidencePayload);
        var payload = new Dictionary<string, object> { ["watchlistSource"] = "playlist" };

        Assert.False((bool)canMonitor!.Invoke(null,
            [item, payload, new HashSet<string>(StringComparer.OrdinalIgnoreCase)])!);
    }

    [Fact]
    public void MapStatusForUi_MapsSkippedToCompleted()
    {
        var mapStatusForUi = typeof(ActivitiesController).GetMethod(
            "MapStatusForUi",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(mapStatusForUi);

        var mapped = mapStatusForUi!.Invoke(null, ["skipped"]) as string;
        Assert.Equal("complete", mapped);
    }

    [Theory]
    [InlineData("completed", "complete")]
    [InlineData("complete", "complete")]
    [InlineData("finished", "complete")]
    [InlineData("download finished", "complete")]
    [InlineData("done", "complete")]
    [InlineData("success", "complete")]
    [InlineData("failed", "failed")]
    [InlineData("error", "failed")]
    [InlineData("canceled", "canceled")]
    [InlineData("cancelled", "canceled")]
    [InlineData("resolving", "queued")]
    [InlineData("inqueue", "queued")]
    [InlineData("downloading", "running")]
    public void MapStatusForUi_ReturnsCanonicalActivityStatus(string rawStatus, string expectedStatus)
    {
        var mapStatusForUi = typeof(ActivitiesController).GetMethod(
            "MapStatusForUi",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(mapStatusForUi);

        var mapped = mapStatusForUi!.Invoke(null, [rawStatus]) as string;
        Assert.Equal(expectedStatus, mapped);
    }

    [Fact]
    public void MapStatusForUi_KeepsRetryingActionable()
    {
        var mapStatusForUi = typeof(ActivitiesController).GetMethod(
            "MapStatusForUi",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(mapStatusForUi);

        var mapped = mapStatusForUi!.Invoke(null, ["retrying"]) as string;
        Assert.Equal("retrying", mapped);
    }

    [Fact]
    public void ActivitiesDownloadsTab_HandlesQueueRemovalEvents()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("connection.on('removedFromQueue'", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('removedAllDownloads'", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('removedFinishedDownloads'", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('addedToQueue'", source, StringComparison.Ordinal);
        Assert.Contains("refreshQueueViewState", source, StringComparison.Ordinal);
        // The removedFromQueue handler must drop the card through removeVisibleQueueTasks. The
        // previously asserted isActiveQueueTask helper was dead code and no longer implemented this.
        var removedFromQueueHandler = source.IndexOf(
            "connection.on('removedFromQueue'",
            StringComparison.Ordinal);
        Assert.True(removedFromQueueHandler >= 0, "The removedFromQueue subscription is missing.");
        var removalCall = source.IndexOf(
            "removeVisibleQueueTasks(taskIds)",
            removedFromQueueHandler,
            StringComparison.Ordinal);
        Assert.True(removalCall >= 0, "The removedFromQueue handler must remove the visible queue tasks.");
        Assert.Contains("function removeVisibleQueueTasks(taskIds)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDownloadsTab_RendersActionsFromBackendFlags()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("canPause: getQueueBoolFlag(item, 'canPause', 'CanPause')", source, StringComparison.Ordinal);
        Assert.Contains("const canCancel = task.canCancel === true", source, StringComparison.Ordinal);
        Assert.Contains("const canRetry = task.canRetry === true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("clearVisibleQueueTasks(isCompletedQueueTask);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("clearVisibleQueueTasks(isCanceledQueueTask);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDownloadsTab_ResetsProgressCacheWhenRetryResetsProgress()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("function resetQueueProgressCache(taskId)", source, StringComparison.Ordinal);
        Assert.Contains("resetQueueProgressCache(taskId);", source, StringComparison.Ordinal);
        Assert.Contains("resetQueueProgressCache(updatedId);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDownloadsTab_LyricsBadgesUseOnlyVersionedArtifactState()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("const lyricsBadges = resolveLyricsBadgesForQueueItem(item);", source, StringComparison.Ordinal);
        Assert.Contains("function getLyricsBadgesFromArtifacts(artifacts)", source, StringComparison.Ordinal);
        Assert.Contains("resolved.concat(downloaded)", source, StringComparison.Ordinal);
        Assert.Contains("incomingLyricsRevision >= (task.lyricsRevision || 0)", source, StringComparison.Ordinal);
        Assert.Contains("? incoming.lyricsBadges", source, StringComparison.Ordinal);
        Assert.DoesNotContain("getLyricsBadgesFromFiles", source, StringComparison.Ordinal);
        Assert.DoesNotContain("getLyricsBadgesFromPrefetch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("prefetchLyricsType", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesHistorySidecar_ShowsFetchingAsActivityNotAColumn()
    {
        var historySource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/wwwroot/js/autotag-status.js"));
        var viewSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("task-activity", historySource, StringComparison.Ordinal);
        Assert.Contains("activityState", historySource, StringComparison.Ordinal);
        Assert.Contains("fetchingsidecars", historySource, StringComparison.Ordinal);
        Assert.Contains("findActiveSidecarKey", historySource, StringComparison.Ordinal);
        Assert.Contains("sortSidecarGroupByTimestamp", historySource, StringComparison.Ordinal);
        Assert.Contains("newestSidecarEntry", historySource, StringComparison.Ordinal);
        Assert.Contains("sidecarEntryTimestamp", historySource, StringComparison.Ordinal);
        Assert.Contains("String(inner.message || \"\")", historySource, StringComparison.Ordinal);
        Assert.DoesNotContain("parseSidecarFetchingKinds", historySource, StringComparison.Ordinal);
        Assert.DoesNotContain("formatSidecarFetchingActivity", historySource, StringComparison.Ordinal);
        Assert.Contains(".autotag-lyrics-list .task-activity", viewSource, StringComparison.Ordinal);
        Assert.Contains("var(--accent-blue)", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("lyrics-row-activity", historySource, StringComparison.Ordinal);
        Assert.DoesNotContain("lyrics-row-activity", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<th scope=\"col\">Fetching</th>", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Fetching lyrics…", historySource, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagHistorySidecar_ShowsTitleArtistAndNeverANoLyricsBadge()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/wwwroot/js/autotag-status.js"));

        Assert.Contains("inner.sourceTitle", source, StringComparison.Ordinal);
        Assert.Contains("inner.sourceArtist", source, StringComparison.Ordinal);
        Assert.Contains("lyrics-row-title", source, StringComparison.Ordinal);
        Assert.Contains("lyrics-row-artist", source, StringComparison.Ordinal);
        Assert.Contains("lyricsBadges.map(lyricsBadgeMarkup)", source, StringComparison.Ordinal);
        Assert.Contains("const settled = sorted.filter((entry) => !isSidecarFetchingRecord(entry?.status?.status || {}));", source, StringComparison.Ordinal);
        Assert.Contains("const badgeSources = settled.length ? settled : [display];", source, StringComparison.Ordinal);
        Assert.Contains("message.startsWith(\"sidecars finished\")", source, StringComparison.Ordinal);
        Assert.Contains("!looksLikeAudio", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function lyricsMissingMarkup", source, StringComparison.Ordinal);
        Assert.DoesNotContain("inner.sourceTitle || toFileName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("badge-lyrics-unsynced\">No lyrics</span>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesLyricsBadges_MatchLyricsSettingsLabels()
    {
        var historySource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/wwwroot/js/autotag-status.js"));
        var viewSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains(">Word Synced Lyrics</span>", historySource, StringComparison.Ordinal);
        Assert.Contains(">Word Synced Lyrics</span>", viewSource, StringComparison.Ordinal);
        Assert.Contains(">Enhanced lyrics</span>", historySource, StringComparison.Ordinal);
        Assert.Contains(">Enhanced lyrics</span>", viewSource, StringComparison.Ordinal);
        Assert.Contains(">Synced lyrics</span>", historySource, StringComparison.Ordinal);
        Assert.Contains(">Synced lyrics</span>", viewSource, StringComparison.Ordinal);
        Assert.Contains(">Unsynced lyrics</span>", historySource, StringComparison.Ordinal);
        Assert.DoesNotContain(">TTML lyrics</span>", historySource, StringComparison.Ordinal);
        Assert.DoesNotContain(">TTML lyrics</span>", viewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDownloadsTab_DerivesAnimatedArtworkBadgeFromGeneratedSidecars()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("const artworkBadges = resolveArtworkBadgesForQueueItem(item);", source, StringComparison.Ordinal);
        Assert.Contains("function resolveArtworkBadgesForQueueItem(item)", source, StringComparison.Ordinal);
        Assert.Contains("artworkKind === 'album-animated'", source, StringComparison.Ordinal);
        Assert.Contains("function isAlbumAnimatedArtworkPath(path)", source, StringComparison.Ordinal);
        Assert.Contains("stem === 'cover'", source, StringComparison.Ordinal);
        Assert.Contains("stem === 'cover_tall'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("stem === 'folder'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return path.endsWith('.mp4')", source, StringComparison.Ordinal);
        Assert.Contains("Animated Artwork", source, StringComparison.Ordinal);
        Assert.Contains("badge-artwork-animated", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDownloadsTab_RendersSidecarPrefetchDownloadInfo()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("connection.on('downloadInfo'", source, StringComparison.Ordinal);
        Assert.Contains("state !== 'post' && state !== 'fetchingsidecars'", source, StringComparison.Ordinal);
        Assert.Contains("setTaskActivity(taskId, message);", source, StringComparison.Ordinal);
        var realtimeIndex = source.IndexOf("const realtime = queueActivityByTask[task.taskId];", StringComparison.Ordinal);
        var prefetchIndex = source.IndexOf("const prefetchText = getPrefetchActivityText(task.taskId);", StringComparison.Ordinal);
        Assert.True(realtimeIndex >= 0, "Realtime queue activity lookup is missing.");
        Assert.True(prefetchIndex >= 0, "Prefetch queue activity lookup is missing.");
        Assert.True(realtimeIndex < prefetchIndex, "Realtime sidecar messages must render before generic prefetch status.");
    }

    [Fact]
    public void AutoTag_ProtectsExistingAlbumFromLossyPlatformMatches()
    {
        var source = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("ApplyAlbumLossyOverwriteGuard(effectiveTagSettings, sourceTrack, file.Tag.Album);", source, StringComparison.Ordinal);
        Assert.Contains("sourceTrack.Album = currentAlbum;", source, StringComparison.Ordinal);
        Assert.Contains("effectiveTagSettings.Album = false;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadEngines_UseCanonicalRunningStartEvent()
    {
        var queueHelperSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Queue/QueueHelperUtils.cs"));
        var deezerSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Deezer/DeezerEngineProcessor.cs"));
        var qobuzSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"));
        var appleSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Apple/AppleEngineProcessor.cs"));
        var sharedSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Shared/EngineAudioPostDownloadHelper.cs"));

        Assert.Contains("SendRunningStartedAsync", queueHelperSource, StringComparison.Ordinal);
        Assert.Contains("status = \"running\"", queueHelperSource, StringComparison.Ordinal);
        Assert.Contains("progress = 0", queueHelperSource, StringComparison.Ordinal);
        Assert.Contains("QueueHelperUtils.SendRunningStartedAsync", deezerSource, StringComparison.Ordinal);
        Assert.Contains("QueueHelperUtils.SendRunningStartedAsync", qobuzSource, StringComparison.Ordinal);
        Assert.Contains("QueueHelperUtils.SendRunningStartedAsync", appleSource, StringComparison.Ordinal);
        Assert.Contains("QueueHelperUtils.SendRunningStartedAsync", sharedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDeleteFailed_EmitsRemovedFromQueue()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Controllers/ActivitiesController.cs"));

        Assert.Contains("_deezspotagListener.SendRemovedFromQueue(request.Uuid);", source, StringComparison.Ordinal);
        Assert.Contains("MarkActivitiesClearedByUuidAsync(request.Uuid", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteFailed_DeletesBeforeHidingAndReportsFailureWhenNothingRemoved()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Controllers/ActivitiesController.cs"));

        var deleteFailed = ExtractMethodSource(source, "DeleteFailed");
        var deleteIndex = deleteFailed.IndexOf("DeleteClearableByUuidAsync", StringComparison.Ordinal);
        var hideIndex = deleteFailed.IndexOf("MarkActivitiesClearedByUuidAsync", StringComparison.Ordinal);

        Assert.True(deleteIndex >= 0, "DeleteFailed must delete the queue row.");
        Assert.True(hideIndex >= 0, "DeleteFailed must still clear the activities marker.");
        Assert.True(
            deleteIndex < hideIndex,
            "DeleteFailed must delete the row before hiding it, otherwise a refused delete leaves an invisible orphan.");

        // A refused delete has to surface as a failure instead of a success the UI trusts.
        Assert.Contains("if (deleted == 0)", deleteFailed, StringComparison.Ordinal);
        Assert.Contains("Conflict(", deleteFailed, StringComparison.Ordinal);
        var conflictIndex = deleteFailed.IndexOf("Conflict(", StringComparison.Ordinal);
        Assert.True(
            conflictIndex < hideIndex,
            "DeleteFailed must return the conflict before hiding the row.");
    }

    [Fact]
    public void PauseTask_DelegatesToDownloadAppPauseSoRetryScheduleIsCleared()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Controllers/ActivitiesController.cs"));

        var pauseTask = ExtractMethodSource(source, "PauseTask");

        // A queued or retrying item must go through the app so the retry schedule is cleared and the
        // user-pause intent is recorded; a raw status write lets the processor overwrite the pause.
        Assert.Contains("PauseDownloadAsync(request.Uuid)", pauseTask, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateStatusAsync(request.Uuid, PausedStatus", pauseTask, StringComparison.Ordinal);
    }

    private static string ExtractMethodSource(string source, string methodName)
    {
        var signatureIndex = source.IndexOf("Task<IActionResult> " + methodName + "(", StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"{methodName} was not found.");
        var bodyStart = source.IndexOf('{', signatureIndex);
        Assert.True(bodyStart >= 0, $"{methodName} has no body.");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..(index + 1)];
                }
            }
        }

        Assert.Fail($"{methodName} has an unterminated body.");
        return string.Empty;
    }

    [Fact]
    public void DownloadClient_DoesNotOwnQueueRealtimeConnectionOrQueueUi()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/wwwroot/js/download-client.js"));
        var layoutSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Shared/_Layout.cshtml"));

        Assert.Contains("DeezSpoTag.DownloadClient", source, StringComparison.Ordinal);
        Assert.Contains("globalThis.DeezSpoTagDownload = DeezSpoTag.DownloadClient", source, StringComparison.Ordinal);
        Assert.Contains("addToQueue(url", source, StringComparison.Ordinal);
        Assert.Contains("addMultipleToQueue(urls", source, StringComparison.Ordinal);
        Assert.Contains("ensureDestinationSelects()", source, StringComparison.Ordinal);
        Assert.Contains("getDestinationFolderId(requireSelection", source, StringComparison.Ordinal);
        Assert.Contains("~/js/download-client.js", layoutSource, StringComparison.Ordinal);
        Assert.DoesNotContain("~/js/download.js", layoutSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/deezerQueueHub", source, StringComparison.Ordinal);
        Assert.DoesNotContain("connection.on(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelDownload(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("retryDownload(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("download-queue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("queue-list", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesHistoryAndLogsTabs_AreDirectChildrenOfTabContent()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));
        var htmlStart = source.IndexOf("<div class=\"container-fluid activities-container\">", StringComparison.Ordinal);
        var htmlEnd = source.IndexOf("@section Scripts", StringComparison.Ordinal);
        Assert.True(htmlStart >= 0 && htmlEnd > htmlStart, "Activities markup bounds are missing.");
        var html = source[htmlStart..htmlEnd];

        Assert.Equal("activitiesTabsContent", FindParentDivId(html, "history-content"));
        Assert.Equal("activitiesTabsContent", FindParentDivId(html, "logs-content"));
        Assert.Equal("activitiesTabsContent", FindParentDivId(html, "media-operations-content"));
    }

    private static string? FindParentDivId(string html, string childId)
    {
        var stack = new Stack<string?>();
        var index = 0;
        while (index < html.Length)
        {
            var nextOpen = html.IndexOf("<div", index, StringComparison.OrdinalIgnoreCase);
            var nextClose = html.IndexOf("</div>", index, StringComparison.OrdinalIgnoreCase);
            if (nextOpen < 0 && nextClose < 0)
            {
                break;
            }

            if (nextOpen >= 0 && (nextClose < 0 || nextOpen < nextClose))
            {
                var tagEnd = html.IndexOf('>', nextOpen);
                if (tagEnd < 0)
                {
                    break;
                }

                var tag = html[nextOpen..tagEnd];
                var id = ReadHtmlAttribute(tag, "id");
                stack.Push(id);
                if (string.Equals(id, childId, StringComparison.Ordinal))
                {
                    foreach (var parentId in stack)
                    {
                        if (!string.Equals(parentId, childId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(parentId))
                        {
                            return parentId;
                        }
                    }

                    return null;
                }

                index = tagEnd + 1;
                continue;
            }

            if (stack.Count > 0)
            {
                stack.Pop();
            }

            index = nextClose + 6;
        }

        return null;
    }

    private static string? ReadHtmlAttribute(string tag, string name)
    {
        var needle = $" {name}=\"";
        var start = tag.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += needle.Length;
        var end = tag.IndexOf('"', start);
        return end < 0 ? null : tag[start..end];
    }

    [Fact]
    public void ActivitiesDownloadsTab_OwnsQueueRealtimeConnection()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains(".withUrl('/deezerQueueHub')", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('updateQueue'", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('downloadProgress'", source, StringComparison.Ordinal);
        Assert.Contains("connection.on('startDownload'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DeezSpoTagDownload?.connection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DeezSpoTag?.Download?.connection", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDownloadsTab_AllowsCancelDuringRetrying()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("allowDuringRetry === true", source, StringComparison.Ordinal);
        Assert.Contains("statusForUi === 'retrying'", source, StringComparison.Ordinal);
        Assert.Contains("beginTaskAction(taskId, { allowDuringRetry: true })", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitiesDownloadsTab_UnavailableRowsKeepRetryDeleteAndMonitorActions()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Web/Views/Activities/Index.cshtml"));

        Assert.Contains("statusForUi === 'unavailable'", source, StringComparison.Ordinal);
        Assert.Contains("const retryButton = canRetry", source, StringComparison.Ordinal);
        Assert.Contains("const monitorUnavailableButton = canMonitorUnavailable", source, StringComparison.Ordinal);
        Assert.Contains("const deleteUnavailableButton = canDelete && statusForUi === 'unavailable'", source, StringComparison.Ordinal);
        Assert.Contains("${retryButton}${monitorUnavailableButton}${deleteFailedButton}${deleteUnavailableButton}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PauseQueuedAsync_CoversAllPendingActiveStatuses()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Queue/DownloadQueueRepository.cs"));

        Assert.Contains("lower(status) IN ('queued', 'inqueue', 'resolving', 'retrying')", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovedAllDownloadsEvent_AlwaysSendsPayload()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Shared/Models/IDeezSpoTagListener.cs"));

        Assert.Contains("Send(\"removedAllDownloads\", new { currentItem });", source, StringComparison.Ordinal);
    }
}
