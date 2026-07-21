using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyTracklistMatchStoreTests
{
    [Fact]
    public void TryReservePending_DoesNotReserveTheSameRowTwice()
    {
        var store = new SpotifyTracklistMatchStore();

        Assert.True(store.TryReservePending("spotify:playlist:test", 0));
        Assert.False(store.TryReservePending("spotify:playlist:test", 0));

        var snapshot = store.GetSnapshot("spotify:playlist:test");
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.Pending);
    }

    [Fact]
    public void TryReservePending_DoesNotReserveACompletedRowAgain()
    {
        var store = new SpotifyTracklistMatchStore();
        const string token = "spotify:playlist:test";

        Assert.True(store.TryReservePending(token, 0));
        store.RecordMatch(token, 0, "3135556", "spotify-id", "matched", "isrc", 1);

        Assert.False(store.TryReservePending(token, 0));
        var snapshot = store.GetSnapshot(token);
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot.Pending);
        Assert.Equal(1, snapshot.Matched);
    }

    [Fact]
    public void TryReservePending_AppendsRowsFromTheNextPage()
    {
        var store = new SpotifyTracklistMatchStore();
        const string token = "spotify:playlist:test";

        Assert.True(store.TryReservePending(token, 0));
        Assert.True(store.TryReservePending(token, 50));

        var snapshot = store.GetSnapshot(token);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Pending);
    }

    [Fact]
    public void Start_ClearsIndexedMatches_WhenSectionContentSignatureChanges()
    {
        var store = new SpotifyTracklistMatchStore();
        const string token = "spotify:section:home-trending-songs";

        store.Start(token, 1, "old-track-list");
        store.RecordMatch(token, 0, "111111", "old-spotify-id", "matched", "isrc", 1);

        store.Start(token, 1, "new-track-list");

        var snapshot = store.GetSnapshot(token);
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.Pending);
        Assert.Empty(snapshot.Matches);
    }

    [Fact]
    public void IncrementalSnapshot_ReturnsOnlyEntriesChangedAfterRevision()
    {
        var store = new SpotifyTracklistMatchStore();
        const string token = "spotify:playlist:incremental";

        Assert.True(store.TryReservePending(token, 0));
        Assert.True(store.TryReservePending(token, 1));
        store.RecordMatch(token, 0, "111", "spotify-0", "matched", "isrc", 1);

        var first = store.GetSnapshot(token);
        Assert.NotNull(first);
        Assert.Single(first.Matches);
        Assert.Equal(1, first.Pending);
        Assert.Equal(1, first.Matched);

        store.RecordMatch(token, 1, "222", "spotify-1", "matched", "isrc", 1);
        var delta = store.GetSnapshot(token, first.Revision);

        Assert.NotNull(delta);
        Assert.Single(delta.Matches);
        Assert.Equal(1, delta.Matches[0].Index);
        Assert.Equal("222", delta.Matches[0].DeezerId);
        Assert.Equal(0, delta.Pending);
        Assert.Equal(2, delta.Matched);
        Assert.True(delta.Revision > first.Revision);
    }

    [Fact]
    public void FirstMatchedTrack_IsPublishedWhileRemainingPlaylistIsStillPending()
    {
        var store = new SpotifyTracklistMatchStore();
        const string token = "spotify:playlist:large";

        for (var index = 0; index < 1000; index++)
        {
            Assert.True(store.TryReservePending(token, index));
        }

        store.RecordMatch(token, 0, "3135556", "spotify-0", "matched", "isrc", 1);
        var snapshot = store.GetSnapshot(token);

        Assert.NotNull(snapshot);
        Assert.Equal(999, snapshot.Pending);
        Assert.Equal(1, snapshot.Matched);
        Assert.Single(snapshot.Matches);
        Assert.Equal("3135556", snapshot.Matches[0].DeezerId);
    }

    [Fact]
    public void ProgressUpdates_AreIncrementalWithoutMarkingTrackComplete()
    {
        var store = new SpotifyTracklistMatchStore();
        const string token = "spotify:playlist:progress";

        Assert.True(store.TryReservePending(token, 0));
        store.RecordProgress(token, 0, "spotify-0", "matching", "match_started", 1);
        var matching = store.GetSnapshot(token);

        Assert.NotNull(matching);
        Assert.Equal(1, matching.Pending);
        Assert.Equal(0, matching.Matched);

        store.RecordProgress(token, 0, "spotify-0", "rechecking", "retry", 2);
        var retryDelta = store.GetSnapshot(token, matching.Revision);

        Assert.NotNull(retryDelta);
        Assert.Single(retryDelta.Matches);
        Assert.Equal("rechecking", retryDelta.Matches[0].Status);
        Assert.Equal(1, retryDelta.Pending);
    }
}
