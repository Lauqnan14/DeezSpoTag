namespace DeezSpoTag.Services.Genre;

/// <summary>Projects cleanup's surviving explicit observations without changing cleanup.</summary>
public static class StyleConstructionEvidenceBuilder
{
    public static StyleConstructionEvidenceSnapshot Build(
        long trackId, GenreSemanticSnapshot snapshot, PersonalGenreResolution cleanup,
        PersonalGenreCatalog catalog, IReadOnlyList<PersonalGenreLock> effectiveLocks,
        IReadOnlyList<PersonalGenreMapping> mappings, IReadOnlyList<PersonalGenreRule> rules,
        StyleConstructionArtistIdentity? artistIdentity = null,
        StyleConstructionArtistLocation? artistLocation = null)
    {
        var facts = new List<StyleConstructionSemanticFact>();
        var builtIn = new PersonalGenreCatalog();
        foreach (var decision in cleanup.Decisions.OrderBy(d => d.ObservationIndex))
        {
            if (decision.ObservationIndex < 0 || decision.ObservationIndex >= cleanup.Observations.Count
                || decision.ObservationIndex >= snapshot.Observations.Count
                || !catalog.TryGetById(decision.TaxonId, out var taxon))
                continue;
            var observation = cleanup.Observations[decision.ObservationIndex];
            if (observation.Origin != GenreObservationOrigin.PostPlatform
                || !cleanup.Classifications.Any(c => c.TaxonId == taxon.Id && c.Kind == taxon.Kind && c.Status != "derived_parent")
                || !SurvivingValues(cleanup, taxon.Kind).Contains(taxon.Name, StringComparer.Ordinal))
                continue;

            ConstructionEvidenceOrigin origin;
            string provenance = "observation:" + decision.ObservationIndex;
            switch (decision.Outcome)
            {
                case "rule_applied":
                    var rule = rules.Where(r => r.Enabled && Matches(r.MatchValue, r.InputField, observation))
                        .OrderByDescending(r => r.Priority).ThenBy(r => r.Id).FirstOrDefault();
                    if (rule is null || !string.Equals(rule.TargetTaxonId, taxon.Id, StringComparison.OrdinalIgnoreCase))
                        continue;
                    origin = ConstructionEvidenceOrigin.UserRule;
                    provenance = "rule:" + rule.Id + "/" + provenance;
                    break;
                case "mapping_applied":
                    var mapping = mappings.Where(m => m.Enabled && Matches(m.MatchValue, m.InputField, observation))
                        .OrderByDescending(m => m.Priority).ThenBy(m => m.Id).FirstOrDefault();
                    if (mapping is null || mapping.Action != PersonalGenreMappingAction.Map
                        || !string.Equals(mapping.TargetTaxonId, taxon.Id, StringComparison.OrdinalIgnoreCase))
                        continue;
                    origin = ConstructionEvidenceOrigin.UserMapped;
                    provenance = "mapping:" + mapping.Id + "/" + provenance;
                    break;
                case "context_only":
                    var contextMapping = mappings.Where(m => m.Enabled && Matches(m.MatchValue, m.InputField, observation))
                        .OrderByDescending(m => m.Priority).ThenBy(m => m.Id).FirstOrDefault();
                    if (contextMapping is not null)
                    {
                        if (contextMapping.Action != PersonalGenreMappingAction.ContextOnly
                            || !string.Equals(contextMapping.TargetTaxonId, taxon.Id, StringComparison.OrdinalIgnoreCase)
                            || taxon.Kind != PersonalGenreTaxonKind.Context)
                            continue;
                        origin = ConstructionEvidenceOrigin.UserMapped;
                        provenance = "mapping:" + contextMapping.Id + "/" + provenance;
                        break;
                    }
                    goto case "canonical_match";
                case "canonical_match":
                case "alias_match":
                    if (!catalog.TryMatch(observation.RawValue, out var matched, out var match) || match.IsCustom
                        || matched.Id != taxon.Id || matched.Kind != taxon.Kind
                        || !builtIn.TryGetById(taxon.Id, out var original) || original.Kind != taxon.Kind)
                        continue;
                    origin = ConstructionEvidenceOrigin.ExplicitFile;
                    break;
                default:
                    continue;
            }

            var transformations = new List<ConstructionTransformation>();
            if (observation.WasNormalized) transformations.Add(ConstructionTransformation.Normalized);
            if (origin == ConstructionEvidenceOrigin.ExplicitFile
                && catalog.TryMatch(observation.RawValue, out _, out var aliasMatch) && aliasMatch.ViaAlias)
                transformations.Add(ConstructionTransformation.AliasCanonicalized);
            if (observation.InputField != taxon.Kind) transformations.Add(ConstructionTransformation.FieldMoved);
            facts.Add(new StyleConstructionSemanticFact
            {
                FactId = "track:" + trackId + "/observation:" + decision.ObservationIndex,
                Kind = taxon.Kind, CanonicalId = taxon.Id, CanonicalValue = taxon.Name,
                // With no identity the fact stays track-scoped, which is the only scope
                // production can currently qualify. With an identity it takes the
                // caller's declared scope, so an artist-scoped rule can be evaluated —
                // but note that production's identity is an album-artist binding with an
                // Unknown role, so the fact is rejected by the evaluator's artist-binding
                // gate. The builder can represent and the evaluator can accept valid
                // artist-scoped evidence; the missing piece is a structured track-level
                // artist-credit source carrying Main/Featured roles, not anything here.
                Scope = artistIdentity?.Scope ?? ConstructionEvidenceScope.Track, Origin = origin,
                Transformations = transformations.ToArray(), Source = "audio-file",
                ProvenanceReference = provenance, OriginalValue = observation.OriginalValue ?? observation.RawValue,
                OriginalField = observation.InputField, KnownAt = snapshot.ReadAtUtc,
                // Only a binding the caller actually proved is recorded. A missing
                // binding leaves these at their defaults, so the evaluator's
                // artist-scope and artist-role gates reject the fact instead of
                // treating an unattributed observation as proven artist evidence.
                ArtistId = artistIdentity?.ArtistId,
                ArtistRole = artistIdentity?.Role ?? ConstructionArtistRole.Unknown,
                ArtistBindingReference = artistIdentity?.BindingReference
            });
        }

        var styleLocks = effectiveLocks.Where(l => l.Enabled && l.TrackId == trackId
            && catalog.TryGetById(l.TaxonId, out var term) && term.Kind == PersonalGenreTaxonKind.Style).ToArray();
        var rank = styleLocks.Select(l => ScopeRank(l.ScopeType)).DefaultIfEmpty(0).Max();
        return new(1, trackId, snapshot.ReadAtUtc, ResearchedGenreCatalog.Current.Version,
            cleanup.ResolverVersion, facts.ToArray(), new(styleLocks.Where(l => ScopeRank(l.ScopeType) == rank)
                .OrderBy(l => l.TaxonId, StringComparer.Ordinal).ToArray()),
            artistIdentity, artistLocation);
    }

    private static bool Matches(string value, PersonalGenreTaxonKind? field, GenreTagObservation observation)
        => (field is null || field == observation.InputField)
           && PersonalGenreTaxonomy.Normalize(value) == PersonalGenreTaxonomy.Normalize(observation.RawValue);

    private static int ScopeRank(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "artist" => 1, "album" => 2, _ => 3
    };

    private static IReadOnlyList<string> SurvivingValues(PersonalGenreResolution resolution, PersonalGenreTaxonKind kind)
        => kind switch
        {
            PersonalGenreTaxonKind.Genre => resolution.Genres,
            PersonalGenreTaxonKind.Style => resolution.Styles,
            PersonalGenreTaxonKind.Substyle => resolution.Substyles,
            PersonalGenreTaxonKind.Scene => resolution.Scenes,
            PersonalGenreTaxonKind.Context => resolution.Contexts,
            PersonalGenreTaxonKind.Language => resolution.Languages,
            _ => []
        };
}
