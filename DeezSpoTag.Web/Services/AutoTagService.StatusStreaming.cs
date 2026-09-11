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

    private void UpdateStatus(
        AutoTagJob job,
        TaggingStatusWrap status,
        string stageName,
        string stageConfigHash,
        int stageIndex,
        int stageCount,
        IDictionary<string, FileTagOutcome>? fileOutcomes)
    {
        if (status.Status == null)
        {
            return;
        }

        job.LastStatus = status;
        job.Progress = ScaleProgress(status.Progress, stageIndex, stageCount);
        job.CurrentPlatform = status.Platform;
        if ((IsEnhancementRunIntent(job.RunIntent)
             || IsManualEnrichmentRunIntent(job.RunIntent))
            && (string.Equals(stageName, AutoTagLiterals.EnhancementStage, StringComparison.OrdinalIgnoreCase)
                || string.Equals(stageName, AutoTagLiterals.EnrichmentStage, StringComparison.OrdinalIgnoreCase))
            && status.FileCount is > 0)
        {
            var fileIndex = Math.Clamp(status.FileIndex ?? 0, 0, status.FileCount.Value - 1);
            job.CurrentPhase = string.IsNullOrWhiteSpace(job.EnhancementFeature)
                ? AutoTagLiterals.EnhancementFeatureGapFill
                : job.EnhancementFeature;
            job.TotalItems = job.TargetUsable > 0 ? job.TargetUsable : status.FileCount.Value;
            job.ProcessedItems = Math.Min(job.TotalItems, fileIndex + 1);
            if (status.BatchNumber is int batchNumber
                && status.BatchCount is int batchCount
                && status.BatchSize is int batchSize
                && status.BatchProcessed is int batchProcessed)
            {
                // The runner reports the true album-boundary batch position.
                job.CurrentBatch = batchNumber;
                job.BatchCount = batchCount;
                job.BatchSize = batchSize;
                job.BatchProcessed = batchProcessed;
            }
            else
            {
                // Fixed-window fallback for runs that are not album-batched.
                job.BatchSize = Math.Min(EnhancementBatchSize, status.FileCount.Value - (fileIndex / EnhancementBatchSize * EnhancementBatchSize));
                job.CurrentBatch = fileIndex / EnhancementBatchSize + 1;
                job.BatchCount = (int)Math.Ceiling(status.FileCount.Value / (double)EnhancementBatchSize);
                job.BatchProcessed = fileIndex % EnhancementBatchSize + 1;
            }
        }
        TryCaptureTagDiff(job, status);
        ApplyIdentityReviewGuard(job, status);
        RouteReviewFileIfNeeded(job, status);
        AppendStatusHistory(job, status);
        TrackFileOutcome(fileOutcomes, status);
        TrackEnhancedFilePath(job, stageName, status);
        switch (status.Status.Status)
        {
            case AutoTagLiterals.OkStatus:
            case AutoTagLiterals.TaggedStatus:
                job.OkCount += 1;
                break;
            case AutoTagLiterals.ErrorStatus:
                job.ErrorCount += 1;
                break;
            case AutoTagLiterals.ReviewStatus:
                job.ReviewCount += 1;
                break;
            case AutoTagLiterals.SkippedStatus:
                job.SkippedCount += 1;
                break;
        }
        var checkpointChanged = TryUpdateResumeCheckpoint(job, stageName, stageConfigHash, status);
        // A checkpoint change must reach disk immediately (it is the resume anchor);
        // routine counter/progress updates may share the throttle window.
        SaveJobThrottled(job, force: checkpointChanged);
    }

    private static void TrackEnhancedFilePath(AutoTagJob job, string stageName, TaggingStatusWrap status)
    {
        var statusValue = status.Status;
        if (!IsEnhancementRunIntent(job.RunIntent)
            || !string.Equals(stageName, AutoTagLiterals.EnhancementStage, StringComparison.OrdinalIgnoreCase)
            || !IsSuccessfulTagStatus(statusValue?.Status)
            || string.IsNullOrWhiteSpace(statusValue?.Path))
        {
            return;
        }

        var normalizedPath = NormalizePathForJob(statusValue.Path);
        if (job.EnhancedFilePaths.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        job.EnhancedFilePaths.Add(normalizedPath);
    }


    private void RouteReviewFileIfNeeded(AutoTagJob job, TaggingStatusWrap status)
    {
        var statusValue = status.Status;
        if (statusValue == null
            || !string.Equals(statusValue.Status, AutoTagLiterals.ReviewStatus, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(statusValue.Path))
        {
            return;
        }

        var isManualEnrichment = IsManualEnrichmentRunIntent(job.RunIntent);
        if (!isManualEnrichment
            && !string.Equals(status.Platform, "shazam", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (!isManualEnrichment && !HasShazamReviewCandidate(statusValue))
        {
            AppendLog(
                job,
                $"review folder: ignored Shazam review status without candidate metadata for {statusValue.Path}; file remains in place.");
            return;
        }

        var settings = _settingsService.LoadSettings();
        var reviewFolder = settings.ReviewFolderPath?.Trim();
        if (string.IsNullOrWhiteSpace(reviewFolder))
        {
            PauseForMissingReviewFolder(
                job,
                "Review folder is not configured. Configure it in Settings > Download Path and mount it in Docker before Shazam review handling can continue.");
        }

        var reviewRoot = ResolveReviewFolderIoPath(reviewFolder!);
        if (!IsReviewFolderWritable(reviewRoot, out var validationError))
        {
            PauseForMissingReviewFolder(
                job,
                $"Review folder is not writable or not mounted: {validationError}. Configure it in Settings > Download Path.");
        }

        var sourcePath = DownloadPathResolver.ResolveIoPath(statusValue.Path);
        if (!File.Exists(sourcePath))
        {
            PauseForMissingReviewFolder(
                job,
                $"Shazam flagged a file for review, but the source file no longer exists: {statusValue.Path}");
        }

        var destinationPath = ResolveReviewDestinationPath(sourcePath, reviewRoot, settings.DownloadLocation);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Move(sourcePath, destinationPath);

        var reportPath = Path.ChangeExtension(destinationPath, ".review.txt");
        File.WriteAllText(reportPath, BuildReviewReport(job, statusValue, sourcePath, destinationPath), new UTF8Encoding(false));
        statusValue.ReviewDestinationPath = destinationPath;
        statusValue.ReviewReportPath = reportPath;
        AppendLog(job, $"review folder: moved Shazam-flagged file to {destinationPath}");
    }

    private void PauseForMissingReviewFolder(AutoTagJob job, string message)
    {
        AppendLog(job, message);
        throw new AutoTagRunPausedException(message);
    }

    private void NotifyDownloadToast(string message, string type)
    {
        try
        {
            _downloadEvents.Send("toastNotification", new
            {
                message,
                type,
                action = new
                {
                    label = "Settings",
                    href = "/Settings#download-path-settings"
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to publish download toast notification.");
        }
    }

    private static string ResolveReviewFolderIoPath(string reviewFolder)
        => DownloadPathResolver.ResolveIoPath(reviewFolder.Trim());

    private static bool IsReviewFolderWritable(string reviewRoot, out string error)
    {
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(reviewRoot))
            {
                error = "path is empty";
                return false;
            }

            Directory.CreateDirectory(reviewRoot);
            var probePath = Path.Join(reviewRoot, $".deezspotag-review-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "ok", new UTF8Encoding(false));
            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool HasShazamReviewCandidate(TaggingStatus status)
    {
        return !string.IsNullOrWhiteSpace(status.CandidateTitle)
            || !string.IsNullOrWhiteSpace(status.CandidateArtist)
            || !string.IsNullOrWhiteSpace(status.CandidateIsrc)
            || status.CandidateDurationSeconds.HasValue;
    }

    private static string ResolveReviewDestinationPath(string sourcePath, string reviewRoot, string downloadLocation)
    {
        var relativePath = Path.GetFileName(sourcePath);
        try
        {
            var downloadRoot = DownloadPathResolver.ResolveIoPath(downloadLocation);
            if (!string.IsNullOrWhiteSpace(downloadRoot)
                && IsPathUnderRoot(sourcePath, downloadRoot))
            {
                relativePath = Path.GetRelativePath(downloadRoot, sourcePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            relativePath = Path.GetFileName(sourcePath);
        }

        var destinationPath = Path.GetFullPath(Path.Join(reviewRoot, relativePath));
        var reviewRootFull = Path.GetFullPath(reviewRoot);
        if (!IsPathUnderRoot(destinationPath, reviewRootFull))
        {
            destinationPath = Path.Join(reviewRootFull, Path.GetFileName(sourcePath));
        }

        return GetAvailableReviewPath(destinationPath);
    }

    private static string GetAvailableReviewPath(string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            return destinationPath;
        }

        var directory = Path.GetDirectoryName(destinationPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(destinationPath);
        var extension = Path.GetExtension(destinationPath);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Join(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Join(directory, $"{name} ({Guid.NewGuid():N}){extension}");
    }

    private static string BuildReviewReport(
        AutoTagJob job,
        TaggingStatus status,
        string sourcePath,
        string destinationPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("DeezSpoTag AutoTag Review");
        builder.AppendLine($"Timestamp: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"Job Id: {job.Id}");
        builder.AppendLine($"Reason: {FirstNonEmpty(status.ReviewReason, status.Message, "Shazam flagged this file for review.")}");
        builder.AppendLine($"Original path: {sourcePath}");
        builder.AppendLine($"Review path: {destinationPath}");
        builder.AppendLine();
        builder.AppendLine("Source");
        builder.AppendLine($"Title: {status.SourceTitle ?? ""}");
        builder.AppendLine($"Artist: {status.SourceArtist ?? ""}");
        builder.AppendLine($"ISRC: {status.SourceIsrc ?? ""}");
        builder.AppendLine($"Duration seconds: {FormatNullableDouble(status.SourceDurationSeconds)}");
        builder.AppendLine();
        builder.AppendLine("Shazam candidate");
        builder.AppendLine($"Title: {status.CandidateTitle ?? ""}");
        builder.AppendLine($"Artist: {status.CandidateArtist ?? ""}");
        builder.AppendLine($"ISRC: {status.CandidateIsrc ?? ""}");
        builder.AppendLine($"Duration seconds: {FormatNullableDouble(status.CandidateDurationSeconds)}");
        return builder.ToString();
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FormatNullableDouble(double? value)
        => value.HasValue ? value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    private static bool IsTerminalStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.OkStatus => true,
            AutoTagLiterals.TaggedStatus => true,
            AutoTagLiterals.ReviewStatus => true,
            AutoTagLiterals.ErrorStatus => true,
            AutoTagLiterals.SkippedStatus => true,
            _ => false
        };
    }

    private static bool TryUpdateResumeCheckpoint(
        AutoTagJob job,
        string stageName,
        string stageConfigHash,
        TaggingStatusWrap status)
    {
        if (!IsTerminalStatus(status.Status?.Status))
        {
            return false;
        }

        var nextPlatformIndex = status.NextPlatformIndex;
        var nextFileIndex = status.NextFileIndex;
        if (nextPlatformIndex is not int
            || nextFileIndex is not int
            || status.PlatformCount is not int platformCount
            || status.FileCount is not int fileCount
            || platformCount <= 0
            || fileCount <= 0)
        {
            // Fallback: some terminal statuses (workflow tails, batch boundaries) omit the
            // next-indexes. If the current indexes are known, advance by one so every
            // successfully processed file still advances the checkpoint (no silent skips).
            if (status.PlatformIndex is not int currentPlatform
                || status.FileIndex is not int currentFile)
            {
                return false;
            }

            platformCount = Math.Max(1, status.PlatformCount ?? 0);
            fileCount = Math.Max(1, status.FileCount ?? 0);
            nextPlatformIndex = currentPlatform;
            nextFileIndex = currentFile + 1;
            if (nextFileIndex >= fileCount)
            {
                nextFileIndex = 0;
                nextPlatformIndex = Math.Min(platformCount, currentPlatform + 1);
            }
        }

        job.ResumeCheckpoint = new AutoTagResumeCheckpoint
        {
            StageName = stageName,
            StageConfigHash = stageConfigHash,
            PlatformIndex = Math.Max(0, nextPlatformIndex ?? 0),
            FileIndex = Math.Max(0, nextFileIndex ?? 0),
            PlatformCount = platformCount,
            FileCount = fileCount,
            LastPath = status.Status?.Path,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return true;
    }

    private static AutoTagResumeCursor? ResolveResumeCursor(AutoTagJob job, AutoTagStageConfig stage)
    {
        if (!CanApplyResumeCheckpoint(job.ResumeCheckpoint, stage))
        {
            return null;
        }

        var checkpoint = job.ResumeCheckpoint!;
        return new AutoTagResumeCursor(
            Math.Max(0, checkpoint.PlatformIndex),
            Math.Max(0, checkpoint.FileIndex),
            checkpoint.PlatformCount,
            checkpoint.FileCount,
            checkpoint.LastPath);
    }

    private static bool CanApplyResumeCheckpoint(AutoTagResumeCheckpoint? checkpoint, AutoTagStageConfig stage)
    {
        if (checkpoint == null)
        {
            return false;
        }

        if (checkpoint.PlatformCount <= 0 || checkpoint.FileCount <= 0)
        {
            return false;
        }

        if (checkpoint.PlatformIndex < 0
            || checkpoint.FileIndex < 0
            || checkpoint.PlatformIndex >= checkpoint.PlatformCount
            || checkpoint.FileIndex > checkpoint.FileCount)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(checkpoint.StageName))
        {
            return false;
        }

        if (!string.Equals(checkpoint.StageName, stage.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Enhancement/gap-fill stage JSON is rebuilt on resume (platform auth,
        // eligibility, sanitization). A hash drift must not discard the cursor —
        // LastPath still identifies the file that finished.
        if (string.Equals(stage.Name, AutoTagLiterals.EnhancementStage, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(checkpoint.StageConfigHash))
        {
            return false;
        }

        return string.Equals(checkpoint.StageConfigHash, stage.ConfigHash, StringComparison.OrdinalIgnoreCase);
    }

    private void TryCaptureTagDiff(AutoTagJob job, TaggingStatusWrap status)
    {
        if (!TryResolveCaptureMode(status, out var normalizedStatus, out var captureBefore, out var captureAfter))
        {
            return;
        }

        var normalizedPath = NormalizeDiffPath(status.Status!.Path!);
        if (!TryBuildDiffSnapshot(normalizedPath, out var snapshot) || snapshot == null)
        {
            return;
        }

        lock (job.TagDiffs)
        {
            var diff = GetOrCreateTagDiff(job.TagDiffs, normalizedPath);
            var platformDiff = GetOrCreatePlatformDiff(diff, status.Platform, normalizedStatus, captureBefore, captureAfter);
            ApplyCapturedDiffSnapshot(diff, platformDiff, snapshot, status.Platform, normalizedStatus, captureBefore, captureAfter);
            SaveTagDiffCheckpoint(job.Id, normalizedPath, diff);
        }
    }

    private static void ApplyIdentityReviewGuard(AutoTagJob job, TaggingStatusWrap status)
    {
        if (status.Status == null
            || string.IsNullOrWhiteSpace(status.Status.Path)
            || !IsSuccessfulTagStatus(status.Status.Status))
        {
            return;
        }

        var normalizedPath = NormalizeDiffPath(status.Status.Path);
        AutoTagTagDiff? diff;
        lock (job.TagDiffs)
        {
            job.TagDiffs.TryGetValue(normalizedPath, out diff);
        }

        var reviewReason = EvaluateIdentityReviewGuard(diff);
        if (string.IsNullOrWhiteSpace(reviewReason))
        {
            return;
        }

        status.Status.Status = AutoTagLiterals.ReviewStatus;
        status.Status.Message = reviewReason;
    }

    private static bool IsSuccessfulTagStatus(string? status)
    {
        return string.Equals(status, AutoTagLiterals.OkStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.TaggedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static string? EvaluateIdentityReviewGuard(AutoTagTagDiff? diff)
    {
        if (diff?.Before == null || diff.After == null)
        {
            return null;
        }

        var beforeTitle = GetMetaFieldValue(diff.Before, AutoTagTitleKey);
        var afterTitle = GetMetaFieldValue(diff.After, AutoTagTitleKey);
        var beforeArtists = GetMetaFieldValue(diff.Before, AutoTagArtistsKey);
        var afterArtists = GetMetaFieldValue(diff.After, AutoTagArtistsKey);
        var beforeAlbumArtists = GetMetaFieldValue(diff.Before, "albumArtists");
        var afterAlbumArtists = GetMetaFieldValue(diff.After, "albumArtists");

        var artistSimilarity = Math.Max(
            ComputeIdentitySimilarity(beforeArtists, afterArtists),
            ComputeIdentitySimilarity(beforeAlbumArtists, afterAlbumArtists));
        var artistChanged = HasMeaningfulIdentityChange(beforeArtists, afterArtists)
            || HasMeaningfulIdentityChange(beforeAlbumArtists, afterAlbumArtists);
        if (artistChanged && artistSimilarity < IdentityReviewArtistSimilarityThreshold)
        {
            return $"requires user review: artist identity changed sharply (artist similarity {artistSimilarity:0.000})";
        }

        if (!HasMeaningfulIdentityChange(beforeTitle, afterTitle))
        {
            return null;
        }

        var titleSimilarity = ComputeIdentitySimilarity(beforeTitle, afterTitle);
        if (titleSimilarity >= IdentityReviewTitleSimilarityThreshold || !artistChanged)
        {
            return null;
        }

        return $"requires user review: identity changed sharply (title similarity {titleSimilarity:0.000}, artist similarity {artistSimilarity:0.000})";
    }

    private static bool HasMeaningfulIdentityChange(object? before, object? after)
    {
        var normalizedBefore = NormalizeCompareValue(before);
        var normalizedAfter = NormalizeCompareValue(after);
        return !string.IsNullOrWhiteSpace(normalizedBefore)
            && !string.IsNullOrWhiteSpace(normalizedAfter)
            && !string.Equals(normalizedBefore, normalizedAfter, StringComparison.Ordinal);
    }

    private static double ComputeIdentitySimilarity(object? before, object? after)
    {
        var normalizedBefore = AutoTagSimilarity.NormalizeText(NormalizeCompareValue(before));
        var normalizedAfter = AutoTagSimilarity.NormalizeText(NormalizeCompareValue(after));
        return AutoTagSimilarity.ComputeScore(normalizedBefore, normalizedAfter);
    }

    private static bool TryResolveCaptureMode(
        TaggingStatusWrap status,
        out string normalizedStatus,
        out bool captureBefore,
        out bool captureAfter)
    {
        normalizedStatus = string.Empty;
        captureBefore = false;
        captureAfter = false;
        if (status.Status == null || string.IsNullOrWhiteSpace(status.Status.Path))
        {
            return false;
        }

        var statusValue = status.Status.Status?.Trim() ?? string.Empty;
        normalizedStatus = statusValue.ToLowerInvariant();
        var message = status.Status.Message ?? string.Empty;
        var isAlreadyTagged = normalizedStatus == AutoTagLiterals.SkippedStatus
            && message.Contains("already tagged", StringComparison.OrdinalIgnoreCase);
        captureBefore = normalizedStatus == AutoTagLiterals.TaggingStatus || isAlreadyTagged;
        captureAfter = normalizedStatus is AutoTagLiterals.TaggedStatus or AutoTagLiterals.OkStatus || isAlreadyTagged;
        return captureBefore || captureAfter;
    }

    private bool TryBuildDiffSnapshot(string normalizedPath, out AutoTagTagSnapshot? snapshot)
    {
        try
        {
            snapshot = BuildTagSnapshot(normalizedPath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "AutoTag diff snapshot failed for {Path}", normalizedPath);
            }
            snapshot = null;
            return false;
        }
    }

    private static AutoTagTagDiff GetOrCreateTagDiff(Dictionary<string, AutoTagTagDiff> diffs, string normalizedPath)
    {
        if (diffs.TryGetValue(normalizedPath, out var existing))
        {
            existing.PlatformDiffs ??= new List<AutoTagPlatformDiffSnapshot>();
            return existing;
        }

        var created = new AutoTagTagDiff
        {
            Path = normalizedPath,
            PlatformDiffs = new List<AutoTagPlatformDiffSnapshot>()
        };
        diffs[normalizedPath] = created;
        return created;
    }

    private static AutoTagPlatformDiffSnapshot? GetOrCreatePlatformDiff(
        AutoTagTagDiff diff,
        string? platform,
        string normalizedStatus,
        bool captureBefore,
        bool captureAfter)
    {
        if (captureBefore)
        {
            var beforeStep = new AutoTagPlatformDiffSnapshot
            {
                Platform = platform ?? string.Empty,
                Status = normalizedStatus,
                CapturedAt = DateTimeOffset.UtcNow
            };
            diff.PlatformDiffs.Add(beforeStep);
            return beforeStep;
        }

        if (!captureAfter)
        {
            return null;
        }

        var existingAfter = diff.PlatformDiffs.LastOrDefault(step =>
            string.Equals(step.Platform, platform, StringComparison.OrdinalIgnoreCase)
            && step.After == null);
        if (existingAfter != null)
        {
            return existingAfter;
        }

        var createdAfter = new AutoTagPlatformDiffSnapshot
        {
            Platform = platform ?? string.Empty,
            Status = normalizedStatus,
            CapturedAt = DateTimeOffset.UtcNow
        };
        diff.PlatformDiffs.Add(createdAfter);
        return createdAfter;
    }

    private static void ApplyCapturedDiffSnapshot(
        AutoTagTagDiff diff,
        AutoTagPlatformDiffSnapshot? platformDiff,
        AutoTagTagSnapshot snapshot,
        string? platform,
        string normalizedStatus,
        bool captureBefore,
        bool captureAfter)
    {
        if (captureBefore && diff.Before == null)
        {
            diff.Before = snapshot;
        }

        if (captureBefore && platformDiff != null && platformDiff.Before == null)
        {
            platformDiff.Before = snapshot;
        }

        if (!captureAfter)
        {
            return;
        }

        diff.After = snapshot;
        diff.LastPlatform = platform;
        if (platformDiff == null)
        {
            return;
        }

        platformDiff.After = snapshot;
        platformDiff.Status = normalizedStatus;
        platformDiff.CapturedAt = DateTimeOffset.UtcNow;
    }

    private AutoTagTagSnapshot BuildTagSnapshot(string path)
    {
        var dump = _quickTagService.Dump(path, includeArtworkData: false, enforceLibraryPathCheck: false);
        return new AutoTagTagSnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Meta = dump.Meta,
            Tags = CloneTags(dump.Tags)
        };
    }

    private static Dictionary<string, List<string>> CloneTags(Dictionary<string, List<string>> tags)
    {
        var clone = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in tags)
        {
            if (BinaryArtworkTagKeys.Contains(key))
            {
                continue;
            }
            clone[key] = values?.ToList() ?? new List<string>();
        }
        return clone;
    }

    private static string NormalizeDiffPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return path;
        }
    }

    private static void TrackFileOutcome(IDictionary<string, FileTagOutcome>? fileOutcomes, TaggingStatusWrap status)
    {
        if (fileOutcomes == null || status.Status == null || string.IsNullOrWhiteSpace(status.Status.Path))
        {
            return;
        }

        var terminalStatus = status.Status.Status;
        if (terminalStatus is not AutoTagLiterals.OkStatus
            and not AutoTagLiterals.TaggedStatus
            and not AutoTagLiterals.SkippedStatus
            and not AutoTagLiterals.ErrorStatus
            and not AutoTagLiterals.ReviewStatus)
        {
            return;
        }

        var filePath = status.Status.Path;
        try
        {
            filePath = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep raw path if canonicalization fails.
        }

        if (!fileOutcomes.TryGetValue(filePath, out var outcome))
        {
            outcome = new FileTagOutcome();
            fileOutcomes[filePath] = outcome;
        }

        outcome.Seen = true;
        switch (status.Status.Status)
        {
            case AutoTagLiterals.OkStatus:
            case AutoTagLiterals.TaggedStatus:
                outcome.Tagged = true;
                break;
            case AutoTagLiterals.SkippedStatus:
                if ((!string.IsNullOrWhiteSpace(status.Status.Message)
                     && status.Status.Message.Contains("already tagged", StringComparison.OrdinalIgnoreCase))
                    || string.Equals(status.Status.Outcome, "no_eligible_tags", StringComparison.Ordinal))
                {
                    outcome.CompletedWithoutChanges = true;
                }
                break;
        }
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

    private static double ScaleProgress(double progress, int stageIndex, int stageCount)
    {
        if (stageCount <= 1)
        {
            return progress;
        }

        var clamped = progress;
        if (clamped < 0)
        {
            clamped = 0;
        }
        else if (clamped > 1)
        {
            clamped = 1;
        }

        var idx = stageIndex < 0 ? 0 : stageIndex;
        if (idx >= stageCount)
        {
            idx = stageCount - 1;
        }

        return (idx + clamped) / stageCount;
    }

    private static string BuildStageStartedLog(AutoTagStageConfig stage, int stageIndex, int stageCount)
    {
        _ = stageIndex;
        _ = stageCount;
        var name = FormatStageName(stage.Name);
        return $"{name} tagging started ({stage.TagCount} tags)";
    }

    private static string BuildStageFinishedLog(AutoTagStageConfig stage, int stageIndex, int stageCount)
    {
        _ = stageIndex;
        _ = stageCount;
        var name = FormatStageName(stage.Name);
        return $"{name} tagging finished";
    }

    private static string FormatStageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Stage";
        }

        var trimmed = name.Trim();
        if (trimmed.Length == 1)
        {
            return trimmed.ToUpperInvariant();
        }

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private void AppendStatusHistory(AutoTagJob job, TaggingStatusWrap status)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var snapshot = new TaggingStatusSnapshot
        {
            Timestamp = timestamp,
            Status = status
        };
        job.LastActivityAt = timestamp;
        lock (job.StatusHistory)
        {
            job.StatusHistory.Add(snapshot);
            if (job.StatusHistory.Count > 300)
            {
                job.StatusHistory.RemoveRange(0, job.StatusHistory.Count - 300);
            }
        }
        AppendArchivedStatus(job.Id, snapshot);
    }

    private void AppendLog(AutoTagJob job, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var cleaned = AnsiRegex.Replace(line, string.Empty);
        job.LastActivityAt = DateTimeOffset.UtcNow;
        TrackStartedPlatform(job, cleaned);
        lock (job.Logs)
        {
            job.Logs.Add(cleaned);
            if (job.Logs.Count > 200)
            {
                job.Logs.RemoveRange(0, job.Logs.Count - 200);
            }
        }
        AppendActivityLog(job.Id, cleaned);
        AppendArchivedLog(job.Id, cleaned);
        SaveJobThrottled(job);
    }

    private void AppendActivityLog(string jobId, string line)
    {
        try
        {
            var level = ResolveLogLevel(line);
            var cleaned = AnsiRegex.Replace(line, string.Empty).Trim();
            cleaned = StripLinePrefix(cleaned);
            if (string.IsNullOrEmpty(cleaned))
            {
                return;
            }
            if (_lastActivityLines.TryGetValue(jobId, out var lastLine) &&
                string.Equals(lastLine, cleaned, StringComparison.Ordinal))
            {
                return;
            }
            _lastActivityLines[jobId] = cleaned;
            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                level,
                $"[autotag] {cleaned}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to add AutoTag line to activity logs.");
        }
    }

    private void TrackStartedPlatform(AutoTagJob job, string line)
    {
        var cleaned = AnsiRegex.Replace(line, string.Empty).Trim();
        cleaned = StripLinePrefix(cleaned);
        if (!TryExtractStartedPlatform(cleaned, out var platform))
        {
            return;
        }

        lock (job.Logs)
        {
            if (!job.StartedPlatforms.Any(p => string.Equals(p, platform, StringComparison.OrdinalIgnoreCase)))
            {
                job.StartedPlatforms.Add(platform);
                SaveJob(job);
            }
        }
    }

    private void AppendPlatformSummary(AutoTagJob job)
    {
        if (job.StartedPlatforms.Count == 0)
        {
            return;
        }

        var summary = $"{AutoTagProtocol.LogMarker} platforms started: {string.Join(", ", job.StartedPlatforms)}";
        AppendActivityLog(job.Id, summary);
    }

    private static bool TryExtractStartedPlatform(string line, out string platform)
    {
        platform = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        const string marker = AutoTagProtocol.LogMarker;
        var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var message = line[(markerIndex + marker.Length)..].TrimStart();
        const string starting = AutoTagProtocol.StartingPlatformMessage;
        if (!message.StartsWith(starting, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = message[starting.Length..].Trim();
        if (rest.StartsWith("tagger", StringComparison.OrdinalIgnoreCase) ||
            rest.StartsWith("tagging", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rest))
        {
            return false;
        }

        platform = rest;
        return true;
    }

    private static string ResolveLogLevel(string line)
    {
        var upper = line.ToUpperInvariant();
        if (upper.Contains("[ERROR]") || upper.Contains(" ERROR "))
        {
            return "error";
        }
        if (upper.Contains("[WARN]") || upper.Contains("[WARNING]") || upper.Contains(" WARN "))
        {
            return "warning";
        }
        if (upper.Contains("[DEBUG]") || upper.Contains(" DEBUG "))
        {
            return "debug";
        }
        return "info";
    }

    private static string StripLinePrefix(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var cleaned = line.Trim();
        var timestampEnd = cleaned.IndexOf(']');
        if (cleaned.StartsWith('[') && timestampEnd > 0)
        {
            cleaned = cleaned[(timestampEnd + 1)..].TrimStart();
        }

        if (cleaned.StartsWith('['))
        {
            var levelEnd = cleaned.IndexOf(']');
            if (levelEnd > 0)
            {
                cleaned = cleaned[(levelEnd + 1)..].TrimStart();
            }
        }

        return cleaned;
    }
}
