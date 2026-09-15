using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guardrails for the quality-checks run shape: one library per pick, a server-side queue that
/// walks "all libraries" in turn and continues after a failure, the remembered last library, the
/// report-only warning, per-stage counts, and the removals that came with the redesign.
/// </summary>
public sealed class QualityChecksSingleLibraryRunTest
{
    [Fact]
    public void ChecksCard_PicksOneLibraryAndRemembersTheChoice()
    {
        var view = ReadView();
        var script = ReadScript();

        Assert.Contains("id=\"enhancementQualityLibrary\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"enhancementQualityFolder\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("enhancementQualityFolderOptions", view, StringComparison.Ordinal);

        var picker = Slice(script, "function refreshQualityChecksLibraryPicker", "function qualityChecksLibraryKey");
        Assert.Contains("select.value = \"\";", picker, StringComparison.Ordinal);
        Assert.Contains("\"All libraries\"", picker, StringComparison.Ordinal);
        // section 11 decision 2: the last library is persisted and pre-selected next time.
        Assert.Contains("AUTOTAG_QUALITY_CHECKS_LIBRARY_KEY", script, StringComparison.Ordinal);
        Assert.Contains("localStorage.getItem(AUTOTAG_QUALITY_CHECKS_LIBRARY_KEY)", picker, StringComparison.Ordinal);
        Assert.Contains("select.value = storedKey;", picker, StringComparison.Ordinal);
        Assert.Contains("persistQualityChecksLibrary(select.value)", picker, StringComparison.Ordinal);

        // The old multi-select control and its profile grouping are gone from the checks path.
        Assert.DoesNotContain("enhancementQualityFolder", script, StringComparison.Ordinal);
        Assert.DoesNotContain("setupQualityChecksFolderDropdown", script, StringComparison.Ordinal);
        Assert.DoesNotContain("updateQualityChecksFolderSummary", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksRunner_MakesOneRequestAndLetsTheServerWalkTheLibraries()
    {
        var script = ReadScript();
        var runner = Slice(script, "async function runEnhancementQualityChecks", "async function pollQualityChecksQueue");

        // section 11 decision 1 + the server-side queue: one request, and the server owns the loop.
        Assert.Contains("postCentralEnhancementStart(features, folderIds)", runner, StringComparison.Ordinal);
        Assert.Contains("payload?.qualityChecksQueue === true", runner, StringComparison.Ordinal);
        Assert.Contains("pollQualityChecksQueue(", runner, StringComparison.Ordinal);

        // The UI must no longer loop over libraries or issue one request per library.
        Assert.DoesNotContain("for (let index", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("startCentralEnhancementFeature(", runner, StringComparison.Ordinal);

        // The staged-download wait moved to the server with the queue.
        Assert.DoesNotContain("waitForEnhancementDownloadsToSettle", script, StringComparison.Ordinal);
        Assert.Contains("/api/autotag/enhancement/quality-checks/queue/", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerQueue_WalksLibrariesInTurnAndContinuesAfterAFailure()
    {
        var queue = ReadWebFile("Services", "AutoTagService.QualityChecksQueue.cs");
        var loop = Slice(queue, "private async Task RunQualityChecksQueueAsync", "private async Task<AutoTagJob?> StartQualityChecksLibraryJobAsync");

        // One library at a time: the next step starts only after the previous job is fully done.
        Assert.Contains("for (var index = 0; index < state.Steps.Count; index++)", loop, StringComparison.Ordinal);
        Assert.Contains("await StartQualityChecksLibraryJobAsync(step)", loop, StringComparison.Ordinal);
        Assert.Contains("await WaitForQualityChecksJobAsync(job.Id)", loop, StringComparison.Ordinal);
        Assert.Contains("await WaitForQualityChecksDownloadsToSettleAsync()", loop, StringComparison.Ordinal);

        // section 11 decision 1: a failed library is reported and the queue continues; it never
        // breaks out of the loop.
        Assert.Contains("continuing with the remaining libraries", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("break;", loop, StringComparison.Ordinal);

        // The pipeline lock is acquired per library by StartJob, never once for the whole queue.
        Assert.Contains("StartJob(step.RootPath, step.ConfigJson, step.Options)", queue, StringComparison.Ordinal);
        Assert.Contains("_qualityChecksQueues[progress.Id] = state;", queue, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_EnqueuesAllLibrariesAndExposesQueueProgress()
    {
        var controller = ReadWebFile("Controllers", "Api", "AutoTagApiController.cs");

        Assert.Contains("StartQualityChecksEnhancementAsync(", controller, StringComparison.Ordinal);
        Assert.Contains("_autoTagService.EnqueueQualityChecksRun(steps)", controller, StringComparison.Ordinal);
        Assert.Contains("qualityChecksQueue = true", controller, StringComparison.Ordinal);
        Assert.Contains("enhancement/quality-checks/queue/{id}", controller, StringComparison.Ordinal);
        Assert.Contains("_autoTagService.GetQualityChecksRun(id)", controller, StringComparison.Ordinal);
        // A single library still takes the direct per-library path.
        Assert.Contains("_autoTagService.StartJob(single.RootPath, single.ConfigJson, single.Options)", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksRun_ReportsPerStageCountsAndLibraryProgress()
    {
        var script = ReadScript();
        var runner = Slice(script, "async function pollQualityChecksQueue", "async function runEnhancementGapFilling");
        var counts = Slice(script, "function describeQualityChecksLibraryResult", "function describeQualityCheckStageCounts");

        Assert.Contains("Library ${Math.min(index + 1, total)}/${total}: ${name}", runner, StringComparison.Ordinal);
        Assert.Contains("foundCount", counts, StringComparison.Ordinal);
        Assert.Contains("gapFilledCount", counts, StringComparison.Ordinal);
        Assert.Contains("sidecarredCount", counts, StringComparison.Ordinal);
        Assert.Contains("tidiedCount", counts, StringComparison.Ordinal);

        var queue = ReadWebFile("Services", "AutoTagService.QualityChecksQueue.cs");
        Assert.Contains("public int FoundCount", queue, StringComparison.Ordinal);
        Assert.Contains("public int GapFilledCount", queue, StringComparison.Ordinal);
        Assert.Contains("public int SidecarredCount", queue, StringComparison.Ordinal);
        Assert.Contains("public int TidiedCount", queue, StringComparison.Ordinal);
        Assert.Contains("result.FoundCount = finished?.EnhancementFoundCount", queue, StringComparison.Ordinal);

        var jobState = ReadWebFile("Services", "AutoTagService.cs");
        Assert.Contains("public int EnhancementFoundCount", jobState, StringComparison.Ordinal);
        Assert.Contains("public int EnhancementGapFilledCount", jobState, StringComparison.Ordinal);
        Assert.Contains("public int EnhancementSidecarredCount", jobState, StringComparison.Ordinal);
        Assert.Contains("public int EnhancementTidiedCount", jobState, StringComparison.Ordinal);
        Assert.Contains("job.EnhancementGapFilledCount += currentFiles.Count;", ReadWorkflows(), StringComparison.Ordinal);
        Assert.Contains("job.EnhancementSidecarredCount += currentFiles.Count;", ReadWorkflows(), StringComparison.Ordinal);
        Assert.Contains("job.EnhancementTidiedCount += context.CurrentFiles.Count;", ReadWorkflows(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksRun_WarnsWhenTheProfileCannotRepair()
    {
        var workflows = ReadWorkflows();
        var start = workflows.IndexOf("private async Task RunQualityChecksOnlyAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = workflows.IndexOf("private async Task RunCoordinatedEnhancementBatchAsync", start, StringComparison.Ordinal);
        var body = workflows[start..end];

        Assert.Contains("HasAnyRepairSectionsEnabled(checksConfigRoot)", body, StringComparison.Ordinal);
        Assert.Contains("will only be reported on, not repaired", body, StringComparison.Ordinal);

        var controller = ReadWebFile("Controllers", "Api", "AutoTagApiController.cs");
        Assert.Contains("checksReportOnly", controller, StringComparison.Ordinal);
        Assert.Contains("!EnhancementWorkflowSelection.HasAnyRepairSectionsEnabled(configNode)", controller, StringComparison.Ordinal);
        Assert.Contains("checksReportOnly = library.ChecksReportOnly", controller, StringComparison.Ordinal);

        // The queue's report-only libraries are known up front, so the UI warns before the first
        // library starts.
        var script = ReadScript();
        Assert.Contains("warnAboutReportOnlyLibraries(payload?.libraries)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyQualityChecksEnabledFlagIsNoLongerWrittenOrRead()
    {
        var selection = ReadWebFile("Services", "EnhancementWorkflowSelection.cs");
        Assert.DoesNotContain("SetSectionEnabled(enhancement, \"qualityChecks\", true)", selection, StringComparison.Ordinal);
        Assert.DoesNotContain("IsMissingCoreMetadataScanEnabled", selection, StringComparison.Ordinal);

        var workflows = ReadWorkflows();
        Assert.DoesNotContain("ReadBool(qualityChecks, EnabledField) != true", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("PriorityTargetFilesKey", ReadWebFile("Services", "AutoTagService.cs"), StringComparison.Ordinal);

        var script = ReadScript();
        Assert.DoesNotContain("qualityChecksHasEnabledFlag", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateWinner_UsesExistingQualityThenMetadataRule()
    {
        var service = ReadWebFile("Services", "DuplicateCleanerService.cs");
        var pickBest = Slice(service, "private static int PickBest", "private static int PickLowestQuality");

        // Quality first, then the existing size/metadata tie-breaks. No bit depth, sample rate or
        // bitrate comparison is introduced by the redesign.
        Assert.Contains("OrderByDescending(index => candidates[index].QualityRank)", pickBest, StringComparison.Ordinal);
        Assert.Contains("ThenByDescending(index => candidates[index].FileSize)", pickBest, StringComparison.Ordinal);
        Assert.Contains("ThenByDescending(index => ComputeMetadataScore(candidates[index]))", pickBest, StringComparison.Ordinal);
        Assert.DoesNotContain("BitDepth", pickBest, StringComparison.Ordinal);
        Assert.DoesNotContain("SampleRate", pickBest, StringComparison.Ordinal);
        Assert.DoesNotContain("Bitrate", pickBest, StringComparison.Ordinal);
    }

    [Fact]
    public void MashupClassifier_IsUsedByTheAuditAndByDedupeStrongIdentity()
    {
        var workflows = ReadWorkflows();
        Assert.Contains(
            "PartitionUnofficialMashupsAsync(missingFiles, IsMashupCandidateIdentifiedAsync, cancellationToken)",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains("class MashupClassifier", ReadWebFile("Services", "MashupClassifier.cs"), StringComparison.Ordinal);

        var service = ReadWebFile("Services", "DuplicateCleanerService.cs");
        Assert.Contains("MashupClassifier.IsMashupTitle", service, StringComparison.Ordinal);
        Assert.Contains("if (candidate.IsMashup)", service, StringComparison.Ordinal);
        Assert.Contains("if (left.IsMashup || right.IsMashup)", service, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start > 0, $"marker not found: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"end marker not found: {endMarker}");
        return source[start..end];
    }

    private static string ReadView()
        => File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));

    private static string ReadScript()
        => File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));

    private static string ReadWorkflows()
        => ReadWebFile("Services", "AutoTagService.EnhancementWorkflows.cs");

    private static string ReadWebFile(params string[] parts)
    {
        var path = Path.Join(FindRepoRoot(), "DeezSpoTag.Web");
        foreach (var part in parts)
        {
            path = Path.Join(path, part);
        }

        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Join(directory, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory, "DeezSpoTag.Tests")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
