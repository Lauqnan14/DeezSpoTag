using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Every AutoTag entry mode — standard runs, single-provider runs, multi-provider
/// enhancement, manual enrichment, post-download, batch, library-wide, resumed and
/// recovered runs — must route provider matching through the single
/// <see cref="LocalAutoTagRunner"/> identity capture/write path. This suite pins the
/// convergence point and refuses any parallel provider identity writer.
/// </summary>
public sealed class ProviderIdentityRunModeCoverageTest
{
    private static readonly string[] ContractOwnedFiles =
    [
        "AutoTagIdentityTags.cs",
        "LocalAutoTagRunner.AlbumIdentity.cs",
        "LocalAutoTagRunner.OverwriteGuards.cs",
        "LocalAutoTagRunner.PlatformMatching.cs",
        "LocalAutoTagRunner.ProviderIdentity.cs",
        "LocalAutoTagRunner.Sidecars.cs",
        "LocalAutoTagRunner.TagFieldWriters.cs",
        "LocalAutoTagRunner.TagWrites.cs",
        "ProviderIdentity.cs",
    ];

    [Fact]
    public void StandardRun_ConvergesOnTheSingleRunnerEntryPoint()
    {
        AssertStandardRunIsAdmitted();
        AssertSingleRunnerEntryPoint();
        AssertSharedIdentityWriterIsReached();
    }

    [Fact]
    public void SingleProviderRun_ConvergesOnTheSingleRunnerEntryPoint()
    {
        // One effective platform still runs the same plan, matcher and writer.
        var config = Config("""
            {
              "platforms": ["deezer"],
              "tags": ["title", "artist", "album", "trackId"]
            }
            """);
        Assert.Equal(1, config["platforms"]!.AsArray().Count);
        AssertSingleRunnerEntryPoint();
        AssertSharedIdentityWriterIsReached();
    }

    [Fact]
    public void MultiProviderEnhancement_ConvergesOnTheSingleRunnerEntryPoint()
    {
        Assert.True(InvokeBool("IsAllowedEnhancementTrigger", "schedule"));
        var ordered = EnhancementWorkflowSelection.OrderSelectedFeatures(
            ["folder-uniformity", "sidecars", "tag-gap-fill"]);
        Assert.Equal(["tag-gap-fill", "sidecars", "folder-uniformity"], ordered);
        AssertSingleRunnerEntryPoint();
        AssertSharedIdentityWriterIsReached();
    }

    [Fact]
    public void ManualEnrichment_ConvergesOnTheSingleRunnerEntryPoint()
    {
        Assert.True(InvokeBool("IsAllowedEnhancementTrigger", "manual"));
        AssertSingleRunnerEntryPoint();
        AssertSharedIdentityWriterIsReached();
    }

    [Fact]
    public void PostDownloadRun_ConvergesOnTheSingleRunnerEntryPoint()
    {
        // The automation trigger is the post-download/queue path; it shares the stage
        // pipeline with every other mode.
        Assert.Equal("automation", InvokeString("NormalizeRunTrigger", "automation"));
        AssertSingleRunnerEntryPoint();
        AssertSharedIdentityWriterIsReached();
    }

    [Fact]
    public void BatchScopedRun_ConvergesOnTheSingleRunnerEntryPoint()
    {
        Assert.Equal("batch-scoped", EnhancementWorkflowSelection.ResolveFolderUniformityRunMode(
            ["tag-gap-fill", "folder-uniformity"]));
        AssertSingleRunnerEntryPoint();
        AssertSharedIdentityWriterIsReached();
    }

    [Fact]
    public void LibraryWideRun_ConvergesOnTheSingleRunnerEntryPoint()
    {
        Assert.Equal("library-wide", EnhancementWorkflowSelection.ResolveFolderUniformityRunMode(
            ["folder-uniformity"]));
        AssertSingleRunnerEntryPoint();
        AssertSharedIdentityWriterIsReached();
    }

    [Fact]
    public void ResumedRun_ConvergesOnTheSingleRunnerEntryPoint()
    {
        Assert.Equal("recovery", InvokeString("NormalizeRunTrigger", "recovery"));
        Assert.True(InvokeBool("IsAllowedEnhancementTrigger", "recovery"));

        var service = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var resumeStart = service.IndexOf("ResumeJobAsync", StringComparison.Ordinal);
        Assert.True(resumeStart >= 0, "ResumeJobAsync is missing");
        Assert.Contains("IsBlockedResumeSuccessor", service, StringComparison.Ordinal);
        AssertSingleRunnerEntryPoint();
        AssertSharedIdentityWriterIsReached();
    }

    [Fact]
    public void RecoveredRun_ConvergesOnTheSingleRunnerEntryPoint()
    {
        var job = new AutoTagJob { Trigger = "recovery" };
        Assert.True(InvokeIsBlockedResumeSuccessor(job) is false);
        AssertSingleRunnerEntryPoint();
        AssertSharedIdentityWriterIsReached();
    }

    [Fact]
    public void ProviderIdentityWriters_ExistOnlyInsideTheCentralContract()
    {
        var webRoot = Path.Combine(ResolveRepositoryRoot(), "DeezSpoTag.Web");
        var files = Directory.GetFiles(webRoot, "*.cs", SearchOption.AllDirectories);

        var fieldOwners = files
            .Where(file => File.ReadAllText(file).Contains("ProviderIdentityField", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ContractOwnedFiles, fieldOwners);

        var writerCallers = files
            .Where(file => File.ReadAllText(file).Contains("WriteProviderIdentityAsync(", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "LocalAutoTagRunner.AlbumIdentity.cs", "LocalAutoTagRunner.ProviderIdentity.cs", "LocalAutoTagRunner.TagWrites.cs" },
            writerCallers);
    }

    [Fact]
    public void NoAutoTagServiceOrController_WritesProviderIdentityDirectly()
    {
        var webRoot = Path.Combine(ResolveRepositoryRoot(), "DeezSpoTag.Web");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(webRoot, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var isServiceOrController = name.StartsWith("AutoTagService", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}Controllers{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
            if (!isServiceOrController)
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (text.Contains("ProviderIdentityField", StringComparison.Ordinal)
                || text.Contains("WriteProviderIdentityAsync", StringComparison.Ordinal)
                || text.Contains("ResolveFamily(", StringComparison.Ordinal)
                || text.Contains("_RELEASE_ID\"", StringComparison.Ordinal)
                || text.Contains("_TRACK_ID\"", StringComparison.Ordinal))
            {
                offenders.Add(name);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void ManualEnrichmentCentralIdentityResolution_StaysConfinedToTheManualPass()
    {
        var orchestration = PartialSourceReader.ReadTypeSource(
            "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.RunOrchestration.cs");
        var callIndex = orchestration.IndexOf("ApplyCentralIdentityForManualEnrichmentAsync(", StringComparison.Ordinal);
        Assert.True(callIndex >= 0, "the manual enrichment identity resolution is missing");
        var gateIndex = orchestration.LastIndexOf("isManualEnrichment && firstManualPass", callIndex, StringComparison.Ordinal);
        Assert.True(gateIndex >= 0 && gateIndex < callIndex, "central identity resolution must stay gated on the manual pass");
        Assert.True(
            orchestration.IndexOf("RestoreTrustedCoreIdentity(", gateIndex, StringComparison.Ordinal) < callIndex,
            "trusted core identity must be restored before central identity resolution");

        var matching = PartialSourceReader.ReadTypeSource(
            "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.PlatformMatching.cs");
        Assert.Contains("private async Task ApplyCentralIdentityForManualEnrichmentAsync(", matching, StringComparison.Ordinal);
    }

    private static void AssertStandardRunIsAdmitted()
    {
        Assert.Equal("manual", InvokeString("NormalizeRunTrigger", "manual"));
        Assert.True(InvokeBool("IsAllowedEnhancementTrigger", "manual"));
    }

    /// <summary>All run modes share one stage pipeline, and the pipeline has exactly one
    /// runner call site: there is no second matching path to write identities from.</summary>
    private static void AssertSingleRunnerEntryPoint()
    {
        var webRoot = Path.Combine(ResolveRepositoryRoot(), "DeezSpoTag.Web");
        var callSites = Directory.GetFiles(webRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("_autoTagRunner.RunAsync(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Equal(new[] { "AutoTagService.StagePipeline.cs" }, callSites);

        var implementations = Directory.GetFiles(webRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file))
            .Count(line => line.Contains(": IAutoTagRunner", StringComparison.Ordinal)
                && !line.Contains("partial class LocalAutoTagRunner", StringComparison.Ordinal));
        Assert.Equal(0, implementations);

        var program = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Program.cs");
        Assert.Contains(
            "services.AddSingleton<DeezSpoTag.Web.Services.AutoTag.IAutoTagRunner, DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner>();",
            program,
            StringComparison.Ordinal);
    }

    /// <summary>The shared path captures a native payload and writes it once.</summary>
    private static void AssertSharedIdentityWriterIsReached()
    {
        var matching = PartialSourceReader.ReadTypeSource(
            "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.PlatformMatching.cs");
        Assert.Contains("CaptureProviderIdentity(", matching, StringComparison.Ordinal);
        Assert.Contains("RecordConfirmedProviderIdentity(context.FileIndex, capturedIdentity)", matching, StringComparison.Ordinal);

        var writes = PartialSourceReader.ReadTypeSource(
            "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.TagWrites.cs");
        Assert.Contains("WriteProviderIdentityAsync(", writes, StringComparison.Ordinal);
        Assert.Contains("ResolveEnabledProviderIdentityFields(", writes, StringComparison.Ordinal);
    }

    private static JsonObject Config(string json) => JsonNode.Parse(json)!.AsObject();

    private static string InvokeString(string name, string? value)
    {
        var method = typeof(AutoTagService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"AutoTagService.{name} not found.");
        return Assert.IsType<string>(method.Invoke(null, [value]));
    }

    private static bool InvokeBool(string name, string? value)
    {
        var method = typeof(AutoTagService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"AutoTagService.{name} not found.");
        return Assert.IsType<bool>(method.Invoke(null, [value]));
    }

    private static bool InvokeIsBlockedResumeSuccessor(AutoTagJob? job)
    {
        var method = typeof(AutoTagService).GetMethod("IsBlockedResumeSuccessor", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AutoTagService.IsBlockedResumeSuccessor not found.");
        return Assert.IsType<bool>(method.Invoke(null, [job]));
    }

    private static string ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !Directory.Exists(Path.Combine(current.FullName, "DeezSpoTag.Web")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}