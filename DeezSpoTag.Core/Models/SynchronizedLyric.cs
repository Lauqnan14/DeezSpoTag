using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models;

/// <summary>
/// Represents a single line of synchronized lyrics with timing information
/// Ported from refreezer's SynchronizedLyric model
/// </summary>
public class SynchronizedLyric
{
    /// <summary>
    /// The text content of the lyric line
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>
    /// The LRC timestamp format (e.g., "[00:12.34]")
    /// </summary>
    [JsonPropertyName("lrcTimestamp")]
    public string? LrcTimestamp { get; set; }

    /// <summary>
    /// Offset in milliseconds from the start of the track
    /// </summary>
    [JsonPropertyName("milliseconds")]
    public int Milliseconds { get; set; }

    /// <summary>
    /// Duration of this lyric line in milliseconds
    /// </summary>
    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    /// <summary>
    /// Offset as TimeSpan for easier manipulation
    /// </summary>
    [JsonIgnore]
    public TimeSpan Offset => TimeSpan.FromMilliseconds(Milliseconds);

    public SynchronizedLyric()
    {
    }

    public SynchronizedLyric(string? text, string? lrcTimestamp, int milliseconds, int duration = 0)
    {
        Text = text;
        LrcTimestamp = lrcTimestamp;
        Milliseconds = milliseconds;
        Duration = duration;
    }

    /// <summary>
    /// Create from legacy deezspotag format
    /// </summary>
    public static SynchronizedLyric FromLegacyFormat(string text, int milliseconds)
    {
        var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
        var lrcTimestamp = $"[{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}.{timeSpan.Milliseconds / 10:D2}]";

        return new SynchronizedLyric(text, lrcTimestamp, milliseconds);
    }

    public static string BuildLrcTimestamp(int milliseconds)
    {
        var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
        return $"[{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}.{timeSpan.Milliseconds / 10:D2}]";
    }

    /// <summary>
    /// Create from refreezer JSON format
    /// </summary>
    public static SynchronizedLyric FromJson(dynamic json)
    {
        var text = json.line?.ToString() ?? "";
        var lrcTimestamp = json.lrcTimestamp?.ToString() ?? "";
        var milliseconds = json.milliseconds != null ? (int)json.milliseconds : 0;
        var duration = json.duration != null ? (int)json.duration : 0;

        if (string.IsNullOrWhiteSpace(lrcTimestamp))
        {
            lrcTimestamp = BuildLrcTimestamp(milliseconds);
        }

        return new SynchronizedLyric(text, lrcTimestamp, milliseconds, duration);
    }

    /// <summary>
    /// Check if this lyric line has valid content
    /// </summary>
    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(Text) && !string.IsNullOrWhiteSpace(LrcTimestamp);
    }

    public override string ToString()
    {
        return $"{LrcTimestamp}{Text}";
    }
}
