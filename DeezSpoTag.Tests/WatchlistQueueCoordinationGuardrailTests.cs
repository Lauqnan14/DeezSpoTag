using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistQueueCoordinationGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void PlaylistWatchQueue_UsesRunBudgetAsTheOnlyQueueItemLimit()
    {
        var source = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");

        Assert.Contains("_queueAdmission.TryAdmitTrack()", source, StringComparison.Ordinal);
        Assert.Contains("_queueAdmission.Release(1)", source, StringComparison.Ordinal);
        Assert.Contains("result.Queued.Count", source, StringComparison.Ordinal);
        Assert.Contains("allowAutomaticSecondaryQuality: false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetActiveDownloadCountAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WatchQueueCapacity", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_UsesStrictQueueGateAndTracksGateDeferrals()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        var intentSource = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        Assert.DoesNotContain("EvaluateQueueGateAsync", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CanQueueWatchItemsAsync", watchSource, StringComparison.Ordinal);
        Assert.Contains("HasActiveDownloadPipelineAsync", ReadSource("DeezSpoTag.Services/Download/Queue/DownloadQueueRepository.cs"), StringComparison.Ordinal);
        Assert.Contains("EnqueueAsync", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("EnqueueManualAsync", watchSource, StringComparison.Ordinal);
        Assert.Contains("ShouldDeferWatchTrack", watchSource, StringComparison.Ordinal);
        Assert.Contains("download_gate_paused", watchSource, StringComparison.Ordinal);
        Assert.Contains("download_gate_paused", intentSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"failed\",\r\n                    cancellationToken);\r\n                failedCount++;\r\n                break;", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_DoesNotUseExistingQueueRowsAsRunBudget()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");

        Assert.DoesNotContain("GetUnfinishedWatchlistDownloadCountAsync", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetActiveWatchlistDownloadCountAsync", watchSource, StringComparison.Ordinal);
        Assert.Contains("_queueAdmission.TryAdmitTrack()", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_SetsDeferredWhenTrackIsDeferredByDownloadGate()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");

        Assert.Contains("var deferred = false;", watchSource, StringComparison.Ordinal);
        Assert.Contains("deferred = true;", watchSource, StringComparison.Ordinal);
        Assert.Contains("new QueueWatchResult(", watchSource, StringComparison.Ordinal);
        Assert.Contains("Deferred: deferred", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_DoesNotUseResolutionAttemptBudgetAsQueueGate()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");

        Assert.DoesNotContain("attemptedCount >= maxResolutionAttempts", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("watch queue reached resolution-attempt budget", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("resolutionAttempts", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("WatchMaxTracksPerPlaylistCheck", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchTriggers_DoNotWaitAndStartAnotherBudgetedRun()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");

        Assert.Contains("_runSignal.Request()", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_runLock", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchTriggers_AreDurableAndDoNotUseOverwriteProneInMemoryFocusFields()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var repositorySource = ReadSource("DeezSpoTag.Services/Library/LibraryRepository.cs");

        Assert.Contains("EnqueueWatchlistReconciliationRequestAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("ClaimDueWatchlistReconciliationRequestsAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("CompleteClaimedWatchlistReconciliationRequestsAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("RetryClaimedWatchlistReconciliationRequestsAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("updated_at=@updatedAt", repositorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("_requestedPlaylistKey", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_requestedArtistId", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedCycle_SeparatesSnapshotSweepFromCachedQueueAdmission()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var admissionSource = ReadSource("DeezSpoTag.Web/Services/WatchlistQueueAdmissionService.cs");
        var evaluateIndex = hostedSource.IndexOf("EvaluateQueueGateAsync", StringComparison.Ordinal);
        var snapshotIndex = hostedSource.IndexOf("var playlistRunResult = await ProcessPlaylistWatchItemsAsync(", StringComparison.Ordinal);
        var admissionIndex = hostedSource.IndexOf("await ProcessPlaylistQueueAdmissionsAsync(", StringComparison.Ordinal);
        var beginRunIndex = hostedSource.IndexOf("queueAdmission.BeginRun", StringComparison.Ordinal);

        Assert.True(evaluateIndex >= 0);
        Assert.True(snapshotIndex > evaluateIndex);
        Assert.True(admissionIndex > snapshotIndex);
        Assert.True(beginRunIndex >= 0);
        Assert.Contains("HasActiveDownloadPipelineAsync", admissionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("EvaluateBatchAsync", admissionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistReconciliationMode", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedCycle_ProcessesTargetSyncJobsThroughSingleCoordinator()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var postDownloadSource = ReadSource("DeezSpoTag.Web/Services/WatchlistPostDownloadSyncService.cs");
        var programSource = ReadSource("DeezSpoTag.Web/Program.cs");

        Assert.Contains("ProcessFinalizationWorkAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("ProcessTargetSyncWorkAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("WatchlistPostDownloadSyncService", hostedSource, StringComparison.Ordinal);
        Assert.Contains("ProcessPlaylistQueueAdmissionsAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("protected override async Task ExecuteAsync", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("while (!stoppingToken.IsCancellationRequested)", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AddDeferredHostedService<DeezSpoTag.Web.Services.WatchlistPostDownloadSyncService>", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("No playlist sync targets configured", hostedSource, StringComparison.Ordinal);
        Assert.True(
            hostedSource.IndexOf("RunTargetSyncPhaseAsync(coordinatorWork", StringComparison.Ordinal)
            < hostedSource.IndexOf("RunWatchCycleCoreAsync(", StringComparison.Ordinal),
            "Target synchronization must start independently before source reconciliation can delay it.");
        Assert.True(
            hostedSource.LastIndexOf("RunTargetSyncPhaseAsync(coordinatorWork", StringComparison.Ordinal)
            > hostedSource.IndexOf("RunWatchCycleCoreAsync(", StringComparison.Ordinal),
            "Target synchronization must run again after source reconciliation schedules fresh jobs.");
        Assert.Contains("TargetSyncJobTimeout", postDownloadSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedCycle_DoesNotInterleaveTargetSyncInsidePlaylistSnapshotSweep()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var postDownloadSource = ReadSource("DeezSpoTag.Web/Services/WatchlistPostDownloadSyncService.cs");
        var loopStart = hostedSource.IndexOf(
            "foreach (var activeItem in scheduledItems)",
            StringComparison.Ordinal);
        var nextMethod = hostedSource.IndexOf(
            "private async Task",
            loopStart + 1,
            StringComparison.Ordinal);
        var loopBody = hostedSource[loopStart..nextMethod];

        Assert.Contains("TryProcessItemAsync(", loopBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessTargetSyncWorkAsync(", loopBody, StringComparison.Ordinal);
        Assert.DoesNotContain("syncJobLimit: 1", loopBody, StringComparison.Ordinal);
        Assert.DoesNotContain("timeBudget", loopBody, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAll", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetOperationTimeout", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("operationCancellation.CancelAfter", postDownloadSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedCycle_BlocksSingleReconciliationQueuePassWhenDownloadsAreActive()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var admissionSource = ReadSource("DeezSpoTag.Web/Services/WatchlistQueueAdmissionService.cs");
        var batchGateIndex = hostedSource.IndexOf("EvaluateQueueGateAsync", StringComparison.Ordinal);
        var processPlaylistIndex = hostedSource.IndexOf("var playlistRunResult = await ProcessPlaylistWatchItemsAsync(", StringComparison.Ordinal);

        Assert.True(batchGateIndex >= 0);
        Assert.True(processPlaylistIndex > batchGateIndex);
        Assert.DoesNotContain("HasPendingPlaylistWatchBatchWorkAsync", admissionSource, StringComparison.Ordinal);
        Assert.Contains("RecoverInvalidPendingWatchClaimsAsync", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PostDownloadSync_UsesDedicatedTargetSyncWithoutASecondReconciliationPath()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        var postDownloadSource = ReadSource("DeezSpoTag.Web/Services/WatchlistPostDownloadSyncService.cs");

        Assert.Contains("AdmitCachedMissingTracksAsync", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistReconciliationMode", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("queuePlanningAllowed", watchSource, StringComparison.Ordinal);
        Assert.Contains("GetCachedPlaylistTrackCandidatesAsync", postDownloadSource, StringComparison.Ordinal);
        Assert.Contains("SyncAvailablePlaylistTracksAsync", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncAvailablePlaylistTracksToTargetAsync", postDownloadSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviousWatchlistRunBlock_UsesUnifiedDownloadGate()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        var admissionSource = ReadSource("DeezSpoTag.Web/Services/WatchlistQueueAdmissionService.cs");

        Assert.Contains("Waiting for active downloads, moves, or enrichment to finish.", admissionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("queue_deferred_previous_watchlist_active", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("WatchQueueStopReason.PreviousWatchlistRunActive", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalTrackUnavailableFailure_PersistsWatchlistAvailabilityRecheck()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        var downloadSource = ReadSource("DeezSpoTag.Services/Download/Shared/EngineAudioPostDownloadHelper.cs");
        var controllerSource = ReadSource("DeezSpoTag.Web/Controllers/Api/LibraryPlaylistWatchlistApiController.cs");
        var repositorySource = ReadSource("DeezSpoTag.Services/Library/LibraryRepository.cs");

        Assert.Contains("WatchlistUnavailableSettingsFingerprint = BuildUnavailableSettingsFingerprint(options)", watchSource, StringComparison.Ordinal);
        Assert.Contains("UnavailableRecheckDays", watchSource, StringComparison.Ordinal);
        Assert.Contains("IsAvailabilityRecheckWindowActive", watchSource, StringComparison.Ordinal);
        Assert.Contains("availability recheck scheduled", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("skipped_unavailable_cooldown", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable from enabled sources; retry scheduled", watchSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IsTrackUnavailableFailure(failureMessage)", downloadSource, StringComparison.Ordinal);
        Assert.Contains("terminalStatus = IsTrackUnavailableFailure(failureMessage)", downloadSource, StringComparison.Ordinal);
        Assert.Contains("context.QueueRepository.UpdateStatusAsync(queueUuid, terminalStatus", downloadSource, StringComparison.Ordinal);
        Assert.Contains("MarkPlaylistWatchTrackUnavailableAsync(", downloadSource, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset.UtcNow.AddDays(WatchlistUnavailableRecheckDays)", downloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("WatchlistUnavailableRetryDays", downloadSource, StringComparison.Ordinal);
        Assert.Contains("Recheck after", controllerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry after", controllerSource, StringComparison.Ordinal);
        Assert.Contains("unavailable_next_retry_utc", repositorySource, StringComparison.Ordinal);
        Assert.Contains("nextRecheckUtc", repositorySource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Qobuz track not found for ISRC or metadata.", true)]
    [InlineData("Enabled fallback sources could not resolve this track after tidal failed.", true)]
    [InlineData("Amazon download API failed with HTTP 404: Track not available", true)]
    [InlineData("Tidal operation timed out or was canceled by an external provider.", false)]
    [InlineData("No Tidal download provider is currently available.", false)]
    [InlineData("Qobuz official credentials are missing.", false)]
    public void TerminalTrackUnavailableFailure_OnlyClassifiesCatalogueMisses(string message, bool expected)
    {
        Assert.Equal(
            expected,
            DeezSpoTag.Services.Download.Shared.EngineAudioPostDownloadHelper.IsTrackUnavailableFailure(message));
    }

    [Fact]
    public void ManualUnavailableQueueResolution_UsesUnavailableTerminalStatus()
    {
        var resolverSource = ReadSource("DeezSpoTag.Web/Services/DownloadIntentQueuedPayloadResolver.cs");
        var appSource = ReadSource("DeezSpoTag.Services/Download/Shared/DeezSpoTagApp.cs");
        var activitiesSource = ReadSource("DeezSpoTag.Web/Controllers/ActivitiesController.cs");

        Assert.Contains("private const string UnavailableStatus = \"unavailable\";", resolverSource, StringComparison.Ordinal);
        Assert.Contains("Status = UnavailableStatus", resolverSource, StringComparison.Ordinal);
        Assert.Contains("EngineAudioPostDownloadHelper.IsTrackUnavailableFailure(resolution.Error)", appSource, StringComparison.Ordinal);
        Assert.Contains("terminalStatus", appSource, StringComparison.Ordinal);
        Assert.Contains("if (string.Equals(effectiveItem.Status, UnavailableStatus", appSource, StringComparison.Ordinal);
        Assert.Contains("IsMonitorableUnavailableActivityItem(item)", activitiesSource, StringComparison.Ordinal);
        Assert.Contains("payload[\"status\"] = IsMonitorableUnavailableActivityItem(item)", activitiesSource, StringComparison.Ordinal);
        Assert.Contains("payload[\"canRetry\"] = CanRetryActivityItem(item)", activitiesSource, StringComparison.Ordinal);
        Assert.Contains("ActivityStatus.Failed or ActivityStatus.Unavailable or ActivityStatus.Canceled", activitiesSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualUnavailablePlaylist_RendersAsLastNormalTracklistWithRetryColumn()
    {
        var watchlistSource = ReadSource("DeezSpoTag.Web/wwwroot/js/library-watchlists.js");
        var tracklistSource = ReadSource("DeezSpoTag.Web/Views/Tracklist/Index.cshtml");
        var apiSource = ReadSource("DeezSpoTag.Web/Controllers/ActivitiesController.cs");

        Assert.Contains("items.map((item, index) =>", watchlistSource, StringComparison.Ordinal);
        Assert.Contains("}).join('') + manualUnavailableCard", watchlistSource, StringComparison.Ordinal);
        Assert.Contains("/Tracklist?id=manual-unavailable&type=playlist&source=manual-unavailable", watchlistSource, StringComparison.Ordinal);
        Assert.DoesNotContain("openManualUnavailablePlaylistPanel", watchlistSource, StringComparison.Ordinal);
        Assert.DoesNotContain("renderManualUnavailableTrackRow", watchlistSource, StringComparison.Ordinal);
        Assert.Contains("manual-unavailable/tracklist", apiSource, StringComparison.Ordinal);
        Assert.Contains("nextRetryAtUtc", apiSource, StringComparison.Ordinal);
        Assert.Contains("function isManualUnavailableTracklist()", tracklistSource, StringComparison.Ordinal);
        Assert.Contains("await loadManualUnavailableTracklist();", tracklistSource, StringComparison.Ordinal);
        Assert.Contains("renderManualUnavailableRetryCell(track)", tracklistSource, StringComparison.Ordinal);
        Assert.Contains("isManualUnavailableTracklist() ? 'Retry in' : 'State'", tracklistSource, StringComparison.Ordinal);
        Assert.Contains("data-manual-unavailable-retry-at", tracklistSource, StringComparison.Ordinal);
        Assert.Contains("data-manual-unavailable-delete", tracklistSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualUnavailableRetry_PersistsPerTrackDeadlineAndUsesCentralManualQueue()
    {
        var modelsSource = ReadSource("DeezSpoTag.Services/Library/Models.cs");
        var schemaSource = ReadSource("DeezSpoTag.Services/Library/Schema/library.sql");
        var migrationSource = ReadSource("DeezSpoTag.Services/Library/LibraryDbService.cs");
        var repositorySource = ReadSource("DeezSpoTag.Services/Library/LibraryRepository.cs");
        var retryServiceSource = ReadSource("DeezSpoTag.Web/Services/ManualUnavailableRetryService.cs");
        var programSource = ReadSource("DeezSpoTag.Web/Program.cs");

        Assert.Contains("DateTimeOffset NextRetryAtUtc", modelsSource, StringComparison.Ordinal);
        Assert.Contains("next_retry_at_utc TEXT NOT NULL", schemaSource, StringComparison.Ordinal);
        Assert.DoesNotContain("idx_manual_unavailable_track_retry", schemaSource, StringComparison.Ordinal);
        Assert.Contains("EnsureColumnAsync(connection, ManualUnavailableTrackTable, \"next_retry_at_utc\"", migrationSource, StringComparison.Ordinal);
        Assert.Contains("EnsureIndexAsync(connection, \"idx_manual_unavailable_track_retry\"", migrationSource, StringComparison.Ordinal);
        Assert.Contains("GetDueManualUnavailableTracksAsync", repositorySource, StringComparison.Ordinal);
        Assert.Contains("ScheduleManualUnavailableTrackRetryAsync", repositorySource, StringComparison.Ordinal);
        Assert.Contains("intentService.EnqueueManualAsync(intent", retryServiceSource, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset.UtcNow.Add(RetryDelay)", retryServiceSource, StringComparison.Ordinal);
        Assert.Contains("DeleteManualUnavailableTrackAsync(track.Id", retryServiceSource, StringComparison.Ordinal);
        Assert.Contains("ManualUnavailableRetryService", programSource, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
