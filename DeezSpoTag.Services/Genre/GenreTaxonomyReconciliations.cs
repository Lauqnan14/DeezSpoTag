namespace DeezSpoTag.Services.Genre;

/// <summary>
/// Dispositions for labels that name more than one semantic identity.
///
/// A shared visible label is not a reason to delete a researched Genre or Style.
/// Context, Scene and Language can share that label with a musical identity.
/// Each identity keeps its own id. The input field chooses among them. A lookup
/// with no field does not.
///
/// A researched Genre or Style may be removed only when the research itself
/// shows that the classification is wrong. A second, non-musical meaning is not
/// that evidence. Removing a row requires the original source, contradictory
/// evidence, a written disposition, a taxonomy version change, and a regression
/// test.
/// </summary>
public static class GenreTaxonomyReconciliations
{
    public const string VocabularyVersion = "researched-master-2026-10-06-v1";

    public sealed record BuiltInResearchConflict(
        string LookupKey,
        string BuiltInId,
        string BuiltInName,
        PersonalGenreTaxonKind BuiltInKind,
        string ResearchedId,
        string ResearchedName,
        string ResearchedKind,
        string Disposition);

    /// <summary>
    /// One overloaded label whose identities are both kept, with the rule that
    /// chooses between them.
    /// </summary>
    public sealed record IdentityReconciliation(
        string LookupKey,
        string Term,
        string ResearchKind,
        string RuntimeDisposition,
        string Reason,
        string Evidence,
        string Version,
        string Date);

    public static IReadOnlyList<BuiltInResearchConflict> BuiltInShadows { get; } = [];

    /// <summary>
    /// Overloaded labels with a written resolution rule. These records do not
    /// remove a musical row.
    /// </summary>
    public static IReadOnlyList<IdentityReconciliation> Reconciliations { get; } =
    [
        new(
            "bollywood",
            "Bollywood",
            nameof(PersonalGenreTaxonKind.Style),
            "Keep Context bollywood and Style bollywood-style. filmi stays a separate Genre.",
            "STYLE, SUBGENRE, SUBSTYLE and GENRE select Style bollywood-style. CONTEXT selects Context bollywood. A lookup with no field stays ambiguous.",
            "AllMusic publishes Bollywood as its own film-music subgenre (ma0000002537). The Hindi film industry is a separate context. filmi is a related repertoire, not a duplicate of that Style row.",
            VocabularyVersion,
            "2026-10-11"),
        new(
            "opm",
            "OPM",
            nameof(PersonalGenreTaxonKind.Genre),
            "Keep Context opm and Genre opm-genre. Original Pilipino Music is an alias of the genre.",
            "GENRE, STYLE, SUBGENRE and SUBSTYLE select Genre opm-genre. CONTEXT selects Context opm. A lookup with no field stays ambiguous.",
            "MusicBrainz publishes opm as a genre related to Pop. The built-in Context remains the wider Filipino popular-music umbrella. The acronym is the musical term; Original Pilipino Music expands it.",
            VocabularyVersion,
            "2026-10-11"),
        new(
            "swahili",
            "Swahili",
            nameof(PersonalGenreTaxonKind.Style),
            "Keep Language swahili and Style swahili-style. Taarab stays its own Genre.",
            "LANGUAGE selects Language swahili. STYLE, SUBGENRE, SUBSTYLE and GENRE select Style swahili-style. A lookup with no field stays ambiguous.",
            "The researched master publishes Swahili as a Style in addition to the built-in language. Being a language does not erase that Style. Taarab is a different term.",
            VocabularyVersion,
            "2026-10-11")
    ];

    /// <summary>
    /// Selects one identity when the input field identifies it.
    /// </summary>
    /// <remarks>
    /// Returns false when the label is not overloaded. When it is overloaded,
    /// <paramref name="selected"/> is set only when the field picks exactly one
    /// candidate. A Genre or Style field with exactly one musical candidate uses
    /// that musical identity, which is how GENRE=Bollywood becomes the Style and
    /// STYLE=OPM becomes the Genre.
    /// </remarks>
    public static bool TrySelect(
        PersonalGenreTaxonKind inputField,
        IReadOnlyList<PersonalGenreTaxon> candidates,
        out PersonalGenreTaxon? selected,
        out string reason)
    {
        selected = null;
        var distinct = candidates
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (distinct.Count < 2)
        {
            reason = string.Empty;
            return false;
        }

        var field = inputField == PersonalGenreTaxonKind.Substyle
            ? PersonalGenreTaxonKind.Style
            : inputField;
        var sameKind = distinct.Where(item => item.Kind == field).ToList();
        if (sameKind.Count == 1)
        {
            selected = sameKind[0];
            reason = FieldReason(field, selected, distinct);
            return true;
        }

        if (sameKind.Count > 1)
        {
            reason = AmbiguousReason(distinct, $"More than one {field} identity uses this label.");
            return true;
        }

        if (field is PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style)
        {
            var musical = distinct
                .Where(item => item.Kind is PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style)
                .ToList();
            if (musical.Count == 1)
            {
                selected = musical[0];
                reason = FieldReason(field, selected, distinct);
                return true;
            }
        }

        reason = AmbiguousReason(distinct, "No input field selected one identity.");
        return true;
    }

    public static DiscrepancyReport BuildDiscrepancyReport(
        PersonalGenreCatalog runtime,
        ResearchedGenreCatalog researched)
    {
        var suppressed = new List<string>();
        var conflicts = new List<string>();
        foreach (var term in researched.Terms)
        {
            if (term.Kind is not (nameof(PersonalGenreTaxonKind.Genre) or nameof(PersonalGenreTaxonKind.Style)))
            {
                continue;
            }

            var musical = runtime.Taxa
                .Where(item => item.Kind is PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style)
                .Where(item => Key(item.Name) == Key(term.Name) || Key(item.Id) == Key(term.Id))
                .ToList();
            if (musical.Count == 0)
            {
                suppressed.Add(term.Id);
                continue;
            }

            if (!musical.Any(item => string.Equals(item.Kind.ToString(), term.Kind, StringComparison.Ordinal)))
            {
                conflicts.Add(term.Id);
            }
        }

        var legitimate = new List<OverloadedLabel>();
        var invalid = new List<string>();
        foreach (var collision in runtime.LookupCollisions())
        {
            var kinds = collision.Members.Select(member => member.Taxon.Kind).Distinct().ToList();
            var description = collision.Key + ": " + string.Join(", ", collision.Members.Select(member =>
                member.Taxon.Id + " [" + member.Taxon.Kind + "]"));
            if (kinds.Count < 2 || collision.Members.GroupBy(member => member.Taxon.Kind).Any(group => group.Count() > 1))
            {
                invalid.Add(description);
                continue;
            }

            var reconciliation = Reconciliations.FirstOrDefault(item => item.LookupKey == collision.Key);
            legitimate.Add(new OverloadedLabel(
                collision.Key,
                collision.Members[0].Taxon.Name,
                collision.Members.Select(member => member.Taxon.Id + " [" + member.Taxon.Kind + "]").ToList(),
                reconciliation?.Reason
                    ?? "Input field selects the candidate of that kind. A Genre or Style field with one musical candidate uses that musical identity. A lookup with no field stays ambiguous."));
        }

        var aliasCollisions = FindAliasCollisions(runtime);
        return new DiscrepancyReport(
            suppressed,
            conflicts,
            legitimate,
            invalid,
            aliasCollisions,
            Reconciliations.Count);
    }

    public readonly record struct DiscrepancyReport(
        IReadOnlyList<string> SuppressedResearchedMusicalIdentities,
        IReadOnlyList<string> UnexplainedMusicalKindConflicts,
        IReadOnlyList<OverloadedLabel> LegitimateOverloadedLabels,
        IReadOnlyList<string> InvalidCollisions,
        IReadOnlyList<string> AliasCollisions,
        int ExplicitReconciliations);

    public sealed record OverloadedLabel(
        string LookupKey,
        string Label,
        IReadOnlyList<string> Identities,
        string ResolutionRule);

    /// <summary>
    /// Fails catalog construction when a researched Genre or Style is absent
    /// from the runtime musical vocabulary.
    /// </summary>
    public static void EnsureNoSuppressedMusicalIdentities(
        IReadOnlyList<PersonalGenreTaxon> runtime,
        ResearchedGenreCatalog researched)
    {
        var suppressed = SuppressedAgainst(runtime, researched);
        if (suppressed.Count > 0)
        {
            throw new InvalidOperationException(
                "Suppressed researched musical identities: " + string.Join(", ", suppressed) + ".");
        }
    }

    private static IReadOnlyList<string> SuppressedAgainst(
        IReadOnlyList<PersonalGenreTaxon> runtime,
        ResearchedGenreCatalog researched)
    {
        var musical = runtime
            .Where(item => item.Kind is PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style)
            .ToList();
        return researched.Terms
            .Where(term => term.Kind is nameof(PersonalGenreTaxonKind.Genre) or nameof(PersonalGenreTaxonKind.Style))
            .Where(term => !musical.Any(item => Key(item.Name) == Key(term.Name) || Key(item.Id) == Key(term.Id)))
            .Select(term => term.Id)
            .ToList();
    }

    private static IReadOnlyList<string> FindAliasCollisions(PersonalGenreCatalog runtime)
    {
        var found = new List<string>();
        foreach (var taxon in runtime.Taxa)
        {
            if (taxon.Aliases is null)
            {
                continue;
            }

            foreach (var key in taxon.Aliases.Select(Key).Where(key => key.Length > 0))
            {
                var owners = runtime.Taxa
                    .Where(item => !string.Equals(item.Id, taxon.Id, StringComparison.OrdinalIgnoreCase))
                    .Where(item => Key(item.Name) == key || Key(item.Id) == key)
                    .Select(item => item.Id + " [" + item.Kind + "]")
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (owners.Count == 0)
                {
                    continue;
                }

                found.Add(key + ": alias of " + taxon.Id + " [" + taxon.Kind + "] shares a label with " + string.Join(", ", owners));
            }
        }

        return found
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
    }

    private static string FieldReason(
        PersonalGenreTaxonKind field,
        PersonalGenreTaxon selected,
        IReadOnlyList<PersonalGenreTaxon> distinct)
        => "Input field " + field + " selects " + selected.Id + " [" + selected.Kind + "]. Other identities: "
           + string.Join(", ", distinct.Where(item => !string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
               .Select(item => item.Id + " [" + item.Kind + "]")) + ".";

    private static string AmbiguousReason(IReadOnlyList<PersonalGenreTaxon> distinct, string because)
        => because + " Candidates: " + string.Join(", ", distinct.Select(item => item.Id + " [" + item.Kind + "]"))
           + ". Lookup order is not a classification.";

    private static string Key(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Trim().Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
