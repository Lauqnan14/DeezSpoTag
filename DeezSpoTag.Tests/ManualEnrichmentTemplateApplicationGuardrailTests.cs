using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ManualEnrichmentTemplateApplicationGuardrailTests
{
    [Fact]
    public void ManualStart_PassesActiveProfileFolderStructureToRuntimeConfig()
    {
        var controllerSource = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs");
        var serviceSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        Assert.True(
            controllerSource.Contains("selectedProfileResult.Profile?.FolderStructure", StringComparison.Ordinal)
                || controllerSource.Contains("selectedProfile.FolderStructure", StringComparison.Ordinal),
            "Manual starts must pass the selected profile folder structure into the runtime config.");
        Assert.Contains("FolderStructureSettings? FolderStructureOverride", serviceSource, StringComparison.Ordinal);
        Assert.Contains("root[\"folderStructure\"] = JsonSerializer.SerializeToNode(folderStructure", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"folderStructure\"", serviceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualEnrichmentStages_EnableTemplateMaterializationButDownloadEnrichmentDoesNot()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.EnrichmentStages.cs");
        var serviceSource = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        Assert.Contains("OrganizeSidecarsIntoTemplateFolders: true", source, StringComparison.Ordinal);
        Assert.Contains("MaterializeToTemplatePath: true", source, StringComparison.Ordinal);
        Assert.Contains("ForceShazamFingerprint = plan.ForceShazamFingerprint", source, StringComparison.Ordinal);
        Assert.Contains("ManualForceFingerprintKey", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ManualShazamBootstrapTags", source, StringComparison.Ordinal);
        Assert.DoesNotContain("manual enrichment Shazam bootstrap", source, StringComparison.Ordinal);
        Assert.Contains("stageRoot[\"organizeSidecarsIntoTemplateFolders\"] = true", source, StringComparison.Ordinal);
        Assert.Contains("stageRoot[\"materializeToTemplatePath\"] = true", source, StringComparison.Ordinal);
        Assert.Contains("\"materializeToTemplatePath\"", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OrganizeSidecarsIntoTemplateFolders",
            ExtractMethod(source, "private static EnrichmentStagePlan BuildAutomaticDownloadEnrichmentStagePlan"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MaterializeToTemplatePath",
            ExtractMethod(source, "private static EnrichmentStagePlan BuildAutomaticDownloadEnrichmentStagePlan"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveAutomaticDownloadEnrichmentRequestedTags(baseRoot)",
            ExtractMethod(source, "private static EnrichmentStagePlan BuildAutomaticDownloadEnrichmentStagePlan"),
            StringComparison.Ordinal);
        Assert.Contains(
            "FilterAutomaticDownloadEnrichmentPlatforms(sourceFilteredPlatforms, requestedTags, platformCaps)",
            ExtractMethod(source, "private static EnrichmentStagePlan BuildAutomaticDownloadEnrichmentStagePlan"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".Where(platform => !IsLyricsProviderPlatform(platform))",
            ExtractMethod(source, "private static List<string> FilterAutomaticDownloadEnrichmentPlatforms"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".Where(tag => !IsLyricsTag(tag))",
            ExtractMethod(source, "private static List<string> ResolveAutomaticDownloadEnrichmentRequestedTags"),
            StringComparison.Ordinal);
        Assert.Contains(
            "caps.SupportedTags.Any(requestedTags.Contains)",
            ExtractMethod(source, "private static bool PlatformSupportsAnyRequestedTag"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_AppliesProfileFolderStructureAndWritesLyricsSidecarsToTemplatePath()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("ApplyFolderStructureOverrides(settings, config.FolderStructure)", source, StringComparison.Ordinal);
        Assert.Contains("settings.ArtistNameTemplate = folderStructure.ArtistNameTemplate.Trim()", source, StringComparison.Ordinal);
        Assert.Contains("settings.AlbumNameTemplate = folderStructure.AlbumNameTemplate.Trim()", source, StringComparison.Ordinal);
        Assert.Contains("settings.PlaylistNameTemplate = folderStructure.PlaylistNameTemplate.Trim()", source, StringComparison.Ordinal);
        Assert.Contains("var lrcPath = BuildLyricsSidecarPath(context, \".lrc\")", source, StringComparison.Ordinal);
        Assert.Contains("var ttmlPath = BuildLyricsSidecarPath(context, TtmlExtension)", source, StringComparison.Ordinal);
        Assert.Contains("var pathInfo = BuildTemplatePathInfo(context.CoreTrack, context.Settings)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_MaterializesManualFileBeforeWritingSidecarsAndTags()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var applyMethod = ExtractMethod(source, "private async Task ApplyResolvedMatchAsync");

        Assert.Contains("public bool? MaterializeToTemplatePath { get; set; }", source, StringComparison.Ordinal);
        Assert.Contains("MaterializeToTemplatePath = raw.MaterializeToTemplatePath", source, StringComparison.Ordinal);
        Assert.Contains("private static string MaterializeFileToTemplatePath", source, StringComparison.Ordinal);
        Assert.Contains("FileMoveFallbackHelper.MoveWithFallback(sourcePath, destinationPath)", source, StringComparison.Ordinal);
        Assert.Contains("public required string File { get; set; }", source, StringComparison.Ordinal);
        Assert.Contains("context.Plan.Files[context.FileIndex] = context.File", applyMethod, StringComparison.Ordinal);
        Assert.True(
            applyMethod.IndexOf("MaterializeFileToTemplatePath(", StringComparison.Ordinal)
                < applyMethod.IndexOf("PopulatePlatformLyricsAsync", StringComparison.Ordinal));
        Assert.True(
            applyMethod.IndexOf("MaterializeFileToTemplatePath(", StringComparison.Ordinal)
                < applyMethod.IndexOf("TagFileAsync", StringComparison.Ordinal));
    }

    private static string ReadSource(params string[] pathParts)
        => File.ReadAllText(Path.Join([ResolveRepoRoot(), .. pathParts]));

    private static string ExtractMethod(string source, string methodName)
    {
        var index = source.IndexOf(methodName, StringComparison.Ordinal);
        if (index < 0)
        {
            return string.Empty;
        }

        var nextMethod = source.IndexOf("\n    private ", index + methodName.Length, StringComparison.Ordinal);
        return nextMethod < 0 ? source[index..] : source[index..nextMethod];
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

        throw new InvalidOperationException("Repository root could not be resolved.");
    }
}
