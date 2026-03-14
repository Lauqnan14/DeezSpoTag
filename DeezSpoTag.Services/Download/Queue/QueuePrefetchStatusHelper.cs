using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download.Queue;

public static class QueuePrefetchStatusHelper
{
    public static void Send(
        IDeezSpoTagListener listener,
        string queueUuid,
        string? artworkStatus,
        string? lyricsStatus,
        string? lyricsType = null)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        listener.Send("updateQueue", new
        {
            uuid = queueUuid,
            prefetchArtworkStatus = artworkStatus,
            prefetchLyricsStatus = lyricsStatus,
            prefetchLyricsType = lyricsType
        });
    }
}
