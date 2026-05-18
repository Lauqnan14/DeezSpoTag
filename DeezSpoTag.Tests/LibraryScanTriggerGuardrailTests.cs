using System;
using System.IO;
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

        Assert.Contains("ResolveChangedLibraryFolderIdsAsync", source);
        Assert.Contains("autoMoveSummary.ChangedFilePaths", source);
        Assert.Contains("await _libraryScanRunner.RunChangedFilesAsync", source);
        Assert.Contains("await _libraryScanRunner.RunChangedFoldersAsync", source);
        Assert.DoesNotContain("_libraryScanRunner.EnqueueAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchlistPostDownloadSync_UsesTargetedChangedFileScansWhenPathsAreKnown()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "WatchlistPostDownloadSyncService.cs");

        Assert.Contains("ChangedFilePaths", source, StringComparison.Ordinal);
        Assert.Contains("await scanner.RunChangedFilesAsync", source, StringComparison.Ordinal);
        Assert.Contains("did not provide changed file paths", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFoldersAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchlistPostDownloadNotifier_CarriesCompletedFilePaths()
    {
        var interfaceSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "IWatchlistPostDownloadSyncNotifier.cs");
        var appSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagApp.cs");
        var helperSource = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineAudioPostDownloadHelper.cs");

        Assert.Contains("IReadOnlyList<string>? changedFilePaths", interfaceSource, StringComparison.Ordinal);
        Assert.Contains("ResolveChangedFilePaths(documentPayload: payloadJson)", appSource, StringComparison.Ordinal);
        Assert.Contains("CollectFinalDestinationPaths", appSource, StringComparison.Ordinal);
        Assert.Contains("ResolveChangedFilePaths(payload)", helperSource, StringComparison.Ordinal);
        Assert.Contains("payload.FinalDestinations.Values", helperSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeLibraryScanService_IsRegisteredAsHostedService()
    {
        var source = ReadSource("DeezSpoTag.Web", "Program.cs");

        Assert.Contains("AddDeferredHostedService<DeezSpoTag.Web.Services.LibraryRealtimeScanService>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeLibraryScanService_UsesTargetedChangedFileScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "LibraryRealtimeScanService.cs");

        Assert.Contains("RunChangedFilesAsync", source, StringComparison.Ordinal);
        Assert.Contains("Realtime targeted library scan triggered", source, StringComparison.Ordinal);
        Assert.Contains("PendingFolderScan", source, StringComparison.Ordinal);
        Assert.Contains("ShouldQueueScan(fullPath)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_scanRunner.RunAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeLibraryScanService_RefreshesBaselineAfterTargetedScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "LibraryRealtimeScanService.cs");

        Assert.Contains("RefreshWatcherBaseline(folderId", source, StringComparison.Ordinal);
        Assert.Contains("public void RefreshBaseline(IEnumerable<string> filePaths)", source, StringComparison.Ordinal);
        Assert.Contains("_baselineFiles[normalizedPath] = currentState", source, StringComparison.Ordinal);
        Assert.Contains("_baselineFiles.Remove(normalizedPath)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeLibraryScanService_HandlesDeletesAndRenameOldPathsWithoutFullFolderScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "LibraryRealtimeScanService.cs");

        Assert.Contains("watcher.Deleted +=", source, StringComparison.Ordinal);
        Assert.Contains("OnFileRenamed(folderId, args.OldFullPath, args.FullPath)", source, StringComparison.Ordinal);
        Assert.Contains("DeletedFilePaths", source, StringComparison.Ordinal);
        Assert.Contains("RemoveLocalAudioFilesByPathAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanupMissingFilesAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeLibraryScanService_UsesPersistedBaselineWhenRepositoryIsConfigured()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "LibraryRealtimeScanService.cs");

        Assert.Contains("_repository.GetLocalScanFileStatesAsync", source, StringComparison.Ordinal);
        Assert.Contains("new FileBaselineState(state.LastWriteUtc, state.Size)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildBaselineAudioFiles(NormalizedRootPath)", source, StringComparison.Ordinal);
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
