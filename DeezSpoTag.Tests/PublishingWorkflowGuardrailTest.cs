using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
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

    [Fact]
    public async Task SnapshotCoordinatorChange_SelectsOnlySnapshotAndProgressTests()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        await fixture.CommitFileAsync("DeezSpoTag.Web/Services/PlaylistPlatformSnapshotCoordinator.cs", "// measurement change");
        var result = await fixture.RunAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, fixture.Calls.Length);
        Assert.Contains("PlaylistPlatformSnapshotTest", fixture.Calls[1]);
        Assert.Contains("WatchlistPlatformProgressTest", fixture.Calls[1]);
        Assert.DoesNotContain("AutoTag", fixture.Calls[1]);
        Assert.DoesNotContain("PlaylistSync", fixture.Calls[1]);
    }

    [Fact]
    public async Task ChangedTest_SelectsOnlyItsClassAndBuildsAndRunsOnce()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        await fixture.CommitFileAsync("DeezSpoTag.Tests/ExampleTest.cs", "public class ExampleTest { }");
        var result = await fixture.RunAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, fixture.Calls.Length);
        Assert.StartsWith("build DeezSpoTag.Tests/", fixture.Calls[0]);
        Assert.Contains("--filter FullyQualifiedName~DeezSpoTag.Tests.ExampleTest.", fixture.Calls[1]);
        Assert.Contains("--no-build --no-restore", fixture.Calls[1]);
        Assert.DoesNotContain("QobuzQueueEndToEndTest", fixture.Calls[1]);
    }

    [Fact]
    public async Task RelatedSourceChanges_CombineMappedSuitesInOneInvocation()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        await fixture.CommitFileAsync("DeezSpoTag.Web/Services/AmazonMusicMetadataService.cs", "// changed");
        await fixture.CommitFileAsync("DeezSpoTag.Services/Download/Queue/VerificationRetryService.cs", "// changed");
        var result = await fixture.RunAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, fixture.Calls.Length);
        Assert.Contains("AmazonMusicMetadataApiGuardrailTest", fixture.Calls[1]);
        Assert.Contains("VerificationRetryServiceTest", fixture.Calls[1]);
    }

    [Fact]
    public async Task UnmappedSourceChange_BlocksWithoutRunningUnrelatedTests()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        await fixture.CommitFileAsync("DeezSpoTag.Web/Services/NewUnmappedService.cs", "// changed");
        var result = await fixture.RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unmapped", result.Output);
        Assert.Contains("NewUnmappedService.cs", result.Output);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task DocumentationChange_DoesNotSelectUnrelatedTests()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        await fixture.CommitFileAsync("docs/example.md", "documentation only");
        var result = await fixture.RunAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Single(fixture.Calls);
        Assert.StartsWith("build DeezSpoTag.Web/", fixture.Calls[0]);
    }

    [Fact]
    public async Task ManualChangedMode_StillIncludesUncommittedTests()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        fixture.WriteFile("DeezSpoTag.Tests/ExampleTest.cs", "public class ExampleTest { }");
        var result = await fixture.RunAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, fixture.Calls.Length);
        Assert.Contains("DeezSpoTag.Tests.ExampleTest.", fixture.Calls[1]);
    }

    [Fact]
    public async Task OverlappingMappings_RunEachSelectedClassOnlyOnce()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        await fixture.CommitFileAsync("DeezSpoTag.Web/Services/AmazonMusicMetadataService.cs", "// changed");
        await fixture.CommitFileAsync("DeezSpoTag.Tests/AmazonMusicMetadataApiGuardrailTest.cs", "public class AmazonMusicMetadataApiGuardrailTest { }");
        var result = await fixture.RunAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, fixture.Calls.Length);
        Assert.Equal(1, fixture.Calls[1].Split("FullyQualifiedName~DeezSpoTag.Tests.AmazonMusicMetadataApiGuardrailTest.").Length - 1);
    }

    [Fact]
    public async Task ExplicitFullMode_RetainsFullSuiteAndBuildsOnlyOnce()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        var result = await fixture.RunAsync(mode: "full");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, fixture.Calls.Length);
        Assert.DoesNotContain("--filter", fixture.Calls[1]);
        Assert.Contains("--no-build --no-restore", fixture.Calls[1]);
    }

    [Fact]
    public async Task TestFailure_DoesNotReportGuardrailsPassed()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        await fixture.CommitFileAsync("DeezSpoTag.Tests/ExampleTest.cs", "public class ExampleTest { }");
        var result = await fixture.RunAsync(testExit: "23");
        Assert.Equal(23, result.ExitCode);
        Assert.DoesNotContain("Guardrails passed.", result.Output);
    }

    [Fact]
    public async Task PrePush_UsesDestinationCommitAndExcludesUnpublishedWorkingTreeChanges()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        await fixture.CommitFileAsync("DeezSpoTag.Tests/EarlierTest.cs", "public class EarlierTest { }");
        var remoteSha = await fixture.GitAsync("rev-parse", "HEAD");
        await fixture.CommitFileAsync("DeezSpoTag.Tests/ExampleTest.cs", "public class ExampleTest { }");
        fixture.WriteFile("DeezSpoTag.Web/Services/UncommittedService.cs", "// not being pushed");
        var result = await fixture.RunHookAsync(remoteSha);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, fixture.Calls.Length);
        Assert.Contains("ExampleTest", fixture.Calls[1]);
        Assert.DoesNotContain("EarlierTest", fixture.Calls[1]);
    }

    [Fact]
    public async Task PrePush_RejectsDivergedDestinationBeforeBuilding()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        await fixture.CommitFileAsync("DeezSpoTag.Tests/RemoteTest.cs", "public class RemoteTest { }");
        var remoteSha = await fixture.GitAsync("rev-parse", "HEAD");
        await fixture.GitAsync("reset", "--hard", fixture.BaseSha);
        await fixture.CommitFileAsync("DeezSpoTag.Tests/ExampleTest.cs", "public class ExampleTest { }");
        var result = await fixture.RunHookAsync(remoteSha);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("merge", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task ChangedSource_SelectsAffectedTestsWhenRipgrepIsUnavailable()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        await fixture.CommitFileAsync("DeezSpoTag.Web/Services/AmazonMusicMetadataService.cs", "// changed");
        await fixture.DisableRipgrepAsync();
        var result = await fixture.RunAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, fixture.Calls.Length);
        Assert.Contains("AmazonMusicMetadataApiGuardrailTest", fixture.Calls[1]);
    }

    [Fact]
    public async Task TemporaryRepository_CommitsDoNotStartAutomaticMaintenance()
    {
        using var fixture = await GuardrailFixture.CreateAsync();
        fixture.WriteFile("fixture.txt", "maintenance regression");
        await fixture.GitAsync("add", "fixture.txt");
        var trace = await fixture.TraceGitAsync("commit", "-m", "fixture change");
        foreach (var line in trace.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var entry = JsonDocument.Parse(line);
            if (entry.RootElement.GetProperty("event").GetString() == "child_start")
            {
                Assert.DoesNotContain("maintenance", entry.RootElement.GetProperty("argv")
                    .EnumerateArray().Select(argument => argument.GetString()));
            }
        }
    }

    private sealed class GuardrailFixture : IDisposable
    {
        private readonly string directory = Path.Join(Path.GetTempPath(), "guardrail-selection-" + Guid.NewGuid());
        public string BaseSha { get; private set; } = "";
        public string[] Calls => File.Exists(Path.Join(directory, "calls")) ? File.ReadAllLines(Path.Join(directory, "calls")) : [];

        private GuardrailFixture() { }

        public static async Task<GuardrailFixture> CreateAsync()
        {
            var fixture = new GuardrailFixture();
            await fixture.InitializeAsync();
            return fixture;
        }

        private async Task InitializeAsync()
        {
            Directory.CreateDirectory(directory);
            WriteFile("scripts/guardrails.sh", File.ReadAllText(Path.Join(ResolveSrcRoot(), "scripts/guardrails.sh")));
            WriteFile(".githooks/pre-push", File.ReadAllText(Path.Join(ResolveSrcRoot(), ".githooks/pre-push")));
            WriteFile("DeezSpoTag.Tests/DeezSpoTag.Tests.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            foreach (var test in Directory.GetFiles(Path.Join(ResolveSrcRoot(), "DeezSpoTag.Tests"), "*Test.cs"))
                WriteFile("DeezSpoTag.Tests/" + Path.GetFileName(test), File.ReadAllText(test));
            WriteFile("bin/dotnet", "#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >> \"$TEST_CALLS\"\nif [[ \"$1\" == test ]]; then exit \"${TEST_EXIT:-0}\"; fi\n");
            await ExecuteAsync("chmod", ["+x", "bin/dotnet", "scripts/guardrails.sh"], []);
            await GitAsync("init", "-b", "main");
            // Disposable repositories must not outlive their awaited Git commands
            // through background maintenance that races recursive cleanup.
            await GitAsync("config", "maintenance.auto", "false");
            await GitAsync("config", "gc.auto", "0");
            await GitAsync("config", "user.email", "fixture@example.invalid");
            await GitAsync("config", "user.name", "Guardrail fixture");
            await GitAsync("add", ".");
            await GitAsync("commit", "-m", "fixture baseline");
            BaseSha = await GitAsync("rev-parse", "HEAD");
            await GitAsync("update-ref", "refs/remotes/origin/main", BaseSha);
        }

        public void WriteFile(string path, string content)
        {
            var fullPath = Path.Join(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public async Task CommitFileAsync(string path, string content)
        {
            WriteFile(path, content);
            await GitAsync("add", path);
            await GitAsync("commit", "-m", "fixture change");
        }

        public async Task<string> GitAsync(params string[] args)
        {
            var result = await ExecuteAsync("git", args, []);
            Assert.Equal(0, result.ExitCode);
            return result.Output.Trim();
        }

        public async Task<string> TraceGitAsync(params string[] args)
        {
            var tracePath = Path.Join(directory, ".git", "process-trace.json");
            var result = await ExecuteAsync("git", args,
                new Dictionary<string, string> { ["GIT_TRACE2_EVENT"] = tracePath });
            Assert.Equal(0, result.ExitCode);
            return await File.ReadAllTextAsync(tracePath);
        }

        public Task<(int ExitCode, string Output)> RunAsync(string mode = "changed", string testExit = "0")
            => ExecuteAsync("bash", ["scripts/guardrails.sh"], new Dictionary<string, string> { ["GUARDRAILS_MODE"] = mode, ["TEST_EXIT"] = testExit });

        public async Task<(int ExitCode, string Output)> RunHookAsync(string remoteSha)
            => await ExecuteAsync("bash", [".githooks/pre-push", "origin", "unused"], [], $"refs/heads/main {await GitAsync("rev-parse", "HEAD")} refs/heads/main {remoteSha}\n");

        private async Task<(int ExitCode, string Output)> ExecuteAsync(string executable, string[] args, Dictionary<string, string> environment, string? input = null)
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GUARDRAILS_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
            start.Environment.Remove("DEEZSPOTAG_SKIP_GUARDRAILS");
            start.Environment["PATH"] = Path.Join(directory, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            start.Environment["TEST_CALLS"] = Path.Join(directory, "calls");
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            using var process = Process.Start(start)!;
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, (await output) + (await error));
        }

        public async Task DisableRipgrepAsync()
        {
            WriteFile("bin/rg", "#!/usr/bin/env bash\nexit 127\n");
            WriteFile(".git/info/exclude", "bin/rg\n");
            var result = await ExecuteAsync("chmod", ["+x", "bin/rg"], []);
            Assert.Equal(0, result.ExitCode);
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
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
