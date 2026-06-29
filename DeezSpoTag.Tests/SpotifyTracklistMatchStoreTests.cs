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
}
