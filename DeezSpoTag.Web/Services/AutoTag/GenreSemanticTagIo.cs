using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Genre;
using static DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner;

namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
/// The single place that knows how DeezSpoTag reads and writes semantic tags.
///
/// AutoTag and Genre Intelligence must agree on what a field means and how it is
/// encoded for each container — ID3 frames, Vorbis comments and MP4 atoms are
/// not interchangeable, and a reader that guesses produces silent data loss.
/// Both sides call these methods, so there is exactly one definition of the
/// mapping between a semantic field and its physical representation.
/// </summary>
internal static class GenreSemanticTagIo
{
    /// <summary>The semantic fields Genre Intelligence reads and rewrites.</summary>
    private static readonly PersonalGenreTaxonKind[] SemanticFields =
    [
        PersonalGenreTaxonKind.Genre,
        PersonalGenreTaxonKind.Style,
        PersonalGenreTaxonKind.Substyle,
        PersonalGenreTaxonKind.Context,
        PersonalGenreTaxonKind.Scene,
        PersonalGenreTaxonKind.Language
    ];

    /// <summary>
    /// Reads every semantic field out of an open file, preserving the order
    /// within each field.
    ///
    /// The order matters: it is the file's own ordering, and Genre Intelligence
    /// keeps it so a rewrite does not reshuffle a user's library.
    /// </summary>
    internal static GenreSemanticSnapshot ReadSnapshot(TagLib.File file, string extension, string stylesTagName)
    {
        var observations = new List<GenreTagObservation>();
        foreach (var field in SemanticFields)
        {
            var raw = ReadValues(file, extension, field, stylesTagName);
            var values = raw
                .SelectMany(value => SplitComposite(value, extension))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var index = 0; index < values.Count; index++)
            {
                observations.Add(new GenreTagObservation(values[index], field, index));
            }
        }

        return new GenreSemanticSnapshot(observations, DateTimeOffset.UtcNow);
    }

    private static List<string> ReadValues(
        TagLib.File file,
        string extension,
        PersonalGenreTaxonKind field,
        string stylesTagName)
    {
        // Genre goes through TagLib's own accessor so ID3v1/ID3v2, Vorbis and
        // MP4 all resolve the way the rest of AutoTag resolves them. Every other
        // field is read as a raw tag, because they are custom fields in every
        // container and have no typed accessor.
        var values = field == PersonalGenreTaxonKind.Genre
            ? (file.Tag.Genres ?? []).ToList()
            : LocalAutoTagRunner.ReadRawTagValues(file, extension, RawTagName(field, stylesTagName));

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToList();
    }

    /// <summary>
    /// Whether a raw value actually represents several values.
    ///
    /// An ID3v2 frame and an MP4 free-form atom both store a multi-value tag as
    /// one separator-joined string, so a raw read returns "Trap, Southern
    /// Hip-Hop" where a Vorbis comment returns two entries. Splitting is what the
    /// AutoTag writer does when it composes such a field, so a reader that does
    /// not split sees one value that can never match the taxonomy.
    ///
    /// Only these two containers are split. A Vorbis comment is genuinely
    /// multi-valued, and splitting a value that legitimately contains a comma —
    /// which is legal inside a quoted Vorbis field — would corrupt it.
    /// </summary>
    internal static IEnumerable<string> SplitComposite(string value, string extension)
    {
        var name = extension.TrimStart('.').ToLowerInvariant();
        if (name is not ("mp3" or "m4a" or "mp4" or "m4b" or "m4p" or "aac"))
        {
            yield return value;
            yield break;
        }

        foreach (var trimmed in value.Split([',', ';', '\0'], StringSplitOptions.RemoveEmptyEntries)
                     .Select(part => part.Trim())
                     .Where(candidate => candidate.Length > 0))
        {
            yield return trimmed;
        }
    }

    /// <summary>
    /// Resolves the physical tag name for a semantic field.
    ///
    /// Style honours the user's configured custom tag per container, because
    /// AutoTag already lets them place it wherever they like. The remaining
    /// dimensions use fixed names and are written only when explicitly enabled.
    /// </summary>
    internal static string RawTagName(PersonalGenreTaxonKind field, string stylesTagName)
        => field switch
        {
            PersonalGenreTaxonKind.Style => stylesTagName,
            PersonalGenreTaxonKind.Genre => "TCON",
            PersonalGenreTaxonKind.Language => LocalAutoTagRunner.LanguageTagName,
            _ => field.ToString().ToUpperInvariant()
        };

    /// <summary>
    /// Builds the final semantic payload: what each field should contain once
    /// Genre Intelligence is done. Genre and Style honor the run capitalization
    /// preference; standalone previews use the default display spelling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resolved dimensions are the authoritative state, written with the display
    /// spelling DeezSpoTag already applies to genres. The researched master stays the
    /// semantic authority: it decides what a value <em>is</em>, and this step decides
    /// only how it is written, so a term the master publishes in lowercase does not
    /// reach a file in lowercase. A term with an established built-in spelling keeps
    /// it, because the catalog prefers that name.
    /// </para>
    /// <para>
    /// A preserved unmapped value is merged back into the field it was originally
    /// read from, which is the one place a value is written to a field other than its
    /// resolved dimension. That is deliberate: the taxonomy cannot say what an unknown
    /// value means, so the user's own placement is the only information there is. It
    /// is also left exactly as written, because a value cleanup was told not to
    /// understand is not one cleanup is entitled to restyle.
    /// </para>
    /// <para>
    /// Deduplication is case-insensitive and keeps the first spelling seen, so a
    /// value appearing in both snapshots produces one entry rather than two.
    /// </para>
    /// </remarks>
    internal static IReadOnlyDictionary<PersonalGenreTaxonKind, List<string>> PlanFields(
        PersonalGenreResolution resolution, bool capitalizeGenres = true)
    {
        var plan = new Dictionary<PersonalGenreTaxonKind, List<string>>();
        var seen = new Dictionary<PersonalGenreTaxonKind, HashSet<string>>();

        Add(PersonalGenreTaxonKind.Genre, resolution.Genres);
        Add(PersonalGenreTaxonKind.Style, resolution.Styles);
        Add(PersonalGenreTaxonKind.Substyle, resolution.Substyles);
        Add(PersonalGenreTaxonKind.Context, resolution.Contexts);
        Add(PersonalGenreTaxonKind.Scene, resolution.Scenes);
        Add(PersonalGenreTaxonKind.Language, resolution.Languages);

        // A value the taxonomy does not know returns to the field it was read
        // from. It is not reclassified, not promoted into Genre, and not restyled.
        foreach (var preserved in resolution.Preserved)
        {
            Add(preserved.InputField, [preserved.Value], format: false);
        }

        return plan;

        void Add(PersonalGenreTaxonKind field, IReadOnlyList<string> values, bool format = true)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var shouldCapitalize = format && (capitalizeGenres
                    || field is not (PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style));
                var output = shouldCapitalize
                    ? LocalAutoTagRunner.CapitalizeGenre(value.Trim())
                    : value.Trim();

                if (!seen.TryGetValue(field, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    seen[field] = keys;
                    plan[field] = [];
                }

                if (keys.Add(output))
                {
                    plan[field].Add(output);
                }
            }
        }
    }

    /// <summary>
    /// Re-reads the file and reports any dimension that does not hold what was
    /// meant to be written.
    ///
    /// This is what makes a write failure visible instead of silent. A tag writer
    /// that reports success while a container quietly drops a frame would
    /// otherwise leave the file inconsistent with the recorded resolution, and
    /// the discrepancy would only surface much later.
    /// </summary>
    /// <param name="plan">The intended final values, per dimension.</param>
    /// <param name="options">The enabled-write flags, so a disabled dimension is not reported as missing.</param>
    internal static IReadOnlyList<string> VerifyWrittenFields(
        string path,
        string extension,
        string stylesTagName,
        IReadOnlyDictionary<PersonalGenreTaxonKind, List<string>> plan,
        AutoTagGenreIntelligenceSettings options,
        IReadOnlyCollection<string>? selectedTags = null)
    {
        var problems = new List<string>();
        GenreSemanticSnapshot actual;
        using (var file = TagLib.File.Create(path))
        {
            actual = ReadSnapshot(file, extension, stylesTagName);
        }

        foreach (var field in new[]
                 {
                     PersonalGenreTaxonKind.Genre,
                     PersonalGenreTaxonKind.Style,
                     PersonalGenreTaxonKind.Substyle,
                     PersonalGenreTaxonKind.Context,
                     PersonalGenreTaxonKind.Scene,
                     PersonalGenreTaxonKind.Language
                 })
        {
            if (!IsWriteEnabled(field, options, selectedTags))
            {
                continue;
            }

            if (!plan.TryGetValue(field, out var intended) || intended.Count == 0)
            {
                continue;
            }

            var found = actual.Observations
                .Where(item => item.InputField == field)
                .Select(item => PersonalGenreTaxonomy.Normalize(item.RawValue))
                .ToHashSet(StringComparer.Ordinal);
            var missing = intended
                .Where(value => !found.Contains(PersonalGenreTaxonomy.Normalize(value)))
                .ToList();
            if (missing.Count > 0)
            {
                problems.Add($"{field} is missing {string.Join(", ", missing)}");
            }
        }

        return problems;
    }

    /// <summary>
    /// Whether Genre Intelligence owns writing a dimension.
    ///
    /// Genre, Style and Language follow the run's selected AutoTag tags, because
    /// that is how the user has always chosen which tags AutoTag manages. The
    /// remaining dimensions are written only when explicitly enabled, so a user
    /// who never asked for a SUBSTYLE tag does not start getting one.
    /// </summary>
    internal static bool IsWriteEnabled(
        PersonalGenreTaxonKind field,
        AutoTagGenreIntelligenceSettings options,
        IReadOnlyCollection<string>? selectedTags = null)
        => field switch
        {
            // A null set means the caller holds a plan that was already filtered
            // to the enabled dimensions, so there is nothing left to gate on.
            PersonalGenreTaxonKind.Genre => selectedTags is null || selectedTags.Contains(GenreTag),
            PersonalGenreTaxonKind.Style => selectedTags is null || selectedTags.Contains(StyleTag),
            PersonalGenreTaxonKind.Language => selectedTags is null || selectedTags.Contains(LanguageTag),
            PersonalGenreTaxonKind.Substyle => options.WriteSubstyle,
            PersonalGenreTaxonKind.Context => options.WriteContext,
            PersonalGenreTaxonKind.Scene => options.WriteScene,
            _ => false
        };

    /// <summary>
    /// Reports which fields would change, so a caller can tell the user what was
    /// corrected rather than silently rewriting a file.
    /// </summary>
    internal static IReadOnlyList<string> DescribeFieldMoves(
        IReadOnlyList<GenreTagObservation> observed,
        IReadOnlyDictionary<PersonalGenreTaxonKind, List<string>> plan)
    {
        var moves = new List<string>();
        foreach (var classification in plan)
        {
            var original = observed
                .Where(item => item.InputField == classification.Key)
                .Select(item => item.RawValue)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (original.Count == 0)
            {
                continue;
            }

            var planned = classification.Value.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gained = planned.Except(original).ToList();
            var lost = original.Except(planned).ToList();
            if (gained.Count == 0 && lost.Count == 0)
            {
                continue;
            }

            moves.Add(gained.Count == 0
                ? $"{classification.Key}: removed {string.Join(", ", lost)}"
                : lost.Count == 0
                    ? $"{classification.Key}: added {string.Join(", ", gained)}"
                    : $"{classification.Key}: {string.Join(", ", gained)} replaced {string.Join(", ", lost)}");
        }

        return moves;
    }

    internal static bool IsSupportedExtension(string extension)
        => extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || LocalAutoTagRunner.IsMp4Family(extension);
}
