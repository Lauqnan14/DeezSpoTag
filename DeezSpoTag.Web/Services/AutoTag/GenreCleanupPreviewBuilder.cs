namespace DeezSpoTag.Web.Services.AutoTag;

using DeezSpoTag.Services.Genre;

/// <summary>
/// Turns a resolution into the inspectable before-and-after a user can review.
/// </summary>
/// <remarks>
/// <para>
/// This lives beside the writer rather than beside the model because it has to apply
/// exactly the same display formatting the writer applies. If the two formatted
/// independently they could drift, and the preview would stop describing the write,
/// which would make it useless as a review surface.
/// </para>
/// <para>
/// It is a projection over an already-computed resolution: it reads nothing, opens
/// no file and produces no side effect, which is what makes it safe to call before a
/// write and safe to call on its own.
/// </para>
/// </remarks>
internal static class GenreCleanupPreviewBuilder
{
    public static GenreCleanupPreview Build(
        PersonalGenreResolution resolution,
        IReadOnlyList<GenreTagObservation> beforeObservations,
        IReadOnlyList<RemovedGenreTagValue> removedByNormalization)
    {
        var beforeGenres = ValuesOf(beforeObservations, PersonalGenreTaxonKind.Genre);
        var beforeStyles = ValuesOf(beforeObservations, PersonalGenreTaxonKind.Style);

        var decisions = new List<GenreCleanupDecision>();

        foreach (var removal in removedByNormalization)
        {
            decisions.Add(new GenreCleanupDecision(
                removal.Value,
                null,
                null,
                removal.InputField,
                null,
                GenreCleanupAction.Remove,
                removal.Reason));
        }

        foreach (var decision in resolution.Decisions)
        {
            if (decision.Outcome == "blocked")
            {
                // Already reported above from the removal that caused it.
                continue;
            }

            decisions.Add(ToDecision(resolution, decision));
        }

        // The after-lists are formatted exactly as the writer will format them, so
        // what the preview shows is what would be written.
        var plan = GenreSemanticTagIo.PlanFields(resolution);
        var afterGenres = PlanOrEmpty(plan, PersonalGenreTaxonKind.Genre);
        var afterStyles = PlanOrEmpty(plan, PersonalGenreTaxonKind.Style);

        return new GenreCleanupPreview(
            beforeGenres,
            afterGenres,
            beforeStyles,
            afterStyles,
            decisions,
            decisions.Where(item => item.Action == GenreCleanupAction.Move)
                .Select(DescribeMove)
                .ToList(),
            decisions.Where(item => item.Action == GenreCleanupAction.Canonicalize)
                .Select(item => $"{item.OriginalValue} -> {item.CanonicalValue}")
                .ToList(),
            decisions.Where(item => item.Action == GenreCleanupAction.Remove)
                .Select(item => $"{item.OriginalValue} -> {item.Reason}")
                .ToList(),
            decisions.Where(item => item.Action is GenreCleanupAction.PreserveUnknown or GenreCleanupAction.Excluded)
                .Select(item => $"{item.OriginalValue} ({item.OriginalField})")
                .ToList(),
            ResearchedGenreCatalog.Current.Version,
            decisions.Any(item => item.Action != GenreCleanupAction.Keep));
    }

    private static List<string> PlanOrEmpty(
        IReadOnlyDictionary<PersonalGenreTaxonKind, List<string>> plan,
        PersonalGenreTaxonKind field)
        => plan.TryGetValue(field, out var values) ? values.ToList() : [];

    private static List<string> ValuesOf(
        IReadOnlyList<GenreTagObservation> observations,
        PersonalGenreTaxonKind field)
        => observations
            .Where(item => item.InputField == field)
            .Select(item => item.RawValue)
            .ToList();

    private static string DescribeMove(GenreCleanupDecision decision)
        => $"{decision.OriginalField}: {decision.OriginalValue} -> {decision.FinalField}: {decision.CanonicalValue}";

    private static GenreCleanupDecision ToDecision(
        PersonalGenreResolution resolution,
        PersonalGenreEvidenceDecision decision)
    {
        // The dimension a value ends up in is decided by what it was recognised as,
        // not by the field it was found in, which is what makes a correction visible.
        var finalField = decision.CanonicalValue is not null
            ? DimensionOf(resolution, decision.CanonicalValue)
            : decision.InputField;

        if (decision.Outcome is "preserved_unmapped" or "preserved_excluded" or "ambiguous")
        {
            return new GenreCleanupDecision(
                decision.RawValue,
                decision.NormalizedValue,
                decision.CanonicalValue,
                decision.InputField,
                decision.InputField,
                decision.Outcome == "preserved_excluded"
                    ? GenreCleanupAction.Excluded
                    : GenreCleanupAction.PreserveUnknown,
                decision.Reason);
        }

        if (decision.CanonicalValue is null)
        {
            // A resolved value is never preserved, so reaching here means the value was
            // dropped by an explicit user instruction.
            return new GenreCleanupDecision(
                decision.RawValue,
                decision.NormalizedValue,
                null,
                decision.InputField,
                null,
                GenreCleanupAction.Remove,
                decision.Reason);
        }

        var changedSpelling = decision.NormalizedValue is not null
            || !string.Equals(decision.RawValue, decision.CanonicalValue, StringComparison.Ordinal);
        var movedField = finalField != decision.InputField;

        // A value that is already spelled canonically and already sits in the right
        // dimension is kept, not reported as a change. Without this every recognised
        // value would look like a canonicalization and the preview would claim to
        // have changes on a file that is already clean.
        var action = movedField
            ? GenreCleanupAction.Move
            : changedSpelling
                ? GenreCleanupAction.Canonicalize
                : GenreCleanupAction.Keep;

        return new GenreCleanupDecision(
            decision.RawValue,
            decision.NormalizedValue,
            decision.CanonicalValue,
            decision.InputField,
            finalField,
            action,
            movedField
                ? $"The researched taxonomy classifies '{decision.RawValue}' as {finalField}, not {decision.InputField}."
                : decision.Reason);
    }

    private static PersonalGenreTaxonKind? DimensionOf(PersonalGenreResolution resolution, string canonicalName)
        => resolution.Classifications.FirstOrDefault(item =>
                   string.Equals(item.Name, canonicalName, StringComparison.OrdinalIgnoreCase))
            ?.Kind;
}
