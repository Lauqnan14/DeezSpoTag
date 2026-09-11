using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Security;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services.CoverPort;
using DeezSpoTag.Web.Services.AutoTag;

namespace DeezSpoTag.Web.Services;

public partial class AutoTagService
{

    private async Task<AutoMoveExecutionResult> RunFinalAutoMoveAsync(
        AutoTagJob job,
        string path,
        string configPath,
        Dictionary<string, FileTagOutcome> fileOutcomes,
        CancellationToken cancellationToken)
    {
        if (!IsManualEnrichmentRunIntent(job.RunIntent)
            && ConfiguredDownloadRootResolver.TryResolve(
                _settingsService,
                "download location",
                "download location is not configured.",
                out var configuredDownloadRoot,
                out _)
            && IsPathUnderRoot(path, configuredDownloadRoot))
        {
            AppendLog(job, "auto-move skipped: download-root finalization is owned by download orchestration");
            var summary = new AutoTagMoveSummary
            {
                Error = "auto-move skipped for download-root run."
            };
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(false, summary);
        }

        if (IsEnhancementRunIntent(job.RunIntent))
        {
            AppendLog(job, "auto-move skipped: enhancement run uses configured enhancement workflows only");
            var summary = new AutoTagMoveSummary
            {
                Error = "auto-move skipped for enhancement run."
            };
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(false, summary);
        }

        if (string.Equals(job.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            AppendLog(job, "auto-move skipped: download enrichment finalization is owned by download orchestration");
            var summary = new AutoTagMoveSummary
            {
                Error = "auto-move skipped for download enrichment run."
            };
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(false, summary);
        }

        var (taggedFiles, failedFiles) = BuildMoveFileSets(fileOutcomes);
        if (IsManualEnrichmentRunIntent(job.RunIntent))
        {
            failedFiles = Array.Empty<string>();
        }
        AppendLog(job, "tagging completed, auto-move starting");
        var result = await MoveAfterAutoTagAsync(job, path, configPath, taggedFiles, failedFiles, cancellationToken);
        if (!IsManualEnrichmentRunIntent(job.RunIntent) || !result.Completed)
        {
            return result;
        }

        var remainingTaggedFiles = taggedFiles
            .Where(file => !string.IsNullOrWhiteSpace(file) && File.Exists(file))
            .ToList();
        if (remainingTaggedFiles.Count == 0)
        {
            return result;
        }

        result.Summary.Error = $"Manual enrichment finalization left {remainingTaggedFiles.Count} enriched file(s) in staging.";
        ApplyAutoMoveSummary(job, result.Summary);
        AppendLog(job, result.Summary.Error);
        return new AutoMoveExecutionResult(false, result.Summary);
    }

    private async Task IngestKnownFilesAfterAutoMoveAsync(
        AutoTagJob job,
        AutoTagMoveSummary autoMoveSummary,
        CancellationToken cancellationToken)
    {
        if (autoMoveSummary.MovedCount <= 0)
        {
            return;
        }

        if (await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
        {
            AppendLog(job, "post auto-move direct library ingestion skipped (downloads active).");
            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Post auto-move direct library ingestion skipped because downloads became active."));
            return;
        }

        var changedFolderIds = await ResolveChangedLibraryFolderIdsAsync(autoMoveSummary, cancellationToken);
        if (changedFolderIds.Count == 0)
        {
            AppendLog(job, "post auto-move direct library ingestion skipped (no moved library folders).");
            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Post auto-move direct library ingestion skipped because no changed library folders were detected."));
            return;
        }

        if (autoMoveSummary.ChangedFilePaths.Count > 0)
        {
            var changedFilesByFolder = await ResolveChangedLibraryFilesByFolderAsync(
                autoMoveSummary,
                changedFolderIds,
                cancellationToken);
            if (changedFilesByFolder.Count == 0)
            {
                changedFilesByFolder = changedFolderIds.ToDictionary(
                    folderId => folderId,
                    _ => autoMoveSummary.ChangedFilePaths.ToList());
            }

            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Post auto-move direct library ingestion starting for {autoMoveSummary.ChangedFilePaths.Count} file(s) in folder(s): {string.Join(", ", changedFolderIds)}."));
            AppendLog(job, $"post auto-move direct library ingestion starting for {autoMoveSummary.ChangedFilePaths.Count} file(s) in folder(s): {string.Join(", ", changedFolderIds)}");
            var ingestion = await _knownFileIngestionService.IngestAndVerifyAsync(
                changedFilesByFolder,
                cancellationToken);
            if (!ingestion.IsComplete)
            {
                _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "error",
                    $"Post auto-move direct library ingestion incomplete; {ingestion.MissingFilePaths.Count} moved audio file(s) are missing from the library DB."));
                AppendLog(job, $"post auto-move direct library ingestion incomplete; {ingestion.MissingFilePaths.Count} moved audio file(s) missing from DB");
            }
            return;
        }

        _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Post auto-move direct library ingestion skipped because no changed file paths were reported (folders={string.Join(", ", changedFolderIds)}, moved={autoMoveSummary.MovedCount}, skipped={autoMoveSummary.SkippedCount}, failed={autoMoveSummary.FailedCount})."));
        AppendLog(job, $"post auto-move direct library ingestion skipped (no changed file paths; folders={string.Join(", ", changedFolderIds)}, moved={autoMoveSummary.MovedCount}, skipped={autoMoveSummary.SkippedCount}, failed={autoMoveSummary.FailedCount})");
    }

    private static void AddMatchingLibraryFolders(
        string destinationRoot,
        IReadOnlyCollection<FolderDto> folders,
        HashSet<long> changed)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            return;
        }

        foreach (var folder in folders.Where(static folder => folder.Id > 0 && !string.IsNullOrWhiteSpace(folder.RootPath)))
        {
            try
            {
                if (IsPathUnderRoot(destinationRoot, folder.RootPath))
                {
                    changed.Add(folder.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ignore paths the runtime cannot normalize; explicit changed folder ids remain authoritative.
            }
        }
    }

    private static AutoTagOrganizerOptions BuildFolderUniformityOptions(
        JsonObject folderUniformity)
    {
        var options = new AutoTagOrganizerOptions
        {
            IncludeSubfolders = ReadBool(folderUniformity, AutoTagLiterals.IncludeSubfoldersKey) ?? true,
            MoveMisplacedFiles = ReadBool(folderUniformity, "moveMisplacedFiles") ?? true,
            MergeIntoExistingDestinationFolders = ReadBool(folderUniformity, "mergeIntoExistingDestinationFolders") != false,
            RenameFilesToTemplate = ReadBool(folderUniformity, "renameFilesToTemplate") != false,
            RemoveEmptyFolders = ReadBool(folderUniformity, "removeEmptyFolders") != false,
            MergeNoAudioArtistFolders = ReadBool(folderUniformity, "mergeNoAudioArtistFolders") != false,
            ReconcileOrphanArtistFolders = ReadBool(folderUniformity, "reconcileOrphanArtistFolders") != false,
            QuarantineNoAudioDirectories = ReadBool(folderUniformity, "quarantineNoAudioDirectories") == true,
            ResolveSameTrackQualityConflicts = ReadBool(folderUniformity, "resolveSameTrackQualityConflicts") != false,
            KeepBothOnUnresolvedConflicts = ReadBool(folderUniformity, "keepBothOnUnresolvedConflicts") != false,
            OnlyMoveWhenTagged = ReadBool(folderUniformity, "onlyMoveWhenTagged") == true,
            OnlyReorganizeAlbumsWithFullTrackSets = ReadBool(folderUniformity, "onlyReorganizeAlbumsWithFullTrackSets") == true,
            SkipCompilationFolders = ReadBool(folderUniformity, "skipCompilationFolders") == true,
            SkipVariousArtistsFolders = ReadBool(folderUniformity, "skipVariousArtistsFolders") == true,
            GenerateReconciliationReport = ReadBool(folderUniformity, "generateReconciliationReport") == true,
            UseShazamForUntaggedFiles = ReadBool(folderUniformity, "useShazamForUntaggedFiles") == true,
            DuplicateConflictPolicy = folderUniformity["duplicateConflictPolicy"]?.GetValue<string>() ?? AutoTagOrganizerOptions.DuplicateConflictKeepBest,
            DuplicatesFolderName = folderUniformity["duplicatesFolderName"]?.GetValue<string>() ?? DuplicateCleanerService.DuplicatesFolderName
        };

        return options;
    }

    private void ApplyAutoMoveSummary(AutoTagJob job, AutoTagMoveSummary summary)
    {
        job.AutoMoveSummary = summary.Clone();
        var destinations = summary.DestinationRoots.Count > 0
            ? string.Join(", ", summary.DestinationRoots)
            : "<none>";
        const string label = "auto-move summary";
        AppendLog(
            job,
            $"{label}: moved={summary.MovedCount}, skipped={summary.SkippedCount}, failed={summary.FailedCount}, destinations=[{destinations}]");
        if (!string.IsNullOrWhiteSpace(summary.Error))
        {
            AppendLog(job, $"{label}: error={summary.Error}");
        }

        SaveJob(job);
    }

    private static bool ResolveDisableAutoMove()
    {
        var env = Environment.GetEnvironmentVariable("DEEZSPOTAG_DISABLE_AUTOMOVE");
        if (!string.IsNullOrWhiteSpace(env) && bool.TryParse(env, out var value))
        {
            return value;
        }
        return false;
    }

    private static (IReadOnlyCollection<string> TaggedFiles, IReadOnlyCollection<string> FailedFiles) BuildMoveFileSets(
        IDictionary<string, FileTagOutcome> fileOutcomes)
    {
        if (fileOutcomes.Count == 0)
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var tagged = new List<string>(fileOutcomes.Count);
        var failed = new List<string>(fileOutcomes.Count);

        foreach (var pair in fileOutcomes)
        {
            var outcome = pair.Value;
            if (!outcome.Seen)
            {
                continue;
            }

            if (outcome.Tagged || outcome.CompletedWithoutChanges)
            {
                tagged.Add(pair.Key);
                continue;
            }

            failed.Add(pair.Key);
        }

        return (tagged, failed);
    }
}
