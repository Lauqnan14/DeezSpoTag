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

        return FindSongObject(payload);
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
        const string songAnchor = "\"song\":{\"id\"";
        var match = Regex.Match(nextData, songAnchor);
        var start = match.Success
            ? match.Index + songAnchor.Length
            : FindObjectByTitleAnchor(nextData);
        if (start < 0)
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
