using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

internal static class PlaylistTrackBlockRuleHelper
{
    public static IReadOnlyList<PlaylistTrackBlockRule> BuildGlobalRules(
        IReadOnlyList<PlaylistWatchPreferenceDto> preferences)
    {
        if (preferences.Count == 0)
        {
            return Array.Empty<PlaylistTrackBlockRule>();
        }

        var rules = new List<PlaylistTrackBlockRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in preferences.SelectMany(static preference => preference.IgnoreRules ?? []))
        {
            AppendRuleIfUnique(rule, rules, seen);
        }

        return rules;
    }

    public static List<PlaylistTrackBlockRule>? MergeRules(
        IReadOnlyList<PlaylistTrackBlockRule>? playlistRules,
        IReadOnlyList<PlaylistTrackBlockRule> globalRules)
    {
        var hasPlaylistRules = playlistRules is { Count: > 0 };
        var hasGlobalRules = globalRules.Count > 0;
        if (!hasPlaylistRules && !hasGlobalRules)
        {
            return null;
        }

        var merged = new List<PlaylistTrackBlockRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasPlaylistRules)
        {
            foreach (var rule in playlistRules!)
            {
                AppendRuleIfUnique(rule, merged, seen);
            }
        }

        if (hasGlobalRules)
        {
            foreach (var rule in globalRules)
            {
                AppendRuleIfUnique(rule, merged, seen);
            }
        }

        return merged;
    }

    private static void AppendRuleIfUnique(
        PlaylistTrackBlockRule rule,
        List<PlaylistTrackBlockRule> destination,
        HashSet<string> seen)
    {
        if (!TryNormalize(rule, out var field, out var op, out var value))
        {
            return;
        }

        var dedupeKey = $"{field}\u001F{op}\u001F{value}";
        if (!seen.Add(dedupeKey))
        {
            return;
        }

        destination.Add(new PlaylistTrackBlockRule(field, op, value, destination.Count));
    }

    private static bool TryNormalize(
        PlaylistTrackBlockRule rule,
        out string field,
        out string op,
        out string value)
    {
        field = (rule.ConditionField ?? string.Empty).Trim();
        op = (rule.ConditionOperator ?? string.Empty).Trim();
        value = (rule.ConditionValue ?? string.Empty).Trim();
        var isExplicitRule = string.Equals(field, "explicit", StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(field)
            && !string.IsNullOrWhiteSpace(op)
            && (isExplicitRule || !string.IsNullOrWhiteSpace(value));
    }
}
