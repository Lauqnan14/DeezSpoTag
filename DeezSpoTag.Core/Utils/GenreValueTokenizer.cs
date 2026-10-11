namespace DeezSpoTag.Core.Utils;

/// <summary>
/// Splits a metadata value into the musical terms it actually contains.
///
/// A separator character is not proof of multiple terms. The whole value is
/// asked first, through <paramref name="isAtomic"/>, whether it is one known
/// term. Only a value that is not recognized is split, and only on the
/// separators that container uses.
/// </summary>
public static class GenreValueTokenizer
{
    /// <summary>
    /// Expands one raw metadata value.
    /// </summary>
    /// <param name="value">The value as the container returned it.</param>
    /// <param name="extension">File extension, including the leading dot. Null uses Vorbis rules.</param>
    /// <param name="normalizationEnabled">
    /// When true, slash and comma are allowed to separate values that were not
    /// recognized whole. When false, only the container's real multi-value
    /// encoding is split.
    /// </param>
    /// <param name="isAtomic">
    /// Returns true when the complete string is one term and must not be split.
    /// A null recognizer treats nothing as atomic.
    /// </param>
    public static List<string> Tokenize(
        string? value,
        string? extension,
        bool normalizationEnabled,
        Func<string, bool>? isAtomic = null)
    {
        var output = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return output;
        }

        foreach (var piece in value.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Expand(piece, extension, normalizationEnabled, isAtomic, output, depth: 0);
        }

        return output;
    }

    private static void Expand(
        string value,
        string? extension,
        bool normalizationEnabled,
        Func<string, bool>? isAtomic,
        List<string> output,
        int depth)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        // A separator that does not produce a smaller token must not recurse.
        if (depth > 8)
        {
            output.Add(trimmed);
            return;
        }

        if (isAtomic?.Invoke(trimmed) == true)
        {
            output.Add(trimmed);
            return;
        }

        var separators = Separators(extension, normalizationEnabled);
        if (separators.Length == 0 || trimmed.IndexOfAny(separators) < 0)
        {
            output.Add(trimmed);
            return;
        }

        var parts = trimmed.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 1)
        {
            output.Add(trimmed);
            return;
        }

        foreach (var part in parts)
        {
            Expand(part, extension, normalizationEnabled, isAtomic, output, depth + 1);
        }
    }

    /// <summary>
    /// Separators that denote multiple stored values for this container.
    /// </summary>
    /// <remarks>
    /// ID3 and MP4 store several values in one string, so comma and semicolon are
    /// decoded even when normalization is off. A Vorbis comment is already one
    /// value; a semicolon still marks multiple terms, and a comma is only a
    /// separator when normalization is on, because a comma is legal inside one
    /// Vorbis value. Slash is a normalization separator in every container, and
    /// only then. Ampersand is never a separator.
    /// </remarks>
    private static char[] Separators(string? extension, bool normalizationEnabled)
    {
        var name = (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        var joinedContainer = name is "mp3" or "m4a" or "mp4" or "m4b" or "m4p" or "aac";
        if (joinedContainer)
        {
            return normalizationEnabled
                ? [',', ';', '/', '\\']
                : [',', ';'];
        }

        return normalizationEnabled
            ? [';', ',', '/', '\\']
            : [';'];
    }
}
