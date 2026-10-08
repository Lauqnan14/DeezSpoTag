using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download;

public static class DownloadSourceCatalog
{
    public const string Auto = "auto";
    public const string Custom = "custom";

    private static readonly DownloadSourceOption[] EngineOptions =
    [
        new(DownloadTagSourceHelper.AmazonSource, "Amazon Music"),
        new(DownloadTagSourceHelper.AppleSource, "Apple Music"),
        new(DownloadTagSourceHelper.DeezerSource, "Deezer"),
        new(DownloadTagSourceHelper.QobuzSource, "Qobuz"),
        new("soundcloud", "SoundCloud"),
        new("soulseek", "Soulseek"),
        new(DownloadTagSourceHelper.TidalSource, "Tidal")
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

    /// <summary>
    ///     The alternative spellings an engine may arrive under, mapped to its canonical id.
    /// </summary>
    /// <remarks>
    ///     This lives here rather than in a caller because the catalog decides which values are valid. An
    ///     alias that only one caller understood would be accepted by that caller and rejected by every
    ///     validation path, so a user who stored <c>slskd</c> would silently fall back to auto.
    /// </remarks>
    private static readonly Dictionary<string, string> EngineAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["applemusic"] = DownloadTagSourceHelper.AppleSource,
        ["apple-music"] = DownloadTagSourceHelper.AppleSource,
        ["apple_music"] = DownloadTagSourceHelper.AppleSource,
        ["amazonmusic"] = DownloadTagSourceHelper.AmazonSource,
        ["amazon-music"] = DownloadTagSourceHelper.AmazonSource,
        ["amazon_music"] = DownloadTagSourceHelper.AmazonSource,
        ["soulseeknt"] = "soulseek",
        ["soulseek-net"] = "soulseek",
        ["soulseek_net"] = "soulseek",
        ["slskd"] = "soulseek"
    };

    /// <summary>
    ///     Resolves any known spelling of an engine to its canonical id, or <see langword="null"/> when the
    ///     value does not name an engine.
    /// </summary>
    public static string? NormalizeEngineName(string? value)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            return null;
        }

        if (EngineAliases.TryGetValue(normalized, out var canonical))
        {
            return canonical;
        }

        var isKnown = EngineOptions.Any(option => string.Equals(option.Value, normalized, StringComparison.Ordinal));
        return isKnown ? normalized : null;
    }

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
