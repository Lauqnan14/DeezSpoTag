namespace DeezSpoTag.Services.Genre;

public static class PersonalGenreResolver
{
    public static PersonalGenreResolution Resolve(
        IReadOnlyList<PersonalGenreEvidence>? evidence,
        IReadOnlyList<PersonalGenreMapping>? mappings = null,
        IReadOnlyList<PersonalGenreRule>? rules = null,
        PersonalGenreSettings? settings = null)
    {
        settings ??= new PersonalGenreSettings();
        evidence ??= Array.Empty<PersonalGenreEvidence>();
        mappings ??= Array.Empty<PersonalGenreMapping>();
        rules ??= Array.Empty<PersonalGenreRule>();

        var appliedRuleIds = new List<string>();
        var resolved = new List<(PersonalGenreTaxon Taxon, double Weight, int Priority, int Order)>();
        var order = 0;

        foreach (var item in evidence)
        {
            var match = ResolveEvidence(item, mappings, rules, appliedRuleIds);
            if (match is null)
            {
                order++;
                continue;
            }

            resolved.Add((match.Value.Taxon, Math.Max(0d, item.Weight), match.Value.Priority, order++));
        }

        var genres = SelectByKind(resolved, PersonalGenreTaxonKind.Genre, settings.MaxGenres);
        var styles = SelectByKind(resolved, PersonalGenreTaxonKind.Style, int.MaxValue);
        var substyles = SelectByKind(resolved, PersonalGenreTaxonKind.Substyle, int.MaxValue);
        var contexts = SelectByKind(resolved, PersonalGenreTaxonKind.Context, int.MaxValue);

        if (settings.IncludeParentGenres)
        {
            foreach (var parent in resolved
                .Select(item => item.Taxon.ParentId)
                .Where(parentId => !string.IsNullOrWhiteSpace(parentId))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (PersonalGenreTaxonomy.TryGetById(parent, out var parentTaxon)
                    && parentTaxon.Kind == PersonalGenreTaxonKind.Genre
                    && !genres.Contains(parentTaxon.Name, StringComparer.OrdinalIgnoreCase)
                    && genres.Count < settings.MaxGenres)
                {
                    genres.Add(parentTaxon.Name);
                }
            }
        }

        return new PersonalGenreResolution(
            genres.FirstOrDefault(),
            genres,
            styles,
            substyles,
            contexts,
            appliedRuleIds.Distinct(StringComparer.Ordinal).ToArray(),
            evidence,
            PersonalGenreTaxonomy.Version);
    }

    private static (PersonalGenreTaxon Taxon, int Priority)? ResolveEvidence(
        PersonalGenreEvidence evidence,
        IReadOnlyList<PersonalGenreMapping> mappings,
        IReadOnlyList<PersonalGenreRule> rules,
        ICollection<string> appliedRuleIds)
    {
        var normalizedValue = PersonalGenreTaxonomy.Normalize(evidence.RawValue);
        var normalizedSource = NormalizeSource(evidence.Source);

        var rule = rules
            .Where(item => item.Enabled)
            .Where(item => PersonalGenreTaxonomy.Normalize(item.MatchValue) == normalizedValue)
            .Where(item => SourceMatches(item.Source, normalizedSource))
            .OrderByDescending(item => item.Priority)
            .FirstOrDefault();

        if (rule is not null && PersonalGenreTaxonomy.TryGetById(rule.TargetTaxonId, out var ruleTaxon))
        {
            appliedRuleIds.Add(rule.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return (ruleTaxon, rule.Priority);
        }

        var mapping = mappings
            .Where(item => item.Enabled)
            .Where(item => PersonalGenreTaxonomy.Normalize(item.MatchValue) == normalizedValue)
            .Where(item => SourceMatches(item.Source, normalizedSource))
            .OrderByDescending(item => item.Priority)
            .FirstOrDefault();

        if (mapping is not null && PersonalGenreTaxonomy.TryGetById(mapping.TargetTaxonId, out var mappedTaxon))
        {
            return (mappedTaxon, mapping.Priority);
        }

        if (PersonalGenreTaxonomy.TryMatch(evidence.RawValue, out var taxonomyTaxon))
        {
            if (evidence.Kind.HasValue && taxonomyTaxon.Kind != evidence.Kind.Value)
            {
                return (taxonomyTaxon, 10);
            }

            return (taxonomyTaxon, 20);
        }

        return null;
    }

    private static List<string> SelectByKind(
        IReadOnlyList<(PersonalGenreTaxon Taxon, double Weight, int Priority, int Order)> resolved,
        PersonalGenreTaxonKind kind,
        int limit)
    {
        return resolved
            .Where(item => item.Taxon.Kind == kind && !item.Taxon.ContextOnly || item.Taxon.Kind == kind)
            .GroupBy(item => item.Taxon.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Taxon = group.First().Taxon,
                Priority = group.Max(item => item.Priority),
                Weight = group.Sum(item => item.Weight),
                Order = group.Min(item => item.Order)
            })
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.Weight)
            .ThenBy(item => item.Order)
            .Select(item => item.Taxon.Name)
            .Take(limit)
            .ToList();
    }

    private static bool SourceMatches(string? configuredSource, string normalizedSource)
        => string.IsNullOrWhiteSpace(configuredSource)
           || string.Equals(NormalizeSource(configuredSource), normalizedSource, StringComparison.Ordinal);

    private static string NormalizeSource(string? source)
        => (source ?? string.Empty).Trim().ToLowerInvariant();
}
