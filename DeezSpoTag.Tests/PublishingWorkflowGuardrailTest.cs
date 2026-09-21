using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PublishingWorkflowGuardrailTest
{
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
