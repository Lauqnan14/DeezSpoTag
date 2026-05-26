using System;
using System.IO;
using System.Linq;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LibraryScanTriggerGuardrailTests
{
    private static readonly long[] ExpectedChangedFolderIds = [5L];

    [Fact]
    public void DownloadOrchestration_UsesTargetedChangedFileScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");

        Assert.Contains("GetRecentMovedAudioFilesByDestinationAsync", source);
        Assert.Contains("await _scanRunner.RunChangedFilesAsync", source);
        Assert.Contains("targeted library scan completed", source);
        Assert.Contains("no moved library file paths detected", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_scanRunner.RunAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await _scanRunner.RunChangedFoldersAsync", source, StringComparison.Ordinal);
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
        var pipelineStart = source.IndexOf("private async Task RunPipelineAsync", StringComparison.Ordinal);
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
    public void AutoTagPostMoveScan_UsesTargetedChangedFileScansWhenPathsAreKnown()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var methodBody = ExtractMethodBody(source, "private async Task TriggerLibraryScanAfterAutoMoveAsync");

        Assert.Contains("ResolveChangedLibraryFolderIdsAsync", source);
        Assert.Contains("autoMoveSummary.ChangedFilePaths", source);
        Assert.Contains("await _libraryScanRunner.RunChangedFilesAsync", methodBody);
        Assert.Contains("Post auto-move library scan skipped because no changed file paths were reported", methodBody, StringComparison.Ordinal);
        Assert.Contains("moved={autoMoveSummary.MovedCount}", methodBody, StringComparison.Ordinal);
        Assert.Contains("failed={autoMoveSummary.FailedCount}", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("await _libraryScanRunner.RunChangedFoldersAsync", methodBody, StringComparison.Ordinal);
        Assert.Contains("TriggerLibraryScanAfterAutoMoveAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TriggerLibraryScanAfterAutoMovePlexRefreshRequestedAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_libraryScanRunner.EnqueueAsync(", source, StringComparison.Ordinal);
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
        Assert.Contains("OrganizeAfterAutoMoveAsync(job, path, context.ConfigPath, autoMove.Summary, cancellationToken)", source, StringComparison.Ordinal);
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
    public void WatchlistPostDownloadSync_UsesTargetedChangedFileScansWhenPathsAreKnown()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "WatchlistPostDownloadSyncService.cs");
        var methodBody = ExtractMethodBody(source, "private static async Task RunLocalLibraryScanAsync");

        Assert.Contains("ChangedFilePaths", source, StringComparison.Ordinal);
        Assert.Contains("await scanner.RunChangedFilesAsync", methodBody, StringComparison.Ordinal);
        Assert.Contains("Missing final paths are a notifier bug", methodBody, StringComparison.Ordinal);
        Assert.Contains("Watchlist playlist library scan skipped because no final file paths were provided", methodBody, StringComparison.Ordinal);
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
        Assert.Contains("EnqueueItemDecision.Fail(\"queue_duplicate\"", intentSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchlistPlaylistNotifier_CarriesFinalizedFilePaths()
    {
        var interfaceSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "IWatchlistPostDownloadSyncNotifier.cs");
        var moveSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTagDownloadMoveService.cs");
        var finalizationSource = ReadSource("DeezSpoTag.Web", "Services", "WatchlistFinalizationService.cs");

        Assert.Contains("NotifyFinalizedAsync", interfaceSource, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<string>? finalFilePaths", interfaceSource, StringComparison.Ordinal);
        Assert.Contains("NotifyQueueItemFinalizedAsync", moveSource, StringComparison.Ordinal);
        Assert.Contains("await _notifier.NotifyFinalizedAsync", finalizationSource, StringComparison.Ordinal);
        Assert.Contains("RepairPlaylistAsync", finalizationSource, StringComparison.Ordinal);
        Assert.Contains("transitions.Values", moveSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeLibraryScanService_IsNotRegisteredAsHostedService()
    {
        var source = ReadSource("DeezSpoTag.Web", "Program.cs");

        Assert.DoesNotContain("AddDeferredHostedService<DeezSpoTag.Web.Services.LibraryRealtimeScanService>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService<DeezSpoTag.Web.Services.LibraryRealtimeScanService>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sp.GetRequiredService<DeezSpoTag.Web.Services.LibraryRealtimeScanService>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundServices_DoNotUseFolderLevelLibraryScans()
    {
        var serviceFiles = Directory
            .EnumerateFiles(Path.Combine(GetRepositoryRoot(), "DeezSpoTag.Web", "Services"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}LibraryScanRunner.cs", StringComparison.Ordinal))
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}LibraryRuntimeSnapshotService.cs", StringComparison.Ordinal))
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}LibraryRealtimeScanService.cs", StringComparison.Ordinal))
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
