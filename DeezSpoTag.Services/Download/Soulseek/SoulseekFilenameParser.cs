using System.Globalization;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>The track facts recovered from a Soulseek filename.</summary>
/// <param name="Title">The recovered track title.</param>
/// <param name="Artist">The recovered artist, when the filename implies one.</param>
/// <param name="Album">The recovered album, when the filename implies one.</param>
/// <param name="TrackNumber">The recovered track number, when present.</param>
/// <param name="DurationMs">The duration slskd reported, when it could determine it.</param>
/// <param name="IsNoise">Whether the filename looks like junk rather than a release.</param>
/// <param name="IsJunk">
///     Whether the noise is severe enough that automation should refuse the file outright, as opposed to
///     merely costing it score.
/// </param>
/// <param name="NoiseReasons">Why the filename was judged noisy, when it was.</param>
public sealed record SoulseekFilenameFacts(
    string Title,
    string? Artist = null,
    string? Album = null,
    int? TrackNumber = null,
    int? DurationMs = null,
    bool IsNoise = false,
    bool IsJunk = false,
    IReadOnlyList<string>? NoiseReasons = null)
{
    /// <summary>Gets a value indicating whether any noise was detected.</summary>
    public bool HasNoise => IsNoise;
}

/// <summary>
///     Recovers track facts from a Soulseek filename.
/// </summary>
/// <remarks>
///     <para>
///         Soulseek has no catalogue metadata, so the filename is the only identity a candidate has. This
///         parser therefore runs before matching: it extracts the best title, artist and album it can from
///         the common release naming conventions, and separately flags filenames that look like noise.
///     </para>
///     <para>
///         Parsing is deliberately conservative. When a filename cannot be confidently understood, the whole
///         basename is treated as the title, which lets the shared
///         <see cref="Matching.TrackCandidateValidator"/> do the actual matching rather than this parser
///         inventing structure it is not sure about.
///     </para>
/// </remarks>
public static partial class SoulseekFilenameParser
{
    /// <summary>
    ///     Tokens that mean the file is not the audio a downloader wants: a video, a preview, a trailer.
    ///     Automation refuses these outright.
    /// </summary>
    private static readonly string[] JunkTokens =
    [
        "official video",
        "official lyric",
        "lyric video",
        "lyrics video",
        "music video",
        "audio only",
        "visualizer",
        "free download",
        "listen online",
        "preview",
        "teaser",
        "trailer"
    ];

    /// <summary>
    ///     Tokens that only make a filename untidy. A "(Remastered)" or "(Explicit)" file is still the right
    ///     track, so these cost score but never disqualify a candidate.
    /// </summary>
    private static readonly string[] CosmeticNoiseTokens =
    [
        "official audio",
        "hq",
        "hd",
        "4k",
        "remastered",
        "explicit",
        "clean version"
    ];

    /// <summary>Tokens that mark a file as an artwork, cue or log sidecar rather than audio.</summary>
    private static readonly string[] NonAudioTokens = ["cover", "folder", "front", "back", "discinfo", "log", "cue", "nfo", "readme"];

    /// <summary>
    ///     Tokens that identify an alternate version rather than an album. A bracketed segment containing one
    ///     of these is left in the title so version drift can be detected.
    /// </summary>
    private static readonly string[] VersionMarkerTokens =
    [
        "live", "remix", "edit", "version", "acoustic", "instrumental", "reprise",
        "rework", "bootleg", "demo", "cover", "mono", "stereo", "radio", "vip", "extended"
    ];

    /// <remarks>
    ///     The number is optional on its own and the separator that follows it may be a bullet, so a scene
    ///     release's "01•01 - Artist - Title" has its numbering removed instead of being read as an artist
    ///     called "01" and a title called "Artist".
    /// </remarks>
    [GeneratedRegex(@"^\s*\d{1,3}\s*[-._)\]•·]\s*(?:\d{1,3}\s*[-._)\]•·]\s*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex TrackNumberPrefix();

    /// <summary>
    ///     The separator a release puts between an artist and a title.
    /// </summary>
    /// <remarks>
    ///     A spaced hyphen is the common form. Scene releases also use a bullet, a middot, an en dash or an
    ///     em dash in the same position, and a name read as one long string because of it is a track that
    ///     cannot be matched, tagged or filed.
    /// </remarks>
    [GeneratedRegex(@"\s+[-–—•·]\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex NameSeparator();

    /// <summary>The track number a name opens with, if it opens with one at all.</summary>
    [GeneratedRegex(@"^\s*(?<track>\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex LeadingTrackNumber();

    [GeneratedRegex(@"\s*[\(\[]\s*(?<duration>\d{1,2}):(?<seconds>\d{2})(?:\.\d+)?\s*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex DurationMarker();

    [GeneratedRegex(@"\s*[\(\[]\s*(?<rest>[^\(\)\[\]]*?)[\)\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex BracketedSegment();

    [GeneratedRegex(@"(?:\d{4})?\s*[\(\[]?\s*\b(?<year>(?:19|20)\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex YearMarker();

    /// <summary>
    ///     Recovers track facts from a remote filename.
    /// </summary>
    /// <param name="filename">The remote filename reported by slskd.</param>
    /// <param name="reportedDurationSeconds">The duration slskd determined, when it could.</param>
    public static SoulseekFilenameFacts Parse(string? filename, int? reportedDurationSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return new SoulseekFilenameFacts(string.Empty, IsNoise: true, NoiseReasons: ["empty_filename"]);
        }

        var durationMs = reportedDurationSeconds is > 0 ? reportedDurationSeconds * 1000 : null;
        var leaf = ExtractLeaf(filename);
        var stem = StripExtension(leaf);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return new SoulseekFilenameFacts(leaf, DurationMs: durationMs, IsNoise: true, NoiseReasons: ["no_filename"]);
        }

        var noiseReasons = new List<string>();
        var isJunk = false;
        var haystack = stem.ToLowerInvariant();

        if (NonAudioTokens.Any(token => ContainsToken(haystack, token)))
        {
            noiseReasons.Add("non_audio_file");
            isJunk = true;
        }

        if (JunkTokens.Any(token => ContainsToken(haystack, token)))
        {
            noiseReasons.Add("promotional_junk");
            isJunk = true;
        }

        if (CosmeticNoiseTokens.Any(token => ContainsToken(haystack, token)))
        {
            noiseReasons.Add("cosmetic_noise");
        }

        // A run of brackets is the classic sign of a scene release, not a tagged release.
        if (stem.Count(character => character is '(' or '[') >= 3)
        {
            noiseReasons.Add("excessive_bracketing");
        }

        // A bare hash is what a peer uploads when it has no real name for the file.
        if (stem.Length >= 8 && stem.All(character => Uri.IsHexDigit(character)))
        {
            noiseReasons.Add("hash_filename");
            isJunk = true;
        }

        var working = DurationMarker().Replace(stem, " ");
        working = TrackNumberPrefix().Replace(working, " ");

        int? trackNumber = null;
        var prefixMatch = LeadingTrackNumber().Match(stem);
        if (prefixMatch.Success
            && int.TryParse(prefixMatch.Groups["track"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTrack))
        {
            trackNumber = parsedTrack;
        }

        working = YearMarker().Replace(working, " ");

        var split = SplitName(working);
        var title = Clean(split.Title);
        var artist = Clean(split.Artist);
        var album = Clean(split.Album);

        if (title.Length == 0)
        {
            noiseReasons.Add("unparseable_name");
            isJunk = true;
        }

        return new SoulseekFilenameFacts(
            title,
            string.IsNullOrEmpty(artist) ? null : artist,
            string.IsNullOrEmpty(album) ? null : album,
            // A number read out of the layout is the same fact as one stripped from the front of the name, and
            // the one the layout found is the more precise of the two.
            trackNumber ?? split.TrackNumber,
            durationMs,
            noiseReasons.Count > 0,
            isJunk,
            noiseReasons);
    }

    /// <summary>
    ///     Scores how well a filename's implied identity matches the wanted track, before the shared
    ///     validator runs.
    /// </summary>
    /// <remarks>
    ///     Used to rank otherwise equal candidates. A filename that names the artist scores higher than one
    ///     that only carries a title.
    /// </remarks>
    public static double IdentityConfidence(SoulseekFilenameFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.Title))
        {
            return 0;
        }

        var score = 0.5;
        if (!string.IsNullOrWhiteSpace(facts.Artist))
        {
            score += 0.3;
        }

        if (!string.IsNullOrWhiteSpace(facts.Album))
        {
            score += 0.1;
        }

        if (facts.TrackNumber.HasValue)
        {
            score += 0.1;
        }

        return Math.Min(1.0, score);
    }

    private static SplitNameResult SplitName(string working)
    {
        // A scene release separates the artist from the title with a bullet or a long dash as readily as with
        // a hyphen, so every such separator is folded to the one form the split below understands. Without
        // this, "Artist • Title" arrives as one long title with no artist at all, and the track can neither
        // be matched against a target nor filed under its artist.
        var normalized = NameSeparator().Replace(working, " - ");

        // "Artist - Title - Album" carries the most structure, so it is tried before the shorter shape.
        var parts = normalized.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3)
        {
            // A fourth component with a number in the second-to-last position is a ripper's
            // "artist - album - track - title", not the older three-part shape. Getting this backwards read the
            // album as the title and the track number as part of it, so a perfectly good file was matched
            // against a track called "04 - Isabella" and rejected.
            if (TryReadNumberedLayout(parts, out var numbered))
            {
                return numbered;
            }

            return new SplitNameResult(parts[1], parts[0], string.Join(" - ", parts.Skip(2)));
        }

        // "Artist - Title" is by far the most common remaining shape.
        var dashIndex = normalized.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIndex > 0)
        {
            return new SplitNameResult(normalized[(dashIndex + 3)..], normalized[..dashIndex], null);
        }

        // Otherwise, a bracketed segment is treated as the album and the remainder as the title. The artist
        // is left null rather than guessed, and the shared validator decides whether that is acceptable for
        // the current mode.
        var albumMatch = BracketedSegment().Match(normalized);
        if (albumMatch.Success && !IsVersionMarker(albumMatch.Groups["rest"].Value))
        {
            var album = albumMatch.Groups["rest"].Value.Trim();
            var title = normalized.Remove(albumMatch.Index, albumMatch.Length).Trim();
            return new SplitNameResult(
                title.Length == 0 ? normalized.Trim() : title,
                null,
                album.Length == 0 ? null : album);
        }

        // Underscores are the last thing tried, and only as a whole layout. Replacing them everywhere would
        // split "Jay_Zzy_Disconnected" into three components and then read a track number out of a word.
        if (TryReadUnderscoreLayout(normalized, out var underscored))
        {
            return underscored;
        }

        return new SplitNameResult(normalized, null, null);
    }

    /// <summary>
    ///     Reads "artist - album - track - title" out of a spaced name.
    /// </summary>
    /// <remarks>
    ///     The track number is what identifies the shape, and it has to be a whole component: "Sauti Sol - Live
    ///     and Die in Afrika - 04 - Isabella" is the layout, while "Artist - Title - Album" is not, because its
    ///     second-to-last component is a word.
    /// </remarks>
    private static bool TryReadNumberedLayout(string[] parts, out SplitNameResult read)
    {
        read = default;
        if (parts.Length < 4 || !TryReadTrackNumber(parts[^2], out var trackNumber))
        {
            return false;
        }

        var album = string.Join(" - ", parts[1..^2]).Trim();
        var title = parts[^1].Trim();
        if (parts[0].Length == 0 || album.Length == 0 || title.Length == 0)
        {
            return false;
        }

        read = new SplitNameResult(title, parts[0], album, trackNumber);
        return true;
    }

    /// <summary>
    ///     Reads "artist_album_track_title" out of an underscore-delimited name.
    /// </summary>
    /// <remarks>
    ///     This runs only after the spaced forms have had their chance, and only for a name that has at least
    ///     four components with a number in the second-to-last one. Anything less is left exactly as it was, so
    ///     an ordinary tagged file that uses underscores in its title keeps its title.
    /// </remarks>
    private static bool TryReadUnderscoreLayout(string normalized, out SplitNameResult read)
    {
        read = default;
        if (normalized.Contains(" - ", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = normalized
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        return TryReadNumberedLayout(parts, out read);
    }

    /// <summary>
    ///     What a filename's shape said about the track.
    /// </summary>
    /// <remarks>
    ///     The track number is carried out of the split because a numbered layout is the one shape that says
    ///     where it is: a leading number is stripped before the split, and a number in the middle would
    ///     otherwise be read as part of the album.
    /// </remarks>
    private readonly record struct SplitNameResult(string Title, string? Artist, string? Album, int? TrackNumber = null);

    /// <summary>Whether a component is nothing but a track number.</summary>
    private static bool TryReadTrackNumber(string component, out int trackNumber)
    {
        trackNumber = 0;
        var trimmed = component.Trim();
        return trimmed.Length > 0
            && trimmed.Length <= 3
            && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out trackNumber);
    }

    /// <summary>
    ///     Returns a value indicating whether a bracketed segment names a version rather than an album.
    /// </summary>
    /// <remarks>
    ///     This matters: "(Live)" must stay in the title so the shared matcher can see the version drift and
    ///     reject the file, but treating it as an album would silently strip it and make a live recording look
    ///     like the studio master.
    /// </remarks>
    private static bool IsVersionMarker(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        var lowered = segment.ToLowerInvariant();
        return VersionMarkerTokens.Any(token => ContainsToken(lowered, token));
    }

    private static string ExtractLeaf(string filename)
    {
        var segments = filename.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length == 0 ? filename : segments[^1];
    }

    private static string StripExtension(string leaf)
    {
        var dot = leaf.LastIndexOf('.');
        return dot > 0 ? leaf[..dot] : leaf;
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = Whitespace().Replace(value.Trim(), " ");
        return cleaned.Trim('_', '-', '.', ' ').Trim();
    }

    private static bool ContainsToken(string haystack, string token)
    {
        var index = haystack.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index - 1;
            var after = index + token.Length;
            var startsOnBoundary = before < 0 || !char.IsLetterOrDigit(haystack[before]);
            var endsOnBoundary = after >= haystack.Length || !char.IsLetterOrDigit(haystack[after]);
            if (startsOnBoundary && endsOnBoundary)
            {
                return true;
            }

            index = haystack.IndexOf(token, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Whitespace();
}
