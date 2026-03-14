namespace DeezSpoTag.Core.Models;

/// <summary>
/// Lyrics model (ported from deezspotag Lyrics.ts)
/// </summary>
public class Lyrics
{
    public string Id { get; set; } = "0";
    public string Sync { get; set; } = "";
    public string Unsync { get; set; } = "";
    public List<SyncLyric> SyncID3 { get; set; } = new();

    public Lyrics()
    {
    }

    public Lyrics(string id)
    {
        Id = id;
    }

    /// <summary>
    /// Parse lyrics data from Gateway API response (ported from deezspotag parseLyrics)
    /// </summary>
    public void ParseLyrics(Dictionary<string, object>? lyricsData)
    {
        if (lyricsData == null) return;

        try
        {
            // Get synchronized lyrics
            if (lyricsData.TryGetValue("LYRICS_SYNC_JSON", out var syncJson) && syncJson != null)
            {
                var syncData = ParseSyncLyrics(syncJson.ToString());
                if (syncData.Count > 0)
                {
                    Sync = GenerateLRCFormat(syncData);
                    SyncID3 = syncData;
                }
            }

            // Get unsynchronized lyrics
            if (lyricsData.TryGetValue("LYRICS_TEXT", out var unsyncText) && unsyncText != null)
            {
                Unsync = unsyncText.ToString() ?? "";
            }

            // Fallback to LYRICS_SYNC if no unsync text
            if (string.IsNullOrEmpty(Unsync) && lyricsData.TryGetValue("LYRICS_SYNC", out var syncText) && syncText != null)
            {
                Unsync = ExtractTextFromSync(syncText.ToString());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If parsing fails, keep empty lyrics
            Sync = "";
            Unsync = "";
            SyncID3.Clear();
        }
    }

    /// <summary>
    /// Parse synchronized lyrics JSON
    /// </summary>
    private static List<SyncLyric> ParseSyncLyrics(string? syncJson)
    {
        var syncLyrics = new List<SyncLyric>();
        
        if (string.IsNullOrEmpty(syncJson)) return syncLyrics;

        try
        {
            var lines = syncJson.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var parsedLine in lines
                         .Select(static line => line.Split('\t'))
                         .Select(static parts =>
                             parts.Length >= 2 && int.TryParse(parts[0], out var timestamp)
                                 ? (timestamp: (int?)timestamp, text: parts[1])
                                 : (timestamp: null, text: string.Empty))
                         .Where(static parsedLine => parsedLine.timestamp.HasValue))
            {
                syncLyrics.Add(new SyncLyric
                {
                    Timestamp = parsedLine.timestamp!.Value,
                    Text = parsedLine.text
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // If parsing fails, return empty list
        }

        return syncLyrics;
    }

    /// <summary>
    /// Generate LRC format from synchronized lyrics
    /// </summary>
    private static string GenerateLRCFormat(List<SyncLyric> syncLyrics)
    {
        var lrcLines = new List<string>();

        foreach (var lyric in syncLyrics)
        {
            var minutes = lyric.Timestamp / 60000;
            var seconds = (lyric.Timestamp % 60000) / 1000;
            var centiseconds = (lyric.Timestamp % 1000) / 10;

            var timeTag = $"[{minutes:D2}:{seconds:D2}.{centiseconds:D2}]";
            lrcLines.Add($"{timeTag}{lyric.Text}");
        }

        return string.Join("\n", lrcLines);
    }

    /// <summary>
    /// Extract plain text from synchronized lyrics
    /// </summary>
    private static string ExtractTextFromSync(string? syncText)
    {
        if (string.IsNullOrEmpty(syncText)) return "";

        try
        {
            var lines = syncText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var textLines = new List<string>();

            foreach (var parts in lines
                         .Select(static line => line.Split('\t'))
                         .Where(static parts => parts.Length >= 2))
            {
                textLines.Add(parts[1]);
            }

            return string.Join("\n", textLines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return "";
        }
    }

    /// <summary>
    /// Check if lyrics are available
    /// </summary>
    public bool HasLyrics => !string.IsNullOrEmpty(Unsync) || !string.IsNullOrEmpty(Sync);

    /// <summary>
    /// Check if synchronized lyrics are available
    /// </summary>
    public bool HasSyncLyrics => !string.IsNullOrEmpty(Sync) && SyncID3.Count > 0;
}

/// <summary>
/// Synchronized lyric line
/// </summary>
public class SyncLyric
{
    public int Timestamp { get; set; } // Timestamp in milliseconds
    public string Text { get; set; } = "";
}
