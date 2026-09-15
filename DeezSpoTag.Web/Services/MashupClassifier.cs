using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Classifies mashup-style releases — mashups, DJ mixes, bootlegs, medleys, "vs" and "×"
/// collaborations. These are typically unofficial: they exist on no database as a release, so
/// their tags rarely match anything and they must not be treated as ordinary orphans.
///
/// The classifier is deliberately title/file-name based and side-effect free. It combines with
/// Shazam identity when the tags are weak: the caller fingerprints first and only then looks for
/// platform data (MusicBrainz included). Two callers use it:
/// <list type="bullet">
///   <item>the missing-metadata audit, where an unmatchable mashup is classified as unofficial
///   rather than counted as an orphan needing repair;</item>
///   <item>dedupe, where such a file may only move to the duplicates folder on a strong identity
///   (Shazam track id, ISRC or audio hash) — never on basename or duration-bucket keys alone.</item>
/// </list>
/// </summary>
internal static class MashupClassifier
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // "mashup" / "mash-up" / "mash_up", "dj mix" / "dj-mix", "bootleg", "medley",
    // a standalone "vs"/"vs." between artists, and the multiplication sign used for collaborations.
    private static readonly Regex MashupPattern = new(
        @"\bmash[\s\-_]?up\b|\bdj[\s\-_]?mix\b|\bbootleg\b|\bmedley\b|\bvs\.?\b|[×✕]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>True when the given title reads as a mashup-style release.</summary>
    public static bool IsMashupTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        try
        {
            return MashupPattern.IsMatch(title);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the file's name (without extension) reads as a mashup-style release. Used when the
    /// tags carry no title — the missing-metadata audit classifies those from the file name.
    /// </summary>
    public static bool IsMashupFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return IsMashupTitle(Path.GetFileNameWithoutExtension(filePath));
    }
}
