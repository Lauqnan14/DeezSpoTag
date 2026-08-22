using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Download.Shared;

public static class AnimatedArtworkNaming
{
    public const string DefaultSquareStem = "cover";
    public const string DefaultTallStem = "cover_tall";

    private static readonly HashSet<string> AnimatedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".webp",
        ".gif"
    };

    public static string ResolveSquareStem(string? configured)
    {
        var stem = SanitizeStem(configured);
        return string.IsNullOrWhiteSpace(stem) ? DefaultSquareStem : stem;
    }

    public static string ResolveTallStem(string? configured)
    {
        var stem = SanitizeStem(configured);
        return string.IsNullOrWhiteSpace(stem) ? DefaultTallStem : stem;
    }

    public static string ResolveSquareStem(DeezSpoTagSettings? settings)
        => ResolveSquareStem(settings?.AnimatedArtworkSquareFileName);

    public static string ResolveTallStem(DeezSpoTagSettings? settings)
        => ResolveTallStem(settings?.AnimatedArtworkTallFileName);

    public static string SanitizeStem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(trimmed.Where(ch => !invalid.Contains(ch) && ch != '/' && ch != '\\').ToArray())
            .Trim();
        return sanitized;
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

    public static bool IsDefaultStem(string? stem)
    {
        return !string.IsNullOrWhiteSpace(stem)
            && (stem.Equals(DefaultSquareStem, StringComparison.OrdinalIgnoreCase)
                || stem.Equals(DefaultTallStem, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCurrentStem(string? stem, string? squareStem, string? tallStem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        var square = ResolveSquareStem(squareStem);
        var tall = ResolveTallStem(tallStem);
        return stem.Equals(square, StringComparison.OrdinalIgnoreCase)
            || stem.Equals(tall, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCurrentStem(string? stem, DeezSpoTagSettings? settings)
        => IsCurrentStem(stem, settings?.AnimatedArtworkSquareFileName, settings?.AnimatedArtworkTallFileName);

    public static bool IsRecognizedAnimatedStem(string? stem, string? squareStem = null, string? tallStem = null)
    {
        return IsLegacyStem(stem)
            || IsDefaultStem(stem)
            || IsCurrentStem(stem, squareStem, tallStem);
    }

    public static bool IsTallStem(string? stem, string? tallStem = null)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        return stem.Equals(ResolveTallStem(tallStem), StringComparison.OrdinalIgnoreCase)
            || stem.Equals(DefaultTallStem, StringComparison.OrdinalIgnoreCase)
            || stem.Equals("tall_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(" - tall_animated_artwork", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAnimatedArtworkSidecar(string? path, string? squareStem = null, string? tallStem = null)
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
        if (IsRecognizedAnimatedStem(stem, squareStem, tallStem))
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

    public static bool IsAlbumAnimatedArtworkSidecar(string? path, string? squareStem = null, string? tallStem = null)
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

        return IsRecognizedAnimatedStem(Path.GetFileNameWithoutExtension(path), squareStem, tallStem);
    }
}
