using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Download;

namespace DeezSpoTag.Web.Services;

public static class WatchlistPreferenceNormalizer
{
    private const string ExplicitField = "explicit";

    public static string? IncomingId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string? IncomingText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string? SpotifyId(string? value)
    {
        var normalized = IncomingId(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.ToLowerInvariant();
    }

    public static string? PreferredEngine(string? value)
    {
        return DownloadSourceCatalog.NormalizeSourcePolicy(value);
    }

    public static string? DownloadVariantMode(string? value)
    {
        var normalized = IncomingText(value)?.ToLowerInvariant();
        return normalized switch
        {
            "dual_quality" or "atmos_only" => normalized,
            "standard" => "standard",
            _ => null
        };
    }

    public static string TopSongsSyncMode(string? value)
    {
        var normalized = IncomingText(value)?.ToLowerInvariant();
        return normalized == "append" ? "append" : "mirror";
    }

    public static string? SyncMode(string? value)
    {
        var normalized = IncomingText(value)?.ToLowerInvariant();
        return normalized switch
        {
            "append" or "mirror" => normalized,
            _ => null
        };
    }

    public static string PlaylistSource(string? source)
    {
        var normalized = source?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "smarttracks" => "smarttracklist",
            "recommendation" => "recommendations",
            "itunes" => "apple",
            "applemusic" => "apple",
            _ => string.IsNullOrWhiteSpace(normalized) ? "deezer" : normalized
        };
    }

    public static List<PlaylistTrackRoutingRule>? RoutingRules(IReadOnlyList<PlaylistTrackRoutingRule>? rules)
    {
        if (rules == null || rules.Count == 0)
        {
            return null;
        }

        return rules
            .Where(static rule => rule.DestinationFolderId > 0)
            .Select(static (rule, index) =>
            {
                var normalized = NormalizeRule(rule.ConditionField, rule.ConditionOperator, rule.ConditionValue);
                return rule with
                {
                    ConditionField = normalized.Field,
                    ConditionOperator = normalized.Operator,
                    ConditionValue = normalized.Value,
                    Order = index
                };
            })
            .Where(static rule => IsUsableRule(rule.ConditionField, rule.ConditionValue))
            .ToList();
    }

    public static List<PlaylistTrackBlockRule>? BlockRules(IReadOnlyList<PlaylistTrackBlockRule>? rules)
    {
        if (rules == null || rules.Count == 0)
        {
            return null;
        }

        return rules
            .Select(static (rule, index) =>
            {
                var normalized = NormalizeRule(rule.ConditionField, rule.ConditionOperator, rule.ConditionValue);
                return rule with
                {
                    ConditionField = normalized.Field,
                    ConditionOperator = normalized.Operator,
                    ConditionValue = normalized.Value,
                    Order = index
                };
            })
            .Where(static rule => IsUsableRule(rule.ConditionField, rule.ConditionValue))
            .ToList();
    }

    private static NormalizedRule NormalizeRule(string? fieldValue, string? operatorValue, string? conditionValue)
    {
        var field = RoutingField(fieldValue);
        return new NormalizedRule(
            field,
            RoutingOperator(field, operatorValue),
            conditionValue?.Trim() ?? string.Empty);
    }

    private static bool IsUsableRule(string? field, string? value)
        => !string.IsNullOrWhiteSpace(field)
            && (string.Equals(field, ExplicitField, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(value));

    private static string RoutingField(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "artist" or "title" or "album" or "genre" or "year" or ExplicitField => normalized,
            _ => string.Empty
        };
    }

    private static string RoutingOperator(string? field, string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.Equals(field, ExplicitField, StringComparison.OrdinalIgnoreCase))
        {
            return normalized == "is_false" ? "is_false" : "is_true";
        }

        if (string.Equals(field, "year", StringComparison.OrdinalIgnoreCase))
        {
            return normalized is "gte" or "lte" ? normalized : "equals";
        }

        return normalized is "equals" or "starts_with" ? normalized : "contains";
    }

    private readonly record struct NormalizedRule(string Field, string Operator, string Value);
}
