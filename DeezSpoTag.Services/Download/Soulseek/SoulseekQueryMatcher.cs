using System.Globalization;
using System.Text;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Grades a free-text Soulseek query against the identity recovered from a candidate's filename.
/// </summary>
/// <remarks>
///     <para>
///         A manual Soulseek search is a peer query, not a catalogue lookup: the user types whatever they are
///         looking for, and it may name an artist, a track, an album, or all three at once. Scoring such a
///         query as if it were a track title rejects everything, because
///         <c>"mejja"</c> was compared against the title parsed out of
///         <c>"Mejja - Thank Me Later - 12 - Cece.mp3"</c> rather than against the artist it names.
///     </para>
///     <para>
///         This matcher therefore reads the whole recovered identity, scores how well the query describes it,
///         and returns a grade rather than a yes or no. The caller decides what to do with a zero, which is
///         what keeps automated matching, where a real artist and title are known, on the shared validator.
///     </para>
/// </remarks>
public static class SoulseekQueryMatcher
{
    /// <summary>Why a candidate was rejected when the query describes nothing about it.</summary>
    public const string NoMatchReason = "query_mismatch";

    /// <summary>The reason recorded when the query does describe the candidate.</summary>
    public const string MatchReason = "query_match";

    /// <summary>The grade of a candidate the query says nothing about.</summary>
    public const double NoMatch = 0.0;

    /// <summary>The query is the whole of a name the file carries: <c>mejja</c> against <c>Mejja</c>.</summary>
    private const double Exact = 1.0;

    /// <summary>The name is a whole part of a longer query: <c>drake take care</c> against <c>Take Care</c>.</summary>
    private const double FieldInsideQuery = 0.9;

    /// <summary>The query is a whole part of a longer name: <c>roygbiv</c> against <c>Roygbiv (Remastered)</c>.</summary>
    private const double QueryInsideField = 0.85;

    /// <summary>Every word of the query is in the name, in any order.</summary>
    private const double AllTokensPresent = 0.8;

    /// <summary>Fewer than this share of the query's words being present is not a match at all.</summary>
    private const double TokenOverlapFloor = 0.5;

    /// <summary>Score of a query that barely describes the name.</summary>
    private const double TokenOverlapBase = 0.5;

    /// <summary>How much a full share of matching words is worth on top of the floor.</summary>
    private const double TokenOverlapSpan = 0.25;

    /// <summary>The query appears in the file's name, but not in any name the parser recovered.</summary>
    private const double FilenameSubstring = 0.45;

    /// <summary>
    ///     Scores how well a search term describes a candidate.
    /// </summary>
    /// <param name="query">What the user typed, in full.</param>
    /// <param name="facts">The identity recovered from the candidate's filename.</param>
    /// <param name="filename">The candidate's full remote path, used only as a last resort.</param>
    /// <returns>A grade between 0 and 1, where 0 means the query says nothing about the candidate.</returns>
    public static double Score(string? query, SoulseekFilenameFacts facts, string? filename = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var queryTokens = Tokens(query);
        if (queryTokens.Length == 0)
        {
            return NoMatch;
        }

        var best = NoMatch;
        foreach (var fieldTokens in new[] { facts.Artist, facts.Title, facts.Album }.Select(Tokens).Where(fieldTokens => fieldTokens.Length != 0))
        {
            if (fieldTokens.AsSpan().SequenceEqual(queryTokens))
            {
                return Exact;
            }

            if (ContainsSequence(queryTokens, fieldTokens))
            {
                best = Math.Max(best, FieldInsideQuery);
            }

            if (fieldTokens.Length > queryTokens.Length && ContainsSequence(fieldTokens, queryTokens))
            {
                best = Math.Max(best, QueryInsideField);
            }

            var overlap = OverlapRatio(queryTokens, fieldTokens);
            if (overlap >= 1.0)
            {
                best = Math.Max(best, AllTokensPresent);
            }
            else if (overlap >= TokenOverlapFloor)
            {
                best = Math.Max(best, Math.Min(TokenOverlapBase + (TokenOverlapSpan * overlap), AllTokensPresent));
            }
        }

        if (best > NoMatch)
        {
            return Math.Round(best, 4);
        }

        // A Soulseek path is a series of folders around the file, and the common layout puts the artist and
        // the album in those folders rather than in the filename:
        //
        //     @@akkia\Massive Attack - Mezzanine (20th Anniversary Deluxe Edition) (Remastered 2018)\CD 1\03 - Teardrop.flac
        //
        // The parser reads only the filename, so it recovers the title "Teardrop" and nothing else, and the
        // query "Massive Attack Mezzanine" scored 0 against it. That folder is plainly about Massive Attack
        // and plainly about Mezzanine, and the file is plainly the right one - so the whole release was
        // reported as one match in eleven. Peers who repeat the artist and album inside every filename were
        // unaffected, which is why the same album scored 19/19 on one peer and 1/11 on another.
        //
        // So the folders are graded on the same ladder as the names. Exact is deliberately not awarded here:
        // a folder that happens to equal the query is good evidence, but it is not a recovered identity the
        // way a parsed artist or title is, and nothing downstream should treat it as one.
        foreach (var folderTokens in FoldersOf(filename).Select(Tokens))
        {
            if (folderTokens.Length == 0)
            {
                continue;
            }

            if (ContainsSequence(queryTokens, folderTokens))
            {
                best = Math.Max(best, FieldInsideQuery);
            }

            if (folderTokens.Length > queryTokens.Length && ContainsSequence(folderTokens, queryTokens))
            {
                best = Math.Max(best, QueryInsideField);
            }

            var folderOverlap = OverlapRatio(queryTokens, folderTokens);
            if (folderOverlap >= 1.0)
            {
                best = Math.Max(best, AllTokensPresent);
            }
            else if (folderOverlap >= TokenOverlapFloor)
            {
                best = Math.Max(best, Math.Min(TokenOverlapBase + (TokenOverlapSpan * folderOverlap), AllTokensPresent));
            }

            if (best >= AllTokensPresent)
            {
                break;
            }
        }

        if (best > NoMatch)
        {
            return Math.Round(best, 4);
        }

        // A filename the parser could not take apart still says something: "Drake - Hotline Bling (feat.
        // XXX).mp3" in an unreadable shape is still a file about Drake.
        var filenameTokens = Tokens(LeafOf(filename));
        if (filenameTokens.Length > 0 && ContainsSequence(filenameTokens, queryTokens))
        {
            return FilenameSubstring;
        }

        return NoMatch;
    }

    /// <summary>
    ///     The distinct words of a value, folded to case and stripped of punctuation and accents.
    /// </summary>
    /// <remarks>
    ///     Folding is what makes a peer name match the query that was typed: <c>Céline Dion</c> and
    ///     <c>celine dion</c> are the same words, and "Celine Dion" is the same words again once the
    ///     punctuation between them is gone.
    /// </remarks>
    private static string[] Tokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Whether every word of <paramref name="shorter" /> appears in <paramref name="longer" />, in order.</summary>
    private static bool ContainsSequence(string[] longer, string[] shorter)
    {
        if (longer.Length < shorter.Length || shorter.Length == 0)
        {
            return false;
        }

        for (var start = 0; start <= longer.Length - shorter.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < shorter.Length; offset++)
            {
                if (!string.Equals(longer[start + offset], shorter[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The share of the query's words that the name carries.</summary>
    private static double OverlapRatio(string[] queryTokens, string[] fieldTokens)
    {
        var present = queryTokens.Count(token => Array.IndexOf(fieldTokens, token) >= 0);

        return (double)present / queryTokens.Length;
    }

    /// <summary>The file's own name, so the parent's folder never makes a match for free.</summary>
    private static string LeafOf(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return string.Empty;
        }

        var segments = SegmentsOf(filename);
        return segments.Length == 0 ? filename : segments[^1];
    }

    /// <summary>
    ///     The folders around a file, outermost first, without the filename itself.
    /// </summary>
    /// <remarks>
    ///     Both separators are accepted because a peer's share is authored on one operating system and read
    ///     on another, and the path arrives here as the peer wrote it.
    /// </remarks>
    private static string[] FoldersOf(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return [];
        }

        var segments = SegmentsOf(filename);
        return segments.Length <= 1 ? [] : segments[..^1];
    }

    private static string[] SegmentsOf(string filename)
        => filename.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
