using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Core.Models.Download;

/// <summary>
/// Base download object (EXACT PORT from deezspotag DownloadObject)
/// </summary>
public abstract class DownloadObject
{
    public string Uuid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public int Size { get; set; } = 0;
    public int Bitrate { get; set; } = 0;
    public bool IsCanceled { get; set; } = false;
    public int Downloaded { get; set; } = 0;
    public int Failed { get; set; } = 0;
    public double Progress { get; set; } = 0;
    public double ProgressNext { get; set; } = 0;
    public string? ExtrasPath { get; set; }
    public List<DownloadFile> Files { get; set; } = new();
    public List<DownloadError> Errors { get; set; } = new();
    public long? DestinationFolderId { get; set; }

    // EXACT PORT: Additional properties for deezspotag compatibility
    public DownloadSingle? Single { get; set; }
    public DownloadCollection? Collection { get; set; }

    /// <summary>
    /// Update download progress
    /// </summary>
    public virtual void UpdateProgress(IDownloadListener? listener = null)
    {
        if (Size == 0) return;

        var currentProgress = Math.Round((Downloaded + (ProgressNext / 100)) / Size * 100, 2);
        if (Math.Abs(currentProgress - Progress) > 0.01)
        {
            Progress = currentProgress;
            listener?.OnProgressUpdate(this);
        }
    }

    /// <summary>
    /// Complete track progress
    /// </summary>
    public virtual void CompleteTrackProgress(IDownloadListener? listener = null)
    {
        ProgressNext = 0;
        UpdateProgress(listener);
    }

    /// <summary>
    /// Cancel the download
    /// </summary>
    public virtual void Cancel()
    {
        IsCanceled = true;
    }

    public bool IsCancellationRequested(System.Threading.CancellationToken cancellationToken = default)
    {
        return IsCanceled || cancellationToken.IsCancellationRequested;
    }
}

/// <summary>
/// EXACT PORT: Single download data structure from deezspotag
/// </summary>
public class DownloadSingle
{
    public Track Track { get; set; } = new();
    public Album? Album { get; set; }
}

/// <summary>
/// EXACT PORT: Collection download data structure from deezspotag
/// </summary>
public class DownloadCollection
{
    public List<Track> Tracks { get; set; } = new();
    public Album? Album { get; set; }
    public Playlist? Playlist { get; set; }
}

/// <summary>
/// Single track download object
/// </summary>
public class SingleDownloadObject : DownloadObject
{
    public Track Track { get; set; } = new();
    public Album? Album { get; set; }
    public Artist? Artist { get; set; }
    public Playlist? Playlist { get; set; }

    public SingleDownloadObject()
    {
        Type = "track";
        Size = 1;
    }
}

/// <summary>
/// Collection download object (album, playlist)
/// </summary>
public class CollectionDownloadObject : DownloadObject
{
    public List<Track> Tracks { get; set; } = new();
    public Album? Album { get; set; }
    public Playlist? Playlist { get; set; }
    public Artist? Artist { get; set; }

    public CollectionDownloadObject()
    {
        Size = Tracks.Count;
    }

    public void UpdateSize()
    {
        Size = Tracks.Count;
    }
}

/// <summary>
/// Download file information
/// </summary>
public class DownloadFile
{
    public string Filename { get; set; } = "";
    public string Path { get; set; } = "";
    public Dictionary<string, object> Data { get; set; } = new();
    public bool Searched { get; set; } = false;
    public List<ImageUrl> AlbumUrls { get; set; } = new();
    public List<ImageUrl> ArtistUrls { get; set; } = new();
    public string AlbumPath { get; set; } = "";
    public string ArtistPath { get; set; } = "";
    public string AlbumFilename { get; set; } = "";
    public string ArtistFilename { get; set; } = "";
}

/// <summary>
/// Download error information
/// </summary>
public class DownloadError
{
    public string Message { get; set; } = "";
    public string? ErrorId { get; set; }
    public string? Stack { get; set; }
    public string Type { get; set; } = "";
    public Dictionary<string, object> Data { get; set; } = new();
}

/// <summary>
/// Image URL for artwork downloads
/// </summary>
public class ImageUrl
{
    public string Url { get; set; } = "";
    public string Extension { get; set; } = "";
}

/// <summary>
/// Download listener interface (EXACT PORT from deezspotag Listener)
/// </summary>
public interface IDownloadListener
{
    void OnProgressUpdate(DownloadObject downloadObject);
    void OnDownloadStart(DownloadObject downloadObject);
    void OnDownloadComplete(DownloadObject downloadObject);
    void OnDownloadError(DownloadObject downloadObject, DownloadError error);
    void OnDownloadInfo(DownloadObject downloadObject, string message, string state);
    void OnDownloadWarning(DownloadObject downloadObject, string message, string state, string solution);
    
    // EXACT PORT: Additional methods from deezspotag listener
    void OnCurrentItemCancelled(DownloadObject downloadObject);
    void OnRemovedFromQueue(DownloadObject downloadObject);
    void OnFinishDownload(DownloadObject downloadObject);
    void OnUpdateQueue(DownloadObject downloadObject, bool downloaded = false, bool failed = false, bool alreadyDownloaded = false);
}
