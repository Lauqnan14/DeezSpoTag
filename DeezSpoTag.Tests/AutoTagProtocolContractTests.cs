using System;
using System.IO;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guardrails for the typed runner→service protocol:
/// - control flow (stopped / paused / failed) is carried by AutoTagRunOutcome, not by
///   parsing error-string prefixes;
/// - the text markers that remain (log lines, organizer report entries) are declared
///   once in AutoTagProtocol and referenced by both emitters and parsers.
/// </summary>
public sealed class AutoTagProtocolContractTests
{
    [Theory]
    [InlineData(AutoTagRunOutcome.Completed, true)]
    [InlineData(AutoTagRunOutcome.Stopped, false)]
    [InlineData(AutoTagRunOutcome.Paused, false)]
    [InlineData(AutoTagRunOutcome.Failed, false)]
    public void AutoTagRunResult_SuccessMeansCompletedOnly(AutoTagRunOutcome outcome, bool expectedSuccess)
    {
        var result = new AutoTagRunResult(outcome, null);

        Assert.Equal(expectedSuccess, result.Success);
    }

    [Fact]
    public void TypedFactories_CarryOutcomeAndError()
    {
        Assert.Equal(AutoTagRunOutcome.Completed, AutoTagRunResult.Completed().Outcome);
        Assert.Null(AutoTagRunResult.Completed().Error);

        var stopped = AutoTagRunResult.Stopped();
        Assert.Equal(AutoTagRunOutcome.Stopped, stopped.Outcome);
        Assert.Equal(AutoTagProtocol.StoppedOutcome, stopped.Error);

        var paused = AutoTagRunResult.Paused("review folder missing");
        Assert.Equal(AutoTagRunOutcome.Paused, paused.Outcome);
        Assert.Equal("review folder missing", paused.Error);
        // The pause reason is the message itself: no "paused:" prefix protocol.
        Assert.DoesNotContain(":", paused.Error, StringComparison.Ordinal);

        Assert.Equal(AutoTagRunOutcome.Failed, AutoTagRunResult.Failed("boom").Outcome);
    }

    [Fact]
    public void Runner_EmitsTypedOutcomesNotStringPrefixes()
    {
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("AutoTagRunResult.Completed()", runner, StringComparison.Ordinal);
        Assert.Contains("AutoTagRunResult.Stopped()", runner, StringComparison.Ordinal);
        Assert.Contains("AutoTagRunResult.Paused(ex.Message)", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("new AutoTagRunResult(false,", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"paused: ", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_SwitchesOnOutcomeInsteadOfErrorStrings()
    {
        var service = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        var stageBody = ExtractMethodBody(service, "private async Task<StageExecutionResult> ExecuteSingleStageAsync");
        Assert.Contains("result.Outcome == AutoTagRunOutcome.Stopped", stageBody, StringComparison.Ordinal);
        Assert.Contains("result.Outcome == AutoTagRunOutcome.Paused", stageBody, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Equals(result.Error, \"stopped\"", stageBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PausedPrefix", stageBody, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformStartMarker_IsDeclaredOnceAndSharedByEmitterAndParser()
    {
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var service = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var protocol = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "AutoTagProtocol.cs");

        // Declared once.
        Assert.Contains("public const string LogMarker = \"onetagger_autotag:\";", protocol, StringComparison.Ordinal);
        Assert.Contains("public const string StartingPlatformMessage = \"starting \";", protocol, StringComparison.Ordinal);

        // Emitted via the constants (no raw literals left at emit sites).
        Assert.DoesNotContain("$\"onetagger_autotag: starting {platform}\"", runner, StringComparison.Ordinal);
        Assert.Contains("AutoTagProtocol.StartingPlatformMessage}{platform}", runner, StringComparison.Ordinal);

        // Parsed via the constants.
        Assert.Contains("AutoTagProtocol.LogMarker", service, StringComparison.Ordinal);
        Assert.Contains("AutoTagProtocol.StartingPlatformMessage", service, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveFileReport_MarkerIsDeclaredOnceAndSharedByWriterAndParser()
    {
        var organizer = ReadSource("DeezSpoTag.Web", "Services", "AutoTagLibraryOrganizer.cs");
        var workflows = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs");
        var protocol = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "AutoTagProtocol.cs");

        Assert.Contains("public const string MoveFileEntryPrefix = \"move-file: \";", protocol, StringComparison.Ordinal);
        Assert.Contains("public const string MoveFileEntrySeparator = \" -> \";", protocol, StringComparison.Ordinal);

        Assert.DoesNotContain("$\"move-file: {action.SourcePath} -> {action.DestinationPath}\"", organizer, StringComparison.Ordinal);
        Assert.Contains("AutoTagProtocol.MoveFileEntryPrefix}{action.SourcePath}", organizer, StringComparison.Ordinal);

        Assert.DoesNotContain("const string prefix = \"move-file: \";", workflows, StringComparison.Ordinal);
        Assert.Contains("AutoTagProtocol.MoveFileEntryPrefix", workflows, StringComparison.Ordinal);
        Assert.Contains("AutoTagProtocol.MoveFileEntrySeparator", workflows, StringComparison.Ordinal);
    }

    private static string ExtractMethodBody(string source, string signatureStart)
    {
        var start = source.IndexOf(signatureStart, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method starting with: {signatureStart}");
        var bodyStart = source.IndexOf('{', start);
        var depth = 0;
        for (var i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unterminated method body for: {signatureStart}");
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
