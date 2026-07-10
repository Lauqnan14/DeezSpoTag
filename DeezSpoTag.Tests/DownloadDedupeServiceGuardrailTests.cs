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
        Assert.Contains("await _dedupeService.CheckAsync(BuildDedupeRequest(context), cancellationToken)", source, StringComparison.Ordinal);
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
    public void QueueDuplicateLookup_TreatsAllRowsAsAuthoritativeUntilCleared()
    {
        var source = ReadSource("DeezSpoTag.Services", "Download", "Queue", "DownloadQueueRepository.cs");

        Assert.Contains("Math.Abs(item.DurationMs.Value - request.DurationMs.Value) <= 2000", source, StringComparison.Ordinal);
        Assert.Contains("AddFinalDestinationPaths(item.FinalDestinationsJson, paths);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCompletedStatus(item.Status) && !HasExistingMaterializedFile(item)", source, StringComparison.Ordinal);
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
        var queueRepository = ReadSource("DeezSpoTag.Services", "Download", "Queue", "DownloadQueueRepository.cs");

        Assert.Contains("QobuzTrackId", service, StringComparison.Ordinal);
        Assert.Contains("TidalTrackId", service, StringComparison.Ordinal);
        Assert.Contains("AmazonTrackId", service, StringComparison.Ordinal);
        Assert.Contains("yield return (QobuzPlatform, request.QobuzTrackId);", service, StringComparison.Ordinal);
        Assert.Contains("yield return (TidalPlatform, request.TidalTrackId);", service, StringComparison.Ordinal);
        Assert.Contains("yield return (AmazonPlatform, request.AmazonTrackId);", service, StringComparison.Ordinal);
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

        var qobuz = ReadSource("DeezSpoTag.Services", "Download", "Qobuz", "QobuzEngineProcessor.cs");
        var apple = ReadSource("DeezSpoTag.Services", "Download", "Apple", "AppleEngineProcessor.cs");
        Assert.Contains("!EngineAudioPostDownloadHelper.IsFinalDestinationDedupeBlock(error)", qobuz, StringComparison.Ordinal);
        Assert.Contains("!EngineAudioPostDownloadHelper.IsFinalDestinationDedupeBlock(reason)", apple, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkPrefetch_PersistsResolvedAppleIdentityBeforeArtworkWork()
    {
        var source = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineAudioPostDownloadHelper.cs");

        var resolveIndex = source.IndexOf("var appleIdentity = await ResolveAppleArtworkIdentityAsync", StringComparison.Ordinal);
        var persistIndex = source.IndexOf("execution.Request.QueueRepository.UpdatePayloadAsync", resolveIndex, StringComparison.Ordinal);
        var artworkIndex = source.IndexOf("ResolveStandardAudioCoverUrlsAsync", resolveIndex, StringComparison.Ordinal);
        Assert.True(resolveIndex >= 0 && persistIndex > resolveIndex && artworkIndex > persistIndex);
    }

    [Fact]
    public async Task FinalDestinationDedupe_AllowsExistingDestinationOnlyForHigherQuality()
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
        var source = ReadSource("DeezSpoTag.Web", "Services", "PlaylistWatchService.cs");
        var selection = ExtractBetween(
            source,
            "private async Task<PlaylistTrackSelection> SelectMissingPlaylistTracksAsync",
            "private readonly record struct PreQueueDedupeHandledResult");

        Assert.Contains("dedupeService.CheckAsync", selection, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveLocalCandidateIdsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddLocalMetadataMatchesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryHandleKnownPlaylistTrackAsync", source, StringComparison.Ordinal);
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
