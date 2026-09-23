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

public sealed class EnhancementMultiSectionRunTest
{
    [Fact]
    public void EnhancementJobSelection_RoundTripsThroughPersistenceJson()
    {
        var job = new AutoTagJob
        {
            SelectedEnhancementFeatures = ["tag-gap-fill", "folder-uniformity"],
            FolderUniformityRunMode = "batch-scoped",
            EnhancementBatchState = new AutoTagEnhancementBatchState
            {
                NextBatchIndex = 2,
                BatchCount = 4,
                Pending = new AutoTagPendingEnhancementBatch
                {
                    BatchNumber = 3,
                    BatchCount = 4,
                    OriginalPaths = ["/music/A/Album/01.flac"],
                    CurrentPaths = ["/music/A/Album/01.flac"],
                    CompletedFeatures = ["tag-gap-fill", "sidecars"]
                }
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(job);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AutoTagJob>(json)!;

        Assert.Equal(["tag-gap-fill", "folder-uniformity"], restored.SelectedEnhancementFeatures);
        Assert.Equal("batch-scoped", restored.FolderUniformityRunMode);
        Assert.Equal(2, restored.EnhancementBatchState.NextBatchIndex);
        Assert.Equal(3, restored.EnhancementBatchState.Pending?.BatchNumber);
        Assert.Equal(["tag-gap-fill", "sidecars"], restored.EnhancementBatchState.Pending?.CompletedFeatures);
    }

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
    public void MissingCoreMetadataAudit_PreparesFromTheChecksMarkerNotTheLegacyFlag()
    {
        var source = ReadWorkflows();
        var start = source.IndexOf("internal static bool ShouldNarrowToMissingCoreMetadata", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = source.IndexOf("private async Task<EnhancementRunManifest> BuildEnhancementRunManifestAsync", start, StringComparison.Ordinal);
        var method = source[start..end];

        // The gate is the run marker plus the audit toggle; the stored enabled flag is still
        // written as the marker but is no longer consulted to decide the run's scope.
        Assert.Contains("isChecksRun", method, StringComparison.Ordinal);
        Assert.Contains("ReadBool(qualityChecks, \"flagMissingTags\") == true", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadBool(qualityChecks, EnabledField) == true", method, StringComparison.Ordinal);
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
    public void MissingCoreMetadataNarrowing_IsGatedOnTheChecksRunMarkerAndTheAuditToggle()
    {
        var source = ReadWorkflows();

        Assert.Contains(
            "ShouldNarrowToMissingCoreMetadata(IsChecksRun(job), enhancementRoot)",
            source,
            StringComparison.Ordinal);
        // The legacy "enabled" gate and the old "nothing else selected" contract are gone.
        Assert.DoesNotContain("IsMissingCoreMetadataOnlyScan", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldPrepareMissingCoreMetadataTargets", source, StringComparison.Ordinal);
        // The audited files define the run's scope, so the priority-wave write is gone too.
        Assert.DoesNotContain(
            "WriteStringList(root, AutoTagLiterals.PriorityTargetFilesKey, priorityPaths);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksRunWithTheMissingMetadataAudit_NarrowsTheRunTargets()
    {
        var enhancement = BuildEnhancementRoot(flagMissingTags: true);

        Assert.True(AutoTagService.ShouldNarrowToMissingCoreMetadata(true, enhancement));
    }

    [Fact]
    public void ChecksRunNarrowing_SurvivesEveryOtherCheckAndSectionBeingOn()
    {
        // The profile decides what is switched on, and the audit's file list stays the run's
        // scope: ticking a second check must not widen the run back to the whole library.
        var enhancement = BuildEnhancementRoot(flagMissingTags: true, flagDuplicates: true);
        enhancement["gapFilling"] = new JsonObject { ["enabled"] = true };
        enhancement["sidecars"] = new JsonObject { ["enabled"] = true, ["queueLyricsRefresh"] = true };
        enhancement["folderUniformity"] = new JsonObject { ["enabled"] = true, ["enforceFolderStructure"] = true };
        enhancement["coverMaintenance"] = new JsonObject { ["enabled"] = true, ["replaceMissingEmbeddedCovers"] = true };

        Assert.True(AutoTagService.ShouldNarrowToMissingCoreMetadata(true, enhancement));
    }

    [Fact]
    public void NarrowingIsRejectedWithoutTheChecksRunMarker()
    {
        // A stored qualityChecks.enabled no longer narrows anything on its own.
        var enhancement = BuildEnhancementRoot(flagMissingTags: true);

        Assert.False(AutoTagService.ShouldNarrowToMissingCoreMetadata(false, enhancement));
    }

    [Fact]
    public void NarrowingIsRejectedWhenTheAuditToggleIsOff()
    {
        var enhancement = BuildEnhancementRoot(flagMissingTags: false);

        Assert.False(AutoTagService.ShouldNarrowToMissingCoreMetadata(true, enhancement));
    }

    [Fact]
    public void PriorityTargetsFeedTheSharedBatchOrderingNotSeparateBatches()
    {
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var planner = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "AutoTag", "EnhancementBatchPlanner.cs"));

        // The two-wave order consumes the audited priority paths...
        Assert.Contains("BuildNormalizedPathSet(config.PriorityTargetFiles)", runner, StringComparison.Ordinal);
        // ...then plan.Files (wave 1 ++ wave 2) is cut into ≤40-file ranges, so
        // priority files occupy slots inside the normal batches — never extra batches.
        Assert.Contains("plan.Files.Clear();", runner, StringComparison.Ordinal);
        Assert.Contains(
            "BuildLibraryWideEnhancementBatchRanges(plan.Files, passFileCount, batchSize)",
            runner,
            StringComparison.Ordinal);
        // Batches stay capped at 40 except when finishing the active album.
        Assert.Contains("end - start < resolvedBatchSize", planner, StringComparison.Ordinal);
        Assert.Contains("SameAlbumDirectory(orderedFiles[end - 1], orderedFiles[end])", planner, StringComparison.Ordinal);
    }

    private static JsonObject BuildEnhancementRoot(
        bool flagMissingTags,
        bool flagDuplicates = false)
        => new JsonObject
        {
            ["qualityChecks"] = new JsonObject
            {
                ["enabled"] = true,
                ["flagMissingTags"] = flagMissingTags,
                ["flagDuplicates"] = flagDuplicates
            }
        };

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
        Assert.Contains("id=\"runScope-folder-uniformity\"", view, StringComparison.Ordinal);
        // Quality Checks runs independently, so it has no run-scope tick.
        Assert.DoesNotContain("id=\"runScope-quality-checks\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"enableFolderUniformityWorkflow\"", view, StringComparison.Ordinal);
        // Quality Checks is manual-only: it has no scheduled-enhancement tick.
        Assert.DoesNotContain("id=\"enableQualityChecksWorkflow\"", view, StringComparison.Ordinal);
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

        foreach (var id in new[] { "tag-gap-fill", "folder-uniformity", "sidecars" })
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
        var uniformity = segment.IndexOf("<!-- Folder Uniformity -->", StringComparison.Ordinal);
        var quality = segment.IndexOf("<!-- Quality Checks -->", StringComparison.Ordinal);

        Assert.True(gap > 0 && sidecars > gap);
        // Folder Uniformity and Quality Checks were interchanged, putting Quality Checks last.
        Assert.True(uniformity > sidecars, "Folder Uniformity now follows Sidecars");
        Assert.True(quality > uniformity, "Quality Checks is now the last section");
        Assert.True(runBlock > quality, "the run block must render after the section cards");
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
        var end = workflows.IndexOf("private static bool IsQualityChecksOnlyRun", start, StringComparison.Ordinal);
        var method = workflows[start..end];

        Assert.Contains("RunWorkflowOnlyEnhancementBatchesAsync", method, StringComparison.Ordinal);
        Assert.Contains("FolderUniformityModeLibraryWide", method, StringComparison.Ordinal);
        Assert.DoesNotContain("if (enhancementStageRan)", method, StringComparison.Ordinal);
    }

    /// <summary>
    /// Quality Checks is not part of the combined enhancement run: it only inspects and flags, so
    /// it must not be executed from the integrated multi-section sequence.
    /// </summary>
    [Fact]
    public void QualityChecksDoesNotJoinTheCombinedEnhancementSequence()
    {
        var workflows = ReadEnhancementWorkflows();
        var start = workflows.IndexOf("private async Task RunIntegratedEnhancementWorkflowsAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = workflows.IndexOf("private static bool IsQualityChecksOnlyRun", start, StringComparison.Ordinal);
        var combined = workflows[start..end];

        // The combined sequence runs Folder Uniformity, and the block that runs the checks is
        // gated behind the dedicated quality-checks-only job marker rather than joining the run.
        Assert.Contains("EnhancementFeatureFolderUniformity", combined, StringComparison.Ordinal);
        Assert.Contains("IsQualityChecksOnlyRun(job)", combined, StringComparison.Ordinal);
        Assert.Contains("RunQualityChecksOnlyAsync(", combined, StringComparison.Ordinal);

        // No unguarded execution of the checks remains in the combined sequence: every occurrence
        // of the checks runner sits inside the dedicated path.
        var runnerCalls = 0;
        var searchFrom = 0;
        while (true)
        {
            var at = combined.IndexOf("RunConfiguredQualityChecksAsync(", searchFrom, StringComparison.Ordinal);
            if (at < 0)
            {
                break;
            }

            runnerCalls++;
            searchFrom = at + 1;
        }

        Assert.Equal(0, runnerCalls);

        // The checks keep their own entry point instead.
        var qualityStart = workflows.IndexOf("private async Task RunQualityChecksOnlyAsync", StringComparison.Ordinal);
        Assert.True(qualityStart > 0, "Quality Checks needs its own execution path");
        var qualityEnd = workflows.IndexOf("private async Task RunCoordinatedEnhancementBatchAsync", qualityStart, StringComparison.Ordinal);
        var qualityOnly = workflows[qualityStart..qualityEnd];
        Assert.Contains("RunConfiguredQualityChecksAsync", qualityOnly, StringComparison.Ordinal);
        // Manual-only: runnability comes from the checks being configured, not the legacy
        // "enabled" schedule flag.
        Assert.Contains("HasConfiguredQualityChecks", qualityOnly, StringComparison.Ordinal);
    }

    /// <summary>
    /// The combined-run declaration and the UI run-scope list must both exclude Quality Checks,
    /// otherwise the section creeps back into a combined run.
    /// </summary>
    [Fact]
    public void QualityChecksIsExcludedFromTheCombinedRunDeclarationAndRunScope()
    {
        var selection = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "EnhancementWorkflowSelection.cs"));
        var orderedStart = selection.IndexOf("OrderedFeatures", StringComparison.Ordinal);
        Assert.True(orderedStart > 0);
        var orderedBlock = selection[orderedStart..selection.IndexOf("];", orderedStart, StringComparison.Ordinal)];
        Assert.Contains("GapFill", orderedBlock, StringComparison.Ordinal);
        Assert.Contains("Sidecars", orderedBlock, StringComparison.Ordinal);
        Assert.Contains("FolderUniformity", orderedBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("QualityChecks", orderedBlock, StringComparison.Ordinal);

        var script = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var runScopeStart = script.IndexOf("const ENHANCEMENT_RUN_SECTION_IDS", StringComparison.Ordinal);
        Assert.True(runScopeStart > 0);
        var runScopeLine = script[runScopeStart..script.IndexOf(';', runScopeStart)];
        Assert.DoesNotContain("quality-checks", runScopeLine, StringComparison.Ordinal);
        Assert.Contains("folder-uniformity", runScopeLine, StringComparison.Ordinal);
    }

    [Fact]
    public void GapFillDoesNotRunADeadPerBatchSectionRunner()
    {
        var workflows = ReadEnhancementWorkflows();

        Assert.DoesNotContain("ApplyEnhancementBatchSectionsAsync", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("RunEnabledEnhancementSectionsForBatchAsync", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("RunCoverMaintenanceForBatchAsync", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("RunQualityChecksForBatchAsync", workflows, StringComparison.Ordinal);
        Assert.Contains("RunCoordinatedEnhancementBatchAsync", workflows, StringComparison.Ordinal);
        Assert.Contains("FolderUniformityModeBatchScoped", workflows, StringComparison.Ordinal);
        Assert.Contains("running Sidecars", workflows, StringComparison.Ordinal);
        Assert.Contains("running Folder Uniformity", workflows, StringComparison.Ordinal);
        Assert.Contains("sidecars lyrics lookup starting", workflows, StringComparison.Ordinal);
        Assert.Contains("IngestAndVerifyAsync(context.FilesByFolder, cancellationToken)", workflows, StringComparison.Ordinal);
        Assert.Contains("GetTrackIdsByFilePathsAsync", workflows, StringComparison.Ordinal);
        Assert.Contains("RunLyricsRefreshForBatchAsync(", workflows, StringComparison.Ordinal);
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
        var sidecarBody = workflows[sidecarStart..(sidecarStart + 14000)];
        Assert.Contains("RunLyricsRefreshForBatchAsync(", sidecarBody, StringComparison.Ordinal);
        Assert.Contains("RunLyricsRefreshIfRequestedAsync(", workflows, StringComparison.Ordinal);
        Assert.Contains("ResolveSidecarRunFilesAsync(", sidecarBody, StringComparison.Ordinal);
        Assert.Contains("IngestAndVerifyAsync", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void GapFillJobDoesNotStartASeparateLyricsRefreshPath()
    {
        var workflows = ReadEnhancementWorkflows();
        var service = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var statusScript = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js"));

        Assert.Contains("RunCoordinatedEnhancementBatchAsync(job, stage.ConfigPath, batch, token)", PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTagService.cs"), StringComparison.Ordinal);
        var applyBatch = workflows.IndexOf("private async Task RunCoordinatedEnhancementBatchAsync", StringComparison.Ordinal);
        Assert.True(applyBatch > 0);
        var applyBody = workflows[applyBatch..Math.Min(workflows.Length, applyBatch + 7000)];
        Assert.Contains("EnhancementWorkflowSelection.Sidecars", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("IsQualityChecksRunnable", applyBody, StringComparison.Ordinal);
        Assert.Contains("FolderUniformityModeBatchScoped", applyBody, StringComparison.Ordinal);
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
        Assert.Contains("if (shouldWriteSidecars)", source, StringComparison.Ordinal);
        Assert.Contains("if (shouldWriteEmbedded)", source, StringComparison.Ordinal);
        Assert.Contains("context.WorkPlan.NeedsExternal || externalNeedsUpgrade", source, StringComparison.Ordinal);
        Assert.Contains("context.WorkPlan.NeedsEmbedded || embeddedNeedsUpgrade", source, StringComparison.Ordinal);
        Assert.DoesNotContain("|| !context.ArtworkState.HasExternal", source, StringComparison.Ordinal);
        Assert.Contains("AlbumResults: albumResults", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SidecarsRunOneFileAtATimeWithAlbumArtworkFetchedOnce()
    {
        var workflows = ReadEnhancementWorkflows();
        var start = workflows.IndexOf(
            "private async Task<EnhancementWorkflowOutcome> RunConfiguredSidecarsAsync",
            StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = workflows[start..(start + 14000)];

        // One ordered pass over the files, the way downloads are processed: the
        // parallel lyrics/cover passes are gone.
        Assert.DoesNotContain("var sidecarTasks = new List<Task<EnhancementWorkflowOutcome>>();", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAll(sidecarTasks)", body, StringComparison.Ordinal);
        Assert.Contains("foreach (var filePath in orderedRun)", body, StringComparison.Ordinal);
        Assert.Contains("RunConfiguredCoverMaintenanceAsync(", body, StringComparison.Ordinal);
        Assert.Contains("RunLyricsRefreshForBatchAsync(", body, StringComparison.Ordinal);

        // The first file of an album fetches the artwork; every file handles its own lyrics.
        // The once-per-album ownership rule lives in the explicit plan the loop resolves.
        Assert.Contains("var fetchScope = sidecarRunPlan.Resolve(filePath, trackId);", body, StringComparison.Ordinal);
        Assert.Contains("var ownsAlbumArtwork = fetchScope.OwnsAlbumArtwork;", body, StringComparison.Ordinal);
        Assert.Contains("var handlesLyrics = fetchScope.HandlesLyrics;", body, StringComparison.Ordinal);
        var planStart = workflows.IndexOf("internal sealed class SidecarFetchPlan", StringComparison.Ordinal);
        Assert.True(planStart > 0);
        var planBody = workflows[planStart..(planStart + 1500)];
        Assert.Contains("_runCovers && _artworkAlbums.Add(ResolveSidecarAlbumKey(filePath))", planBody, StringComparison.Ordinal);
        Assert.Contains("_runLyrics && trackId > 0", planBody, StringComparison.Ordinal);

        // Detection happens first: what the file actually still needs decides both the message
        // and whether the pass does any network work at all.
        Assert.Contains("var coverPlan = ownsAlbumArtwork", body, StringComparison.Ordinal);
        Assert.Contains("PlanConfiguredCoverMaintenanceAsync(", body, StringComparison.Ordinal);
        Assert.Contains("_lyricsRefreshQueueService.PlanTrackRefreshAsync(", body, StringComparison.Ordinal);
        Assert.Contains(
            "var needs = ResolveSidecarFileNeeds(ownsAlbumArtwork, coverPlan, handlesLyrics, lyricsPlan);",
            body,
            StringComparison.Ordinal);

        // A fully-populated file is skipped at once and reported skipped, not failed.
        Assert.Contains("if (needs.IsAlreadyComplete)", body, StringComparison.Ordinal);
        Assert.Contains("RecordSidecarFetchSkipped(", body, StringComparison.Ordinal);

        // The card names exactly the remaining work for this file, and shrinks after artwork.
        Assert.Contains("RecordSidecarRemainingWork(", body, StringComparison.Ordinal);
        Assert.Contains("needs.RequiresLyricsStep", body, StringComparison.Ordinal);
        Assert.Contains("RecordSidecarNoLyricsAsync(", workflows, StringComparison.Ordinal);
        Assert.Contains("SidecarFetchActivity.DescribeEnhancement", workflows, StringComparison.Ordinal);
        Assert.Contains("DescribeSidecarFetchProgress(", workflows, StringComparison.Ordinal);
        Assert.Contains("ResolveSidecarCardStateAsync(", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("(file {position}: {name})", workflows, StringComparison.Ordinal);
        Assert.Contains("onAlbumCompleted: suppressFetchActivity", workflows, StringComparison.Ordinal);
        Assert.Contains("HasLocalOnlyLyricsWork", workflows, StringComparison.Ordinal);

        // The network wait is bounded per file, the disk write gets its own budget, and a
        // lyrics check that does not finish is recorded as unverified rather than as absence.
        Assert.Contains("new SidecarFetchBudget(", body, StringComparison.Ordinal);
        Assert.Contains("SidecarArtworkNetworkTimeout", body, StringComparison.Ordinal);
        Assert.Contains("onWritePhaseStarted: artworkBudget.BeginWrite", body, StringComparison.Ordinal);
        Assert.Contains("onWritePhaseStarted: lyricsBudget.BeginWrite", body, StringComparison.Ordinal);
        Assert.Contains("RecordSidecarLyricsUnverified(", body, StringComparison.Ordinal);
        Assert.Contains("ClassifyLyricsOutcome(", body, StringComparison.Ordinal);
        Assert.Contains("DescribeSidecarCompletion(counters)", body, StringComparison.Ordinal);

        // Artist artwork is processed and updated on its own path, never here.
        Assert.DoesNotContain("PlanArtistRefreshAsync", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshArtistNowAsync", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverMaintenanceWithoutAStillArtworkSource_IsSkippedWhenStillArtworkWasRequested()
    {
        var shouldSkip = AutoTagService.ShouldSkipCoverMaintenanceForMissingStillArtworkSource(
            replaceMissingEmbedded: true,
            syncExternalCovers: true,
            upgradeLowResolution: true,
            enabledSourceCount: 0);

        Assert.True(shouldSkip);
    }

    [Fact]
    public void AnimatedArtworkBadgeIsCarriedByOneRepresentativeTrackPerAlbum()
    {
        var workflows = ReadEnhancementWorkflows();

        // One record per album, attributed to a representative track so the
        // sidecar tab merges it with that track's lyrics card. Artwork must
        // not be emitted for every track of the album, and album titles must
        // not be used as the card title.
        Assert.Contains("bool countOutcome = true", workflows, StringComparison.Ordinal);
        Assert.Contains("if (countOutcome)", workflows, StringComparison.Ordinal);
        Assert.Contains("ResolveAlbumRepresentativeTracksAsync(", workflows, StringComparison.Ordinal);
        Assert.Contains("long? representativeTrackId = representative is { TrackId: > 0 } ? representative.TrackId : null;", workflows, StringComparison.Ordinal);
        Assert.Contains("trackId: representativeTrackId);", workflows, StringComparison.Ordinal);
        Assert.Contains("sourceTitle: representative?.Title,", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var audioPath in album.AudioFilePaths)", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceTitle: album.Album,", workflows, StringComparison.Ordinal);
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
        Assert.Contains("if (shouldWriteSidecars)", source, StringComparison.Ordinal);
        Assert.Contains("if (shouldWriteEmbedded)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (workPlan.RequiresStillCoverUpdate && (!request.QueueAnimatedArtwork || !animatedSaved))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatusCarriesAnimatedArtworkBadges()
    {
        var statusSource = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var runnerSource = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var historySource = File.ReadAllText(Path.Join(
            FindEnhancementRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js"));

        Assert.Contains("public List<string> ArtworkBadges", statusSource, StringComparison.Ordinal);
        Assert.Contains("ArtworkBadges = ResolveAnimatedArtworkBadges(context.File, context.Plan.Settings)", runnerSource, StringComparison.Ordinal);
        Assert.Contains("AnimatedArtworkNaming.IsAlbumAnimatedArtworkSidecar", runnerSource, StringComparison.Ordinal);
        Assert.Contains("artworkBadgeMarkup", historySource, StringComparison.Ordinal);
        Assert.Contains("renderLyricsCards(allRows.filter(isSidecarHistoryRow))", historySource, StringComparison.Ordinal);
        Assert.Contains("task-activity", historySource, StringComparison.Ordinal);
        Assert.Contains("activityState", historySource, StringComparison.Ordinal);
        Assert.Contains("resolveSidecarFetchingActivity", historySource, StringComparison.Ordinal);
        Assert.Contains("findActiveSidecarKey", historySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Fetching lyrics…", historySource, StringComparison.Ordinal);
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
        var autoTagService = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        Assert.Contains("job.ProcessedItems = Math.Max(job.ProcessedItems, Math.Max(0, processed));", workflows, StringComparison.Ordinal);
        Assert.Contains("job.TotalItems = job.TargetUsable > 0", workflows, StringComparison.Ordinal);
        Assert.Contains("PublishEnhancementPhaseHeartbeat", workflows, StringComparison.Ordinal);
        Assert.Contains("PublishEnhancementPhaseHeartbeat(job, AutoTagLiterals.EnhancementFeatureSidecars, message)", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("job.RootPath ?? string.Empty,\n            AutoTagLiterals.CompletedStatus", workflows, StringComparison.Ordinal);
        Assert.Contains("cover maintenance starting", workflows, StringComparison.Ordinal);
        Assert.Contains("quality checks starting", workflows, StringComparison.Ordinal);
        Assert.Contains("folder uniformity starting", workflows, StringComparison.Ordinal);
        Assert.Contains("onAlbumCompleted", coverService, StringComparison.Ordinal);
        Assert.Contains("onAlbumFetchStarted", coverService, StringComparison.Ordinal);
        Assert.Contains("AutoTagLiterals.RecoveryTrigger", workflows, StringComparison.Ordinal);
        Assert.Contains("SidecarFetchActivity.Describe", workflows, StringComparison.Ordinal);
        Assert.Contains("PlanTrackRefreshAsync", workflows, StringComparison.Ordinal);
        Assert.Contains("activityState: \"fetchingSidecars\"", workflows, StringComparison.Ordinal);
        Assert.Contains("_coverMaintenanceService.PlanAsync", workflows, StringComparison.Ordinal);
        Assert.Contains("lock (job)", workflows, StringComparison.Ordinal);
        Assert.Contains("updateTrackIndex: false", autoTagService, StringComparison.Ordinal);
        Assert.DoesNotContain("job.TotalItems = job.TargetUsable;\n        }", workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchMediaRefreshUsesTheOutbox()
    {
        var workflows = ReadEnhancementWorkflows();

        Assert.DoesNotContain("EnqueueBatchMediaServerRefreshAsync", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("EnqueueMediaRefreshForBatchAsync", workflows, StringComparison.Ordinal);
        var ingest = File.ReadAllText(Path.Join(FindEnhancementRepoRoot(), "DeezSpoTag.Web", "Services", "KnownLibraryFileIngestionService.cs"));
        Assert.Contains("EnqueueTargetIdentityRefreshAsync(", ingest, StringComparison.Ordinal);
        var service = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
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

    [Fact]
    public void MultiSectionEnhancementRun_ReusesTheSharedIdentityBoundary()
    {
        var service = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        Assert.Contains("await _autoTagRunner.RunAsync(", service, StringComparison.Ordinal);

        var workflows = PartialSourceReader.ReadTypeSource(
            "DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs");
        Assert.DoesNotContain("ProviderIdentityField", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteProviderIdentityAsync", workflows, StringComparison.Ordinal);
    }
}
