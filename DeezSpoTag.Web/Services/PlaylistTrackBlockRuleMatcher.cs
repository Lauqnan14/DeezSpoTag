using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

internal static class PlaylistTrackBlockRuleMatcher
{
    public static PlaylistTrackBlockRule? FindMatch(
        DownloadIntent intent,
        IReadOnlyList<PlaylistTrackBlockRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return null;
        }

        return rules
            .OrderBy(static rule => rule.Order)
            .FirstOrDefault(rule => RuleMatches(
                intent.Title,
                intent.Artist,
                intent.Album,
                intent.Genres,
                intent.Explicit,
                intent.ReleaseDate,
                rule.ConditionField,
                rule.ConditionOperator,
                rule.ConditionValue));
    }

    public static PlaylistTrackBlockRule? FindMatch(
        string? title,
        string? artist,
        string? album,
        IReadOnlyList<string>? genres,
        bool? isExplicit,
        string? releaseDate,
        IReadOnlyList<PlaylistTrackBlockRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return null;
        }

        return rules
            .OrderBy(static rule => rule.Order)
            .FirstOrDefault(rule => RuleMatches(
                title,
                artist,
                album,
                genres,
                isExplicit,
                releaseDate,
                rule.ConditionField,
                rule.ConditionOperator,
                rule.ConditionValue));
    }

    public static bool RuleMatches(
        string? title,
        string? artist,
        string? album,
        IReadOnlyList<string>? genres,
        bool? isExplicit,
        string? releaseDate,
        string? conditionField,
        string? conditionOperator,
        string? conditionValue)
    {
        return Normalize(conditionField) switch
        {
            "artist" => EvalStringCondition(artist, conditionOperator, conditionValue),
            "title" => EvalStringCondition(title, conditionOperator, conditionValue),
            "album" => EvalStringCondition(album, conditionOperator, conditionValue),
            "genre" => EvalGenreCondition(genres, conditionOperator, conditionValue),
            "explicit" => Normalize(conditionOperator) == "is_true" ? isExplicit == true : isExplicit != true,
            "year" => EvalYearCondition(releaseDate, conditionOperator, conditionValue),
            _ => false
        };
    }

    public static string Describe(PlaylistTrackBlockRule rule)
    {
        var field = string.IsNullOrWhiteSpace(rule.ConditionField) ? "rule" : rule.ConditionField.Trim();
        var value = string.IsNullOrWhiteSpace(rule.ConditionValue) ? rule.ConditionOperator : rule.ConditionValue.Trim();
        return string.IsNullOrWhiteSpace(value) ? field : $"{field}={value}";
    }

    private static bool EvalStringCondition(string? value, string? op, string? conditionValue)
    {
        var candidate = (value ?? string.Empty).Trim();
        var rule = (conditionValue ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(rule))
        {
            return false;
        }

        return Normalize(op) switch
        {
            "contains" => candidate.Contains(rule, StringComparison.OrdinalIgnoreCase),
            "equals" => string.Equals(candidate, rule, StringComparison.OrdinalIgnoreCase),
            "starts_with" => candidate.StartsWith(rule, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool EvalGenreCondition(IReadOnlyList<string>? genres, string? op, string? conditionValue)
    {
        if (genres is null || genres.Count == 0)
        {
            return false;
        }

        return genres
            .Where(static genre => !string.IsNullOrWhiteSpace(genre))
            .Any(genre => EvalStringCondition(genre, op, conditionValue));
    }

    private static bool EvalYearCondition(string? releaseDate, string? op, string? conditionValue)
    {
        if (!TryParseReleaseYear(releaseDate, out var trackYear)
            || !int.TryParse((conditionValue ?? string.Empty).Trim(), out var ruleYear))
        {
            return false;
        }

        return Normalize(op) switch
        {
            "gte" => trackYear >= ruleYear,
            "lte" => trackYear <= ruleYear,
            _ => trackYear == ruleYear
        };
    }

    private static bool TryParseReleaseYear(string? releaseDate, out int year)
    {
        year = 0;
        var value = (releaseDate ?? string.Empty).Trim();
        if (value.Length < 4)
        {
            return false;
        }

        return int.TryParse(value[..4], out year);
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
