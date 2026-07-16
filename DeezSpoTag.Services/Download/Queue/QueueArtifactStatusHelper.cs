using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download.Queue;

public static class ArtworkPrefetchStatusHelper
{
    public static void Send(
        IDeezSpoTagListener listener,
        string queueUuid,
        string? artworkStatus)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        listener.Send("updateQueue", new
        {
            uuid = queueUuid,
            prefetchArtworkStatus = artworkStatus
        });
    }
}

public static class QueueLyricsArtifactHelper
{
    public static void Send(
        IDeezSpoTagListener listener,
        string queueUuid,
        LyricsArtifactState state)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        listener.Send("updateQueue", new
        {
            uuid = queueUuid,
            lyricsArtifacts = state
        });
    }
}
