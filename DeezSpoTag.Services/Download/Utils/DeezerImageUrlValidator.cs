namespace DeezSpoTag.Services.Download.Utils;

public static class DeezerImageUrlValidator
{
    private static readonly HashSet<string> BlockedDeezerImageMd5 = new(StringComparer.OrdinalIgnoreCase)
    {
        "d41d8cd98f00b204e9800998ecf8427e",
        "522c7b1de6d02790c348da447d3fd2b7",
        "c34f636093a87af8fd7dda0a10184280"
    };

    public static bool HasUsableDeezerMd5(string? md5)
    {
        if (string.IsNullOrWhiteSpace(md5))
        {
            return false;
        }

        return !BlockedDeezerImageMd5.Contains(md5.Trim());
    }

    public static bool IsAllowedDeezerImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return true;
        }

        if (!uri.Host.Contains("dzcdn.net", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Keep empty entries: Deezer serves placeholders as ".../images/artist//500x500-...jpg",
        // and collapsing the blank segment would read the size token as the image hash.
        var segments = uri.AbsolutePath.Split('/');
        var imagesIndex = Array.FindIndex(
            segments,
            segment => segment.Equals("images", StringComparison.OrdinalIgnoreCase));
        if (imagesIndex < 0 || segments.Length < imagesIndex + 3)
        {
            return true;
        }

        return HasUsableDeezerMd5(segments[imagesIndex + 2]);
    }
}
