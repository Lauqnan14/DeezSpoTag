using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
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
            "EnhancementFeature: selectedForJob.Count == 1 ? selectedForJob.Single() : null",
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
        var result = EnhancementWorkflowSelection.NormalizeSelectedFeatures(requested);
        Assert.Equal(expected, result.Count);
    }

    [Fact]
    public void ApplyEnhancementRunSelection_EnablesOnlyTheRequestedSections()
    {
        var enhancement = new JsonObject
        {
            ["gapFilling"] = new JsonObject(),
            ["folderUniformity"] = new JsonObject { ["enabled"] = false },
            ["sidecars"] = new JsonObject { ["enabled"] = true },
            ["coverMaintenance"] = new JsonObject { ["enabled"] = true, ["upgradeLowResolutionCovers"] = true },
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
        Assert.True((bool)enhancement["sidecars"]!["enabled"]!);
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
        var end = source.IndexOf("private async Task<EnhancementRunManifest> BuildEnhancementRunManifestAsync", start, StringComparison.Ordinal);
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

        Assert.Contains("EnhancementFeatureFolderUniformity", method, StringComparison.Ordinal);
        Assert.Contains("EnhancementFeatureSidecars", method, StringComparison.Ordinal);
        Assert.Contains("EnhancementFeatureQualityChecks", method, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementStageConfig_KeepsSelectedWorkflowSettingsForBatchExecution()
    {
        var field = typeof(AutoTagService).GetField(
            "EnhancementStageAllowedKeys",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var keys = Assert.IsAssignableFrom<HashSet<string>>(field!.GetValue(null));

        Assert.Contains("enhancement", keys);
    }

    [Fact]
    public void RunScope_OffersEverySectionIncludingGapFilling()
    {
        var view = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));

        Assert.Contains("id=\"runScope-tag-gap-fill\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"runScope-sidecars\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"runScope-quality-checks\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"runScope-folder-uniformity\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"enableFolderUniformityWorkflow\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"enableQualityChecksWorkflow\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"enableSidecarsWorkflow\"", view, StringComparison.Ordinal);
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
        Assert.Contains("EnhancementWorkflowSelection.ApplyFeatureSelection", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentDownloads_HasAUiTriggerThatSendsRecentScopeAndTargetFiles()
    {
        var view = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var script = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));

        Assert.Contains("id=\"runSelectedEnhancementSectionsRecent\"", view, StringComparison.Ordinal);
        var sidecars = view.IndexOf("<!-- Sidecars -->", StringComparison.Ordinal);
        var quality = view.IndexOf("<!-- Quality Checks -->", StringComparison.Ordinal);
        var recentButton = view.IndexOf("id=\"runSelectedEnhancementSectionsRecent\"", StringComparison.Ordinal);
        var runEnabled = view.IndexOf("id=\"runSelectedEnhancementSections\"", StringComparison.Ordinal);
        Assert.True(sidecars > 0 && recentButton > sidecars && recentButton < quality);
        Assert.True(runEnabled > quality);
        Assert.Contains("[\"tag-gap-fill\", \"sidecars\"]", script, StringComparison.Ordinal);
        var recentRunner = script.IndexOf("async function runSelectedEnhancementSectionsOnRecentDownloads", StringComparison.Ordinal);
        Assert.True(recentRunner > 0);
        var recentBody = script[recentRunner..(recentRunner + 500)];
        Assert.Contains("[\"tag-gap-fill\", \"sidecars\"]", recentBody, StringComparison.Ordinal);
        Assert.DoesNotContain("getSelectedEnhancementRunSectionIds", recentBody, StringComparison.Ordinal);
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

        foreach (var id in new[] { "tag-gap-fill", "folder-uniformity", "quality-checks", "sidecars" })
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
        var marker = "Ticked workflows run together in one job";

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

        var runBlock = segment.IndexOf("Enhancement Run Workflows", StringComparison.Ordinal);
        var gap = segment.IndexOf("<!-- Gap Filling -->", StringComparison.Ordinal);
        var sidecars = segment.IndexOf("<!-- Sidecars -->", StringComparison.Ordinal);
        var quality = segment.IndexOf("<!-- Quality Checks -->", StringComparison.Ordinal);
        var uniformity = segment.IndexOf("<!-- Folder Uniformity -->", StringComparison.Ordinal);

        Assert.True(gap > 0 && sidecars > gap);
        Assert.True(quality > sidecars);
        Assert.True(uniformity > quality);
        Assert.True(runBlock > uniformity, "the run block must render after the section cards");
    }

    [Fact]
    public void RecentDownloadsWindowLivesOnSidecarsAndArtistRenameLivesOnFolderUniformity()
    {
        var view = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var script = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var panel = view.IndexOf("id=\"autotag-stage3-panel\"", StringComparison.Ordinal);
        var platforms = view.IndexOf("TAB 6: PLATFORMS", panel, StringComparison.Ordinal);
        var segment = view[panel..platforms];

        var sidecars = segment.IndexOf("<!-- Sidecars -->", StringComparison.Ordinal);
        var quality = segment.IndexOf("<!-- Quality Checks -->", StringComparison.Ordinal);
        var uniformity = segment.IndexOf("<!-- Folder Uniformity -->", StringComparison.Ordinal);
        var recentWindow = segment.IndexOf("id=\"enhancementRecentDownloadWindowDays\"", StringComparison.Ordinal);
        var recentTime = segment.IndexOf("id=\"enhancementRecentDownloadTime\"", StringComparison.Ordinal);
        var artistRename = segment.IndexOf("id=\"enhancementRenameSpotifyArtistFolders\"", StringComparison.Ordinal);

        Assert.True(recentWindow > sidecars && recentWindow < quality);
        Assert.True(recentTime > sidecars && recentTime < quality);
        Assert.True(artistRename > uniformity);
        Assert.Contains("step=\"5\"", view, StringComparison.Ordinal);
        Assert.Contains("normalizeRecentDownloadWindowDays", script, StringComparison.Ordinal);
        Assert.Contains("MIN_RECENT_DOWNLOAD_WINDOW_DAYS = 5", script, StringComparison.Ordinal);
        Assert.Contains("DEFAULT_RECENT_DOWNLOAD_ENHANCEMENT_TIME = \"05:00\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("createRecentDownloadWindowControlsRow", script, StringComparison.Ordinal);
        Assert.DoesNotContain("createRenameSpotifyArtistFoldersControl", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ensureRecentDownloadWindowControls", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentDownloadAutomation_RunsDailyGapFillAndSidecarsAfterEnrichmentSettles()
    {
        var orchestration = ReadOrchestration();
        Assert.Contains("RunScheduledRecentDownloadEnhancementIfDueAsync", orchestration, StringComparison.Ordinal);
        Assert.Contains("GetEnabledRecentDownloadEnhancementFeatures", orchestration, StringComparison.Ordinal);
        Assert.Contains("RunIntentEnhancementRecentDownloads", orchestration, StringComparison.Ordinal);
        var start = orchestration.IndexOf("private async Task RunScheduledRecentDownloadEnhancementIfDueAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = orchestration.IndexOf("private async Task<bool> RunRecentDownloadEnhancementJobAsync", start, StringComparison.Ordinal);
        var method = orchestration[start..end];
        Assert.Contains("HasPendingPostDownloadEnrichmentAsync", method, StringComparison.Ordinal);
        Assert.Contains("HasActiveDownloadsAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("quality-checks", method, StringComparison.Ordinal);
        Assert.DoesNotContain("folder-uniformity", method, StringComparison.Ordinal);
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
    public void SelectedWorkflowsRunSequentiallyAfterGapFill()
    {
        var workflows = ReadEnhancementWorkflows();
        var start = workflows.IndexOf("private async Task RunIntegratedEnhancementWorkflowsAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = workflows.IndexOf("private async Task<bool> ApplyCompletedGapFillBatchAsync", start, StringComparison.Ordinal);
        var method = workflows[start..end];

        var sidecarIndex = method.IndexOf("EnhancementFeatureSidecars", StringComparison.Ordinal);
        var qualityIndex = method.IndexOf("EnhancementFeatureQualityChecks", StringComparison.Ordinal);
        var uniformityIndex = method.IndexOf("EnhancementFeatureFolderUniformity", StringComparison.Ordinal);
        Assert.True(sidecarIndex > 0);
        Assert.True(qualityIndex > sidecarIndex);
        Assert.True(uniformityIndex > qualityIndex);
        Assert.DoesNotContain("if (enhancementStageRan)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void GapFillDoesNotRunADeadPerBatchSectionRunner()
    {
        var workflows = ReadEnhancementWorkflows();

        Assert.DoesNotContain("ApplyEnhancementBatchSectionsAsync", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("RunEnabledEnhancementSectionsForBatchAsync", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("RunCoverMaintenanceForBatchAsync", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("RunQualityChecksForBatchAsync", workflows, StringComparison.Ordinal);
        Assert.Contains("ApplyCompletedGapFillBatchAsync", workflows, StringComparison.Ordinal);
        Assert.Contains("if (!EnhancementWorkflowSelection.IsSidecarsRunnable(enhancementRoot))", workflows, StringComparison.Ordinal);
        Assert.Contains("running opted-in sidecars.", workflows, StringComparison.Ordinal);
        Assert.Contains("sidecars lyrics lookup starting", workflows, StringComparison.Ordinal);
        Assert.Contains("IngestAndVerifyAsync(context.FilesByFolder, cancellationToken)", workflows, StringComparison.Ordinal);
        Assert.Contains("GetTrackIdsByFilePathsAsync", workflows, StringComparison.Ordinal);
        Assert.Contains("RunLyricsRefreshForBatchAsync(job, trackIds, lyricsOptions, cancellationToken)", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveTrackIdsFromPathsAsync", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("lyrics already applied during gap-fill tagging.", workflows, StringComparison.Ordinal);
        Assert.Contains("RunConfiguredSidecarsAsync(", workflows, StringComparison.Ordinal);
        Assert.Contains("RunConfiguredCoverMaintenanceAsync(", workflows, StringComparison.Ordinal);
        Assert.Contains("RunConfiguredQualityChecksAsync(", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void EndOfRunPassDoesNotRepeatWorkAlreadyDonePerBatch()
    {
        var workflows = ReadEnhancementWorkflows();

        Assert.DoesNotContain("EnhancementSectionsAppliedPerBatch", workflows, StringComparison.Ordinal);
        Assert.Contains("RunConfiguredFolderUniformityAsync(", workflows, StringComparison.Ordinal);
        Assert.Contains("RunConfiguredQualityChecksAsync(", workflows, StringComparison.Ordinal);
        Assert.Contains("RunConfiguredCoverMaintenanceAsync(", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("if (enhancementStageRan)", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderUniformityStillUsesBatchScopedTemplateApplication()
    {
        var workflows = ReadEnhancementWorkflows();

        Assert.Contains("OrganizeFilesWithReportAsync(", workflows, StringComparison.Ordinal);
        Assert.Contains("ResolveCurrentBatchFiles(successfulBatchFiles, folderReports)", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshBatchLibraryIndexAsync", workflows, StringComparison.Ordinal);
        Assert.Contains("RunConfiguredCoverMaintenanceAsync(", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void LyricsRefreshDoesNotReRunFolderUniformity()
    {
        var workflows = ReadEnhancementWorkflows();
        var lyricsStart = workflows.IndexOf("private async Task RunLyricsRefreshIfRequestedAsync", StringComparison.Ordinal);
        Assert.True(lyricsStart > 0);
        var lyricsEnd = workflows.IndexOf("private DeezSpoTagSettings BuildEnhancementLyricsSettings", lyricsStart, StringComparison.Ordinal);
        var lyricsBody = workflows[lyricsStart..lyricsEnd];
        Assert.DoesNotContain("RunFolderUniformityForBatchAsync(", lyricsBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LyricsRefreshRunsInTheSidecarStageNotQualityChecks()
    {
        var workflows = ReadEnhancementWorkflows();
        var start = workflows.IndexOf("private async Task<EnhancementWorkflowOutcome> RunConfiguredQualityChecksAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = workflows[start..(start + 3200)];

        Assert.DoesNotContain("skipLyricsRefresh", body, StringComparison.Ordinal);
        Assert.DoesNotContain("lyrics already looked up during opted-in quality checks.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RunLyricsRefreshIfRequestedAsync(", body, StringComparison.Ordinal);
        Assert.Contains("RunQualityScannerIfRequestedAsync(", body, StringComparison.Ordinal);

        var sidecarStart = workflows.IndexOf("private async Task<EnhancementWorkflowOutcome> RunConfiguredSidecarsAsync", StringComparison.Ordinal);
        Assert.True(sidecarStart > 0);
        var sidecarBody = workflows[sidecarStart..(sidecarStart + 4500)];
        Assert.Contains("RunConfiguredSidecarLyricsAsync(", sidecarBody, StringComparison.Ordinal);
        Assert.Contains("RunLyricsRefreshIfRequestedAsync(", workflows, StringComparison.Ordinal);
        Assert.Contains("if (batchFiles is not null)", sidecarBody, StringComparison.Ordinal);
        Assert.Contains("IngestAndVerifyAsync", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void GapFillJobDoesNotStartASeparateLyricsRefreshPath()
    {
        var workflows = ReadEnhancementWorkflows();
        var service = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTagService.cs"));
        var runner = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs"));
        var statusScript = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js"));

        Assert.Contains("ApplyCompletedGapFillBatchAsync(job, stage.ConfigPath, files, token)", File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTagService.cs")), StringComparison.Ordinal);
        var applyBatch = workflows.IndexOf("private async Task<bool> ApplyCompletedGapFillBatchAsync", StringComparison.Ordinal);
        Assert.True(applyBatch > 0);
        var applyBody = workflows[applyBatch..(applyBatch + 2500)];
        Assert.Contains("IsSidecarsRunnable", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("IsQualityChecksRunnable", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("IsFolderUniformityRunnable", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("skipLyricsRefresh", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("\"lyrics-refresh\"", workflows, StringComparison.Ordinal);
        Assert.Contains("AutoTagLiterals.EnhancementPhaseSidecarsLyrics", workflows, StringComparison.Ordinal);
        Assert.Contains("gap-fill will use the selected folder.", workflows, StringComparison.Ordinal);
        Assert.Contains("if (missingTargets.Count > 0)", workflows, StringComparison.Ordinal);
        Assert.Contains("Where(platform => !IsLyricsProviderPlatform(platform))", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadEnhancementLyricsWork", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyEnhancementTtmlCleanup", runner, StringComparison.Ordinal);
        Assert.Contains("function isSidecarPlatform(value)", statusScript, StringComparison.Ordinal);
        Assert.Contains("platform === \"sidecars-lyrics\"", statusScript, StringComparison.Ordinal);
        Assert.Contains("platform === \"lyrics-refresh\"", statusScript, StringComparison.Ordinal);
        Assert.Contains("platform === \"cover-maintenance\"", statusScript, StringComparison.Ordinal);
        Assert.Contains("const platformLabel = formatEnhancementFeature(platform);", statusScript, StringComparison.Ordinal);
        Assert.DoesNotContain("return platform.includes(\"lyrics\") || platform.includes(\"cover-maintenance\");", statusScript, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverMaintenanceResultCarriesAlbumLevelArtworkOutcomes()
    {
        var source = File.ReadAllText(Path.Join(
            FindEnhancementRepoRoot(), "DeezSpoTag.Web", "Services", "CoverPort", "CoverLibraryMaintenanceService.cs"));

        Assert.Contains("public sealed record CoverAlbumMaintenanceOutcome", source, StringComparison.Ordinal);
        Assert.Contains("bool AnimatedArtworkSaved", source, StringComparison.Ordinal);
        Assert.Contains("bool HasAnimatedArtwork", source, StringComparison.Ordinal);
        Assert.Contains("string? CoverPath", source, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<string>? AudioFilePaths", source, StringComparison.Ordinal);
        Assert.Contains("bool WriteEmbeddedCover", source, StringComparison.Ordinal);
        Assert.Contains("bool WriteExternalSidecar", source, StringComparison.Ordinal);
        Assert.Contains("bool UseShazamForUntaggedFiles", source, StringComparison.Ordinal);
        Assert.Contains("TryRecognizeUntaggedAlbumAsync", source, StringComparison.Ordinal);
        Assert.Contains("onAlbumCompleted", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref completedAlbums)", source, StringComparison.Ordinal);
        Assert.Contains("if (context.Request.WriteExternalSidecar)", source, StringComparison.Ordinal);
        Assert.Contains("if (context.Request.WriteEmbeddedCover)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("|| !context.ArtworkState.HasExternal", source, StringComparison.Ordinal);
        Assert.Contains("AlbumResults: albumResults", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SidecarWorkflowRunsLyricsAndCoverMaintenanceTogether()
    {
        var workflows = ReadEnhancementWorkflows();
        var start = workflows.IndexOf(
            "private async Task<EnhancementWorkflowOutcome> RunConfiguredSidecarsAsync",
            StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = workflows[start..(start + 4200)];

        Assert.Contains("var sidecarTasks = new List<Task<EnhancementWorkflowOutcome>>();", body, StringComparison.Ordinal);
        Assert.Contains("sidecarTasks.Add(RunConfiguredSidecarLyricsAsync(", body, StringComparison.Ordinal);
        Assert.Contains("sidecarTasks.Add(RunConfiguredCoverMaintenanceAsync(", body, StringComparison.Ordinal);
        Assert.Contains("var outcomes = await Task.WhenAll(sidecarTasks);", body, StringComparison.Ordinal);
        Assert.DoesNotContain("await RunLyricsRefreshForBatchAsync(job, trackIds, lyricsOptions, cancellationToken);\n            if (runCovers)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnimatedArtworkBadgesAreEmittedForAlbumFilesWithoutInflatingCounts()
    {
        var workflows = ReadEnhancementWorkflows();

        Assert.Contains("bool countOutcome = true", workflows, StringComparison.Ordinal);
        Assert.Contains("if (countOutcome)", workflows, StringComparison.Ordinal);
        Assert.Contains("album.AudioFilePaths is { Count: > 0 }", workflows, StringComparison.Ordinal);
        Assert.Contains("foreach (var audioPath in album.AudioFilePaths)", workflows, StringComparison.Ordinal);
        Assert.Contains("countOutcome: false", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void AnimatedArtworkAppliesStillArtworkFromTheSameAppleSource()
    {
        var source = File.ReadAllText(Path.Join(
            FindEnhancementRepoRoot(), "DeezSpoTag.Web", "Services", "CoverPort", "CoverLibraryMaintenanceService.cs"));

        Assert.Contains("private readonly record struct AnimatedArtworkUpdateResult", source, StringComparison.Ordinal);
        Assert.Contains("bool MatchingStillApplied", source, StringComparison.Ordinal);
        Assert.Contains("TryApplyMatchingAppleStillArtworkAsync", source, StringComparison.Ordinal);
        Assert.Contains("updatedAnything = animatedResult.AnimatedSaved || animatedResult.MatchingStillApplied || updatedAnything;", source, StringComparison.Ordinal);
        Assert.Contains("if (workPlan.RequiresStillCoverUpdate && !animatedResult.AnimatedSaved)", source, StringComparison.Ordinal);
        Assert.Contains("if (request.WriteExternalSidecar)", source, StringComparison.Ordinal);
        Assert.Contains("if (request.WriteEmbeddedCover)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (workPlan.RequiresStillCoverUpdate && (!request.QueueAnimatedArtwork || !animatedSaved))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusCarriesAnimatedArtworkBadges()
    {
        var statusSource = File.ReadAllText(Path.Join(
            FindEnhancementRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTagService.cs"));
        var runnerSource = File.ReadAllText(Path.Join(
            FindEnhancementRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs"));
        var historySource = File.ReadAllText(Path.Join(
            FindEnhancementRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js"));

        Assert.Contains("public List<string> ArtworkBadges", statusSource, StringComparison.Ordinal);
        Assert.Contains("ArtworkBadges = ResolveAnimatedArtworkBadges(context.File, context.Plan.Settings)", runnerSource, StringComparison.Ordinal);
        Assert.Contains("AnimatedArtworkNaming.IsAlbumAnimatedArtworkSidecar", runnerSource, StringComparison.Ordinal);
        Assert.Contains("artworkBadgeMarkup", historySource, StringComparison.Ordinal);
        Assert.Contains("renderLyricsCards(allRows.filter(isSidecarHistoryRow))", historySource, StringComparison.Ordinal);
        Assert.DoesNotContain("collectSidecarRows", historySource, StringComparison.Ordinal);
        Assert.DoesNotContain("${usedShazam}${message}${artworkBadgeHtml}", historySource, StringComparison.Ordinal);
        Assert.Contains("resolveSidecarCoverUrl", historySource, StringComparison.Ordinal);
        Assert.Contains("isSidecarHistoryRow", historySource, StringComparison.Ordinal);
        Assert.Contains("cover-maintenance", historySource, StringComparison.Ordinal);
        Assert.Contains("setHistoryView(\"sidecar\")", historySource, StringComparison.Ordinal);
        Assert.Contains("setHistoryView(\"tags\")", historySource, StringComparison.Ordinal);
    }

    [Fact]
    public void SidecarCoverAndLaterPhasesKeepLiveStatusWithoutResettingOverallProgress()
    {
        var workflows = ReadEnhancementWorkflows();
        var coverService = File.ReadAllText(Path.Join(
            FindEnhancementRepoRoot(), "DeezSpoTag.Web", "Services", "CoverPort", "CoverLibraryMaintenanceService.cs"));
        var autoTagService = File.ReadAllText(Path.Join(
            FindEnhancementRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.Contains("job.ProcessedItems = Math.Max(job.ProcessedItems, Math.Max(0, processed));", workflows, StringComparison.Ordinal);
        Assert.Contains("job.TotalItems = job.TargetUsable > 0", workflows, StringComparison.Ordinal);
        Assert.Contains("PublishEnhancementPhaseHeartbeat", workflows, StringComparison.Ordinal);
        Assert.Contains("cover maintenance starting", workflows, StringComparison.Ordinal);
        Assert.Contains("quality checks starting", workflows, StringComparison.Ordinal);
        Assert.Contains("folder uniformity starting", workflows, StringComparison.Ordinal);
        Assert.Contains("onAlbumCompleted", coverService, StringComparison.Ordinal);
        Assert.Contains("lock (job)", workflows, StringComparison.Ordinal);
        Assert.Contains("updateTrackIndex: false", autoTagService, StringComparison.Ordinal);
        Assert.DoesNotContain("job.TotalItems = job.TargetUsable;\n        }", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchMediaRefreshUsesTheOutbox()
    {
        var workflows = ReadEnhancementWorkflows();

        Assert.DoesNotContain("EnqueueBatchMediaServerRefreshAsync", workflows, StringComparison.Ordinal);
        Assert.Contains("EnqueueMediaRefreshForBatchAsync", workflows, StringComparison.Ordinal);
        var service = File.ReadAllText(Path.Join(FindEnhancementRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTagService.cs"));
        Assert.Contains("TriggerConfiguredMediaServerRefreshAfterEnhancementAsync", service, StringComparison.Ordinal);
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
