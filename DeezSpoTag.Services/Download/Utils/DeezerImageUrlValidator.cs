using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DeezSpoTag.Services.Download.Utils;

public static class DeezerImageUrlValidator
{
    private static readonly HashSet<string> BlockedDeezerImageMd5 = new(StringComparer.OrdinalIgnoreCase)
    {
        "d41d8cd98f00b204e9800998ecf8427e",
        "522c7b1de6d02790c348da447d3fd2b7",
        "c34f636093a87af8fd7dda0a10184280",
        "df237cf6f464c6fed6a4cc208db28aeb",
        "8ad2e6f95167c3fc4113de04cccda30f"
    };

    // 16x16 luminance signature of Deezer's grey person-outline placeholder. Matching the
    // actual pixels also catches resized/re-encoded copies with no Deezer URL provenance.
    private static readonly byte[] ArtistPlaceholder = Convert.FromBase64String(
        "6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6eXl6enp6enp6enp6enp6d7S0t7p6enp6enp6enp6enU4uLU6enp6enp6enp6enp2dbW2enp6enp6enp6enp6eXa2uXp6enp6enp6enp5djT09PT1+Xp6enp6enp6dfa5ujo5tvX6enp6enp6enU3+Tk5OTf1Onp6enp6enp3dPU1NTU093p6enp6enp6eno6Ojo6Ojp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6enp6Q==");

    public static bool IsArtistPlaceholder(Image image)
    {
        if (image.Width < 16 || image.Height < 16 || Math.Abs(image.Width / (double)image.Height - 1) > 0.01)
            return false;
        using var sample = image.CloneAs<Rgba32>();
        sample.Mutate(x => x.Resize(16, 16, KnownResamplers.Triangle));
        double error = 0;
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
        {
            var pixel = sample[x, y];
            if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) > 12)
                return false;
            var luminance = pixel.R * 0.299 + pixel.G * 0.587 + pixel.B * 0.114;
            var difference = Math.Abs(luminance - ArtistPlaceholder[y * 16 + x]);
            if (difference > 6) return false;
            error += difference;
        }
        return error / ArtistPlaceholder.Length <= 1.5;
    }

    public static bool IsArtistPlaceholderFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            using var image = Image.Load(path);
            return IsArtistPlaceholder(image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnknownImageFormatException or InvalidImageContentException)
        {
            return false; // Unreadable files are handled by the caller's existing image validation.
        }
    }

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
