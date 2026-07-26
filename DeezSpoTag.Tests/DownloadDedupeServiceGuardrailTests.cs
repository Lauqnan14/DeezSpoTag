using System;
using System.IO;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadDedupeServiceGuardrailTests
{
    [Fact]
    public void DownloadIntentService_UsesSingleDedupeService()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");

        Assert.Contains("private readonly DownloadDedupeService _dedupeService;", source, StringComparison.Ordinal);
        Assert.Contains("var finalOutputPath = await ResolveExpectedFinalOutputPathAsync(payload, context, cancellationToken);", source, StringComparison.Ordinal);
        Assert.Contains("DownloadEngineSettingsHelper.ResolveAndApplyProfileAsync", source, StringComparison.Ordinal);
        Assert.Contains("await _dedupeService.CheckAsync(BuildDedupeRequest(context, finalOutputPath), cancellationToken)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryValidateLibraryDuplicateStateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveQueueDuplicateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBlockByGlobalBlocklistAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBlockByRuleSet", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectEngineEnqueueHelper_UsesSingleDedupeService()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "DownloadQueueEnqueueHelper.cs");

        Assert.Contains("DownloadDedupeService dedupeService", source, StringComparison.Ordinal);
        Assert.Contains("await dedupeService.CheckAsync", source, StringComparison.Ordinal);
        Assert.Contains("DownloadDedupeService.FromQueuePayload", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDuplicateAsync(duplicateRequest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RequeueAsync(existing.QueueUuid", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueDedupe_DoesNotIgnoreCompletedRowsAfterMaterializedFilesAreGone()
    {
        var source = ReadSource("DeezSpoTag.Services", "Download", "Queue", "DownloadQueueRepository.cs");
        var orchestration = ReadSource("DeezSpoTag.Web", "Services", "DownloadOrchestrationService.cs");

        Assert.DoesNotContain("IsStaleCompletedDuplicate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCompletedStatus(item.Status) && !HasExistingMaterializedFile(item)", source, StringComparison.Ordinal);
        Assert.Contains("HasRecordedFinalDestination(item)", orchestration, StringComparison.Ordinal);
        Assert.Contains("already recorded final destinations; no staging artifact remains to finalize", orchestration, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalBlocklist_CoversTrackArtistAlbumAndGenre()
    {
        var source = ReadSource("DeezSpoTag.Services", "Library", "LibraryRepository.cs");
        var service = ReadSource("DeezSpoTag.Services", "Download", "DownloadDedupeService.cs");

        Assert.Contains("private const string GenreType = \"genre\";", source, StringComparison.Ordinal);
        Assert.Contains("field = 'genre'", source, StringComparison.Ordinal);
        Assert.Contains("request.Genres", service, StringComparison.Ordinal);
    }

    [Fact]
    public void DedupeIdentity_CoversAllDownloadEngines()
    {
        var service = ReadSource("DeezSpoTag.Services", "Download", "DownloadDedupeService.cs");
        var library = ReadSource("DeezSpoTag.Services", "Library", "LibraryRepository.cs");
        var queueRepository = ReadSource("DeezSpoTag.Services", "Download", "Queue", "DownloadQueueRepository.cs");

        Assert.Contains("QobuzTrackId", service, StringComparison.Ordinal);
        Assert.Contains("TidalTrackId", service, StringComparison.Ordinal);
        Assert.Contains("AmazonTrackId", service, StringComparison.Ordinal);
        Assert.Contains("ResolveLocalTrackIdentityAsync", service, StringComparison.Ordinal);
        Assert.Contains("RequestedAudioVariant", service, StringComparison.Ordinal);
        Assert.Contains("GetBestLocalQualityRankForTrackAsync", library, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildSourceChecks", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ExistsTrackSourceAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ExistsTrackByAlbumSourceAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ExistsLibraryDuplicateGloballyAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildMetadataArtists", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ExistsTrackByMetadataAsync", library, StringComparison.Ordinal);
        Assert.Contains("ReadPayloadString(root, \"QobuzId\", \"qobuzId\")", queueRepository, StringComparison.Ordinal);
        Assert.Contains("ReadPayloadString(root, \"TidalId\", \"tidalId\")", queueRepository, StringComparison.Ordinal);
        Assert.Contains("ReadPayloadString(root, \"AmazonId\", \"amazonId\")", queueRepository, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadIntentService_ForwardsAllEngineIdentitiesToDedupe()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");
        var requestBuilder = ExtractBetween(
            source,
            "private static DownloadDedupeRequest BuildDedupeRequest",
            "private static void PopulateStandardQueuePayload");
        var payloadIdentity = ExtractBetween(
            source,
            "private static PayloadIdentity BuildPayloadIdentity",
            "private static string? ResolvePayloadArtistId");

        Assert.Contains("QobuzTrackId = context.Identity.QobuzTrackId", requestBuilder, StringComparison.Ordinal);
        Assert.Contains("TidalTrackId = context.Identity.TidalTrackId", requestBuilder, StringComparison.Ordinal);
        Assert.Contains("AmazonTrackId = context.Identity.AmazonTrackId", requestBuilder, StringComparison.Ordinal);
        Assert.Contains("TryGetPayloadString(payload, \"QobuzId\")", payloadIdentity, StringComparison.Ordinal);
        Assert.Contains("TryGetPayloadString(payload, \"TidalId\")", payloadIdentity, StringComparison.Ordinal);
        Assert.Contains("TryGetPayloadString(payload, \"AmazonId\")", payloadIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDedupeIdentity_DoesNotUseGenericQueuePayloadIdAsSourceId()
    {
        var service = ReadSource("DeezSpoTag.Services", "Download", "DownloadDedupeService.cs");
        var resolver = ExtractBetween(
            service,
            "private static string? ResolvePayloadSourceId",
            "private static string? ResolveIntentSourceId");

        Assert.Contains("ReadStringProperty(payload, propertyName)", resolver, StringComparison.Ordinal);
        Assert.Contains("ExtractSourceTrackId(payload.SourceUrl, source)", resolver, StringComparison.Ordinal);
        Assert.Contains("ExtractSourceTrackId(payload.Url, source)", resolver, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.Id", resolver, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineDownloadServices_UseSharedFinalDestinationDedupe()
    {
        var dedupe = ReadSource("DeezSpoTag.Services", "Download", "DownloadDedupeService.cs");
        var requestBase = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineDownloadRequestBase.cs");
        var requestBuilder = ReadSource("DeezSpoTag.Services", "Download", "Shared", "RequestBuilderCommon.cs");
        var qobuz = ReadSource("DeezSpoTag.Services", "Download", "Qobuz", "QobuzDownloadService.cs");
        var tidal = ReadSource("DeezSpoTag.Services", "Download", "Tidal", "TidalDownloadService.cs");
        var amazon = ReadSource("DeezSpoTag.Services", "Download", "Amazon", "AmazonDownloadService.cs");

        Assert.Contains("CheckFinalDestinationAsync", dedupe, StringComparison.Ordinal);
        Assert.Contains("RequestedLocalQualityRank", requestBase, StringComparison.Ordinal);
        Assert.Contains("MediaQualityInference.MapRequestedNumericQualityToLocalRank", requestBuilder, StringComparison.Ordinal);
        Assert.Contains("MediaQualityInference.InferLocalQualityRankFromText", requestBuilder, StringComparison.Ordinal);
        Assert.Contains("CheckFinalDestinationAsync", qobuz, StringComparison.Ordinal);
        Assert.Contains("CheckFinalDestinationAsync", tidal, StringComparison.Ordinal);
        Assert.Contains("CheckFinalDestinationAsync", amazon, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveExistingDownloadPath", qobuz, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveExpectedExisting", qobuz, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanUnverifiedExpectedOutput", qobuz, StringComparison.Ordinal);
        Assert.DoesNotContain("TryFindExistingByIsrc(request.OutputDir", tidal, StringComparison.Ordinal);
        Assert.DoesNotContain("return existingPath", amazon, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalDestinationDedupe_RejectsExistingDestinationWithoutHigherQuality()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deezspotag-dedupe-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        try
        {
            var service = new DownloadDedupeService(null!, null!, NullLogger<DownloadDedupeService>.Instance);
            var decision = await DownloadDedupeService.CheckFinalDestinationAsync(new DownloadDedupeRequest
            {
                TrackTitle = "Track",
                TrackArtist = "Artist",
                RequestedLocalQualityRank = 2,
                FinalOutputPath = path
            });

            Assert.False(decision.Allowed);
            Assert.Equal("final_destination_quality_not_higher", decision.ReasonCode);
            Assert.Contains(path, decision.Message, StringComparison.Ordinal);
            Assert.StartsWith("Skipped before download:", decision.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PostDownloadFailure_DoesNotMarkArtworkFailedForFinalDestinationDedupe()
    {
        var source = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineAudioPostDownloadHelper.cs");

        Assert.Contains("if (!IsFinalDestinationDedupeBlock(failureMessage))", source, StringComparison.Ordinal);
        Assert.Contains("Skipped before download: final destination already contains", source, StringComparison.Ordinal);
        Assert.Contains("TryCompleteWatchlistFinalDestinationDedupeAsync", source, StringComparison.Ordinal);
        Assert.Contains("UpsertWatchlistFinalizationOutboxAsync", source, StringComparison.Ordinal);
        Assert.Contains("RequestAllPlaylistSyncAsync", source, StringComparison.Ordinal);

        var qobuz = ReadSource("DeezSpoTag.Services", "Download", "Qobuz", "QobuzEngineProcessor.cs");
        var apple = ReadSource("DeezSpoTag.Services", "Download", "Apple", "AppleEngineProcessor.cs");
        Assert.Contains("!EngineAudioPostDownloadHelper.IsFinalDestinationDedupeBlock(error)", qobuz, StringComparison.Ordinal);
        Assert.Contains("!EngineAudioPostDownloadHelper.IsFinalDestinationDedupeBlock(reason)", apple, StringComparison.Ordinal);
    }

    [Fact]
    public void CentralResolution_PersistsAppleIdentityBeforeArtworkWork()
    {
        var queueResolution = ReadSource("DeezSpoTag.Services", "Download", "Queue", "QueuePreResolutionPayload.cs");
        var artwork = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineAudioPostDownloadHelper.cs");

        Assert.Contains("AppleId", queueResolution, StringComparison.Ordinal);
        Assert.Contains("AppleAlbumId", queueResolution, StringComparison.Ordinal);
        Assert.Contains("AppleAlbumName", queueResolution, StringComparison.Ordinal);
        Assert.Contains("AppleArtistName", queueResolution, StringComparison.Ordinal);
        Assert.Contains("AppleIsrc", queueResolution, StringComparison.Ordinal);
        Assert.Contains("AppleDurationMs", queueResolution, StringComparison.Ordinal);
        Assert.Contains("ResolveAppleArtworkIdentity(execution)", artwork, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveAppleArtworkIdentityAsync", artwork, StringComparison.Ordinal);
        Assert.DoesNotContain("TrackIdentityResolutionRequest", artwork, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEngineProcessor_PassesAppleIdentityToAnimatedArtwork()
    {
        var source = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineQueueProcessorHelper.cs");

        Assert.Contains("AppleCoverLookupIdOverride: ResolveAppleArtworkOverride(workContext.Payload)", source, StringComparison.Ordinal);
        Assert.Contains("AnimatedArtworkAppleIdOverride: ResolveAppleArtworkOverride(workContext.Payload)", source, StringComparison.Ordinal);
        Assert.Contains("AppleCoverLookupIdOverride: ResolveAppleArtworkOverride(context.Payload)", source, StringComparison.Ordinal);
        Assert.Contains("AnimatedArtworkAppleIdOverride: ResolveAppleArtworkOverride(context.Payload)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QueuePayload_PreservesAppleIdForPostDownloadArtwork()
    {
        var source = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineQueueItemBase.cs");

        Assert.Contains("[\"appleId\"] = AppleId", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalDestinationDedupe_AllowsExistingDestinationOnlyForLossyToLosslessUpgrade()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deezspotag-dedupe-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        try
        {
            var service = new DownloadDedupeService(null!, null!, NullLogger<DownloadDedupeService>.Instance);
            var decision = await DownloadDedupeService.CheckFinalDestinationAsync(new DownloadDedupeRequest
            {
                TrackTitle = "Track",
                TrackArtist = "Artist",
                RequestedLocalQualityRank = 3,
                FinalOutputPath = path
            });

            Assert.True(decision.Allowed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FinalDestinationDedupe_RejectsLosslessToHiResAutoUpgrade()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deezspotag-dedupe-{Guid.NewGuid():N}.flac");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        try
        {
            var decision = await DownloadDedupeService.CheckFinalDestinationAsync(new DownloadDedupeRequest
            {
                TrackTitle = "Track",
                TrackArtist = "Artist",
                RequestedLocalQualityRank = 4,
                FinalOutputPath = path
            });

            Assert.False(decision.Allowed);
            Assert.Equal("final_destination_quality_not_higher", decision.ReasonCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LibraryExistsApi_UsesDedupeLibraryPresence()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "LibraryExistsApiController.cs");

        Assert.Contains("DownloadDedupeService _dedupeService", source, StringComparison.Ordinal);
        Assert.Contains("CheckLibraryPresenceAsync", source, StringComparison.Ordinal);
        Assert.Contains("IsAppleSource(source)", source, StringComparison.Ordinal);
        Assert.Contains("IsAmazonSource(source)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExistsInLibraryAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistSync_UsesCentralLocalTrackIdentityResolver()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");

        Assert.Contains("ResolveLocalTrackIdentityAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FindLocalTrackIdByMetadataAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetTrackIdsBySourceIdsAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Recommendations_FilterFinalPoolThroughDedupe()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "LibraryRecommendationService.cs");

        Assert.Contains("DownloadDedupeService DedupeService", source, StringComparison.Ordinal);
        Assert.Contains("FilterRecommendationCandidatesThroughDedupeAsync", source, StringComparison.Ordinal);
        Assert.Contains("await _dedupeService.CheckAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(track => !libraryIdSet.Contains", source, StringComparison.Ordinal);
        Assert.DoesNotContain("libraryIdSet.Contains(deezerId)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchlistSelection_DoesNotUseParallelLibraryLookup()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "WatchlistEngine.cs");
        var selection = ExtractBetween(
            source,
            "private async Task<PlaylistTrackSelection> SelectMissingPlaylistTracksAsync",
            "private readonly record struct PreQueueDedupeHandledResult");

        Assert.Contains("dedupeService.CheckAsync", selection, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveLocalCandidateIdsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddLocalMetadataMatchesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryHandleKnownPlaylistTrackAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BoomplayWatchlistMapping_DoesNotUseConcurrentTrackMatching()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "BoomplayWatchlistMappingService.cs");

        Assert.DoesNotContain("Task.WhenAll", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MaximumConcurrentMatches", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var track in tracks)", source, StringComparison.Ordinal);
        Assert.Contains("TrackResolutionLocks", source, StringComparison.Ordinal);
        Assert.Contains("await resolutionGate.Semaphore.WaitAsync", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, Path.Combine(relativeParts));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate source file.", Path.Combine(relativeParts));
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"{startMarker} was not found.");
        }

        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"{endMarker} was not found.");
        }

        return source[start..end];
    }
}
