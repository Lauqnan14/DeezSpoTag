using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LibraryScanTriggerGuardrailTests
{
    private static readonly long[] ExpectedChangedFolderIds = [5L];

    [Fact]
    public void DownloadOrchestration_UsesDirectKnownFileIngestion()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");

        Assert.Contains("GetRecentMovedAudioFilesByDestinationAsync", source);
        Assert.Contains("await _knownFileIngestionService.IngestAndVerifyAsync", source);
        Assert.Contains("IngestMovedFilesBeforeWatchlistFinalizationAsync", source);
        Assert.Contains("direct library ingestion completed", source);
        Assert.Contains("no moved library file paths detected", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_scanRunner", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_scanRunner.RunAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await _scanRunner.RunChangedFoldersAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadOrchestration_PersistsFinalizationAfterVerifiedIngestion()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");
        var ingestionIndex = source.IndexOf(
            "IngestMovedFilesBeforeWatchlistFinalizationAsync(group, summary.ChangedFilePaths",
            StringComparison.Ordinal);
        var outboxIndex = source.IndexOf(
            "PersistWatchlistFinalizationOutboxAsync(",
            StringComparison.Ordinal);

        Assert.True(ingestionIndex >= 0);
        Assert.True(outboxIndex > ingestionIndex);
        Assert.DoesNotContain("RefreshConfiguredMediaServersAfterMoveAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadOrchestration_QueuesMediaRefreshWithoutBlockingOnTargetServers()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");
        var ingestionIndex = source.IndexOf(
            "IngestMovedFilesBeforeWatchlistFinalizationAsync(group, summary.ChangedFilePaths",
            StringComparison.Ordinal);
        var refreshOutboxIndex = source.IndexOf(
            "await _mediaServerRefreshOutboxService.EnqueueAsync(",
            StringComparison.Ordinal);

        Assert.True(ingestionIndex >= 0);
        Assert.True(refreshOutboxIndex > ingestionIndex);
        Assert.DoesNotContain("RefreshConfiguredMediaServersForNonWatchlistMoveAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await _mediaServerLibraryRefreshService.RefreshConfiguredServersAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualEnrichmentAutoMove_QueuesTargetIdentityRefreshForMovedFiles()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var ingestionIndex = source.IndexOf(
            "await _knownFileIngestionService.IngestAndVerifyAsync(",
            StringComparison.Ordinal);
        var refreshOutboxIndex = source.IndexOf(
            "await EnqueueTargetIdentityRefreshForAutoMoveAsync(",
            StringComparison.Ordinal);

        Assert.True(ingestionIndex >= 0);
        Assert.True(refreshOutboxIndex > ingestionIndex);
        Assert.Contains("_mediaServerRefreshOutboxService.EnqueueAsync(folderId, files, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("Manual enrichment", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryMediaServerRefresh_DoesNotRebuildTargetTrackIndexes()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "MediaServerLibraryRefreshService.cs");

        Assert.Contains("RequestLibraryRefreshAsync", source, StringComparison.Ordinal);
        Assert.Contains("RefreshPlexAsync(state.Plex, updateTrackIndex: false", source, StringComparison.Ordinal);
        Assert.Contains("RefreshJellyfinAsync(state.Jellyfin, updateTrackIndex: false", source, StringComparison.Ordinal);
        Assert.Contains("RefreshNavidromeAsync(state.Navidrome, updateTrackIndex: false", source, StringComparison.Ordinal);
        Assert.Contains("if (updateTrackIndex)", source, StringComparison.Ordinal);
        Assert.Contains("UpdatePlexTrackMetadataIndexAsync", source, StringComparison.Ordinal);
        Assert.Contains("UpdateJellyfinTrackMetadataIndexAsync", source, StringComparison.Ordinal);
        Assert.Contains("UpdateNavidromeTrackMetadataIndexAsync", source, StringComparison.Ordinal);
        Assert.Contains("IngestTargetTracksAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetLibraryTracksAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetAudioTracksAsync", source, StringComparison.Ordinal);
        Assert.Contains("IngestConfiguredTargetIdentitiesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PromoteSharedIdentitiesFromMetadataAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryTargetIdentityRefresh_IsFirstClassLibraryAction()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "LibraryTargetIdentitiesApiController.cs");
        var libraryView = ReadSource("DeezSpoTag.Web", "Views", "Library", "Index.cshtml");
        var libraryScript = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "library.js");
        var extrasScript = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "library-apple-extras.js");

        Assert.Contains("api/library/target-identities", controller, StringComparison.Ordinal);
        Assert.Contains("GetTargetServerIdentityCoverageAsync", controller, StringComparison.Ordinal);
        Assert.Contains("FetchTargetIdentitiesAsync", controller, StringComparison.Ordinal);
        var refreshService = ReadSource("DeezSpoTag.Web", "Services", "MediaServerLibraryRefreshService.cs");
        Assert.Contains("DeleteMediaServerTrackMetadataForScopeAsync", refreshService, StringComparison.Ordinal);
        Assert.Contains("UpdateTrackMetadataIndexAsync", refreshService, StringComparison.Ordinal);
        Assert.Contains("data-target-identity-service=\"plex\"", libraryView, StringComparison.Ordinal);
        Assert.Contains("data-target-identity-service=\"jellyfin\"", libraryView, StringComparison.Ordinal);
        Assert.Contains("data-target-identity-service=\"navidrome\"", libraryView, StringComparison.Ordinal);
        Assert.Contains("Fetch Track IDs", libraryView, StringComparison.Ordinal);
        Assert.Contains("Reset &amp; Fetch Track IDs", libraryView, StringComparison.Ordinal);
        Assert.Contains("loadTargetIdentityStatus", libraryScript, StringComparison.Ordinal);
        Assert.Contains("runTargetIdentityRefresh", libraryScript, StringComparison.Ordinal);
        Assert.Contains("startTargetIdentityRefreshPolling", libraryScript, StringComparison.Ordinal);
        Assert.Contains("targetIdentityRefreshInProgress", libraryScript, StringComparison.Ordinal);
        Assert.Contains("state?.progress?.running === true", libraryScript, StringComparison.Ordinal);
        Assert.Contains("Target track IDs fetched for all connected servers.", libraryScript, StringComparison.Ordinal);
        Assert.Contains("GetTargetIdentityRefreshProgress", controller, StringComparison.Ordinal);
        Assert.Contains("StartTargetIdentityResetProgress", refreshService, StringComparison.Ordinal);
        Assert.Contains("statusRefreshed", libraryScript, StringComparison.Ordinal);
        Assert.Contains("ReportTargetIdentityProgress", ReadSource("DeezSpoTag.Web", "Services", "MediaServerLibraryRefreshService.cs"), StringComparison.Ordinal);
        Assert.Contains("fetchTargetTrackIdsButton", extrasScript, StringComparison.Ordinal);
        Assert.Contains("resetFetchTargetTrackIdsButton", extrasScript, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryTargetIdentityRefresh_UsesMissingFirstScopedIndex()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "LibraryTargetIdentitiesApiController.cs");
        var service = ReadSource("DeezSpoTag.Web", "Services", "MediaServerLibraryRefreshService.cs");
        var repository = ReadSource("DeezSpoTag.Services", "Library", "LibraryRepository.cs");
        var jellyfin = ReadSource("DeezSpoTag.Integrations", "Jellyfin", "JellyfinApiClient.cs");
        var navidrome = ReadSource("DeezSpoTag.Integrations", "Navidrome", "NavidromeApiClient.cs");

        Assert.Contains("FetchTargetIdentitiesAsync", controller, StringComparison.Ordinal);
        Assert.Contains("UpdateTrackMetadataIndexAsync(normalizedService, folderId", service, StringComparison.Ordinal);
        Assert.Contains("RebuildTrackMetadataIndexAsync(normalizedService, folderId", service, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAll(resultTasks)", controller, StringComparison.Ordinal);
        Assert.Contains("GetTargetServerIdentityLocalTracksAsync", repository, StringComparison.Ordinal);
        Assert.Contains("TargetServerIdentityLocalTrackDto", repository, StringComparison.Ordinal);
        Assert.Contains("TargetIdentityLocalIndex.Build", service, StringComparison.Ordinal);
        Assert.Contains("localIndex.MissingTrackIds.Count == 0", service, StringComparison.Ordinal);
        Assert.Contains("TryResolveByPath", service, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetIdentitySearchLimit", service, StringComparison.Ordinal);
        Assert.Contains("DeleteOrphanedMediaServerTrackMetadataAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveMissingTargetIdentitiesBySearchAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("IngestTargetTracksWithMetadataAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("IngestTargetTrackMetadataAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchTracksAsync(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("GetTrackIdsByFilePathsAsync(\n            tracks.Select", service.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveLocalTrackIdentityAsync(", service, StringComparison.Ordinal);
        Assert.Contains("AlbumArtists,Artists,Album", jellyfin, StringComparison.Ordinal);
        Assert.Contains("string? Album", jellyfin, StringComparison.Ordinal);
        Assert.Contains("public async Task<string?> LoginNativeApiAsync", navidrome, StringComparison.Ordinal);
        Assert.Contains("GetLibraryTracksAsync(\n        string serverUrl,\n        string nativeApiToken", navidrome.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryTrackRowsExposeStoredTargetServerIds()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "LibraryAlbumsApiController.cs");
        var repository = ReadSource("DeezSpoTag.Services", "Library", "LibraryRepository.cs");
        var playlistSync = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");

        Assert.Contains("GetAlbumTrackTargetServerIdsAsync", controller, StringComparison.Ordinal);
        Assert.Contains("PlexTrackId = targetServerIds?.PlexTrackId", controller, StringComparison.Ordinal);
        Assert.Contains("JellyfinTrackId = targetServerIds?.JellyfinTrackId", controller, StringComparison.Ordinal);
        Assert.Contains("NavidromeTrackId = targetServerIds?.NavidromeTrackId", controller, StringComparison.Ordinal);
        Assert.Contains("media_server_track_metadata", repository, StringComparison.Ordinal);
        Assert.Contains("TrackTargetServerIdsDto", repository, StringComparison.Ordinal);
        Assert.Contains("GetMediaServerItemIdsByTrackIdsAsync", playlistSync, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPlexRatingKeysByTrackIdsAsync", playlistSync, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadOrchestration_FinalDestinationReaderUsesDatabaseJsonOnly()
    {
        const string staleStagingPath = "/tmp/deezspotag/staging/Artist/Album/Artist - Song.flac";
        const string finalLibraryPath = "/tmp/deezspotag/library/Artist/Album/Artist - Song.flac";
        var finalDestinationsJson = $$"""
            {
              "{{staleStagingPath}}": "{{finalLibraryPath}}"
            }
            """;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var method = typeof(DownloadOrchestrationService).GetMethod(
            "CollectFinalDestinationJsonPaths",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, [finalDestinationsJson, paths]);

        Assert.Contains(finalLibraryPath, paths);
        Assert.DoesNotContain(staleStagingPath, paths);
    }

    [Fact]
    public void DownloadOrchestration_DoesNotMarkCompletedDownloadsProcessedWhenSourceFilesRemain()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");

        Assert.Contains("FilterCompletedMarkersReadyToPersistAsync", source, StringComparison.Ordinal);
        Assert.Contains("!PayloadHasExistingSourceUnderRoot(currentItem.PayloadJson, context.DownloadRootPath)", source, StringComparison.Ordinal);
        Assert.Contains("remain eligible for recovery", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private void PersistPipelineCompletionMarkers(PipelineRunContext context)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadOrchestration_GroupsPostDownloadWorkByDestinationProfile()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");
        var autoTagSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        Assert.Contains("private sealed record PipelineWorkGroup", source, StringComparison.Ordinal);
        Assert.Contains("BuildPipelineWorkGroups(profileContext, pendingItems, downloadRootPath)", source, StringComparison.Ordinal);
        Assert.Contains(".GroupBy(item => item.DestinationFolderId!.Value)", source, StringComparison.Ordinal);
        Assert.Contains("ApplyTargetFiles(configJson, sourceFiles)", source, StringComparison.Ordinal);
        Assert.Contains("RunPipelineEnrichmentAsync(context, group, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("BatchScopedFilesOnly = true", source, StringComparison.Ordinal);
        Assert.Contains("group.SourceFilePaths", source, StringComparison.Ordinal);
        Assert.Contains("FilterQueueItemsToBatchScope", ReadSource("DeezSpoTag.Web", "Services", "AutoTagDownloadMoveService.cs"), StringComparison.Ordinal);
        Assert.Contains("includeTargetFiles: true", autoTagSource, StringComparison.Ordinal);
        Assert.DoesNotContain("The latest completed item will determine the AutoTag profile", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStaleRecovery_DoesNotOwnFileMovement()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        Assert.DoesNotContain("RunStaleRecoveryCleanupAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldAutoMoveAfterEnrichmentStage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cleanup auto-move queued", source, StringComparison.Ordinal);
        Assert.Contains("file finalization remains owned by its authoritative pipeline", source, StringComparison.Ordinal);
        Assert.Contains("download-root finalization is owned by download orchestration", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadOrchestration_FinalizesCompletedDownloadsAfterAnyEnrichmentOutcome()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");

        Assert.Contains("ResolvePipelineEnrichmentResult", source, StringComparison.Ordinal);
        Assert.Contains("RunPostDownloadFinalizationAsync", source, StringComparison.Ordinal);
        Assert.Contains("_downloadMoveService.MoveForRootWithSummaryAsync", source, StringComparison.Ordinal);
        Assert.Contains("Automation: post-download finalization starting", source, StringComparison.Ordinal);
        Assert.Contains("Automation: post-download finalization completed", source, StringComparison.Ordinal);
        Assert.Contains("AutoTagLiterals.FailedStatus", source, StringComparison.Ordinal);
        Assert.Contains("AutoTagLiterals.InterruptedStatus", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadOrchestration_DoesNotTriggerRecentDownloadEnhancementFromCompletedDownloads()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");
        var pipelineStart = source.IndexOf("private async Task<bool> RunPipelineAsync", StringComparison.Ordinal);
        Assert.True(pipelineStart >= 0);
        var pipelineEnd = source.IndexOf("private async Task<bool> ResumePausedEnhancementAsync", pipelineStart, StringComparison.Ordinal);
        Assert.True(pipelineEnd > pipelineStart);
        var pipelineBody = source.Substring(pipelineStart, pipelineEnd - pipelineStart);

        Assert.DoesNotContain("RunRecentDownloadEnhancementAsync", pipelineBody, StringComparison.Ordinal);
        Assert.Contains("RunPostDownloadFinalizationAsync", pipelineBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadOrchestration_StagingGateDoesNotBlockOnUnrelatedAudioForever()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");

        Assert.Contains("unrelated audio file present in download staging; not blocking", source, StringComparison.Ordinal);
        Assert.Contains("ShouldDeferEnhancementForDownloadStagingAudio", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryStatusPolling_DoesNotReloadArtistsDuringActiveScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "library.js");
        var methodStart = source.IndexOf("async function refreshArtistsDuringActiveScan", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var activeScanStart = source.IndexOf("libraryState.wasScanRunning = true;", methodStart, StringComparison.Ordinal);
        Assert.True(activeScanStart >= 0);
        var methodEnd = source.IndexOf("\n}\n\nasync function saveLibrarySettings", activeScanStart, StringComparison.Ordinal);
        Assert.True(methodEnd > activeScanStart);
        var activeScanBody = source.Substring(activeScanStart, methodEnd - activeScanStart);

        Assert.DoesNotContain("loadArtists()", activeScanBody, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagPostMove_UsesDirectKnownFileIngestionWhenPathsAreKnown()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var methodBody = ExtractMethodBody(source, "private async Task IngestKnownFilesAfterAutoMoveAsync");

        Assert.Contains("ResolveChangedLibraryFolderIdsAsync", source);
        Assert.Contains("autoMoveSummary.ChangedFilePaths", source);
        Assert.Contains("await _knownFileIngestionService.IngestAndVerifyAsync", methodBody);
        Assert.Contains("Post auto-move direct library ingestion incomplete", methodBody);
        Assert.Contains("Post auto-move direct library ingestion skipped because no changed file paths were reported", methodBody, StringComparison.Ordinal);
        Assert.Contains("moved={autoMoveSummary.MovedCount}", methodBody, StringComparison.Ordinal);
        Assert.Contains("failed={autoMoveSummary.FailedCount}", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("await _libraryScanRunner.RunChangedFoldersAsync", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("RunFolderScanAndWaitAsync", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_libraryScanRunner.RunAsync", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_libraryScanRunner.EnqueueAsync", methodBody, StringComparison.Ordinal);
        Assert.Contains("IngestKnownFilesAfterAutoMoveAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TriggerLibraryScanAfterAutoMovePlexRefreshRequestedAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_libraryScanRunner.EnqueueAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadEnrichmentFinalization_DoesNotTriggerLibraryScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");

        Assert.Contains("IngestAndVerifyAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFilesAndWaitForIngestionAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_scanRunner", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_scanRunner.RunChangedFoldersAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_scanRunner.RunFolderScanAndWaitAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_scanRunner.EnqueueAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_scanRunner.RunAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagDownloadEnrichment_DoesNotOwnFinalMoveOrFailWhenNoStagesBuild()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        Assert.Contains("No runnable download enrichment stage was configured.", source, StringComparison.Ordinal);
        Assert.Contains("AutoTagLiterals.SkippedStatus", source, StringComparison.Ordinal);
        Assert.Contains("download enrichment finalization is owned by download orchestration", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))\n        {\n            AppendLog(job, \"tagging completed, auto-move starting\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagEnhancementRefresh_UsesSingleConfiguredServerCompletionPathWithoutLibraryReindex()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var workflowSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs");

        Assert.Contains("public List<string> EnhancedFilePaths", source, StringComparison.Ordinal);
        Assert.Contains("TrackEnhancedFilePath(job, stageName, status)", source, StringComparison.Ordinal);
        Assert.Contains("RefreshConfiguredServersAsync", source, StringComparison.Ordinal);
        Assert.Contains("updateTrackIndex: false", source, StringComparison.Ordinal);
        Assert.Contains("not waiting for a full library reindex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshEnhancementLibraryIndexAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("final-library-reindex", workflowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("library-index-refresh", workflowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RunFolderScanAndWaitAsync", workflowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueEnhancementPlexRefreshBatchIfDue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TriggerTargetedPlexRefreshForEnhancedFilesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LastPlexRefreshEnhancedFileCount", source, StringComparison.Ordinal);
        Assert.Contains("_jobCancellationSources", source, StringComparison.Ordinal);
        Assert.Contains("stopped = true;", source, StringComparison.Ordinal);
        Assert.Contains("RunIntegratedEnhancementWorkflowsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OrganizeAfterAutoMoveAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("generic organizer skipped", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TriggerPlexMetadataRefreshAfterEnhancementAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagLibraryOrganizer_AcceptsCancellationForEnhancementPostProcessing()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagLibraryOrganizer.cs");

        Assert.Contains("CancellationToken cancellationToken", source, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadOrchestration_SignalsVibeAnalysisWithoutAwaitingItAfterIngestion()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");
        var methodBody = ExtractMethodBody(source, "private async Task RunPostAutoTagStagesAsync");

        Assert.Contains("_analysisService.TrySignalBackgroundAnalysis", methodBody, StringComparison.Ordinal);
        Assert.Contains("vibe analysis signaled after direct library ingestion completed", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("await _analysisService.AnalyzeNowAsync", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchlistPostDownloadSync_UsesCachedCandidatesForFullPlaylistSync()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "WatchlistPostDownloadSyncService.cs");

        Assert.DoesNotContain("ChangedFilePaths", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VerifyLocalLibraryIngestionAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryScanRunner", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFilesAndWaitForIngestionAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFoldersAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetCachedPlaylistTrackCandidatesAsync", source, StringComparison.Ordinal);
        Assert.Contains("SyncAvailablePlaylistTracksAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncAvailablePlaylistTracksToTargetAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchlistRecovery_RefreshesCanonicalIndexAndReplaysFinalizationWithoutQueueRow()
    {
        var coordinator = ReadSource("DeezSpoTag.Web", "Services", "WatchlistRunCoordinator.cs");
        var postDownload = ReadSource("DeezSpoTag.Web", "Services", "WatchlistPostDownloadSyncService.cs");

        Assert.Contains("RefreshWatchlistIdentityIndexAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("GetLocalScanFileStatesAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("IngestAndVerifyAsync", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("IngestConfiguredTargetIdentitiesAsync", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFoldersAsync", coordinator, StringComparison.Ordinal);
        Assert.True(
            coordinator.IndexOf("await RefreshWatchlistIdentityIndexAsync(", StringComparison.Ordinal)
            < coordinator.IndexOf("var playlistItems = BuildPlaylistWatchItems", StringComparison.Ordinal),
            "The canonical library index must be refreshed before watchlist missing-track selection.");
        var identityIndexBody = ExtractMethodBody(coordinator, "private async Task RefreshWatchlistIdentityIndexAsync");
        Assert.DoesNotContain("IngestConfiguredTargetIdentitiesAsync", identityIndexBody, StringComparison.Ordinal);
        Assert.Contains("?? BuildOutboxQueueItem(work.QueueUuid, work.PayloadJson)", postDownload, StringComparison.Ordinal);
        Assert.DoesNotContain("Queue item is not currently available", postDownload, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatch_DoesNotRunPreQueueMediaServerSync()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "WatchlistEngine.cs");

        Assert.DoesNotContain("PreQueuePlaylistSyncAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunPreQueueLibraryScanAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFoldersAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFilesAsync", source, StringComparison.Ordinal);
        Assert.Contains("selection.MissingTracks", source, StringComparison.Ordinal);
        Assert.Contains("WatchlistHistoryStatus.MissingTracksQueued", source, StringComparison.Ordinal);
        Assert.DoesNotContain("candidates.Select(candidate => new PlaylistWatchTrackInsert(candidate.TrackSourceId, candidate.Isrc))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistSync_QueuesOnlyMissingTargetIdentitiesThroughTheOutbox()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");
        var outbox = ReadSource("DeezSpoTag.Web", "Services", "MediaServerRefreshOutboxService.cs");
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "LibraryTargetIdentitiesApiController.cs");
        var refresh = ReadSource("DeezSpoTag.Web", "Services", "MediaServerLibraryRefreshService.cs");

        Assert.Contains("GetMediaServerIdentityRefreshFilesAsync", source, StringComparison.Ordinal);
        Assert.Contains("EnqueueTargetAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestTargetLibraryRefreshAsync", source, StringComparison.Ordinal);
        Assert.Contains("job.DestinationFolderId", outbox, StringComparison.Ordinal);
        Assert.Contains("job.RequestedTrackIds", outbox, StringComparison.Ordinal);
        Assert.DoesNotContain("if (unresolvedPaths.Count > 0 || trackIds.Count == 0)", outbox, StringComparison.Ordinal);
        Assert.Contains("job.AttemptCount == 0", outbox, StringComparison.Ordinal);
        Assert.Contains("scan submitted; waiting for requested track IDs", outbox, StringComparison.Ordinal);
        Assert.Contains("FetchTargetIdentitiesAsync", outbox, StringComparison.Ordinal);
        Assert.Contains("FetchTargetIdentitiesAsync", controller, StringComparison.Ordinal);
        Assert.Contains("public async Task<TargetIdentityFetchResult> FetchTargetIdentitiesAsync", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateTrackMetadataIndexAsync(\n            job.TargetService", outbox.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("ResolveIdentityImportRetryDelay", outbox, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(60)", outbox, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(2)", outbox, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(3)", outbox, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(5)", outbox, StringComparison.Ordinal);
        Assert.Contains("WatchlistWakeReason.TargetSync", outbox, StringComparison.Ordinal);
        Assert.DoesNotContain("AddMinutes(delayMinutes)", outbox, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt == 1 ? 5 : Math.Min(30, attempt * 5)", outbox, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 180)]
    [InlineData(4, 300)]
    public void MediaServerRefreshOutbox_UsesPostScanIdentityImportSchedule(int attempt, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            MediaServerRefreshOutboxService.ResolveIdentityImportRetryDelay(attempt));
    }

    [Fact]
    public void ConfiguredServerRefresh_RebuildsIdentityIndexForEveryTargetWhenRequested()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "MediaServerLibraryRefreshService.cs");
        var ingestBody = ExtractMethodBody(source, "public async Task<MediaServerIdentityIngestSummary> IngestConfiguredTargetIdentitiesAsync");
        var indexBody = ExtractMethodBody(source, "public async Task UpdateTrackMetadataIndexAsync");

        Assert.Contains("UpdateTrackMetadataIndexAsync(service, cancellationToken)", ingestBody, StringComparison.Ordinal);
        Assert.Contains("case PlexService", indexBody, StringComparison.Ordinal);
        Assert.Contains("case JellyfinService", indexBody, StringComparison.Ordinal);
        Assert.Contains("case NavidromeService", indexBody, StringComparison.Ordinal);
        Assert.Contains("IngestTargetTracksAsync(PlexService", source, StringComparison.Ordinal);
        Assert.Contains("IngestTargetTracksAsync(JellyfinService", source, StringComparison.Ordinal);
        Assert.Contains("IngestTargetTracksAsync(NavidromeService", source, StringComparison.Ordinal);
        Assert.Contains("TargetIdentityLocalIndex.Build", source, StringComparison.Ordinal);
        Assert.Contains("TryResolveByPath", source, StringComparison.Ordinal);
        Assert.Contains("DeleteOrphanedMediaServerTrackMetadataAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveMissingTargetIdentitiesBySearchAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IngestTargetTracksWithMetadataAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IngestTargetTrackMetadataAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchTracksAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveLocalTrackIdentityAsync", source, StringComparison.Ordinal);
        Assert.Contains("RefreshPlexAsync(state.Plex, updateTrackIndex, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("RefreshJellyfinAsync(state.Jellyfin, updateTrackIndex, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("RefreshNavidromeAsync(state.Navidrome, updateTrackIndex, cancellationToken)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetIdentitySearch_RequestsFilePathsFromEveryServer()
    {
        var jellyfin = ReadSource("DeezSpoTag.Integrations", "Jellyfin", "JellyfinApiClient.cs");
        var plex = ReadSource("DeezSpoTag.Integrations", "Plex", "PlexApiClient.cs");
        var navidrome = ReadSource("DeezSpoTag.Integrations", "Navidrome", "NavidromeApiClient.cs");
        var jellyfinSearch = ExtractMethodBody(jellyfin, "public async Task<List<JellyfinAudioTrack>> SearchTracksAsync");
        var jellyfinList = ExtractMethodBody(jellyfin, "public async Task<List<JellyfinAudioTrack>> GetAudioTracksAsync");

        Assert.Contains("Fields=Path,RunTimeTicks,AlbumArtists,Artists", jellyfinSearch, StringComparison.Ordinal);
        Assert.Contains("Fields=Path,RunTimeTicks,AlbumArtists,Artists", jellyfinList, StringComparison.Ordinal);
        Assert.Contains("NormalizeTrackFilePath", plex, StringComparison.Ordinal);
        Assert.Contains("Part", plex, StringComparison.Ordinal);
        Assert.Contains("song.Path", navidrome, StringComparison.Ordinal);
        Assert.Contains("ResolveNativeSongPath", navidrome, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistSync_ResolvesEveryUnmappedPlexTrackInMainResolver()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");
        var resolver = ReadSource("DeezSpoTag.Web", "Services", "SharedIdentityResolver.cs");
        var methodBody = ExtractMethodBody(source, "private async Task<SyncMatchSummary> ResolvePlexRatingKeysAsync");

        Assert.DoesNotContain("PlexSequentialSearchFallbackLimit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Skipped sequential Plex search", source, StringComparison.Ordinal);
        Assert.Contains("ResolveSharedTargetIdentitiesAsync", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolvePlexRatingKeyAsync", methodBody, StringComparison.Ordinal);
        Assert.Contains("GetMediaServerItemIdsByTrackIdsAsync", resolver, StringComparison.Ordinal);
        Assert.DoesNotContain("UpsertMediaServerTrackMetadataAsync", resolver, StringComparison.Ordinal);
        Assert.Contains("SelectBestMediaServerMatch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpsertPlexTrackMetadataAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedWatchDownloadClaims_FanOutCompletionToAllClaimedPlaylists()
    {
        var helperSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineAudioPostDownloadHelper.cs");
        var appSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagApp.cs");
        var intentSource = ReadSource("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");

        Assert.Contains("GetPlaylistWatchDownloadClaimsAsync", helperSource, StringComparison.Ordinal);
        Assert.Contains("MarkSharedWatchDownloadClaimsDownloadedAsync", helperSource, StringComparison.Ordinal);
        Assert.Contains("status: \"pending\"", helperSource, StringComparison.Ordinal);
        Assert.Contains("UpdateSharedWatchDownloadClaimsStatusAsync", helperSource, StringComparison.Ordinal);
        Assert.Contains("GetPlaylistWatchDownloadClaimsAsync", appSource, StringComparison.Ordinal);
        Assert.Contains("private const string PendingStatus = \"pending\";", appSource, StringComparison.Ordinal);
        Assert.Contains("status: PendingStatus", appSource, StringComparison.Ordinal);
        Assert.Contains("UpdateSharedWatchDownloadClaimsStatusAsync", appSource, StringComparison.Ordinal);
        Assert.Contains("RelatedQueueUuids", intentSource, StringComparison.Ordinal);
        Assert.Contains("await _dedupeService.CheckAsync(BuildDedupeRequest(context, finalOutputPath), cancellationToken)", intentSource, StringComparison.Ordinal);
        Assert.Contains("dedupeDecision.QueueUuid", intentSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchlistPlaylistNotifier_RequestsOnlyAffectedPlaylistAfterFinalization()
    {
        var interfaceSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "IWatchlistPostDownloadSyncNotifier.cs");
        var moveSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTagDownloadMoveService.cs");
        var finalizationSource = ReadSource("DeezSpoTag.Web", "Services", "WatchlistFinalizationService.cs");

        Assert.Contains("RequestPlaylistSyncAsync", interfaceSource, StringComparison.Ordinal);
        Assert.Contains("string source", interfaceSource, StringComparison.Ordinal);
        Assert.Contains("string playlistId", interfaceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IReadOnlyList<string>? finalFilePaths", interfaceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyQueueItemFinalizedAsync", moveSource, StringComparison.Ordinal);
        Assert.Contains("UpdateFinalDestinationsAsync", moveSource, StringComparison.Ordinal);
        Assert.Contains("PersistWatchlistFinalizationOutboxAsync", ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"), StringComparison.Ordinal);
        Assert.Contains("ResolvePlaylistWatchMissingTracksByQueueAsync", ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"), StringComparison.Ordinal);
        Assert.Contains("UpsertWatchlistFinalizationOutboxAsync", ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("RequestAllPlaylistSyncAsync", finalizationSource, StringComparison.Ordinal);
        Assert.Contains("EnqueueWatchlistReconciliationRequestAsync", finalizationSource, StringComparison.Ordinal);
        Assert.Contains("RepairPlaylistAsync", finalizationSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeLibraryScanService_IsRemoved()
    {
        var source = ReadSource("DeezSpoTag.Web", "Program.cs");
        var servicePath = Path.Combine(GetRepositoryRoot(), "DeezSpoTag.Web", "Services", "LibraryRealtimeScanService.cs");

        Assert.False(File.Exists(servicePath), "Dead realtime scan service must stay removed.");
        Assert.DoesNotContain("LibraryRealtimeScanService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddDeferredHostedService<DeezSpoTag.Web.Services.LibraryRealtimeScanService>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService<DeezSpoTag.Web.Services.LibraryRealtimeScanService>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sp.GetRequiredService<DeezSpoTag.Web.Services.LibraryRealtimeScanService>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryScanRunner_CoalescesBusyScansInsteadOfRecursing()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "LibraryScanRunner.cs");

        Assert.Contains("QueuePendingScan(request);", source, StringComparison.Ordinal);
        Assert.Contains("DrainPendingScheduledScansAsync", source, StringComparison.Ordinal);
        Assert.Contains("Library full scan request coalesced; pending targeted scans were absorbed.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await WaitForCurrentScanAsync(cancellationToken);\n                await RunAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryScanRunner_FullScansAbsorbPendingTargetedScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "LibraryScanRunner.cs");
        var queueMethod = ExtractMethodBody(source, "private void QueuePendingScan");
        var startMethod = ExtractMethodBody(source, "private bool TryStartScan");

        Assert.Contains("_pendingFolderScans.Clear();", queueMethod, StringComparison.Ordinal);
        Assert.Contains("ClearPendingChangedFileScansLocked();", queueMethod, StringComparison.Ordinal);
        Assert.Contains("_activeScanScope == ScanScope.Full", queueMethod, StringComparison.Ordinal);
        Assert.Contains("scope == ScanScope.Full", startMethod, StringComparison.Ordinal);
        Assert.Contains("ClearPendingChangedFileScansLocked();", startMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryScanRunner_FolderScanAndWaitDrainsQueuedFolderScan()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "LibraryScanRunner.cs");
        var methodBody = ExtractMethodBody(source, "public async Task RunFolderScanAndWaitAsync");

        Assert.Contains("await RunAsync(", methodBody, StringComparison.Ordinal);
        Assert.Contains("await WaitForScheduledScansIdleAsync(cancellationToken);", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("await WaitForCurrentScanAsync(cancellationToken);", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryScanRunner_DoesNotDrainPendingScansAfterCancellation()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "LibraryScanRunner.cs");
        var helperBody = ExtractMethodBody(source, "private static bool ShouldDrainPendingAfterRun");

        Assert.Contains("ShouldDrainPendingAfterRun(ownsActiveScan, cts, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("cts?.IsCancellationRequested != true", helperBody, StringComparison.Ordinal);
        Assert.Contains("!callerCancellationToken.IsCancellationRequested", helperBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryScanRunner_PendingFullScanAbsorbsWaitingChangedFileBatches()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "LibraryScanRunner.cs");
        var drainBody = ExtractMethodBody(source, "private async Task DrainChangedFileScansAsync");

        Assert.Contains("if (HasPendingFullScan())", drainBody, StringComparison.Ordinal);
        Assert.Contains("Targeted library scan batch absorbed by pending full library scan", drainBody, StringComparison.Ordinal);
        Assert.Contains("await WaitForScheduledScansIdleAsync(cancellationToken);", drainBody, StringComparison.Ordinal);
        Assert.True(drainBody.IndexOf("if (HasPendingFullScan())", StringComparison.Ordinal)
            < drainBody.IndexOf("await RunChangedFilesBatchAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyPlaylistSyncScanPath_IsRemoved()
    {
        var root = GetRepositoryRoot();

        Assert.False(File.Exists(Path.Combine(root, "DeezSpoTag.Services", "PlaylistSync", "PlexSyncService.cs")),
            "Dead legacy Plex sync scan path must stay removed.");
        Assert.False(File.Exists(Path.Combine(root, "DeezSpoTag.Services", "PlaylistSync", "SyncOrchestrator.cs")),
            "Dead legacy sync orchestrator must stay removed.");
    }

    [Fact]
    public void BackgroundServices_DoNotUseFolderLevelLibraryScans()
    {
        var serviceFiles = Directory
            .EnumerateFiles(Path.Combine(GetRepositoryRoot(), "DeezSpoTag.Web", "Services"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}LibraryScanRunner.cs", StringComparison.Ordinal))
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}LibraryRuntimeSnapshotService.cs", StringComparison.Ordinal))
            .ToList();

        foreach (var path in serviceFiles)
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("RunChangedFoldersAsync(", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LibraryRepository_SupportsTargetedAudioFileDeletionByPath()
    {
        var source = ReadSource("DeezSpoTag.Services", "Library", "LibraryRepository.cs");

        Assert.Contains("RemoveLocalAudioFilesByPathAsync", source, StringComparison.Ordinal);
        Assert.Contains("audio_file_delete_target", source, StringComparison.Ordinal);
        Assert.Contains("ComputeRelativePath(normalizedRoot, normalizedPath)", source, StringComparison.Ordinal);
        Assert.Contains("DeleteEmptyAlbumLocalRowsAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagMoveSummary_TracksChangedFolderIdsAndFilePaths()
    {
        var summary = new AutoTagMoveSummary();

        summary.MarkChangedFolder(5);
        summary.MarkChangedFolder(5);
        summary.MarkChangedFolder(0);
        summary.MarkChangedFile("/music/Artist/Album/Track.flac");
        summary.MarkChangedFile("/music/Artist/Album/Track.flac");
        var clone = summary.Clone();

        Assert.Equal(ExpectedChangedFolderIds, summary.ChangedFolderIds);
        Assert.Equal(ExpectedChangedFolderIds, clone.ChangedFolderIds);
        Assert.Equal(["/music/Artist/Album/Track.flac"], summary.ChangedFilePaths);
        Assert.Equal(["/music/Artist/Album/Track.flac"], clone.ChangedFilePaths);
    }

    private static string ReadSource(params string[] pathParts)
    {
        var path = Path.Combine(GetRepositoryRoot(), Path.Combine(pathParts));
        return File.ReadAllText(path);
    }

    private static string ExtractMethodBody(string source, string methodSignature)
    {
        var start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method: {methodSignature}");
        var brace = source.IndexOf('{', start);
        Assert.True(brace > start, $"Could not find method body: {methodSignature}");
        var depth = 0;
        for (var index = brace; index < source.Length; index++)
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
                    return source.Substring(brace, index - brace + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not extract method body: {methodSignature}");
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }
}
