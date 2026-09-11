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

    private static string? ResolveValueSource<T>(
        T? finalValue,
        T? baselineValue,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector,
        Func<AutoTagPlatformDiffSnapshot, T?> beforeSelector)
    {
        var mergedSources = ResolveMergedValueSources(
            finalValue,
            baselineValue,
            completed,
            afterSelector,
            beforeSelector);
        if (!string.IsNullOrWhiteSpace(mergedSources))
        {
            return mergedSources;
        }

        var normalizedFinal = NormalizeCompareValue(finalValue);
        if (string.IsNullOrEmpty(normalizedFinal))
        {
            return null;
        }

        var normalizedBaseline = NormalizeCompareValue(baselineValue);
        var (currentValue, currentSource) = ResolveCurrentValueAndSource(completed, normalizedBaseline, afterSelector);

        if (string.Equals(currentValue, normalizedFinal, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(currentSource))
            {
                return currentSource;
            }

            return string.Equals(normalizedBaseline, normalizedFinal, StringComparison.Ordinal)
                ? "original"
                : null;
        }

        return ResolveFallbackTransitionSource(
            normalizedFinal,
            normalizedBaseline,
            completed,
            afterSelector,
            beforeSelector);
    }

    private static (string CurrentValue, string? CurrentSource) ResolveCurrentValueAndSource<T>(
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        string normalizedBaseline,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector)
    {
        var currentValue = normalizedBaseline;
        string? currentSource = null;

        foreach (var step in completed)
        {
            var stepAfter = NormalizeCompareValue(afterSelector(step));
            if (string.Equals(stepAfter, currentValue, StringComparison.Ordinal))
            {
                continue;
            }

            currentValue = stepAfter;
            if (!string.IsNullOrWhiteSpace(step.Platform))
            {
                currentSource = step.Platform;
            }
        }

        return (currentValue, currentSource);
    }

    private static string? ResolveFallbackTransitionSource<T>(
        string normalizedFinal,
        string normalizedBaseline,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector,
        Func<AutoTagPlatformDiffSnapshot, T?> beforeSelector)
    {
        foreach (var step in completed)
        {
            var after = NormalizeCompareValue(afterSelector(step));
            if (!string.Equals(after, normalizedFinal, StringComparison.Ordinal))
            {
                continue;
            }

            var before = NormalizeCompareValue(beforeSelector(step));
            var effectiveBefore = string.IsNullOrEmpty(before) ? normalizedBaseline : before;
            if (!string.Equals(effectiveBefore, normalizedFinal, StringComparison.Ordinal))
            {
                return step.Platform;
            }
        }

        return null;
    }

    private static string? ResolveMergedValueSources<T>(
        T? finalValue,
        T? baselineValue,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector,
        Func<AutoTagPlatformDiffSnapshot, T?> beforeSelector)
    {
        var finalParts = NormalizeCompareParts(finalValue);
        if (finalParts.Count <= 1)
        {
            return null;
        }

        var baselineParts = NormalizeCompareParts(baselineValue);
        var sources = new List<string>();
        foreach (var step in completed)
        {
            if (string.IsNullOrWhiteSpace(step.Platform))
            {
                continue;
            }

            var beforeParts = NormalizeCompareParts(beforeSelector(step));
            if (beforeParts.Count == 0)
            {
                beforeParts = baselineParts;
            }

            var afterParts = NormalizeCompareParts(afterSelector(step));
            if (afterParts.Count == 0)
            {
                continue;
            }

            var stepChanged = !string.Equals(
                NormalizeCompareValue(beforeSelector(step)),
                NormalizeCompareValue(afterSelector(step)),
                StringComparison.Ordinal);
            var retainedContribution = afterParts.Intersect(finalParts, StringComparer.Ordinal).Any();
            var introducedContribution = afterParts
                .Except(beforeParts, StringComparer.Ordinal)
                .Intersect(finalParts, StringComparer.Ordinal)
                .Any();
            var changedToFinalValue = string.Equals(
                NormalizeCompareValue(afterSelector(step)),
                NormalizeCompareValue(finalValue),
                StringComparison.Ordinal)
                && !string.Equals(
                    NormalizeCompareValue(beforeSelector(step)),
                    NormalizeCompareValue(finalValue),
                    StringComparison.Ordinal);

            if ((introducedContribution || changedToFinalValue || retainedContribution)
                && stepChanged
                && !sources.Contains(step.Platform, StringComparer.OrdinalIgnoreCase))
            {
                sources.Add(step.Platform);
            }
        }

        return sources.Count > 1 ? string.Join(", ", sources) : null;
    }

    private static object? GetMetaFieldValue(AutoTagTagSnapshot? snapshot, string key)
    {
        if (snapshot?.Meta == null || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var property = typeof(QuickTagDumpMeta).GetProperties()
            .FirstOrDefault(prop => string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase));
        return property?.GetValue(snapshot.Meta);
    }

    private static string ResolveStopStatus(AutoTagJob job, string stopReason)
    {
        if (!IsEnhancementRunIntent(job.RunIntent)
            && !IsManualEnrichmentRunIntent(job.RunIntent))
        {
            return AutoTagLiterals.CanceledStatus;
        }

        // Enhancement runs are never cancelled outright: every stop is a pause that the
        // explicit resume endpoint (POST /jobs/{id}/resume) can continue from.
        return string.Equals(stopReason, AutoTagLiterals.AutomationTrigger, StringComparison.OrdinalIgnoreCase)
            ? AutoTagLiterals.PausedStatus
            : string.Equals(stopReason, "user", StringComparison.OrdinalIgnoreCase)
                ? AutoTagLiterals.PausedStatus
                : AutoTagLiterals.InterruptedStatus;
    }

    private static bool IsTerminalStopStatus(string? status)
    {
        return string.Equals(status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadBoundedInt(JsonObject node, string propertyName, int fallback, int min, int max)
    {
        if (!node.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return Math.Clamp(intValue, min, max);
        }

        if (value.TryGetValue<string>(out var raw) && int.TryParse(raw, out var parsed))
        {
            return Math.Clamp(parsed, min, max);
        }

        return fallback;
    }

    private static int? ReadOptionalInt(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is null)
        {
            return null;
        }

        if (valueNode is JsonValue intNode && intNode.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (valueNode is JsonValue stringNode
            && stringNode.TryGetValue<string>(out var raw)
            && int.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static List<string> ResolveEnhancementRequestedTags(JsonObject baseRoot)
        => AutoTagPlatformTagContract.ResolveRequestedTags(baseRoot);

    private static List<string> ReadStringList(JsonObject root, string propertyName)
    {
        if (root[propertyName] is not JsonArray array)
        {
            return new List<string>();
        }

        return array
            .Select(item => item?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();
    }

    private static void WriteStringList(JsonObject root, string propertyName, IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            array.Add(value);
        }

        root[propertyName] = array;
    }

    private static bool? ReadBool(JsonObject? node, string propertyName)
    {
        if (node == null)
        {
            return null;
        }

        if (!node.TryGetPropertyValue(propertyName, out var value) || value is not JsonValue jsonValue)
        {
            return null;
        }

        return jsonValue.TryGetValue<bool>(out var parsed) ? parsed : null;
    }

    private static AutoTagOrganizerOptions LoadOrganizerOptions(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return new AutoTagOrganizerOptions();
            }

            var json = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AutoTagOrganizerOptions();
            }

            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null)
            {
                return new AutoTagOrganizerOptions();
            }

            var enhancementNode = node[AutoTagLiterals.EnhancementStage] as JsonObject;
            var folderUniformityNode = enhancementNode?["folderUniformity"] as JsonObject;
            var tagsNode = node["tags"] as JsonObject;
            var options = new AutoTagOrganizerOptions
            {
                OnlyMoveWhenTagged = ReadBool(folderUniformityNode, "onlyMoveWhenTagged") == true,
                MoveTaggedPath = node["moveSuccess"]?.GetValue<bool>() == true
                    ? node["moveSuccessPath"]?.GetValue<string>()
                    : null,
                MoveUntaggedPath = node["moveFailed"]?.GetValue<bool>() == true
                    ? node["moveFailedPath"]?.GetValue<string>()
                    : null,
                IncludeSubfolders = node[AutoTagLiterals.IncludeSubfoldersKey]?.GetValue<bool>() ?? true,
                MoveMisplacedFiles = ReadBool(folderUniformityNode, "moveMisplacedFiles") ?? true,
                RenameFilesToTemplate = ReadBool(folderUniformityNode, "renameFilesToTemplate") != false,
                RemoveEmptyFolders = ReadBool(folderUniformityNode, "removeEmptyFolders") != false,
                UsePrimaryArtistFoldersOverride =
                    tagsNode?["singleAlbumArtist"]?.GetValue<bool?>(),
                MultiArtistSeparatorOverride =
                    tagsNode?[AutoTagLiterals.MultiArtistSeparatorKey]?.GetValue<string>()
            };

            return options;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AutoTagOrganizerOptions();
        }
    }

    private async Task<AutoTagOrganizerOptions> LoadOrganizerOptionsAsync(AutoTagJob job, string configPath)
    {
        var options = LoadOrganizerOptions(configPath);
        await ApplyJobProfileOrganizerOverridesAsync(job, options, CancellationToken.None);
        return options;
    }

    private static void RedactSensitiveNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    foreach (var key in obj.Select(pair => pair.Key).ToList())
                    {
                        if (ShouldRedactConfigKey(key))
                        {
                            obj.Remove(key);
                            continue;
                        }

                        if (obj[key] is { } child)
                        {
                            RedactSensitiveNode(child);
                        }
                    }
                    break;
                }
            case JsonArray array:
                {
                    foreach (var item in array.Where(static item => item != null))
                    {
                        RedactSensitiveNode(item!);
                    }
                    break;
                }
        }
    }

    private static JsonObject GetOrCreateCustomNode(JsonObject node)
    {
        if (node[AutoTagLiterals.CustomKey] is JsonObject custom)
        {
            return custom;
        }

        custom = new JsonObject();
        node[AutoTagLiterals.CustomKey] = custom;
        return custom;
    }

    private static JsonObject GetOrCreatePlatformCustomNode(JsonObject custom, string platformId)
    {
        if (custom[platformId] is JsonObject platformCustom)
        {
            return platformCustom;
        }

        platformCustom = new JsonObject();
        custom[platformId] = platformCustom;
        return platformCustom;
    }

    private static bool IsAllowedEnhancementTrigger(string? trigger)
    {
        if (string.Equals(trigger, AutoTagLiterals.ManualTrigger, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, AutoTagLiterals.ScheduleTrigger, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The recovery trigger is only ever produced by resume paths (explicit resume
        // endpoint and stuck-job recovery). Without it, explicitly resuming an
        // enhancement job would be admitted as a blocked successor while the source
        // job was stamped resumed - reporting "running" while nothing runs.
        if (string.Equals(trigger, AutoTagLiterals.RecoveryTrigger, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void SetIfEmpty(JsonObject target, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (target.TryGetPropertyValue(key, out var existingNode)
            && existingNode is JsonValue existingValue
            && existingValue.TryGetValue<string>(out var existingText)
            && !string.IsNullOrWhiteSpace(existingText))
        {
            return;
        }

        target[key] = value.Trim();
    }

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

    private static bool IsSuccessfulTagStatus(string? status)
    {
        return string.Equals(status, AutoTagLiterals.OkStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.TaggedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMeaningfulIdentityChange(object? before, object? after)
    {
        var normalizedBefore = NormalizeCompareValue(before);
        var normalizedAfter = NormalizeCompareValue(after);
        return !string.IsNullOrWhiteSpace(normalizedBefore)
            && !string.IsNullOrWhiteSpace(normalizedAfter)
            && !string.Equals(normalizedBefore, normalizedAfter, StringComparison.Ordinal);
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

    private static void RecordMalformedHistoryEntry(ref int skippedMalformed)
        => skippedMalformed += 1;

    private static DateTimeOffset ResolveLastActivityTimestamp(AutoTagJob job)
    {
        var timestamp = job.StartedAt;
        if (job.ResumeCheckpoint?.UpdatedAt > timestamp)
        {
            timestamp = job.ResumeCheckpoint.UpdatedAt;
        }

        var latestStatusTimestamp = job.StatusHistory
            .Select(static entry => entry.Timestamp)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (latestStatusTimestamp > timestamp)
        {
            timestamp = latestStatusTimestamp;
        }

        if (job.FinishedAt > timestamp)
        {
            timestamp = job.FinishedAt.Value;
        }

        return timestamp;
    }

    private static DateTimeOffset GetLastProgressTimestamp(AutoTagJob job, string? jobPath = null)
    {
        var timestamp = job.StartedAt;
        if (job.LastActivityAt > timestamp)
        {
            timestamp = job.LastActivityAt;
        }

        if (job.ResumeCheckpoint?.UpdatedAt > timestamp)
        {
            timestamp = job.ResumeCheckpoint.UpdatedAt;
        }

        var lastStatusTimestamp = job.StatusHistory
            .Select(entry => entry.Timestamp)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (lastStatusTimestamp > timestamp)
        {
            timestamp = lastStatusTimestamp;
        }

        if (!string.IsNullOrWhiteSpace(jobPath) && File.Exists(jobPath))
        {
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(jobPath), TimeSpan.Zero);
            if (modified > timestamp)
            {
                timestamp = modified;
            }
        }

        return timestamp;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:0.0}h";
        }

        return $"{Math.Max(1, duration.TotalMinutes):0}m";
    }
}
