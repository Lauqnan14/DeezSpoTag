namespace DeezSpoTag.Services.Genre;

/// <summary>Exact predicate evaluation over qualified canonical facts; no I/O or write-back.</summary>
public static class StyleConstructionEvaluator
{
    public static StyleConstructionPreview Evaluate(StyleConstructionEvidenceSnapshot snapshot,
        StyleConstructionRuleCatalog rules, PersonalGenreCatalog catalog)
    {
        if (snapshot.SchemaVersion != 1) throw new ArgumentException("Unsupported construction evidence schema.", nameof(snapshot));
        var output = new List<StyleConstructionDecision>();
        foreach (var rule in rules.Candidates.OrderBy(r => r.RuleId, StringComparer.Ordinal))
        {
            if (!catalog.TryGetById(rule.TargetStyleId, out var target) || target.Kind != PersonalGenreTaxonKind.Style)
                throw new ArgumentException("Construction target no longer matches its validated canonical Style.", nameof(catalog));
            if (!rule.Enabled || !rule.InferenceAllowed || rule.ResearchDisposition == ConstructionResearchDisposition.RecognitionOnly)
            {
                output.Add(new(target.Id, target.Name, rule.RuleId, rule.Version, StyleConstructionOutcome.RecognitionOnly,
                    [], [], [], [], [], rule.Research.SufficientEvidence, rule.Research, rule.ResearchSources));
                continue;
            }

            var satisfied = new List<string>();
            var missing = new List<string>();
            var conflicts = new List<StyleConstructionSemanticFact>();
            var used = new List<StyleConstructionSemanticFact>();
            var rejected = new List<StyleConstructionRejectedAlternative>();

            IReadOnlyList<StyleConstructionSemanticFact> Match(StyleConstructionPredicateGroup group)
            {
                var matches = new List<StyleConstructionSemanticFact>();
                foreach (var alternative in group.AnyOf.OrderBy(a => a.CanonicalId, StringComparer.Ordinal).ThenBy(a => a.Kind).ThenBy(a => a.ArtistRole))
                {
                    // Invalid facts are also reported so a malformed ID/kind cannot disappear silently.
                    var candidates = snapshot.SemanticFacts.OrderBy(f => f.FactId, StringComparer.Ordinal).ToArray();
                    foreach (var fact in candidates)
                    {
                        var reason = IneligibleReason(fact, snapshot, rule, catalog);
                        if (reason is null && (fact.CanonicalId != alternative.CanonicalId || fact.Kind != alternative.Kind))
                            continue;
                        if (reason is null && alternative.ArtistRole.HasValue && fact.ArtistRole != alternative.ArtistRole)
                            reason = "Artist role does not satisfy the predicate.";
                        if (reason is null && alternative.ArtistRole.HasValue && !HasArtistBinding(fact))
                            reason = "Artist identity and binding are unavailable.";
                        if (reason is not null)
                        {
                            rejected.Add(new(group.PredicateId, alternative.CanonicalId, fact.FactId, reason));
                            continue;
                        }
                        matches.Add(fact);
                    }
                    if (!matches.Any(f => f.CanonicalId == alternative.CanonicalId && f.Kind == alternative.Kind))
                        rejected.Add(new(group.PredicateId, alternative.CanonicalId, null, "No eligible fact satisfies this alternative."));
                }
                return OrderedFacts(matches);
            }

            foreach (var group in rule.RequiredAll.OrderBy(g => g.PredicateId, StringComparer.Ordinal))
            {
                var matches = Match(group);
                if (matches.Count == 0) missing.Add(group.PredicateId);
                else { satisfied.Add(group.PredicateId); used.AddRange(matches); }
            }
            foreach (var group in rule.Supporting.OrderBy(g => g.PredicateId, StringComparer.Ordinal))
            {
                var matches = Match(group);
                if (matches.Count > 0) { satisfied.Add(group.PredicateId); used.AddRange(matches); }
            }
            foreach (var group in rule.ForbiddenAny.OrderBy(g => g.PredicateId, StringComparer.Ordinal))
                conflicts.AddRange(Match(group));

            var hasStyleAuthority = snapshot.UserAuthority.StyleLocks.Any(l => l.Enabled && l.TrackId == snapshot.TrackId
                && catalog.TryGetById(l.TaxonId, out var term) && term.Kind == PersonalGenreTaxonKind.Style);
            var outcome = conflicts.Count > 0 ? StyleConstructionOutcome.Conflicted
                : missing.Count > 0 ? StyleConstructionOutcome.MissingEvidence
                : hasStyleAuthority ? StyleConstructionOutcome.SuppressedByUserAuthority
                : StyleConstructionOutcome.Qualified;
            var explanation = outcome switch
            {
                StyleConstructionOutcome.Conflicted => "Forbidden evidence contradicts this rule. Missing requirements, if any, remain listed.",
                StyleConstructionOutcome.MissingEvidence => "Required evidence is unavailable: " + string.Join(", ", missing) + ".",
                StyleConstructionOutcome.SuppressedByUserAuthority => "Required evidence qualifies, but the existing Style lock is final user authority.",
                _ => "Every required predicate is satisfied and no forbidden evidence was found. Preview only; no Style is constructed."
            };
            output.Add(new(target.Id, target.Name, rule.RuleId, rule.Version, outcome,
                satisfied.Order(StringComparer.Ordinal).ToArray(), missing.Order(StringComparer.Ordinal).ToArray(),
                OrderedFacts(conflicts), OrderedFacts(used), rejected.Distinct().OrderBy(r => r.PredicateId, StringComparer.Ordinal)
                    .ThenBy(r => r.CanonicalId, StringComparer.Ordinal).ThenBy(r => r.FactId, StringComparer.Ordinal)
                    .ThenBy(r => r.Reason, StringComparer.Ordinal).ToArray(), explanation, rule.Research, rule.ResearchSources));
        }
        return new(snapshot, rules.DatasetVersion, output.ToArray());
    }

    private static string? IneligibleReason(StyleConstructionSemanticFact fact, StyleConstructionEvidenceSnapshot snapshot,
        StyleConstructionRule rule, PersonalGenreCatalog catalog)
    {
        if (!catalog.TryGetById(fact.CanonicalId, out var term) || term.Kind != fact.Kind || term.Name != fact.CanonicalValue)
            return "Fact does not match a canonical identity, kind and value.";
        if (!Enum.IsDefined(fact.Origin) || !rule.AllowedEvidenceOrigins.Contains(fact.Origin))
            return "Evidence origin is not allowed by this rule.";
        if (!Enum.IsDefined(fact.Scope) || !rule.AllowedScopes.Contains(fact.Scope))
            return "Evidence scope is not allowed by this rule.";
        if (fact.KnownAt == default || fact.KnownAt > snapshot.SnapshotAt)
            return "Evidence was not known at the snapshot time.";
        if (string.IsNullOrWhiteSpace(fact.FactId) || string.IsNullOrWhiteSpace(fact.Source) || string.IsNullOrWhiteSpace(fact.ProvenanceReference))
            return "Evidence provenance is incomplete.";
        if (fact.Scope == ConstructionEvidenceScope.Artist && !HasArtistBinding(fact))
            return "Artist identity, role and binding must be proven.";
        if (fact.Origin == ConstructionEvidenceOrigin.ExplicitFile
            && (!new PersonalGenreCatalog().TryGetById(fact.CanonicalId, out var recognized) || recognized.Kind != fact.Kind))
            return "Custom canonical facts require explicit user mapping or rule authority.";
        return null;
    }

    private static bool HasArtistBinding(StyleConstructionSemanticFact fact)
        => fact.ArtistId is > 0 && fact.ArtistRole is ConstructionArtistRole.Main or ConstructionArtistRole.Featured
            && !string.IsNullOrWhiteSpace(fact.ArtistBindingReference);

    private static IReadOnlyList<StyleConstructionSemanticFact> OrderedFacts(IEnumerable<StyleConstructionSemanticFact> facts)
        => facts.DistinctBy(f => f.FactId).OrderBy(f => f.FactId, StringComparer.Ordinal).ToArray();
}
