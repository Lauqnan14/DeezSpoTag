using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Utils;

/// <summary>
/// Core utilities ported from deezspotag core.ts
/// </summary>
public static class CoreUtils
{
    public const string UserAgentHeader = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Change text case (port of changeCase function)
    /// </summary>
    public static string ChangeCase(string text, string caseType)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return caseType.ToLower() switch
        {
            "lower" => text.ToLower(),
            "upper" => text.ToUpper(),
            "start" => ToTitleCase(text),
            "sentence" => ToSentenceCase(text),
            _ => text
        };
    }

    /// <summary>
    /// Convert to title case (each word capitalized)
    /// </summary>
    private static string ToTitleCase(string text)
    {
        var words = text.Trim().Split(' ');
        var brackets = new[] { "(", "{", "[", "'", "\"" };

        for (int i = 0; i < words.Length; i++)
        {
            if (string.IsNullOrEmpty(words[i]))
                continue;

            if (words[i].Length > 1 && brackets.Any(b => words[i].StartsWith(b)))
            {
                // Handle words starting with brackets
                words[i] = words[i][0] + char.ToUpper(words[i][1]) + words[i][2..].ToLower();
            }
            else if (words[i].Length > 1)
            {
                words[i] = char.ToUpper(words[i][0]) + words[i][1..].ToLower();
            }
            else
            {
                words[i] = words[i].ToUpper();
            }
        }

        return string.Join(" ", words);
    }

    /// <summary>
    /// Convert to sentence case (first letter capitalized)
    /// </summary>
    private static string ToSentenceCase(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return char.ToUpper(text[0]) + text[1..].ToLower();
    }

    /// <summary>
    /// Remove featuring artists from title (port of removeFeatures function)
    /// </summary>
    public static string RemoveFeatures(string title)
    {
        if (string.IsNullOrEmpty(title))
            return title;

        var clean = title;
        var found = false;
        var pos = -1;

        // Look for "feat." or "ft." patterns
        var featMatch = Regex.Match(clean, @"[\s(]\(?s?feat\.?\s", RegexOptions.IgnoreCase, RegexTimeout);
        var ftMatch = Regex.Match(clean, @"[\s(]\(?s?ft\.?\s", RegexOptions.IgnoreCase, RegexTimeout);

        if (featMatch.Success)
        {
            pos = featMatch.Index;
            found = true;
        }
        else if (ftMatch.Success)
        {
            pos = ftMatch.Index;
            found = true;
        }

        if (!found)
            return clean;

        var openBracket = pos < clean.Length && (clean[pos] == '(' || (pos + 1 < clean.Length && clean[pos + 1] == '('));
        var otherBracket = clean.IndexOf('(', pos + 2);

        var tempTrack = clean[..pos];

        if (clean.Contains(')') && openBracket)
        {
            var closingPos = clean.IndexOf(')', pos + 2);
            if (closingPos != -1)
            {
                tempTrack += clean[(closingPos + 1)..];
            }
        }

        if (!openBracket && otherBracket != -1)
        {
            tempTrack += " " + clean[otherBracket..];
        }

        clean = tempTrack.Trim();
        // Remove extra spaces
        clean = Regex.Replace(clean, @"\s\s+", " ", RegexOptions.None, RegexTimeout);

        return clean;
    }

    /// <summary>
    /// Join list with commas and "&" (port of andCommaConcat function)
    /// </summary>
    public static string AndCommaConcat(IList<string> list)
    {
        if (list == null || list.Count == 0)
            return string.Empty;

        if (list.Count == 1)
            return list[0];

        if (list.Count == 2)
            return $"{list[0]} & {list[1]}";

        var result = string.Join(", ", list.Take(list.Count - 1));
        result += $" & {list[^1]}";

        return result;
    }

    /// <summary>
    /// Remove duplicate entries from array (port of uniqueArray function)
    /// </summary>
    public static List<string> UniqueArray(List<string> arr)
    {
        if (arr == null || arr.Count == 0)
            return arr ?? new List<string>();

        var result = new List<string>();

        foreach (var item in arr)
        {
            var isDuplicate = result.Any(existing => 
                existing.Equals(item, StringComparison.OrdinalIgnoreCase) ||
                existing.Contains(item, StringComparison.OrdinalIgnoreCase) ||
                item.Contains(existing, StringComparison.OrdinalIgnoreCase));

            if (!isDuplicate)
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// Generate replay gain string (port of generateReplayGainString function)
    /// </summary>
    public static string GenerateReplayGainString(double trackGain)
    {
        var value = Math.Round((trackGain + 18.4) * -100) / 100;
        return $"{value:F2} dB";
    }

    /// <summary>
    /// Escape shell command (port of shellEscape function)
    /// </summary>
    public static string ShellEscape(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        // Check if string contains only safe characters
        if (!Regex.IsMatch(s, @"[^\w@%+=:,./-]", RegexOptions.None, RegexTimeout))
            return s;

        // Escape single quotes and wrap in single quotes
        return "'" + s.Replace("'", "'\"'\"'") + "'";
    }

    /// <summary>
    /// Remove duplicate artists (port of removeDuplicateArtists function)
    /// </summary>
    public static (Dictionary<string, List<string>> artists, List<string> artistList) RemoveDuplicateArtists(
        Dictionary<string, List<string>> artists, 
        List<string> artistList)
    {
        // Clean up artist list
        artistList = UniqueArray(artistList);

        // Clean up artists by role
        var cleanedArtists = new Dictionary<string, List<string>>();
        foreach (var kvp in artists)
        {
            cleanedArtists[kvp.Key] = UniqueArray(kvp.Value);
        }

        return (cleanedArtists, artistList);
    }

    /// <summary>
    /// Check if path is writable
    /// </summary>
    public static bool CanWrite(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return false;

            // Try to create a temporary file to test write access
            var tempFile = Path.Join(path, Path.GetRandomFileName());
            File.WriteAllText(tempFile, "test");
            File.Delete(tempFile);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return false;
        }
    }

    /// <summary>
    /// Format listener messages (port of formatListener function)
    /// </summary>
    public static string FormatListener(string key, object data)
    {
        // This is a simplified version - the full implementation would need
        // proper data structure definitions for each message type
        return key switch
        {
            "startAddingArtist" => $"Started gathering artist's albums",
            "finishAddingArtist" => $"Finished gathering artist's albums", 
            "updateQueue" => "Queue updated",
            "downloadInfo" => "Download info",
            "downloadWarn" => "Download warning",
            "currentItemCancelled" => "Current item cancelled",
            "removedFromQueue" => "Removed from queue",
            "finishDownload" => "Download complete",
            "startConversion" => "Started converting",
            "finishConversion" => "Conversion complete",
            _ => key
        };
    }

    /// <summary>
    /// Clean filename for cross-platform compatibility
    /// </summary>
    public static string CleanFilename(string filename, string replacement = "_")
    {
        var cleaned = CjkFilenameSanitizer.SanitizeSegment(
            filename,
            fallback: "unknown",
            replacement: replacement,
            collapseWhitespace: true,
            trimTrailingDotsAndSpaces: true,
            maxLength: 200);

        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }

    /// <summary>
    /// Normalize text for consistent comparison
    /// </summary>
    public static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text.Normalize(NormalizationForm.FormC)
                  .Trim()
                  .ToLowerInvariant();
    }

    /// <summary>
    /// Calculate similarity between two strings (for fallback matching)
    /// </summary>
    public static double CalculateSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return 0.0;

        var normalizedA = NormalizeText(a);
        var normalizedB = NormalizeText(b);

        if (normalizedA == normalizedB)
            return 1.0;

        // Simple Levenshtein distance calculation
        var matrix = new int[normalizedA.Length + 1, normalizedB.Length + 1];

        for (int i = 0; i <= normalizedA.Length; i++)
            matrix[i, 0] = i;

        for (int j = 0; j <= normalizedB.Length; j++)
            matrix[0, j] = j;

        for (int i = 1; i <= normalizedA.Length; i++)
        {
            for (int j = 1; j <= normalizedB.Length; j++)
            {
                var cost = normalizedA[i - 1] == normalizedB[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        var maxLength = Math.Max(normalizedA.Length, normalizedB.Length);
        return 1.0 - (double)matrix[normalizedA.Length, normalizedB.Length] / maxLength;
    }
}
