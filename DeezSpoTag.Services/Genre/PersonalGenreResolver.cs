namespace DeezSpoTag.Services.Genre;

public static class PersonalGenreResolver
{
    private const double UserRuleAuthority = 0.98;
    private const double UserLockAuthority = 1.00;

    public static PersonalGenreResolution Resolve(
        IReadOnlyList<PersonalGenreEvidence>? evidence,
        IReadOnlyList<PersonalGenreMapping>? mappings = null,
        IReadOnlyList<PersonalGenreRule>? rules = null,
        IReadOnlyList<PersonalGenreLock>? locks = null,
        IReadOnlyList<PersonalGenreTaxon>? customTaxa = null,
        PersonalGenreSettings? settings = null)
    {
        settings ??= new PersonalGenreSettings();
        evidence ??= Array.Empty<PersonalGenreEvidence>();
        mappings ??= Array.Empty<PersonalGenreMapping>();
        rules ??= Array.Empty<PersonalGenreRule>();
        locks ??= Array.Empty<PersonalGenreLock>();

        var catalog = new PersonalGenreCatalog(customTaxa);
        var appliedRuleIds = new List<string>();
        var candidates = new List<Candidate>();
        var matchedEvidenceIndexes = new HashSet<int>();

        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            var match = ResolveEvidence(item, mappings, rules, catalog);
            if (match is null)
            {
                continue;
            }

            matchedEvidenceIndexes.Add(index);
            if (match.AppliedRuleId.HasValue)
            {
                appliedRuleIds.Add(match.AppliedRuleId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var cap = GetAuthorityCap(item);
            var contribution = Math.Min(Math.Clamp(item.Weight, 0d, 1d), cap);
            if (match.AppliedRuleId.HasValue)
            {
                contribution = Math.Max(contribution, UserRuleAuthority);
            }

            candidates.Add(new Candidate(
                match.Taxon,
                contribution,
                NormalizeSource(item.Source),
                index,
                match.AppliedRuleId.HasValue));
        }

        if (settings.PreserveProviderFallback)
        {
            AppendProviderFallbacks(candidates, evidence, matchedEvidenceIndexes);
        }

        var classifications = Fuse(candidates);
        classifications = ApplyLocks(classifications, locks, catalog);
        classifications = IncludeParentGenresIfRequested(classifications, locks, settings, catalog);

        var ordered = classifications
            .OrderByDescending(item => item.UserLocked)
            .ThenByDescending(item => item.Confidence)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var genres = ordered
            .Where(item => item.Kind == PersonalGenreTaxonKind.Genre)
            .Take(Math.Clamp(settings.MaxGenres, 1, 10))
            .Select(item => item.Name)
            .ToList();
        var styles = ordered
            .Where(item => item.Kind == PersonalGenreTaxonKind.Style)
            .Select(item => item.Name)
            .ToList();
        var substyles = ordered
            .Where(item => item.Kind == PersonalGenreTaxonKind.Substyle)
            .Select(item => item.Name)
            .ToList();
        var contexts = ordered
            .Where(item => item.Kind == PersonalGenreTaxonKind.Context)
            .Select(item => item.Name)
            .ToList();

        return new PersonalGenreResolution(
            genres.FirstOrDefault(),
            genres,
            styles,
            substyles,
            contexts,
            ordered,
            appliedRuleIds.Distinct(StringComparer.Ordinal).ToArray(),
            evidence,
            PersonalGenreTaxonomy.Version);
    }

    public static double GetAuthorityCap(PersonalGenreEvidence evidence)
    {
        var source = NormalizeSource(evidence.Source);
        var scope = NormalizeSource(evidence.Scope);

        if (source == "embedded")
        {
            return 0.95;
        }

        if (source == "discogs")
        {
            return evidence.Kind is PersonalGenreTaxonKind.Style or PersonalGenreTaxonKind.Substyle
                ? 0.90
                : 0.85;
        }

        if (source == "audiomack")
        {
            return scope switch
            {
                "editorial" => 0.82,
                "album" => 0.80,
                "artist" => 0.60,
                _ => 0.88
            };
        }

        if (source == "lastfm")
        {
            return scope == "artist" ? 0.50 : 0.72;
        }

        if (source == "spotify")
        {
            return 0.55;
        }

        if (source.StartsWith("essentia", StringComparison.Ordinal))
        {
            return 0.40;
        }

        if (source is "manual" or "user")
        {
            return 0.95;
        }

        if (source == "vibe-resolved")
        {
            return 0.35;
        }

        return 0.35;
    }

    private static MatchResult? ResolveEvidence(
        PersonalGenreEvidence evidence,
        IReadOnlyList<PersonalGenreMapping> mappings,
        IReadOnlyList<PersonalGenreRule> rules,
        PersonalGenreCatalog catalog)
    {
        var effectiveValue = string.IsNullOrWhiteSpace(evidence.CanonicalValue)
            ? evidence.RawValue
            : evidence.CanonicalValue!;
        var normalizedValue = PersonalGenreTaxonomy.Normalize(effectiveValue);
        if (normalizedValue.Length == 0)
        {
            return null;
        }

        var normalizedSource = NormalizeSource(evidence.Source);
        var rule = rules
            .Where(item => item.Enabled)
            .Where(item => PersonalGenreTaxonomy.Normalize(item.MatchValue) == normalizedValue)
            .Where(item => SourceMatches(item.Source, normalizedSource))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Id)
            .FirstOrDefault();

        if (rule is not null && catalog.TryGetById(rule.TargetTaxonId, out var ruleTaxon))
        {
            return new MatchResult(ruleTaxon, rule.Id);
        }

        var mapping = mappings
            .Where(item => item.Enabled)
            .Where(item => PersonalGenreTaxonomy.Normalize(item.MatchValue) == normalizedValue)
            .Where(item => SourceMatches(item.Source, normalizedSource))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Id)
            .FirstOrDefault();

        if (mapping is not null && catalog.TryGetById(mapping.TargetTaxonId, out var mappedTaxon))
        {
            return new MatchResult(mappedTaxon, null);
        }

        return catalog.TryMatch(effectiveValue, out var taxonomyTaxon)
            ? new MatchResult(taxonomyTaxon, null)
            : null;
    }

    private static List<PersonalGenreClassification> Fuse(IReadOnlyList<Candidate> candidates)
    {
        var output = new List<PersonalGenreClassification>();
        foreach (var taxonGroup in candidates.GroupBy(item => item.Taxon.Id, StringComparer.OrdinalIgnoreCase))
        {
            var taxon = taxonGroup.First().Taxon;
            var providerContributions = taxonGroup
                .GroupBy(item => item.ProviderKey, StringComparer.Ordinal)
                .Select(group => group.Max(item => item.Contribution))
                .ToList();

            var remaining = 1d;
            foreach (var contribution in providerContributions)
            {
                remaining *= 1d - Math.Clamp(contribution, 0d, 1d);
            }

            var confidence = Math.Round(1d - remaining, 3);
            output.Add(new PersonalGenreClassification(
                taxon.Id,
                taxon.Name,
                taxon.Kind,
                confidence,
                taxonGroup.Select(item => item.ProviderKey)
                    .Where(item => item.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()));
        }

        return output;
    }

    private static List<PersonalGenreClassification> ApplyLocks(
        IReadOnlyList<PersonalGenreClassification> classifications,
        IReadOnlyList<PersonalGenreLock> locks,
        PersonalGenreCatalog catalog)
    {
        var validLocks = locks
            .Where(item => item.Enabled)
            .Select(item => catalog.TryGetById(item.TaxonId, out var taxon)
                ? (Lock: item, Taxon: taxon)
                : ((PersonalGenreLock Lock, PersonalGenreTaxon Taxon)?)null)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToList();

        if (validLocks.Count == 0)
        {
            return classifications.ToList();
        }

        var lockedKinds = validLocks
            .Select(item => item.Taxon.Kind)
            .ToHashSet();

        var output = classifications
            .Where(item => !lockedKinds.Contains(item.Kind))
            .ToList();

        foreach (var item in validLocks
            .GroupBy(item => item.Taxon.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            output.Add(new PersonalGenreClassification(
                item.Taxon.Id,
                item.Taxon.Name,
                item.Taxon.Kind,
                UserLockAuthority,
                ["user-lock"],
                UserLocked: true));
        }

        return output;
    }

    private static List<PersonalGenreClassification> IncludeParentGenresIfRequested(
        IReadOnlyList<PersonalGenreClassification> classifications,
        IReadOnlyList<PersonalGenreLock> locks,
        PersonalGenreSettings settings,
        PersonalGenreCatalog catalog)
    {
        if (!settings.IncludeParentGenres)
        {
            return classifications.ToList();
        }

        var hasGenreLock = locks.Any(item =>
            item.Enabled
            && catalog.TryGetById(item.TaxonId, out var taxon)
            && taxon.Kind == PersonalGenreTaxonKind.Genre);
        if (hasGenreLock)
        {
            return classifications.ToList();
        }

        var output = classifications.ToList();
        foreach (var classification in classifications
            .Where(item => item.Kind is PersonalGenreTaxonKind.Style or PersonalGenreTaxonKind.Substyle))
        {
            if (!catalog.TryGetById(classification.TaxonId, out var taxon)
                || taxon.ParentIds is not { Count: > 0 })
            {
                continue;
            }

            foreach (var parentId in taxon.ParentIds)
            {
                if (!catalog.TryGetById(parentId, out var parent)
                    || parent.Kind != PersonalGenreTaxonKind.Genre
                    || output.Any(item => string.Equals(item.TaxonId, parent.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                output.Add(new PersonalGenreClassification(
                    parent.Id,
                    parent.Name,
                    parent.Kind,
                    Math.Round(classification.Confidence * 0.95, 3),
                    classification.Sources
                        .Append("taxonomy-parent")
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()));
            }
        }

        return output;
    }

    private static void AppendProviderFallbacks(
        ICollection<Candidate> candidates,
        IReadOnlyList<PersonalGenreEvidence> evidence,
        IReadOnlySet<int> matchedEvidenceIndexes)
    {
        for (var index = 0; index < evidence.Count; index++)
        {
            if (matchedEvidenceIndexes.Contains(index))
            {
                continue;
            }

            var item = evidence[index];
            if (item.Kind != PersonalGenreTaxonKind.Genre || string.IsNullOrWhiteSpace(item.RawValue))
            {
                continue;
            }

            var raw = item.RawValue.Trim();
            var providerKey = NormalizeSource(item.Source);
            var fallbackTaxon = new PersonalGenreTaxon(
                $"provider:{providerKey}:{PersonalGenreTaxonomy.Normalize(raw)}",
                raw,
                PersonalGenreTaxonKind.Genre);

            candidates.Add(new Candidate(
                fallbackTaxon,
                Math.Min(GetAuthorityCap(item), 0.20),
                providerKey,
                index,
                FromUserRule: false));
        }
    }

    private static bool SourceMatches(string? configuredSource, string normalizedSource)
        => string.IsNullOrWhiteSpace(configuredSource)
           || string.Equals(NormalizeSource(configuredSource), normalizedSource, StringComparison.Ordinal);

    private static string NormalizeSource(string? source)
        => (source ?? string.Empty).Trim().ToLowerInvariant();

    private sealed record MatchResult(PersonalGenreTaxon Taxon, long? AppliedRuleId);

    private sealed record Candidate(
        PersonalGenreTaxon Taxon,
        double Contribution,
        string ProviderKey,
        int Order,
        bool FromUserRule);
}
