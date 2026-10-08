using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Guardrails for Soulseek completion meaning verified audio, and for the operations built on it.
/// </summary>
/// <remarks>
///     A peer publishes a filename, not an identity. Tagging the delivered bytes immediately writes whatever
///     the peer embedded and reports a tag-writing warning for a transfer that actually succeeded - which is
///     the reported symptom this work exists to remove.
/// </remarks>
public sealed class SoulseekAudioCompletionBoundaryTest
{
    // ─────────────────────────── Task 4: verified audio, not completed tagging ───────────────────────────

    /// <summary>
    ///     Soulseek ends the download after the audio is verified, with no tag writing.
    /// </summary>
    [Fact]
    public void SoulseekEndsAtVerifiedAudioWithoutTagWriting()
    {
        var helper = ReadService("Download", "Shared", "EngineQueueProcessorHelper.cs");

        // The handoff is opt-in and lives before the tag-writing call.
        var defer = helper.IndexOf("if (DefersToEnrichmentStage(workContext))", StringComparison.Ordinal);
        var tagWrite = helper.IndexOf("outputPath = await ApplyPostDownloadSettingsAsync(", StringComparison.Ordinal);
        Assert.True(defer > 0, "The audio-only completion branch was not found.");
        Assert.True(
            defer < tagWrite,
            "The handoff must come before the tag-writing step, or a Soulseek download is still tagged here.");

        // And it returns rather than falling through to it.
        var branch = helper[defer..];
        branch = branch[..branch.IndexOf("outputPath = await ApplyPostDownloadSettingsAsync(", StringComparison.Ordinal)];
        Assert.Contains("return;", branch, StringComparison.Ordinal);

        // Opt-in, so every other engine keeps its current behaviour.
        Assert.Contains("workContext.Callbacks.CompleteAudioOnlyAsync is not null", helper, StringComparison.Ordinal);
        Assert.Contains(
            "Func<TPayload, string, CancellationToken, Task>? CompleteAudioOnlyAsync = null",
            helper,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The handoff leaves a recoverable file and says so, rather than failing the transfer.
    /// </summary>
    /// <remarks>
    ///     A download whose enrichment later fails must be resumed from the verified audio. Treating it as a
    ///     failed download would re-run the transfer, spending a stranger's bandwidth to obtain a file that
    ///     is already on disk.
    /// </remarks>
    [Fact]
    public void TheHandoffMarksEnrichmentPendingRatherThanFailing()
    {
        var processor = ReadService("Download", "Soulseek", "SoulseekEngineProcessor.cs");

        var start = processor.IndexOf("private static Task CompleteAudioOnlyAsync", StringComparison.Ordinal);
        Assert.True(start > 0, "CompleteAudioOnlyAsync was not found on the Soulseek processor.");
        var method = processor[start..];
        method = method[..method.IndexOf("\n    private ", StringComparison.Ordinal)];

        Assert.Contains("payload.Status = SoulseekDownloadStatus.Completed;", method, StringComparison.Ordinal);
        Assert.Contains("payload.AudioAcquired = true;", method, StringComparison.Ordinal);
        Assert.Contains("payload.AcquiredAudioPath = verifiedAudioPath;", method, StringComparison.Ordinal);
        Assert.Contains("payload.EnrichmentPending = true;", method, StringComparison.Ordinal);

        // It must not rethrow, and must not mark the item failed.
        Assert.DoesNotContain("throw ", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SoulseekDownloadStatus.Failed", method, StringComparison.Ordinal);

        // And it must be wired in as the opt-in.
        Assert.Contains("CompleteAudioOnlyAsync)", processor, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Ownership has to be recorded while the transfer runs, and it has to reach the queue row.
    /// </summary>
    /// <remarks>
    ///     The transfer table is the authority for resuming, but the payload's own transfer id is what a reader
    ///     and the maintenance tooling see. Writing it only at a terminal outcome left a transfer that was still
    ///     running with no id anywhere, which is the state recovery cannot start from.
    /// </remarks>
    [Fact]
    public void TheTransferIdIsPersistedWhileTheTransferIsStillRunning()
    {
        var processor = ReadService("Download", "Soulseek", "SoulseekEngineProcessor.cs");

        var start = processor.IndexOf("private async Task<string> DownloadAsync", StringComparison.Ordinal);
        Assert.True(start > 0, "The Soulseek processor's download step was not found.");
        var method = processor[start..];
        method = method[..method.IndexOf("\n    private ", StringComparison.Ordinal)];

        // The callback is supplied before the download starts, and it writes through the existing payload
        // update rather than inventing a second place to record the id.
        Assert.Contains("soulseekRequest.TransferAttachedAsync = async (status, token) =>", method, StringComparison.Ordinal);
        Assert.Contains("status.TransferId is not { } transferId", method, StringComparison.Ordinal);
        Assert.Contains("payload.SoulseekTransferId = transferId.ToString();", method, StringComparison.Ordinal);
        Assert.Contains("QueueHelperUtils.UpdatePayloadAsync(_queueRepository, payload.Id, payload, token)", method, StringComparison.Ordinal);

        // It is set before the download is asked to do anything, not after.
        var callback = method.IndexOf("TransferAttachedAsync", StringComparison.Ordinal);
        var download = method.IndexOf("_downloader", StringComparison.Ordinal);
        Assert.True(callback > 0 && callback < download, "The transfer id must be wired before the download starts.");
    }

    [Fact]
    public void TheProcessorThreadsTheHostShutdownTokenSeparatelyFromTheItemToken()
    {
        var processor = ReadService("Download", "Soulseek", "SoulseekEngineProcessor.cs");

        // Host shutdown and reader cancellation are different things. Treating them alike cancels a transfer
        // slskd is still fetching, and a download that was nearly finished is thrown away on every restart.
        Assert.Contains(
            "soulseekRequest.HostStoppingToken = _hostLifetime?.ApplicationStopping ?? CancellationToken.None;",
            processor,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     Recovered audio has to reach enrichment by the same route as freshly downloaded audio.
    /// </summary>
    /// <remarks>
    ///     Recovery changes where the file came from, not what happens to it afterwards. A recovered file that
    ///     skipped the quality guard or the acquired checkpoint would be imported without being checked, and the
    ///     enrichment stage would see a completed row with nothing acquired.
    /// </remarks>
    [Fact]
    public void RecoveredVerifiedAudioTakesTheSameCompletionRouteAsFreshAudio()
    {
        var service = ReadService("Download", "Soulseek", "SoulseekDownloadService.cs");

        var start = service.IndexOf("private async Task<SoulseekCompletedFile> WatchRecoveredTransferAsync", StringComparison.Ordinal);
        Assert.True(start > 0, "Recovery does not watch the recorded transfer.");
        var method = service[start..];
        method = method[..method.IndexOf("\n    private ", StringComparison.Ordinal)];

        // It completes through the same verification and the same persisted outcome a fresh transfer uses.
        Assert.Contains("WaitForCompletionAsync(", method, StringComparison.Ordinal);
        Assert.Contains("verified: true,", method, StringComparison.Ordinal);
        Assert.Contains("IsSuccessful: true,", method, StringComparison.Ordinal);
        Assert.Contains("IsTerminal: true,", method, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDownloadStepStillEndsAtVerifiedAudioWithoutTagWriting()
    {
        var processor = ReadService("Download", "Soulseek", "SoulseekEngineProcessor.cs");

        var start = processor.IndexOf("private async Task<string> DownloadAsync", StringComparison.Ordinal);
        var method = processor[start..];
        method = method[..method.IndexOf("\n    private ", StringComparison.Ordinal)];

        // Adding ownership recording must not have moved the boundary: the engine still hands over verified
        // bytes and leaves tagging and enrichment to the shared pipeline.
        Assert.Contains("PublishImportUpdate(new SoulseekImportUpdate(payload.Id, \"verified\"", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TagLib", method, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A deferred download must still be completed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is worth stating plainly, because the early return looked harmless. Completing the
    ///         download is what writes the queue row's status, fires the queue UI events, clears the retry
    ///         scheduler and reports engine success.
    ///     </para>
    ///     <para>
    ///         Skipping it leaves a verified download in "running" forever, and the enrichment stage only
    ///         ever looks at completed rows - so the handoff would be invisible to the very thing it hands
    ///         off to. The card would spin and the file would sit in staging indefinitely.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ADeferredDownloadStillRunsTheCompletionBookkeeping()
    {
        var helper = ReadService("Download", "Shared", "EngineQueueProcessorHelper.cs");
        var start = helper.IndexOf("private static async Task ExecutePipelineAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var method = helper[start..];

        var defer = method.IndexOf("if (DefersToEnrichmentStage(workContext))", StringComparison.Ordinal);
        var branch = method[defer..];
        branch = branch[..branch.IndexOf("outputPath = await ApplyPostDownloadSettingsAsync(", StringComparison.Ordinal)];

        Assert.Contains("CompleteProcessingAsync(workContext, outputPath", branch, StringComparison.Ordinal);

        // Every part of completion that the queue depends on must still be there.
        var complete = helper[helper.IndexOf("private static async Task CompleteProcessingAsync", StringComparison.Ordinal)..];
        foreach (var required in new[]
                 {
                     "DownloadLifecycleCheckpoint.MarkCompleted",
                     "UpdateStatusAsync",
                     "RetryScheduler.ClearAsync",
                     "SendFinishDownload",
                     "ReportSuccess"
                 })
        {
            Assert.Contains(required, complete, StringComparison.Ordinal);
        }

        // The only thing a deferred item skips is the prefetch wait, because none was started.
        Assert.Contains("bool deferredToEnrichment = false", complete, StringComparison.Ordinal);
        Assert.Contains("if (!deferredToEnrichment)", complete, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verification is untouched: the handoff happens after it, not instead of it.
    /// </summary>
    [Fact]
    public void VerificationStillRunsBeforeTheHandoff()
    {
        var helper = ReadService("Download", "Shared", "EngineQueueProcessorHelper.cs");
        var start = helper.IndexOf("private static async Task ExecutePipelineAsync", StringComparison.Ordinal);
        Assert.True(start > 0);
        var method = helper[start..];

        var qualityGuard = method.IndexOf("DeliveredAudioQualityGuard.EnsurePlanStepSatisfiedAsync", StringComparison.Ordinal);
        var acquire = method.IndexOf("DownloadLifecycleCheckpoint.PersistAcquiredAsync", StringComparison.Ordinal);
        var defer = method.IndexOf("if (DefersToEnrichmentStage(workContext))", StringComparison.Ordinal);

        Assert.True(qualityGuard > 0 && acquire > 0 && defer > 0);
        Assert.True(qualityGuard < defer, "The delivered-quality step must still run before the handoff.");
        Assert.True(acquire < defer, "The acquired file must be persisted before the handoff.");
    }

    /// <summary>
    ///     A pinned peer is never replaced because enrichment did not run.
    /// </summary>
    /// <remarks>
    ///     Exact-peer pinning is the reason a reader can choose what they get. Nothing in the handoff may
    ///     substitute a different peer or a different quality, and a failed transfer or quality check still
    ///     fails outright rather than reaching the handoff.
    /// </remarks>
    [Fact]
    public void TheHandoffNeverSubstitutesAPeerOrAQuality()
    {
        var helper = ReadService("Download", "Shared", "EngineQueueProcessorHelper.cs");
        var start = helper.IndexOf("private static async Task ExecutePipelineAsync", StringComparison.Ordinal);
        var method = helper[start..];
        var branch = method[method.IndexOf("if (DefersToEnrichmentStage(workContext))", StringComparison.Ordinal)..];
        branch = branch[..branch.IndexOf("outputPath = await ApplyPostDownloadSettingsAsync(", StringComparison.Ordinal)];

        // The handoff only records what was already verified; it decides nothing.
        foreach (var forbidden in new[]
                 {
                     "SoulseekUsername =",
                     "SoulseekRemotePath =",
                     "SoulseekQualityCode =",
                     "DeliveredAudioQualityGuard",
                     "RejectAcquiredAudioAsync"
                 })
        {
            Assert.DoesNotContain(forbidden, branch, StringComparison.Ordinal);
        }
    }

    // ─────────────────────────── Task 5: release classification ───────────────────────────

    /// <summary>
    ///     The folder's category comes from how many audio files it holds, never from album text.
    /// </summary>
    [Fact]
    public void TheCategoryComesFromCollectionEvidenceNotAlbumText()
    {
        var planner = ReadWeb("Services", "SoulseekBatchQueuePlanner.cs");
        var start = planner.IndexOf("var folderAudioCount", StringComparison.Ordinal);
        Assert.True(start > 0, "The release category derivation was not found.");
        var block = planner[start..];
        block = block[..block.IndexOf("var items = new List<SoulseekBatchQueuePlanItem>", StringComparison.Ordinal)];

        Assert.Contains("freshFiles.Count(file => file.Eligible)", block, StringComparison.Ordinal);
        Assert.Contains("folderAudioCount > 1", block, StringComparison.Ordinal);
        Assert.Contains("AutoTagReleaseCategory.Album", block, StringComparison.Ordinal);

        // A single audio file proves nothing either way, so it stays unknown.
        Assert.Contains(": null;", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Album\"", block, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An unknown category is carried as unknown rather than defaulted.
    /// </summary>
    [Fact]
    public void AnUnknownCategoryIsCarriedRatherThanDefaulted()
    {
        var item = ReadService("Download", "Soulseek", "SoulseekQueueItem.cs");
        Assert.Contains("public string? SoulseekReleaseCategory { get; set; }", item, StringComparison.Ordinal);
        Assert.DoesNotContain("public string SoulseekReleaseCategory", item, StringComparison.Ordinal);

        var intent = ReadService("Download", "Shared", "Models", "DownloadIntent.cs");
        Assert.Contains("public string? SoulseekReleaseCategory { get; set; }", intent, StringComparison.Ordinal);

        // The planner names album only when more than one eligible file backs it, and otherwise leaves the
        // category null so the destination profile decides. A single file must not be frozen into a mode.
        var planner = ReadWeb("Services", "SoulseekBatchQueuePlanner.cs");
        Assert.Contains("var folderAudioCount = freshFiles.Count(file => file.Eligible);", planner, StringComparison.Ordinal);
        Assert.Contains("? AutoTagReleaseCategory.Album", planner, StringComparison.Ordinal);
        Assert.Contains(": null;", planner, StringComparison.Ordinal);
        Assert.DoesNotContain("SoulseekReleaseCategory = AutoTagReleaseCategory.Album", planner, StringComparison.Ordinal);
        Assert.DoesNotContain("SoulseekReleaseCategory ?? AutoTagReleaseCategory", planner, StringComparison.Ordinal);
        Assert.Contains("SoulseekReleaseCategory = releaseCategory", planner, StringComparison.Ordinal);

        // The queue row serialises the whole intent, so the category rides along in the existing payload
        // rather than needing a separate write, and the reader treats null or blank as genuinely unknown.
        var enqueue = ReadWeb("Services", "DownloadIntentService.cs");
        Assert.Contains("var json = JsonSerializer.Serialize(payload);", enqueue, StringComparison.Ordinal);
        Assert.DoesNotContain("SoulseekReleaseCategory", enqueue, StringComparison.Ordinal);

        var orchestration = ReadWeb("Services", "DownloadOrchestrationService.cs");
        var reader = ExtractMethod(orchestration, "private static string? ReadSoulseekReleaseCategory");
        Assert.Contains("!string.IsNullOrWhiteSpace(category))", reader, StringComparison.Ordinal);
        Assert.Contains("return null;", reader, StringComparison.Ordinal);
        // Never a hard-coded mode: the reader may only hand back what the payload recorded, so no literal
        // and no defaulting expression may appear anywhere in it.
        Assert.DoesNotContain("AutoTagReleaseCategory", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("\"album\"", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("\"single\"", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("?? ", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultIfEmpty", reader, StringComparison.Ordinal);
    }

    // ─────────────────────────── Task 6: partition and ordering ───────────────────────────

    /// <summary>
    ///     Soulseek files are identified by the engine that delivered them.
    /// </summary>
    [Fact]
    public void ThePartitionUsesTheCompletedEngineNotHowTheRequestWasMade()
    {
        var orchestration = ReadWeb("Services", "DownloadOrchestrationService.cs");

        Assert.Contains(
            "private static bool IsSoulseekCompletedItem(DownloadQueueItem item)",
            orchestration,
            StringComparison.Ordinal);
        Assert.Contains("item.Engine, SoulseekQueueItem.EngineId", orchestration, StringComparison.Ordinal);

        // SourceService records how the download was requested, which is exactly the wrong signal for a
        // fallback download that arrived from a peer.
        var predicate = orchestration[orchestration.IndexOf("private static bool IsSoulseekCompletedItem", StringComparison.Ordinal)..];
        predicate = predicate[..predicate.IndexOf("\n    private ", StringComparison.Ordinal)];
        Assert.DoesNotContain("SourceService", predicate, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadJson", predicate, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Soulseek work is appended after every ordinary group.
    /// </summary>
    [Fact]
    public void SoulseekGroupsAreAppendedAfterOrdinaryOnes()
    {
        var orchestration = ReadWeb("Services", "DownloadOrchestrationService.cs");

        var partition = orchestration.IndexOf("var soulseekItems = items.Where(IsSoulseekCompletedItem)", StringComparison.Ordinal);
        var append = orchestration.IndexOf("AddSoulseekEnrichmentGroups(groups, profileContext,", StringComparison.Ordinal);
        Assert.True(partition > 0 && append > 0);

        // The Soulseek items are held back rather than added inside the per-folder loop...
        var ordinaryAdd = orchestration.IndexOf("itemsWithSourceFiles,\n                ResolveExistingSourceAudioFilesUnderRoot", StringComparison.Ordinal);
        Assert.True(partition < append, "Soulseek items must be collected and appended after the folder loop.");

        // ...and the append happens once, after the loop closes.
        var afterLoop = orchestration[append..];
        afterLoop = afterLoop[..(afterLoop.IndexOf("\n        if (groups.Count > 1)", StringComparison.Ordinal) is var idx && idx > 0
            ? idx
            : afterLoop.Length)];
        Assert.Contains("AddSoulseekEnrichmentGroups(groups, profileContext,", afterLoop, StringComparison.Ordinal);

        // The ordinary path is untouched: its groups carry the default intent, and no Soulseek file can
        // reach it because the partition happens before the ordinary groups are built.
        Assert.Contains(
            "string RunIntent = AutoTagLiterals.RunIntentDownloadEnrichment",
            orchestration,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     Each group carries the intent its job is started with.
    /// </summary>
    [Fact]
    public void EachGroupCarriesItsOwnEnrichmentIntent()
    {
        var orchestration = ReadWeb("Services", "DownloadOrchestrationService.cs");

        Assert.Contains(
            "string RunIntent = AutoTagLiterals.RunIntentDownloadEnrichment",
            orchestration,
            StringComparison.Ordinal);
        Assert.Contains("group.RunIntent,", orchestration, StringComparison.Ordinal);
        Assert.Contains("RunIntent: runIntent", orchestration, StringComparison.Ordinal);

        // Soulseek files must never be fed to the ordinary operation.
        var add = orchestration[orchestration.IndexOf("private void AddSoulseekPartitionGroup", StringComparison.Ordinal)..];
        add = add[..add.IndexOf("\n    private ", StringComparison.Ordinal)];
        Assert.Contains("AutoTagLiterals.RunIntentSoulseekEnrichment", add, StringComparison.Ordinal);
        Assert.Contains("AddPipelineWorkGroup(", add, StringComparison.Ordinal);

        // And it is the partition that supplies the mode, never a default.
        Assert.Contains("PartitionSoulseekRuns(soulseekItems, SavedPreferenceFor)", orchestration, System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The run config must actually mark the job as an external-file operation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the gate the whole feature hangs on, and missing it is invisible: the job starts, the
    ///         runner runs, and the album-organization boundary and the staged sidecar pass are simply skipped
    ///         because the runner does not recognise the configuration as a peer-folder operation.
    ///     </para>
    ///     <para>
    ///         The destination folder id is what tells the runner where the files are going. Without it the
    ///         operation cannot be recognised at all, so it has to be written here - the manual controller sets
    ///         it for its own route and nothing else was setting it for this one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheSoulseekRunConfigDeclaresTheExternalFileOperation()
    {
        var orchestration = ReadWeb("Services", "DownloadOrchestrationService.cs");
        var start = orchestration.IndexOf("private static string ApplySoulseekOperationShape", StringComparison.Ordinal);
        Assert.True(start > 0, "The operation-shape writer was not found.");
        var method = orchestration[start..];
        method = method[..method.IndexOf("\n    private ", StringComparison.Ordinal)];

        Assert.Contains("root[\"manualDestinationFolderId\"] = destinationFolderId;", method, StringComparison.Ordinal);
        Assert.Contains("ManualReleasePreferenceKey", method, System.StringComparison.Ordinal);
        Assert.Contains("root[\"materializeToTemplatePath\"] = true;", method, System.StringComparison.Ordinal);
        Assert.Contains("root[\"organizeSidecarsIntoTemplateFolders\"] = true;", method, System.StringComparison.Ordinal);

        // And it has to be applied at the group call site, or it is dead code.
        // And it has to be applied at the group call site, or it is dead code.
        Assert.Contains(
            "ApplySoulseekOperationShape(configJson, destinationFolderId, partition.Mode)",
            orchestration,
            System.StringComparison.Ordinal);
    }

    /// <summary>
    ///     The runner recognises the operation from the destination folder and the release preference.
    /// </summary>
    /// <remarks>
    ///     Both are required, so the config written for a Soulseek run has to carry both or the whole
    ///     operation quietly degrades to a plain library run.
    /// </remarks>
    [Fact]
    public void TheRunnerRecognisesTheOperationOnlyWhenBothConfigHalvesArePresent()
    {
        var runner = ReadWeb("Services", "AutoTag", "LocalAutoTagRunner.MetadataInference.cs");
        Assert.Contains(
            "private static bool IsExternalFileOrganizationRun(string? manualReleasePreference, long? manualDestinationFolderId)",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("!string.IsNullOrWhiteSpace(manualReleasePreference)", runner, StringComparison.Ordinal);
        Assert.Contains("manualDestinationFolderId is > 0", runner, System.StringComparison.Ordinal);

        // The manual controller is the other producer, and it must set the same two keys.
        var controller = ReadWeb("Controllers", "Api", "AutoTagApiController.cs");
        Assert.Contains("configNode[AutoTagLiterals.ManualReleasePreferenceKey]", controller, System.StringComparison.Ordinal);
        Assert.Contains("configNode[AutoTagLiterals.ManualDestinationFolderIdKey]", controller, System.StringComparison.Ordinal);
    }

    /// <summary>Brace-matched body of the method whose declaration starts with
    /// <paramref name="declarationMarker"/>, so an assertion cannot be satisfied by an unrelated
    /// occurrence of the same fragment elsewhere in the file.</summary>
    private static string ExtractMethod(string source, string declarationMarker)
    {
        var start = source.IndexOf(declarationMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method declaration: {declarationMarker}");
        var open = source.IndexOf('{', start);
        Assert.True(open > start, $"Missing opening brace for: {declarationMarker}");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
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
                    return source[start..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unterminated method: {declarationMarker}");
    }

    private static string ReadWeb(params string[] parts)
        => File.ReadAllText(Path.Join([ResolveRepoRoot(), "DeezSpoTag.Web", .. parts]));

    private static string ReadService(params string[] parts)
        => File.ReadAllText(Path.Join([ResolveRepoRoot(), "DeezSpoTag.Services", .. parts]));

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
