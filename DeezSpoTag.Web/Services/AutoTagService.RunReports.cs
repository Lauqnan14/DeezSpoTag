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

    private static Dictionary<string, string> ComputeRetainedSources(
        AutoTagTagSnapshot? baseline,
        AutoTagTagSnapshot finalSnapshot,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed)
    {
        var retained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRetainedMetaSources(retained, baseline, finalSnapshot, completed);
        AddRetainedTagSources(retained, baseline, finalSnapshot, completed);
        return retained;
    }

    private static void AddRetainedMetaSources(
        Dictionary<string, string> retained,
        AutoTagTagSnapshot? baseline,
        AutoTagTagSnapshot finalSnapshot,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed)
    {
        foreach (var metaKey in DiffMetaKeys)
        {
            var source = ResolveValueSource(
                GetMetaFieldValue(finalSnapshot, metaKey),
                baseline is null ? null : GetMetaFieldValue(baseline, metaKey),
                completed,
                step => GetMetaFieldValue(step.After, metaKey),
                step => GetMetaFieldValue(step.Before, metaKey));
            if (!string.IsNullOrWhiteSpace(source))
            {
                retained[metaKey] = source;
            }
        }
    }

    private static void AddRetainedTagSources(
        Dictionary<string, string> retained,
        AutoTagTagSnapshot? baseline,
        AutoTagTagSnapshot finalSnapshot,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed)
    {
        var finalTags = finalSnapshot.Tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in finalTags.Keys)
        {
            finalTags.TryGetValue(key, out var finalTagValue);
            var baselineTagValue = baseline?.Tags != null && baseline.Tags.TryGetValue(key, out var value)
                ? value
                : null;
            var source = AutoTagIdentityTags.ResolveOwningProvider(key)
                ?? ResolveValueSource(
                    finalTagValue,
                    baselineTagValue,
                    completed,
                    step => step.After != null && step.After.Tags.TryGetValue(key, out var stepValue) ? stepValue : null,
                    step => step.Before != null && step.Before.Tags.TryGetValue(key, out var stepValue) ? stepValue : null);
            if (!string.IsNullOrWhiteSpace(source))
            {
                retained[$"tag:{key.ToLowerInvariant()}"] = source;
            }
        }
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

    private void SaveRunSummary(AutoTagJob job)
    {
        try
        {
            var archiveLock = _archiveLocks.GetOrAdd(job.Id, static _ => new object());
            lock (archiveLock)
            {
                Directory.CreateDirectory(GetRunHistoryDirectory(job.Id));
                var summary = BuildRunSummary(job);
                File.WriteAllText(
                    GetRunSummaryPath(job.Id),
                    JsonSerializer.Serialize(summary, _jsonOptions),
                    new UTF8Encoding(false));
                UpdateRunIndex(summary);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to save AutoTag run summary for {JobId}", job.Id);
            }
        }
    }

    private AutoTagRunSummary BuildRunSummary(AutoTagJob job)
    {
        return new AutoTagRunSummary
        {
            Id = job.Id,
            Status = job.Status,
            StartedAt = job.StartedAt,
            FinishedAt = job.FinishedAt,
            ExitCode = job.ExitCode,
            Error = job.Error,
            Progress = job.Progress,
            OkCount = job.OkCount,
            ErrorCount = job.ErrorCount,
            ReviewCount = job.ReviewCount,
            SkippedCount = job.SkippedCount,
            RootPath = job.RootPath,
            Trigger = string.IsNullOrWhiteSpace(job.Trigger) ? AutoTagLiterals.ManualTrigger : job.Trigger,
            RunIntent = NormalizeRunIntent(job.RunIntent),
            ProfileId = job.ProfileId,
            ProfileName = job.ProfileName,
            EnhancementFeature = job.EnhancementFeature,
            SelectedEnhancementFeatures = job.SelectedEnhancementFeatures.ToList(),
            FolderUniformityRunMode = job.FolderUniformityRunMode,
            EnhancementGroupId = job.EnhancementGroupId,
            CurrentPhase = job.CurrentPhase,
            CurrentBatch = job.CurrentBatch,
            BatchCount = job.BatchCount,
            BatchProcessed = job.BatchProcessed,
            BatchSize = job.BatchSize,
            ProcessedItems = job.ProcessedItems,
            TotalItems = job.TotalItems,
            TargetReason = job.TargetReason,
            TargetRequested = job.TargetRequested,
            TargetUsable = job.TargetUsable,
            EnhancementFoundCount = job.EnhancementFoundCount,
            EnhancementGapFilledCount = job.EnhancementGapFilledCount,
            EnhancementSidecarredCount = job.EnhancementSidecarredCount,
            EnhancementTidiedCount = job.EnhancementTidiedCount,
            EnhancementManifestPath = job.EnhancementManifestPath,
            AutoMoveSummary = job.AutoMoveSummary?.Clone(),
            ResumeFromJobId = string.IsNullOrWhiteSpace(job.ResumeFromJobId) ? null : job.ResumeFromJobId,
            HistoryDate = ResolveRunHistoryDate(job),
            LogCount = GetArchivedLogCount(job.Id, job.Logs.Count),
            StatusEntryCount = GetArchivedStatusCount(job.Id, job.StatusHistory.Count)
        };
    }

    private AutoTagRunSummary? LoadRunSummary(string jobId)
    {
        try
        {
            var path = ResolveRunFilePath(jobId, "summary.json");
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return LoadRunSummaryFromPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to load AutoTag run summary for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return null;
        }
    }

    private AutoTagRunSummary? LoadRunSummaryFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        var summary = JsonSerializer.Deserialize<AutoTagRunSummary>(json, _jsonOptions);
        if (summary == null)
        {
            return null;
        }

        // Archived run summaries are immutable history: statuses/errors are never rewritten on load.
        if (string.IsNullOrWhiteSpace(summary.ResumeFromJobId))
        {
            summary.ResumeFromJobId = TryReadJobResumeFromJobId(summary.Id);
        }

        return summary;
    }

    private string GetRunSummaryPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "summary.json");
}
