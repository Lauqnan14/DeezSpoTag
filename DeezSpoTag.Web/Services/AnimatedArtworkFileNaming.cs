namespace DeezSpoTag.Web.Services;

internal static class AnimatedArtworkFileNaming
{
    private static readonly HashSet<string> AnimatedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".webp",
        ".gif"
    };

    public static bool IsAnimatedArtworkSidecar(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        if (!AnimatedExtensions.Contains(extension))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(path);
        if (IsLegacyStem(stem)
            || stem.Equals("cover", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("cover_tall", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var media = TagLib.File.Create(path);
            return media.Properties.MediaTypes.HasFlag(TagLib.MediaTypes.Video)
                && !media.Properties.MediaTypes.HasFlag(TagLib.MediaTypes.Audio);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    public static bool IsLegacyStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        return stem.Equals("square_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("tall_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(" - square_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(" - tall_animated_artwork", StringComparison.OrdinalIgnoreCase);
    }
}
