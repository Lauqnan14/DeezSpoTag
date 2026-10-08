using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     The peer file the reader chose has to survive the trip from the panel to the queue item.
/// </summary>
/// <remarks>
///     <para>
///         The Soulseek tab queues through the shared download client, which posts the intent to
///         <c>/api/download/intent</c>. The chosen file travels as intent fields, so anything that drops them
///         on the way - a name that does not bind, or a copy that does not run - silently turns "download this
///         file" into "search for the track", and the item then fetches whatever the network offers instead of
///         what was clicked.
///     </para>
/// </remarks>
public sealed class SoulseekPinnedCandidateIntentTest
{
    private static readonly MethodInfo ApplyIntentMetadataMethod =
        typeof(DeezSpoTag.Web.Services.DownloadIntentService).GetMethod(
            "ApplyIntentMetadata",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(SoulseekQueueItem), typeof(DownloadIntent)])
        ?? throw new InvalidOperationException("DownloadIntentService.ApplyIntentMetadata(SoulseekQueueItem, DownloadIntent) not found.");

    private static readonly JsonSerializerOptions ClientOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The exact field names the download client puts in the intent body.</summary>
    private const string PanelIntentJson = """
        {
          "sourceService": "soulseek",
          "preferredEngine": "soulseek",
          "title": "Music Is Math",
          "artist": "Boards of Canada",
          "contentType": "stereo",
          "soulseekUsername": "kungfool",
          "soulseekRemotePath": "music\\Boards of Canada\\Geogaddi\\02.Music Is Math.flac",
          "soulseekRemoteSizeBytes": 114504040
        }
        """;

    /// <summary>
    ///     A request from the Soulseek tab must arrive as a chosen file, or not arrive at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The tab has a file in front of the reader; that is the whole point of selecting one. A request that
    ///         reaches the endpoint without a peer and a path therefore cannot be honoured as asked, and the only
    ///         thing it can become is a fresh search for the track - which is how "download this file" quietly
    ///         became "download something like this file", and why the observed item fetched a different copy.
    ///     </para>
    ///     <para>
    ///         Rejecting it at admission keeps that failure in front of the reader, where the answer is to select
    ///         again, instead of in the queue, where it looks like the network's fault.
    ///     </para>
    /// </remarks>
    [Fact]
    public void SoulseekTabIntentWithoutPeerAndPathIsRejected()
    {
        var message = Validate(pinned: false, path: null, size: null);

        Assert.NotNull(message);
        Assert.Contains("peer or remote path", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SoulseekTabIntentWithOnlyOnePinFieldIsRejected()
    {
        // Username and path are one fact about a file, not two optional ones. Half of it identifies nothing.
        Assert.NotNull(Validate(pinned: true, path: null, size: null));
        Assert.NotNull(Validate(pinned: false, path: "music\\Boards of Canada\\Geogaddi\\02.Music Is Math.flac", size: 1));
    }

    [Fact]
    public void SoulseekTabIntentWithCompletePinIsAccepted()
    {
        // The size is the peer's advertised length, not part of the identity, and the tab has it from the search
        // result, but it is not required: some responses carry no size and the transfer still works.
        Assert.Null(Validate(pinned: true, path: "music\\Boards of Canada\\Geogaddi\\02.Music Is Math.flac", size: 114504040));
        Assert.Null(Validate(pinned: true, path: "music\\Boards of Canada\\Geogaddi\\02.Music Is Math.flac", size: null));
    }

    [Fact]
    public void NonSoulseekIntentMayRemainUnpinned()
    {
        // A library or automation request that prefers Soulseek has no peer file and never will: it is asking for
        // a track, not for a file somebody chose. Rejecting it here would break every other queue.
        Assert.Null(Validate(sourceService: "deezer", pinned: false, path: null, size: null));
        Assert.Null(Validate(sourceService: "spotify", pinned: false, path: null, size: null));
    }

    private static string? Validate(
        bool pinned = true,
        string? sourceService = "soulseek",
        string? path = "music\\Boards of Canada\\Geogaddi\\02.Music Is Math.flac",
        long? size = 114504040)
    {
        var intent = new DownloadIntent
        {
            SourceService = sourceService,
            PreferredEngine = "soulseek",
            Title = "Music Is Math",
            Artist = "Boards of Canada",
            ContentType = "stereo",
            SoulseekUsername = pinned ? "kungfool" : null,
            SoulseekRemotePath = path,
            SoulseekRemoteSizeBytes = size ?? 0
        };

        var validate = typeof(DeezSpoTag.Web.Controllers.Api.DownloadIntentApiController).GetMethod(
            "ValidateSoulseekCandidateIntent",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "DownloadIntentApiController.ValidateSoulseekCandidateIntent not found; a Soulseek-tab request can still arrive unpinned.");

        return (string?)validate.Invoke(null, [intent]);
    }

    /// <summary>
    ///     A queued item is rebuilt into an intent before it is re-resolved, and the chosen file must survive that.
    /// </summary>
    /// <remarks>
    ///     The payload is the only place the pin lives: there is no column for it. So a reconstruction that does
    ///     not read the three fields back turns a chosen file into a plain track request the moment the item is
    ///     retried, resumed or re-resolved - and the engine then runs a fresh search and downloads a different
    ///     file, which is exactly the failure this whole path exists to prevent.
    /// </remarks>
    [Fact]
    public void QueuedResolutionReconstructionPreservesPinnedSoulseekIdentity()
    {
        const string peer = "Nintendude94";
        const string remotePath = "Music\\Nick Drake\\(1969) Five Leaves Left\\01 - Nick Drake - Time Has Told Me.flac";
        const long size = 24_679_285;

        var pinned = Reconstruct($$"""
            {
              "SourceService": "soulseek",
              "Engine": "soulseek",
              "Title": "Time Has Told Me",
              "Artist": "Nick Drake",
              "SoulseekUsername": "{{peer}}",
              "SoulseekRemotePath": "{{remotePath.Replace("\\", "\\\\")}}",
              "SoulseekRemoteSizeBytes": {{size}}
            }
            """);

        Assert.Equal(peer, pinned.SoulseekUsername);
        Assert.Equal(remotePath, pinned.SoulseekRemotePath);
        Assert.Equal(size, pinned.SoulseekRemoteSizeBytes);

        // And the reconstruction is enough on its own: the request the engine builds from it is the chosen file,
        // with no search in front of it.
        var request = SoulseekRequestBuilder.BuildRequest(
            new SoulseekQueueItem
            {
                Id = "87c643923d264bd69d4aefbec28b902f",
                Engine = "soulseek",
                SourceService = "soulseek",
                Artist = "Nick Drake",
                Title = "Time Has Told Me"
            },
            new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings());

        request.Username = pinned.SoulseekUsername;
        request.RemotePath = pinned.SoulseekRemotePath;
        request.RemoteSizeBytes = pinned.SoulseekRemoteSizeBytes;

        Assert.Equal(peer, request.Username);
        Assert.Equal(remotePath, request.RemotePath);
        Assert.Equal(size, request.RemoteSizeBytes);
    }

    [Fact]
    public void AnUnpinnedQueueItemReconstructsWithoutInheritingSomebodyElsesPin()
    {
        // The control: a library item has no peer file, and rebuilding it must not borrow one.
        var reconstructed = Reconstruct("""
            { "SourceService": "deezer", "Engine": "deezer", "Title": "Time Has Told Me", "Artist": "Nick Drake" }
            """);

        Assert.True(string.IsNullOrEmpty(reconstructed.SoulseekUsername));
        Assert.True(string.IsNullOrEmpty(reconstructed.SoulseekRemotePath));
        Assert.Equal(0, reconstructed.SoulseekRemoteSizeBytes);
    }

    [Fact]
    public void RequestBuilderUsesTheGlobalDownloadLocationAsItsCompletedRoot()
    {
        var request = SoulseekRequestBuilder.BuildRequest(
            new SoulseekQueueItem { Id = "shared-root", Artist = "Artist", Title = "Track" },
            new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings
            {
                DownloadLocation = "/music/deezspotag-downloads"
            });

        Assert.Equal("/music/deezspotag-downloads", request.CompletedDownloadsRoot);
    }

    private static DownloadIntent Reconstruct(string payloadJson)
    {
        var build = typeof(DeezSpoTag.Web.Services.DownloadIntentService).GetMethod(
            "BuildIntentFromQueueItem",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "DownloadIntentService.BuildIntentFromQueueItem not found; a queued item cannot be inspected here.");

        var item = new DownloadQueueItem(
            Id: 0,
            QueueUuid: "87c643923d264bd69d4aefbec28b902f",
            Engine: "soulseek",
            ArtistName: "Nick Drake",
            TrackTitle: "Time Has Told Me",
            Isrc: null,
            DeezerTrackId: null,
            DeezerAlbumId: null,
            DeezerArtistId: null,
            SpotifyTrackId: null,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: null,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: null,
            DestinationFolderId: null,
            QualityRank: null,
            QueueOrder: null,
            ContentType: "stereo",
            Status: "failed",
            PayloadJson: payloadJson,
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        return (DownloadIntent)build.Invoke(null, [item])!;
    }

    /// <summary>
    ///     The queued payload has to keep the engine's own fields on the way into the queue.
    /// </summary>
    /// <remarks>
    ///     The payload is built as the abstract queue-item base and then serialized, and
    ///     <c>JsonSerializer</c> writes the <em>declared</em> type of the value it is given. Serializing the base
    ///     therefore wrote a complete, valid looking payload that contained none of the Soulseek fields - no peer,
    ///     no remote path, no size. The engine then read an unpinned item, searched for the track, and either
    ///     found nothing or downloaded a different file, while every layer above reported that the pin had been
    ///     carried. This is the one place that decides it, so it is pinned here.
    /// </remarks>
    [Fact]
    public void SerializingTheQueuedPayloadAsTheRuntimeTypeKeepsThePin()
    {
        var payload = new SoulseekQueueItem
        {
            Id = "87c643923d264bd69d4aefbec28b902f",
            Engine = "soulseek",
            SourceService = "soulseek",
            Title = "Time Has Told Me",
            Artist = "Nick Drake",
            SoulseekUsername = "Nintendude94",
            SoulseekRemotePath = "Music\\Nick Drake\\(1969) Five Leaves Left\\01 - Nick Drake - Time Has Told Me.flac",
            SoulseekRemoteSizeBytes = 24_679_285
        };

        // The base-typed value is what the enqueue path holds; the engine fields must survive serialization.
        EngineQueueItemBase asQueued = payload;
        var json = JsonSerializer.Serialize(asQueued, asQueued.GetType());
        var restored = JsonSerializer.Deserialize<SoulseekQueueItem>(json, ClientOptions);

        Assert.NotNull(restored);
        Assert.Equal("Nintendude94", restored!.SoulseekUsername);
        Assert.Equal(payload.SoulseekRemotePath, restored.SoulseekRemotePath);
        Assert.Equal(24_679_285, restored.SoulseekRemoteSizeBytes);
    }

    [Fact]
    public void AnEmptyIntentInTheBatchIsRejectedRatherThanThrowing()
    {
        // A batch may legitimately mix kinds of intent, so a null element is a malformed body, not a validation
        // failure. Reading it as a Soulseek intent would turn a bad request into a server error.
        var validate = typeof(DeezSpoTag.Web.Controllers.Api.DownloadIntentApiController).GetMethod(
            "ValidateRequest",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DownloadIntentApiController.ValidateRequest not found.");

        var controller = (Microsoft.AspNetCore.Mvc.ControllerBase)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(DeezSpoTag.Web.Controllers.Api.DownloadIntentApiController));
        var result = validate.Invoke(controller, [new DeezSpoTag.Web.Controllers.Api.DownloadIntentBatchRequest { Intents = [null!] }]);

        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
    }

    [Fact]
    public void TheChosenPeerFileBindsFromTheClientsIntentBody()
    {
        var intent = JsonSerializer.Deserialize<DownloadIntent>(PanelIntentJson, ClientOptions);

        Assert.NotNull(intent);
        Assert.Equal("kungfool", intent!.SoulseekUsername);
        Assert.Equal("music\\Boards of Canada\\Geogaddi\\02.Music Is Math.flac", intent.SoulseekRemotePath);
        Assert.Equal(114504040, intent.SoulseekRemoteSizeBytes);
    }

    [Fact]
    public void TheChosenPeerFileLandsOnTheQueueItem()
    {
        var intent = JsonSerializer.Deserialize<DownloadIntent>(PanelIntentJson, ClientOptions)!;
        var payload = new SoulseekQueueItem();

        ApplyIntentMetadataMethod.Invoke(null, [payload, intent]);

        Assert.Equal("kungfool", payload.SoulseekUsername);
        Assert.Equal("music\\Boards of Canada\\Geogaddi\\02.Music Is Math.flac", payload.SoulseekRemotePath);
        Assert.Equal(114504040, payload.SoulseekRemoteSizeBytes);
    }

    [Fact]
    public void AnIntentWithoutAChoiceCarriesNoPeerFile()
    {
        // A library download that reaches Soulseek through the ladder has no chosen file, and must not inherit
        // one: it searches for itself, which is what every other source does.
        var intent = JsonSerializer.Deserialize<DownloadIntent>(
            """{"sourceService":"spotify","preferredEngine":"spotify","title":"Roygbiv","artist":"Boards of Canada"}""",
            ClientOptions)!;
        var payload = new SoulseekQueueItem();

        ApplyIntentMetadataMethod.Invoke(null, [payload, intent]);

        Assert.Equal(string.Empty, payload.SoulseekUsername);
        Assert.Equal(string.Empty, payload.SoulseekRemotePath);
        Assert.Equal(0, payload.SoulseekRemoteSizeBytes);
    }

    [Fact]
    public void AManualPeerSelectionHasOneExactQualityStepAndNeedsNoCatalogueResolution()
    {
        var intent = JsonSerializer.Deserialize<DownloadIntent>(PanelIntentJson, ClientOptions)!;
        intent.Quality = "FLAC";
        var settings = new DeezSpoTagSettings();
        var build = typeof(DownloadIntentService).GetMethod(
            "BuildVisiblePreResolutionPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Visible queue builder not found.");

        var payload = Assert.IsType<SoulseekQueueItem>(build.Invoke(null,
            [intent, settings, "soulseek", "FLAC", null, null]));

        var step = Assert.Single(payload.FallbackPlan);
        Assert.Equal("soulseek", step.Engine);
        Assert.Equal("FLAC", step.Quality);
        Assert.Equal("FLAC", payload.Quality);
        Assert.Equal(QueuePreResolutionPayload.Resolved, payload.ResolutionStatus);
        Assert.True(QueuePreResolutionPayload.IsResolved(
            JsonNode.Parse(JsonSerializer.Serialize(payload))!.AsObject()));
        Assert.Equal("Boards of Canada", payload.Artist);
        Assert.Equal("kungfool", payload.SoulseekUsername);
        Assert.Equal("FLAC", payload.SoulseekQualityCode);
    }

    [Fact]
    public void OtherSourceIntentCannotInheritASoulseekPeerPin()
    {
        var intent = JsonSerializer.Deserialize<DownloadIntent>(PanelIntentJson, ClientOptions)!;
        intent.SourceService = "deezer";
        var payload = new SoulseekQueueItem();

        ApplyIntentMetadataMethod.Invoke(null, [payload, intent]);

        Assert.Equal(string.Empty, payload.SoulseekUsername);
        Assert.Equal(string.Empty, payload.SoulseekRemotePath);
        Assert.Equal(0, payload.SoulseekRemoteSizeBytes);
    }

    [Fact]
    public void AlbumQueuePathStartsAtTheChosenSoulseekFileAndHasNoFallbackStep()
    {
        var intent = JsonSerializer.Deserialize<DownloadIntent>(PanelIntentJson, ClientOptions)!;
        intent.Quality = "FLAC";
        var settings = new DeezSpoTagSettings();
        var routingType = typeof(DownloadIntentService).GetNestedType("EnqueueRoutingState", BindingFlags.NonPublic)!;
        var routing = routingType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0]
            .Invoke(["soulseek", false, false, new List<string> { "qobuz|7", "soulseek|FLAC", "deezer|9" },
                "soulseek", "FLAC", null, false]);
        var select = typeof(DownloadIntentService).GetMethod(
            "BuildPendingEnqueueTarget", BindingFlags.NonPublic | BindingFlags.Static)!;
        var target = select.Invoke(null, [intent, routing, settings])!;

        Assert.Equal("soulseek", target.GetType().GetProperty("Engine")!.GetValue(target));
        Assert.Equal("FLAC", target.GetType().GetProperty("SelectedQuality")!.GetValue(target));
        Assert.Equal(false, target.GetType().GetProperty("AllowCrossEngineFallback")!.GetValue(target));

        var requestType = typeof(DownloadIntentService).GetNestedType("EnqueueFallbackRequest", BindingFlags.NonPublic)!;
        var request = requestType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0]
            .Invoke([intent, settings, "soulseek", "FLAC", true, true, false,
                new List<string> { "qobuz|7", "soulseek|FLAC", "deezer|9" }, null,
                // Eligible, so this test observes the pinned single-step plan rather than the login gate.
                true]);
        var build = typeof(DownloadIntentService).GetMethod(
            "BuildEnqueueFallbackInfo", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var service = (DownloadIntentService)RuntimeHelpers.GetUninitializedObject(typeof(DownloadIntentService));
        var result = (ITuple)build.Invoke(service, [request])!;
        var plan = Assert.IsType<List<FallbackPlanStep>>(result[0]);

        var step = Assert.Single(plan);
        Assert.Equal("soulseek", step.Engine);
        Assert.Equal("FLAC", step.Quality);
    }

    [Fact]
    public void BatchPlanPinsEveryFilesOwnCanonicalIdentityAndMetadata()
    {
        var firstPath = "Album/01 First.flac";
        var secondPath = "Album/02 Second.mp3";
        var request = new QueueSoulseekBatchDownloadRequest(
            "peer",
            "Album",
            1,
            "https://example.test/cover.jpg",
            [
                new(firstPath, 100, "Wrong one", "Wrong", "Wrong", 9, 9, "MP3_128"),
                new(secondPath, 200, "Wrong two", "Wrong", "Wrong", 9, 9, "FLAC")
            ]);
        var fresh = new[]
        {
            SoulseekBatchQueuePlannerTest.BrowseFile(
                firstPath,
                size: 100,
                title: "First",
                album: "Canonical release",
                trackNumber: 1,
                durationSeconds: 181,
                quality: "FLAC"),
            SoulseekBatchQueuePlannerTest.BrowseFile(
                secondPath,
                size: 200,
                title: "Second",
                album: "Canonical release",
                trackNumber: 2,
                durationSeconds: 202,
                quality: "MP3_320")
        };

        var plan = SoulseekBatchQueuePlanner.CreatePlan(request, fresh, SoulseekBatchQueuePlannerTest.Folder());

        Assert.Null(plan.RequestErrorCode);
        Assert.Empty(plan.Rejections);
        Assert.Collection(
            plan.Items,
            first => AssertPinned(first.Intent, firstPath, 100, "First", 1, 181_000, "FLAC"),
            second => AssertPinned(second.Intent, secondPath, 200, "Second", 2, 202_000, "MP3_320"));
    }

    private static void AssertPinned(
        DownloadIntent intent,
        string remotePath,
        long size,
        string title,
        int trackNumber,
        int durationMs,
        string quality)
    {
        Assert.Equal("soulseek", intent.SourceService);
        Assert.Equal("soulseek", intent.PreferredEngine);
        Assert.Equal("peer", intent.SoulseekUsername);
        Assert.Equal(remotePath, intent.SoulseekRemotePath);
        Assert.Equal(size, intent.SoulseekRemoteSizeBytes);
        Assert.Equal(title, intent.Title);
        Assert.Equal("Canonical release", intent.Album);
        Assert.Equal(trackNumber, intent.TrackNumber);
        Assert.Equal(durationMs, intent.DurationMs);
        Assert.Equal(quality, intent.Quality);
        Assert.Equal(1, intent.DestinationFolderId);
        Assert.Equal("stereo", intent.ContentType);
    }
}
