using System;
using System.IO;
using System.Text.Json;
using DeezSpoTag.Web.Controllers;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistQueueCoordinationGuardrailTest
{
    [Fact]
    public void ActiveWatchCycle_DrainsTargetJobsWithoutWaitingForSourceReconciliationToFinish()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var cycleStart = hostedSource.IndexOf("private async Task RunOneWatchCycleAsync(", StringComparison.Ordinal);
        Assert.True(cycleStart > 0);
        var cycleBody = hostedSource[cycleStart..(cycleStart + 7500)];
        var targetPump = cycleBody.IndexOf("RunActiveCycleTargetWorkAsync(", StringComparison.Ordinal);
        var sourceRun = cycleBody.IndexOf("await RunWatchCycleCoreAsync(", StringComparison.Ordinal);

        Assert.True(targetPump > 0 && sourceRun > targetPump);
        Assert.Contains("activeTargetWorkCancellation.Cancel()", cycleBody, StringComparison.Ordinal);
        Assert.Contains("await activeTargetWork", cycleBody, StringComparison.Ordinal);
    }
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
        Assert.Contains("skipDownloadGate: true", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("EnqueueManualAsync", watchSource, StringComparison.Ordinal);
        Assert.Contains("ShouldDeferWatchTrack", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("download_gate_paused", watchSource, StringComparison.Ordinal);
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
        Assert.Contains("_queueAdmission.HasAnyAttemptedIdentity(identityKeys)", watchSource, StringComparison.Ordinal);
        Assert.Contains("_queueAdmission.RememberAttemptedIdentities(identityKeys)", watchSource, StringComparison.Ordinal);
        var reserveIndex = watchSource.IndexOf("var admission = _queueAdmission.TryAdmitTrack()", StringComparison.Ordinal);
        var rememberIndex = watchSource.IndexOf("_queueAdmission.RememberAttemptedIdentities(identityKeys)", reserveIndex, StringComparison.Ordinal);
        var enqueueIndex = watchSource.IndexOf("await TryQueuePrimaryIntentAsync(", reserveIndex, StringComparison.Ordinal);
        Assert.True(reserveIndex >= 0 && rememberIndex > reserveIndex && enqueueIndex > rememberIndex);
        Assert.Contains("BuildWatchIdentityKeys", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveWatchCycle_FiltersItemsWithoutExplicitDestinationsBeforeTraversalAndAccounting()
    {
        var coordinator = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var loadIndex = coordinator.IndexOf("GetPlaylistWatchlistAsync", StringComparison.Ordinal);
        var filterIndex = coordinator.IndexOf("FilterEligibleWatchItemsAsync", loadIndex, StringComparison.Ordinal);
        var combinedIndex = coordinator.IndexOf("BuildCombinedWatchItems", loadIndex, StringComparison.Ordinal);
        var playlistTraversalIndex = coordinator.IndexOf("ProcessPlaylistWatchItemsAsync", loadIndex, StringComparison.Ordinal);
        var artistTraversalIndex = coordinator.IndexOf("ProcessArtistWatchItemsAsync", loadIndex, StringComparison.Ordinal);

        Assert.True(loadIndex >= 0);
        Assert.True(filterIndex > loadIndex && filterIndex < combinedIndex);
        Assert.True(filterIndex < playlistTraversalIndex && filterIndex < artistTraversalIndex);
        Assert.Contains("DestinationFolderId is > 0", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("AtmosDestinationFolderId is > 0", coordinator, StringComparison.Ordinal);
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
    public void PlaylistWatchTriggers_PersistWithoutInterruptingCompletionAnchoredCountdown()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");

        Assert.Contains("_runSignal.Request(WatchlistWakeReason.Reconciliation)", hostedSource, StringComparison.Ordinal);
        Assert.Contains("WaitForFullRunDeadlineAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("cycleCompletedUtc + GetWatchInterval()", hostedSource, StringComparison.Ordinal);
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
    public void HostedCycle_AdmitsWhenQuotaIsReachedAndDefersUnderQuotaUntilAllItemsAreVisited()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var admissionSource = ReadSource("DeezSpoTag.Web/Services/WatchlistQueueAdmissionService.cs");
        var engineSource = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        var repositorySource = ReadSource("DeezSpoTag.Services/Library/LibraryRepository.cs");
        var schemaSource = ReadSource("DeezSpoTag.Services/Library/Schema/library.sql");
        var evaluateIndex = hostedSource.IndexOf("EvaluateQueueGateAsync", StringComparison.Ordinal);
        var snapshotIndex = hostedSource.IndexOf("await RunWatchCycleCoreAsync(", StringComparison.Ordinal);
        var beginRunIndex = hostedSource.IndexOf("queueAdmission.BeginRun", StringComparison.Ordinal);
        var loopIndex = hostedSource.IndexOf("foreach (var activeItem in playlistItems)", StringComparison.Ordinal);
        var admissionIndex = hostedSource.IndexOf("AdmitDueMissingTracksFromLedgerAsync", StringComparison.Ordinal);
        var loopEnd = hostedSource.IndexOf(
            "private static async Task PersistPlaylistProgressAsync(",
            loopIndex + 1,
            StringComparison.Ordinal);
        var loopBody = hostedSource[loopIndex..loopEnd];
        var cycleCoreStart = hostedSource.IndexOf(
            "private async Task<PlaylistRunResult> RunWatchCycleCoreAsync(",
            StringComparison.Ordinal);
        var cycleCoreEnd = hostedSource.IndexOf(
            "private void ThrowIfWatchlistStopped(",
            cycleCoreStart + 1,
            StringComparison.Ordinal);
        var cycleCoreBody = hostedSource[cycleCoreStart..cycleCoreEnd];

        Assert.True(snapshotIndex >= 0);
        Assert.True(evaluateIndex >= 0 && evaluateIndex < snapshotIndex);
        Assert.True(beginRunIndex >= 0 && beginRunIndex < snapshotIndex);
        Assert.True(admissionIndex >= 0);
        Assert.True(loopIndex >= 0);
        Assert.Contains("AdmitDueMissingTracksWhenQuotaReadyAsync", loopBody, StringComparison.Ordinal);
        Assert.Contains("AdmitDueMissingTracksFromLedgerAsync", cycleCoreBody, StringComparison.Ordinal);
        Assert.DoesNotContain("AdmitDueMissingTracksFromLedgerAsync", loopBody, StringComparison.Ordinal);
        Assert.Contains("SelectAdmissionBatch(", engineSource, StringComparison.Ordinal);
        Assert.Contains("allowBelowQuota: false", engineSource, StringComparison.Ordinal);
        Assert.Contains("allowBelowQuota: true", engineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AdmitCached" + "MissingTracksAsync", loopBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDuePlaylistWatchMissingTracksInPriorityOrderAsync", loopBody, StringComparison.Ordinal);
        Assert.Contains("repository.GetPlaylistWatchlistAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("HasActiveDownloadPipelineAsync", admissionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("EvaluateBatchAsync", admissionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistReconciliationMode", hostedSource, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS playlist_watch_missing_track", schemaSource, StringComparison.Ordinal);
        Assert.Contains("UpsertPlaylistWatchMissingTracksAsync", engineSource, StringComparison.Ordinal);
        Assert.Contains("GetDuePlaylistWatchMissingTracksInPriorityOrderAsync", engineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AdmitCached" + "MissingTracksAsync", engineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AdmitMissing" + "TrackRowsAsync", engineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDuePlaylistWatchMissingTracksAsync", engineSource, StringComparison.Ordinal);
        Assert.Contains("MarkPlaylistWatchMissingTrackQueuedAsync", engineSource, StringComparison.Ordinal);
        Assert.Contains("ResolvePlaylistWatchMissingTrackAsync", repositorySource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var selection = await SelectMissingPlaylistTracksAsync(\r\n            source,\r\n            sourceId,\r\n            candidates,\r\n            preference?.DestinationFolderId,\r\n            queueOptions,\r\n            cancellationToken);\r\n        var queueResult = await QueueWatchIntentTracksAsync(",
            engineSource,
            StringComparison.Ordinal);
        var artistLoopStart = hostedSource.IndexOf("foreach (var item in artistItems)", StringComparison.Ordinal);
        var artistLoopEnd = hostedSource.IndexOf("private async Task<WatchItemExecutionOutcome> TryProcessItemAsync", artistLoopStart, StringComparison.Ordinal);
        Assert.Contains(
            "AdmitDueMissingTracksWhenQuotaReadyAsync",
            hostedSource[artistLoopStart..artistLoopEnd],
            StringComparison.Ordinal);
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
        Assert.Contains("AdmitDueMissingTracksFromLedgerAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("MergeVisitAdmission", hostedSource, StringComparison.Ordinal);
        Assert.Contains("TargetSyncBudget", postDownloadSource, StringComparison.Ordinal);
        Assert.Contains("shouldStop: () => DateTimeOffset.UtcNow >= fullRunDeadlineUtc", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("protected override async Task ExecuteAsync", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("while (!stoppingToken.IsCancellationRequested)", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AddDeferredHostedService<DeezSpoTag.Web.Services.WatchlistPostDownloadSyncService>", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("No playlist sync targets configured", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RunBudgetedTargetSyncAsync(", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolvePreSweepDrainBudget(", hostedSource, StringComparison.Ordinal);
        // The cycle-end drain is bounded by a fixed, named job count so cycle teardown always
        // reaches its end. Jobs past the cap are never claimed, so they stay durably queued.
        Assert.Contains("MaxDrainJobsPerCycle", hostedSource, StringComparison.Ordinal);
        Assert.Contains("MaxDrainJobsPerCycle = 25", hostedSource, StringComparison.Ordinal);
        Assert.Contains("DrainBounded", hostedSource, StringComparison.Ordinal);
        Assert.Contains("MaxJobs", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RunResidualTargetSyncAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RunInterleavedPlaylistSliceAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("WaitForFullRunDeadlineAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("UpdateWatchlistCycleStateAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectDuePlaylistItems", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDueWatchlistReconciliationRequestCountAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetNextWakeAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("TargetSyncAttemptDeadline", postDownloadSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedCycle_UsesInlineTargetApplyThenGlobalMissingLedgerAdmission()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var postDownloadSource = ReadSource("DeezSpoTag.Web/Services/WatchlistPostDownloadSyncService.cs");
        var admissionSource = ReadSource("DeezSpoTag.Web/Services/WatchlistQueueAdmissionService.cs");
        var loopStart = hostedSource.IndexOf(
            "foreach (var activeItem in playlistItems)",
            StringComparison.Ordinal);
        var loopNext = hostedSource.IndexOf(
            "private static async Task PersistPlaylistProgressAsync(",
            loopStart + 1,
            StringComparison.Ordinal);
        var loopBody = hostedSource[loopStart..loopNext];
        Assert.Contains("TryProcessItemAsync(", loopBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessTargetSyncWorkAsync(", loopBody, StringComparison.Ordinal);
        Assert.DoesNotContain("AdmitCached" + "MissingTracksAsync", loopBody, StringComparison.Ordinal);
        Assert.DoesNotContain("MergeVisitAdmission", loopBody, StringComparison.Ordinal);
        Assert.Contains("AdmitDueMissingTracksFromLedgerAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("timeBudget", loopBody + postDownloadSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IgnoreReconciliationLeaseOwner", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("HasWatchlistReconciliationRequestAsync", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("HasActiveDownloadPipelineAsync", admissionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAll(jobs", postDownloadSource, StringComparison.Ordinal);
        Assert.Contains("TargetSyncAttemptDeadline", postDownloadSource, StringComparison.Ordinal);
        Assert.Contains("CancelAfter(TargetSyncAttemptDeadline)", postDownloadSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedCycle_BlocksSingleReconciliationQueuePassWhenDownloadsAreActive()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var admissionSource = ReadSource("DeezSpoTag.Web/Services/WatchlistQueueAdmissionService.cs");
        var batchGateIndex = hostedSource.IndexOf("EvaluateQueueGateAsync", StringComparison.Ordinal);

        Assert.True(batchGateIndex >= 0);
        Assert.True(batchGateIndex < hostedSource.IndexOf("await RunWatchCycleCoreAsync(", StringComparison.Ordinal));
        Assert.DoesNotContain("HasPendingPlaylistWatchBatchWorkAsync", admissionSource, StringComparison.Ordinal);
        Assert.Contains("RecoverInvalidPendingWatchClaimsAsync", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PostDownloadSync_UsesDedicatedTargetSyncWithoutASecondReconciliationPath()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        var postDownloadSource = ReadSource("DeezSpoTag.Web/Services/WatchlistPostDownloadSyncService.cs");

        Assert.Contains("AdmitDueMissingTracksFromLedgerAsync", watchSource, StringComparison.Ordinal);
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

        Assert.DoesNotContain("Waiting for active downloads, moves, or enrichment to finish.", admissionSource, StringComparison.Ordinal);
        Assert.Contains("EvaluateDownloadGateAsync", admissionSource, StringComparison.Ordinal);
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
    public void QueuedPayloadResolution_PropagatesTheRealFailureMessage()
    {
        // The resolver used to substitute a constant "track unavailable" message, which
        // IsTrackUnavailableFailure classifies as a terminal catalogue miss. Every transient
        // resolution failure (timeout, rate limit, missing credentials) was therefore filed as
        // a missing track and never retried.
        var resolverSource = ReadSource("DeezSpoTag.Web/Services/DownloadIntentQueuedPayloadResolver.cs");
        var appSource = ReadSource("DeezSpoTag.Services/Download/Shared/DeezSpoTagApp.cs");

        Assert.DoesNotContain("private const string FailedMessage", resolverSource, StringComparison.Ordinal);
        Assert.Contains("var error = result.Error.Trim();", resolverSource, StringComparison.Ordinal);
        Assert.Contains("QueuePreResolutionPayload.ApplyFailed(payload, error, DateTimeOffset.UtcNow)", resolverSource, StringComparison.Ordinal);
        Assert.Contains("Error = error", resolverSource, StringComparison.Ordinal);
        Assert.Contains("                error);", resolverSource, StringComparison.Ordinal);

        // The caller decides whether to schedule a retry by reading Status, so the classified
        // status has to be the one it reads back.
        Assert.Contains("return resolution.Item with { Status = terminalStatus, Error = resolution.Error };", appSource, StringComparison.Ordinal);
    }

    [Theory]
    // A genuine catalogue miss stays terminal and monitorable.
    [InlineData("Qobuz track not found for ISRC or metadata.", "failed", "unavailable")]
    [InlineData("Enabled fallback sources could not resolve this track after tidal failed.", "failed", "unavailable")]
    // A transient provider failure is retried instead of being recorded as a missing track.
    [InlineData("Tidal operation timed out or was canceled by an external provider.", "failed", "failed")]
    [InlineData("Amazon download API returned HTTP 429: Too Many Requests", "failed", "failed")]
    [InlineData("Qobuz official credentials are missing.", "failed", "failed")]
    public void PreResolutionFailure_ClassifiesTransientErrorsAsRetryable(
        string error,
        string resolverStampedStatus,
        string expectedPersistedStatus)
    {
        // Mirrors the resolver stamp and the app's ternary over the same message.
        var classified = DeezSpoTag.Services.Download.Shared.EngineAudioPostDownloadHelper
            .IsTrackUnavailableFailure(error)
            ? "unavailable"
            : "failed";

        Assert.Equal(resolverStampedStatus, "failed");
        Assert.Equal(expectedPersistedStatus, classified);
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

        // The track's own metadata belongs in the record, not only inside the opaque payload blob.
        // A fresh database must carry every one of these columns; the runtime migration in
        // LibraryDbWatchlistMigrationTest verifies an existing database gains them too.
        foreach (var column in new[]
                 {
                     "cover_url TEXT",
                     "duration_ms INTEGER",
                     "track_number INTEGER",
                     "track_total INTEGER",
                     "disc_number INTEGER",
                     "disc_total INTEGER",
                     "release_date TEXT",
                     "explicit INTEGER"
                 })
        {
            Assert.Contains(column, schemaSource, StringComparison.Ordinal);
        }

        // The original queue row survives at a failed/unavailable status, so a fresh enqueue is
        // rejected as a queue duplicate and the track was never re-attempted. The retry must reuse
        // that row instead, and must not revive one the user cancelled.
        Assert.Contains("app.RetryDownloadAsync(track.QueueUuid", retryServiceSource, StringComparison.Ordinal);
        Assert.Contains("app.GetQueueItemAsync(track.QueueUuid", retryServiceSource, StringComparison.Ordinal);
        Assert.Contains("IsCanceledStatus(existing.Status)", retryServiceSource, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset.UtcNow.Add(RetryDelay)", retryServiceSource, StringComparison.Ordinal);
        Assert.Contains("DeleteManualUnavailableTrackAsync(track.Id", retryServiceSource, StringComparison.Ordinal);
        Assert.Contains("ManualUnavailableRetryService", programSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// A track row is a track, not a playlist tile. The "Unavailable Tracks" artwork belongs to the
    /// playlist card and page header; painting it onto every row destroyed the one piece of metadata
    /// the record actually had. The mapper is exercised directly so this cannot regress silently.
    /// </summary>
    [Fact]
    public void ManualUnavailableTracklist_UsesTrackMetadataInsteadOfPlaylistPresentationMetadata()
    {
        const string unavailablePlaylistImage = "/images/unavailable/unavailable.jpg";

        var mapped = JsonSerializer.SerializeToElement(ActivitiesController.MapManualUnavailableTrack(
            CreateManualUnavailableTrack(
                coverUrl: "https://example.test/album-cover.jpg",
                durationMs: 205000,
                trackNumber: 4,
                trackTotal: 12,
                discNumber: 2,
                discTotal: 3,
                releaseDate: "2025-07-18",
                @explicit: true),
            index: 7));

        var album = mapped.GetProperty("album");
        Assert.Equal("https://example.test/album-cover.jpg", album.GetProperty("cover_medium").GetString());
        Assert.Equal("https://example.test/album-cover.jpg", album.GetProperty("cover_big").GetString());
        Assert.Equal(205, mapped.GetProperty("duration").GetInt32());
        Assert.Equal(205000, mapped.GetProperty("durationMs").GetInt32());
        Assert.Equal(4, mapped.GetProperty("track_position").GetInt32());
        Assert.Equal(12, mapped.GetProperty("track_total").GetInt32());
        Assert.Equal(2, mapped.GetProperty("disk_number").GetInt32());
        Assert.Equal(3, mapped.GetProperty("disk_total").GetInt32());
        Assert.Equal("2025-07-18", mapped.GetProperty("release_date").GetString());
        Assert.True(mapped.GetProperty("explicit_lyrics").GetBoolean());

        // The playlist image may only ever appear on the playlist card, never on a track row.
        Assert.NotEqual(unavailablePlaylistImage, album.GetProperty("cover_medium").GetString());
        Assert.NotEqual(unavailablePlaylistImage, album.GetProperty("cover_big").GetString());

        // Unknown artwork stays unknown. Substituting the playlist image would fabricate a cover the
        // record never had.
        var withoutCover = JsonSerializer.SerializeToElement(ActivitiesController.MapManualUnavailableTrack(
            CreateManualUnavailableTrack(
                coverUrl: null,
                durationMs: null,
                trackNumber: null,
                trackTotal: null,
                discNumber: null,
                discTotal: null,
                releaseDate: null,
                @explicit: null),
            index: 2));

        var bareAlbum = withoutCover.GetProperty("album");
        Assert.Equal(string.Empty, bareAlbum.GetProperty("cover_medium").GetString());
        Assert.Equal(string.Empty, bareAlbum.GetProperty("cover_big").GetString());
        Assert.Equal(0, withoutCover.GetProperty("durationMs").GetInt32());
        Assert.Equal(0, withoutCover.GetProperty("duration").GetInt32());
        Assert.Equal(3, withoutCover.GetProperty("track_position").GetInt32());
        Assert.Equal(0, withoutCover.GetProperty("track_total").GetInt32());
        Assert.Equal(0, withoutCover.GetProperty("disk_number").GetInt32());
        Assert.Equal(0, withoutCover.GetProperty("disk_total").GetInt32());
        Assert.Equal(string.Empty, withoutCover.GetProperty("release_date").GetString());

        // An unknown explicit status must reach the client as null, not as false. Collapsing it would
        // tell the reader the track is confirmed clean when nobody ever said so.
        Assert.Equal(
            JsonValueKind.Null,
            withoutCover.GetProperty("explicit_lyrics").ValueKind);

        // A record that did state "false" still has to say false, not null.
        var knownClean = JsonSerializer.SerializeToElement(ActivitiesController.MapManualUnavailableTrack(
            CreateManualUnavailableTrack(
                coverUrl: null,
                durationMs: null,
                trackNumber: null,
                trackTotal: null,
                discNumber: null,
                discTotal: null,
                releaseDate: null,
                @explicit: false),
            index: 0));
        Assert.False(knownClean.GetProperty("explicit_lyrics").GetBoolean());
    }

    [Fact]
    public void BuildManualUnavailableTrackInput_ExtractsCompleteQueueMetadata()
    {
        // PascalCase payload, the shape the queue writes.
        var input = BuildInput(
            @"{""Title"":""Real Title"",""Artist"":""Real Artist"",""Album"":""Real Album""," +
            @"""Cover"":""https://example.test/pascal.jpg"",""DurationSeconds"":205," +
            @"""TrackNumber"":4,""TrackTotal"":12,""DiscNumber"":2,""DiscTotal"":3," +
            @"""ReleaseDate"":""2025-07-18"",""Explicit"":true,""Isrc"":""USSM12345678""}",
            queueDurationMs: null);

        Assert.Equal("Real Title", input.Title);
        Assert.Equal("https://example.test/pascal.jpg", input.CoverUrl);
        Assert.Equal(205000, input.DurationMs);
        Assert.Equal(4, input.TrackNumber);
        Assert.Equal(12, input.TrackTotal);
        Assert.Equal(2, input.DiscNumber);
        Assert.Equal(3, input.DiscTotal);
        Assert.Equal("2025-07-18", input.ReleaseDate);
        Assert.True(input.Explicit);
        Assert.Equal("USSM12345678", input.Isrc);

        // camelCase and alternate provider keys resolve the same way.
        var camel = BuildInput(
            @"{""coverUrl"":""https://example.test/camel.jpg"",""durationMs"":42000," +
            @"""spotifyTrackNumber"":7,""spotifyTotalTracks"":9,""spotifyDiscNumber"":1," +
            @"""discTotal"":2,""release_date"":""2024-01-02"",""explicit_lyrics"":""true""}",
            queueDurationMs: null);

        Assert.Equal("https://example.test/camel.jpg", camel.CoverUrl);
        Assert.Equal(42000, camel.DurationMs);
        Assert.Equal(7, camel.TrackNumber);
        Assert.Equal(9, camel.TrackTotal);
        Assert.Equal(1, camel.DiscNumber);
        Assert.Equal(2, camel.DiscTotal);
        Assert.Equal("2024-01-02", camel.ReleaseDate);
        Assert.True(camel.Explicit);

        Assert.Equal("https://example.test/album-cover.jpg", BuildInput(
            @"{""AlbumCover"":""https://example.test/album-cover.jpg""}",
            queueDurationMs: null).CoverUrl);

        // The queue writes a placeholder into "cover" when it found no artwork
        // (QueuePayloadBuilder.DefaultCoverPath). It is not a cover and must not be stored as one.
        Assert.Null(BuildInput(@"{""cover"":""/images/unavailable/unavailable.jpg""}", null).CoverUrl);
        Assert.Null(BuildInput(@"{""cover"":""/images/default-cover.png""}", null).CoverUrl);
        Assert.Null(BuildInput(@"{""Cover"":"" /images/unavailable/unavailable.jpg ""}", null).CoverUrl);

        // A real cover further down the key list still wins over the placeholder.
        Assert.Equal(
            "https://example.test/real.jpg",
            BuildInput(
                @"{""cover"":""/images/unavailable/unavailable.jpg"",""albumCover"":""https://example.test/real.jpg""}",
                null).CoverUrl);
    }

    [Fact]
    public void BuildManualUnavailableTrackInput_UsesDurationSecondsWhenMillisecondsAreAbsent()
    {
        Assert.Equal(205000, BuildInput(@"{""DurationSeconds"":205}", null).DurationMs);
        Assert.Equal(205000, BuildInput(@"{""DurationMs"":null,""DurationSeconds"":205}", null).DurationMs);
        Assert.Equal(205000, BuildInput(@"{""DurationMs"":0,""DurationSeconds"":205}", null).DurationMs);

        // A seconds value that would overflow Int32 on conversion is dropped, not wrapped negative.
        Assert.Null(BuildInput(@"{""DurationSeconds"":9999999999}", null).DurationMs);
    }

    [Fact]
    public void BuildManualUnavailableTrackInput_PrefersQueueDurationMilliseconds()
    {
        // The queue row is the resolved answer; a stale payload must not contradict it.
        Assert.Equal(205000, BuildInput(@"{""DurationSeconds"":999}", 205000).DurationMs);
        Assert.Equal(205000, BuildInput(@"{""DurationMs"":1}", 205000).DurationMs);

        // A missing or non-positive queue duration still falls through to the payload.
        Assert.Equal(205000, BuildInput(@"{""DurationMs"":205000}", null).DurationMs);
        Assert.Equal(205000, BuildInput(@"{""DurationMs"":205000}", 0).DurationMs);
        Assert.Equal(205000, BuildInput(@"{""DurationMs"":205000}", -1).DurationMs);
    }

    [Fact]
    public void BuildManualUnavailableTrackInput_LeavesAbsentMetadataUnknown()
    {
        var input = BuildInput(@"{""Title"":""Only A Title""}", null);

        Assert.Null(input.CoverUrl);
        Assert.Null(input.DurationMs);
        Assert.Null(input.TrackNumber);
        Assert.Null(input.TrackTotal);
        Assert.Null(input.DiscNumber);
        Assert.Null(input.DiscTotal);
        Assert.Null(input.ReleaseDate);
        Assert.Null(input.Explicit);

        // A value that cannot be read as metadata stays unknown instead of being guessed at.
        var malformed = BuildInput(
            @"{""TrackNumber"":""not-a-number"",""TrackTotal"":0,""DiscNumber"":-3,""DiscTotal"":""12"",""DurationMs"":""abc""}",
            null);
        Assert.Null(malformed.TrackNumber);
        Assert.Null(malformed.TrackTotal);
        Assert.Null(malformed.DiscNumber);
        Assert.Equal(12, malformed.DiscTotal);
        Assert.Null(malformed.DurationMs);

        // Explicit is stated as a JSON boolean, an integer and a string; all three resolve.
        Assert.True(BuildInput(@"{""Explicit"":true}", null).Explicit);
        Assert.False(BuildInput(@"{""Explicit"":false}", null).Explicit);
        Assert.True(BuildInput(@"{""Explicit"":1}", null).Explicit);
        Assert.False(BuildInput(@"{""Explicit"":0}", null).Explicit);
        Assert.True(BuildInput(@"{""Explicit"":""TRUE""}", null).Explicit);
        Assert.False(BuildInput(@"{""Explicit"":""False""}", null).Explicit);
        Assert.True(BuildInput(@"{""Explicit"":""1""}", null).Explicit);
        Assert.False(BuildInput(@"{""Explicit"":""0""}", null).Explicit);

        // Unreadable as a boolean means unknown, never false.
        Assert.Null(BuildInput(@"{""Explicit"":""maybe""}", null).Explicit);
        Assert.Null(BuildInput(@"{""Explicit"":5}", null).Explicit);

        // A whole number written as a JSON string is still a number, exactly as the legacy repair
        // path reads it. The two paths used to disagree here.
        Assert.Equal(12, BuildInput(@"{""TrackNumber"":""12""}", null).TrackNumber);
        Assert.Equal(205000, BuildInput(@"{""DurationSeconds"":""205""}", null).DurationMs);
        Assert.Equal(9, BuildInput(@"{""DiscTotal"":""9""}", null).DiscTotal);

        // A fractional value is not a track number. Rounding it would state a number the source never
        // gave, so it stays unknown instead.
        Assert.Null(BuildInput(@"{""TrackNumber"":4.7}", null).TrackNumber);
        Assert.Null(BuildInput(@"{""DurationSeconds"":205.5}", null).DurationMs);
        Assert.Null(BuildInput(@"{""DiscTotal"":2.5}", null).DiscTotal);
    }

    [Fact]
    public void ManualUnavailableRetry_ReconstructsCompleteIntentFromNormalizedMetadata()
    {
        var intent = DeezSpoTag.Web.Services.ManualUnavailableRetryService.BuildIntent(
            CreateManualUnavailableTrack(
                coverUrl: "https://example.test/album-cover.jpg",
                durationMs: 205000,
                trackNumber: 4,
                trackTotal: 12,
                discNumber: 2,
                discTotal: 3,
                releaseDate: "2025-07-18",
                @explicit: true));

        Assert.Equal("https://example.test/album-cover.jpg", intent.Cover);
        Assert.Equal(205000, intent.DurationMs);
        Assert.Equal(4, intent.TrackNumber);
        Assert.Equal(12, intent.TrackTotal);
        Assert.Equal(2, intent.DiscNumber);
        Assert.Equal(3, intent.DiscTotal);
        Assert.Equal("2025-07-18", intent.ReleaseDate);
        Assert.True(intent.Explicit);

        // Fields already represented on the record must keep their existing behaviour.
        Assert.Equal("Track Title", intent.Title);
        Assert.Equal("Track Artist", intent.Artist);
        Assert.Equal("Track Album", intent.Album);
        Assert.Equal("Album Artist", intent.AlbumArtist);
        Assert.Equal("USSM12345678", intent.Isrc);
        Assert.Equal("deezer", intent.PreferredEngine);
        Assert.Equal("FLAC", intent.Quality);
        Assert.Equal(7, intent.DestinationFolderId);
    }

    [Fact]
    public void ManualUnavailableRetry_RecoversLegacyDurationSeconds()
    {
        // A record written before the normalised columns existed: everything is null and only the
        // payload knows anything. The queue duration used to be rebuilt as zero from this.
        var legacy = CreateManualUnavailableTrack(
            coverUrl: null,
            durationMs: null,
            trackNumber: null,
            trackTotal: null,
            discNumber: null,
            discTotal: null,
            releaseDate: null,
            @explicit: null);
        legacy = legacy with { PayloadJson = @"{""Cover"":""https://example.test/legacy.jpg"",""DurationSeconds"":205}" };

        var intent = DeezSpoTag.Web.Services.ManualUnavailableRetryService.BuildIntent(legacy);

        Assert.Equal(205000, intent.DurationMs);
        Assert.Equal("https://example.test/legacy.jpg", intent.Cover);

        // Nothing was stated, so nothing is invented.
        Assert.Null(intent.Explicit);
        Assert.Equal(0, intent.TrackNumber);
        Assert.Equal(0, intent.TrackTotal);
        Assert.Equal(0, intent.DiscNumber);
        Assert.Equal(0, intent.DiscTotal);
        Assert.Equal(string.Empty, intent.ReleaseDate);
    }

    [Fact]
    public void ManualUnavailableRetry_NeverUsesUnavailablePlaylistArtwork()
    {
        // Cover is read by the post-download artwork pipeline and ends up in the file's tags. A record
        // with no artwork of its own must produce an empty cover, not the playlist placeholder.
        var noArtwork = CreateManualUnavailableTrack(
            coverUrl: null,
            durationMs: null,
            trackNumber: null,
            trackTotal: null,
            discNumber: null,
            discTotal: null,
            releaseDate: null,
            @explicit: null);

        var intent = DeezSpoTag.Web.Services.ManualUnavailableRetryService.BuildIntent(noArtwork);
        Assert.Equal(string.Empty, intent.Cover);
        Assert.NotEqual("/images/unavailable/unavailable.jpg", intent.Cover);

        // The queue writes this placeholder into "cover" whenever it found no artwork, so a legacy
        // payload can carry it. Cover feeds the tagging pipeline, so it must not be promoted.
        var placeholderPayload = noArtwork with
        {
            PayloadJson = @"{""cover"":""/images/unavailable/unavailable.jpg"",""albumCover"":""https://example.test/real.jpg""}"
        };
        var fromPlaceholder = DeezSpoTag.Web.Services.ManualUnavailableRetryService.BuildIntent(placeholderPayload);
        Assert.Equal("https://example.test/real.jpg", fromPlaceholder.Cover);

        Assert.Equal(
            string.Empty,
            DeezSpoTag.Web.Services.ManualUnavailableRetryService.BuildIntent(
                noArtwork with { PayloadJson = @"{""cover"":""/images/unavailable/unavailable.jpg""}" }).Cover);

        // The normalised value is what reaches the intent, so a stale payload cover cannot displace it.
        var withCover = CreateManualUnavailableTrack(
            coverUrl: "https://example.test/normalized.jpg",
            durationMs: null,
            trackNumber: null,
            trackTotal: null,
            discNumber: null,
            discTotal: null,
            releaseDate: null,
            @explicit: null) with { PayloadJson = @"{""Cover"":""https://example.test/payload.jpg""}" };

        Assert.Equal(
            "https://example.test/normalized.jpg",
            DeezSpoTag.Web.Services.ManualUnavailableRetryService.BuildIntent(withCover).Cover);
    }

    [Fact]
    public void ManualUnavailableRetry_FallsBackToLegacyPayloadForEveryMetadataField()
    {
        // A record whose normalised columns are empty but whose payload states everything, in the
        // camelCase shape and with explicit given as a string.
        var legacy = CreateManualUnavailableTrack(
            coverUrl: null,
            durationMs: null,
            trackNumber: null,
            trackTotal: null,
            discNumber: null,
            discTotal: null,
            releaseDate: null,
            @explicit: null) with
        {
            PayloadJson = @"{""coverUrl"":""https://example.test/legacy.jpg"",""durationMs"":42000," +
                          @"""spotifyTrackNumber"":7,""spotifyTotalTracks"":9,""spotifyDiscNumber"":1," +
                          @"""discTotal"":2,""release_date"":""2024-01-02"",""explicit_lyrics"":""TRUE""}"
        };

        var intent = DeezSpoTag.Web.Services.ManualUnavailableRetryService.BuildIntent(legacy);

        Assert.Equal("https://example.test/legacy.jpg", intent.Cover);
        Assert.Equal(42000, intent.DurationMs);
        Assert.Equal(7, intent.TrackNumber);
        Assert.Equal(9, intent.TrackTotal);
        Assert.Equal(1, intent.DiscNumber);
        Assert.Equal(2, intent.DiscTotal);
        Assert.Equal("2024-01-02", intent.ReleaseDate);
        Assert.True(intent.Explicit);

        // The retry fallback reads the same payload through its own JsonObject reader, so it has to
        // agree with the live path and the legacy repair on what counts as a number.
        var stringNumber = BuildIntentFromPayload(
            @"{""TrackNumber"":""12"",""DurationSeconds"":""205"",""DiscTotal"":""9""}");
        Assert.Equal(12, stringNumber.TrackNumber);
        Assert.Equal(205000, stringNumber.DurationMs);
        Assert.Equal(9, stringNumber.DiscTotal);

        var fractional = BuildIntentFromPayload(
            @"{""TrackNumber"":4.7,""DurationSeconds"":205.5,""DiscTotal"":2.5}");
        Assert.Equal(0, fractional.TrackNumber);
        Assert.Equal(0, fractional.DurationMs);
        Assert.Equal(0, fractional.DiscTotal);
    }

    private static DeezSpoTag.Services.Download.Shared.Models.DownloadIntent BuildIntentFromPayload(string payloadJson)
        => DeezSpoTag.Web.Services.ManualUnavailableRetryService.BuildIntent(
            CreateManualUnavailableTrack(
                coverUrl: null,
                durationMs: null,
                trackNumber: null,
                trackTotal: null,
                discNumber: null,
                discTotal: null,
                releaseDate: null,
                @explicit: null) with { PayloadJson = payloadJson });

    private static DeezSpoTag.Services.Library.ManualUnavailableTrackUpsertInput BuildInput(
        string payloadJson,
        int? queueDurationMs)
    {
        var createdAt = DateTimeOffset.UnixEpoch;
        var item = new DeezSpoTag.Services.Download.Queue.DownloadQueueItem(
            Id: 1,
            QueueUuid: "probe-queue",
            Engine: "deezer",
            ArtistName: "Item Artist",
            TrackTitle: "Item Title",
            Isrc: "USSM00000000",
            DeezerTrackId: "deezer-item",
            DeezerAlbumId: null,
            DeezerArtistId: null,
            SpotifyTrackId: null,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: null,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: queueDurationMs,
            DestinationFolderId: 7,
            QualityRank: null,
            QueueOrder: null,
            ContentType: "music",
            FinalizationStatus: null,
            EnrichmentStatus: null,
            Status: "unavailable",
            PayloadJson: payloadJson,
            Progress: null,
            Downloaded: null,
            Failed: 1,
            Error: "not available from enabled sources",
            CreatedAt: createdAt,
            UpdatedAt: createdAt);

        return ActivitiesController.BuildManualUnavailableTrackInput(
            item,
            DeezSpoTag.Services.Download.Shared.QueuePayloadJsonParser.Parse(payloadJson));
    }

    private static DeezSpoTag.Services.Library.ManualUnavailableTrackDto CreateManualUnavailableTrack(
        string? coverUrl,
        int? durationMs,
        int? trackNumber,
        int? trackTotal,
        int? discNumber,
        int? discTotal,
        string? releaseDate,
        bool? @explicit)
        => new(
            Id: 1,
            QueueUuid: "queue-uuid",
            Title: "Track Title",
            Artist: "Track Artist",
            Album: "Track Album",
            AlbumArtist: "Album Artist",
            Isrc: "USSM12345678",
            Engine: "deezer",
            SourceService: "deezer",
            SourceUrl: "https://example.test/track",
            DeezerId: "deezer-1",
            SpotifyId: "spotify-1",
            AppleId: null,
            QobuzId: null,
            TidalId: null,
            AmazonId: null,
            DestinationFolderId: 7,
            ExpectedFinalPath: "/music/Track Title.flac",
            Quality: "FLAC",
            ContentType: "music",
            Reason: "unavailable",
            PayloadJson: "{}",
            FirstUnavailableAtUtc: DateTimeOffset.UnixEpoch,
            NextRetryAtUtc: DateTimeOffset.UnixEpoch,
            AddedAtUtc: DateTimeOffset.UnixEpoch,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch,
            CoverUrl: coverUrl,
            DurationMs: durationMs,
            TrackNumber: trackNumber,
            TrackTotal: trackTotal,
            DiscNumber: discNumber,
            DiscTotal: discTotal,
            ReleaseDate: releaseDate,
            Explicit: @explicit);

    [Fact]
    public void ActiveWatchCycle_ProcessesArtistLedgerAdmissionAfterPlaylistWork()
    {
        // Artists are a separate domain but must never jump ahead of playlists: discovery
        // runs after playlist reconciliation, and the single end-of-run ledger admission
        // waits until both domains have written missing rows so a below-quota remainder is
        // judged against the full watchlist.
        var coordinator = ReadSource("DeezSpoTag.Web/Services/WatchlistRunCoordinator.cs");
        var cycleCoreStart = coordinator.IndexOf(
            "private async Task<PlaylistRunResult> RunWatchCycleCoreAsync(",
            StringComparison.Ordinal);
        var cycleCoreEnd = coordinator.IndexOf(
            "private void ThrowIfWatchlistStopped(",
            cycleCoreStart + 1,
            StringComparison.Ordinal);
        var cycleCore = coordinator[cycleCoreStart..cycleCoreEnd];
        var playlistProcess = cycleCore.IndexOf("await ProcessPlaylistWatchItemsAsync(", StringComparison.Ordinal);
        var artistDiscovery = cycleCore.IndexOf("await ProcessArtistWatchItemsAsync(", StringComparison.Ordinal);
        var finalAdmission = cycleCore.IndexOf(
            "await reconciler.AdmitDueMissingTracksFromLedgerAsync(",
            StringComparison.Ordinal);

        Assert.True(playlistProcess >= 0);
        Assert.True(artistDiscovery > playlistProcess);
        Assert.True(finalAdmission > artistDiscovery);
        Assert.DoesNotContain("AdmitArtistWatchMissingTracksFromLedgerAsync(", cycleCore, StringComparison.Ordinal);
        Assert.Contains(
            "await reconciler.AdmitDueMissingTracksWhenQuotaReadyAsync(",
            coordinator,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LedgerAdmission_ReconcilesLiveQueueOwnershipBeforeOrdering()
    {
        // Stale 'queued' ledger rows must be validated against the live download queue before
        // ordered admission reads the due rows, from both admission entry points.
        var engine = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        Assert.Contains(
            "private async Task<int> ReconcileLedgerOwnershipWithLiveQueueAsync(",
            engine,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            engine.Split("await ReconcileLedgerOwnershipWithLiveQueueAsync(cancellationToken);").Length - 1);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
