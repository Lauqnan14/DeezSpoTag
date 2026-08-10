using System.Reflection;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistNotificationEventTests
{
    private static object? Build(int queuedCount, string? sourceLabel, string? playlistId)
    {
        var type = typeof(DeezSpoTag.Web.Services.WatchlistEngine);
        var method = type.GetMethod(
            "BuildWatchContentNotification",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return method.Invoke(null, [queuedCount, sourceLabel, playlistId]);
    }

    private static string Read(object notification, string property)
        => notification.GetType().GetProperty(property)!.GetValue(notification)!.ToString()!;

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoNotification_WhenNothingWasQueued(int queuedCount)
    {
        Assert.Null(Build(queuedCount, "Alikiba", null));
    }

    [Fact]
    public void ArtistRelease_WhenNoPlaylistIdIsPresent()
    {
        var notification = Build(3, "Alikiba", null);

        Assert.NotNull(notification);
        Assert.Equal("artist_new_release", Read(notification!, "Kind"));
        Assert.Equal("New release: Alikiba", Read(notification!, "Title"));
        Assert.Equal("3 tracks queued for download.", Read(notification!, "Body"));
        Assert.Equal("artist", Read(notification!, "EntityType"));
        Assert.Equal("artist_new_release:alikiba", Read(notification!, "DedupeKey"));
    }

    [Fact]
    public void PlaylistUpdate_WhenAPlaylistIdIsPresent()
    {
        var notification = Build(2, "Bongo Hits", "spotify:37i9dQ");

        Assert.NotNull(notification);
        Assert.Equal("playlist_updated", Read(notification!, "Kind"));
        Assert.Equal("New in playlist: Bongo Hits", Read(notification!, "Title"));
        Assert.Equal("playlist", Read(notification!, "EntityType"));
        Assert.Equal("spotify:37i9dQ", Read(notification!, "EntityId"));
        Assert.Equal("playlist_updated:spotify:37i9dq", Read(notification!, "DedupeKey"));
    }

    [Fact]
    public void SingleTrack_UsesSingularWording()
    {
        Assert.Equal("1 track queued for download.", Read(Build(1, "Alikiba", null)!, "Body"));
    }

    [Fact]
    public void FallsBackToAGenericLabel_WhenSourceLabelIsMissing()
    {
        Assert.Equal("New release: Watched artist", Read(Build(1, "   ", null)!, "Title"));
        Assert.Equal("New in playlist: Watched playlist", Read(Build(1, null, "deezer:99")!, "Title"));
    }

    [Fact]
    public void DedupeKeyIsStable_AcrossRepeatedRunsForTheSameArtist()
    {
        var first = Read(Build(1, "Alikiba", null)!, "DedupeKey");
        var second = Read(Build(9, "alikiba", null)!, "DedupeKey");

        Assert.Equal(first, second);
    }
}
