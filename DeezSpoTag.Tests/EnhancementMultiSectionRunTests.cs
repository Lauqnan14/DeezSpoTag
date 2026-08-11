using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EnhancementMultiSectionRunTests
{
    [Fact]
    public void EnhancementStart_AcceptsMoreThanOneSection()
    {
        var source = ReadController();

        Assert.Contains("if (selectedFeatures.Count < 1)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Select exactly one enhancement section per job.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementStart_KeepsManualEnrichmentExclusive()
    {
        var source = ReadController();

        Assert.Contains("isManualEnrichment && selectedFeatures.Count > 1", source, StringComparison.Ordinal);
        Assert.Contains("Manual enrichment must run as its own job.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementStart_LeavesFeatureUnsetForMultiSectionRuns()
    {
        var source = ReadController();

        Assert.Contains(
            "EnhancementFeature: selectedFeatures.Count == 1 ? selectedFeatures.Single() : null",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "folder-uniformity" }, 1)]
    [InlineData(new[] { "folder-uniformity", "cover-maintenance" }, 2)]
    [InlineData(new[] { "folder-uniformity", "cover-maintenance", "quality-checks", "tag-gap-fill" }, 4)]
    [InlineData(new[] { "folder-uniformity", "folder-uniformity" }, 1)]
    [InlineData(new[] { "bogus-section" }, 0)]
    public void NormalizeEnhancementFeatures_KeepsEveryKnownSection(string[] requested, int expected)
    {
        var method = typeof(AutoTagJobsController).GetMethod(
            "NormalizeEnhancementFeatures",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (System.Collections.Generic.IEnumerable<string>)method.Invoke(null, [requested])!;
        var count = result.Count();

        Assert.Equal(expected, count);
    }

    [Fact]
    public void ApplyEnhancementRunSelection_EnablesOnlyTheRequestedSections()
    {
        var enhancement = new JsonObject
        {
            ["gapFilling"] = new JsonObject(),
            ["folderUniformity"] = new JsonObject { ["enabled"] = false },
            ["coverMaintenance"] = new JsonObject { ["enabled"] = true },
            ["qualityChecks"] = new JsonObject { ["enabled"] = true }
        };
        var configNode = new JsonObject { ["enhancement"] = enhancement };
        var request = new AutoTagEnhancementStartRequest
        {
            Features = ["folder-uniformity", "cover-maintenance"]
        };

        var method = typeof(AutoTagJobsController).GetMethod(
            "ApplyEnhancementRunSelection",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [configNode, request, Array.Empty<long>(), Array.Empty<string>()]);

        Assert.True((bool)enhancement["folderUniformity"]!["enabled"]!);
        Assert.True((bool)enhancement["coverMaintenance"]!["enabled"]!);
        Assert.False((bool)enhancement["qualityChecks"]!["enabled"]!);
    }

    [Fact]
    public void ApplyEnhancementFolderScope_LeavesSavedSectionScopeWhenRequestHasNoFolders()
    {
        var enhancement = new JsonObject
        {
            ["folderUniformity"] = new JsonObject { ["folderIds"] = new JsonArray(7) }
        };
        var configNode = new JsonObject { ["enhancement"] = enhancement };
        var request = new AutoTagEnhancementStartRequest { Features = ["folder-uniformity"] };

        var method = typeof(AutoTagJobsController).GetMethod(
            "ApplyEnhancementRunSelection",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [configNode, request, Array.Empty<long>(), Array.Empty<string>()]);

        var folderIds = enhancement["folderUniformity"]!["folderIds"]!.AsArray();
        Assert.Single(folderIds);
        Assert.Equal(7, (int)folderIds[0]!);
    }

    [Fact]
    public void MissingCoreMetadataAudit_OnlyPreparesWhenQualityChecksIsPartOfTheRun()
    {
        var source = ReadWorkflows();
        var start = source.IndexOf("private static bool ShouldPrepareMissingCoreMetadataTargets", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = source.IndexOf("private static List<FolderDto> ResolveEnhancementJobFolders", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("ReadBool(qualityChecks, EnabledField) == true", method, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCoreMetadataAudit_ScopesToTheQualityChecksSection()
    {
        var source = ReadWorkflows();

        Assert.Contains(
            "AutoTagLiterals.EnhancementFeatureQualityChecks);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("string? featureOverride = null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementEngine_RunsEverySelectedSectionInOneJob()
    {
        var source = ReadWorkflows();
        var start = source.IndexOf("private async Task RunIntegratedEnhancementWorkflowsAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = source.IndexOf("private bool ShouldRunIntegratedEnhancementWorkflows", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("\"folder-uniformity\"", method, StringComparison.Ordinal);
        Assert.Contains("\"cover-maintenance\"", method, StringComparison.Ordinal);
        Assert.Contains("\"quality-checks\"", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RunScope_OffersEverySectionIncludingGapFilling()
    {
        var view = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));

        Assert.Contains("id=\"runScope-tag-gap-fill\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"enableFolderUniformityWorkflow\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"enableQualityChecksWorkflow\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"enableCoverMaintenanceWorkflow\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"runSelectedEnhancementSections\"", view, StringComparison.Ordinal);
        Assert.Contains("enhancement-checkbox-grid", view, StringComparison.Ordinal);
        Assert.DoesNotContain("enhancement-section-matrix", view, StringComparison.Ordinal);
    }

    [Fact]
    public void RunScope_IsNotPersistedToConfig()
    {
        var script = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var start = script.IndexOf("function readMoveAndFolderUniformityConfig", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = script.IndexOf("function readCoverAndQualityEnhancementConfig", start, StringComparison.Ordinal);
        var reader = script[start..end];

        Assert.DoesNotContain("runScope-", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementScopes_AreGroupedByProfileNotByFolder()
    {
        var script = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));

        Assert.Contains("function groupFolderIdsByProfile", script, StringComparison.Ordinal);
        Assert.Contains("autoTagProfileId", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Automation_RunsEverySelectedSectionInASingleJob()
    {
        var source = ReadOrchestration();

        Assert.DoesNotContain("foreach (var feature in enabledFeatures)", source, StringComparison.Ordinal);
        Assert.Contains(
            "BuildEnhancementFeatureConfig(enhancementConfig, enabledFeatures, target.FolderId)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnhancementFeature: enabledFeatures.Count == 1 ? enabledFeatures[0] : null",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Automation_ConfigBuilderEnablesTheWholeSelectedSet()
    {
        var source = ReadOrchestration();
        var start = source.IndexOf("private static string BuildEnhancementFeatureConfig", StringComparison.Ordinal);
        Assert.True(start > 0);
        var method = source[start..(start + 2000)];

        Assert.Contains("IReadOnlyCollection<string> selectedFeatures", method, StringComparison.Ordinal);
        Assert.Contains("selected.Contains(AutoTagLiterals.EnhancementFeatureFolderUniformity)", method, StringComparison.Ordinal);
        Assert.Contains("selected.Contains(AutoTagLiterals.EnhancementFeatureGapFill)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentDownloads_HasAUiTriggerThatSendsRecentScopeAndTargetFiles()
    {
        var view = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var script = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));

        Assert.Contains("id=\"runSelectedEnhancementSectionsRecent\"", view, StringComparison.Ordinal);
        Assert.Contains("collectRecentDownloadFilePaths", script, StringComparison.Ordinal);
        Assert.Contains("groupRecentTargetsByProfile", script, StringComparison.Ordinal);
        Assert.Contains("scope: recentOnly ? \"recent\" : \"full\"", script, StringComparison.Ordinal);
        Assert.Contains("request.targetFiles = options.targetFiles;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentDownloads_OnlyUsesCompletedQueueItems()
    {
        var script = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var start = script.IndexOf("async function collectRecentDownloadFilePaths", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = script.IndexOf("function isPathUnderRoot", start, StringComparison.Ordinal);
        var fn = script[start..end];

        Assert.Contains("\"complete\"", fn, StringComparison.Ordinal);
        Assert.Contains("FilePath", fn, StringComparison.Ordinal);
    }

    private static string ReadOrchestration()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"));

    [Fact]
    public void ManualFolderRun_LivesInTheEnhancementTabNotTheLibraryFolderRows()
    {
        var libraryScript = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "library.js"));
        var view = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));

        Assert.DoesNotContain("data-enhance", libraryScript, StringComparison.Ordinal);
        Assert.DoesNotContain("startFolderEnhancement", libraryScript, StringComparison.Ordinal);
        Assert.Contains("id=\"runSelectedEnhancementSections\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderScheduleControl_StaysOnTheLibraryFolderTab()
    {
        var libraryScript = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "library.js"));

        Assert.Contains("enhancementSchedule", libraryScript, StringComparison.Ordinal);
    }

    [Fact]
    public void RunUsesEachSectionsOwnFolderScope()
    {
        var script = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));

        Assert.Contains("parseFolderIdList(section.folderIds)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("enhancementRunFolder", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UnconfiguredWorkflowIsFlaggedByAHiddenTooltipRatherThanInlineText()
    {
        var view = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var script = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));

        foreach (var id in new[] { "tag-gap-fill", "folder-uniformity", "quality-checks", "cover-maintenance" })
        {
            Assert.Contains($"id=\"runScopeHint-{id}\"", view, StringComparison.Ordinal);
        }

        Assert.Contains("autotag-tooltip-icon autotag-tooltip-warning ms-1 d-none", view, StringComparison.Ordinal);
        Assert.Contains("hint.classList.toggle(\"d-none\", section.configured);", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"nothing configured\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowGuidanceIsATooltipNotAParagraph()
    {
        var view = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var marker = "Ticked workflows run together in a single job";

        Assert.Contains($"title=\"{marker}", view, StringComparison.Ordinal);
        Assert.DoesNotContain($"<span class=\"helper\">{marker}", view, StringComparison.Ordinal);
    }

    [Fact]
    public void RunWorkflowsBlockSitsBelowTheSectionCards()
    {
        var view = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var panel = view.IndexOf("id=\"autotag-stage3-panel\"", StringComparison.Ordinal);
        Assert.True(panel > 0);
        var platforms = view.IndexOf("TAB 6: PLATFORMS", panel, StringComparison.Ordinal);
        var segment = view[panel..platforms];

        var covers = segment.IndexOf("<!-- Cover Maintenance -->", StringComparison.Ordinal);
        var runBlock = segment.IndexOf("Enhancement Run Workflows", StringComparison.Ordinal);

        Assert.True(covers > 0);
        Assert.True(runBlock > covers, "the run block must render after the section cards");
    }

    [Fact]
    public void RecentDownloadCountIsShownOnTheButtonNotAsLooseText()
    {
        var view = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var script = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));

        Assert.DoesNotContain("enhancementRecentTargetHint", view, StringComparison.Ordinal);
        Assert.Contains("button.setAttribute(\"title\", label);", script, StringComparison.Ordinal);
        Assert.Contains("completed download", script, StringComparison.Ordinal);
    }

    private static string ReadController()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs"));

    private static string ReadWorkflows()
        => File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs"));

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

    [Fact]
    public void EveryEnabledSectionRunsOnABatchBeforeTheNextBatchStarts()
    {
        var workflows = ReadEnhancementWorkflows();
        var start = workflows.IndexOf("ApplyEnhancementBatchTemplatesAsync(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = workflows[start..(start + 1400)];

        Assert.Contains("RunEnabledEnhancementSectionsForBatchAsync(job, configPath, context", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PerBatchSectionRunnerCoversBatchSafeSectionsOnly()
    {
        var workflows = ReadEnhancementWorkflows();
        var start = workflows.IndexOf("RunEnabledEnhancementSectionsForBatchAsync(\n", StringComparison.Ordinal);
        if (start < 0)
        {
            start = workflows.IndexOf("private async Task<bool> RunEnabledEnhancementSectionsForBatchAsync(", StringComparison.Ordinal);
        }

        Assert.True(start > 0);
        var body = workflows[start..(start + 2600)];

        Assert.Contains("IsCoverMaintenanceWorkflowEnabled(enhancementRoot)", body, StringComparison.Ordinal);
        Assert.Contains("RunCoverMaintenanceForBatchAsync(", body, StringComparison.Ordinal);
        Assert.Contains("RunQualityChecksForBatchAsync(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RunFolderUniformityForPathsAsync(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EndOfRunPassDoesNotRepeatWorkAlreadyDonePerBatch()
    {
        var workflows = ReadEnhancementWorkflows();

        Assert.Contains("job.EnhancementSectionsAppliedPerBatch = true;", workflows, StringComparison.Ordinal);
        Assert.Contains("if (job.EnhancementSectionsAppliedPerBatch)", workflows, StringComparison.Ordinal);
        Assert.Contains("already applied per batch", workflows, StringComparison.Ordinal);
        Assert.Contains("cover maintenance already applied per batch", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void PerBatchSectionsAreScopedToTheBatchFiles()
    {
        var workflows = ReadEnhancementWorkflows();

        Assert.Contains("ResolveBatchRootPaths(", workflows, StringComparison.Ordinal);
        Assert.Contains("TargetFiles: batchFiles.ToList()", workflows, StringComparison.Ordinal);
        Assert.Contains("context.CurrentFiles", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchTemplateApplicationFeedsFreshPathsToLaterSections()
    {
        var workflows = ReadEnhancementWorkflows();

        Assert.Contains("OrganizeFilesWithReportAsync(", workflows, StringComparison.Ordinal);
        Assert.Contains("ResolveCurrentBatchFiles(successfulBatchFiles, folderReports)", workflows, StringComparison.Ordinal);
        Assert.Contains("context = await RefreshBatchLibraryIndexAsync", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void LyricsRefreshRunsInsideTheBatchQualitySection()
    {
        var workflows = ReadEnhancementWorkflows();
        var start = workflows.IndexOf("private async Task<bool> RunQualityChecksForBatchAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = workflows[start..(start + 3000)];

        Assert.Contains("options.QueueLyricsRefresh", body, StringComparison.Ordinal);
        Assert.Contains("RunLyricsRefreshForBatchAsync(", body, StringComparison.Ordinal);
        Assert.Contains("targetTrackIds", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchMediaRefreshUsesTheOutbox()
    {
        var workflows = ReadEnhancementWorkflows();

        Assert.Contains("EnqueueBatchMediaServerRefreshAsync(job, context", workflows, StringComparison.Ordinal);
        Assert.Contains("_mediaServerRefreshOutboxService.EnqueueAsync", workflows, StringComparison.Ordinal);
    }

    private static string ReadEnhancementWorkflows()
        => File.ReadAllText(Path.Join(
            FindEnhancementRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs"));

    private static string FindEnhancementRepoRoot()
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
