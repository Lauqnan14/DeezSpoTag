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

    public AutoTagTagDiff? GetTagDiff(string jobId, string path, string? platform = null)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = NormalizeDiffPath(path);
        var job = GetJob(jobId) ?? LoadJob(jobId);
        if (job != null)
        {
            var diffFromJob = TryResolveTagDiff(job.TagDiffs, normalized, path, platform);
            if (diffFromJob != null)
            {
                return diffFromJob;
            }
        }

        var diffFromArchive = TryResolveTagDiff(ReadRunTagDiffs(jobId), normalized, path, platform);
        if (diffFromArchive != null)
        {
            return diffFromArchive;
        }

        if (job == null)
        {
            return null;
        }

        // Fallback for older jobs: if no diff snapshots were persisted, capture a current snapshot
        // so the UI can still display tag data for troubleshooting.
        try
        {
            var current = BuildTagSnapshot(normalized);
            var fallback = new AutoTagTagDiff
            {
                Path = normalized,
                LastPlatform = null,
                Before = null,
                After = current
            };
            lock (job.TagDiffs)
            {
                job.TagDiffs[normalized] = fallback;
            }
            SaveJob(job);
            return SelectRequestedPlatformDiff(fallback, platform);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AutoTag diff fallback snapshot failed for Path");
        }

        return null;
    }

    private static AutoTagTagDiff? TryResolveTagDiff(
        Dictionary<string, AutoTagTagDiff>? tagDiffs,
        string normalizedPath,
        string rawPath,
        string? platform)
    {
        if (tagDiffs == null || tagDiffs.Count == 0)
        {
            return null;
        }

        if (tagDiffs.TryGetValue(normalizedPath, out var normalized))
        {
            return SelectRequestedPlatformDiff(normalized, platform);
        }

        if (tagDiffs.TryGetValue(rawPath, out var raw))
        {
            return SelectRequestedPlatformDiff(raw, platform);
        }

        return null;
    }

    private static AutoTagTagDiff SelectRequestedPlatformDiff(AutoTagTagDiff stored, string? requestedPlatform)
    {
        if (string.IsNullOrWhiteSpace(requestedPlatform))
        {
            return CloneDiff(stored);
        }

        var completed = (stored.PlatformDiffs ?? new List<AutoTagPlatformDiffSnapshot>())
            .Where(step => step.After != null)
            .ToList();
        if (completed.Count == 0)
        {
            return CloneDiff(stored);
        }

        var targetIndex = completed.FindLastIndex(step =>
            string.Equals(step.Platform, requestedPlatform, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
        {
            return CloneDiff(stored);
        }

        var target = completed[targetIndex];
        var isFinal = targetIndex == completed.Count - 1;
        var baseSnapshot = stored.Before ?? completed[0].Before ?? target.Before;
        var cumulativeSteps = completed
            .Take(targetIndex + 1)
            .Select(ClonePlatformDiff)
            .ToList();

        var selected = new AutoTagTagDiff
        {
            Path = stored.Path,
            LastPlatform = target.Platform,
            TargetPlatform = target.Platform,
            IsFinalPlatformDiff = isFinal,
            BasePlatform = "original",
            Before = baseSnapshot,
            After = target.After,
            PlatformDiffs = cumulativeSteps
        };

        if (selected.After != null)
        {
            selected.RetainedSources = ComputeRetainedSources(
                selected.Before,
                selected.After,
                selected.PlatformDiffs);
        }

        return selected;
    }

    private static AutoTagTagDiff CloneDiff(AutoTagTagDiff source)
    {
        return new AutoTagTagDiff
        {
            Path = source.Path,
            LastPlatform = source.LastPlatform,
            BasePlatform = source.BasePlatform,
            TargetPlatform = source.TargetPlatform,
            IsFinalPlatformDiff = source.IsFinalPlatformDiff,
            Before = source.Before,
            After = source.After,
            RetainedSources = source.RetainedSources != null
                ? new Dictionary<string, string>(source.RetainedSources, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            PlatformDiffs = (source.PlatformDiffs ?? new List<AutoTagPlatformDiffSnapshot>())
                .Select(ClonePlatformDiff)
                .ToList()
        };
    }

    private static AutoTagPlatformDiffSnapshot ClonePlatformDiff(AutoTagPlatformDiffSnapshot source)
    {
        return new AutoTagPlatformDiffSnapshot
        {
            Platform = source.Platform,
            Status = source.Status,
            CapturedAt = source.CapturedAt,
            Before = source.Before,
            After = source.After
        };
    }

    private static JsonObject CloneRoot(JsonObject root)
    {
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        return (JsonNode.Parse(json) as JsonObject) ?? new JsonObject();
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

    private static double ComputeIdentitySimilarity(object? before, object? after)
    {
        var normalizedBefore = AutoTagSimilarity.NormalizeText(NormalizeCompareValue(before));
        var normalizedAfter = AutoTagSimilarity.NormalizeText(NormalizeCompareValue(after));
        return AutoTagSimilarity.ComputeScore(normalizedBefore, normalizedAfter);
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

    private void PruneOrphanedJobSnapshots(HashSet<string> retainedIds, DateTimeOffset cutoffUtc)
    {
        try
        {
            if (!Directory.Exists(_jobsDir))
            {
                return;
            }

            foreach (var jobPath in Directory.EnumerateFiles(_jobsDir, AutoTagLiterals.JsonFileSearchPattern))
            {
                var jobId = Path.GetFileNameWithoutExtension(jobPath);
                if (string.IsNullOrWhiteSpace(jobId)
                    || _activeJobIds.ContainsKey(jobId)
                    || retainedIds.Contains(jobId))
                {
                    continue;
                }

                if (GetFileSystemTimestampUtc(jobPath) < cutoffUtc)
                {
                    TryDeleteFile(jobPath);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to prune orphaned AutoTag job snapshots.");
            }
        }
    }

    private string GetRunTagDiffsPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "tag-diffs.json");

    private Dictionary<string, AutoTagTagDiff> LoadPersistedTagDiffs(string jobId)
    {
        var resolved = new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = ResolveRunFilePath(jobId, "tag-diffs.json");
            if (!string.IsNullOrWhiteSpace(path))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, AutoTagTagDiff>>(
                    File.ReadAllText(path, Encoding.UTF8),
                    _jsonOptions);
                if (parsed != null)
                {
                    foreach (var (pathKey, diff) in parsed)
                    {
                        resolved[pathKey] = diff;
                    }
                }
            }

            var checkpointDirectory = GetRunTagDiffCheckpointDirectory(jobId);
            if (Directory.Exists(checkpointDirectory))
            {
                foreach (var checkpointPath in Directory.EnumerateFiles(checkpointDirectory, "*.json"))
                {
                    var checkpoint = JsonSerializer.Deserialize<Dictionary<string, AutoTagTagDiff>>(
                        File.ReadAllText(checkpointPath, Encoding.UTF8),
                        _jsonOptions);
                    if (checkpoint == null)
                    {
                        continue;
                    }
                    foreach (var (pathKey, diff) in checkpoint)
                    {
                        resolved[pathKey] = diff;
                    }
                }
            }
            return resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to load persisted AutoTag tag diffs for {JobId}", jobId);
            }
            return resolved;
        }
    }

    private Dictionary<string, AutoTagTagDiff> ReadRunTagDiffs(string jobId)
    {
        try
        {
            var path = ResolveRunFilePath(jobId, "tag-diffs.json");
            if (string.IsNullOrWhiteSpace(path))
            {
                return new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, AutoTagTagDiff>>(json, _jsonOptions);
            var resolved = parsed != null
                ? new Dictionary<string, AutoTagTagDiff>(parsed, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
            if (resolved.Count > 0)
            {
                return resolved;
            }

            var repaired = TryRepairArchivedTagDiffsFromJob(jobId, path);
            return repaired.Count > 0 ? repaired : resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read archived AutoTag tag diffs for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
