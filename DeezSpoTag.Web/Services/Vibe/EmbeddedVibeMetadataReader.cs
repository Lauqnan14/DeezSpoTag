using System.Security.Cryptography;
using System.Text;
using TagLib;

namespace DeezSpoTag.Web.Services.Vibe;

/// <summary>
/// Semantic tags actually embedded in the audio file — Tier 0, the highest Vibe
/// authority. Field meaning comes from the field it was read from (Genre/Style/
/// Mood), never from guessing what a value "looks like".
/// </summary>
public sealed record EmbeddedVibeMetadata(
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Styles,
    IReadOnlyList<string> Moods)
{
    /// <summary>
    /// Stable fingerprint over the normalized embedded semantics. A change means
    /// AutoTag rewrote the file's semantic tags and Vibe resolution is stale.
    /// </summary>
    public string ComputeFingerprint()
    {
        var normalized = new StringBuilder();
        AppendSection(normalized, "genre", Genres);
        AppendSection(normalized, "style", Styles);
        AppendSection(normalized, "mood", Moods);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToString()));
        return Convert.ToHexString(bytes);
    }

    private static void AppendSection(StringBuilder builder, string section, IReadOnlyList<string> values)
    {
        builder.Append(section).Append('\u001f');
        foreach (var value in values)
        {
            builder.Append(value.Trim().ToLowerInvariant()).Append('\u001f');
        }
    }
}

/// <summary>
/// Reads the same fields AutoTag actually writes: standard genre field per format,
/// the configured AutoTag STYLE raw field, and TMOO/MOOD mood fields — preserving
/// multi-values, trimming, splitting stored composite strings on the format's
/// separator, and deduplicating case-insensitively. Never collapses to first value.
/// </summary>
public sealed class EmbeddedVibeMetadataReader
{
    private const string DefaultStyleTag = "STYLE";
    private const string AppleFreeformBase = "----:com.apple.iTunes:";

    public EmbeddedVibeMetadata Read(
        string filePath,
        string? id3StyleTag = null,
        string? vorbisStyleTag = null,
        string? mp4StyleTag = null)
    {
        using var file = TagLib.File.Create(filePath);
        var tag = file.Tag;
        var extension = Path.GetExtension(filePath);

        var genres = Normalize(tag.Genres ?? Array.Empty<string>(), SplitSeparator(extension));
        var mood = ReadRawMultiValue(file, extension, "MOOD", mp4StyleTag == null ? null : "MOOD");
        var moods = Normalize(mood, SplitSeparator(extension));

        var styleTag = ResolveStyleTag(extension, id3StyleTag, vorbisStyleTag, mp4StyleTag);
        var styles = Normalize(ReadRawMultiValue(file, extension, styleTag, styleTag), SplitSeparator(extension));

        return new EmbeddedVibeMetadata(genres, styles, moods);
    }

    private static string ResolveStyleTag(string extension, string? id3, string? vorbis, string? mp4)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(id3) ? DefaultStyleTag : id3!.Trim();
        }

        if (extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(vorbis) ? DefaultStyleTag : vorbis!.Trim();
        }

        return string.IsNullOrWhiteSpace(mp4) ? DefaultStyleTag : mp4!.Trim();
    }

    private static string? SplitSeparator(string extension)
        => extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ? null : ", ";

    private static string[] ReadRawMultiValue(TagLib.File file, string extension, string tag, string? mp4Alias)
    {
        try
        {
            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                && file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                var values = new List<string>();
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.TextInformationFrame>())
                {
                    if (frame.FrameId.ToString().Equals("TMOO", StringComparison.OrdinalIgnoreCase))
                    {
                        values.AddRange(frame.Text ?? Array.Empty<string>());
                    }
                }

                foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                {
                    if (string.Equals(frame.Description, tag, StringComparison.OrdinalIgnoreCase))
                    {
                        values.AddRange(frame.Text ?? Array.Empty<string>());
                    }
                }

                return values.ToArray();
            }

            if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                return xiph.GetField(tag);
            }

            if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
                {
                    var values = new List<string>();
                    foreach (var key in new[] { tag, mp4Alias ?? tag })
                    {
                        var raw = apple.GetDashBox("com.apple.iTunes", key);
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            values.Add(raw);
                        }
                    }

                    return values.ToArray();
                }
            }

            return Array.Empty<string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> values, string? separator)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var parts = separator is null
                ? new[] { raw }
                : raw.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var value = part.Trim();
                if (value.Length == 0 || !seen.Add(value))
                {
                    continue;
                }

                output.Add(value);
            }
        }

        return output;
    }
}
