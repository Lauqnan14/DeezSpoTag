using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Behavioural guardrails for the quality-checks run shape: one library per run, a per-library
/// sequential queue that stops on failure, a report-only warning, per-stage counts, and the
/// removals that came with the redesign.
/// </summary>
public sealed class QualityChecksSingleLibraryRunTest
{
    [Fact]
    public void ChecksCard_PicksOneLibraryAndNeverRemembersTheChoice()
    {
        var view = ReadView();
        var script = ReadScript();

        Assert.Contains("id=\"enhancementQualityLibrary\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"enhancementQualityFolder\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("enhancementQualityFolderOptions", view, StringComparison.Ordinal);

        var picker = Slice(script, "function refreshQualityChecksLibraryPicker", "function qualityChecksLibraryKey");
        Assert.Contains("select.value = \"\";", picker, StringComparison.Ordinal);
        Assert.Contains("\"All libraries\"", picker, StringComparison.Ordinal);

        // The old multi-select control and its profile grouping are gone from the checks path.
        Assert.DoesNotContain("enhancementQualityFolder", script, StringComparison.Ordinal);
        Assert.DoesNotContain("setupQualityChecksFolderDropdown", script, StringComparison.Ordinal);
        Assert.DoesNotContain("updateQualityChecksFolderSummary", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksQueue_RunsLibrariesSequentiallyAndStopsOnFailure()
    {
        var script = ReadScript();
        var runner = Slice(script, "async function runEnhancementQualityChecks", "function describeQualityCheckStageCounts");

        // One scope per library, "all libraries" queues every scope, and the call is awaited so the
        // next library cannot start before the previous one finishes.
        Assert.Contains("resolveQualityChecksLibraryScopes(folders, selectedLibraryKey)", runner, StringComparison.Ordinal);
        Assert.Contains("await startCentralEnhancementFeature(features, scope.folderIds, \"enhancementQualityChecksStatus\")", runner, StringComparison.Ordinal);
        Assert.Contains("await waitForEnhancementDownloadsToSettle(", runner, StringComparison.Ordinal);
        // A failure throws out of the loop, so the remaining libraries are not started.
        Assert.Contains("throw new Error(`${position} failed:", runner, StringComparison.Ordinal);

        // The checks path no longer groups folders by profile.
        Assert.DoesNotContain("groupFolderIdsByProfile", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("resolveEnhancementFolderScopes", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksRun_ReportsPerStageCountsAndLibraryProgress()
    {
        var script = ReadScript();
        var runner = Slice(script, "async function runEnhancementQualityChecks", "async function runEnhancementGapFilling");
        var counts = Slice(script, "function describeQualityCheckStageCounts", "function isActiveDownloadQueueStatus");

        Assert.Contains("Library ${index + 1}/${total}: ${scope.libraryName}", runner, StringComparison.Ordinal);
        Assert.Contains("enhancementFoundCount", counts, StringComparison.Ordinal);
        Assert.Contains("enhancementGapFilledCount", counts, StringComparison.Ordinal);
        Assert.Contains("enhancementSidecarredCount", counts, StringComparison.Ordinal);
        Assert.Contains("enhancementTidiedCount", counts, StringComparison.Ordinal);

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
        Assert.Contains("QualityChecksLibraryScope.SpansMultipleLibraries(scopedFolders)", controller, StringComparison.Ordinal);
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
        Assert.Contains("PartitionUnofficialMashups(missingFiles)", workflows, StringComparison.Ordinal);
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
