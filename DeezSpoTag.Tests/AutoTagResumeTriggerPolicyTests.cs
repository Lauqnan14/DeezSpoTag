using System;
using System.IO;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Behavioral guardrails for AutoTag resume:
/// 1. the enhancement trigger policy must admit the recovery trigger, because
///    ResumeJobAsync starts successors with trigger "recovery" — without this the
///    successor is admitted as blocked while the source job is stamped "resumed",
///    so the API reports a running resume while nothing runs;
/// 2. a resume successor that was admitted as blocked/skipped must be reported as
///    a failed resume instead of a success.
/// </summary>
public sealed class AutoTagResumeTriggerPolicyTests
{
    [Theory]
    [InlineData("manual", true)]
    [InlineData("schedule", true)]
    [InlineData("recovery", true)]
    [InlineData("automation", false)]
    [InlineData("user", false)]
    [InlineData("invalid", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsAllowedEnhancementTrigger_AdmitsRecoveryForResumes(string? trigger, bool expected)
    {
        var method = typeof(AutoTagService).GetMethod(
            "IsAllowedEnhancementTrigger",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AutoTagService.IsAllowedEnhancementTrigger not found.");

        var result = Assert.IsType<bool>(method.Invoke(null, new object?[] { trigger }));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsAllowedEnhancementTrigger_IsCaseInsensitive()
    {
        var method = typeof(AutoTagService).GetMethod(
            "IsAllowedEnhancementTrigger",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AutoTagService.IsAllowedEnhancementTrigger not found.");

        Assert.True(Assert.IsType<bool>(method.Invoke(null, new object?[] { "Recovery" })));
        Assert.True(Assert.IsType<bool>(method.Invoke(null, new object?[] { "MANUAL" })));
    }

    [Theory]
    [InlineData("blocked", true)]
    [InlineData("BLOCKED", true)]
    [InlineData("skipped", true)]
    [InlineData("Skipped", true)]
    [InlineData("running", false)]
    [InlineData("queued", false)]
    [InlineData("completed", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsBlockedResumeSuccessor_FlagsAdmissionFailureStatusesOnly(string? status, bool expected)
    {
        var job = new AutoTagJob { Status = status ?? string.Empty };

        Assert.Equal(expected, InvokeIsBlockedResumeSuccessor(job));
    }

    [Fact]
    public void IsBlockedResumeSuccessor_NullSuccessorIsNotBlocked()
    {
        Assert.False(InvokeIsBlockedResumeSuccessor(null));
    }

    [Fact]
    public void ResumeJobAsync_UsesTheBlockedSuccessorGuard()
    {
        var service = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");

        var resumeStart = service.IndexOf(
            "public async Task<ResumeJobOutcome?> ResumeJobAsync",
            StringComparison.Ordinal);
        Assert.True(resumeStart >= 0, "Missing ResumeJobAsync.");
        var guardIndex = service.IndexOf("IsBlockedResumeSuccessor(resumed)", resumeStart, StringComparison.Ordinal);
        Assert.True(guardIndex > resumeStart, "ResumeJobAsync must check the successor with IsBlockedResumeSuccessor.");
        var stampIndex = service.IndexOf("AutoTagLiterals.ResumedStatus", resumeStart, StringComparison.Ordinal);
        Assert.True(stampIndex > guardIndex, "The source job must only be stamped resumed after the successor passed the blocked-successor guard.");
    }

    [Fact]
    public void TriggerPolicy_AdmittingRecovery_IsDocumentedAtTheDecisionSite()
    {
        // Locks the intent of the recovery allowance next to the decision so a future
        // cleanup cannot silently remove it and reintroduce the silent no-op resume.
        var service = ReadSource("DeezSpoTag.Web", "Services", "AutoTagService.cs");
        var policyStart = service.IndexOf(
            "private static bool IsAllowedEnhancementTrigger",
            StringComparison.Ordinal);
        Assert.True(policyStart >= 0, "Missing IsAllowedEnhancementTrigger.");
        var policyEnd = service.IndexOf("private static", policyStart + 1, StringComparison.Ordinal);
        var policy = service[policyStart..(policyEnd > policyStart ? policyEnd : service.Length)];

        Assert.Contains("AutoTagLiterals.RecoveryTrigger", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void StopEndpoint_ReportsTheStatusActuallyApplied()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs");
        var stopStart = controller.IndexOf("[HttpPost(\"jobs/{id}/stop\")]", StringComparison.Ordinal);
        Assert.True(stopStart >= 0, "Missing stop endpoint.");
        var stopEnd = controller.IndexOf("[HttpPost(\"jobs/{id}/resume\")]", stopStart, StringComparison.Ordinal);
        var stop = controller[stopStart..(stopEnd > stopStart ? stopEnd : controller.Length)];

        Assert.Contains("StopJobWithStatusAsync", stop, StringComparison.Ordinal);
        Assert.Contains("status = outcome.Status", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("status = \"paused\"", stop, StringComparison.Ordinal);
    }

    [Fact]
    public void JobResponses_ExposeTheResumeCheckpoint()
    {
        // The resume banner (wwwroot/js/enhancement-resume.js) only offers resume when
        // the job payload carries a checkpoint; without this field the button can
        // never appear anywhere.
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs");

        Assert.Contains("job.ResumeCheckpoint,", controller, StringComparison.Ordinal);
    }

    private static bool InvokeIsBlockedResumeSuccessor(AutoTagJob? job)
    {
        var method = typeof(AutoTagService).GetMethod(
            "IsBlockedResumeSuccessor",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AutoTagService.IsBlockedResumeSuccessor not found.");

        return Assert.IsType<bool>(method.Invoke(null, new object?[] { job }));
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
