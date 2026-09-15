using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

public partial class AutoTagService
{
    private static readonly TimeSpan QualityChecksQueuePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QualityChecksQueueAdmissionWait = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan QualityChecksQueueJobWait = TimeSpan.FromHours(12);
    private static readonly TimeSpan QualityChecksQueueDownloadWait = TimeSpan.FromMinutes(30);

    /// <summary>
    /// One library's step in a Quality Checks run. The server walks the steps in order, starting
    /// each library's job only after the previous one has completely finished, so the pipeline lock
    /// (the single-active-job admission) is held per library and released between libraries.
    /// <see cref="BuildError"/> carries a library that could not be prepared at all; it is reported
    /// and skipped without stopping the queue.
    /// </summary>
    public sealed record QualityChecksLibraryStep(
        string LibraryName,
        string RootPath,
        string ConfigJson,
        StartJobOptions? Options,
        bool ChecksReportOnly,
        string? BuildError = null);

    public sealed class QualityChecksQueueLibraryResult
    {
        public string LibraryName { get; init; } = string.Empty;
        public string Status { get; set; } = "pending";
        public bool ChecksReportOnly { get; set; }
        public int FoundCount { get; set; }
        public int GapFilledCount { get; set; }
        public int SidecarredCount { get; set; }
        public int TidiedCount { get; set; }
        public string? Error { get; set; }
    }

    public sealed class QualityChecksQueueProgress
    {
        public string Id { get; init; } = string.Empty;
        public string Status { get; set; } = AutoTagLiterals.QueuedStatus;
        public int Total { get; init; }
        public int CurrentIndex { get; set; }
        public string? CurrentLibraryName { get; set; }
        public string? CurrentJobId { get; set; }
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; set; }
        public List<QualityChecksQueueLibraryResult> Libraries { get; init; } = new();
    }

    /// <summary>
    /// Enqueues a server-side Quality Checks run over several libraries and returns its progress
    /// handle immediately. The UI makes one request for "all libraries"; the server owns the loop.
    /// </summary>
    public QualityChecksQueueProgress EnqueueQualityChecksRun(IReadOnlyList<QualityChecksLibraryStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            throw new ArgumentException("At least one library is required for a Quality Checks run.", nameof(steps));
        }

        var progress = new QualityChecksQueueProgress
        {
            Id = Guid.NewGuid().ToString("N"),
            Total = steps.Count,
            StartedAt = DateTimeOffset.UtcNow,
            Libraries = steps
                .Select(step => new QualityChecksQueueLibraryResult
                {
                    LibraryName = step.LibraryName,
                    ChecksReportOnly = step.ChecksReportOnly
                })
                .ToList()
        };
        var state = new QualityChecksQueueState
        {
            Steps = steps,
            Progress = progress
        };
        _qualityChecksQueues[progress.Id] = state;
        _ = RunQualityChecksQueueAsync(state);
        return progress;
    }

    public QualityChecksQueueProgress? GetQualityChecksRun(string? id)
        => !string.IsNullOrWhiteSpace(id) && _qualityChecksQueues.TryGetValue(id, out var state)
            ? state.Progress
            : null;

    private async Task RunQualityChecksQueueAsync(QualityChecksQueueState state)
    {
        var progress = state.Progress;
        progress.Status = AutoTagLiterals.RunningStatus;
        try
        {
            for (var index = 0; index < state.Steps.Count; index++)
            {
                var step = state.Steps[index];
                var result = progress.Libraries[index];
                progress.CurrentIndex = index;
                progress.CurrentLibraryName = step.LibraryName;
                progress.CurrentJobId = null;

                if (!string.IsNullOrWhiteSpace(step.BuildError))
                {
                    result.Status = "failed";
                    result.Error = step.BuildError;
                    progress.FailedCount++;
                    AppendQueueLog($"library '{step.LibraryName}' could not be prepared: {step.BuildError}");
                    continue;
                }

                try
                {
                    var job = await StartQualityChecksLibraryJobAsync(step);
                    if (job == null)
                    {
                        throw new InvalidOperationException(
                            "Downloads stayed active, so the library was not started within the wait window.");
                    }

                    progress.CurrentJobId = job.Id;
                    AppendQueueLog($"library '{step.LibraryName}' started (job {job.Id}).");
                    var finished = await WaitForQualityChecksJobAsync(job.Id);
                    var finalStatus = finished?.Status ?? job.Status;
                    result.FoundCount = finished?.EnhancementFoundCount ?? job.EnhancementFoundCount;
                    result.GapFilledCount = finished?.EnhancementGapFilledCount ?? job.EnhancementGapFilledCount;
                    result.SidecarredCount = finished?.EnhancementSidecarredCount ?? job.EnhancementSidecarredCount;
                    result.TidiedCount = finished?.EnhancementTidiedCount ?? job.EnhancementTidiedCount;

                    if (IsQualityChecksFailureStatus(finalStatus))
                    {
                        result.Status = "failed";
                        result.Error = finished?.Error ?? job.Error ?? $"quality checks {finalStatus}";
                        progress.FailedCount++;
                        AppendQueueLog($"library '{step.LibraryName}' failed: {result.Error}; continuing with the remaining libraries.");
                    }
                    else
                    {
                        result.Status = "completed";
                        progress.CompletedCount++;
                        AppendQueueLog(
                            $"library '{step.LibraryName}' completed: found={result.FoundCount}, gap-filled={result.GapFilledCount}, sidecars={result.SidecarredCount}, tidied={result.TidiedCount}.");
                    }

                    // A library's run may stage downloads; the next library must not start until
                    // those settle. Bounded and fail-open.
                    await WaitForQualityChecksDownloadsToSettleAsync();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed library is reported and the queue continues; it never cancels the rest.
                    result.Status = "failed";
                    result.Error = ex.Message;
                    progress.FailedCount++;
                    _logger.LogWarning(
                        ex,
                        "Quality Checks queue: library {Library} failed; continuing with the remaining libraries.",
                        step.LibraryName);
                    AppendQueueLog($"library '{step.LibraryName}' failed: {ex.Message}; continuing with the remaining libraries.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            progress.Status = AutoTagLiterals.CanceledStatus;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress.Status = AutoTagLiterals.FailedStatus;
            _logger.LogError(ex, "Quality Checks queue {QueueId} failed.", progress.Id);
        }
        finally
        {
            if (IsActiveJobStatus(progress.Status))
            {
                progress.Status = progress.FailedCount > 0 ? "completed_with_failures" : AutoTagLiterals.CompletedStatus;
            }

            progress.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Starts one library's job. It waits for the pipeline to be free (no active downloads, no
    /// other job) and retries within a bounded window instead of holding any lock across libraries.
    /// </summary>
    private async Task<AutoTagJob?> StartQualityChecksLibraryJobAsync(QualityChecksLibraryStep step)
    {
        var deadline = DateTimeOffset.UtcNow + QualityChecksQueueAdmissionWait;
        while (true)
        {
            if (!await _queueRepository.HasActiveDownloadsAsync() && !HasRunningJobs())
            {
                var job = await StartJob(step.RootPath, step.ConfigJson, step.Options);
                if (job != null && !string.Equals(job.Status, AutoTagLiterals.BlockedStatus, StringComparison.OrdinalIgnoreCase))
                {
                    return job;
                }

                if (job != null)
                {
                    _logger.LogWarning(
                        "Quality Checks queue: library {Library} was blocked: {Reason}",
                        step.LibraryName,
                        job.Error);
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(QualityChecksQueuePollInterval);
        }
    }

    private async Task<AutoTagJob?> WaitForQualityChecksJobAsync(string jobId)
    {
        var deadline = DateTimeOffset.UtcNow + QualityChecksQueueJobWait;
        while (true)
        {
            var job = GetJob(jobId);
            if (job != null && !IsActiveJobStatus(job.Status))
            {
                return job;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return job;
            }

            await Task.Delay(QualityChecksQueuePollInterval);
        }
    }

    private async Task WaitForQualityChecksDownloadsToSettleAsync()
    {
        var deadline = DateTimeOffset.UtcNow + QualityChecksQueueDownloadWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await _queueRepository.HasActiveDownloadsAsync())
            {
                return;
            }

            await Task.Delay(QualityChecksQueuePollInterval);
        }
    }

    private static bool IsQualityChecksFailureStatus(string? status)
        => !string.IsNullOrWhiteSpace(status)
            && (status.Equals(AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase)
                || status.Equals(AutoTagLiterals.ErrorStatus, StringComparison.OrdinalIgnoreCase)
                || status.Equals(AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
                || status.Equals(AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
                || status.Equals(AutoTagLiterals.BlockedStatus, StringComparison.OrdinalIgnoreCase));

    private void AppendQueueLog(string message)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Quality Checks queue: {Message}", message);
        }
    }

    private sealed class QualityChecksQueueState
    {
        public required IReadOnlyList<QualityChecksLibraryStep> Steps { get; init; }
        public required QualityChecksQueueProgress Progress { get; init; }
    }
}
