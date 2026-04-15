using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models;

/// <summary>
/// Modern lyrics implementation for Pipe API responses
/// Ported from refreezer's LyricsNew class
/// </summary>
public class LyricsNew : LyricsBase
{
    public LyricsNew() : base()
    {
    }

    /// <summary>
    /// Create from Pipe API JSON response
    /// </summary>
    public LyricsNew(JsonElement json) : base()
    {
        ParseFromPipeApiResponse(json);
    }

    /// <summary>
    /// Create from Pipe API dynamic response
    /// </summary>
    public LyricsNew(dynamic json) : base()
    {
        ParseFromPipeApiResponse(json);
    }

    /// <summary>
    /// Parse lyrics data from Pipe API response
    /// </summary>
    private void ParseFromPipeApiResponse(dynamic json)
    {
        try
        {
            if (json is JsonElement jsonElement)
            {
                ParseFromPipeApiResponse(jsonElement);
                return;
            }

            ParseFromPipeApiResponse(JsonSerializer.SerializeToElement(json));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetErrorMessage($"Error parsing Pipe API response: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse lyrics data from Pipe API JsonElement response
    /// </summary>
    private void ParseFromPipeApiResponse(JsonElement json)
    {
        try
        {
            if (json.ValueKind != JsonValueKind.Object)
            {
                SetErrorMessage("Pipe API response was not an object");
                return;
            }

            if (TryGetPipeApiError(json, out var errorMessage))
            {
                SetErrorMessage(errorMessage);
                return;
            }

            if (!TryGetPipeTrackElement(json, out var trackElement, out var trackError))
            {
                SetErrorMessage(trackError);
                return;
            }

            ApplyTrackFlags(trackElement);
            ApplyTrackLyrics(trackElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetErrorMessage($"Error parsing Pipe API response: {ex.Message}");
        }
    }

    private static bool TryGetPipeApiError(JsonElement json, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!json.TryGetProperty("errors", out var errorsElement) || errorsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var firstError = errorsElement.EnumerateArray().FirstOrDefault();
        if (firstError.ValueKind == JsonValueKind.Undefined
            || !firstError.TryGetProperty("message", out var messageElement))
        {
            return false;
        }

        errorMessage = $"Pipe API Error: {messageElement.GetString()}";
        return true;
    }

    private static bool TryGetPipeTrackElement(JsonElement json, out JsonElement trackElement, out string errorMessage)
    {
        trackElement = default;
        errorMessage = string.Empty;

        if (!json.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Object)
        {
            errorMessage = "Pipe API response contained null data";
            return false;
        }

        if (!dataElement.TryGetProperty("track", out trackElement))
        {
            errorMessage = "Pipe API response missing track data";
            return false;
        }

        if (trackElement.ValueKind != JsonValueKind.Object)
        {
            errorMessage = "Pipe API response contained null track data";
            return false;
        }

        return true;
    }

    private void ApplyTrackFlags(JsonElement trackElement)
    {
        if (trackElement.TryGetProperty("isExplicit", out var isExplicitElement))
        {
            IsExplicit = isExplicitElement.GetBoolean();
        }
    }

    private void ApplyTrackLyrics(JsonElement trackElement)
    {
        if (!trackElement.TryGetProperty("lyrics", out var lyricsElement)
            || lyricsElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (lyricsElement.TryGetProperty("id", out var idElement))
        {
            Id = idElement.GetString() ?? "0";
        }

        if (lyricsElement.TryGetProperty("writers", out var writersElement))
        {
            Writers = writersElement.GetString();
        }

        if (lyricsElement.TryGetProperty("text", out var textElement))
        {
            UnsyncedLyrics = textElement.GetString();
        }

        if (lyricsElement.TryGetProperty("copyright", out var copyrightElement))
        {
            Copyright = copyrightElement.GetString();
        }

        if (lyricsElement.TryGetProperty("synchronizedLines", out var syncedElement))
        {
            ParseSynchronizedLines(syncedElement);
        }
    }

    /// <summary>
    /// Parse synchronized lyrics lines from JsonElement array
    /// </summary>
    private void ParseSynchronizedLines(JsonElement syncedElement)
    {
        try
        {
            SyncedLyrics = new List<SynchronizedLyric>();

            if (syncedElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var lyric in syncedElement.EnumerateArray()
                             .Select(ParseSynchronizedLine)
                             .Where(static lyric => lyric?.IsValid() == true)
                             .Select(static lyric => lyric!))
                {
                    SyncedLyrics.Add(lyric);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetErrorMessage($"Error parsing synchronized lyrics: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse a single synchronized lyric line from JsonElement
    /// </summary>
    private static SynchronizedLyric? ParseSynchronizedLine(JsonElement lineElement)
    {
        try
        {
            var text = lineElement.TryGetProperty("line", out var lineProperty) ? lineProperty.GetString() : "";
            var lrcTimestamp = lineElement.TryGetProperty("lrcTimestamp", out var timestampProperty) ? timestampProperty.GetString() : "";
            var milliseconds = lineElement.TryGetProperty("milliseconds", out var millisecondsProperty) ? millisecondsProperty.GetInt32() : 0;
            var duration = lineElement.TryGetProperty("duration", out var durationProperty) ? durationProperty.GetInt32() : 0;

            if (string.IsNullOrWhiteSpace(lrcTimestamp))
            {
                lrcTimestamp = SynchronizedLyric.BuildLrcTimestamp(milliseconds);
            }

            return new SynchronizedLyric(text, lrcTimestamp, milliseconds, duration);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Create error lyrics instance
    /// </summary>
    public static LyricsNew CreateError(string errorMessage)
    {
        var lyrics = new LyricsNew();
        lyrics.SetErrorMessage(errorMessage);
        return lyrics;
    }
}
