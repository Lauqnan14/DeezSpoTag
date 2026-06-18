namespace DeezSpoTag.Services.Download;

public static class DownloadSourceCatalog
{
    public const string Auto = "auto";
    public const string Custom = "custom";

    private static readonly DownloadSourceOption[] EngineOptions =
    [
        new("amazon", "Amazon Music"),
        new("apple", "Apple Music"),
        new("deezer", "Deezer"),
        new("qobuz", "Qobuz"),
        new("tidal", "Tidal")
    ];

    public static IReadOnlyList<DownloadSourceOption> GetEngineOptions()
        => EngineOptions;

    public static IReadOnlyList<DownloadSourceOption> GetSettingsSourceOptions()
        =>
        [
            new(Auto, "Auto"),
            new(Custom, "Custom"),
            .. EngineOptions
        ];

    public static IReadOnlyList<DownloadSourceOption> GetWatchlistSourceOptions()
        => GetSettingsSourceOptions();

    public static bool IsEngineOrAuto(string? value)
    {
        var normalized = Normalize(value);
        return string.Equals(normalized, Auto, StringComparison.Ordinal)
            || EngineOptions.Any(option => string.Equals(option.Value, normalized, StringComparison.Ordinal));
    }

    public static bool IsSourcePolicy(string? value)
    {
        var normalized = Normalize(value);
        return string.Equals(normalized, Custom, StringComparison.Ordinal)
            || IsEngineOrAuto(normalized);
    }

    public static string? NormalizeEngineOrAuto(string? value)
    {
        var normalized = Normalize(value);
        return IsEngineOrAuto(normalized) ? normalized : null;
    }

    public static string? NormalizeSourcePolicy(string? value)
    {
        var normalized = Normalize(value);
        return IsSourcePolicy(normalized) ? normalized : null;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

public sealed record DownloadSourceOption(string Value, string Label);
