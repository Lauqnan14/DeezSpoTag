using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SoulseekBatchQueuePlannerTest
{
    [Fact]
    public void CreatePlan_RejectsANullRequest()
        => Assert.Equal("invalid_request", SoulseekBatchQueuePlanner.CreatePlan(null, [], null).RequestErrorCode);

    [Fact]
    public void CreatePlan_RejectsAnEmptyBatch()
        => Assert.Equal("empty_batch", SoulseekBatchQueuePlanner.CreatePlan(Request(files: []), [], Folder()).RequestErrorCode);

    [Fact]
    public void CreatePlan_RejectsMoreThanOneHundredFiles()
    {
        var files = new List<QueueSoulseekBatchFileRequest>();
        for (var index = 0; index < 101; index++)
        {
            files.Add(RequestFile($"Album/{index:000} Track.flac"));
        }

        Assert.Equal("batch_too_large", SoulseekBatchQueuePlanner.CreatePlan(Request(files: files), [], Folder()).RequestErrorCode);
    }

    [Theory]
    [InlineData("", "Album", 1L, "username_required")]
    [InlineData("peer", "", 1L, "remote_directory_required")]
    [InlineData("peer", "Album", null, "destination_required")]
    public void CreatePlan_RequiresTheBatchIdentityAndDestination(
        string username,
        string remoteDirectory,
        long? destinationId,
        string expectedCode)
    {
        var request = Request(username, remoteDirectory, destinationId);

        Assert.Equal(expectedCode, SoulseekBatchQueuePlanner.CreatePlan(request, [], Folder()).RequestErrorCode);
    }

    [Fact]
    public void CreatePlan_RejectsDuplicateNormalizedRemotePaths()
    {
        var request = Request(files:
        [
            RequestFile("Album\\Track.flac"),
            RequestFile("album/track.flac")
        ]);

        Assert.Equal("duplicate_remote_path", SoulseekBatchQueuePlanner.CreatePlan(request, [], Folder()).RequestErrorCode);
    }

    [Theory]
    [InlineData("Album Two/Track.flac")]
    [InlineData("Album/Disc 1/Track.flac")]
    public void CreatePlan_RejectsPathsOutsideTheExactDirectDirectory(string path)
    {
        var request = Request(files: [RequestFile(path)]);

        Assert.Equal("remote_path_outside_directory", SoulseekBatchQueuePlanner.CreatePlan(request, [], Folder()).RequestErrorCode);
    }

    [Fact]
    public void CreatePlan_RejectsFreshFilesFromAnotherPeer()
    {
        var path = "Album/Track.flac";
        var request = Request(files: [RequestFile(path)]);

        Assert.Equal(
            "mixed_peer_identity",
            SoulseekBatchQueuePlanner.CreatePlan(request, [BrowseFile(path, username: "other")], Folder()).RequestErrorCode);
    }

    [Theory]
    [InlineData("missing", true, true, "profile", "destination_not_found")]
    [InlineData("present", false, true, "profile", "destination_disabled")]
    [InlineData("atmos", true, true, "profile", "destination_not_stereo")]
    [InlineData("present", true, false, "profile", "destination_autotag_disabled")]
    [InlineData("present", true, true, "", "destination_autotag_profile_required")]
    public void CreatePlan_RejectsAnIneligibleDestination(
        string destinationKind,
        bool enabled,
        bool autoTagEnabled,
        string profile,
        string expectedCode)
    {
        FolderDto? destination = destinationKind == "missing"
            ? null
            : Folder(
                enabled: enabled,
                desiredQuality: destinationKind == "atmos" ? "atmos" : "flac",
                autoTagEnabled: autoTagEnabled,
                profileId: profile);

        var plan = SoulseekBatchQueuePlanner.CreatePlan(Request(), [BrowseFile("Album/Track.flac")], destination);

        Assert.Equal(expectedCode, plan.RequestErrorCode);
        Assert.Empty(plan.Items);
    }

    [Fact]
    public void CreatePlan_RejectsAMissingFreshFilePerItem()
    {
        var plan = SoulseekBatchQueuePlanner.CreatePlan(Request(), [], Folder());

        var rejection = Assert.Single(plan.Rejections);
        Assert.Equal("file_no_longer_available", Assert.Single(rejection.ReasonCodes));
    }

    [Fact]
    public void CreatePlan_RejectsANowIneligibleFreshFilePerItem()
    {
        var path = "Album/Track.flac";
        var plan = SoulseekBatchQueuePlanner.CreatePlan(
            Request(),
            [BrowseFile(path, eligible: false, rejectedBecause: "quality_not_allowed")],
            Folder());

        var rejection = Assert.Single(plan.Rejections);
        Assert.Equal("quality_not_allowed", Assert.Single(rejection.ReasonCodes));
    }

    [Fact]
    public void CreatePlan_RejectsASingleFileOutsideTheCurrentSourceSelection()
    {
        var fresh = SoulseekBrowseFilePolicy.Evaluate(
            "peer",
            new SlskdFile
            {
                Filename = "Album/Track.mp3",
                Extension = ".mp3",
                BitRate = 320,
                Size = 100
            },
            new SoulseekDownloadSettings(),
            ["FLAC"]);
        var plan = SoulseekBatchQueuePlanner.CreatePlan(
            Request(files: [RequestFile("Album/Track.mp3")]),
            [fresh],
            Folder());

        Assert.Empty(plan.Items);
        Assert.Equal("quality_not_allowed", Assert.Single(Assert.Single(plan.Rejections).ReasonCodes));
    }

    [Fact]
    public void CreatePlan_RejectsAChangedPositiveFileSizePerItem()
    {
        var path = "Album/Track.flac";
        var plan = SoulseekBatchQueuePlanner.CreatePlan(
            Request(files: [RequestFile(path, size: 10)]),
            [BrowseFile(path, size: 20)],
            Folder());

        var rejection = Assert.Single(plan.Rejections);
        Assert.Equal("remote_size_changed", Assert.Single(rejection.ReasonCodes));
    }

    private static QueueSoulseekBatchDownloadRequest Request(
        string username = "peer",
        string remoteDirectory = "Album",
        long? destinationId = 1,
        IReadOnlyList<QueueSoulseekBatchFileRequest>? files = null,
        IReadOnlyList<string>? sidecars = null)
        => new(
            username,
            remoteDirectory,
            destinationId,
            "https://example.test/cover.jpg",
            files ?? [RequestFile("Album/Track.flac")],
            sidecars);

    private static QueueSoulseekBatchFileRequest RequestFile(string path, long? size = 100)
        => new(path, size, "Advisory title", "Advisory artist", "Advisory album", 9, 999, "MP3_128");

    /// <summary>
    ///     A cover the reader ticked is pinned onto the intent, and the request is not refused for carrying it.
    /// </summary>
    [Fact]
    public void CreatePlan_AcceptsAndPinsATakeablePeerSidecar()
    {
        var plan = SoulseekBatchQueuePlanner.CreatePlan(
            Request(sidecars: ["Album/cover.jpg"]),
            [BrowseFile("Album/Track.flac"), Sidecar("Album/cover.jpg", SoulseekSidecarPolicy.Cover)],
            Folder());

        Assert.True(plan.IsValid);
        Assert.Equal(["Album/cover.jpg"], Assert.Single(plan.Items).Intent.SoulseekSidecarRemotePaths);
    }

    [Fact]
    public void CreatePlan_AcceptsACueSheetAsLyrics()
    {
        var plan = SoulseekBatchQueuePlanner.CreatePlan(
            Request(sidecars: ["Album/Album.cue"]),
            [BrowseFile("Album/Track.flac"), Sidecar("Album/Album.cue", SoulseekSidecarPolicy.CueSheet)],
            Folder());

        Assert.True(plan.IsValid);
        Assert.Single(Assert.Single(plan.Items).Intent.SoulseekSidecarRemotePaths);
    }

    [Fact]
    public void CreatePlan_PinsNoSidecarWhenTheReaderTookNone()
        => Assert.Empty(Assert.Single(
            SoulseekBatchQueuePlanner.CreatePlan(Request(), [BrowseFile("Album/Track.flac")], Folder()).Items).Intent.SoulseekSidecarRemotePaths);

    /// <summary>
    ///     A sidecar is held to exactly the same rule as the audio, which is what keeps it inside the one
    ///     folder that was browsed.
    /// </summary>
    [Theory]
    [InlineData("Other/cover.jpg", "sidecar_outside_directory")]
    [InlineData("Album/Disc 1/cover.jpg", "sidecar_outside_directory")]
    public void CreatePlan_RefusesASidecarFromOutsideTheBrowsedDirectory(string path, string expectedCode)
    {
        var plan = SoulseekBatchQueuePlanner.CreatePlan(
            Request(sidecars: [path]),
            [BrowseFile("Album/Track.flac"), Sidecar(path, SoulseekSidecarPolicy.Cover)],
            Folder());

        Assert.Equal(expectedCode, plan.RequestErrorCode);
    }

    [Fact]
    public void CreatePlan_RefusesASidecarThatHasSinceDisappeared()
    {
        var plan = SoulseekBatchQueuePlanner.CreatePlan(
            Request(sidecars: ["Album/cover.jpg"]),
            [BrowseFile("Album/Track.flac")],
            Folder());

        Assert.Equal("sidecar_no_longer_available", plan.RequestErrorCode);
    }

    /// <summary>
    ///     A rip log is not content. Refusing it is the honest answer: taking it would spend a peer's
    ///     bandwidth and the reader's disk on a file nothing in the app can use.
    /// </summary>
    [Theory]
    [InlineData(SoulseekSidecarPolicy.Log)]
    [InlineData(SoulseekSidecarPolicy.Playlist)]
    [InlineData(SoulseekSidecarPolicy.Other)]
    public void CreatePlan_RefusesASidecarThatIsNotArtworkOrLyrics(string role)
    {
        var plan = SoulseekBatchQueuePlanner.CreatePlan(
            Request(sidecars: ["Album/sidecar.txt"]),
            [BrowseFile("Album/Track.flac"), Sidecar("Album/sidecar.txt", role)],
            Folder());

        Assert.Equal("sidecar_not_takeable", plan.RequestErrorCode);
    }

    [Fact]
    public void CreatePlan_RefusesAnEmptySidecarPath()
    {
        var plan = SoulseekBatchQueuePlanner.CreatePlan(
            Request(sidecars: ["   "]),
            [BrowseFile("Album/Track.flac")],
            Folder());

        Assert.Equal("sidecar_path_required", plan.RequestErrorCode);
    }

    /// <summary>
    ///     The endpoint rejects a malformed request before it spends a peer round trip, and that pre-flight
    ///     pass has no listing to judge sidecars against.
    /// </summary>
    /// <remarks>
    ///     Judging them there would report every sidecar as gone, so the pre-flight pass checks only the
    ///     request's own shape and leaves the peer check to the pass that has actually asked the peer.
    /// </remarks>
    [Fact]
    public void CreatePlan_TheShapeOnlyPreflightDoesNotJudgeSidecarsAgainstAnEmptyListing()
    {
        var plan = SoulseekBatchQueuePlanner.CreatePlan(
            Request(sidecars: ["Album/cover.jpg"]),
            [],
            Folder(),
            validateSidecars: false);

        Assert.True(plan.IsValid);

        // The same request against a real listing is still judged, and a genuine problem still surfaces.
        Assert.Equal(
            "sidecar_no_longer_available",
            SoulseekBatchQueuePlanner.CreatePlan(
                Request(sidecars: ["Album/cover.jpg"]),
                [BrowseFile("Album/Track.flac")],
                Folder()).RequestErrorCode);
    }

    [Fact]
    public void CreatePlan_TheShapeOnlyPreflightStillRejectsAMalformedSidecar()
    {
        Assert.Equal(
            "sidecar_path_required",
            SoulseekBatchQueuePlanner.CreatePlan(Request(sidecars: ["  "]), [], Folder(), validateSidecars: false)
                .RequestErrorCode);

        var tooMany = Enumerable.Range(0, 101).Select(i => $"Album/sidecar{i}.txt").ToArray();
        Assert.Equal(
            "sidecar_batch_too_large",
            SoulseekBatchQueuePlanner.CreatePlan(Request(sidecars: tooMany), [], Folder(), validateSidecars: false)
                .RequestErrorCode);
    }

    private static SoulseekBrowseFile Sidecar(string path, string role)    {
        var file = BrowseFile(path);
        return file with { Eligible = false, RejectedBecause = "non_audio_file", SidecarRole = role };
    }

    internal static SoulseekBrowseFile BrowseFile(
        string path,
        string username = "peer",
        long size = 100,
        bool eligible = true,
        string? rejectedBecause = null,
        string title = "Canonical title",
        string? artist = "Canonical artist",
        string? album = "Canonical album",
        int? trackNumber = 1,
        int? durationSeconds = 180,
        string quality = "FLAC")
        => new(
            "id",
            username,
            SoulseekRemotePath.GetDirectory(path),
            path,
            SoulseekRemotePath.GetLeaf(path),
            title,
            artist,
            album,
            trackNumber,
            size,
            durationSeconds,
            null,
            16,
            44_100,
            quality,
            quality,
            eligible,
            rejectedBecause,
            SoulseekSidecarPolicy.Other);

    internal static FolderDto Folder(
        bool enabled = true,
        string desiredQuality = "flac",
        bool autoTagEnabled = true,
        string? profileId = "profile")
        => new(
            1,
            "/music",
            "Music",
            enabled,
            1,
            "Library",
            desiredQuality,
            profileId,
            autoTagEnabled,
            false,
            null,
            null);
}
