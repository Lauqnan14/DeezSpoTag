using System;
using System.Collections.Generic;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Download.Soulseek;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SoulseekBrowseFilePolicyTest
{
    [Fact]
    public void RemotePath_NormalizesWindowsAndPosixSeparatorsToOneIdentity()
    {
        const string windows = "@@peer\\Music\\Artist\\Album\\01 Track.flac";
        const string posix = "@@peer/Music/Artist/Album/01 Track.flac";

        Assert.Equal(SoulseekRemotePath.Normalize(posix), SoulseekRemotePath.Normalize(windows));
        Assert.Equal("@@peer/Music/Artist/Album/01 Track.flac", SoulseekRemotePath.Normalize(windows));
    }

    [Theory]
    [InlineData("@@peer/Music/Artist/Album/")]
    [InlineData("  @@peer\\Music\\Artist\\Album\\  ")]
    public void RemotePath_RemovesTrailingSeparatorsAndOuterWhitespace(string value)
    {
        Assert.Equal("@@peer/Music/Artist/Album", SoulseekRemotePath.Normalize(value));
    }

    [Theory]
    [InlineData("@@peer\\Music\\Album\\01 Track.flac", "@@peer/Music/Album", "01 Track.flac")]
    [InlineData("@@peer/Music/Album/02 Track.mp3", "@@peer/Music/Album", "02 Track.mp3")]
    public void RemotePath_ExtractsDirectoryAndLeafAcrossPeerOperatingSystems(
        string filename,
        string expectedDirectory,
        string expectedLeaf)
    {
        Assert.Equal(expectedDirectory, SoulseekRemotePath.GetDirectory(filename));
        Assert.Equal(expectedLeaf, SoulseekRemotePath.GetLeaf(filename));
    }

    [Theory]
    [InlineData("Album Two/file.flac", "Album")]
    [InlineData("Album/Disc 1/file.flac", "Album")]
    [InlineData("", "Album")]
    [InlineData("Album/file.flac", "")]
    public void RemotePath_DirectChildRequiresTheExactImmediateDirectory(string filename, string directory)
    {
        Assert.False(SoulseekRemotePath.IsDirectChildOf(filename, directory));
    }

    [Fact]
    public void RemotePath_DirectChildMatchesCaseInsensitivelyWithoutHostFilesystemRules()
    {
        Assert.True(SoulseekRemotePath.IsDirectChildOf(
            "@@PEER\\Music\\Album\\01 Track.flac",
            "@@peer/music/album/"));
    }

    [Theory]
    [InlineData("@@peer\\Music\\Album\\01 Track.flac", ".flac", null, "FLAC")]
    [InlineData("@@peer/Music/Album/02 Track.mp3", ".mp3", 320, "MP3_320")]
    public void BrowseFile_NormalizesQualityFromTheRawFileFacts(
        string filename,
        string extension,
        int? bitrateKbps,
        string expectedQuality)
    {
        var result = Evaluate(
            "peer",
            File(filename, extension, bitrateKbps),
            enabledQualities: [expectedQuality]);

        Assert.Equal(expectedQuality, result.QualityCode);
        Assert.True(result.Eligible);
        Assert.Null(result.RejectedBecause);
    }

    [Theory]
    [InlineData("@@peer\\Album\\Sauti Sol_Live and Die in Afrika_04_Isabella.flac")]
    [InlineData("@@peer\\Album\\Sauti Sol - Live and Die in Afrika - 04 - Isabella.flac")]
    public void BrowseFile_UsesTheRepairedFilenameParserForAlbumTracks(string filename)
    {
        var result = Evaluate("peer", File(filename, ".flac"), enabledQualities: ["FLAC"]);

        Assert.Equal("Isabella", result.Title);
        Assert.Equal("Sauti Sol", result.Artist);
        Assert.Equal("Live and Die in Afrika", result.Album);
        Assert.Equal(4, result.TrackNumber);
    }

    [Fact]
    public void BrowseFile_RejectsBlockedUsersBeforeOtherRules()
    {
        var settings = Settings();
        settings.BlockedUsers.Add("peer");
        settings.BlockedFilenamePatterns.Add("track");

        var result = Evaluate("PEER", File("Album/Track.flac", ".flac", isLocked: true), settings, ["FLAC"]);

        Assert.False(result.Eligible);
        Assert.Equal("blocked_user", result.RejectedBecause);
    }

    [Fact]
    public void BrowseFile_RejectsBlockedWildcardFilename()
    {
        var settings = Settings();
        settings.BlockedFilenamePatterns.Add("*/live/*");

        var result = Evaluate("peer", File("@@peer/share/live/Track.flac", ".flac"), settings, ["FLAC"]);

        Assert.Equal("blocked_filename_pattern", result.RejectedBecause);
    }

    [Fact]
    public void BrowseFile_RejectsLockedFiles()
    {
        var result = Evaluate("peer", File("Album/Track.flac", ".flac", isLocked: true), enabledQualities: ["FLAC"]);

        Assert.Equal("file_locked", result.RejectedBecause);
    }

    [Theory]
    [InlineData("Album/cover.jpg", ".jpg")]
    [InlineData("Album/booklet.jpg", ".jpg")]
    [InlineData("Album/Track (Official Video).flac", ".flac")]
    public void BrowseFile_RejectsSidecarsAndSevereJunkAsNonAudio(string filename, string extension)
    {
        var result = Evaluate("peer", File(filename, extension), enabledQualities: ["FLAC"]);

        Assert.Equal("non_audio_file", result.RejectedBecause);
    }

    [Fact]
    public void BrowseFile_UsesSourceQualityInsteadOfAnExtensionList()
    {
        var result = Evaluate("peer", File("Album/Track.mp3", ".mp3", 320), enabledQualities: ["MP3_320"]);
        Assert.True(result.Eligible);
    }

    [Fact]
    public void BrowseFile_RejectsAQualityOutsideTheEnabledLadder()
    {
        var result = Evaluate("peer", File("Album/Track.mp3", ".mp3", 320), enabledQualities: ["FLAC"]);

        Assert.Equal("quality_not_allowed", result.RejectedBecause);
    }

    [Fact]
    public void BrowseFile_RejectsAllAudioWhenSourceHasNoSoulseekQuality()
    {
        var result = Evaluate("peer", File("Album/Track.flac", ".flac"), enabledQualities: []);

        Assert.False(result.Eligible);
        Assert.Equal("quality_not_allowed", result.RejectedBecause);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BrowseFile_RequiresBothUnknownOptInAndAnEnabledUnknownRung(
        bool allowUnknown,
        bool enableUnknown)
    {
        var settings = Settings();
        settings.AllowUnknownQuality = allowUnknown;
        var enabled = enableUnknown ? new[] { "UNKNOWN" } : new[] { "FLAC" };

        var result = Evaluate("peer", File("Album/Track.opus", ".opus"), settings, enabled);

        Assert.Equal(allowUnknown && enableUnknown, result.Eligible);
        Assert.Equal(
            allowUnknown ? (enableUnknown ? null : "quality_not_allowed") : "unknown_quality",
            result.RejectedBecause);
    }

    [Fact]
    public void BrowseFile_EmptyExtensionAllowListRetainsKnownQualityBehavior()
    {
        var result = Evaluate("peer", File("Album/Track.flac", ".flac"), enabledQualities: ["FLAC"]);

        Assert.True(result.Eligible);
    }

    [Fact]
    public void BrowseFile_EvaluatesMixedQualitiesIndependently()
    {
        var enabled = new[] { "FLAC", "MP3_320" };

        var flac = Evaluate("peer", File("Album/Track One.flac", ".flac"), enabledQualities: enabled);
        var mp3 = Evaluate("peer", File("Album/Track Two.mp3", ".mp3", 320), enabledQualities: enabled);

        Assert.True(flac.Eligible);
        Assert.True(mp3.Eligible);
    }

    [Fact]
    public void BrowseFile_IdIsStableForOnePeerAndPathButDifferentForAnotherPeer()
    {
        var file = File("Album/Track.flac", ".flac");

        var first = Evaluate("peer", file, enabledQualities: ["FLAC"]);
        var repeated = Evaluate("peer", file, enabledQualities: ["FLAC"]);
        var otherPeer = Evaluate("other-peer", file, enabledQualities: ["FLAC"]);

        Assert.Equal(first.Id, repeated.Id);
        Assert.NotEqual(first.Id, otherPeer.Id);
    }

    [Fact]
    public void BrowseFile_RemoteDirectoryComesFromTheExactPathRatherThanParsedAlbumMetadata()
    {
        var result = Evaluate(
            "peer",
            File("@@peer/Completely Different/Sauti Sol - Live and Die in Afrika - 04 - Isabella.flac", ".flac"),
            enabledQualities: ["FLAC"]);

        Assert.Equal("@@peer/Completely Different", result.RemoteDirectory);
        Assert.Equal("Live and Die in Afrika", result.Album);
        Assert.Equal("Sauti Sol - Live and Die in Afrika - 04 - Isabella.flac", result.DisplayFilename);
    }

    /// <summary>
    ///     slskd lists a folder's files relative to that folder, so a listed file arrives as a bare leaf.
    /// </summary>
    /// <remarks>
    ///     The browsed folder is the other half of the file's identity, and the full path is what the queue
    ///     pins and what slskd is asked for. Without it the whole album read as a list of unqueueable leaves.
    /// </remarks>
    [Fact]
    public void BrowseFile_ComposesTheFullRemotePathFromTheBrowsedDirectoryWhenThePeerListsALeaf()
    {
        var result = Evaluate(
            "peer",
            File("01. Boards of Canada - Wildlife Analysis.flac", ".flac"),
            enabledQualities: ["FLAC"],
            browseDirectory: "@@bvvpl\\Music\\Boards of Canada - Music Has The Right To Children (1998)");

        Assert.Equal(
            "@@bvvpl/Music/Boards of Canada - Music Has The Right To Children (1998)/01. Boards of Canada - Wildlife Analysis.flac",
            result.Filename);
        Assert.Equal("@@bvvpl/Music/Boards of Canada - Music Has The Right To Children (1998)", result.RemoteDirectory);
        Assert.Equal("01. Boards of Canada - Wildlife Analysis.flac", result.DisplayFilename);
        Assert.True(SoulseekRemotePath.IsDirectChildOf(result.Filename, result.RemoteDirectory));
    }

    [Fact]
    public void BrowseFile_LeavesAFullPathFromThePeerExactlyAsThePeerStatedIt()
    {
        const string stated = "@@peer/Some/Other/Place/01 Track.flac";

        var result = Evaluate(
            "peer",
            File(stated, ".flac"),
            enabledQualities: ["FLAC"],
            browseDirectory: "@@peer/Music/Album");

        // A path the peer stated outright is authoritative. Rewriting it against the browsed folder would
        // only risk disagreeing with the peer about a path that was already given.
        Assert.Equal(stated, result.Filename);
        Assert.Equal("@@peer/Some/Other/Place", result.RemoteDirectory);
    }

    [Fact]
    public void BrowseFile_IdentityIsTheSameWhetherOrNotThePeerStatedTheDirectory()
    {
        // The same file, once as a leaf from a directory listing and once as a full path, must be the same
        // file: a change of identity between the two would make a re-browse look like the file had gone.
        var leaf = Evaluate(
            "peer",
            File("01 Track.flac", ".flac"),
            enabledQualities: ["FLAC"],
            browseDirectory: "@@peer/Music/Album");
        var full = Evaluate(
            "peer",
            File("@@peer/Music/Album/01 Track.flac", ".flac"),
            enabledQualities: ["FLAC"],
            browseDirectory: "@@peer/Music/Album");

        Assert.Equal(full.Id, leaf.Id);
    }

    /// <summary>
    ///     A peer folder is not only the tracks, and the difference matters most for a release no streaming
    ///     service carries: the cover sitting next to the audio is then the only artwork there will ever be.
    /// </summary>
    [Theory]
    [InlineData("cover.jpg", SoulseekSidecarPolicy.Cover)]
    [InlineData("Folder.JPEG", SoulseekSidecarPolicy.Cover)]
    [InlineData("folder.PNG", SoulseekSidecarPolicy.Cover)]
    [InlineData("track.lrc", SoulseekSidecarPolicy.Lyrics)]
    [InlineData("Track.TTML", SoulseekSidecarPolicy.Lyrics)]
    [InlineData("Album.cue", SoulseekSidecarPolicy.CueSheet)]
    [InlineData("Album.log", SoulseekSidecarPolicy.Log)]
    [InlineData("Album.m3u", SoulseekSidecarPolicy.Playlist)]
    [InlineData("folder.ico", SoulseekSidecarPolicy.Other)]
    public void SidecarPolicy_NamesWhatEachNonAudioFileIsFor(string filename, string expectedRole)
        => Assert.Equal(expectedRole, SoulseekSidecarPolicy.Classify(filename));

    /// <summary>
    ///     A plain text file in a release folder is lyrics or it is not, and the suffix cannot say
    ///     which: the same folders carry a track list and a readme as .txt. The name is what tells
    ///     them apart, so a folder's lyrics are taken and its notes are left alone.
    /// </summary>
    [Theory]
    [InlineData("Lyrics.txt", SoulseekSidecarPolicy.Lyrics)]
    [InlineData("lyrics.txt", SoulseekSidecarPolicy.Lyrics)]
    [InlineData("Massive Attack - Mezzanine (Lyrics).txt", SoulseekSidecarPolicy.Lyrics)]
    [InlineData("01 - Angel.lyrics.txt", SoulseekSidecarPolicy.Lyrics)]
    [InlineData("@@qosme/Massive Attack/LYRICS.TXT", SoulseekSidecarPolicy.Lyrics)]
    [InlineData("-- track list.txt", SoulseekSidecarPolicy.Other)]
    [InlineData("readme.txt", SoulseekSidecarPolicy.Other)]
    [InlineData("album notes.txt", SoulseekSidecarPolicy.Other)]
    [InlineData("rip.txt", SoulseekSidecarPolicy.Other)]
    public void SidecarPolicy_TakesPlainTextLyricsByNameAndLeavesTheRestAlone(string filename, string expectedRole)
        => Assert.Equal(expectedRole, SoulseekSidecarPolicy.Classify(filename));

    /// <summary>
    ///     A rip log and a playlist describe how a release was made, not what it contains.
    /// </summary>
    [Theory]
    [InlineData(SoulseekSidecarPolicy.Cover, true)]
    [InlineData(SoulseekSidecarPolicy.Lyrics, true)]
    [InlineData(SoulseekSidecarPolicy.CueSheet, true)]
    [InlineData(SoulseekSidecarPolicy.Log, false)]
    [InlineData(SoulseekSidecarPolicy.Playlist, false)]
    [InlineData(SoulseekSidecarPolicy.Other, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SidecarPolicy_OnlyTakesWhatTheAppCanActuallyUse(string? role, bool expectedFetchable)
        => Assert.Equal(expectedFetchable, SoulseekSidecarPolicy.IsFetchable(role));

    [Fact]
    public void BrowseFile_ReportsACoverAsACoverAndStillRefusesItAsAudio()
    {
        var cover = Evaluate(
            "peer",
            File("cover.jpg", ".jpg"),
            enabledQualities: ["FLAC"]);

        // It is not a track, so it is not selectable as one. It is also no longer an anonymous "non audio
        // file", which is what let the album drawer say nothing about a cover that was right there.
        Assert.Equal(SoulseekSidecarPolicy.Cover, cover.SidecarRole);
        Assert.False(cover.Eligible);
        Assert.Equal("non_audio_file", cover.RejectedBecause);
        Assert.True(SoulseekSidecarPolicy.IsFetchable(cover.SidecarRole));
    }

    [Fact]
    public void BrowseFile_AnAudioTrackCarriesNoSidecarRole()
    {
        var track = Evaluate(
            "peer",
            File("@@peer/Music/Album/01 Track.flac", ".flac"),
            enabledQualities: ["FLAC"]);

        Assert.True(track.Eligible);
        Assert.Equal(SoulseekSidecarPolicy.Other, track.SidecarRole);
        Assert.False(SoulseekSidecarPolicy.IsFetchable(track.SidecarRole));
    }

    private static SoulseekBrowseFile Evaluate(
        string username,
        SlskdFile file,
        SoulseekDownloadSettings? settings = null,
        IReadOnlyCollection<string>? enabledQualities = null,
        string? browseDirectory = null)
        => SoulseekBrowseFilePolicy.Evaluate(
            username,
            file,
            settings ?? Settings(),
            enabledQualities ?? ["FLAC"],
            browseDirectory);

    private static SoulseekDownloadSettings Settings() => new();

    private static SlskdFile File(
        string filename,
        string extension,
        int? bitrateKbps = null,
        bool isLocked = false)
        => new()
        {
            Filename = filename,
            Extension = extension,
            Size = 12_345,
            BitRate = bitrateKbps,
            BitDepth = extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ? 16 : null,
            SampleRate = extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ? 44_100 : null,
            Length = 210,
            IsLocked = isLocked
        };
}
