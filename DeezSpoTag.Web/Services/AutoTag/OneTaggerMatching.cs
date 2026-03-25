using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services.AutoTag;

internal static class OneTaggerMatching
{
    public sealed record TrackSelectors<T>(
        Func<T, string> GetTitle,
        Func<T, string?> GetVersion,
        Func<T, List<string>> GetArtists,
        Func<T, TimeSpan?> GetDuration,
        Func<T, DateTime?> GetReleaseDate);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly string[] AttributesToRemove =
    [
        "(intro)", "(clean)", "(intro clean)", "(dirty)", "(intro dirty)", "(clean extended)",
        "(intro outro)", "(extended)", "(instrumental)", "(quick hit)", "(club version)", "(radio version)", "(club)", "(radio)", "(main)",
        "(radio edit)", "(ck cut)", "(super cut)", "(mega cutz)", "(snip hitz)", "(jd live cut)", "(djcity intro)", "(vdj jd edit)"
    ];

    private static readonly Regex LeadingArticles = CreateRegex(@"^(?:a|an|the)\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OriginalMix = CreateRegex(@"((\(|\[)*)original( (mix|version|edit))*((\)|\])*)$", RegexOptions.Compiled);
    private static readonly Regex FeatRegex = CreateRegex(@"(\(|\[)?(feat|ft)\.? .+?(\)|\]|\(|$)", RegexOptions.Compiled);
    private static readonly Regex TemplateVarRegex = CreateRegex("%[a-zA-Z0-9 ]+%", RegexOptions.Compiled);
    private static Regex CreateRegex(string pattern, RegexOptions options)
        => new(pattern, options, RegexTimeout);

    public static Regex? ParseFilenameTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var reserved = ".?+*$^()[]/|";
        var escaped = template;
        foreach (var c in reserved)
        {
            escaped = escaped.Replace(c.ToString(), $"\\{c}", StringComparison.Ordinal);
        }

        escaped = escaped
            .Replace("%title%", "(?P<title>.+)", StringComparison.OrdinalIgnoreCase)
            .Replace("%artist%", "(?P<artists>.+)", StringComparison.OrdinalIgnoreCase)
            .Replace("%artists%", "(?P<artists>.+)", StringComparison.OrdinalIgnoreCase);

        escaped = TemplateVarRegex.Replace(escaped, "(.+)");
        escaped = $"{escaped}\\.[a-zA-Z0-9]{{2,4}}$";

        try
        {
            return new Regex(escaped, RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }

    public static string CleanTitle(string input)
    {
        var step = CleanTitleStep1(input);
        step = CleanTitleStep2(step);
        step = CleanTitleStep3(step);
        step = CleanTitleStep4(step);
        step = CleanTitleStep1(step);
        step = CleanTitleStep5(step);
        return CleanTitleStep1(step);
    }

    public static string CleanTitleMatching(string input)
    {
        var step = CleanTitle(input);
        step = CleanTitleStep6(step);
        return CleanTitleStep7(step);
    }

    public static string CleanArtistSearching(string input)
    {
        var step = CleanTitleStep1(input.ToLowerInvariant());
        step = CleanTitleStep5(step);
        return step.Trim();
    }

    public static List<string> CleanArtists(IEnumerable<string> artists)
    {
        var cleaned = artists
            .Select(a => RemoveSpecial(a.ToLowerInvariant()).Trim())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
        return cleaned;
    }

    public static bool MatchArtist(List<string> a, List<string> b, double strictness)
    {
        var cleanA = CleanArtists(a);
        var cleanB = CleanArtists(b);

        if (cleanA.Any(cleanB.Contains))
        {
            return true;
        }

        var cleanAJoined = string.Join(" ", cleanA);
        if (cleanB.Any(artist => cleanAJoined.Contains(artist, StringComparison.Ordinal)))
        {
            return true;
        }

        var cleanBJoined = string.Join(" ", cleanB);
        if (cleanA.Any(artist => cleanBJoined.Contains(artist, StringComparison.Ordinal)))
        {
            return true;
        }

        var acc = NormalizedLevenshtein(string.Join(" ", cleanA), string.Join(", ", cleanB));
        return acc >= strictness;
    }

    public static bool MatchDuration(int? infoDurationSeconds, TimeSpan? trackDuration, AutoTagMatchingConfig config)
    {
        if (!config.MatchDuration || infoDurationSeconds is null)
        {
            return true;
        }

        var infoDuration = infoDurationSeconds.Value;
        if (infoDuration <= 0 || trackDuration is null || trackDuration.Value == TimeSpan.Zero)
        {
            return true;
        }

        var diff = Math.Abs(infoDuration - (int)trackDuration.Value.TotalSeconds);
        return diff <= config.MaxDurationDifferenceSeconds;
    }

    public static string FullTitle(string title, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return title;
        }

        var trimmed = version.Trim();
        return trimmed.Length == 0 ? title : $"{title} ({trimmed})";
    }

    public static MatchSelection<T>? MatchTrack<T>(
        AutoTagAudioInfo info,
        IReadOnlyList<T> tracks,
        AutoTagMatchingConfig config,
        TrackSelectors<T> selectors,
        bool matchArtist = true)
    {
        var requireArtistMatch = matchArtist && info.Artists.Any(a => !string.IsNullOrWhiteSpace(a));
        var exact = MatchTrackExactFallback(info, tracks, config, selectors, requireArtistMatch);
        if (exact != null)
        {
            return exact;
        }

        var cleanTitle = CleanTitleMatching(info.Title);
        var fuzzy = new List<(double Score, T Track)>();

        foreach (var track in tracks)
        {
            if (requireArtistMatch && !MatchArtist(info.Artists, selectors.GetArtists(track), config.Strictness))
            {
                continue;
            }

            var trackTitle = FullTitle(selectors.GetTitle(track), selectors.GetVersion(track));
            var clean = CleanTitleMatching(trackTitle);
            var score = NormalizedLevenshtein(clean, cleanTitle);
            if (score >= config.Strictness)
            {
                fuzzy.Add((score, track));
            }
        }

        if (fuzzy.Count == 0)
        {
            return null;
        }

        fuzzy.Sort((a, b) => b.Score.CompareTo(a.Score));
        var bestScore = fuzzy[0].Score;
        var top = fuzzy.Where(item => item.Score >= bestScore).ToList();
        SortTracks(top, config.MultipleMatches, selectors.GetReleaseDate);

        return new MatchSelection<T>(top[0].Score, top[0].Track);
    }

    private static MatchSelection<T>? MatchTrackExactFallback<T>(
        AutoTagAudioInfo info,
        IReadOnlyList<T> tracks,
        AutoTagMatchingConfig config,
        TrackSelectors<T> selectors,
        bool requireArtistMatch)
    {
        var steps = new Func<string, string>[]
        {
            CleanTitleStep1,
            CleanTitleStep2,
            CleanTitleStep3,
            CleanTitleStep4,
            CleanTitleStep5,
            CleanTitleStep6,
            CleanTitleStep7
        };

        string ApplySteps(string input, int count)
        {
            var output = input;
            for (var i = 0; i < count; i++)
            {
                output = steps[i](output);
            }
            return output;
        }

        for (var stepCount = 0; stepCount < steps.Length; stepCount++)
        {
            var cleanTitle = ApplySteps(info.Title, stepCount);
            foreach (var track in tracks)
            {
                if (!MatchDuration(info.DurationSeconds, selectors.GetDuration(track), config))
                {
                    continue;
                }

                var trackTitle = FullTitle(selectors.GetTitle(track), selectors.GetVersion(track));
                if (!string.Equals(cleanTitle, ApplySteps(trackTitle, stepCount), StringComparison.Ordinal))
                {
                    continue;
                }

                if (requireArtistMatch && !MatchArtist(info.Artists, selectors.GetArtists(track), config.Strictness))
                {
                    continue;
                }

                return new MatchSelection<T>(1.0, track);
            }
        }

        return null;
    }

    private static void SortTracks<T>(
        List<(double Score, T Track)> tracks,
        MultipleMatchesSort sort,
        Func<T, DateTime?> getReleaseDate)
    {
        if (sort == MultipleMatchesSort.Default)
        {
            return;
        }

        tracks.Sort((a, b) =>
        {
            var aDate = getReleaseDate(a.Track);
            var bDate = getReleaseDate(b.Track);
            if (aDate is null || bDate is null)
            {
                return 0;
            }

            return sort == MultipleMatchesSort.Oldest
                ? DateTime.Compare(aDate.Value, bDate.Value)
                : DateTime.Compare(bDate.Value, aDate.Value);
        });
    }

    private static string CleanTitleStep1(string input)
    {
        var output = input.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal);
        while (output.Contains("  ", StringComparison.Ordinal))
        {
            output = output.Replace("  ", " ", StringComparison.Ordinal);
        }
        return output.Trim();
    }

    private static string CleanTitleStep2(string input)
    {
        return LeadingArticles.Replace(input, "");
    }

    private static string CleanTitleStep3(string input)
    {
        return OriginalMix.Replace(input, "");
    }

    private static string CleanTitleStep4(string input)
    {
        var output = input;
        foreach (var item in AttributesToRemove)
        {
            output = output.Replace(item, "", StringComparison.Ordinal);
        }
        return output;
    }

    private static string CleanTitleStep5(string input)
    {
        return FeatRegex.Replace(input, "");
    }

    private static string CleanTitleStep6(string input)
    {
        return input.Replace("edit", "", StringComparison.Ordinal);
    }

    private static string CleanTitleStep7(string input)
    {
        return RemoveSpecial(input);
    }

    private static string RemoveSpecial(string input)
    {
        var special = ".,()[]&_\"'-/\\^";
        var output = input;
        foreach (var c in special)
        {
            output = output.Replace(c.ToString(), "", StringComparison.Ordinal);
        }
        output = output.Replace("  ", " ", StringComparison.Ordinal);
        output = RemoveDiacritics(output.Trim());
        return output;
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var ch in normalized.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(ch);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static double NormalizedLevenshtein(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0;
        }
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }
        var distance = LevenshteinDistance(a, b);
        var max = Math.Max(a.Length, b.Length);
        return max == 0 ? 0 : 1.0 - (double)distance / max;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }
        return dp[a.Length, b.Length];
    }

    internal sealed record MatchSelection<T>(double Accuracy, T Track);
}

public enum MultipleMatchesSort
{
    Default,
    Oldest,
    Newest
}

public sealed class MultipleMatchesSortConverter : JsonConverter<MultipleMatchesSort>
{
    public override MultipleMatchesSort Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric))
        {
            return Enum.IsDefined(typeof(MultipleMatchesSort), numeric)
                ? (MultipleMatchesSort)numeric
                : MultipleMatchesSort.Default;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString() ?? string.Empty;
            if (Enum.TryParse<MultipleMatchesSort>(raw, true, out var parsed))
            {
                return parsed;
            }
        }

        return MultipleMatchesSort.Default;
    }

    public override void Write(Utf8JsonWriter writer, MultipleMatchesSort value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}
