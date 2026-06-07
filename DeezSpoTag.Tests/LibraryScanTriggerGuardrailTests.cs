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
        Assert.Contains("includeTargetFiles: true", autoTagSource, StringComparison.Ordinal);
        Assert.DoesNotContain("The latest completed item will determine the AutoTag profile", source, StringComparison.Ordinal);
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
        Assert.DoesNotContain("_libraryScanRunner", source, StringComparison.Ordinal);
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
    public void AutoTagEnhancementRefresh_IsTargetedAndInterruptible()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        Assert.Contains("public List<string> EnhancedFilePaths", source, StringComparison.Ordinal);
        Assert.Contains("TrackEnhancedFilePath(job, stageName, status)", source, StringComparison.Ordinal);
        Assert.Contains("QueueEnhancementPlexRefreshBatchIfDue(job)", source, StringComparison.Ordinal);
        Assert.Contains("TriggerTargetedPlexRefreshForEnhancedFilesAsync", source, StringComparison.Ordinal);
        Assert.Contains("LastPlexRefreshEnhancedFileCount", source, StringComparison.Ordinal);
        Assert.Contains("GetMetadataParentKeysAsync", source, StringComparison.Ordinal);
        Assert.Contains("RefreshMetadataAsync", source, StringComparison.Ordinal);
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
    public void WatchlistPostDownloadSync_UsesDirectKnownFileIngestionWhenPathsAreKnown()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "WatchlistPostDownloadSyncService.cs");
        var methodBody = ExtractMethodBody(source, "private static async Task<bool> VerifyLocalLibraryIngestionAsync");

        Assert.Contains("ChangedFilePaths", source, StringComparison.Ordinal);
        Assert.Contains("await ingestionService.VerifyAsync", methodBody, StringComparison.Ordinal);
        Assert.Contains("return ingestion.IsComplete", methodBody, StringComparison.Ordinal);
        Assert.Contains("Missing final paths are a notifier bug", methodBody, StringComparison.Ordinal);
        Assert.Contains("Watchlist playlist direct library ingestion skipped because no final file paths were provided", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryScanRunner", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFilesAndWaitForIngestionAsync", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFoldersAsync", methodBody, StringComparison.Ordinal);
        Assert.Contains("watcher.ReconcilePlaylistAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatch_DoesNotRunPreQueueMediaServerSync()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "PlaylistWatchService.cs");

        Assert.DoesNotContain("PreQueuePlaylistSyncAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunPreQueueLibraryScanAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFoldersAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFilesAsync", source, StringComparison.Ordinal);
        Assert.Contains("selection.MissingTracks", source, StringComparison.Ordinal);
        Assert.Contains("missing_tracks_queued", source, StringComparison.Ordinal);
        Assert.DoesNotContain("candidates.Select(candidate => new PlaylistWatchTrackInsert(candidate.TrackSourceId, candidate.Isrc))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistSync_ResolvesEveryUnmappedPlexTrackInMainResolver()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");
        var methodBody = ExtractMethodBody(source, "private async Task<SyncMatchSummary> ResolvePlexRatingKeysAsync");

        Assert.DoesNotContain("PlexSequentialSearchFallbackLimit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Skipped sequential Plex search", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var index in unresolvedSearchIndexes)", methodBody, StringComparison.Ordinal);
        Assert.Contains("ResolvePlexRatingKeyAsync", methodBody, StringComparison.Ordinal);
        Assert.Contains("UpsertPlexTrackMetadataAsync", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedWatchDownloadClaims_FanOutCompletionToAllClaimedPlaylists()
    {
        var helperSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineAudioPostDownloadHelper.cs");
        var appSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagApp.cs");
        var intentSource = ReadSource("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");

        Assert.Contains("GetPlaylistWatchDownloadClaimsAsync", helperSource, StringComparison.Ordinal);
        Assert.Contains("NotifySharedWatchDownloadClaimsAsync", helperSource, StringComparison.Ordinal);
        Assert.Contains("status: \"pending\"", helperSource, StringComparison.Ordinal);
        Assert.Contains("UpdateSharedWatchDownloadClaimsStatusAsync", helperSource, StringComparison.Ordinal);
        Assert.Contains("GetPlaylistWatchDownloadClaimsAsync", appSource, StringComparison.Ordinal);
        Assert.Contains("private const string PendingStatus = \"pending\";", appSource, StringComparison.Ordinal);
        Assert.Contains("status: PendingStatus", appSource, StringComparison.Ordinal);
        Assert.Contains("UpdateSharedWatchDownloadClaimsStatusAsync", appSource, StringComparison.Ordinal);
        Assert.Contains("RelatedQueueUuids", intentSource, StringComparison.Ordinal);
        Assert.Contains("await _dedupeService.CheckAsync(BuildDedupeRequest(context), cancellationToken)", intentSource, StringComparison.Ordinal);
        Assert.Contains("dedupeDecision.QueueUuid", intentSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchlistPlaylistNotifier_CarriesFinalizedFilePaths()
    {
        var interfaceSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "IWatchlistPostDownloadSyncNotifier.cs");
        var moveSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTagDownloadMoveService.cs");
        var finalizationSource = ReadSource("DeezSpoTag.Web", "Services", "WatchlistFinalizationService.cs");

        Assert.Contains("NotifyFinalizedAsync", interfaceSource, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<string>? finalFilePaths", interfaceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyQueueItemFinalizedAsync", moveSource, StringComparison.Ordinal);
        Assert.Contains("UpdateFinalDestinationsAsync", moveSource, StringComparison.Ordinal);
        Assert.Contains("NotifyWatchlistFinalizedItemsAsync", ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"), StringComparison.Ordinal);
        Assert.Contains("await _notifier.NotifyFinalizedAsync", finalizationSource, StringComparison.Ordinal);
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
