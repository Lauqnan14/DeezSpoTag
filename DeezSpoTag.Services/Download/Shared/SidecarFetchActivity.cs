namespace DeezSpoTag.Services.Download.Shared;

public readonly record struct SidecarFetchWork(
    bool AlbumArtwork,
    bool AnimatedArtwork,
    bool ArtistArtwork,
    bool Lyrics);

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

        return parts.Count switch
        {
            0 => null,
            1 => $"Fetching {parts[0]}",
            _ => $"Fetching {string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"
        };
    }
}
