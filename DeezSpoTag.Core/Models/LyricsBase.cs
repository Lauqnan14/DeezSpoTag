using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models;

/// <summary>
/// Base class for lyrics implementations
/// Provides common functionality for both modern and legacy lyrics formats
/// </summary>
public abstract class LyricsBase
{
    /// <summary>
    /// Unique identifier for the lyrics
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "0";

    /// <summary>
    /// Writers/authors of the lyrics
    /// </summary>
    [JsonPropertyName("writers")]
    public string? Writers { get; set; }

    /// <summary>
    /// Copyright information for the lyrics
    /// </summary>
    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }

    /// <summary>
    /// Unsynchronized lyrics text
    /// </summary>
    [JsonPropertyName("unsyncedLyrics")]
    public string? UnsyncedLyrics { get; set; }

    /// <summary>
    /// List of synchronized lyrics lines with timing
    /// </summary>
    [JsonPropertyName("syncedLyrics")]
    public List<SynchronizedLyric>? SyncedLyrics { get; set; }

    /// <summary>
    /// Error message if lyrics fetching failed
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Raw TTML lyrics payload (Apple Music)
    /// </summary>
    [JsonPropertyName("ttmlLyrics")]
    public string? TtmlLyrics { get; set; }

    /// <summary>
    /// Whether the lyrics contain explicit content
    /// </summary>
    [JsonPropertyName("isExplicit")]
    public bool IsExplicit { get; set; }

    protected LyricsBase()
    {
        SyncedLyrics = new List<SynchronizedLyric>();
    }

    /// <summary>
    /// Check if lyrics data has been loaded successfully
    /// </summary>
    public virtual bool IsLoaded()
    {
        return (SyncedLyrics?.Count > 0)
            || !string.IsNullOrWhiteSpace(UnsyncedLyrics)
            || !string.IsNullOrWhiteSpace(TtmlLyrics);
    }

    /// <summary>
    /// Check if lyrics have synchronized timing information
    /// </summary>
    public virtual bool IsSynced()
    {
        return SyncedLyrics?.Count > 1;
    }

    /// <summary>
    /// Check if lyrics have unsynchronized text
    /// </summary>
    public virtual bool IsUnsynced()
    {
        return !IsSynced() && !string.IsNullOrWhiteSpace(UnsyncedLyrics);
    }

    /// <summary>
    /// Set error message for failed lyrics fetching
    /// </summary>
    public virtual void SetErrorMessage(string errorMessage)
    {
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Generate LRC format content from synchronized lyrics
    /// </summary>
    public virtual string GenerateLrcContent(string? title = null, string? artist = null, string? album = null)
    {
        if (!IsSynced())
            return string.Empty;

        var lrcBuilder = new System.Text.StringBuilder();

        // Add metadata if provided
        if (!string.IsNullOrWhiteSpace(artist))
            lrcBuilder.AppendLine($"[ar:{artist}]");
        if (!string.IsNullOrWhiteSpace(album))
            lrcBuilder.AppendLine($"[al:{album}]");
        if (!string.IsNullOrWhiteSpace(title))
            lrcBuilder.AppendLine($"[ti:{title}]");

        // Add synchronized lyrics
        foreach (var lyric in SyncedLyrics?.Where(l => l.IsValid()) ?? Enumerable.Empty<SynchronizedLyric>())
        {
            lrcBuilder.AppendLine($"{lyric.LrcTimestamp}{lyric.Text}");
        }

        return lrcBuilder.ToString();
    }

    /// <summary>
    /// Convert legacy deezspotag sync format to new format
    /// </summary>
    protected void ConvertLegacySyncFormat(List<object[]> legacySyncData)
    {
        if (legacySyncData == null || legacySyncData.Count == 0)
            return;

        SyncedLyrics = new List<SynchronizedLyric>();

        foreach (var item in legacySyncData
                     .Where(static item =>
                         item.Length >= 2 &&
                         item[0] is string &&
                         item[1] is int)
                     .Select(static item => ((string)item[0], (int)item[1]))
                     .Select(static item => SynchronizedLyric.FromLegacyFormat(item.Item1, item.Item2))
                     .Where(static syncLyric => syncLyric.IsValid()))
        {
            SyncedLyrics.Add(item);
        }
    }
}
