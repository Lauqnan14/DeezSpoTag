using System.Text.Json;
using System.Web;

namespace DeezSpoTag.Core.Models;

/// <summary>
/// Classic lyrics implementation for legacy GW API responses
/// Ported from refreezer's LyricsClassic class
/// </summary>
public class LyricsClassic : LyricsBase
{
    public LyricsClassic() : base()
    {
    }

    /// <summary>
    /// Create from legacy GW API JSON response
    /// </summary>
    public LyricsClassic(JsonElement json) : base()
    {
        ParseFromGwApiResponse(json);
    }

    /// <summary>
    /// Create from legacy GW API dynamic response
    /// </summary>
    public LyricsClassic(dynamic json) : base()
    {
        ParseFromGwApiResponse(json);
    }

    /// <summary>
    /// Parse lyrics data from legacy GW API response
    /// </summary>
    private void ParseFromGwApiResponse(dynamic json)
    {
        try
        {
            if (json == null)
            {
                SetErrorMessage("No lyrics data received from GW API");
                return;
            }

            // Parse basic lyrics information
            Id = json.LYRICS_ID?.ToString() ?? "0";
            Writers = json.LYRICS_WRITERS?.ToString();
            UnsyncedLyrics = json.LYRICS_TEXT?.ToString();
            Copyright = json.LYRICS_COPYRIGHTS?.ToString();

            // Parse synchronized lyrics from LYRICS_SYNC_JSON
            var syncedJsonArray = json.LYRICS_SYNC_JSON;
            if (syncedJsonArray != null)
            {
                ParseSynchronizedLyricsFromGwApi(syncedJsonArray);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetErrorMessage($"Error parsing GW API response: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse lyrics data from legacy GW API JsonElement response
    /// </summary>
    private void ParseFromGwApiResponse(JsonElement json)
    {
        try
        {
            // Parse basic lyrics information
            if (json.TryGetProperty("LYRICS_ID", out var idElement))
                Id = idElement.GetString() ?? "0";

            if (json.TryGetProperty("LYRICS_WRITERS", out var writersElement))
                Writers = writersElement.GetString();

            if (json.TryGetProperty("LYRICS_TEXT", out var textElement))
                UnsyncedLyrics = textElement.GetString();

            if (json.TryGetProperty("LYRICS_COPYRIGHTS", out var copyrightElement))
                Copyright = copyrightElement.GetString();

            // Parse synchronized lyrics
            if (json.TryGetProperty("LYRICS_SYNC_JSON", out var syncedElement))
            {
                ParseSynchronizedLyricsFromGwApi(syncedElement);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetErrorMessage($"Error parsing GW API response: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse synchronized lyrics from GW API format
    /// </summary>
    private void ParseSynchronizedLyricsFromGwApi(dynamic syncedJsonArray)
    {
        try
        {
            SyncedLyrics = new List<SynchronizedLyric>();

            if (syncedJsonArray is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var lyric in jsonElement.EnumerateArray()
                             .Select(ParseGwApiSynchronizedLine)
                             .Where(static lyric => lyric?.IsValid() == true)
                             .Select(static lyric => lyric!))
                {
                    SyncedLyrics.Add(lyric);
                }
            }
            else
            {
                // Handle dynamic array from GW API
                foreach (var lyric in ((IEnumerable<dynamic>)syncedJsonArray)
                             .Select(ParseGwApiSynchronizedLine)
                             .Where(static lyric => lyric?.IsValid() == true)
                             .Select(static lyric => lyric!))
                {
                    SyncedLyrics.Add(lyric);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetErrorMessage($"Error parsing GW API synchronized lyrics: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse synchronized lyrics from GW API JsonElement format
    /// </summary>
    private void ParseSynchronizedLyricsFromGwApi(JsonElement syncedElement)
    {
        try
        {
            SyncedLyrics = new List<SynchronizedLyric>();

            if (syncedElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var lyric in syncedElement.EnumerateArray()
                             .Select(ParseGwApiSynchronizedLine)
                             .Where(static lyric => lyric?.IsValid() == true)
                             .Select(static lyric => lyric!))
                {
                    SyncedLyrics.Add(lyric);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetErrorMessage($"Error parsing GW API synchronized lyrics: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse a single synchronized lyric line from GW API format
    /// </summary>
    private static SynchronizedLyric? ParseGwApiSynchronizedLine(dynamic lineJson)
    {
        try
        {
            var text = HttpUtility.HtmlDecode(lineJson.line?.ToString() ?? "");
            var lrcTimestamp = lineJson.lrc_timestamp?.ToString() ?? "";
            var milliseconds = lineJson.milliseconds != null ? (int)lineJson.milliseconds : 0;

            // Skip empty lines but keep the timing for proper synchronization
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(lrcTimestamp))
            {
                lrcTimestamp = SynchronizedLyric.BuildLrcTimestamp(milliseconds);
            }

            return new SynchronizedLyric(text, lrcTimestamp, milliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a single synchronized lyric line from GW API JsonElement format
    /// </summary>
    private static SynchronizedLyric? ParseGwApiSynchronizedLine(JsonElement lineElement)
    {
        try
        {
            var text = "";
            var lrcTimestamp = "";
            var milliseconds = 0;

            if (lineElement.TryGetProperty("line", out var lineProperty))
                text = HttpUtility.HtmlDecode(lineProperty.GetString() ?? "");

            if (lineElement.TryGetProperty("lrc_timestamp", out var timestampProperty))
                lrcTimestamp = timestampProperty.GetString() ?? "";

            if (lineElement.TryGetProperty("milliseconds", out var millisecondsProperty))
                milliseconds = millisecondsProperty.GetInt32();

            // Skip empty lines but keep the timing for proper synchronization
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(lrcTimestamp))
            {
                lrcTimestamp = SynchronizedLyric.BuildLrcTimestamp(milliseconds);
            }

            return new SynchronizedLyric(text, lrcTimestamp, milliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Create error lyrics instance
    /// </summary>
    public static LyricsClassic CreateError(string errorMessage)
    {
        var lyrics = new LyricsClassic();
        lyrics.SetErrorMessage(errorMessage);
        return lyrics;
    }
}
