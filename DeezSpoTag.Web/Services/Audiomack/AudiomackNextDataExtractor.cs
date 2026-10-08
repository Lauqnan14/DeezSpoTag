using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>
/// Extracts Audiomack's Next.js v13 page payload from a public track page and
/// locates the song object inside it. Used to confirm the real mood/subgenre
/// schema and, when AUDIOMACK_DEBUG_PAYLOAD=1, to dump a sanitized copy under
/// the application data root's debug directory. Never logs or saves cookies,
/// sessions or authorization values.
/// </summary>
public static class AudiomackNextDataExtractor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Fetches the public track page and returns the embedded song object as a
    /// JsonElement, plus the page payload for diagnostics. The URL comes only
    /// from the coherent expected identity; a mismatched or unverifiable page
    /// yields no object instead of an unrelated song.
    /// </summary>
    public static async Task<JsonElement?> FetchSongObjectAsync(
        IHttpClientFactory httpClientFactory,
        AudiomackSongCandidate expected,
        string? debugOutputPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!AudiomackIdNormalizer.TryGetSongIdentity(expected, out _, out var canonicalPath, out _))
        {
            return null;
        }

        var url = expected.Url;
        if (string.IsNullOrWhiteSpace(url) || !AudiomackIdNormalizer.TryGetCanonicalSongPath(url, out _))
        {
            if (canonicalPath is null)
            {
                return null;
            }

            var parts = canonicalPath.Split('/', 2);
            url = $"https://audiomack.com/{parts[0]}/song/{parts[1]}";
        }

        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
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

        return FindSongObject(payload, expected) ?? FindJsonLdSong(html, expected);
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
            var closed = false;
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
                            closed = true;
                            try
                            {
                                var parsed = JsonSerializer.Deserialize<JsonElement[]>(html[i..(j + 1)]);
                                if (parsed is not null)
                                {
                                    foreach (var part in parsed
                                        .Where(part => part.ValueKind == JsonValueKind.String))
                                    {
                                        data.Append(part.GetString());
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

            if (!closed)
            {
                // Unterminated array: never let the scan position sit still, and
                // never accept a partial object as an overlay.
                break;
            }
        }

        return data.ToString();
    }

    /// <summary>
    /// Selects only objects whose identity verifies against the expected
    /// recording. Multiple representations of the same verified song merge;
    /// verified objects that disagree with each other reject the page overlay
    /// instead of unioning foreign metadata into one candidate.
    /// </summary>
    public static JsonElement? FindSongObject(string nextData, AudiomackSongCandidate expected)
    {
        if (string.IsNullOrEmpty(nextData))
        {
            return null;
        }

        var verified = new List<JsonElement>();
        CollectVerified(nextData, expected, verified.Add);
        CollectTypeSongObjects(nextData, expected, verified);
        CollectTitleAnchorObject(nextData, expected, verified);

        if (verified.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < verified.Count; i++)
        {
            for (var j = i + 1; j < verified.Count; j++)
            {
                var a = AudiomackSongCandidate.FromJson(verified[i]);
                var b = AudiomackSongCandidate.FromJson(verified[j]);
                if (a is not null && b is not null
                    && !AudiomackIdNormalizer.IsSameSong(a, b, out _))
                {
                    // Ambiguous page overlay: do not salvage pieces of it.
                    return null;
                }
            }
        }

        if (verified.Count == 1)
        {
            return verified[0];
        }

        var merged = JsonNode.Parse(verified[0].GetRawText()) ?? new JsonObject();
        for (var i = 1; i < verified.Count; i++)
        {
            merged = MergeSongNodes(merged, JsonNode.Parse(verified[i].GetRawText())) ?? merged;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(merged.ToJsonString());
        }
        catch (JsonException)
        {
            return verified[0];
        }
    }

    private static void CollectVerified(
        string nextData,
        AudiomackSongCandidate expected,
        Action<JsonElement> onVerified)
    {
        const string anchor = "\"song\":{\"id\"";
        var pos = 0;
        while (pos < nextData.Length)
        {
            var index = nextData.IndexOf(anchor, pos, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            pos = index + anchor.Length;
            var candidate = ParseObjectAt(nextData, index + "\"song\":".Length);
            if (candidate is JsonElement element && Verifies(element, expected))
            {
                onVerified(element);
            }
        }
    }

    private static bool Verifies(JsonElement element, AudiomackSongCandidate expected)
    {
        var candidate = AudiomackSongCandidate.FromJson(element);
        return candidate is not null
            && AudiomackIdNormalizer.IsSameSong(expected, candidate, out _);
    }

    private static void CollectTypeSongObjects(string nextData, AudiomackSongCandidate expected, List<JsonElement> verified)
    {
        if (nextData.IndexOf("\"type\":\"song\"", StringComparison.Ordinal) < 0
            && nextData.IndexOf("\"type\": \"song\"", StringComparison.Ordinal) < 0)
        {
            return;
        }

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

            if (Verifies(element, expected))
            {
                verified.Add(element);
            }
        }
    }

    private static void CollectTitleAnchorObject(string nextData, AudiomackSongCandidate expected, List<JsonElement> verified)
    {
        var match = Regex.Match(nextData, @"\{""id"":""?\d+""?,""title""", RegexOptions.None, RegexTimeout);
        if (!match.Success)
        {
            return;
        }

        var parsed = ParseObjectAt(nextData, match.Index);
        if (parsed is JsonElement element && Verifies(element, expected))
        {
            verified.Add(element);
        }
    }

    /// <summary>Conservative enrichment merge: a verified duplicate may contribute fields the base object lacks.</summary>
    private static JsonNode? MergeSongNodes(JsonNode? left, JsonNode? right)
    {
        if (left is not JsonObject leftObject || right is not JsonObject rightObject)
        {
            return left?.DeepClone();
        }

        var merged = leftObject.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var property in rightObject)
        {
            if (property.Value is JsonArray rightArray && merged[property.Key] is JsonArray leftArray)
            {
                var seen = new HashSet<string>(leftArray.Select(item => item?.ToJsonString()));
                foreach (var item in rightArray
                    .Where(item => item is not null && seen.Add(item.ToJsonString())))
                {
                    leftArray.Add(item!.DeepClone());
                }
            }
            else if (!merged.TryGetPropertyValue(property.Key, out var existingNode) || IsEmptyValue(existingNode))
            {
                merged[property.Key] = property.Value?.DeepClone();
            }
        }

        return merged;
    }

    private static bool IsEmptyValue(JsonNode? node)
        => node is null
           || (node is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text))
           || (node is JsonArray array && array.Count == 0);

    private static bool IsTypeSong(JsonElement element)
        => element.TryGetProperty("type", out var type)
           && type.ValueKind == JsonValueKind.String
           && string.Equals(type.GetString(), "song", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// JSON-LD <c>MusicRecording</c> fallback when the RSC song object cannot be
    /// located. Supplies at least title/artist/genre, which is enough for AutoTag
    /// to avoid writing a junk catalog bucket.
    /// </summary>
    public static JsonElement? FindJsonLdSong(string html, AudiomackSongCandidate expected)
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

                var candidate = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload));
                if (Verifies(candidate, expected))
                {
                    return candidate;
                }
            }
            catch (JsonException)
            {
                // Malformed JSON-LD: try the next script block.
            }
        }
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
            return type.EnumerateArray()
                .Any(item => item.ValueKind == JsonValueKind.String
                    && string.Equals(item.GetString(), "MusicRecording", StringComparison.OrdinalIgnoreCase));
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
            foreach (var name in byArtist.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => GetJsonString(item, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                return name;
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
                RegexOptions.IgnoreCase,
                RegexTimeout);
            File.WriteAllText(path, sanitized);
        }
        catch
        {
            // Diagnostics only: never let the dump break Vibe.
        }
    }
}
