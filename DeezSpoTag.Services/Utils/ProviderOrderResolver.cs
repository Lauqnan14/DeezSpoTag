namespace DeezSpoTag.Services.Utils;

public static class ProviderOrderResolver
{
    public static List<string> Resolve(
        bool enabled,
        string? configuredOrder,
        IEnumerable<string> defaultOrder,
        Func<string?, string> normalizeToken)
    {
        var providers = (configuredOrder ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(normalizeToken)
            .Where(static provider => !string.IsNullOrWhiteSpace(provider))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (providers.Count == 0)
        {
            providers.AddRange(defaultOrder);
        }

        return enabled
            ? providers
            : new List<string> { providers[0] };
    }
}
