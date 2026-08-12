using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagEnhancementConfigCanonicalizationTests
{
    private static readonly int[] DuplicateQualityFolderIds = { 9, 9, 10 };

    private static readonly MethodInfo SanitizeConfigJsonMethod =
        typeof(AutoTagService).GetMethod("SanitizeConfigJson", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AutoTagService.SanitizeConfigJson not found.");

    private static readonly Type TaggingProfileDataHelperType =
        typeof(TaggingProfileService).Assembly.GetType("DeezSpoTag.Web.Services.TaggingProfileDataHelper")
        ?? throw new InvalidOperationException("TaggingProfileDataHelper type not found.");

    private static readonly MethodInfo CanonicalizeEnhancementConfigMethod =
        TaggingProfileDataHelperType.GetMethod("CanonicalizeEnhancementConfig", BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("CanonicalizeEnhancementConfig method not found.");

    private static readonly MethodInfo SanitizeAutoTagSettingsMethod =
        TaggingProfileDataHelperType.GetMethod("SanitizeAutoTagSettings", BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("SanitizeAutoTagSettings method not found.");

    [Fact]
    public void SanitizeConfigJson_MigratesLegacyFolderIdToFolderIds()
    {
        var json = """
        {
          "enhancement": {
            "folderUniformity": { "folderId": 7 },
            "coverMaintenance": { "folderId": "9" },
            "qualityChecks": { "folderId": 7, "folderIds": [7, 8, 8] }
          }
        }
        """;

        var sanitized = (string)SanitizeConfigJsonMethod.Invoke(null, [json])!;
        using var document = JsonDocument.Parse(sanitized);
        var enhancement = document.RootElement.GetProperty("enhancement");

        var uniformity = enhancement.GetProperty("folderUniformity");
        Assert.False(uniformity.TryGetProperty("folderId", out _));
        Assert.Equal(new long[] { 7 }, ReadLongArray(uniformity.GetProperty("folderIds")));

        var cover = enhancement.GetProperty("coverMaintenance");
        Assert.False(cover.TryGetProperty("folderId", out _));
        Assert.Equal(new long[] { 9 }, ReadLongArray(cover.GetProperty("folderIds")));

        var quality = enhancement.GetProperty("qualityChecks");
        Assert.False(quality.TryGetProperty("folderId", out _));
        Assert.Equal(new long[] { 7, 8 }, ReadLongArray(quality.GetProperty("folderIds")));
    }

    [Fact]
    public void SanitizeConfigJson_RemovesLegacyFolderUniformityStructureMirrorKeys()
    {
        var json = """
        {
          "enhancement": {
            "folderUniformity": {
              "folderIds": [1],
              "createArtistFolder": true,
              "artistNameTemplate": "%artist%",
              "createAlbumFolder": true,
              "albumNameTemplate": "%album%",
              "illegalCharacterReplacer": "_",
              "multiArtistSeparator": "default",
              "usePrimaryArtistFolders": true,
              "renameSpotifyArtistFolders": true
            }
          }
        }
        """;

        var sanitized = (string)SanitizeConfigJsonMethod.Invoke(null, [json])!;
        using var document = JsonDocument.Parse(sanitized);
        var folderUniformity = document.RootElement
            .GetProperty("enhancement")
            .GetProperty("folderUniformity");

        Assert.False(folderUniformity.TryGetProperty("createArtistFolder", out _));
        Assert.False(folderUniformity.TryGetProperty("artistNameTemplate", out _));
        Assert.False(folderUniformity.TryGetProperty("createAlbumFolder", out _));
        Assert.False(folderUniformity.TryGetProperty("albumNameTemplate", out _));
        Assert.False(folderUniformity.TryGetProperty("illegalCharacterReplacer", out _));
        Assert.False(folderUniformity.TryGetProperty("multiArtistSeparator", out _));
        Assert.False(folderUniformity.TryGetProperty("usePrimaryArtistFolders", out _));
        Assert.False(folderUniformity.TryGetProperty("renameSpotifyArtistFolders", out _));
        Assert.Equal(new long[] { 1 }, ReadLongArray(folderUniformity.GetProperty("folderIds")));
    }

    [Fact]
    public void SanitizeConfigJson_RemovesLegacyEmbeddedDeezerAuthentication()
    {
        var json = """
        {
          "arl": "legacy-root-token",
          "custom": {
            "deezer": {
              "ARL": "legacy-custom-token",
              "art_resolution": 1200
            }
          }
        }
        """;

        var sanitized = (string)SanitizeConfigJsonMethod.Invoke(null, [json])!;
        using var document = JsonDocument.Parse(sanitized);

        Assert.False(document.RootElement.TryGetProperty("arl", out _));
        var deezer = document.RootElement.GetProperty("custom").GetProperty("deezer");
        Assert.DoesNotContain(deezer.EnumerateObject(), property =>
            string.Equals(property.Name, "arl", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1200, deezer.GetProperty("art_resolution").GetInt32());
    }

    [Fact]
    public void TaggingProfileDataHelper_CanonicalizeEnhancementConfig_MigratesAndPurgesLegacyKeys()
    {
        var data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["enhancement"] = JsonSerializer.SerializeToElement(new
            {
                folderUniformity = new
                {
                    folderId = 5,
                    createArtistFolder = true,
                    artistNameTemplate = "%artist%"
                },
                coverMaintenance = new
                {
                    folderId = "6"
                },
                qualityChecks = new
                {
                    folderIds = DuplicateQualityFolderIds,
                    folderId = 11
                }
            })
        };

        var changed = (bool)CanonicalizeEnhancementConfigMethod.Invoke(null, [data])!;
        Assert.True(changed);

        using var document = JsonDocument.Parse(data["enhancement"].GetRawText());
        var enhancement = document.RootElement;

        var uniformity = enhancement.GetProperty("folderUniformity");
        Assert.False(uniformity.TryGetProperty("folderId", out _));
        Assert.False(uniformity.TryGetProperty("createArtistFolder", out _));
        Assert.False(uniformity.TryGetProperty("artistNameTemplate", out _));
        Assert.Equal(new long[] { 5 }, ReadLongArray(uniformity.GetProperty("folderIds")));

        var cover = enhancement.GetProperty("coverMaintenance");
        Assert.False(cover.TryGetProperty("folderId", out _));
        Assert.Equal(new long[] { 6 }, ReadLongArray(cover.GetProperty("folderIds")));

        var quality = enhancement.GetProperty("qualityChecks");
        Assert.False(quality.TryGetProperty("folderId", out _));
        Assert.Equal(new long[] { 9, 10 }, ReadLongArray(quality.GetProperty("folderIds")));
    }

    [Fact]
    public void TaggingProfileDataHelper_SanitizeAutoTagSettings_MigratesLegacyAlbumArtFileToSaveArtwork()
    {
        var autoTag = new AutoTagSettings
        {
            Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["albumArtFile"] = JsonSerializer.SerializeToElement(true)
            }
        };

        var sanitized = (AutoTagSettings)SanitizeAutoTagSettingsMethod.Invoke(null, [autoTag, "deezer"])!;
        Assert.True(sanitized.Data.TryGetValue("saveArtwork", out var saveArtwork));
        Assert.Equal(JsonValueKind.True, saveArtwork.ValueKind);
        Assert.False(sanitized.Data.ContainsKey("albumArtFile"));
    }

    [Fact]
    public void TaggingProfileDataHelper_SanitizeAutoTagSettings_PrefersSaveArtworkWhenBothKeysExist()
    {
        var autoTag = new AutoTagSettings
        {
            Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["saveArtwork"] = JsonSerializer.SerializeToElement(false),
                ["albumArtFile"] = JsonSerializer.SerializeToElement(true)
            }
        };

        var sanitized = (AutoTagSettings)SanitizeAutoTagSettingsMethod.Invoke(null, [autoTag, "deezer"])!;
        Assert.True(sanitized.Data.TryGetValue("saveArtwork", out var saveArtwork));
        Assert.Equal(JsonValueKind.False, saveArtwork.ValueKind);
        Assert.False(sanitized.Data.ContainsKey("albumArtFile"));
    }

    [Fact]
    public void EnhancementContracts_UseFolderIdsOnly()
    {
        Assert.Null(typeof(AutoTagEnhancementStartRequest).GetProperty("FolderId"));
        Assert.NotNull(typeof(AutoTagEnhancementStartRequest).GetProperty("FolderIds"));
        Assert.NotNull(typeof(AutoTagEnhancementStartRequest).GetProperty("TargetFiles"));
        Assert.NotNull(typeof(AutoTagEnhancementStartRequest).GetProperty("Features"));

        var endpoint = typeof(AutoTagEnhancementController).GetMethod(nameof(AutoTagEnhancementController.GetEnhancementTechnicalProfiles));
        Assert.NotNull(endpoint);
        Assert.DoesNotContain(endpoint!.GetParameters(), parameter =>
            string.Equals(parameter.Name, "folderId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnhancementTechnicalProfileUpgrade_IsExplicitlyWired()
    {
        var repoRoot = ResolveRepoRoot();
        var viewSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var scriptSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var controllerSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs"));
        var workflowSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs"));

        Assert.Contains("enhancementQueueTechnicalProfileUpgrades", viewSource, StringComparison.Ordinal);
        Assert.Contains("startCentralEnhancementFeature(\"quality-checks\"", scriptSource, StringComparison.Ordinal);
        Assert.Contains("ApplyEnhancementFolderScope(enhancement, \"qualityChecks\"", controllerSource, StringComparison.Ordinal);
        Assert.Contains("var runQualityUpgradeStage = queueTechnicalProfileUpgrades", workflowSource, StringComparison.Ordinal);
        Assert.Contains("EnhancementAdmissionLimit = EnhancementBatchSize", workflowSource, StringComparison.Ordinal);
        Assert.Contains("EnqueueEnhancementBatchAsync", File.ReadAllText(
            Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "QualityScannerService.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("StartEnhancementQualityScannerAsync", controllerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementFeatureRuns_UseOnlyTheCentralAutoTagJobPath()
    {
        var repoRoot = ResolveRepoRoot();
        var jobsController = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs"));
        var enhancementController = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "AutoTagEnhancementController.cs"));
        var scriptSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));

        Assert.Contains("[HttpPost(\"enhancement/start\")]", jobsController, StringComparison.Ordinal);
        Assert.Contains("_autoTagService.StartJob(", jobsController, StringComparison.Ordinal);
        Assert.Contains("RunIntentEnhancementRecentDownloads", jobsController, StringComparison.Ordinal);
        Assert.Contains("SetEnhancementFeatureEnabled", jobsController, StringComparison.Ordinal);
        Assert.Contains("/api/autotag/enhancement/start", scriptSource, StringComparison.Ordinal);
        Assert.DoesNotContain("enhancement/folder-uniformity/start", scriptSource, StringComparison.Ordinal);
        Assert.DoesNotContain("enhancement/folder-uniformity/status", scriptSource, StringComparison.Ordinal);
        Assert.DoesNotContain("enhancement/quality-checks", scriptSource, StringComparison.Ordinal);
        Assert.DoesNotContain("[HttpPost", enhancementController, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteFolderUniformityAsync", enhancementController, StringComparison.Ordinal);
        Assert.DoesNotContain("StartEnhancementQualityScannerAsync", enhancementController, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicFolderEnhancement_RequiresAssignedProfilesAndHasNoGlobalTemplateFallback()
    {
        var repoRoot = ResolveRepoRoot();
        var folderController = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryFoldersApiController.cs"));
        var profileResolution = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagProfileResolutionService.cs"));
        var organizerOverlay = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagOrganizerProfileOverlay.cs"));
        var autoTagService = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.Contains("ResolveRequiredMusicProfileIdAsync", folderController, StringComparison.Ordinal);
        Assert.Contains("Music folders must always have an AutoTag profile.", folderController, StringComparison.Ordinal);
        Assert.Contains("profiles.FirstOrDefault(profile => profile.IsDefault)?.Id", profileResolution, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySettingsOverrides", organizerOverlay, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.TracknameTemplate", autoTagService, StringComparison.Ordinal);
        Assert.Contains("AutoTag organization requires a valid profile.", autoTagService, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileDeletion_RequiresUserConfirmationBeforeDeleteRequest()
    {
        var repoRoot = ResolveRepoRoot();
        var scriptSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var deleteStart = scriptSource.IndexOf("async function deleteProfile()", StringComparison.Ordinal);
        var deleteEnd = scriptSource.IndexOf("function showToast", deleteStart, StringComparison.Ordinal);
        Assert.True(deleteStart >= 0 && deleteEnd > deleteStart);
        var deleteBody = scriptSource[deleteStart..deleteEnd];

        var confirmIndex = deleteBody.IndexOf("DeezSpoTag.ui.confirm", StringComparison.Ordinal);
        var requestIndex = deleteBody.IndexOf("method: \"DELETE\"", StringComparison.Ordinal);
        Assert.True(confirmIndex >= 0);
        Assert.True(requestIndex > confirmIndex);
        Assert.Contains("if (!confirmed)", deleteBody, StringComparison.Ordinal);
        Assert.Contains("Delete AutoTag Profile", deleteBody, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementFeatureSelection_DisablesUnselectedWorkflowsAndGapFill()
    {
        var method = typeof(AutoTagJobsController).GetMethod(
            "ApplyEnhancementRunSelection",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Central enhancement selection method was not found.");
        var config = System.Text.Json.Nodes.JsonNode.Parse("""
            {
              "gapFillTags": ["title"],
              "enhancement": {
                "folderUniformity": { "enabled": true },
                "coverMaintenance": { "enabled": true },
                "qualityChecks": { "enabled": true }
              }
            }
            """)!.AsObject();
        var request = new AutoTagEnhancementStartRequest
        {
            Features = ["quality-checks"]
        };

        method.Invoke(null, [config, request, new long[] { 7 }, Array.Empty<string>()]);

        var enhancement = config["enhancement"]!.AsObject();
        Assert.False(enhancement["folderUniformity"]!["enabled"]!.GetValue<bool>());
        Assert.False(enhancement["coverMaintenance"]!["enabled"]!.GetValue<bool>());
        Assert.True(enhancement["qualityChecks"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(7, enhancement["qualityChecks"]!["folderIds"]![0]!.GetValue<long>());
        Assert.Empty(config["gapFillTags"]!.AsArray());
    }

    [Fact]
    public void MissingMetadataScan_PreservesGapFillOnlyForScannedTargetFiles()
    {
        var method = typeof(AutoTagJobsController).GetMethod(
            "ApplyEnhancementRunSelection",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Central enhancement selection method was not found.");
        var config = System.Text.Json.Nodes.JsonNode.Parse("""
            {
              "gapFillTags": ["title", "artist", "album", "album_artist", "track_number"],
              "enhancement": {
                "folderUniformity": { "enabled": true },
                "coverMaintenance": { "enabled": true },
                "qualityChecks": {
                  "enabled": true,
                  "flagMissingTags": true
                }
              }
            }
            """)!.AsObject();
        var request = new AutoTagEnhancementStartRequest
        {
            Features = ["quality-checks"]
        };

        method.Invoke(null, [config, request, new long[] { 7 }, new[] { "/tmp/music/track.flac" }]);

        var enhancement = config["enhancement"]!.AsObject();
        Assert.False(enhancement["folderUniformity"]!["enabled"]!.GetValue<bool>());
        Assert.True(enhancement["qualityChecks"]!["enabled"]!.GetValue<bool>());
        Assert.NotEmpty(config["gapFillTags"]!.AsArray());
        Assert.Equal("/tmp/music/track.flac", config["targetFiles"]![0]!.GetValue<string>());
    }

    [Fact]
    public void MissingMetadataScan_UsesCoreMetadataTargetFilesAndDoesNotStartQualityScanner()
    {
        var repoRoot = ResolveRepoRoot();
        var controllerSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs"));
        var workflowSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs"));
        var repositorySource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var autoTagServiceSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));
        var prepareRunBody = ExtractMethodBody(workflowSource, "private async Task PrepareEnhancementRunAsync");

        Assert.DoesNotContain("GetMissingCoreMetadataFilesAsync", controllerSource, StringComparison.Ordinal);
        Assert.Contains("GetMissingCoreMetadataFilesAsync", workflowSource, StringComparison.Ordinal);
        Assert.Contains("MissingCoreMetadataTags", workflowSource, StringComparison.Ordinal);
        Assert.Contains("ResolveEnhancementTargetRootPath(targetFiles)", controllerSource, StringComparison.Ordinal);
        Assert.Contains("BuildMissingCoreMetadataRepair", repositorySource, StringComparison.Ordinal);
        Assert.Contains("IsMissingOrWeakMetadata", repositorySource, StringComparison.Ordinal);
        Assert.Contains("RepeatedNumericFilenamePrefixRegex", repositorySource, StringComparison.Ordinal);
        Assert.Contains("OrderByDescending(static file => file.RepairScore)", repositorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("var runQualityUpgradeStage = flagMissingTags", workflowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("var runQualityScanner = flagMissingTags", workflowSource, StringComparison.Ordinal);
        Assert.Contains("ReportMissingCoreMetadataAuditIfRequestedAsync", workflowSource, StringComparison.Ordinal);
        Assert.Contains("missing core metadata DB audit", workflowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists", prepareRunBody, StringComparison.Ordinal);
        Assert.Contains("RefreshConfiguredServersAsync", autoTagServiceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementRunWorkflows_AreExplicitlyOptedInAndDecoupledFromGapFillTags()
    {
        var repoRoot = ResolveRepoRoot();
        var viewSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var scriptSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var serviceSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));
        var workflowSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs"));
        var orchestrationSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"));
        var controllerSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "AutoTagEnhancementController.cs"));
        var organizerSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagLibraryOrganizer.cs"));

        Assert.Contains("enableFolderUniformityWorkflow", viewSource, StringComparison.Ordinal);
        Assert.Contains("enableQualityChecksWorkflow", viewSource, StringComparison.Ordinal);
        Assert.Contains("enableCoverMaintenanceWorkflow", viewSource, StringComparison.Ordinal);
        Assert.Contains("folderUniformityIncludeSubfolders", viewSource, StringComparison.Ordinal);
        Assert.Contains("Keep both on unresolved sidecar/path conflicts", viewSource, StringComparison.Ordinal);
        Assert.Contains("folderUniformity.enabled = getChecked(\"enableFolderUniformityWorkflow\"", scriptSource, StringComparison.Ordinal);
        Assert.Contains("folderUniformity.includeSubfolders = getChecked(\"folderUniformityIncludeSubfolders\"", scriptSource, StringComparison.Ordinal);
        Assert.Contains("delete folderUniformity.renameSpotifyArtistFolders", scriptSource, StringComparison.Ordinal);
        Assert.Contains("coverMaintenance.enabled = getChecked(\"enableCoverMaintenanceWorkflow\"", scriptSource, StringComparison.Ordinal);
        Assert.Contains("qualityChecks.enabled = getChecked(\"enableQualityChecksWorkflow\"", scriptSource, StringComparison.Ordinal);
        Assert.Contains("TryMarkNoStagesConfigured(job, stages, includesEnhancementWorkflows)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("gap-fill tagging skipped", serviceSource, StringComparison.Ordinal);
        Assert.Contains("ReadBool(config, EnabledField) == true", workflowSource, StringComparison.Ordinal);
        Assert.Contains("ReadBool(coverMaintenance, EnabledField) != true", workflowSource, StringComparison.Ordinal);
        Assert.Contains("ReadBool(qualityChecks, EnabledField) != true", workflowSource, StringComparison.Ordinal);
        Assert.Contains("enhancementCount > 0 || HasConfiguredEnhancementWorkflows(root)", orchestrationSource, StringComparison.Ordinal);
        Assert.Contains("profile has no gap-fill tags or enhancement workflows", orchestrationSource, StringComparison.Ordinal);
        Assert.Contains("LibraryFolderPathSafety.IsMusicFolder(folder)", controllerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("enhancement workflows own folder uniformity", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldRunGenericOrganizer", serviceSource, StringComparison.Ordinal);
        Assert.Contains("ApplyEnhancementBatchSectionsAsync", serviceSource, StringComparison.Ordinal);
        Assert.Contains("OrganizeFilesWithReportAsync", workflowSource, StringComparison.Ordinal);
        Assert.Contains("folder structure skipped (enforceFolderStructure is disabled)", workflowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderTemplatesAppliedInBatches", workflowSource, StringComparison.Ordinal);
        Assert.Contains("AutoTagOrganizerBatchResult", organizerSource, StringComparison.Ordinal);
        Assert.Contains("RecordEnhancementItemStatus", workflowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementActivities_ExposeMetadataAndFortyFileBatchProgress()
    {
        var repoRoot = ResolveRepoRoot();
        var viewSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));
        var autoTagViewSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml"));
        var serviceSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));
        var workflowSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs"));
        var orchestrationSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs"));

        Assert.Contains("autotag-enhancement-feature", viewSource, StringComparison.Ordinal);
        Assert.Contains("autotag-current-batch", viewSource, StringComparison.Ordinal);
        Assert.Contains("runSelectedEnhancementSections", autoTagViewSource, StringComparison.Ordinal);
        Assert.Contains("EnhancementGroupId", serviceSource, StringComparison.Ordinal);
        Assert.Contains("private const int EnhancementBatchSize = 40", workflowSource, StringComparison.Ordinal);
        Assert.Contains("GetEnabledEnhancementFeatures", orchestrationSource, StringComparison.Ordinal);
        Assert.Contains("BuildEnhancementFeatureConfig", orchestrationSource, StringComparison.Ordinal);
    }

    [Fact]
    public void QualityScanner_TargetTrackIdsAreAppliedInRepositoryQuery()
    {
        var repoRoot = ResolveRepoRoot();
        var scannerSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "QualityScannerService.cs"));
        var repositorySource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var loadRunTracksBody = ExtractMethodBody(scannerSource, "private async Task<List<QualityScanTrackDto>> LoadRunTracksAsync");

        Assert.Contains("targetTrackIds: options.TargetTrackIds", loadRunTracksBody, StringComparison.Ordinal);
        Assert.DoesNotContain("options.TargetTrackIds.Contains(track.TrackId)", loadRunTracksBody, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyCollection<long>? targetTrackIds = null", repositorySource, StringComparison.Ordinal);
        Assert.Contains("@targetTrackIdsJson IS NULL OR t.id IN (SELECT value FROM json_each(@targetTrackIdsJson))", repositorySource, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStuckWatchdog_UsesLastActivityHeartbeatForNonTaggingEnhancementPhases()
    {
        var repoRoot = ResolveRepoRoot();
        var serviceSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));
        var progressTimestampBody = ExtractMethodBody(serviceSource, "private static DateTimeOffset GetLastProgressTimestamp");

        Assert.Contains("if (job.LastActivityAt > timestamp)", progressTimestampBody, StringComparison.Ordinal);
        Assert.Contains("timestamp = job.LastActivityAt;", progressTimestampBody, StringComparison.Ordinal);
        Assert.Contains("job.LastActivityAt = DateTimeOffset.UtcNow;", serviceSource, StringComparison.Ordinal);
    }

    private static long[] ReadLongArray(JsonElement element)
    {
        return element
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out _))
            .Select(static item => item.GetInt64())
            .ToArray();
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

    private static string ExtractMethodBody(string source, string methodSignature)
    {
        var start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method marker not found: {methodSignature}");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"Method body not found: {methodSignature}");

        var depth = 0;
        for (var index = braceStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[braceStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Method body was not closed: {methodSignature}");
    }
}
