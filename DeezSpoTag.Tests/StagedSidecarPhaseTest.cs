using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Guardrails for the staged sidecar phase.
/// </summary>
/// <remarks>
///     <para>
///         For both external-file operations the sidecar work happens while the audio is still staged, and it
///         travels with the audio into the library.
///     </para>
///     <para>
///         The previous arrangement moved first and then resolved sidecars against library track ids that only
///         existed because the move ingested the moved files. That is the library being populated purely so a
///         question can be asked about files that are still being enriched.
///     </para>
/// </remarks>
public sealed class StagedSidecarPhaseTest
{
    private static readonly string[] SidecarExtensions = [".lrc", ".ttml", ".jpg", ".png"];

    /// <summary>
    ///     Sidecars run before the move, not after it.
    /// </summary>
    [Fact]
    public void SidecarsRunBeforeTheMoveForBothExternalFileIntents()
    {
        var source = ReadSource("AutoTagService.JobLifecycle.cs");
        var method = source[source.IndexOf("RunSuccessPostProcessingAsync", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("private static bool IsTerminalRunStatus", StringComparison.Ordinal)];

        var stagedSidecars = method.IndexOf("RunExternalFileStagedSidecarsAsync(", StringComparison.Ordinal);
        var move = method.IndexOf("RunFinalAutoMoveAsync(", StringComparison.Ordinal);

        Assert.True(stagedSidecars > 0, "The staged sidecar phase is not called during success post-processing.");
        Assert.True(move > 0, "The final move is not called during success post-processing.");
        Assert.True(
            stagedSidecars < move,
            "Sidecars must run before the move, so they are written beside the staged audio and travel with it "
            + "into the library. Running them afterwards resolves every file a second time against ids that "
            + "only exist because the move ingested them.");

        // And the phase is reached through the shared predicate, not a manual-only check.
        Assert.Contains("if (isExternalFileEnrichment)", method, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The after-move sidecar branch is skipped once the staged phase has run.
    /// </summary>
    [Fact]
    public void TheAfterMoveSidecarBranchIsSkippedWhenTheStagedPhaseAlreadyRan()
    {
        var lifecycle = ReadSource("AutoTagService.JobLifecycle.cs");
        Assert.Contains("sidecarsAlreadyRunStaged: isExternalFileEnrichment", lifecycle, StringComparison.Ordinal);

        var workflows = ReadSource("AutoTagService.EnhancementWorkflows.cs");
        var method = workflows[workflows.IndexOf("private async Task RunIntegratedEnhancementWorkflowsAsync", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("RunConfiguredFolderUniformityAsync", StringComparison.Ordinal)];

        Assert.Contains("if (isExternalFileEnrichment && sidecarsAlreadyRunStaged)", method, StringComparison.Ordinal);

        // The early return must come before the ingestion and the moved-file sidecar call, or the second
        // pass still happens.
        var gate = method.IndexOf("if (isExternalFileEnrichment && sidecarsAlreadyRunStaged)", StringComparison.Ordinal);
        var ingest = method.IndexOf("IngestKnownFilesAfterAutoMoveAsync(", StringComparison.Ordinal);
        var afterMoveSidecars = method.IndexOf("RunManualEnrichmentBatchSidecarsAsync(", StringComparison.Ordinal);

        Assert.True(gate > 0 && gate < ingest, "The skip must precede the library ingestion.");
        Assert.True(gate < afterMoveSidecars, "The skip must precede the after-move sidecar call.");
    }

    /// <summary>
    ///     A staged file is never ingested into the library just to obtain a track id.
    /// </summary>
    [Fact]
    public void StagedFilesAreResolvedWithoutLibraryIngestion()
    {
        var source = ReadSource("AutoTagService.EnhancementWorkflows.cs");

        // The staged resolver is pure filesystem work.
        var start = source.IndexOf("private static IReadOnlyList<string> ResolveStagedSidecarFiles", StringComparison.Ordinal);
        Assert.True(start > 0, "ResolveStagedSidecarFiles was not found.");
        var body = source[start..];
        body = body[..body.IndexOf("private async Task<IReadOnlyList<string>> ResolveSidecarRunFilesAsync", StringComparison.Ordinal)];

        Assert.DoesNotContain("IngestAndVerifyAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_knownFileIngestionService", body, StringComparison.Ordinal);
        Assert.Contains("File.Exists", body, StringComparison.Ordinal);
        Assert.Contains("Distinct(StringComparer.OrdinalIgnoreCase)", body, StringComparison.Ordinal);

        // The flag has to actually select it at the call site, not merely exist.
        Assert.Contains(
            "resolveFileIdentityFromTags\n            ? ResolveStagedSidecarFiles(batchFiles)",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     A disabled sidecar category does no work at all.
    /// </summary>
    /// <remarks>
    ///     The check happens before any path is enumerated or any network call is planned, so a profile with
    ///     lyrics off never touches artwork and an entirely disabled profile costs no filesystem work.
    /// </remarks>
    [Fact]
    public void DisabledSidecarCategoriesDoNoWork()
    {
        var source = ReadSource("AutoTagService.EnhancementWorkflows.cs");
        var start = source.IndexOf("private async Task RunExternalFileStagedSidecarsAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var method = source[start..];
        method = method[..method.IndexOf("RunManualEnrichmentBatchSidecarsAsync", StringComparison.Ordinal)];

        var enabledCheck = method.IndexOf("if (!stagedSidecarEnabled || (!runLyrics && !runCovers))", StringComparison.Ordinal);
        var enumerate = method.IndexOf("var stagedFiles = fileOutcomes", StringComparison.Ordinal);
        var run = method.IndexOf("RunConfiguredSidecarsAsync(", StringComparison.Ordinal);

        Assert.True(enabledCheck > 0, "The disabled-category gate was not found.");
        Assert.True(enumerate > enabledCheck, "The gate must come before the staged files are enumerated.");
        Assert.True(
            run > enabledCheck,
            "The gate must come before the sidecar pass is invoked.");
    }

    /// <summary>
    ///     Only files the tagging chain actually enriched are offered to the sidecar phase.
    /// </summary>
    [Fact]
    public void OnlyEnrichedStagedFilesAreOfferedToSidecars()
    {
        var source = ReadSource("AutoTagService.EnhancementWorkflows.cs");
        var start = source.IndexOf("private async Task RunExternalFileStagedSidecarsAsync", StringComparison.Ordinal);
        var method = source[start..];
        method = method[..method.IndexOf("RunManualEnrichmentBatchSidecarsAsync", StringComparison.Ordinal)];

        // A file no platform could identify must not be sidecar-processed: it has no identity to look lyrics
        // up by, and pretending otherwise would report a lookup that could not have succeeded.
        Assert.Contains("entry.Value is { Tagged: true }", method, StringComparison.Ordinal);
        Assert.Contains("entry.Value is { CompletedWithoutChanges: true }", method, StringComparison.Ordinal);
        Assert.DoesNotContain("entry.Value is { Seen: true }", method, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The move carries the audio's sidecars into the library with it.
    /// </summary>
    [Fact]
    public void TheMoveCarriesTheAudiosSidecars()
    {
        var source = ReadSource("AutoTagDownloadMoveService.cs");

        Assert.Contains("MoveAdjacentSidecarsWithAudio(sourceIo, destinationPath);", source, StringComparison.Ordinal);

        var start = source.IndexOf("private static void MoveAdjacentSidecarsWithAudio", StringComparison.Ordinal);
        Assert.True(start > 0, "MoveAdjacentSidecarsWithAudio was not found.");
        var method = source[start..];
        method = method[..method.IndexOf("private static string? ResolveAlreadyMovedPathUnderRoot", StringComparison.Ordinal)];

        // Same stem, same filter the duplicate-quarantine path uses, and the name derived from the audio's
        // final destination so a renamed audio keeps its sidecar. Renaming the sidecar independently would
        // orphan it - see SoulseekStagedSidecarDeliveryTest for that case.
        Assert.Contains("Path.GetFileNameWithoutExtension(sourcePath)", method, StringComparison.Ordinal);
        Assert.Contains("EnhancementReplacementSidecarExtensions.Contains(extension)", method, StringComparison.Ordinal);
        Assert.Contains("Path.ChangeExtension(destinationPath, extension)", method, StringComparison.Ordinal);

        // A sidecar that cannot travel must never fail the audio move, which is the deliverable.
        Assert.Contains("is IOException or UnauthorizedAccessException", method, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Sidecars are matched by stem, so a track's own lyrics and cover travel with it.
    /// </summary>
    /// <remarks>
    ///     This is the whole reason the phase has to run beside the staged audio rather than writing to the
    ///     destination: a file written later, under the destination's name, would no longer match the audio's
    ///     stem and the move would not carry it.
    /// </remarks>
    [Theory]
    [InlineData(".lrc")]
    [InlineData(".ttml")]
    [InlineData(".jpg")]
    [InlineData(".png")]
    public void SidecarsSharingTheAudiosStemAreTheOnesThatTravel(string extension)
    {
        var source = ReadSource("AutoTagDownloadMoveService.cs");
        var filterStart = source.IndexOf("EnhancementReplacementSidecarExtensions = new", StringComparison.Ordinal);
        Assert.True(filterStart > 0, "The sidecar extension filter was not found.");
        var filter = source[filterStart..];
        filter = filter[..filter.IndexOf("];", StringComparison.Ordinal)];

        Assert.Contains($"\"{extension}\"", filter, StringComparison.Ordinal);
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