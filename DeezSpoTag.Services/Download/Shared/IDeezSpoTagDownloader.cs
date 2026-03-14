using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download.Shared;

/// <summary>
/// Interface for deezspotag downloaders
/// </summary>
public interface IDeezSpoTagDownloader : IDisposable
{
    /// <summary>
    /// The download object being processed
    /// </summary>
    DeezSpoTagDownloadObject DownloadObject { get; }

    /// <summary>
    /// Start the download process
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Cancel the download
    /// </summary>
    void Cancel();
}