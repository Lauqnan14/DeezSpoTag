using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EnhancementFingerprintRecoveryGuardrailTests
{
    [Fact]
    public void EnhancementStage_KeepsFingerprintIntentAndBootstrapsShazam()
    {
        var service = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var stages = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.EnrichmentStages.cs");
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs");
        var workflows = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs");

        Assert.Contains("EnhancementForceFingerprintKey", service, StringComparison.Ordinal);
        Assert.Contains("keys.Add(AutoTagLiterals.EnhancementForceFingerprintKey)", service, StringComparison.Ordinal);
        Assert.Contains("ConfigureShazamFingerprintBootstrap(stageRoot)", service, StringComparison.Ordinal);
        Assert.Contains("configNode[AutoTagLiterals.EnhancementForceFingerprintKey] = request.ForceFingerprint", controller, StringComparison.Ordinal);
        Assert.Contains("root[AutoTagLiterals.EnhancementUntrustedTargetsKey] = true", workflows, StringComparison.Ordinal);
        Assert.Contains("[\"id_first\"] = false", stages, StringComparison.Ordinal);
    }

    [Fact]
    public void UntrustedFiles_SkipIdFirstAndOriginalTagValidation()
    {
        var runner = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var matcher = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "ShazamMatcher.cs");

        Assert.Contains("if (shazamConfig.IdFirst && identityIsTrusted)", runner, StringComparison.Ordinal);
        Assert.Contains("HasUsableMatchIdentity", runner, StringComparison.Ordinal);
        Assert.Contains("treatSourceAsUntrusted: !identityIsTrusted", runner, StringComparison.Ordinal);
        Assert.Contains("trustSourceIdentity: identityIsTrusted", runner, StringComparison.Ordinal);
        Assert.Contains("IsTrustedSourceIdentity", runner, StringComparison.Ordinal);
        Assert.Contains("TrackIdentityTrust.IsUntrustedIdentity", runner, StringComparison.Ordinal);
        Assert.Contains("bool trustSourceIdentity = true", matcher, StringComparison.Ordinal);
        Assert.Contains("if (trustSourceIdentity && !TrackTitleMatcher.HasCompatibleTitleIdentity(info.Title, recognized.Title))", matcher, StringComparison.Ordinal);
        Assert.Contains("if (trustSourceIdentity && titleSimilarity < minTitleSimilarity)", matcher, StringComparison.Ordinal);
        Assert.DoesNotContain("if (shazamConfig.IdFirst)\n        {", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancementUi_ExposesRecognitionRadiosAndSendsForceFingerprint()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "AutoTag", "Index.cshtml");
        var script = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "autotag.js");

        Assert.Contains("name=\"enhancementRecognitionMethod\" value=\"id-first\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"enhancementRecognitionMethod\" value=\"fingerprint\"", view, StringComparison.Ordinal);
        Assert.Contains("request.forceFingerprint", script, StringComparison.Ordinal);
        Assert.Contains("gapFilling.forceFingerprint", script, StringComparison.Ordinal);
        Assert.Contains("Files with corrupt or missing identity always fingerprint", view, StringComparison.Ordinal);
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
