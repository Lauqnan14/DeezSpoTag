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
        var source = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.Contains("_watchlistRunQueueBudget.TryReserve(1)", source, StringComparison.Ordinal);
        Assert.Contains("_watchlistRunQueueBudget.Release(1)", source, StringComparison.Ordinal);
        Assert.Contains("result.Queued.Count", source, StringComparison.Ordinal);
        Assert.Contains("allowAutomaticSecondaryQuality: false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetActiveDownloadCountAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WatchQueueCapacity", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_UsesStrictQueueGateAndTracksGateDeferrals()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");
        var intentSource = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        Assert.Contains("EvaluateDownloadGateAsync", watchSource, StringComparison.Ordinal);
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
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.DoesNotContain("GetUnfinishedWatchlistDownloadCountAsync", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetActiveWatchlistDownloadCountAsync", watchSource, StringComparison.Ordinal);
        Assert.Contains("_watchlistRunQueueBudget.TryReserve(1)", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_SetsDeferredWhenTrackIsDeferredByDownloadGate()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.Contains("var deferred = false;", watchSource, StringComparison.Ordinal);
        Assert.Contains("deferred = true;", watchSource, StringComparison.Ordinal);
        Assert.Contains("new QueueWatchResult(", watchSource, StringComparison.Ordinal);
        Assert.Contains("Deferred: deferred", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_DoesNotUseResolutionAttemptBudgetAsQueueGate()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchHostedService.cs");

        Assert.DoesNotContain("attemptedCount >= maxResolutionAttempts", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("watch queue reached resolution-attempt budget", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("resolutionAttempts", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("WatchMaxTracksPerPlaylistCheck", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchTriggers_DoNotWaitAndStartAnotherBudgetedRun()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchHostedService.cs");

        Assert.Contains("Interlocked.Exchange(ref _triggerPending, 1)", hostedSource, StringComparison.Ordinal);
        Assert.Contains("if (!await _runLock.WaitAsync(0, cancellationToken))", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("await _runLock.WaitAsync(cancellationToken);", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedCycle_ChecksExistingWatchlistDownloadsBeforeOpeningRunBudget()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchHostedService.cs");
        var activeCheckIndex = hostedSource.IndexOf("HasActiveWatchlistDownloadsAsync", StringComparison.Ordinal);
        var beginRunIndex = hostedSource.IndexOf("runQueueBudget?.BeginRun", StringComparison.Ordinal);

        Assert.True(activeCheckIndex >= 0);
        Assert.True(beginRunIndex > activeCheckIndex);
        Assert.Contains("WatchlistQueueBlockReason.PreviousWatchlistRunActive", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedCycle_BlocksPlaylistProcessingWhenPreviousBatchWorkIsActive()
    {
        var hostedSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchHostedService.cs");
        var batchGateIndex = hostedSource.IndexOf("HasActiveWatchlistBatchWorkAsync", StringComparison.Ordinal);
        var processPlaylistIndex = hostedSource.IndexOf("var playlistRunResult = await ProcessPlaylistWatchItemsAsync(", StringComparison.Ordinal);

        Assert.True(batchGateIndex >= 0);
        Assert.True(processPlaylistIndex > batchGateIndex);
        Assert.Contains("HasPendingPlaylistWatchBatchWorkAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("previous watchlist download, enrichment, finalization, or claim work is still active", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistReconciliation_SupportsSyncOnlyModeWithoutQueuePlanning()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");
        var postDownloadSource = ReadSource("DeezSpoTag.Web/Services/WatchlistPostDownloadSyncService.cs");

        Assert.Contains("SyncOnly", watchSource, StringComparison.Ordinal);
        Assert.Contains("var queuePlanningAllowed = mode == PlaylistReconciliationMode.QueuePlanningAllowed", watchSource, StringComparison.Ordinal);
        Assert.Contains("if (queuePlanningAllowed)", watchSource, StringComparison.Ordinal);
        Assert.Contains("mode: PlaylistWatchService.PlaylistReconciliationMode.SyncOnly", postDownloadSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviousWatchlistRunBlock_HasSpecificNonFailureStatusAndMessage()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.Contains("Waiting for downloads from the previous watchlist run to finish.", watchSource, StringComparison.Ordinal);
        Assert.Contains("queue_deferred_previous_watchlist_active", watchSource, StringComparison.Ordinal);
        Assert.Contains("WatchQueueStopReason.PreviousWatchlistRunActive", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalTrackUnavailableFailure_PersistsWatchlistAvailabilityRecheck()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");
        var downloadSource = ReadSource("DeezSpoTag.Services/Download/Shared/EngineAudioPostDownloadHelper.cs");
        var controllerSource = ReadSource("DeezSpoTag.Web/Controllers/Api/LibraryPlaylistWatchlistApiController.cs");
        var repositorySource = ReadSource("DeezSpoTag.Services/Library/LibraryRepository.cs");

        Assert.Contains("WatchlistUnavailableSettingsFingerprint = BuildUnavailableSettingsFingerprint(options)", watchSource, StringComparison.Ordinal);
        Assert.Contains("UnavailableRecheckDays", watchSource, StringComparison.Ordinal);
        Assert.Contains("IsAvailabilityRecheckWindowActive", watchSource, StringComparison.Ordinal);
        Assert.Contains("skipped_unavailable_recheck_window", watchSource, StringComparison.Ordinal);
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
        var apiSource = ReadSource("DeezSpoTag.Web/Controllers/Api/LibraryPlaylistWatchlistApiController.cs");

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
