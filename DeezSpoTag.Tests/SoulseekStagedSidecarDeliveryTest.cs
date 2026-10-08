using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Behavioural tests for the three ways a Soulseek staged sidecar pass failed to deliver.
/// </summary>
/// <remarks>
///     Each of these was a promise the staged pass made and did not keep: it resolved no artwork, it
///     resolved no lyrics, and it could deliver a sidecar under a name nothing would pair it with.
/// </remarks>
public sealed class SoulseekStagedSidecarDeliveryTest
{
    /// <summary>
    ///     Forcing the profile's lyrics must not switch album artwork off.
    /// </summary>
    /// <remarks>
    ///     The two flags answer independent questions, and coupling them meant every external-file operation
    ///     - which forces the profile's lyrics - did no cover work at all, however clearly the profile asked
    ///     for artwork. A Soulseek release on no streaming service often has its only artwork in the peer's
    ///     folder, so this is the category that matters most here.
    /// </remarks>
    [Fact]
    public void AnArtworkEnabledProfileStillGetsArtworkWork()
    {
        var source = ReadSource("AutoTagService.EnhancementWorkflows.cs");
        var start = source.IndexOf("private async Task<EnhancementWorkflowOutcome> RunConfiguredSidecarsAsync", StringComparison.Ordinal);
        Assert.True(start > 0, "RunConfiguredSidecarsAsync was not found.");
        var method = source[start..];
        method = method[..method.IndexOf("if (!runLyrics && !runCovers)", StringComparison.Ordinal)];

        var runCovers = method;
        Assert.Contains("var runCovers =", runCovers, StringComparison.Ordinal);

        // The decisive property: runCovers must NOT be gated on the lyrics flag being off.
        var assignment = runCovers[runCovers.LastIndexOf("var runCovers =", StringComparison.Ordinal)..];
        assignment = assignment[..assignment.IndexOf(';', StringComparison.Ordinal)];
        Assert.DoesNotContain("!forceProfileLyrics", assignment, StringComparison.Ordinal);
        Assert.Contains("HasExplicitCoverActions", assignment, StringComparison.Ordinal);
        Assert.Contains("sidecarEnabled", assignment, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The staged pass must not force lyrics the profile never enabled.
    /// </summary>
    /// <remarks>
    ///     Forcing means a lookup runs regardless of configuration. The phase already decides which
    ///     categories are enabled from the profile, so forcing past that decision is how a category the
    ///     reader switched off still costs network calls.
    /// </remarks>
    [Fact]
    public void TheStagedPassRunsOnlyTheEnabledCategories()
    {
        var source = ReadSource("AutoTagService.EnhancementWorkflows.cs");
        var start = source.IndexOf("private async Task RunExternalFileStagedSidecarsAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var method = source[start..];
        method = method[..method.IndexOf("RunManualEnrichmentBatchSidecarsAsync", StringComparison.Ordinal)];

        Assert.Contains("forceProfileLyrics: false,", method, StringComparison.Ordinal);
        Assert.Contains("HasSidecarLyricsActions(enhancementRoot)", method, StringComparison.Ordinal);
        Assert.Contains("HasExplicitCoverActions(enhancementRoot)", method, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A staged file with no library id still gets its lyrics handled.
    /// </summary>
    /// <remarks>
    ///     <c>SidecarFetchPlan</c> required a library track id, so every staged file resolved trackId 0 and was
    ///     reported as handling no lyrics - the whole staged pass did nothing for the category a reader is
    ///     most likely to want, while appearing to run.
    /// </remarks>
    [Fact]
    public void AStagedFileWithoutALibraryIdStillHandlesLyrics()
    {
        var source = ReadSource("AutoTagService.EnhancementWorkflows.cs");

        var planStart = source.IndexOf("internal sealed class SidecarFetchPlan", StringComparison.Ordinal);
        Assert.True(planStart > 0, "SidecarFetchPlan was not found.");
        var plan = source[planStart..];
        plan = plan[..plan.IndexOf("internal readonly record struct SidecarFetchScope", StringComparison.Ordinal)];

        var resolve = plan[plan.IndexOf("public SidecarFetchScope Resolve", StringComparison.Ordinal)..];
        resolve = resolve[..resolve.IndexOf('}', resolve.IndexOf("return new SidecarFetchScope", StringComparison.Ordinal))];

        Assert.Contains("identityResolvedFromTags", resolve, StringComparison.Ordinal);
        Assert.Contains("trackId > 0 || identityResolvedFromTags", resolve, StringComparison.Ordinal);

        // And the call site has to pass the flag, or the new parameter is decorative.
        Assert.Contains(
            "sidecarRunPlan.Resolve(filePath, trackId, resolveFileIdentityFromTags)",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The staged lyrics lookup is planned and executed through the file, not a library row.
    /// </summary>
    /// <Fact]
    public void StagedLyricsArePlannedAndWrittenThroughTheFile()
    {
        var workflows = ReadSource("AutoTagService.EnhancementWorkflows.cs");
        Assert.Contains("_lyricsRefreshQueueService.PlanStagedFileRefresh(", workflows, StringComparison.Ordinal);
        Assert.Contains("_lyricsRefreshQueueService.RefreshStagedFileNowAsync(", workflows, StringComparison.Ordinal);

        // The staged path must bypass the library-batch runner, which cannot work without a track id.
        var start = workflows.IndexOf("if (needs.RequiresLyricsStep)", StringComparison.Ordinal);
        Assert.True(start > 0);
        var method = workflows[start..];
        method = method[..method.IndexOf("var verdict = ClassifyLyricsOutcome", StringComparison.Ordinal)];

        var stagedBranch = method.IndexOf("RefreshStagedFileNowAsync(", StringComparison.Ordinal);
        var batchBranch = method.IndexOf("RunLyricsRefreshForBatchAsync(", StringComparison.Ordinal);
        Assert.True(stagedBranch > 0, "The staged lyrics branch is missing.");
        Assert.True(stagedBranch < batchBranch, "The staged branch must come first; it returns before the library batch path.");
    }

    /// <summary>
    ///     The staged lyrics entry points exist and do not require the library.
    /// </summary>
    [Fact]
    public void TheStagedLyricsEntryPointsDoNotRequireALibraryRepository()
    {
        var service = ReadSource("LyricsRefreshQueueService.cs");

        Assert.Contains("public LyricsRefreshPlan PlanStagedFileRefresh(", service, StringComparison.Ordinal);
        Assert.Contains("public async Task<LyricsRefreshTrackResult> RefreshStagedFileNowAsync(", service, StringComparison.Ordinal);

        // Neither may reach for a library row: that is the whole point of them existing.
        foreach (var member in new[] { "PlanStagedFileRefresh", "RefreshStagedFileNowAsync" })
        {
            var start = service.IndexOf(member, StringComparison.Ordinal);
            Assert.True(start > 0);
            var body = service[start..];
            body = body[..body.IndexOf("\n    private ", StringComparison.Ordinal)];

            Assert.DoesNotContain("GetTrackAudioInfoAsync", body, StringComparison.Ordinal);
            Assert.DoesNotContain("GetTrackSourceLinksAsync", body, StringComparison.Ordinal);
        }

        // Identity comes from the file's own tags, read with the runner's raw reader so MP4/ATL quirks are
        // handled once rather than twice.
        Assert.Contains("AutoTag.LocalAutoTagRunner.ReadRawTagValues(", service, StringComparison.Ordinal);
        Assert.Contains("AddStagedProviderId(audio, extension, urls, \"deezer_track_id\"", service, StringComparison.Ordinal);

        // Both paths share one implementation, so they cannot drift.
        Assert.Contains("ProcessResolvedFileLyricsAsync(", service, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A sidecar keeps the stem of the audio it belongs to, even when the audio was renamed.
    /// </summary>
    /// <remarks>
    ///     Renaming a sidecar independently produced <c>01 Track (1).flac</c> beside
    ///     <c>01 Track (1) (1).lrc</c> - two files, nothing to pair them. The name is derived from the audio's
    ///     final destination and used as-is.
    /// </remarks>
    [Fact]
    public void ASidecarKeepsTheStemOfTheMovedAudio()
    {
        var source = ReadSource("AutoTagDownloadMoveService.cs");
        var start = source.IndexOf("private static void MoveAdjacentSidecarsWithAudio", StringComparison.Ordinal);
        Assert.True(start > 0);
        var method = source[start..];
        method = method[..method.IndexOf("private static string? ResolveAlreadyMovedPathUnderRoot", StringComparison.Ordinal)];

        // Derived from the audio's destination, and never independently renamed.
        Assert.Contains("Path.ChangeExtension(destinationPath, extension)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetUniqueDestinationPath", method, StringComparison.Ordinal);
        Assert.DoesNotContain("IOFile.Move(sidecarPath, GetUniqueDestinationPath", source, StringComparison.Ordinal);

        // An occupied destination is left alone rather than overwritten or renamed.
        Assert.Contains("if (IOFile.Exists(sidecarTarget))", method, StringComparison.Ordinal);
    }

    private static string ReadSource(string fileName)
        => File.ReadAllText(Path.Join(ResolveRepoRoot(), "DeezSpoTag.Web", "Services", fileName));

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Services")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}