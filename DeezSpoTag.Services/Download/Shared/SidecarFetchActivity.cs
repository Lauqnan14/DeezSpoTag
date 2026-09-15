namespace DeezSpoTag.Services.Download.Shared;

public readonly record struct SidecarFetchWork(
    bool AlbumArtwork,
    bool AnimatedArtwork,
    bool ArtistArtwork,
    bool Lyrics);

[Flags]
public enum LyricsSidecarWorkKind
{
    None = 0,
    FetchMissing = 1,
    UpgradeLrcToWord = 2,
    RewriteTtmlToWord = 4,
    RemoveLineSyncedTtml = 8,
    FetchUnsyncedTxt = 16
}

public static class SidecarFetchActivity
{
    public static string? Describe(SidecarFetchWork work)
    {
        var parts = new List<string>();
        if (work.AlbumArtwork)
        {
            parts.Add("album artwork");
        }

        if (work.AnimatedArtwork)
        {
            parts.Add("animated artwork");
        }

        if (work.ArtistArtwork)
        {
            parts.Add("artist artwork");
        }

        if (work.Lyrics)
        {
            parts.Add("lyrics");
        }

        return JoinFetching(parts);
    }

    public static string? DescribeLyricsWork(LyricsSidecarWorkKind work)
    {
        if (work == LyricsSidecarWorkKind.None)
        {
            return null;
        }

        var updatingLyrics = work.HasFlag(LyricsSidecarWorkKind.UpgradeLrcToWord);
        var updatingTtml = work.HasFlag(LyricsSidecarWorkKind.RewriteTtmlToWord);
        var removingTtml = work.HasFlag(LyricsSidecarWorkKind.RemoveLineSyncedTtml);
        var fetching = work.HasFlag(LyricsSidecarWorkKind.FetchMissing)
            || work.HasFlag(LyricsSidecarWorkKind.FetchUnsyncedTxt);

        if (updatingLyrics && updatingTtml)
        {
            return "Updating lyrics and TTML";
        }

        if (updatingLyrics)
        {
            return "Updating lyrics";
        }

        if (updatingTtml && fetching)
        {
            return "Fetching lyrics and updating TTML";
        }

        if (updatingTtml)
        {
            return "Updating TTML";
        }

        if (removingTtml)
        {
            return "Removing line-synced TTML";
        }

        return fetching ? "Fetching lyrics" : null;
    }

    public static string? DescribeEnhancement(
        bool albumArtwork,
        bool animatedArtwork,
        LyricsSidecarWorkKind lyricsWork)
    {
        var lyricsPhrase = DescribeLyricsWork(lyricsWork);
        var artworkParts = new List<string>();
        if (albumArtwork)
        {
            artworkParts.Add("album artwork");
        }

        if (animatedArtwork)
        {
            artworkParts.Add("animated artwork");
        }

        if (artworkParts.Count == 0)
        {
            return lyricsPhrase;
        }

        if (lyricsPhrase is null)
        {
            return JoinFetching(artworkParts);
        }

        if (string.Equals(lyricsPhrase, "Fetching lyrics", StringComparison.Ordinal))
        {
            artworkParts.Add("lyrics");
            return JoinFetching(artworkParts);
        }

        var tail = char.ToLowerInvariant(lyricsPhrase[0]) + lyricsPhrase[1..];
        return artworkParts.Count == 1
            ? $"Fetching {artworkParts[0]} and {tail}"
            : $"Fetching {string.Join(", ", artworkParts)} and {tail}";
    }

    private static string? JoinFetching(IReadOnlyList<string> parts)
        => parts.Count switch
        {
            0 => null,
            1 => $"Fetching {parts[0]}",
            _ => $"Fetching {string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"
        };
}
