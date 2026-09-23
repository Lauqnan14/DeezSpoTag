namespace DeezSpoTag.Services.Download.Shared;

public static class ArtworkFormatPolicy
{
    private static readonly string[] CanonicalOrder = ["jpg", "png", "webp"];

    public static IReadOnlyList<string> Parse(string? raw)
    {
        var tokens = string.Equals(raw?.Trim(), "both", StringComparison.OrdinalIgnoreCase)
            ? new[] { "jpg", "png" }
            : (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = tokens
            .Select(static value => value.Trim().TrimStart('.').ToLowerInvariant())
            .Select(static value => value == "jpeg" ? "jpg" : value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = CanonicalOrder.Where(selected.Contains).ToArray();
        return result.Length == 0 ? ["jpg"] : result;
    }

    public static string Normalize(string? raw) => string.Join(',', Parse(raw));

    public static string Serialize(IEnumerable<string>? formats)
        => Normalize(formats == null ? null : string.Join(',', formats));

    public static string ResolveEmbeddedFormat(string? configured, string? audioExtension)
    {
        var formats = Parse(configured);
        return formats.Contains("jpg", StringComparer.OrdinalIgnoreCase) ? "jpg" : formats[0];
    }
}
