using System.Text.Json.Nodes;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EnhancementWorkflowSelectionTests
{
    [Fact]
    public void FolderUniformityOnly_ClearsGapFillTags()
    {
        var config = Parse("""
            {
              "gapFillTags": ["title", "artist"],
              "enhancement": {
                "folderUniformity": { "enabled": true },
                "coverMaintenance": { "enabled": true },
                "qualityChecks": { "enabled": true, "flagMissingTags": true }
              }
            }
            """);

        EnhancementWorkflowSelection.ApplyFeatureSelection(
            config,
            ["folder-uniformity"],
            [7]);

        var enhancement = config["enhancement"]!.AsObject();
        Assert.True(enhancement["folderUniformity"]!["enabled"]!.GetValue<bool>());
        Assert.False(enhancement["coverMaintenance"]!["enabled"]!.GetValue<bool>());
        Assert.False(enhancement["qualityChecks"]!["enabled"]!.GetValue<bool>());
        Assert.Empty(config["gapFillTags"]!.AsArray());
    }

    [Fact]
    public void QualityChecksOnly_DoesNotKeepGapFillBecauseOfFlagMissingTags()
    {
        var config = Parse("""
            {
              "gapFillTags": ["title", "artist", "album"],
              "enhancement": {
                "folderUniformity": { "enabled": true },
                "coverMaintenance": { "enabled": true },
                "qualityChecks": { "enabled": true, "flagMissingTags": true }
              }
            }
            """);
        var request = new AutoTagEnhancementStartRequest { Features = ["quality-checks"] };

        AutoTagJobsController.ApplyEnhancementRunSelection(
            config,
            request,
            [7],
            ["/tmp/music/track.flac"]);

        Assert.False(config["enhancement"]!["folderUniformity"]!["enabled"]!.GetValue<bool>());
        Assert.True(config["enhancement"]!["qualityChecks"]!["enabled"]!.GetValue<bool>());
        Assert.Empty(config["gapFillTags"]!.AsArray());
        Assert.Equal("/tmp/music/track.flac", config["targetFiles"]![0]!.GetValue<string>());
    }

    [Fact]
    public void HasExplicitCoverActions_IgnoresRenameDefaultAndShazamOnly()
    {
        var enhancement = Parse("""
            {
              "sidecars": { "enabled": true },
              "coverMaintenance": {
                "renameExistingAnimatedArtwork": true,
                "useShazamForUntaggedFiles": true
              }
            }
            """);
        Assert.False(EnhancementWorkflowSelection.HasExplicitCoverActions(enhancement));
        Assert.False(EnhancementWorkflowSelection.IsSidecarsRunnable(enhancement));

        enhancement["coverMaintenance"]!["overwriteExistingAnimatedArtwork"] = true;
        Assert.True(EnhancementWorkflowSelection.HasExplicitCoverActions(enhancement));
        Assert.True(EnhancementWorkflowSelection.IsSidecarsRunnable(enhancement));
        enhancement["coverMaintenance"]!["overwriteExistingAnimatedArtwork"] = false;
        enhancement["coverMaintenance"]!["removeOldAnimatedArtwork"] = true;
        Assert.True(EnhancementWorkflowSelection.HasExplicitCoverActions(enhancement));
    }

    [Fact]
    public void Sidecars_TtmlCleanupFlagsAreRunnableWithoutCoverActions()
    {
        var enhancement = Parse("""
            {
              "sidecars": {
                "enabled": true,
                "removeLineSyncedTtml": true
              }
            }
            """);
        Assert.True(EnhancementWorkflowSelection.IsSidecarsRunnable(enhancement));
        Assert.True(EnhancementWorkflowSelection.HasSidecarLyricsActions(enhancement));
        Assert.False(EnhancementWorkflowSelection.HasExplicitCoverActions(enhancement));

        enhancement["sidecars"]!["removeLineSyncedTtml"] = false;
        Assert.False(EnhancementWorkflowSelection.IsSidecarsRunnable(enhancement));

        enhancement["sidecars"]!["rewriteLineSyncedTtml"] = true;
        Assert.True(EnhancementWorkflowSelection.IsSidecarsRunnable(enhancement));
    }

    [Fact]
    public void IsQualityChecksRunnable_IgnoresLegacyLyricsKeys()
    {
        var enhancement = Parse("""
            {
              "qualityChecks": {
                "enabled": true,
                "queueLyricsRefresh": true,
                "removeLineSyncedTtml": true
              }
            }
            """);
        Assert.False(EnhancementWorkflowSelection.IsQualityChecksRunnable(enhancement));

        enhancement["qualityChecks"]!["flagDuplicates"] = true;
        Assert.True(EnhancementWorkflowSelection.IsQualityChecksRunnable(enhancement));
    }

    [Fact]
    public void MissingCoreMetadataScan_RequiresQualityChecksEnabled()
    {
        var config = Parse("""
            {
              "enhancement": {
                "qualityChecks": {
                  "enabled": false,
                  "flagMissingTags": true
                }
              }
            }
            """);
        Assert.False(EnhancementWorkflowSelection.IsMissingCoreMetadataScanEnabled(config));

        config["enhancement"]!["qualityChecks"]!["enabled"] = true;
        Assert.True(EnhancementWorkflowSelection.IsMissingCoreMetadataScanEnabled(config));
    }

    [Fact]
    public void NormalizeSelectedFeatures_DoesNotAddUnselectedSections()
    {
        var qualityOnly = EnhancementWorkflowSelection.NormalizeSelectedFeatures(["quality-checks"]);
        Assert.Contains(AutoTagLiterals.EnhancementFeatureQualityChecks, qualityOnly);
        Assert.DoesNotContain(AutoTagLiterals.EnhancementFeatureSidecars, qualityOnly);

        var coverOnly = EnhancementWorkflowSelection.NormalizeSelectedFeatures(["cover-maintenance"]);
        Assert.DoesNotContain(AutoTagLiterals.EnhancementFeatureQualityChecks, coverOnly);
        Assert.Contains(AutoTagLiterals.EnhancementFeatureSidecars, coverOnly);
        Assert.DoesNotContain(AutoTagLiterals.EnhancementFeatureCoverMaintenance, coverOnly);

        var folderOnly = EnhancementWorkflowSelection.NormalizeSelectedFeatures(["folder-uniformity"]);
        Assert.Contains(AutoTagLiterals.EnhancementFeatureFolderUniformity, folderOnly);
        Assert.DoesNotContain(AutoTagLiterals.EnhancementFeatureQualityChecks, folderOnly);
        Assert.DoesNotContain(AutoTagLiterals.EnhancementFeatureSidecars, folderOnly);
    }

    [Fact]
    public void NormalizeSelectedFeatures_MapsCoverMaintenanceToSidecars()
    {
        var selected = EnhancementWorkflowSelection.NormalizeSelectedFeatures(["cover-maintenance"]);
        Assert.Contains(AutoTagLiterals.EnhancementFeatureSidecars, selected);
        Assert.DoesNotContain(AutoTagLiterals.EnhancementFeatureCoverMaintenance, selected);
    }

    [Fact]
    public void NormalizeSelectedFeatures_RejectsLyricsRefreshAsStartFeature()
    {
        var selected = EnhancementWorkflowSelection.NormalizeSelectedFeatures(["lyrics-refresh"]);
        Assert.Empty(selected);
    }

    [Fact]
    public void ApplyEnhancementRunSelection_EnablesOnlyOptedInSections()
    {
        var config = Parse("""
            {
              "gapFillTags": ["title"],
              "enhancement": {
                "qualityChecks": { "enabled": true, "flagMissingTags": true },
                "sidecars": { "enabled": true, "queueLyricsRefresh": true },
                "coverMaintenance": { "enabled": true, "upgradeLowResolutionCovers": true }
              }
            }
            """);
        var request = new AutoTagEnhancementStartRequest { Features = ["quality-checks"] };

        var selected = AutoTagJobsController.ApplyEnhancementRunSelection(config, request, [7], []);

        Assert.Contains(AutoTagLiterals.EnhancementFeatureQualityChecks, selected);
        Assert.DoesNotContain(AutoTagLiterals.EnhancementFeatureSidecars, selected);
        Assert.True(config["enhancement"]!["qualityChecks"]!["enabled"]!.GetValue<bool>());
        Assert.False(config["enhancement"]!["sidecars"]!["enabled"]!.GetValue<bool>());
        Assert.False(config["enhancement"]!["coverMaintenance"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void NormalizeSelectedFeatures_KeepsKnownIdsOnly()
    {
        var selected = EnhancementWorkflowSelection.NormalizeSelectedFeatures(
            ["folder-uniformity", "nope", "TAG-GAP-FILL", "manual-enrichment"]);
        Assert.Contains(AutoTagLiterals.EnhancementFeatureFolderUniformity, selected);
        Assert.Contains(AutoTagLiterals.EnhancementFeatureGapFill, selected);
        Assert.Contains(AutoTagLiterals.EnhancementFeatureManualEnrichment, selected);
        Assert.DoesNotContain("nope", selected);
    }

    private static JsonObject Parse(string json)
        => JsonNode.Parse(json)!.AsObject();
}
