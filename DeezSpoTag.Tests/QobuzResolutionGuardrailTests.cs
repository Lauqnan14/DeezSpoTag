using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QobuzResolutionGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void QobuzProcessor_ValidatesCandidateBeforePersistingStagingPath()
    {
        var source = ReadSource("DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs");

        var resolveIndex = source.IndexOf(
            "var resolvedTrack = await ResolveAndPersistPreferredTrackAsync",
            StringComparison.Ordinal);
        var contextIndex = source.IndexOf(
            "var context = await BuildTrackContextAsync",
            StringComparison.Ordinal);
        var persistIndex = source.IndexOf(
            "PersistExpectedStagingPathAsync",
            StringComparison.Ordinal);
        var failureIndex = source.IndexOf(
            "Qobuz track not found for ISRC or metadata.",
            StringComparison.Ordinal);

        Assert.True(resolveIndex >= 0);
        Assert.Contains("ValidateQobuzUrlTrackSelectionAsync", source, StringComparison.Ordinal);
        Assert.Contains("_qobuzTrackResolver.ValidateTrackIdAsync", source, StringComparison.Ordinal);
        Assert.Contains("new QobuzTrackResolution(track, \"resolved_url\", 20)", source, StringComparison.Ordinal);
        Assert.Contains("new QobuzTrackResolution(validated.Track, \"validated_url\", validated.Score)", source, StringComparison.Ordinal);
        Assert.Contains("if (sourceSelection.HasTrackUrl)", source, StringComparison.Ordinal);
        Assert.Contains("payload.ResolutionStatus = QueuePreResolutionPayload.Resolved", source, StringComparison.Ordinal);
        Assert.True(contextIndex > resolveIndex);
        Assert.True(persistIndex > contextIndex);
        Assert.True(failureIndex > resolveIndex && failureIndex < contextIndex);
    }

    [Fact]
    public void QobuzDirectEnqueue_UsesSingleDedupeService()
    {
        var controller = ReadSource("DeezSpoTag.Web/Controllers/Api/QobuzDownloadApiController.cs");
        var controllerServices = ReadSource("DeezSpoTag.Web/Services/DownloadControllerServices.cs");
        var helper = ReadSource("DeezSpoTag.Web/Controllers/Api/DownloadQueueEnqueueHelper.cs");

        Assert.Contains("DownloadControllerServices services", controller, StringComparison.Ordinal);
        Assert.Contains("_dedupeService = services.DedupeService", controller, StringComparison.Ordinal);
        Assert.Contains("DownloadDedupeService DedupeService", controllerServices, StringComparison.Ordinal);
        Assert.Contains("await dedupeService.CheckAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("RequeueAsync(existing.QueueUuid", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void QobuzResolution_UsesSingleAuthoritativeResolverPath()
    {
        var songLinkResolver = ReadSource("DeezSpoTag.Services/Download/Utils/SongLinkResolver.cs");
        var downloadIntentService = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        Assert.DoesNotContain("TryResolveQobuzUrlViaPublicSearchAsync", songLinkResolver, StringComparison.Ordinal);
        Assert.DoesNotContain("PickBestQobuzCandidate", songLinkResolver, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchQobuzCandidatesByQueriesAsync", songLinkResolver, StringComparison.Ordinal);
        Assert.Contains("return await TryResolveQobuzUrlViaMetadataServiceAsync(normalizedIsrc, cancellationToken);", songLinkResolver, StringComparison.Ordinal);
        Assert.Contains("var resolverResult = await TryResolveQobuzUrlViaResolverAsync", songLinkResolver, StringComparison.Ordinal);

        Assert.Contains("var validated = await _qobuzTrackResolver.ValidateTrackIdAsync", downloadIntentService, StringComparison.Ordinal);
        Assert.Contains("Rejected Qobuz mapped URL that did not match requested track", downloadIntentService, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadRouting_DoesNotUseAvailabilityAsHardEngineGate()
    {
        var downloadIntentService = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        Assert.DoesNotContain("FilterAutoSourcesByAvailability", downloadIntentService, StringComparison.Ordinal);
        Assert.DoesNotContain("reason = \"unavailable\";", downloadIntentService, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoMapping_DoesNotAcceptQobuzWithoutResolvableUrl()
    {
        var downloadIntentService = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        Assert.Contains(
            "candidateEngine is DeezerPlatform or QobuzPlatform or TidalPlatform or AmazonPlatform or ApplePlatform",
            downloadIntentService,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TidalAtmosSecondaryQueue_UsesResolvedAtmosTrackOnly()
    {
        var downloadIntentService = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");
        var tidalAtmosMethodStart = downloadIntentService.IndexOf(
            "private async Task<bool> TryEnqueueTidalAtmosSecondaryAsync",
            StringComparison.Ordinal);
        var nextMethodStart = downloadIntentService.IndexOf(
            "private static string[] ResolveAtmosEngineOrder",
            StringComparison.Ordinal);
        Assert.True(tidalAtmosMethodStart >= 0);
        Assert.True(nextMethodStart > tidalAtmosMethodStart);

        var tidalAtmosMethod = downloadIntentService[tidalAtmosMethodStart..nextMethodStart];

        Assert.Contains("ResolveAtmosTrackAsync", tidalAtmosMethod, StringComparison.Ordinal);
        Assert.Contains("if (string.IsNullOrWhiteSpace(tidalAtmosUrl))", tidalAtmosMethod, StringComparison.Ordinal);
        Assert.Contains("payload.Isrc = FirstNonEmpty(resolvedAtmosTrack?.Isrc, request.Intent.Isrc)", tidalAtmosMethod, StringComparison.Ordinal);
        Assert.Contains("ResolveResolvedAlbumForAtmos(request.Intent.Album, resolvedAtmosTrack?.Album)", tidalAtmosMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveIntentAsync", tidalAtmosMethod, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
