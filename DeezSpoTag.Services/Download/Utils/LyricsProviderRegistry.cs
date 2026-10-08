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

    /// <summary>
    ///     Lyrics the Soulseek peer shipped in the same folder as the audio.
    /// </summary>
    /// <remarks>
    ///     Resolvable only from a file already fetched from the peer, and only when the reader has explicitly
    ///     asked for it. It is never consulted for a track that did not come from Soulseek, and it yields
    ///     nothing when no peer file was taken, so registering it cannot change any other download.
    /// </remarks>
    public const string Peer = "peer";

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
            ["better-lyrics", "better_lyrics", "better lyrics"]),

        // Listed last and never part of the configured default order, so it cannot be reached by accident.
        // It is only ever consulted ahead of the chain when the reader turned it on and the download actually
        // carried a peer lyrics or cue file.
        new(Peer, "Peer-supplied", true, true, false, false, false, ["peer-supplied", "soulseek"])
    ];

    private static readonly IReadOnlyDictionary<string, LyricsProviderDescriptor> ProvidersById =
        Providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<LyricsProviderDescriptor> All => Providers;

    /// <summary>
    ///     The providers a download consults when the reader has expressed no preference.
    /// </summary>
    /// <remarks>
    ///     The peer source is deliberately absent. A default-order entry would be consulted for every track in
    ///     the app, including the many that never came from a peer and so can only ever answer "nothing". It is
    ///     reached exactly once, explicitly, by a download that actually carries a peer file.
    /// </remarks>
    public static IReadOnlyList<string> DefaultOrder { get; } =
        Providers
            .Where(provider => provider.Id != Peer)
            .Select(provider => provider.Id)
            .ToArray();

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
