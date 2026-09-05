using System;
using System.IO;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guardrails for the AutoTag job-persistence IO contract:
/// - routine progress updates (per-file statuses, log lines) go through the save
///   throttle, while checkpoint updates force an immediate save (resume anchor);
/// - terminal transitions and stop paths always save directly;
/// - archived log/status counts are maintained incrementally instead of re-counting
///   whole files on every per-file event.
/// </summary>
public sealed class AutoTagJobSaveThrottleTests
{
    [Theory]
    [InlineData(0, 250, true)]
    [InlineData(0, 999, true)]
    [InlineData(0, 1000, false)]
    [InlineData(0, 5000, false)]
    public void ShouldThrottleJobSave_WithinOneSecondWindowOnly(int lastSaveSecondsAgo, int nowOffsetMs, bool expected)
    {
        var lastSave = DateTimeOffset.UtcNow.AddSeconds(-lastSaveSecondsAgo);
        var now = lastSave.AddMilliseconds(nowOffsetMs);

        Assert.Equal(expected, InvokeShouldThrottleJobSave(lastSave, now));
    }

    [Fact]
    public void ShouldThrottleJobSave_NoPriorSaveNeverThrottles()
    {
        Assert.False(InvokeShouldThrottleJobSave(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CountArchiveFileLines_CountsLinesAndReturnsNullForMissingFiles()
    {
        var method = typeof(AutoTagService).GetMethod(
            "CountArchiveFileLines",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AutoTagService.CountArchiveFileLines not found.");

        var tempFile = Path.Combine(Path.GetTempPath(), $"autotag-logcount-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllLines(tempFile, new[] { "one", "two", "three" });
            var counted = Assert.IsType<int>(method.Invoke(null, new object?[] { tempFile }));
            Assert.Equal(3, counted);

            var missing = Path.Combine(Path.GetTempPath(), $"autotag-logcount-missing-{Guid.NewGuid():N}.log");
            Assert.Null(method.Invoke(null, new object?[] { missing }));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void RoutineProgressSaves_GoThroughTheThrottle()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        var appendLog = ExtractMethodBody(source, "private void AppendLog(AutoTagJob job, string? line)");
        Assert.Contains("SaveJobThrottled(job);", appendLog, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveJob(job);", appendLog, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckpointUpdates_ForceAnImmediateSave()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        var updateStatus = ExtractMethodBody(source, "private void UpdateStatus(");
        Assert.Contains("SaveJobThrottled(job, force: checkpointChanged)", updateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalAndStopPaths_SaveDirectly()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        foreach (var methodName in new[]
                 {
                     "private void FinalizeStageExecution(AutoTagJob job, bool success)",
                     "private void HandleRunJobCanceled(AutoTagJob job)"
                 })
        {
            var body = ExtractMethodBody(source, methodName);
            Assert.Contains("SaveJob(job);", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ArchivedCounts_AreIncrementalNotReCounted()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        var logCount = ExtractMethodBody(source, "private int GetArchivedLogCount(string jobId, int fallback)");
        var statusCount = ExtractMethodBody(source, "private int GetArchivedStatusCount(string jobId, int fallback)");

        // The getters seed once via GetOrAdd; they must not parse status history or
        // re-count whole files on every call.
        Assert.Contains("_archivedLogLineCounts.GetOrAdd", logCount, StringComparison.Ordinal);
        Assert.Contains("_archivedStatusEntryCounts.GetOrAdd", statusCount, StringComparison.Ordinal);
        Assert.DoesNotContain("ParseStatusHistoryEntries", statusCount, StringComparison.Ordinal);

        var appendLog = ExtractMethodBody(source, "private void AppendArchivedLog(string jobId, string line)");
        var appendStatus = ExtractMethodBody(source, "private void AppendArchivedStatus(string jobId, TaggingStatusSnapshot snapshot)");
        Assert.Contains("_archivedLogLineCounts.AddOrUpdate", appendLog, StringComparison.Ordinal);
        Assert.Contains("_archivedStatusEntryCounts.AddOrUpdate", appendStatus, StringComparison.Ordinal);
    }

    private static bool InvokeShouldThrottleJobSave(DateTimeOffset? lastSave, DateTimeOffset now)
    {
        var method = typeof(AutoTagService).GetMethod(
            "ShouldThrottleJobSave",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AutoTagService.ShouldThrottleJobSave not found.");

        return Assert.IsType<bool>(method.Invoke(null, new object?[] { lastSave, now }));
    }

    private static string ExtractMethodBody(string source, string signatureStart)
    {
        var start = source.IndexOf(signatureStart, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method starting with: {signatureStart}");
        var bodyStart = source.IndexOf('{', start);
        var depth = 0;
        for (var i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unterminated method body for: {signatureStart}");
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var repoRoot = ResolveRepoRoot();
        var path = Path.Join(repoRoot, Path.Join(relativeParts));
        Assert.True(File.Exists(path), $"Missing source: {path}");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
