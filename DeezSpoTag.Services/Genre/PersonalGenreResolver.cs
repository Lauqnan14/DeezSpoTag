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
        var suppressedEvidenceIndexes = new HashSet<int>();
        var classifiedEvidenceIndexes = new HashSet<int>();
        var decisions = new List<PersonalGenreEvidenceDecision>(evidence.Count);

        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            var attempt = ResolveEvidence(item, mappings, rules, catalog);
            decisions.Add(new PersonalGenreEvidenceDecision(
                index,
                item.Source,
                item.RawValue,
                item.CanonicalValue,
                attempt.Outcome,
                attempt.Taxon?.Id,
                attempt.Reason));

            if (attempt.SuppressFallback)
            {
                suppressedEvidenceIndexes.Add(index);
            }

            if (attempt.Taxon is null)
            {
                continue;
            }

            classifiedEvidenceIndexes.Add(index);
            if (attempt.AppliedRuleId.HasValue)
            {
                appliedRuleIds.Add(attempt.AppliedRuleId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var cap = GetAuthorityCap(item);
            var contribution = Math.Min(Math.Clamp(item.Weight, 0d, 1d), cap);
            if (attempt.AppliedRuleId.HasValue)
            {
                contribution = Math.Max(contribution, UserRuleAuthority);
            }

            candidates.Add(new Candidate(
                attempt.Taxon,
                contribution,
                NormalizeSource(item.Source),
                index,
                attempt.AppliedRuleId.HasValue));
        }

        if (settings.PreserveProviderFallback)
        {
            var fallbackIndexes = AppendProviderFallbacks(
                candidates,
                evidence,
                classifiedEvidenceIndexes,
                suppressedEvidenceIndexes);
            if (fallbackIndexes.Count > 0)
            {
                decisions = decisions
                    .Select(item => fallbackIndexes.Contains(item.EvidenceIndex)
                        ? item with
                        {
                            Outcome = "provider_fallback",
                            TaxonId = candidates.First(candidate => candidate.EvidenceIndex == item.EvidenceIndex).Taxon.Id,
                            Reason = "No canonical mapping matched; retained as a low-authority provider genre fallback."
                        }
                        : item)
                    .ToList();
            }
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
            decisions,
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

    private static ResolutionAttempt ResolveEvidence(
        PersonalGenreEvidence evidence,
        IReadOnlyList<PersonalGenreMapping> mappings,
        IReadOnlyList<PersonalGenreRule> rules,
        PersonalGenreCatalog catalog)
    {
        var normalizedRaw = PersonalGenreTaxonomy.Normalize(evidence.RawValue);
        var normalizedCanonical = PersonalGenreTaxonomy.Normalize(evidence.CanonicalValue);
        if (normalizedRaw.Length == 0 && normalizedCanonical.Length == 0)
        {
            return new ResolutionAttempt(
                null,
                null,
                "unmapped",
                "Evidence contained no usable raw or canonical value.",
                SuppressFallback: true);
        }

        var normalizedSource = NormalizeSource(evidence.Source);
        var rule = rules
            .Where(item => item.Enabled)
            .Where(item =>
            {
                var match = PersonalGenreTaxonomy.Normalize(item.MatchValue);
                return match == normalizedRaw || (normalizedCanonical.Length > 0 && match == normalizedCanonical);
            })
            .Where(item => SourceMatches(item.Source, normalizedSource))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Id)
            .FirstOrDefault();

        if (rule is not null && catalog.TryGetById(rule.TargetTaxonId, out var ruleTaxon))
        {
            return new ResolutionAttempt(
                ruleTaxon,
                rule.Id,
                "classified",
                $"Matched user rule {rule.Id}.",
                SuppressFallback: true);
        }

        var mapping = mappings
            .Where(item => item.Enabled)
            .Where(item =>
            {
                var match = PersonalGenreTaxonomy.Normalize(item.MatchValue);
                return match == normalizedRaw || (normalizedCanonical.Length > 0 && match == normalizedCanonical);
            })
            .Where(item => SourceMatches(item.Source, normalizedSource))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Id)
            .FirstOrDefault();

        if (mapping is not null)
        {
            if (mapping.Action == PersonalGenreMappingAction.Ignore)
            {
                return new ResolutionAttempt(
                    null,
                    null,
                    "ignored",
                    $"Provider mapping {mapping.Id} explicitly ignores this value.",
                    SuppressFallback: true);
            }

            if (mapping.Action == PersonalGenreMappingAction.Ambiguous)
            {
                return new ResolutionAttempt(
                    null,
                    null,
                    "ambiguous",
                    $"Provider mapping {mapping.Id} marks this value as ambiguous.",
                    SuppressFallback: true);
            }

            if (catalog.TryGetById(mapping.TargetTaxonId, out var mappedTaxon))
            {
                if (mapping.Action == PersonalGenreMappingAction.ContextOnly)
                {
                    mappedTaxon = mappedTaxon with
                    {
                        Kind = PersonalGenreTaxonKind.Context,
                        ContextOnly = true
                    };
                    return new ResolutionAttempt(
                        mappedTaxon,
                        null,
                        "context_only",
                        $"Provider mapping {mapping.Id} restricts this evidence to Context.",
                        SuppressFallback: true);
                }

                return new ResolutionAttempt(
                    mappedTaxon,
                    null,
                    "classified",
                    $"Matched provider mapping {mapping.Id}.",
                    SuppressFallback: true);
            }
        }

        if (!string.IsNullOrWhiteSpace(evidence.CanonicalValue)
            && catalog.TryMatch(evidence.CanonicalValue, out var canonicalTaxon))
        {
            return new ResolutionAttempt(
                canonicalTaxon,
                null,
                canonicalTaxon.ContextOnly ? "context_only" : "classified",
                "Matched canonical evidence value in the Personal Genre taxonomy.",
                SuppressFallback: true);
        }

        if (catalog.TryMatch(evidence.RawValue, out var rawTaxon))
        {
            return new ResolutionAttempt(
                rawTaxon,
                null,
                rawTaxon.ContextOnly ? "context_only" : "classified",
                "Matched raw provider value in the Personal Genre taxonomy.",
                SuppressFallback: true);
        }

        return new ResolutionAttempt(
            null,
            null,
            "unmapped",
            "No user rule, provider mapping, or canonical taxonomy term matched this evidence.",
            SuppressFallback: false);
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

            var sources = taxonGroup
                .Select(item => item.ProviderKey)
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var confidence = Math.Round(1d - remaining, 3);
            output.Add(new PersonalGenreClassification(
                taxon.Id,
                taxon.Name,
                taxon.Kind,
                confidence,
                sources,
                UserLocked: false,
                Status: "suggested",
                EvidenceState: sources.Length >= 2 ? "agreement" : "single_source"));
        }

        return output;
    }

    private static List<PersonalGenreClassification> ApplyLocks(
        IReadOnlyList<PersonalGenreClassification> classifications,
        IReadOnlyList<PersonalGenreLock> locks,
        PersonalGenreCatalog catalog)
    {
        var effectiveLocks = GetMostSpecificLocks(locks, catalog);
        if (effectiveLocks.Count == 0)
        {
            return classifications.ToList();
        }

        var lockedKinds = effectiveLocks
            .Select(item => item.Taxon.Kind)
            .ToHashSet();

        var output = classifications
            .Where(item => !lockedKinds.Contains(item.Kind))
            .ToList();

        foreach (var item in effectiveLocks
            .GroupBy(item => item.Taxon.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            var scope = NormalizeLockScope(item.Lock.ScopeType);
            output.Add(new PersonalGenreClassification(
                item.Taxon.Id,
                item.Taxon.Name,
                item.Taxon.Kind,
                UserLockAuthority,
                [$"user-lock:{scope}"],
                UserLocked: true,
                Status: "locked",
                EvidenceState: "user_override"));
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

        var hasGenreLock = GetMostSpecificLocks(locks, catalog)
            .Any(item => item.Taxon.Kind == PersonalGenreTaxonKind.Genre);
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
                        .ToArray(),
                    UserLocked: false,
                    Status: "suggested",
                    EvidenceState: classification.EvidenceState));
            }
        }

        return output;
    }

    private static List<(PersonalGenreLock Lock, PersonalGenreTaxon Taxon)> GetMostSpecificLocks(
        IReadOnlyList<PersonalGenreLock> locks,
        PersonalGenreCatalog catalog)
    {
        var valid = locks
            .Where(item => item.Enabled)
            .Select(item => catalog.TryGetById(item.TaxonId, out var taxon)
                ? (Lock: item, Taxon: taxon)
                : ((PersonalGenreLock Lock, PersonalGenreTaxon Taxon)?)null)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToList();

        if (valid.Count == 0)
        {
            return [];
        }

        var output = new List<(PersonalGenreLock Lock, PersonalGenreTaxon Taxon)>();
        foreach (var kindGroup in valid.GroupBy(item => item.Taxon.Kind))
        {
            var highestRank = kindGroup.Max(item => LockScopeRank(item.Lock.ScopeType));
            output.AddRange(kindGroup.Where(item => LockScopeRank(item.Lock.ScopeType) == highestRank));
        }

        return output;
    }

    private static int LockScopeRank(string? scopeType)
        => NormalizeLockScope(scopeType) switch
        {
            "track" => 3,
            "album" => 2,
            "artist" => 1,
            _ => 0
        };

    private static string NormalizeLockScope(string? scopeType)
    {
        var normalized = (scopeType ?? "track").Trim().ToLowerInvariant();
        return normalized is "artist" or "album" or "track" ? normalized : "track";
    }

    private static HashSet<int> AppendProviderFallbacks(
        ICollection<Candidate> candidates,
        IReadOnlyList<PersonalGenreEvidence> evidence,
        IReadOnlySet<int> classifiedEvidenceIndexes,
        IReadOnlySet<int> suppressedEvidenceIndexes)
    {
        var fallbackIndexes = new HashSet<int>();
        for (var index = 0; index < evidence.Count; index++)
        {
            if (classifiedEvidenceIndexes.Contains(index) || suppressedEvidenceIndexes.Contains(index))
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
            fallbackIndexes.Add(index);
        }

        return fallbackIndexes;
    }

    private static bool SourceMatches(string? configuredSource, string normalizedSource)
        => string.IsNullOrWhiteSpace(configuredSource)
           || string.Equals(NormalizeSource(configuredSource), normalizedSource, StringComparison.Ordinal);

    private static string NormalizeSource(string? source)
        => (source ?? string.Empty).Trim().ToLowerInvariant();

    private sealed record ResolutionAttempt(
        PersonalGenreTaxon? Taxon,
        long? AppliedRuleId,
        string Outcome,
        string Reason,
        bool SuppressFallback);

    private sealed record Candidate(
        PersonalGenreTaxon Taxon,
        double Contribution,
        string ProviderKey,
        int EvidenceIndex,
        bool FromUserRule);
}
