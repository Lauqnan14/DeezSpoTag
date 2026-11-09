using System;
using System.Linq;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlaylistReconcileDeltaTest
{
    [Fact]
    public void UnchangedCompleteSnapshotWithNoLibraryChangeReusesStoredSelection()
    {
        Assert.True(WatchlistEngine.PlaylistReconcileDelta.CanReuseUnchangedSelection(
            sourceChanged: false,
            snapshotComplete: true,
            snapshotTruncated: false,
            sameTrackSet: true,
            libraryIdentityChanged: false));
    }

    [Fact]
    public void LibraryChangeOrIncompleteSnapshotDoesNotReuseStoredSelection()
    {
        Assert.False(WatchlistEngine.PlaylistReconcileDelta.CanReuseUnchangedSelection(
            sourceChanged: false,
            snapshotComplete: true,
            snapshotTruncated: false,
            sameTrackSet: true,
            libraryIdentityChanged: true));
        Assert.False(WatchlistEngine.PlaylistReconcileDelta.CanReuseUnchangedSelection(
            sourceChanged: false,
            snapshotComplete: false,
            snapshotTruncated: false,
            sameTrackSet: true,
            libraryIdentityChanged: false));
        Assert.False(WatchlistEngine.PlaylistReconcileDelta.CanReuseUnchangedSelection(
            sourceChanged: true,
            snapshotComplete: true,
            snapshotTruncated: false,
            sameTrackSet: false,
            libraryIdentityChanged: false));
    }

    [Fact]
    public void ChangedSnapshotResolvesOnlyAddedTracks()
    {
        var incoming = new[]
        {
            Candidate("keep"),
            Candidate("added-1"),
            Candidate("added-2")
        };

        var added = WatchlistEngine.PlaylistReconcileDelta.Added(incoming, new[] { "keep", "removed" });

        Assert.Equal(new[] { "added-1", "added-2" }, added.Select(candidate => candidate.TrackSourceId));
    }

    [Fact]
    public void ThreeThousandTrackUnchangedSetIsRecognizedWithoutWalkingTracks()
    {
        var ids = Enumerable.Range(1, 3000).Select(index => "track-" + index).ToArray();

        Assert.True(WatchlistEngine.PlaylistReconcileDelta.SameTrackSet(ids, ids.Reverse()));
        Assert.Empty(WatchlistEngine.PlaylistReconcileDelta.Added(
            ids.Select(Candidate).ToArray(),
            ids));
    }

    [Fact]
    public void MatchingLibraryWatermarkSkipsAnotherIdentityResolve()
    {
        var stored = WatchlistEngine.PlaylistReconcileDelta.WithLibraryWatermark("revision", "4:9:2026:4:9:mtime");

        Assert.Equal("4:9:2026:4:9:mtime", WatchlistEngine.PlaylistReconcileDelta.ReadLibraryWatermark(stored));
        Assert.True(WatchlistEngine.PlaylistReconcileDelta.LibraryWatermarkMatches(
            WatchlistEngine.PlaylistReconcileDelta.ReadLibraryWatermark(stored),
            "4:9:2026:4:9:mtime"));
        Assert.False(WatchlistEngine.PlaylistReconcileDelta.LibraryWatermarkMatches(
            WatchlistEngine.PlaylistReconcileDelta.ReadLibraryWatermark("revision"),
            "4:9:2026:4:9:mtime"));
    }

    [Fact]
    public void ReorderingWithoutExplicitPositionsStillUpdatesExistingTracks()
    {
        var previous = new[] { Candidate("first"), Candidate("second") };
        var reordered = previous.Reverse().ToArray();
        Assert.Equal(new[] { "second", "first" }, WatchlistEngine.PlaylistReconcileDelta.Changed(reordered, previous)
            .Select(track => track.TrackSourceId));
        Assert.Empty(WatchlistEngine.PlaylistReconcileDelta.Changed(previous, previous));
    }

    [Fact]
    public void CorrectedMetadataSelectsOnlyTheChangedExistingTrack()
    {
        var previous = new[] { Candidate("keep"), Candidate("correct") };
        var incoming = new[] { previous[0], previous[1] with { Title = "Corrected title" } };
        Assert.Equal("correct", Assert.Single(WatchlistEngine.PlaylistReconcileDelta.Changed(incoming, previous)).TrackSourceId);
    }

    private static PlaylistTrackCandidate Candidate(string id)
        => new(id, null, id, "Artist", "Album", null, 1000, false, Array.Empty<string>());
}
