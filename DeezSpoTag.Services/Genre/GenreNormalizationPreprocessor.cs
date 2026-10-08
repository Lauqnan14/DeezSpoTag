namespace DeezSpoTag.Services.Genre;

using DeezSpoTag.Core.Utils;

/// <summary>
/// Applies the shared genre-normalization preferences to the values read out of a
/// file, before Genre Cleanup classifies anything.
///
/// <para>
/// This is deliberately not a second normalizer. It calls
/// <see cref="GenreTagAliasNormalizer.NormalizeExpandFilterAndDedupeValues"/> with
/// exactly the arguments AutoTag's own <c>SanitizeGenres</c> passes, so one
/// implementation of "what does the user's saved preference do to this value"
/// serves downloads, QuickTag, the AutoTag provider stage and Genre Intelligence.
/// A change to the shared algorithm therefore cannot silently diverge between
/// them.
/// </para>
/// <para>
/// Normalization is preprocessing, not classification. It answers "what spelling
/// does the user want written" and "which values did the user forbid"; it does not
/// decide whether a value is a Genre or a Style. That decision belongs to the
/// cleanup classification, which runs afterwards.
/// </para>
/// <para>
/// The original value is never discarded. Every surviving observation keeps both
/// its raw spelling and its normalized spelling, and every removed value is
/// reported with the reason it was removed, so the decision trail can explain a
/// removal instead of leaving a silent gap.
/// </para>
/// </summary>
public static class GenreNormalizationPreprocessor
{
    /// <summary>
    /// The fields that carry genre meaning and therefore obey the genre
    /// preferences exactly as AutoTag applies them.
    /// </summary>
    private static readonly PersonalGenreTaxonKind[] NormalizedFields =
    [
        PersonalGenreTaxonKind.Genre,
        PersonalGenreTaxonKind.Style,
        PersonalGenreTaxonKind.Substyle,
        PersonalGenreTaxonKind.Context,
        PersonalGenreTaxonKind.Scene,
        PersonalGenreTaxonKind.Language
    ];

    /// <summary>
    /// Normalizes a snapshot.
    /// </summary>
    /// <param name="snapshot">The values read from the file.</param>
    /// <param name="normalization">
    /// The user's saved preferences. A null snapshot means normalization has never
    /// been configured, which is the same state as the shipped defaults: no alias
    /// rewriting and the standard block list.
    /// </param>
    /// <returns>
    /// The surviving observations, each carrying its original and normalized
    /// spelling, plus every value that was removed and why.
    /// </returns>
    public static GenreNormalizationResult Apply(
        GenreSemanticSnapshot? snapshot,
        GenreNormalizationSnapshot? normalization)
    {
        normalization ??= GenreNormalizationSnapshot.Default;

        if (snapshot is null || snapshot.Observations.Count == 0)
        {
            return new GenreNormalizationResult(
                Array.Empty<GenreTagObservation>(),
                Array.Empty<RemovedGenreTagValue>());
        }

        var kept = new List<GenreTagObservation>();
        var removed = new List<RemovedGenreTagValue>();

        foreach (var group in snapshot.Observations
                     .Where(item => NormalizedFields.Contains(item.InputField))
                     .GroupBy(item => item.InputField))
        {
            var field = group.Key;
            var originals = group
                .Select(item => item with { RawValue = (item.RawValue ?? string.Empty).Trim() })
                .Where(item => item.RawValue.Length > 0)
                .ToList();
            if (originals.Count == 0)
            {
                continue;
            }

            // The genre field is the one AutoTag sanitizes, so it has to be produced
            // by the same call with the same arguments. The user's block list is a
            // genre block list and composite splitting is a genre-writing
            // behaviour, so both are applied here and only here. The other semantic
            // fields get the user's alias preference and deduplication, because a
            // spelling preference should not depend on which field a value happened
            // to land in, but they do not get genre-only behaviour AutoTag never
            // applied to them.
            var isGenreField = field == PersonalGenreTaxonKind.Genre;
            var splitComposite = isGenreField && normalization.Enabled;

            // One call for the whole field, never one call per value. The shared
            // function deduplicates across the batch it is handed, so calling it
            // per value would let "Afro Pop" and "Afropop" both survive as
            // "Afropop" and the same composite would appear twice.
            var normalized = GenreTagAliasNormalizer.NormalizeExpandFilterAndDedupeValues(
                originals.Select(item => item.RawValue),
                normalization.AliasMap,
                splitComposite,
                blockedValues: isGenreField ? normalization.BlockList : null);

            var surviving = normalized.ToHashSet(StringComparer.Ordinal);
            var lineage = BuildLineage(originals, normalization, splitComposite, surviving);

            foreach (var original in originals)
            {
                var tokens = GenreTagAliasNormalizer.NormalizeAndExpandValues(
                    [original.RawValue],
                    normalization.AliasMap,
                    splitComposite);

                if (tokens.Any(token => surviving.Contains(token)))
                {
                    continue;
                }

                removed.Add(new RemovedGenreTagValue(
                    original.RawValue,
                    field,
                    DescribeRemoval(original.RawValue, normalization, isGenreField),
                    original.Origin));
            }

            var order = 0;
            foreach (var value in normalized)
            {
                kept.Add(new GenreTagObservation(value, field, order++, originals[0].Origin)
                {
                    OriginalValue = lineage.GetValueOrDefault(value)
                });
            }
        }

        // Anything the resolver does not understand is passed through rather than
        // being silently dropped, and keeps a position after the cleaned fields.
        var passthrough = 0;
        foreach (var observation in snapshot.Observations
                     .Where(item => !NormalizedFields.Contains(item.InputField)))
        {
            kept.Add(observation with { Order = passthrough++ });
        }

        return new GenreNormalizationResult(kept, removed);
    }

    /// <summary>
    /// Records which value in the file each surviving normalized value came from.
    /// </summary>
    /// <remarks>
    /// This is reporting only. Behaviour comes from the single shared call above;
    /// this merely answers "the file said Hip Hop and we are writing Hip-Hop", so
    /// the first original spelling that produced a value owns it.
    /// </remarks>
    private static Dictionary<string, string> BuildLineage(
        IReadOnlyList<GenreTagObservation> originals,
        GenreNormalizationSnapshot normalization,
        bool splitComposite,
        HashSet<string> surviving)
    {
        var lineage = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var original in originals)
        {
            foreach (var token in GenreTagAliasNormalizer.NormalizeAndExpandValues(
                         [original.RawValue],
                         normalization.AliasMap,
                         splitComposite))
            {
                if (surviving.Contains(token))
                {
                    lineage.TryAdd(token, original.RawValue);
                }
            }
        }

        return lineage;
    }

    /// <summary>
    /// Says why a value disappeared, so the decision trail records the actual
    /// cause rather than a generic "removed".
    /// </summary>
    private static string DescribeRemoval(string raw, GenreNormalizationSnapshot normalization, bool isGenreField)
    {
        if (!isGenreField)
        {
            return $"'{raw}' normalized to nothing.";
        }

        var key = GenreTagAliasNormalizer.ToLookupKey(raw);
        var blocked = normalization.BlockList
            .Select(GenreTagAliasNormalizer.ToLookupKey)
            .Where(candidate => candidate.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        return blocked.Contains(key)
            ? $"'{raw}' is blocked by the saved Genre Normalization block list."
            : $"'{raw}' was removed by the saved Genre Normalization preferences.";
    }
}

/// <summary>
/// The outcome of applying the shared normalization preferences to one file.
/// </summary>
/// <param name="Observations">
/// The values that survived, each carrying the value as it was found in the file
/// and the spelling the user's preferences produce from it.
/// </param>
/// <param name="Removed">
/// Every value the preferences removed, with the reason. A blocked value is never
/// silently dropped: it is reported here so the run can explain it.
/// </param>
public sealed record GenreNormalizationResult(
    IReadOnlyList<GenreTagObservation> Observations,
    IReadOnlyList<RemovedGenreTagValue> Removed);

/// <summary>
/// One value the shared normalization preferences removed from a file.
/// </summary>
/// <param name="Value">The value exactly as it was found in the file.</param>
/// <param name="InputField">The file field it was read from.</param>
/// <param name="Reason">Why it was removed, in terms a user can act on.</param>
/// <param name="Origin">Whether AutoTag wrote it or it was already in the file.</param>
public sealed record RemovedGenreTagValue(
    string Value,
    PersonalGenreTaxonKind InputField,
    string Reason,
    GenreObservationOrigin Origin = GenreObservationOrigin.PostPlatform);
