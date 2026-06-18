using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Services.Download;

public static class PlaylistTrackBlockRuleMatcher
{
    public sealed record TrackRuleMatchInput(
        string? Title,
        string? Artist,
        string? Album,
        IReadOnlyList<string>? Genres,
        bool? IsExplicit,
        string? ReleaseDate);

    private sealed record RuleCondition(
        string? Field,
        string? Operator,
        string? Value);

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
                new TrackRuleMatchInput(
                title,
                artist,
                album,
                genres,
                isExplicit,
                releaseDate),
                new RuleCondition(rule.ConditionField, rule.ConditionOperator, rule.ConditionValue)));
    }

    public static bool RuleMatches(TrackRuleMatchInput track, string? conditionField, string? conditionOperator, string? conditionValue)
        => RuleMatches(track, new RuleCondition(conditionField, conditionOperator, conditionValue));

    private static bool RuleMatches(TrackRuleMatchInput track, RuleCondition condition)
    {
        return Normalize(condition.Field) switch
        {
            "artist" => EvalStringCondition(track.Artist, condition.Operator, condition.Value),
            "title" => EvalStringCondition(track.Title, condition.Operator, condition.Value),
            "album" => EvalStringCondition(track.Album, condition.Operator, condition.Value),
            "genre" => EvalGenreCondition(track.Genres, condition.Operator, condition.Value),
            "explicit" => Normalize(condition.Operator) == "is_true" ? track.IsExplicit == true : track.IsExplicit != true,
            "year" => EvalYearCondition(track.ReleaseDate, condition.Operator, condition.Value),
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
