namespace DeezSpoTag.Services.Download.Shared.Models;

/// <summary>
/// Listener interface for deezspotag events
/// Ported from: /src/deezspotag/deezspotag/src/types/listener.ts
/// Enhanced with all deezspotag event types
/// </summary>
public interface IDeezSpoTagListener
{
    /// <summary>
    /// Send event to connected clients
    /// </summary>
    void Send(string eventName, object? data = null);

    // Specific event methods for type safety and better IntelliSense

    /// <summary>
    /// Send download info event
    /// Ported from: downloadInfo event in deezspotag downloader.ts
    /// </summary>
    void SendDownloadInfo(string uuid, string title, object data, string state)
    {
        Send("downloadInfo", new { uuid, title, data, state });
    }

    /// <summary>
    /// Send download warning event
    /// Ported from: downloadWarn event in deezspotag downloader.ts
    /// </summary>
    void SendDownloadWarn(string uuid, object data, string state, string solution)
    {
        Send("downloadWarn", new { uuid, data, state, solution });
    }

    /// <summary>
    /// Send queue update event
    /// Ported from: updateQueue event in deezspotag downloader.ts
    /// </summary>
    void SendUpdateQueue(string uuid, object updateData)
    {
        Send("updateQueue", new { uuid, updateData });
    }

    /// <summary>
    /// Send start generating items event
    /// Ported from: startGeneratingItems event in deezspotag deezSpoTagApp.ts
    /// </summary>
    void SendStartGeneratingItems(string uuid, int total)
    {
        Send("startGeneratingItems", new { uuid, total });
    }

    /// <summary>
    /// Send finish generating items event
    /// Ported from: finishGeneratingItems event in deezspotag deezSpoTagApp.ts
    /// </summary>
    void SendFinishGeneratingItems(string uuid, int total)
    {
        Send("finishGeneratingItems", new { uuid, total });
    }

    /// <summary>
    /// Send queue error event
    /// Ported from: queueError event in deezspotag deezSpoTagApp.ts
    /// </summary>
    void SendQueueError(string link, string error, string? errid = null, string? details = null)
    {
        Send("queueError", new { link, error, errid, details });
    }

    /// <summary>
    /// Send already in queue event
    /// Ported from: alreadyInQueue event in deezspotag deezSpoTagApp.ts
    /// </summary>
    void SendAlreadyInQueue(object downloadObject)
    {
        Send("alreadyInQueue", downloadObject);
    }

    /// <summary>
    /// Send added to queue event
    /// Ported from: addedToQueue event in deezspotag deezSpoTagApp.ts
    /// </summary>
    void SendAddedToQueue(object downloadObjects)
    {
        Send("addedToQueue", downloadObjects);
    }

    /// <summary>
    /// Send start download event
    /// Ported from: startDownload event in deezspotag deezSpoTagApp.ts
    /// </summary>
    void SendStartDownload(string uuid)
    {
        Send("startDownload", uuid);
    }

    /// <summary>
    /// Send finish download event
    /// Ported from: finishDownload event in deezspotag downloader.ts
    /// </summary>
    void SendFinishDownload(string uuid, string title)
    {
        Send("finishDownload", new { uuid, title });
    }

    /// <summary>
    /// Send current item cancelled event
    /// Ported from: currentItemCancelled event in deezspotag downloader.ts
    /// </summary>
    void SendCurrentItemCancelled(string uuid, string title)
    {
        Send("currentItemCancelled", new { uuid, title });
    }

    /// <summary>
    /// Send removed from queue event
    /// Ported from: removedFromQueue event in deezspotag downloader.ts
    /// </summary>
    void SendRemovedFromQueue(string uuid, string? title = null)
    {
        Send("removedFromQueue", new { uuid, title });
    }

    /// <summary>
    /// Send cancelling current item event
    /// Ported from: cancellingCurrentItem event in deezspotag deezSpoTagApp.ts
    /// </summary>
    void SendCancellingCurrentItem(string uuid)
    {
        Send("cancellingCurrentItem", uuid);
    }

    /// <summary>
    /// Send removed all downloads event
    /// Ported from: removedAllDownloads event in deezspotag deezSpoTagApp.ts
    /// </summary>
    void SendRemovedAllDownloads(string? currentItem = null)
    {
        Send("removedAllDownloads", currentItem);
    }

    /// <summary>
    /// Send removed finished downloads event
    /// Ported from: removedFinishedDownloads event in deezspotag deezSpoTagApp.ts
    /// </summary>
    void SendRemovedFinishedDownloads()
    {
        Send("removedFinishedDownloads", new { });
    }

    /// <summary>
    /// Send download progress event
    /// Custom event for progress tracking
    /// </summary>
    void SendDownloadProgress(string uuid, string title, double progress, int downloaded, int failed)
    {
        Send("downloadProgress", new { uuid, title, progress, downloaded, failed });
    }

    /// <summary>
    /// Send track progress event
    /// Custom event for individual track progress within collections
    /// </summary>
    void SendTrackProgress(string uuid, string title, int trackIndex, string trackTitle, string state)
    {
        Send("trackProgress", new { uuid, title, trackIndex, trackTitle, state });
    }
}

/// <summary>
/// Default implementation of deezspotag listener
/// </summary>
public class DeezSpoTagListener : IDeezSpoTagListener
{
    private readonly Action<string, object>? _sendCallback;

    public DeezSpoTagListener(Action<string, object>? sendCallback = null)
    {
        _sendCallback = sendCallback;
    }

    public void Send(string eventName, object? data = null)
    {
        if (data != null)
        {
            _sendCallback?.Invoke(eventName, data);
        }
    }
}