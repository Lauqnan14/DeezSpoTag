namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>
/// Shared Audiomack genre/style/mood rules for AutoTag and Vibe.
/// Audiomack's <c>genre</c> field is a catalog bucket that can be a non-music
/// content type (Audiobook, Podcast). Music identity lives in <c>subgenres</c>,
/// typed <c>tags[].type</c>, and the public-page <c>tagdisplay</c> list.
/// Location chips in <c>tagdisplay</c> use <c>-&gt;</c> and are never music tags.
/// </summary>
internal static class AudiomackTaxonomy
{
    private static readonly HashSet<string> NonMusicValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "audiobook",
        "audiobooks",
        "podcast",
        "podcasts",
        "spoken word",
        "spoken-word",
        "spokenword",
        "radio",
        "interview",
        "interviews",
        "talk",
        "talk show",
        "talkshow"
    };

    internal static bool IsNonMusicValue(string? value)
    {
        var key = NormalizeKey(value);
        return key.Length > 0 && NonMusicValues.Contains(key);
    }

    internal static bool IsLocationTag(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Contains("->", StringComparison.Ordinal);

    /// <summary>
    /// True when the candidate is a non-music catalog row (audiobook/podcast)
    /// with no music styles to salvage. AutoTag skips these the same way Boomplay
    /// skips unusable tracks.
    /// </summary>
    internal static bool IsNonMusicCandidate(AudiomackSongCandidate song)
    {
        if (IsNonMusicValue(song.ContentType))
        {
            return true;
        }

        return IsNonMusicValue(song.Genre) && CollectStyles(song).Count == 0;
    }

    internal static IReadOnlyList<string> CollectGenres(
        AudiomackSongCandidate song,
        bool promoteStylesWhenEmpty = false,
        bool titleCase = false)
    {
        var genres = new List<string>();
        AddUnique(genres, song.Genre, titleCase, allowNonMusic: false);
        if (song.TypedTags is { Count: > 0 })
        {
            foreach (var (name, type) in song.TypedTags)
            {
                if (string.Equals(type, "genre", StringComparison.OrdinalIgnoreCase))
                {
                    AddUnique(genres, name, titleCase, allowNonMusic: false);
                }
            }
        }

        if (promoteStylesWhenEmpty && genres.Count == 0)
        {
            foreach (var style in CollectStyles(song, titleCase))
            {
                AddUnique(genres, style, titleCase: false, allowNonMusic: false);
            }
        }

        return genres;
    }

    internal static IReadOnlyList<string> CollectStyles(
        AudiomackSongCandidate song,
        bool titleCase = false)
    {
        var styles = new List<string>();
        foreach (var value in song.Subgenres)
        {
            AddUnique(styles, value, titleCase, allowNonMusic: false);
        }

        if (song.TypedTags is { Count: > 0 })
        {
            foreach (var (name, type) in song.TypedTags)
            {
                if (string.Equals(type, "subgenre", StringComparison.OrdinalIgnoreCase))
                {
                    AddUnique(styles, name, titleCase, allowNonMusic: false);
                }
            }
        }

        var displayTags = song.TagDisplay;
        if (displayTags.Count > 0)
        {
            foreach (var value in displayTags)
            {
                if (IsLocationTag(value))
                {
                    continue;
                }

                AddUnique(styles, value, titleCase, allowNonMusic: false);
            }

            return styles;
        }

        foreach (var value in song.UserTags)
        {
            if (IsLocationTag(value) || LooksLikeMashedLocation(value))
            {
                continue;
            }

            AddUnique(styles, value, titleCase, allowNonMusic: false);
        }

        return styles;
    }

    internal static IReadOnlyList<string> CollectMoods(
        AudiomackSongCandidate song,
        bool titleCase = false)
    {
        var moods = new List<string>();
        if (song.Moods.Count > 0)
        {
            foreach (var value in song.Moods)
            {
                AddUnique(moods, value, titleCase, allowNonMusic: false);
            }
        }
        else
        {
            AddUnique(moods, song.Mood, titleCase, allowNonMusic: false);
        }

        if (song.TypedTags is { Count: > 0 })
        {
            foreach (var (name, type) in song.TypedTags)
            {
                if (string.Equals(type, "mood", StringComparison.OrdinalIgnoreCase))
                {
                    AddUnique(moods, name, titleCase, allowNonMusic: false);
                }
            }
        }

        return moods;
    }

    internal static bool HasPageTaxonomy(AudiomackSongCandidate song)
        => !string.IsNullOrWhiteSpace(song.Genre)
           && !IsNonMusicValue(song.Genre)
           && CollectStyles(song).Count > 0;

    internal static AudiomackSongCandidate Merge(AudiomackSongCandidate current, AudiomackSongCandidate richer)
    {
        var genre = current.Genre;
        if (IsNonMusicValue(genre) && !IsNonMusicValue(richer.Genre) && !string.IsNullOrWhiteSpace(richer.Genre))
        {
            genre = richer.Genre;
        }
        else if (string.IsNullOrWhiteSpace(genre))
        {
            genre = richer.Genre;
        }

        return current with
        {
            Id = FirstNonEmpty(current.Id, richer.Id),
            Title = FirstNonEmpty(current.Title, richer.Title),
            Artist = FirstNonEmpty(current.Artist, richer.Artist),
            Album = FirstNonEmpty(current.Album, richer.Album),
            Genre = genre,
            Mood = FirstNonEmpty(current.Mood, richer.Mood),
            Isrc = FirstNonEmpty(current.Isrc, richer.Isrc),
            Label = FirstNonEmpty(current.Label, richer.Label),
            DurationSeconds = current.DurationSeconds ?? richer.DurationSeconds,
            ArtworkUrl = FirstNonEmpty(current.ArtworkUrl, richer.ArtworkUrl),
            ReleasedDate = FirstNonEmpty(current.ReleasedDate, richer.ReleasedDate),
            Url = FirstNonEmpty(current.Url, richer.Url),
            UrlSlug = FirstNonEmpty(current.UrlSlug, richer.UrlSlug),
            ArtistSlug = FirstNonEmpty(current.ArtistSlug, richer.ArtistSlug),
            UploaderName = FirstNonEmpty(current.UploaderName, richer.UploaderName),
            AlbumId = FirstNonEmpty(current.AlbumId, richer.AlbumId),
            Featuring = FirstNonEmpty(current.Featuring, richer.Featuring),
            ContentType = FirstNonEmpty(current.ContentType, richer.ContentType),
            Subgenres = Union(current.Subgenres, richer.Subgenres),
            Moods = Union(current.Moods, richer.Moods),
            UserTags = Union(current.UserTags, richer.UserTags),
            TagDisplay = Union(current.TagDisplay, richer.TagDisplay),
            Artists = Union(current.Artists, richer.Artists),
            TypedTags = MergeTypedTags(current.TypedTags, richer.TypedTags)
        };
    }

    internal static string ToDisplayName(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var chars = words[i].ToCharArray();
            if (chars.Length == 0)
            {
                continue;
            }

            chars[0] = char.ToUpperInvariant(chars[0]);
            for (var c = 1; c < chars.Length; c++)
            {
                if (chars[c - 1] == '&' && char.IsLetter(chars[c]))
                {
                    chars[c] = char.ToUpperInvariant(chars[c]);
                }
            }

            words[i] = new string(chars);
        }

        return string.Join(' ', words);
    }

    private static void AddUnique(List<string> target, string? raw, bool titleCase, bool allowNonMusic)
    {
        var trimmed = raw?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || IsLocationTag(trimmed))
        {
            return;
        }

        if (!allowNonMusic && IsNonMusicValue(trimmed))
        {
            return;
        }

        var display = titleCase ? ToDisplayName(trimmed) : trimmed;
        if (display.Length == 0)
        {
            return;
        }

        if (target.Any(existing => string.Equals(existing, display, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        target.Add(display);
    }

    private static bool LooksLikeMashedLocation(string value)
    {
        var compact = NormalizeKey(value);
        return compact.Length > 20 && !value.Contains(' ', StringComparison.Ordinal);
    }

    private static string NormalizeKey(string? value)
        => string.Join(' ', (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? FirstNonEmpty(string? left, string? right)
        => string.IsNullOrWhiteSpace(left) ? (string.IsNullOrWhiteSpace(right) ? left : right) : left;

    private static IReadOnlyList<string> Union(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (right.Count == 0)
        {
            return left;
        }

        if (left.Count == 0)
        {
            return right;
        }

        var merged = new List<string>(left);
        foreach (var value in right)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (merged.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            merged.Add(value.Trim());
        }

        return merged;
    }

    private static IReadOnlyList<(string Name, string? Type)>? MergeTypedTags(
        IReadOnlyList<(string Name, string? Type)>? left,
        IReadOnlyList<(string Name, string? Type)>? right)
    {
        if (right is not { Count: > 0 })
        {
            return left;
        }

        if (left is not { Count: > 0 })
        {
            return right;
        }

        var merged = new List<(string Name, string? Type)>(left);
        foreach (var tag in right)
        {
            if (merged.Any(existing =>
                    string.Equals(existing.Name, tag.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Type, tag.Type, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            merged.Add(tag);
        }

        return merged;
    }
}
