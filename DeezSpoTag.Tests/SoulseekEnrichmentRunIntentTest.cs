using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Guardrails for the <c>soulseek_enrichment</c> run intent.
/// </summary>
/// <remarks>
///     <para>
///         Manual enrichment and Soulseek enrichment are one operation run against two different sets of
///         files. These tests pin the split that keeps them one operation: they must agree on every shared
///         behaviour, and disagree only where the two are genuinely different operations - who starts them,
///         and whose files they may touch.
///     </para>
///     <para>
///         The failure this guards against is silent divergence. Folding Soulseek into
///         <c>IsManualEnrichmentRunIntent</c> everywhere would admit manual runs over live queue-owned
///         downloads and stop every automatic Soulseek run at the scope gate; leaving it out everywhere would
///         give Soulseek a run that tags, moves and finalizes with none of manual's resumability.
///     </para>
/// </remarks>
public sealed class SoulseekEnrichmentRunIntentTest
{
    /// <summary>
    ///     The intent is persisted, so an unknown value would be rewritten to "default" on load and a resumed
    ///     Soulseek job would come back as an ordinary run.
    /// </summary>
    [Theory]
    [InlineData("soulseek_enrichment", "soulseek_enrichment")]
    [InlineData("Soulseek_Enrichment", "soulseek_enrichment")]
    [InlineData("  SOULSEEK_ENRICHMENT  ", "soulseek_enrichment")]
    [InlineData("manual_enrichment", "manual_enrichment")]
    [InlineData("download_enrichment", "download_enrichment")]
    [InlineData("nonsense", "default")]
    [InlineData("", "default")]
    [InlineData(null, "default")]
    public void NormalizeRunIntent_RoundTripsTheSoulseekIntent(string? input, string expected)
    {
        Assert.Equal(expected, InvokeNormalizeRunIntent(input));
    }

    /// <summary>
    ///     Both intents are external-file operations, so both take the shared behaviour.
    /// </summary>
    [Theory]
    [InlineData("manual_enrichment")]
    [InlineData("soulseek_enrichment")]
    [InlineData("MANUAL_ENRICHMENT")]
    [InlineData("SOULSEEK_ENRICHMENT")]
    public void BothExternalFileIntentsShareTheSharedPredicate(string intent)
    {
        Assert.True(InvokeBool("IsExternalFileEnrichmentRunIntent", intent));
    }

    /// <summary>
    ///     The shared predicate is narrow on purpose. Ordinary download enrichment is a different operation
    ///     whose move is owned by download orchestration, and enhancement intents already have their own
    ///     rules.
    /// </summary>
    [Theory]
    [InlineData("download_enrichment")]
    [InlineData("enhancement_only")]
    [InlineData("artist_alias_merge")]
    [InlineData("default")]
    [InlineData("")]
    [InlineData(null)]
    public void TheSharedPredicateDoesNotSwallowOtherIntents(string? intent)
    {
        Assert.False(InvokeBool("IsExternalFileEnrichmentRunIntent", intent));
    }

    /// <summary>
    ///     The manual-only predicate must stay manual-only. It is what keeps the scope gate from admitting a
    ///     reader-started run over a queue-owned staging file.
    /// </summary>
    [Fact]
    public void TheManualPredicateDoesNotIncludeSoulseek()
    {
        Assert.True(InvokeBool("IsManualEnrichmentRunIntent", "manual_enrichment"));
        Assert.False(InvokeBool("IsManualEnrichmentRunIntent", "soulseek_enrichment"));
    }

    /// <summary>
    ///     Soulseek enrichment is not an enhancement intent, which matters because the enhancement scope branch
    ///     refuses anything inside the download root - exactly where every Soulseek file lives.
    /// </summary>
    [Fact]
    public void SoulseekEnrichmentIsNotAnEnhancementIntent()
    {
        Assert.False(InvokeBool("IsEnhancementRunIntent", "soulseek_enrichment"));
        Assert.True(InvokeBool("IsEnhancementRunIntent", "enhancement_only"));
    }

    /// <summary>
    ///     The reader is told which operation produced a log line or history entry, even though both run the
    ///     same machinery.
    /// </summary>
    [Fact]
    public void EachExternalFileIntentNamesItselfDistinctly()
    {
        Assert.Equal("Manual enrichment", Describe("manual_enrichment"));
        Assert.Equal("Soulseek enrichment", Describe("soulseek_enrichment"));

        // A shared stage error must not be attributed to the wrong owner.
        Assert.NotEqual(Describe("manual_enrichment"), Describe("soulseek_enrichment"));
    }

    /// <summary>
    ///     Soulseek enrichment owns its own move.
    /// </summary>
    /// <remarks>
    ///     The auto-move skips any run under the download root whose finalization is owned by download
    ///     orchestration. Soulseek enrichment runs under the download root and is a different operation that
    ///     performs its own scoped move, so if it fell into that guard it would tag, then silently keep every
    ///     file in staging and report no error. This pins the ordering of that guard against the shared
    ///     predicate.
    /// </remarks>
    [Fact]
    public void TheDownloadRootMoveGuardExemptsBothExternalFileIntents()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Services", "AutoTagService.FolderOrganizer.cs");

        var guardIndex = source.IndexOf(
            "auto-move skipped: download-root finalization is owned by download orchestration",
            StringComparison.Ordinal);
        Assert.True(guardIndex > 0, "The download-root auto-move guard was not found.");

        // The predicate that decides whether the guard applies must be the shared one, and must appear
        // before the log line - i.e. it must actually gate this branch.
        var sharedIndex = source.IndexOf(
            "!IsExternalFileEnrichmentRunIntent(job.RunIntent)",
            StringComparison.Ordinal);
        Assert.True(
            sharedIndex > 0 && sharedIndex < guardIndex,
            "The download-root auto-move guard must be gated by IsExternalFileEnrichmentRunIntent so a "
            + "Soulseek enrichment run reaches its own move instead of being skipped.");

        // And download_enrichment must still be skipped by it, which is the guard's original purpose.
        Assert.Contains(
            "AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase)",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     A file that no platform could identify keeps its existing review behaviour. The scope of that rule
    ///     belongs to both intents equally.
    /// </summary>
    [Fact]
    public void TheReviewGateIsStillPendingTaskTwoAndIsNotSilentlyChanged()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Services", "AutoTagService.PathHelpers.cs");

        // Task 2 replaces this branch. Pinning that it is still manual-only documents the starting point, so
        // Task 2's change is a visible, deliberate edit rather than an unnoticed drift.
        Assert.Contains("var isManualEnrichment = IsManualEnrichmentRunIntent(job.RunIntent);", source, StringComparison.Ordinal);
    }

    // ─────────────────────────── the three Task 1 review gaps ───────────────────────────

    /// <summary>
    ///     An automatic Soulseek run must actually reach the sidecar phase.
    /// </summary>
    /// <remarks>
    ///     Download orchestration starts every Soulseek enrichment job with the <c>automation</c> trigger,
    ///     and that trigger was excluded from the workflow gate. A Soulseek run has no enhancement stage of
    ///     its own, so the sidecar phase is the only place lyrics and artwork are ever resolved for it: the
    ///     run tagged, moved and finalized while silently resolving nothing. Manual and schedule still pass on
    ///     the original rule, so an ordinary enhancement run keeps its existing trigger set.
    /// </remarks>
    [Theory]
    [InlineData("soulseek_enrichment", "automation", true)]
    [InlineData("manual_enrichment", "automation", true)]
    [InlineData("soulseek_enrichment", "manual", true)]
    [InlineData("soulseek_enrichment", "schedule", true)]
    [InlineData("soulseek_enrichment", "recovery", true)]
    [InlineData("soulseek_enrichment", "", true)]
    public void TheAutomationTriggerReachesSidecarsForExternalFileEnrichment(
        string runIntent,
        string trigger,
        bool expected)
        => Assert.Equal(expected, InvokeWorkflowTrigger(runIntent, trigger));

    /// <summary>
    ///     An ordinary enhancement run must not gain the automation trigger as a side effect.
    /// </summary>
    [Theory]
    [InlineData("enhancement_only", "automation", false)]
    [InlineData("enhancement_recent_downloads", "automation", false)]
    [InlineData("download_enrichment", "automation", false)]
    [InlineData("default", "automation", false)]
    [InlineData("enhancement_only", "manual", true)]
    [InlineData("enhancement_only", "schedule", true)]
    public void TheAutomationTriggerIsNotWidenedForOtherIntents(
        string runIntent,
        string trigger,
        bool expected)
        => Assert.Equal(expected, InvokeWorkflowTrigger(runIntent, trigger));

    /// <summary>
    ///     The integrated workflow entry point must use the widened gate, not the original one.
    /// </summary>
    /// <remarks>
    ///     Behaviour-level assertion on the source, because the gate sits on a private method that only a
    ///     constructed service can reach. A future edit that restores the old call would otherwise silently
    ///     reintroduce the gap while every test above still passed.
    /// </remarks>
    [Fact]
    public void BothWorkflowGatesUseTheIntentAwareTriggerRule()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Services", "AutoTagService.EnhancementWorkflows.cs");

        Assert.Equal(2, CountOccurrences(source, "IsWorkflowTriggerAllowedForIntent(job.RunIntent, job.Trigger)"));
        Assert.Equal(0, CountOccurrences(source, "IsEnhancementWorkflowTrigger(job.Trigger)"));

        // The original rule must survive unchanged underneath, because the other intents still depend on it.
        var rule = source[source.IndexOf("private static bool IsEnhancementWorkflowTrigger", StringComparison.Ordinal)..];
        rule = rule[..rule.IndexOf('}', rule.IndexOf("RecoveryTrigger", StringComparison.Ordinal))];
        Assert.DoesNotContain("AutomationTrigger", rule, StringComparison.Ordinal);
        Assert.Contains("ManualTrigger", rule, StringComparison.Ordinal);
        Assert.Contains("ScheduleTrigger", rule, StringComparison.Ordinal);
        Assert.Contains("RecoveryTrigger", rule, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Neither external-file intent may be started through the generic route.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The generic endpoint took the caller's run intent verbatim, so both operations were reachable
    ///         by hand over any allowed path - which is the ownership boundary the whole design rests on.
    ///     </para>
    ///     <para>
    ///         The pre-existing scope check cannot be relied on to catch it: it deliberately permits paths
    ///         inside the download root, which is exactly where both of these files live.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("manual_enrichment", true)]
    [InlineData("soulseek_enrichment", true)]
    [InlineData("MANUAL_ENRICHMENT", true)]
    [InlineData(" Soulseek_Enrichment ", true)]
    [InlineData("download_enrichment", false)]
    [InlineData("enhancement_only", false)]
    [InlineData("default", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheGenericStartRouteRefusesBothExternalFileIntents(string? runIntent, bool expected)
        => Assert.Equal(expected, InvokeControllerPredicate("IsExternalFileEnrichmentIntent", runIntent));

    /// <summary>
    ///     The refusal has to be wired into the route, not merely present as a helper.
    /// </summary>
    [Fact]
    public void TheGenericStartRouteRejectsBeforeItBuildsAJob()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs");

        var start = source.IndexOf("[HttpPost(\"start\")]", StringComparison.Ordinal);
        Assert.True(start > 0, "The generic start route was not found.");

        var next = source.IndexOf("[HttpPost(", start + 1, StringComparison.Ordinal);
        var route = source[start..next];

        var guardLine = route.IndexOf("if (IsExternalFileEnrichmentIntent(startRequest.RunIntent))", StringComparison.Ordinal);
        Assert.True(guardLine > 0, "The generic start route must refuse both external-file intents.");

        // The guard must be reachable, not merely present. A source-text search alone is satisfied by a
        // dead "if (false && ...)", which is exactly the kind of edit that would silently reopen this
        // bypass while every other assertion in this file still passed - so the condition is pinned to be
        // the whole test, with no negation and no short-circuit in front of it.
        var lineStart = route.LastIndexOf('\n', guardLine) + 1;
        var condition = route[lineStart..route.IndexOf('\n', guardLine)].Trim();
        Assert.Equal("if (IsExternalFileEnrichmentIntent(startRequest.RunIntent))", condition);

        // It must actually return a rejection, not merely evaluate.
        var guardBody = route[guardLine..];
        guardBody = guardBody[..guardBody.IndexOf("}", StringComparison.Ordinal)];
        Assert.Contains("return BadRequest(", guardBody, System.StringComparison.Ordinal);

        // The refusal must precede the service call, or it is only decoration.
        var serviceCall = route.IndexOf("_autoTagService.StartJob(", StringComparison.Ordinal);
        Assert.True(serviceCall > guardLine, "The intent refusal must come before StartJob.");

        // And the caller is told which operation it was, rather than a generic error.
        Assert.Contains("DescribeEnrichmentIntentForCaller(startRequest.RunIntent)", route, System.StringComparison.Ordinal);
        Assert.Contains("must be started through its own endpoint.", route, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The manual endpoint keeps its stronger idle-queue check; the Soulseek intent has no reader-facing
    ///     endpoint at all, because only download orchestration may start it.
    /// </summary>
    [Fact]
    public void TheManualEndpointRetainsItsIdleQueueCheckAndSoulseekHasNoEndpoint()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "AutoTagApiController.cs");

        Assert.Contains(
            "Manual enrichment cannot claim staged files until the queue is idle.",
            source,
            StringComparison.Ordinal);

        // Deliberately asserted absent: Task 6 starts Soulseek enrichment from download orchestration,
        // never over HTTP, so exposing a route for it would recreate the bypass in a nicer wrapper. The
        // constant is legitimately present in the deny-list above, so what matters is that it is never
        // handed to the service as a run intent.
        Assert.DoesNotContain(
            "RunIntent: AutoTagLiterals.RunIntentSoulseekEnrichment",
            source,
            System.StringComparison.Ordinal);
        Assert.DoesNotContain(
            "runIntent = AutoTagLiterals.RunIntentSoulseekEnrichment",
            source,
            System.StringComparison.Ordinal);
        Assert.Equal(0, CountOccurrences(source, "RunIntent: RunIntentSoulseekEnrichment"));
    }

    /// <summary>
    ///     A pause, resume or completion notification names the operation that produced it.
    /// </summary>
    /// <remarks>
    ///     These are the notifications a reader acts on without opening the run, so both external-file
    ///     intents reporting as a plain "AutoTag" run was indistinguishable from an ordinary one in the
    ///     notification list - the specific labelling claim that was made and was not true.
    /// </remarks>
    [Theory]
    [InlineData("manual_enrichment", "Manual enrichment")]
    [InlineData("soulseek_enrichment", "Soulseek enrichment")]
    [InlineData("MANUAL_ENRICHMENT", "Manual enrichment")]
    [InlineData("SOULSEEK_ENRICHMENT", "Soulseek enrichment")]
    [InlineData("enhancement_only", "Enhancement")]
    [InlineData("default", "AutoTag")]
    [InlineData("", "AutoTag")]
    [InlineData(null, "AutoTag")]
    public void EveryRunNotificationNamesItsOwnOperation(string? runIntent, string expected)
        => Assert.Equal(expected, InvokeDescribeRun(runIntent));

    [Fact]
    public void AllThreeNotificationSitesUseTheLabellingHelper()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Services", "AutoTagService.RunHistory.cs");

        Assert.Equal(3, CountOccurrences(source, "DescribeRunForNotification(job.RunIntent)"));

        // The old two-way label must be gone entirely: it is what collapsed both intents into "AutoTag".
        Assert.DoesNotContain(
            "IsEnhancementRunIntent(job.RunIntent) ? \"Enhancement\" : \"AutoTag\"",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     A persisted Soulseek job reloads as a Soulseek job, not as an ordinary run.
    /// </summary>
    /// <remarks>
    ///     This is the durability half of the feature. A restart normalizes the stored intent; if that
    ///     normalization dropped the new value the job would come back as <c>default</c>, lose its
    ///     resumability, lose its notification label and be eligible for the generic route. Round-tripping
    ///     the serialized job is what actually pins that.
    /// </remarks>
    [Fact]
    public void APersistedSoulseekJobReloadsAsASoulseekJob()
    {
        var job = new AutoTagJob
        {
            Id = "job-1",
            Trigger = AutoTagLiterals.AutomationTrigger,
            RunIntent = AutoTagLiterals.RunIntentSoulseekEnrichment,
            Status = AutoTagLiterals.InterruptedStatus,
            OkCount = 3,
            ErrorCount = 1,
            SkippedCount = 0
        };

        var json = JsonSerializer.Serialize(job);
        var restored = JsonSerializer.Deserialize<AutoTagJob>(json);
        Assert.NotNull(restored);

        // The value survives serialization verbatim...
        Assert.Equal("soulseek_enrichment", restored!.RunIntent);

        // ...and survives the normalization that runs on every load.
        Assert.Equal("soulseek_enrichment", InvokeNormalizeRunIntent(restored.RunIntent));

        // So a reloaded job keeps everything that depends on the intent: it is resumable, it is labelled
        // as itself, and it still counts as an external-file operation.
        Assert.True(InvokeBool("IsExternalFileEnrichmentRunIntent", restored.RunIntent));
        Assert.Equal("Soulseek enrichment", InvokeDescribeRun(restored.RunIntent));
    }

    /// <summary>
    ///     The same, for the manual intent, so the two are symmetric and neither is special-cased.
    /// </summary>
    [Fact]
    public void APersistedManualEnrichmentJobReloadsAsAManualEnrichmentJob()
    {
        var job = new AutoTagJob { Id = "job-2", RunIntent = AutoTagLiterals.RunIntentManualEnrichment };
        var restored = JsonSerializer.Deserialize<AutoTagJob>(JsonSerializer.Serialize(job));

        Assert.NotNull(restored);
        Assert.Equal("manual_enrichment", restored!.RunIntent);
        Assert.Equal("manual_enrichment", InvokeNormalizeRunIntent(restored.RunIntent));
        Assert.True(InvokeBool("IsExternalFileEnrichmentRunIntent", restored.RunIntent));
        Assert.Equal("Manual enrichment", InvokeDescribeRun(restored.RunIntent));
    }

    /// <summary>
    ///     An unknown stored intent degrades to <c>default</c> and stops claiming to be either operation.
    /// </summary>
    [Fact]
    public void AnUnknownStoredIntentDoesNotBecomeASoulseekJob()
    {
        Assert.Equal("default", InvokeNormalizeRunIntent("soulseek_enrichment_v2"));
        Assert.False(InvokeBool("IsExternalFileEnrichmentRunIntent", "soulseek_enrichment_v2"));
        Assert.Equal("AutoTag", InvokeDescribeRun("soulseek_enrichment_v2"));
    }

    private static bool InvokeWorkflowTrigger(string? runIntent, string? trigger)
    {
        var method = typeof(AutoTagService).GetMethod(
            "IsWorkflowTriggerAllowedForIntent",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AutoTagService.IsWorkflowTriggerAllowedForIntent not found.");

        return Assert.IsType<bool>(method.Invoke(null, [runIntent, trigger]));
    }

    private static string InvokeDescribeRun(string? runIntent)
    {
        var method = typeof(AutoTagService).GetMethod(
            "DescribeRunForNotification",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AutoTagService.DescribeRunForNotification not found.");

        return Assert.IsType<string>(method.Invoke(null, new object?[] { runIntent }));
    }

    private static bool InvokeControllerPredicate(string methodName, string? runIntent)
    {
        var method = typeof(AutoTagJobsController).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"AutoTagJobsController.{methodName} not found.");

        return Assert.IsType<bool>(method.Invoke(null, new object?[] { runIntent }));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string InvokeNormalizeRunIntent(string? runIntent)
    {
        var method = typeof(AutoTagService).GetMethod(
            "NormalizeRunIntent",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AutoTagService.NormalizeRunIntent not found.");

        return Assert.IsType<string>(method.Invoke(null, new object?[] { runIntent }));
    }

    private static bool InvokeBool(string methodName, string? runIntent)
    {
        var method = typeof(AutoTagService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"AutoTagService.{methodName} not found.");

        return Assert.IsType<bool>(method.Invoke(null, new object?[] { runIntent }));
    }

    private static string Describe(string? runIntent)
    {
        var method = typeof(AutoTagService).GetMethod(
            "DescribeExternalFileEnrichment",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AutoTagService.DescribeExternalFileEnrichment not found.");

        return Assert.IsType<string>(method.Invoke(null, new object?[] { runIntent }));
    }

    private static string ReadRepoFile(params string[] parts)
    {
        // The repository root is the directory holding DeezSpoTag.Services. Anchoring on DeezSpoTag.Web
        // instead would match the copy under DeezSpoTag.Tests/bin, which has no sources.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Services")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return File.ReadAllText(Path.Combine([directory.FullName, .. parts]));
    }
}