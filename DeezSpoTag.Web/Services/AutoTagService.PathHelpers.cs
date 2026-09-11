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

    private async Task<bool> ShouldSkipForActiveDownloadsAsync()
    {
        return await _queueRepository.HasActiveDownloadsAsync();
    }

    private static bool HasEligibleInputFiles(string rootPath, string configJson)
    {
        var normalizedRoot = NormalizeRootPath(rootPath);
        if (normalizedRoot == null)
        {
            return false;
        }

        if (!TryParseAutoTagConfig(configJson, out var root))
        {
            // Avoid suppressing valid runs due to config parse issues.
            return true;
        }

        var includeSubfolders = ReadBool(root, AutoTagLiterals.IncludeSubfoldersKey) ?? true;
        var targetFiles = ReadStringList(root, AutoTagLiterals.TargetFilesKey);
        if (targetFiles.Count > 0)
        {
            return HasEligibleTargetFiles(targetFiles, normalizedRoot);
        }

        return HasEligibleFilesInDirectory(normalizedRoot, includeSubfolders);
    }

    private static bool HasEligibleTargetFiles(IEnumerable<string> targetFiles, string normalizedRoot)
    {
        foreach (var rawPath in targetFiles)
        {
            var candidate = NormalizePathForJob(rawPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsPathWithinScope(candidate, normalizedRoot)
                || !File.Exists(candidate))
            {
                continue;
            }

            var extension = Path.GetExtension(candidate);
            if (!string.IsNullOrWhiteSpace(extension) && EligibleAudioExtensions.Contains(extension))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEligibleFilesInDirectory(string normalizedRoot, bool includeSubfolders)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        return Directory.EnumerateFiles(normalizedRoot, "*", options)
            .Select(Path.GetExtension)
            .Any(extension => !string.IsNullOrWhiteSpace(extension) && EligibleAudioExtensions.Contains(extension));
    }

    private static bool IsPathWithinScope(string candidatePath, string scopePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(scopePath))
        {
            return false;
        }

        if (string.Equals(candidatePath, scopePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var scopeWithSeparator = scopePath.EndsWith(Path.DirectorySeparatorChar)
                                 || scopePath.EndsWith(Path.AltDirectorySeparatorChar)
            ? scopePath
            : scopePath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(scopeWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<long>> ResolveChangedLibraryFolderIdsAsync(
        AutoTagMoveSummary autoMoveSummary,
        CancellationToken cancellationToken)
    {
        var changed = autoMoveSummary.ChangedFolderIds
            .Where(folderId => folderId > 0)
            .ToHashSet();
        if (autoMoveSummary.MovedCount <= 0 || autoMoveSummary.DestinationRoots.Count == 0 || !_libraryRepository.IsConfigured)
        {
            return changed.OrderBy(folderId => folderId).ToList();
        }

        var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
        foreach (var destinationRoot in autoMoveSummary.DestinationRoots)
        {
            AddMatchingLibraryFolders(destinationRoot, folders, changed);
        }

        return changed.OrderBy(folderId => folderId).ToList();
    }

    private async Task<Dictionary<long, List<string>>> ResolveChangedLibraryFilesByFolderAsync(
        AutoTagMoveSummary autoMoveSummary,
        List<long> changedFolderIds,
        CancellationToken cancellationToken)
    {
        var grouped = new Dictionary<long, List<string>>();
        if (autoMoveSummary.ChangedFilePaths.Count == 0
            || changedFolderIds.Count == 0
            || !_libraryRepository.IsConfigured)
        {
            return grouped;
        }

        var changedFolderIdSet = changedFolderIds.ToHashSet();
        var folders = (await _libraryRepository.GetFoldersAsync(cancellationToken))
            .Where(folder => changedFolderIdSet.Contains(folder.Id))
            .ToList();

        foreach (var path in autoMoveSummary.ChangedFilePaths)
        {
            foreach (var folder in folders)
            {
                TryAddPathToFolderGroup(grouped, path, folder);
            }
        }

        return grouped;
    }

    private static void TryAddPathToFolderGroup(
        Dictionary<long, List<string>> grouped,
        string path,
        FolderDto folder)
    {
        try
        {
            if (!IsPathUnderRoot(path, folder.RootPath))
            {
                return;
            }

            if (!grouped.TryGetValue(folder.Id, out var paths))
            {
                paths = new List<string>();
                grouped[folder.Id] = paths;
            }

            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ignore paths the runtime cannot normalize; folder-level fallback remains available.
        }
    }

    private static List<long> ParseFolderIds(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is not JsonArray values)
        {
            return new List<long>();
        }

        var parsed = new List<long>();
        foreach (var item in values)
        {
            if (item is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var longValue) && longValue > 0)
            {
                parsed.Add(longValue);
                continue;
            }

            if (item is JsonValue stringValue
                && stringValue.TryGetValue<string>(out var raw)
                && long.TryParse(raw, out var parsedValue)
                && parsedValue > 0)
            {
                parsed.Add(parsedValue);
            }
        }

        return parsed
            .Distinct()
            .ToList();
    }

    private static bool IsMusicCapableFolder(FolderDto folder)
    {
        var normalized = (folder.DesiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        return !normalized.Contains("video", StringComparison.Ordinal)
            && !normalized.Contains("podcast", StringComparison.Ordinal);
    }

    private static bool PathsOverlap(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftPrefix = normalizedLeft + Path.DirectorySeparatorChar;
        var rightPrefix = normalizedRight + Path.DirectorySeparatorChar;
        return normalizedLeft.StartsWith(rightPrefix, StringComparison.OrdinalIgnoreCase)
            || normalizedRight.StartsWith(leftPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AutoMoveExecutionResult> MoveAfterAutoTagAsync(
        AutoTagJob job,
        string rootPath,
        string configPath,
        IReadOnlyCollection<string> taggedFiles,
        IReadOnlyCollection<string> failedFiles,
        CancellationToken cancellationToken)
    {
        if (_disableAutoMove)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("AutoTag job {JobId}: auto-move skipped (disabled).", job.Id);
            }
            AppendLog(job, "auto-move skipped: disabled");
            var disabledSummary = new AutoTagMoveSummary
            {
                Error = "auto-move disabled by configuration."
            };
            ApplyAutoMoveSummary(job, disabledSummary);
            return new AutoMoveExecutionResult(false, disabledSummary);
        }

        try
        {
            _logger.LogInformation("AutoTag job JobId: auto-move started for RootPath");
            AppendLog(job, "auto-move started");
            var organizerOptions = await LoadOrganizerOptionsAsync(job, configPath);
            if (IsManualEnrichmentRunIntent(job.RunIntent))
            {
                organizerOptions.BatchScopedFilesOnly = true;
                organizerOptions.MoveUntaggedPath = null;
                organizerOptions.OnlyMoveWhenTagged = true;
            }
            var summary = await _downloadMoveService.MoveForRootWithSummaryAsync(
                rootPath,
                organizerOptions,
                taggedFiles,
                failedFiles,
                cancellationToken);
            _logger.LogInformation("AutoTag job JobId: auto-move finished for RootPath");
            AppendLog(job, "auto-move finished");
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(true, summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: auto-move failed.", job.Id);
            AppendLog(job, $"auto-move failed: {ex.Message}");
            var failedSummary = new AutoTagMoveSummary
            {
                FailedCount = 1,
                Error = ex.Message
            };
            ApplyAutoMoveSummary(job, failedSummary);
            return new AutoMoveExecutionResult(false, failedSummary);
        }
    }

    private static bool TryParseLegacyFolderId(JsonNode? folderIdNode, out long folderId)
    {
        folderId = 0;
        if (folderIdNode is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<long>(out var longValue) && longValue > 0)
        {
            folderId = longValue;
            return true;
        }

        if (value.TryGetValue<int>(out var intValue) && intValue > 0)
        {
            folderId = intValue;
            return true;
        }

        if (value.TryGetValue<string>(out var stringValue)
            && long.TryParse(stringValue, out var parsedValue)
            && parsedValue > 0)
        {
            folderId = parsedValue;
            return true;
        }

        return false;
    }


    private static void RemoveNulls(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kvp => kvp.Key).ToList())
            {
                var value = obj[key];
                if (value is null || value.GetValueKind() == JsonValueKind.Null)
                {
                    obj.Remove(key);
                }
                else
                {
                    RemoveNulls(value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(static child => child != null))
            {
                RemoveNulls(child!);
            }
        }
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

    private static DateTimeOffset GetFileSystemTimestampUtc(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return Directory.GetLastWriteTimeUtc(path);
            }

            if (File.Exists(path))
            {
                return File.GetLastWriteTimeUtc(path);
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // Use a current timestamp when the filesystem cannot provide one so pruning stays conservative.
        }

        return DateTimeOffset.UtcNow;
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to delete expired AutoTag history directory {Path}.", path);
            }
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to delete expired AutoTag history file {Path}.", path);
            }
        }
    }
}
