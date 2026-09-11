using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>
/// Extracts Audiomack's Next.js v13 page payload from a public track page and
/// locates the song object inside it. Used to confirm the real mood/subgenre
/// schema and, when AUDIOMACK_DEBUG_PAYLOAD=1, to dump a sanitized copy to
/// /tmp/deezspotag-audiomack-nextdata.json. Never logs or saves cookies,
/// sessions or authorization values.
/// </summary>
public static class AudiomackNextDataExtractor
{
    private static readonly Regex ChunkRegex = new(
        @"self\.__next_f\.push\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Fetches the public track page and returns the embedded song object as a
    /// JsonElement, plus the page payload for diagnostics.
    /// </summary>
    public static async Task<JsonElement?> FetchSongObjectAsync(
        IHttpClientFactory httpClientFactory,
        string artistSlug,
        string songSlug,
        string? debugOutputPath = null,
        CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        using var response = await client.GetAsync(
            $"https://audiomack.com/{Uri.EscapeDataString(artistSlug)}/song/{Uri.EscapeDataString(songSlug)}",
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var payload = ExtractNextData(html);
        if (debugOutputPath is not null)
        {
            TrySaveSanitizedPayload(debugOutputPath, payload);
        }

        return FindSongObject(payload) ?? FindJsonLdSong(html);
    }

    /// <summary>yt-dlp style Next.js v13 data extraction: join the RSC chunk strings.</summary>
    public static string ExtractNextData(string html)
    {
        var data = new StringBuilder();
        var marker = "self.__next_f.push(";
        var pos = 0;
        while (true)
        {
            var idx = html.IndexOf(marker, pos, StringComparison.Ordinal);
            if (idx < 0)
            {
                break;
            }

            var i = idx + marker.Length;
            while (i < html.Length && (html[i] == ' ' || html[i] == '\n'))
            {
                i++;
            }

            if (i >= html.Length || html[i] != '[')
            {
                pos = i + 1;
                continue;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            var j = i;
            while (j < html.Length)
            {
                var ch = html[j];
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = !inString;
                }
                else if (!inString)
                {
                    if (ch == '[')
                    {
                        depth++;
                    }
                    else if (ch == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            try
                            {
                                var parsed = JsonSerializer.Deserialize<JsonElement[]>(html[i..(j + 1)]);
                                if (parsed is not null)
                                {
                                    foreach (var part in parsed)
                                    {
                                        if (part.ValueKind == JsonValueKind.String)
                                        {
                                            data.Append(part.GetString());
                                        }
                                    }
                                }
                            }
                            catch (JsonException)
                            {
                                // Malformed chunk: skipped, the payload remains usable.
                            }

                            pos = j + 1;
                            break;
                        }
                    }
                }

                j++;
            }
        }

        return data.ToString();
    }

    /// <summary>Brace-matches the song object inside the RSC payload.</summary>
    public static JsonElement? FindSongObject(string nextData)
    {
        if (string.IsNullOrEmpty(nextData))
        {
            return null;
        }

        var wrapped = FindObjectAtAnchor(nextData, "\"song\":{\"id\"", skipPrefix: "\"song\":");
        if (wrapped is not null)
        {
            return wrapped;
        }

        var byType = FindBestTypeSongObject(nextData);
        if (byType is not null)
        {
            return byType;
        }

        var titleStart = FindObjectByTitleAnchor(nextData);
        return titleStart >= 0 ? ParseObjectAt(nextData, titleStart) : null;
    }

    /// <summary>
    /// JSON-LD <c>MusicRecording</c> fallback when the RSC song object cannot be
    /// located. Supplies at least title/artist/genre, which is enough for AutoTag
    /// to avoid writing a junk catalog bucket.
    /// </summary>
    public static JsonElement? FindJsonLdSong(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        const string marker = "application/ld+json";
        var pos = 0;
        while (true)
        {
            var markerIndex = html.IndexOf(marker, pos, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            var scriptStart = html.IndexOf('>', markerIndex);
            if (scriptStart < 0)
            {
                return null;
            }

            var scriptEnd = html.IndexOf("</script>", scriptStart, StringComparison.OrdinalIgnoreCase);
            if (scriptEnd < 0)
            {
                return null;
            }

            var raw = html[(scriptStart + 1)..scriptEnd].Trim();
            pos = scriptEnd + 1;
            if (raw.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (!IsMusicRecording(root))
                {
                    continue;
                }

                var payload = new Dictionary<string, object?>
                {
                    ["title"] = GetJsonString(root, "name"),
                    ["artist"] = ReadJsonLdArtist(root),
                    ["genre"] = GetJsonString(root, "genre"),
                    ["url"] = GetJsonString(root, "url"),
                    ["isrc"] = GetJsonString(root, "isrcCode"),
                    ["type"] = "song"
                };

                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload));
            }
            catch (JsonException)
            {
                // Malformed JSON-LD: try the next script block.
            }
        }
    }

    private static JsonElement? FindBestTypeSongObject(string nextData)
    {
        if (nextData.IndexOf("\"type\":\"song\"", StringComparison.Ordinal) < 0
            && nextData.IndexOf("\"type\": \"song\"", StringComparison.Ordinal) < 0)
        {
            return null;
        }

        JsonElement? best = null;
        var bestScore = -1;
        var pos = 0;
        while (pos < nextData.Length)
        {
            var start = nextData.IndexOf('{', pos);
            if (start < 0)
            {
                break;
            }

            pos = start + 1;
            var parsed = ParseObjectAt(nextData, start);
            if (parsed is not JsonElement element
                || element.ValueKind != JsonValueKind.Object
                || !IsTypeSong(element))
            {
                continue;
            }

            if (!element.TryGetProperty("title", out var title)
                || title.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(title.GetString()))
            {
                continue;
            }

            var score = ScoreSongObject(element);
            if (score > bestScore)
            {
                best = element;
                bestScore = score;
            }
        }

        return best;
    }

    private static bool IsTypeSong(JsonElement element)
        => element.TryGetProperty("type", out var type)
           && type.ValueKind == JsonValueKind.String
           && string.Equals(type.GetString(), "song", StringComparison.OrdinalIgnoreCase);

    private static int ScoreSongObject(JsonElement element)
    {
        var score = 0;
        if (HasNonEmptyString(element, "genre")) score += 2;
        if (HasNonEmptyString(element, "usertags") || HasNonEmptyString(element, "tagdisplay")) score += 3;
        if (element.TryGetProperty("moods", out var moods) && moods.ValueKind == JsonValueKind.Array) score += 2;
        if (element.TryGetProperty("subgenres", out var subgenres) && subgenres.ValueKind == JsonValueKind.Array) score += 2;
        if (HasNonEmptyString(element, "mood")) score += 1;
        if (HasNonEmptyString(element, "artist")) score += 1;
        return score;
    }

    private static bool HasNonEmptyString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString());

    private static JsonElement? FindObjectAtAnchor(string nextData, string anchor, string skipPrefix)
    {
        var index = nextData.IndexOf(anchor, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        return ParseObjectAt(nextData, index + skipPrefix.Length);
    }

    private static JsonElement? ParseObjectAt(string nextData, int start)
    {
        if (start < 0 || start >= nextData.Length || nextData[start] != '{')
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < nextData.Length; i++)
        {
            var ch = nextData[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    try
                    {
                        return JsonSerializer.Deserialize<JsonElement>(nextData[start..(i + 1)]);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }
            }
        }

        return null;
    }

    private static int FindObjectByTitleAnchor(string nextData)
    {
        // Fallback: any object whose first keys look like a song entity.
        var match = Regex.Match(nextData, @"\{""id"":""?\d+""?,""title""");
        return match.Success ? match.Index : -1;
    }

    private static bool IsMusicRecording(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("@type", out var type))
        {
            return false;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return string.Equals(type.GetString(), "MusicRecording", StringComparison.OrdinalIgnoreCase);
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in type.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && string.Equals(item.GetString(), "MusicRecording", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? GetJsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadJsonLdArtist(JsonElement root)
    {
        if (!root.TryGetProperty("byArtist", out var byArtist))
        {
            return GetJsonString(root, "artist");
        }

        if (byArtist.ValueKind == JsonValueKind.Object)
        {
            return GetJsonString(byArtist, "name");
        }

        if (byArtist.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in byArtist.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var name = GetJsonString(item, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
            }
        }

        return null;
    }

    private static void TrySaveSanitizedPayload(string path, string payload)
    {
        try
        {
            var sanitized = Regex.Replace(
                payload,
                @"""(cookie|session|authorization|oauth_signature|oauth_token|csrf[^""]*)""\s*:\s*""[^""]*""",
                @"""$1"":""[redacted]""",
                RegexOptions.IgnoreCase);
            File.WriteAllText(path, sanitized);
        }
        catch
        {
            // Diagnostics only: never let the dump break Vibe.
        }
    }
}
