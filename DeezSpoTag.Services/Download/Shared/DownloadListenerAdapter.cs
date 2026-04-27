using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download.Shared;

/// <summary>
/// Adapter to convert IDeezSpoTagListener to IDownloadListener.
/// </summary>
internal sealed class DownloadListenerAdapter : IDownloadListener
{
    private readonly IDeezSpoTagListener? _deezspoTagListener;

    public DownloadListenerAdapter(IDeezSpoTagListener? deezspoTagListener)
    {
        _deezspoTagListener = deezspoTagListener;
    }

    public void OnProgressUpdate(DownloadObject downloadObject)
    {
        Send(
            "downloadProgress",
            downloadObject,
            ("progress", downloadObject.Progress),
            ("progressNext", downloadObject.ProgressNext));
    }

    public void OnDownloadStart(DownloadObject downloadObject)
    {
        Send("downloadStart", downloadObject);
    }

    public void OnDownloadInfo(DownloadObject downloadObject, string message, string state)
    {
        Send("downloadInfo", downloadObject, ("data", message), ("state", state));
    }

    public void OnDownloadComplete(DownloadObject downloadObject)
    {
        Send("downloadComplete", downloadObject);
    }

    public void OnDownloadError(DownloadObject downloadObject, DownloadError error)
    {
        Send(
            "downloadError",
            downloadObject,
            ("error", error.Message),
            ("errorId", error.ErrorId),
            ("stack", error.Stack),
            ("type", error.Type),
            ("data", error.Data));
    }

    public void OnDownloadWarning(DownloadObject downloadObject, string message, string state, string solution)
    {
        Send("downloadWarn", downloadObject, ("data", message), ("state", state), ("solution", solution));
    }

    public void OnCurrentItemCancelled(DownloadObject downloadObject)
    {
        Send("currentItemCancelled", downloadObject);
    }

    public void OnRemovedFromQueue(DownloadObject downloadObject)
    {
        Send("removedFromQueue", downloadObject);
    }

    public void OnFinishDownload(DownloadObject downloadObject)
    {
        // FinishDownload is emitted by DeezSpoTagApp after status updates to avoid duplicates.
    }

    public void OnUpdateQueue(
        DownloadObject downloadObject,
        bool downloaded = false,
        bool failed = false,
        bool alreadyDownloaded = false)
    {
        Send(
            "updateQueue",
            downloadObject,
            ("downloaded", downloaded),
            ("failed", failed),
            ("alreadyDownloaded", alreadyDownloaded),
            ("extrasPath", downloadObject.ExtrasPath));
    }

    private void Send(string eventName, DownloadObject downloadObject, params (string Key, object? Value)[] fields)
    {
        if (_deezspoTagListener == null)
        {
            return;
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["uuid"] = downloadObject.Uuid,
            ["title"] = downloadObject.Title
        };

        foreach (var (key, value) in fields)
        {
            payload[key] = value;
        }

        _deezspoTagListener.Send(eventName, payload);
    }
}
