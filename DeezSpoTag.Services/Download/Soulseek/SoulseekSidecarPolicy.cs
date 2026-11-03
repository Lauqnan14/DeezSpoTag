namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     What a non-audio file a peer is sharing alongside the audio is for.
/// </summary>
/// <remarks>
///     <para>
///         A Soulseek release folder is not just the tracks. Real folders carry a cover image, a cue sheet, a
///         rip log, a playlist and occasionally a lyrics file, and every one of them was previously rejected
///         with the same undifferentiated "non audio file" reason and never fetched. For a release that is on
///         no streaming service, that cover is the only artwork that will ever exist for it.
///     </para>
///     <para>
///         Naming the role is what makes the rest possible: the drawer can say what a file is, and only the
///         roles worth taking are actually taken. Classification is deliberately conservative - a file whose
///         meaning cannot be established from its extension is <see cref="Other" /> and is left alone.
///     </para>
/// </remarks>
public static class SoulseekSidecarPolicy
{
    /// <summary>An image the peer shipped for this release.</summary>
    public const string Cover = "cover";

    /// <summary>A lyrics file the peer shipped for this release.</summary>
    public const string Lyrics = "lyrics";

    /// <summary>A cue sheet, which carries the release's track structure and often its lyrics.</summary>
    public const string CueSheet = "cuesheet";

    /// <summary>An EAC/Log or ripper job log. Diagnostics, not content, so it is never fetched.</summary>
    public const string Log = "log";

    /// <summary>A playlist. Not part of the release's content, so it is never fetched.</summary>
    public const string Playlist = "playlist";

    /// <summary>Anything whose purpose could not be established. Never fetched.</summary>
    public const string Other = "other";

    private static readonly Dictionary<string, string> RolesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = Cover,
        [".jpeg"] = Cover,
        [".png"] = Cover,
        [".gif"] = Cover,
        [".bmp"] = Cover,
        [".webp"] = Cover,
        [".lrc"] = Lyrics,
        // TTML is the timed-lyrics format Apple Music and Spotify serve, so a peer who rips against either
        // ships it instead of an LRC. Without it such a folder's lyrics classified as Other and were never
        // fetched, which is the one thing the lyrics sidecar exists to prevent.
        [".ttml"] = Lyrics,

        // Plain text is lyrics only when the name says so. Release folders also carry a track list and a
        // readme as .txt, so the suffix alone would take both; Classify resolves this entry by name.
        [TextExtension] = Lyrics,
        [".cue"] = CueSheet,
        [".log"] = Log,
        [".m3u"] = Playlist,
        [".m3u8"] = Playlist,
        [".pls"] = Playlist
    };

    /// <summary>The extension whose meaning depends on the file's name rather than its suffix alone.</summary>
    private const string TextExtension = ".txt";

    private static readonly HashSet<string> FetchableRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        Cover,
        Lyrics,
        CueSheet
    };

    /// <summary>
    ///     Classifies one listed file.
    /// </summary>
    /// <param name="filename">The file's name or full remote path.</param>
    /// <param name="extension">The extension slskd reported, when it reported one.</param>
    public static string Classify(string? filename, string? extension = null)
    {
        var normalized = NormalizeExtension(extension);
        if (normalized.Length == 0)
        {
            normalized = NormalizeExtension(Path.GetExtension(SoulseekRemotePath.GetLeaf(filename)));
        }

        if (!RolesByExtension.TryGetValue(normalized, out var role))
        {
            return Other;
        }

        // .txt is the one extension whose table entry cannot be taken at face value. Release folders carry a
        // track list, a readme and a rip note as plain text as often as they carry lyrics, and every one of
        // them is a .txt. Taking them would spend a peer's bandwidth on a file nothing can use and file a
        // track list as if it were words to a song, so the name decides: "Mezzanine - Lyrics.txt" is lyrics
        // and "-- track list.txt" is not. The extension alone genuinely cannot tell them apart, which is why
        // this is a name test rather than a missing table entry.
        if (role == Lyrics && normalized == TextExtension)
        {
            return LooksLikeLyrics(SoulseekRemotePath.GetLeaf(filename)) ? Lyrics : Other;
        }

        return role;
    }

    /// <summary>
    ///     Whether a plain text file's name says it holds words rather than notes about the release.
    /// </summary>
    /// <remarks>
    ///     Only the word "lyric" counts, in any of the spellings a ripper produces. Deliberately narrow: a
    ///     looser match would take "notes.txt" and "album info.txt" as lyrics, which is the failure this
    ///     guards against, so an unrecognised name stays <see cref="Other" /> and is left alone.
    /// </remarks>
    private static bool LooksLikeLyrics(string? leaf)
    {
        if (string.IsNullOrWhiteSpace(leaf))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(leaf) ?? string.Empty;
        return name.Contains("lyric", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Whether a role is worth downloading alongside the audio.
    /// </summary>
    /// <remarks>
    ///     A log and a playlist describe how the release was made, not what it contains, so taking them would
    ///     spend a peer's bandwidth and the reader's disk on files nothing in the app can use.
    /// </remarks>
    public static bool IsFetchable(string? role)
        => !string.IsNullOrWhiteSpace(role) && FetchableRoles.Contains(role);

    public static bool UsesPeerSidecars(DeezSpoTag.Services.Download.Shared.EngineQueueItemBase item)
        => string.Equals(item.DownloadOrigin, "shazam", StringComparison.OrdinalIgnoreCase);

    /// <summary>Select one front cover and lyrics belonging to this track, from its exact peer directory.</summary>
    public static IReadOnlyList<string> SelectForTrack(
        string audioPath, string title, string artist, IEnumerable<string> paths, bool artwork, bool lyrics)
    {
        var directory = SoulseekRemotePath.GetDirectory(audioPath);
        var siblings = paths.Select(SoulseekRemotePath.Normalize)
            .Where(path => SoulseekRemotePath.IsDirectChildOf(path, directory)
                && !path.Split('/').Any(part => part is "." or ".."))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<string>();
        if (artwork)
        {
            var cover = siblings.Where(path => Classify(path) == Cover)
                .Select(path => (Path: path, Rank: CoverRank(path)))
                .Where(item => item.Rank < int.MaxValue)
                .OrderBy(item => item.Rank).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (cover.Path is not null) result.Add(cover.Path);
        }
        if (lyrics)
        {
            var audioStem = NormalizeStem(audioPath, remoteFile: true);
            var song = NormalizeStem(title);
            var creditedSong = NormalizeStem(artist + " " + title);
            var matches = siblings.Where(path => Classify(path) == Lyrics)
                .Where(path => {
                    var stem = NormalizeStem(path, remoteFile: true);
                    return stem == audioStem || stem == song || stem == creditedSong;
                });
            // Keep at most one lyric file per format; never take another track's words or a cue sheet.
            result.AddRange(matches.GroupBy(path => Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First()));
        }
        return result;
    }

    private static int CoverRank(string path)
    {
        var name = Path.GetFileNameWithoutExtension(SoulseekRemotePath.GetLeaf(path)).ToLowerInvariant();
        var words = System.Text.RegularExpressions.Regex.Split(name, @"[^\p{L}\p{N}]+", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        if (words.Any(word => word is "back" or "rear" or "disc" or "cd" or "booklet" or "inlay" or "tray" or "backcover" or "coverback" or "discart")) return int.MaxValue;
        if (words.Any(word => word is "front" or "frontcover" or "coverfront")) return 0;
        if (words.Any(word => word is "cover" or "folder" or "albumart" or "artwork")) return 1;
        return 2;
    }

    private static string NormalizeStem(string value, bool remoteFile = false)
    {
        // Titles need not have an extension. Only strip known audio/lyric suffixes from remote filenames.
        var leaf = remoteFile ? SoulseekRemotePath.GetLeaf(value) : value;
        var isTextLyrics = remoteFile && Path.GetExtension(leaf).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        if (remoteFile && new[] { ".flac", ".mp3", ".m4a", ".wav", ".ogg", ".opus", ".aiff", ".lrc", ".ttml", ".txt" }
            .Contains(Path.GetExtension(leaf), StringComparer.OrdinalIgnoreCase))
            leaf = Path.GetFileNameWithoutExtension(leaf);
        if (isTextLyrics) leaf = System.Text.RegularExpressions.Regex.Replace(leaf, @"[\s_-]+lyrics$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        leaf = System.Text.RegularExpressions.Regex.Replace(leaf, @"^\d+[.\s_-]+", "", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return new string(leaf.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static string NormalizeExtension(string? extension)
    {
        var normalized = (extension ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return normalized.StartsWith('.') ? normalized : $".{normalized}";
    }
}
