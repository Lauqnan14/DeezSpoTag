using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PublishingWorkflowGuardrailTest
{
    [Fact]
    public void VibeRuntime_UsesOneExactDependencyLock()
    {
        var root = ResolveSrcRoot();
        var requirements = File.ReadAllLines(Path.Join(root, "scripts", "vibe-runtime-requirements.txt"));
        var dockerfile = File.ReadAllText(Path.Join(root, "Dockerfile"));
        var localSetup = File.ReadAllText(Path.Join(root, "scripts", "setup-vibe-analysis-local.sh"));

        Assert.NotEmpty(requirements);
        Assert.All(requirements, line =>
        {
            Assert.Matches("^[A-Za-z0-9_.-]+==[^=\\s]+$", line);
            Assert.DoesNotContain(">=", line, StringComparison.Ordinal);
        });
        Assert.Contains(requirements, line => line.StartsWith("essentia-tensorflow==", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requirements, line => line.StartsWith("numpy==", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requirements, line => line.StartsWith("PyYAML==", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requirements, line => line.StartsWith("six==", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--requirement /app/scripts/vibe-runtime-requirements.txt", dockerfile, StringComparison.Ordinal);
        Assert.Contains("--requirement \"$ROOT_DIR/scripts/vibe-runtime-requirements.txt\"", localSetup, StringComparison.Ordinal);
        Assert.DoesNotContain("numpy>=", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("ESSENTIA_TF_PACKAGE", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("pip install --upgrade", localSetup, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerRuntime_UsesPinnedBaseImagesWithoutUnrestrictedUpgrade()
    {
        var dockerfile = File.ReadAllText(Path.Join(ResolveSrcRoot(), "Dockerfile"));

        Assert.DoesNotContain("apt-get upgrade", dockerfile, StringComparison.Ordinal);
        Assert.Matches("FROM mcr\\.microsoft\\.com/dotnet/sdk:[^\\s]+@sha256:[0-9a-f]{64} AS build", dockerfile);
        Assert.Matches("FROM docker:cli@sha256:[0-9a-f]{64} AS docker-cli", dockerfile);
        Assert.Matches("FROM golang:1\\.26\\.6-bookworm@sha256:[0-9a-f]{64} AS apple-wrapper-build", dockerfile);
        Assert.Matches("FROM mcr\\.microsoft\\.com/dotnet/aspnet:[^\\s]+@sha256:[0-9a-f]{64} AS runtime", dockerfile);
    }

    [Fact]
    public void VibeModels_AreChecksumVerifiedAndCacheKeyTracksManifest()
    {
        var root = ResolveSrcRoot();
        var fetchScript = File.ReadAllText(Path.Join(root, "scripts", "fetch-vibe-models.sh"));
        var workflow = File.ReadAllText(Path.Join(root, ".github", "workflows", "docker-publish.yml"));
        var parity = File.ReadAllText(Path.Join(root, "scripts", "docker-parity-smoke.sh"));
        var downloads = fetchScript.Split('\n').Where(line => line.StartsWith("download \"", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(downloads);
        Assert.All(downloads, line => Assert.Matches("^download \"[^\"]+\" \"https://[^\"]+\" \"[0-9a-f]{64}\"$", line));
        Assert.Contains("sha256sum -c", fetchScript, StringComparison.Ordinal);
        Assert.Contains("Cached model checksum mismatch", fetchScript, StringComparison.Ordinal);
        Assert.Contains("hashFiles('scripts/fetch-vibe-models.sh')", workflow, StringComparison.Ordinal);
        Assert.Contains("Vibe model manifest SHA-256", parity, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerPublish_PushesTheParityTestedAppImageWithoutRebuilding()
    {
        var root = ResolveSrcRoot();
        var workflow = File.ReadAllText(Path.Join(root, ".github", "workflows", "docker-publish.yml"));
        var parity = File.ReadAllText(Path.Join(root, "scripts", "docker-parity-smoke.sh"));
        var analyzer = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Tools", "vibe_analyzer.py"));

        Assert.Contains("name: Push parity-tested app image", workflow, StringComparison.Ordinal);
        Assert.Contains("id: push_tested_app", workflow, StringComparison.Ordinal);
        Assert.Contains("tested_digest", workflow, StringComparison.Ordinal);
        Assert.Contains("Published digest matches parity-tested digest", workflow, StringComparison.Ordinal);
        Assert.Contains("matrix.image_name == 'deezspotag-apple-wrapper'", workflow, StringComparison.Ordinal);
        Assert.Contains("name: Run published app parity smoke audit", workflow, StringComparison.Ordinal);
        Assert.Contains("response.get(\"AnalysisVersion\") != \"musicnn-1\"", parity, StringComparison.Ordinal);
        Assert.Contains("response.get(\"GenreModel\") != \"discogs519-maest-30s-pw-519l\"", parity, StringComparison.Ordinal);
        Assert.Contains("EssentiaGenreEvidence", parity, StringComparison.Ordinal);
        Assert.Contains("\"AnalysisVersion\": \"musicnn-1\"", analyzer, StringComparison.Ordinal);
        Assert.Contains("\"EssentiaGenreEvidence\"", analyzer, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerPublish_InstallsHostFfmpegForAppParityFixtures()
    {
        var root = ResolveSrcRoot();
        var workflow = File.ReadAllText(Path.Join(root, ".github", "workflows", "docker-publish.yml"));

        Assert.Contains("name: Install app parity audit dependencies", workflow, StringComparison.Ordinal);
        Assert.Contains("if: matrix.image_name == 'deezspotag'", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "sudo apt-get update && sudo apt-get install -y --no-install-recommends ffmpeg",
            workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DockerPublish_RequiresFullGuardrailsBeforeVersioningAndPublishing()
    {
        var root = ResolveSrcRoot();
        var workflow = File.ReadAllText(Path.Join(root, ".github", "workflows", "docker-publish.yml"));

        Assert.Contains("validate:\n", workflow, StringComparison.Ordinal);
        Assert.Contains("GUARDRAILS_MODE=full ./scripts/guardrails.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("needs: [validate, change-scope]", workflow, StringComparison.Ordinal);
        Assert.Contains("needs: [validate, release-context, change-scope, bump-version]", workflow, StringComparison.Ordinal);
        Assert.Contains("needs.validate.result == 'success'", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardrailChangedMode_IncludesUnpublishedWorkingTreeChanges()
    {
        var script = File.ReadAllText(Path.Join(ResolveSrcRoot(), "scripts", "guardrails.sh"));

        Assert.Contains("git diff --name-only", script, StringComparison.Ordinal);
        Assert.Contains("git diff --cached --name-only", script, StringComparison.Ordinal);
        Assert.Contains("git ls-files --others --exclude-standard", script, StringComparison.Ordinal);
    }

    private static string ResolveSrcRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
