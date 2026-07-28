namespace DeezSpoTag.Services.Download.Utils;

public sealed record LyricsProviderDescriptor(
    string Id,
    string DisplayName,
    bool SupportsPlain,
    bool SupportsLineSynchronized,
    bool SupportsWordSynchronized,
    bool SupportsNativeTtml,
    bool IsLyricsOnly,
    IReadOnlyList<string> Aliases);

public static class LyricsProviderRegistry
{
    public const string Apple = "apple";
    public const string Deezer = "deezer";
    public const string Spotify = "spotify";
    public const string Lrclib = "lrclib";
    public const string Musixmatch = "musixmatch";
    public const string YouLyPlus = "youlyplus";
    public const string BetterLyrics = "betterlyrics";

    private static readonly IReadOnlyList<LyricsProviderDescriptor> Providers =
    [
        new(Apple, "Apple Music", true, true, true, true, false,
            ["itunes", "applemusic", "apple-music", "apple_music", "apple music", "music.apple"]),
        new(Deezer, "Deezer", true, true, false, false, false, []),
        new(Spotify, "Spotify", true, true, false, false, false, []),
        new(Lrclib, "LRCLIB", true, true, false, false, true, ["lrcget", "lrc-get", "lrc_get"]),
        new(Musixmatch, "Musixmatch", true, true, true, false, true, []),
        new(YouLyPlus, "YouLy+", true, true, true, false, true,
            ["youly", "youly+", "youly-plus", "lyricsplus"]),
        new(BetterLyrics, "BetterLyrics", true, true, true, true, true,
            ["better-lyrics", "better_lyrics", "better lyrics"])
    ];

    private static readonly IReadOnlyDictionary<string, LyricsProviderDescriptor> ProvidersById =
        Providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<LyricsProviderDescriptor> All => Providers;

    public static IReadOnlyList<string> DefaultOrder { get; } =
        Providers.Select(provider => provider.Id).ToArray();

    public static bool IsRegistered(string? provider)
        => TryNormalize(provider, out _);

    public static bool TryGet(string? provider, out LyricsProviderDescriptor descriptor)
    {
        descriptor = null!;
        if (!TryNormalize(provider, out var normalized)
            || !ProvidersById.TryGetValue(normalized, out var found))
        {
            return false;
        }
        descriptor = found;
        return true;
    }

    public static bool TryNormalize(string? provider, out string normalized)
    {
        var candidate = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (ProvidersById.ContainsKey(candidate))
        {
            normalized = candidate;
            return true;
        }

        var descriptor = Providers.FirstOrDefault(item =>
            item.Aliases.Contains(candidate, StringComparer.OrdinalIgnoreCase));
        normalized = descriptor?.Id ?? string.Empty;
        return descriptor != null;
    }

    public static string NormalizeOrEmpty(string? provider)
        => TryNormalize(provider, out var normalized) ? normalized : string.Empty;
}
