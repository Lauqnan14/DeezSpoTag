using DeezSpoTag.Services.Download.Shared.Models;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared;

/// <summary>
/// EXACT port of deezspotag progress tracking from decryption.ts
/// Tracks download progress with granular updates exactly like deezspotag
/// </summary>
public class DeezSpoTagProgressTracker : IDisposable
{
    private readonly ILogger? _logger;
    private readonly DeezSpoTagDownloadObject _downloadObject;
    private readonly IDeezSpoTagListener? _listener;
    private readonly CancellationToken _cancellationToken;

    // Progress tracking state - EXACT port from deezspotag
    private long _chunkLength = 0;
    private long _complete = 0;
    private long _lastChunkTimestamp = 0;
    private readonly object _progressLock = new();
    private bool _disposed;

    public DeezSpoTagProgressTracker(
        DeezSpoTagDownloadObject downloadObject,
        IDeezSpoTagListener? listener,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        _downloadObject = downloadObject;
        _listener = listener;
        _cancellationToken = cancellationToken;
        _logger = logger;
    }

    /// <summary>
    /// Initialize progress tracking with total content length
    /// EXACT port from deezspotag decryption.ts response handler
    /// </summary>
    public void InitializeProgress(long totalContentLength)
    {
        lock (_progressLock)
        {
            _complete = totalContentLength;
            _chunkLength = 0;

            if (_complete == 0)
            {
                _logger?.LogWarning("Download content length is 0 for {UUID}", _downloadObject.UUID);
            }
        }
    }

    /// <summary>
    /// Update progress for each downloaded chunk
    /// EXACT port from deezspotag decryption.ts data handler
    /// </summary>
    public void UpdateChunkProgress(int chunkSize)
    {
        if (_downloadObject.IsCanceled || _cancellationToken.IsCancellationRequested)
        {
            return;
        }

        lock (_progressLock)
        {
            _chunkLength += chunkSize;
            var nowTicks = DateTimeOffset.UtcNow.Ticks;
            if (_lastChunkTimestamp != 0)
            {
                var elapsedSeconds = TimeSpan.FromTicks(nowTicks - _lastChunkTimestamp).TotalSeconds;
                if (elapsedSeconds > 0)
                {
                    var bytesPerSecond = chunkSize / elapsedSeconds;
                    DeezSpoTagSpeedTracker.ReportSpeed(_downloadObject.UUID, bytesPerSecond);
                }
            }
            _lastChunkTimestamp = nowTicks;

            if (_downloadObject != null && _complete > 0)
            {
                // EXACT formula from deezspotag: (chunk.length / complete / downloadObject.size) * 100
                var progressIncrement = (double)chunkSize / _complete / _downloadObject.Size * 100;
                _downloadObject.ProgressNext += progressIncrement;

                // EXACT port: Call updateProgress which only updates UI every 2%
                _downloadObject.UpdateProgress(_listener);
            }
        }
    }

    /// <summary>
    /// Remove progress when download fails or is cancelled
    /// EXACT port from deezspotag decryption.ts error handler
    /// </summary>
    public void RemoveChunkProgress()
    {
        lock (_progressLock)
        {
            if (_downloadObject != null && _chunkLength != 0 && _complete > 0)
            {
                // EXACT formula from deezspotag: (chunkLength / complete / downloadObject.size) * 100
                var progressDecrement = (double)_chunkLength / _complete / _downloadObject.Size * 100;
                _downloadObject.ProgressNext -= progressDecrement;

                // EXACT port: Call updateProgress to update UI
                _downloadObject.UpdateProgress(_listener);
            }
        }
    }

    /// <summary>
    /// Get current progress statistics
    /// </summary>
    public ProgressStats GetProgressStats()
    {
        lock (_progressLock)
        {
            return new ProgressStats
            {
                ChunkLength = _chunkLength,
                Complete = _complete,
                ProgressNext = _downloadObject.ProgressNext,
                Progress = _downloadObject.Progress,
                PercentageComplete = _complete > 0 ? (double)_chunkLength / _complete * 100 : 0
            };
        }
    }

    /// <summary>
    /// Send download info message exactly like deezspotag
    /// </summary>
    public void SendDownloadInfo(string state, object? data = null)
    {
        var itemData = data ?? new
        {
            id = _downloadObject.Id,
            title = _downloadObject.Title,
            artist = _downloadObject.Artist
        };

        _listener?.Send("downloadInfo", new
        {
            uuid = _downloadObject.UUID,
            title = _downloadObject.Title,
            data = itemData,
            state = state
        });
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = disposing;
    }
}

/// <summary>
/// Progress statistics container
/// </summary>
public class ProgressStats
{
    public long ChunkLength { get; set; }
    public long Complete { get; set; }
    public double ProgressNext { get; set; }
    public double Progress { get; set; }
    public double PercentageComplete { get; set; }
}

/// <summary>
/// Enhanced download listener that integrates with deezspotag progress tracking
/// </summary>
public class DeezSpoTagProgressDownloadListener : DeezSpoTag.Core.Models.Download.IDownloadListener
{
    private readonly IDeezSpoTagListener? _deezspotagListener;
    private readonly string _uuid;
    private readonly DeezSpoTagDownloadObject _deezspotagDownloadObject;

    public DeezSpoTagProgressDownloadListener(
        IDeezSpoTagListener? deezspotagListener,
        string uuid,
        DeezSpoTagDownloadObject deezspotagDownloadObject,
        DeezSpoTagProgressTracker? progressTracker = null)
    {
        _deezspotagListener = deezspotagListener;
        _uuid = uuid;
        _deezspotagDownloadObject = deezspotagDownloadObject;
        _ = progressTracker;
    }

    public void OnProgressUpdate(DeezSpoTag.Core.Models.Download.DownloadObject downloadObject)
    {
        // Update the deezspotag download object's progress using the exact deezspotag logic
        // The progress tracking is handled by DeezSpoTagProgressTracker during streaming

        // Sync the final progress values
        _deezspotagDownloadObject.Progress = downloadObject.Progress;
        _deezspotagDownloadObject.ProgressNext = downloadObject.Progress;

        // Send update using deezspotag format
        _deezspotagListener?.Send("updateQueue", new
        {
            uuid = _uuid,
            progress = _deezspotagDownloadObject.Progress,
            downloaded = _deezspotagDownloadObject.Downloaded,
            failed = _deezspotagDownloadObject.Failed
        });
    }

    public void OnDownloadStart(DeezSpoTag.Core.Models.Download.DownloadObject downloadObject)
    {
        _deezspotagListener?.Send("downloadStart", new
        {
            uuid = _uuid,
        });
    }

    public void OnDownloadComplete(DeezSpoTag.Core.Models.Download.DownloadObject downloadObject)
    {
        // Complete track progress using exact deezspotag logic
        _deezspotagDownloadObject.CompleteTrackProgress(_deezspotagListener);

        _deezspotagListener?.Send("downloadComplete", new
        {
            uuid = _uuid,
        });
    }

    public void OnDownloadError(DeezSpoTag.Core.Models.Download.DownloadObject downloadObject, DeezSpoTag.Core.Models.Download.DownloadError error)
    {
        // Remove track progress on error using exact deezspotag logic
        _deezspotagDownloadObject.RemoveTrackProgress(_deezspotagListener);

        _deezspotagListener?.Send("downloadError", new
        {
            uuid = _uuid,
            error = error.Message
        });
    }

    public void OnDownloadInfo(DeezSpoTag.Core.Models.Download.DownloadObject downloadObject, string message, string state)
    {
        _deezspotagListener?.Send("downloadInfo", new
        {
            uuid = _uuid,
            data = new { message, state }
        });
    }

    public void OnDownloadWarning(DeezSpoTag.Core.Models.Download.DownloadObject downloadObject, string message, string state, string solution)
    {
        _deezspotagListener?.Send("downloadWarning", new
        {
            uuid = _uuid,
            data = new { message, state, solution }
        });
    }

    public void OnCurrentItemCancelled(DeezSpoTag.Core.Models.Download.DownloadObject downloadObject)
    {
        _deezspotagListener?.Send("currentItemCancelled", new
        {
            uuid = _uuid
        });
    }

    public void OnRemovedFromQueue(DeezSpoTag.Core.Models.Download.DownloadObject downloadObject)
    {
        _deezspotagListener?.Send("removedFromQueue", new
        {
            uuid = _uuid
        });
    }

    public void OnFinishDownload(DeezSpoTag.Core.Models.Download.DownloadObject downloadObject)
    {
        // FinishDownload is emitted by DeezSpoTagApp after status updates to avoid duplicates.
    }

    public void OnUpdateQueue(DeezSpoTag.Core.Models.Download.DownloadObject downloadObject, bool downloaded = false, bool failed = false, bool alreadyDownloaded = false)
    {
        _deezspotagListener?.Send("updateQueue", new
        {
            uuid = _uuid,
            downloaded,
            failed,
            alreadyDownloaded
        });
    }
}
