using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PublishingWorkflowGuardrailTest
{
    [Fact]
    public void VibeWorkerSmoke_UsesAudioLongEnoughForMaestAndRetainsModelAssertions()
    {
        var smoke = File.ReadAllText(Path.Join(ResolveSrcRoot(), "scripts", "docker-parity-smoke.sh"));
        Assert.Contains("sine=frequency=440:sample_rate=44100:duration=35", smoke, StringComparison.Ordinal);
        Assert.Contains("response.get(\"GenreModel\") != \"discogs519-maest-30s-pw-519l\"", smoke, StringComparison.Ordinal);
        Assert.Contains("first_evidence.get(\"model\") != \"discogs519-maest-30s-pw-519l\"", smoke, StringComparison.Ordinal);
        Assert.Contains("stderr: {completed.stderr[-2000:]}", smoke, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("guardrails.yml", "name: Run guardrails")]
    [InlineData("docker-publish.yml", "name: Run full guardrails")]
    public void GuardrailJobs_InstallPinnedNumpyBeforeTests(string fileName, string testStep)
    {
        var workflow = File.ReadAllText(Path.Join(ResolveSrcRoot(), ".github", "workflows", fileName));
        var installIndex = workflow.IndexOf("name: Install Vibe test dependency", StringComparison.Ordinal);
        Assert.True(installIndex >= 0 && installIndex < workflow.IndexOf(testStep, StringComparison.Ordinal));
        Assert.Contains("uses: actions/setup-python@v5", workflow, StringComparison.Ordinal);
        Assert.Contains("python3 -m pip install --requirement <(sed -n '/^numpy==/p' scripts/vibe-runtime-requirements.txt)", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerRelease_RequiresSuccessfulImagePublication()
    {
        var workflow = File.ReadAllText(Path.Join(ResolveSrcRoot(), ".github", "workflows", "docker-publish.yml"));
        var release = workflow[workflow.IndexOf("  release:", StringComparison.Ordinal)..];
        var condition = release.Split('\n').First(line => line.TrimStart().StartsWith("if:", StringComparison.Ordinal));
        Assert.Contains("needs.publish.result == 'success'", condition, StringComparison.Ordinal);
    }

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
        Assert.Contains("docker pull --platform \"$platform\" \"$ref\"", workflow, StringComparison.Ordinal);
        Assert.Contains("docker image inspect \"$ref\" --format '{{.Os}}/{{.Architecture}}'", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -q \"$platform\" <<< \"$inspect_output\"", workflow, StringComparison.Ordinal);
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

    [Fact]
    public void DockerPublish_RecordsAndAggregatesPublishedContainerDigests()
    {
        var root = ResolveSrcRoot();
        var workflow = File.ReadAllText(Path.Join(root, ".github", "workflows", "docker-publish.yml"));

        // Per-image artifacts are uploaded only after the published-image/platform/runtime checks.
        var writeIndex = workflow.IndexOf("name: Write container digest reference", StringComparison.Ordinal);
        Assert.True(writeIndex > workflow.IndexOf("Published digest matches parity-tested digest", StringComparison.Ordinal),
            "Digest reference must be recorded after the tested/published digest verification.");
        Assert.True(writeIndex > workflow.IndexOf("name: Run published app parity smoke audit", StringComparison.Ordinal),
            "Digest reference must be recorded after the published parity audit.");
        Assert.True(writeIndex > workflow.IndexOf("Verify published manifest platforms", StringComparison.Ordinal),
            "Digest reference must be recorded after the platform verification.");
        Assert.Contains("name: Upload container digest artifact", workflow, StringComparison.Ordinal);
        Assert.Contains("uses: actions/upload-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("container-digest-${{ matrix.image_name }}", workflow, StringComparison.Ordinal);
        Assert.Contains("tested_digest", workflow, StringComparison.Ordinal);
        // The digest artifact write/upload is conditioned on the image selection.
        Assert.Contains("if: steps.image_scope.outputs.publish == 'true'\n        shell: bash\n        run: |\n          set -euo pipefail\n          owner=", workflow, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", workflow, StringComparison.Ordinal);
        // Immutable published references only.
        Assert.Contains("^sha256:[0-9a-f]{64}$", workflow, StringComparison.Ordinal);
        Assert.Contains("@${digest}", workflow, StringComparison.Ordinal);
        // Selected image scope is computed and reused for verification.
        Assert.Contains("selected_images", workflow, StringComparison.Ordinal);
        Assert.Contains("needs.change-scope.outputs.selected_images", workflow, StringComparison.Ordinal);
        // Release job aggregates the same run's per-image artifacts (no explicit run id override).
        var downloadIndex = workflow.IndexOf("name: Download published digest artifacts", StringComparison.Ordinal);
        Assert.True(downloadIndex > writeIndex, "Release job must download the artifacts the publish job uploaded.");
        Assert.Contains("uses: actions/download-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("pattern: container-digest-*", workflow, StringComparison.Ordinal);
        Assert.Contains("merge-multiple: true", workflow, StringComparison.Ordinal);
        // Notes inclusion: digest section is appended before both create and update paths.
        var digestNotesIndex = workflow.IndexOf("## Container Digests", StringComparison.Ordinal);
        Assert.True(digestNotesIndex > downloadIndex, "Digest notes must be built after artifact download.");
        var updateIndex = workflow.IndexOf("--raw-field body=\"${notes}\"", StringComparison.Ordinal);
        var createIndex = workflow.IndexOf("gh release create", StringComparison.Ordinal);
        Assert.True(digestNotesIndex > 0 && digestNotesIndex < updateIndex, "Digest notes must be appended before the update path.");
        Assert.True(digestNotesIndex < createIndex, "Digest notes must be appended before the create path.");
        // Skipped scopes: no digest and no artifact required — the expected set comes from selected_images
        // and a missing artifact for a selected image is a hard failure.
        Assert.Contains("expected_images[", workflow, StringComparison.Ordinal);
        Assert.Contains("Missing digest artifact for selected image", workflow, StringComparison.Ordinal);
        Assert.Contains("Digest artifact for non-selected image", workflow, StringComparison.Ordinal);
        // Digest format is validated before admission to the notes.
        Assert.Contains("@sha256:[0-9a-f]{64}$", workflow, StringComparison.Ordinal);
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
