using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QobuzResolutionGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void QobuzProcessor_ConsumesCentrallyResolvedIdentityBeforePersistingStagingPath()
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
        Assert.DoesNotContain("ValidateQobuzUrlTrackSelectionAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_qobuzTrackResolver", source, StringComparison.Ordinal);
        Assert.Contains("BuildResolvedUrlTrack(payload, trackId.Value, resolvedIsrc)", source, StringComparison.Ordinal);
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
        var downloadIntentService = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        Assert.Contains("ResolveQobuzUrlFromCentralIdentityAsync", downloadIntentService, StringComparison.Ordinal);
        Assert.Contains("var validated = await _qobuzTrackResolver.ValidateTrackIdAsync", downloadIntentService, StringComparison.Ordinal);
        Assert.Contains("Rejected Qobuz mapped URL that did not match requested track", downloadIntentService, StringComparison.Ordinal);
        Assert.DoesNotContain("SongLinkResolver", downloadIntentService, StringComparison.Ordinal);
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
        Assert.Contains("ResolveTrackIdentityMatrixAsync", tidalAtmosMethod, StringComparison.Ordinal);
        Assert.Contains("new[] { TidalPlatform }", tidalAtmosMethod, StringComparison.Ordinal);
        Assert.Contains("if (string.IsNullOrWhiteSpace(tidalAtmosUrl))", tidalAtmosMethod, StringComparison.Ordinal);
        Assert.Contains("payload.Isrc = FirstNonEmpty(resolvedAtmosTrack?.Isrc, request.Intent.Isrc)", tidalAtmosMethod, StringComparison.Ordinal);
        Assert.Contains("ResolveResolvedAlbumForAtmos(request.Intent.Album, resolvedAtmosTrack?.Album)", tidalAtmosMethod, StringComparison.Ordinal);
        Assert.Contains("payload.TidalId = resolvedTidalAtmosId", tidalAtmosMethod, StringComparison.Ordinal);
        Assert.Contains("IsAtmosSourceRequest(intent.ContentType, quality)", downloadIntentService, StringComparison.Ordinal);
        Assert.Contains("autoSources.Where(IsAtmosEncodedSource).ToList()", downloadIntentService, StringComparison.Ordinal);
        Assert.True(
            tidalAtmosMethod.LastIndexOf("payload.TidalId = resolvedTidalAtmosId", StringComparison.Ordinal)
            > tidalAtmosMethod.IndexOf("ApplyIntentMetadata(payload, request.Intent)", StringComparison.Ordinal));
        Assert.Contains("ResolveAmazonAtmosAvailabilityAsync", tidalAtmosMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveIntentAsync", tidalAtmosMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void AtmosResolution_DoesNotStopOnWeakProviderCandidates()
    {
        var tidalSource = ReadSource("DeezSpoTag.Services/Download/Tidal/TidalDownloadService.cs");
        var amazonSource = ReadSource("DeezSpoTag.Web/Services/AmazonMusicMetadataService.cs");
        var downloadIntentService = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        Assert.Contains("SearchTracksByIsrcAsync", tidalSource, StringComparison.Ordinal);
        Assert.Contains("BuildTidalNativeApiUrl", tidalSource, StringComparison.Ordinal);
        Assert.Contains("[\"isrc\"] = isrc.Trim()", tidalSource, StringComparison.Ordinal);
        Assert.Contains("HydrateTidalAtmosCandidatesAsync", tidalSource, StringComparison.Ordinal);
        Assert.Contains("maximumHydratedCandidates = 8", tidalSource, StringComparison.Ordinal);
        Assert.Contains("SearchTracksAsync(query, 25", tidalSource, StringComparison.Ordinal);
        Assert.Contains("sourceAlbum, string.Empty, expectedDuration", tidalSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchTracksFromAllSourcesAsync", tidalSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchTracksViaOauthAsync", tidalSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetTrackInfoByIdViaOauthAsync", tidalSource, StringComparison.Ordinal);
        Assert.Contains("if (publicTrack != null && HasTidalAtmosMode(publicTrack))", tidalSource, StringComparison.Ordinal);
        Assert.DoesNotContain("allTracks.AddRange(result.Where(HasTidalAtmosMode))", tidalSource, StringComparison.Ordinal);
        Assert.DoesNotContain("return null;\n                }\n            }\n\n            var trackInfo = await SearchAtmosTrackByMetadataWithIsrcAsync", tidalSource, StringComparison.Ordinal);
        Assert.Contains("Tidal credential playback info returned a preview asset", tidalSource, StringComparison.Ordinal);

        Assert.Contains("ResolveAtmosTrackAsync", amazonSource, StringComparison.Ordinal);
        Assert.Contains("candidate.HasAtmos && IsAcceptedResolvedTrack", amazonSource, StringComparison.Ordinal);
        Assert.Contains("ResolveAtmosTrackAsync(", downloadIntentService, StringComparison.Ordinal);
        Assert.Contains("amazonId,", downloadIntentService, StringComparison.Ordinal);
    }

    [Fact]
    public void QobuzDownload_ConsumesResolvedTrackIdWithoutRepeatingIdentityResolution()
    {
        var source = ReadSource("DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs");

        Assert.Contains("TryParseQobuzTrackId(payload.QobuzId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_qobuzTrackResolver", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAudioEngine_ConsumesItsCentrallyResolvedSourceIdentity()
    {
        var deezer = ReadSource("DeezSpoTag.Services/Download/Deezer/DeezerEngineProcessor.cs");
        var qobuz = ReadSource("DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs");
        var tidal = ReadSource("DeezSpoTag.Services/Download/Tidal/TidalEngineProcessor.cs");
        var amazon = ReadSource("DeezSpoTag.Services/Download/Amazon/AmazonEngineProcessor.cs");
        var apple = ReadSource("DeezSpoTag.Services/Download/Apple/AppleEngineProcessor.cs");

        Assert.Contains("_deezerClient.GetTrackAsync(payload.DeezerId)", deezer, StringComparison.Ordinal);
        Assert.Contains("TryParseQobuzTrackId(payload.QobuzId", qobuz, StringComparison.Ordinal);
        Assert.Contains("NormalizeNumericTrackId(payload.TidalId)", tidal, StringComparison.Ordinal);
        Assert.Contains("NormalizeAmazonTrackId(payload.AmazonId)", amazon, StringComparison.Ordinal);
        Assert.Contains("AppleIdParser.Resolve(payload.AppleId, payload.SourceUrl)", apple, StringComparison.Ordinal);

        Assert.DoesNotContain("ResolveAndPersistStorefrontAppleIdAsync", apple, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveAppleIdForStorefrontAsync", apple, StringComparison.Ordinal);
        Assert.DoesNotContain("ValidateQobuzUrlTrackSelectionAsync", qobuz, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueDispatchAndFallback_AlwaysReenterCentralResolution()
    {
        var app = ReadSource("DeezSpoTag.Services/Download/Shared/DeezSpoTagApp.cs");
        var fallback = ReadSource("DeezSpoTag.Services/Download/Fallback/EngineFallbackCoordinator.cs");

        Assert.Contains(
            "string.Equals(resolutionStatus, QueuePreResolutionPayload.Resolved",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "|| string.IsNullOrWhiteSpace(resolutionStatus)",
            app,
            StringComparison.Ordinal);
        Assert.Contains("MarkCentralResolutionPending(context.PayloadForSerialization)", fallback, StringComparison.Ordinal);
        Assert.Contains("payload.ResolutionStatus = QueuePreResolutionPayload.Pending", fallback, StringComparison.Ordinal);
        Assert.Contains("payload.ResolvedEngine = string.Empty", fallback, StringComparison.Ordinal);
        Assert.Contains("payload.ResolvedSourceUrl = string.Empty", fallback, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
