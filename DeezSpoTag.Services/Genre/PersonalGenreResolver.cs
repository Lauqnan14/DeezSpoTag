namespace DeezSpoTag.Services.Genre;

/// <summary>
/// Interprets the semantic tags read from an audio file.
///
/// The resolver is deliberately provider-agnostic. It never learns which service
/// produced a value, because by the time it runs the file is the only input:
/// AutoTag has already written whatever its platforms decided, and the job here
/// is to say what those values <em>mean</em> under the user's taxonomy.
///
/// What survives from the earlier provider-based design is the precedence chain
/// and the user's own vocabulary. What is gone is the authority model: there is
/// no way to weigh one source against another, because there are no sources.
///
/// <para><b>The stages, in the order they actually run.</b></para>
///
/// <para>PREPROCESS, performed by <see cref="GenreNormalizationPreprocessor"/>
/// before this resolver is called at all and never inside it:</para>
/// <list type="number">
/// <item>Shared Genre Normalization: the user's alias preferences, composite
/// splitting and deduplication.</item>
/// <item>Shared block rules: the user's blocked values, including the shipped
/// defaults.</item>
/// </list>
///
/// <para>THEN CLASSIFY, in this order:</para>
/// <list type="number">
/// <item>Explicit user rule.</item>
/// <item>User mapping: Map, ContextOnly, Ignore or Ambiguous.</item>
/// <item>Researched exclusion: the master decided this is not a Genre or a Style.</item>
/// <item>Researched ambiguity: the master has held this term.</item>
/// <item>Canonical and alias recognition against the taxonomy.</item>
/// <item>Preserved unknown.</item>
/// </list>
///
/// <para>A blocked value never reaches any classification stage. There is no product
/// requirement that lets a user rule resurrect a value their own block list forbids,
/// so the preprocessing removal is final: no rule, mapping or lock is consulted for
/// it, and it is recorded as a blocked decision instead.</para>
///
/// <para>POST-CLASSIFICATION:</para>
/// <list type="bullet">
/// <item>Locks, at track over album over artist precedence.</item>
/// <item>Parent-genre derivation, when the user asked for it.</item>
/// </list>
///
/// <para>No stage constructs a Style from metadata. Location, era, label and the
/// rest are recorded against a decision for auditing only; inferring a term from them
/// is a separate objective and is deliberately absent.</para>
/// </summary>
public static class PersonalGenreResolver
{
    public const string Version = "personal-genre-file-v2";

    public static PersonalGenreResolution Resolve(
        IReadOnlyList<GenreTagObservation>? observations,
        IReadOnlyList<PersonalGenreMapping>? mappings = null,
        IReadOnlyList<PersonalGenreRule>? rules = null,
        IReadOnlyList<PersonalGenreLock>? locks = null,
        IReadOnlyList<PersonalGenreTaxon>? customTaxa = null,
        PersonalGenreSettings? settings = null,
        IReadOnlyList<GenreArtistLocationContext>? artistLocations = null,
        IReadOnlyList<GenreTagObservation>? originalObservations = null,
        IReadOnlyList<RemovedGenreTagValue>? removedByNormalization = null)
    {
        settings ??= new PersonalGenreSettings();
        var effective = (observations ?? Array.Empty<GenreTagObservation>())
            .Where(item => !string.IsNullOrWhiteSpace(item.RawValue))
            .Select((item, index) => item with
            {
                RawValue = item.RawValue.Trim(),
                Order = item.Order == 0 ? index : item.Order
            })
            .ToList();

        var catalog = new PersonalGenreCatalog(customTaxa);
        var effectiveMappings = mappings ?? Array.Empty<PersonalGenreMapping>();
        var effectiveRules = rules ?? Array.Empty<PersonalGenreRule>();

        // Values the shared Genre Normalization preferences removed are recorded
        // before classification runs, so a blocked value appears in the decision
        // trail as a deliberate removal rather than simply being absent. Index
        // continues past the surviving values so a decision index keeps pointing at
        // the same observation it always did.
        var removalDecisions = new List<PersonalGenreEvidenceDecision>();
        var removalOffset = effective.Count;
        var removalIndex = 0;
        foreach (var removed in removedByNormalization ?? Array.Empty<RemovedGenreTagValue>())
        {
            removalDecisions.Add(new PersonalGenreEvidenceDecision(
                removalOffset + removalIndex++,
                removed.Value,
                removed.InputField,
                "blocked",
                null,
                removed.Reason,
                NormalizedValue: null,
                CanonicalValue: null));
        }

        // Values the file had before AutoTag ran, and that no platform replaced.
        // Only the ones this resolver cannot interpret are carried forward: a
        // platform replacing a value the taxonomy understands is a legitimate
        // correction, whereas dropping a value nobody recognises would silently
        // delete something the user wrote themselves.
        var carried = CarryForwardUninterpreted(
            originalObservations ?? Array.Empty<GenreTagObservation>(),
            effective,
            effectiveMappings,
            effectiveRules,
            catalog,
            (removedByNormalization ?? Array.Empty<RemovedGenreTagValue>())
                .Select(item => item.Value)
                .ToArray());
        var offset = effective.Count;
        for (var index = 0; index < carried.Count; index++)
        {
            effective.Add(carried[index] with { Order = offset + index });
        }
        var appliedRuleIds = new List<string>();
        var candidates = new List<Candidate>();
        var preserved = new List<PreservedTagValue>();
        var decisions = new List<PersonalGenreEvidenceDecision>(effective.Count);

        for (var index = 0; index < effective.Count; index++)
        {
            var observation = effective[index];
            var attempt = ResolveObservation(observation, effectiveMappings, effectiveRules, catalog);
            var reason = artistLocations is { Count: > 0 }
                ? attempt.Reason + " " + DescribeLocationSupport(observation.RawValue, artistLocations)
                : attempt.Reason;
            // The decision reports the value the file held, so history written
            // before normalization ran keeps the same meaning, and separately the
            // spelling the saved preferences produced and the canonical term it was
            // recognised as.
            decisions.Add(new PersonalGenreEvidenceDecision(
                index,
                observation.OriginalValue ?? observation.RawValue,
                observation.InputField,
                attempt.Outcome,
                attempt.Taxon?.Id,
                reason,
                observation.WasNormalized ? observation.RawValue : null,
                attempt.Taxon?.Name));

            if (attempt.AppliedRuleId.HasValue)
            {
                appliedRuleIds.Add(attempt.AppliedRuleId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // A value nothing could decide stays exactly where the user put it.
            // "Unmapped", "ambiguous" and "excluded" all mean the taxonomy does not
            // offer this value as a Genre or a Style, so the value itself is kept in
            // its original field and the reason it was not classified is recorded.
            // "ignored" means the user explicitly answered, so it is honoured and
            // dropped. A resolved value is never preserved — it is written to the
            // dimension its classification implies, not the one it was found in.
            if (attempt.Taxon is null)
            {
                if (settings.PreserveUnmappedTags
                    && attempt.Outcome is "unmapped" or "ambiguous" or "excluded")
                {
                    decisions[index] = decisions[index] with
                    {
                        Outcome = attempt.Outcome == "excluded" ? "preserved_excluded" : "preserved_unmapped",
                        Reason = attempt.Reason + " It is returned to " + observation.InputField + " unchanged."
                    };
                    preserved.Add(new PreservedTagValue(
                        observation.RawValue,
                        observation.InputField,
                        observation.Order,
                        observation.Origin));
                }

                continue;
            }

            candidates.Add(new Candidate(attempt.Taxon, observation.InputField, observation.Order, index));
        }

        var classifications = Collect(candidates);
        classifications = ApplyLocks(classifications, locks ?? Array.Empty<PersonalGenreLock>(), catalog);
        classifications = IncludeParentGenresIfRequested(classifications, locks ?? Array.Empty<PersonalGenreLock>(), settings, catalog);

        var ordered = classifications
            .OrderByDescending(item => item.UserLocked)
            .ThenBy(item => FirstOrder(item, candidates))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var marked = MarkLocksAndParents(ordered, candidates);
        var genres = marked
            .Where(item => item.Kind == PersonalGenreTaxonKind.Genre)
            .Take(Math.Clamp(settings.MaxGenres, 1, 10))
            .Select(item => item.Name)
            .ToList();
        var styles = marked.Where(item => item.Kind == PersonalGenreTaxonKind.Style).Select(item => item.Name).ToList();
        var substyles = marked.Where(item => item.Kind == PersonalGenreTaxonKind.Substyle).Select(item => item.Name).ToList();
        var contexts = marked.Where(item => item.Kind == PersonalGenreTaxonKind.Context).Select(item => item.Name).ToList();
        var scenes = marked.Where(item => item.Kind == PersonalGenreTaxonKind.Scene).Select(item => item.Name).ToList();
        var languages = marked.Where(item => item.Kind == PersonalGenreTaxonKind.Language).Select(item => item.Name).ToList();

        // Removal decisions follow the classification decisions so the trail reads
        // in the order the values were handled: every surviving value first, then
        // every value the shared preferences removed.
        var allDecisions = new List<PersonalGenreEvidenceDecision>(decisions.Count + removalDecisions.Count);
        allDecisions.AddRange(decisions);
        allDecisions.AddRange(removalDecisions);

        return new PersonalGenreResolution(
            genres.FirstOrDefault(),
            genres,
            styles,
            substyles,
            contexts,
            scenes,
            languages,
            preserved,
            marked,
            allDecisions,
            appliedRuleIds.Distinct(StringComparer.Ordinal).ToArray(),
            effective,
            Version);
    }

    /// <summary>
    /// Records the file state the resolver is about to interpret.
    ///
    /// Both the value and the field are kept, and the order within each field is
    /// preserved, so a run can show exactly what the file contained before
    /// anything was rewritten.
    /// </summary>
    public static GenreSemanticSnapshot ReadSnapshot(IReadOnlyList<GenreTagObservation> observations)
        => new(
            (observations ?? Array.Empty<GenreTagObservation>())
                .Select((item, index) => item with { Order = item.Order == 0 ? index : item.Order })
                .ToList(),
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Notes, in the audit trail, whether a main artist's stored location
    /// corroborates a term that is already present in the file's value.
    ///
    /// This is read-only corroboration. The function cannot introduce, rename or
    /// reclassify anything: an artist located in Atlanta facing a plain
    /// "Hip-Hop" tag produces no geographic term, because the value never
    /// mentioned Atlanta. Only a value that already contains the location is
    /// annotated. Featured, remix and guest credits are ignored by construction,
    /// because only <see cref="GenreArtistLocationContext.IsMainArtist"/> entries
    /// are consulted.
    /// </summary>
    private static string DescribeLocationSupport(
        string rawValue,
        IReadOnlyList<GenreArtistLocationContext> locations)
    {
        var matched = locations
            .Where(item => item.IsMainArtist)
            .SelectMany(Describe)
            .Where(value => value.Length > 0)
            .Where(value => rawValue.Contains(value, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matched.Count == 0
            ? "Location context did not add or change this classification."
            : $"Location context corroborated the existing term(s): {string.Join(", ", matched)}.";
    }

    private static IEnumerable<string> Describe(GenreArtistLocationContext location)
    {
        if (!string.IsNullOrWhiteSpace(location.City)) yield return location.City.Trim();
        if (!string.IsNullOrWhiteSpace(location.Region)) yield return location.Region.Trim();
        if (!string.IsNullOrWhiteSpace(location.Country)) yield return location.Country.Trim();
    }

    /// <summary>
    /// Selects the pre-AutoTag values that must survive into the resolution.
    ///
    /// A value is carried forward when both of the following hold:
    /// it still appears nowhere in the post-AutoTag file state, so no platform
    /// replaced or preserved it anywhere; and this resolver cannot interpret it,
    /// so nothing else would put it back.
    ///
    /// The second condition is what keeps this from fighting AutoTag. If a
    /// platform replaced "Pop" with "Bongo Flava", "Pop" is gone and stays gone —
    /// the platform made a correction the taxonomy understands. But if a platform
    /// overwrote a field and took "My Personal Genre" with it, that value is
    /// uninterpretable and would be silently destroyed, so it is kept.
    /// </summary>
    private static List<GenreTagObservation> CarryForwardUninterpreted(
        IReadOnlyList<GenreTagObservation> originalObservations,
        IReadOnlyList<GenreTagObservation> postPlatform,
        IReadOnlyList<PersonalGenreMapping> mappings,
        IReadOnlyList<PersonalGenreRule> rules,
        PersonalGenreCatalog catalog,
        IReadOnlyCollection<string>? removedByNormalization = null)
    {
        if (originalObservations.Count == 0)
        {
            return [];
        }

        var present = postPlatform
            .Select(item => PersonalGenreTaxonomy.Normalize(item.RawValue))
            .ToHashSet(StringComparer.Ordinal);

        // A value the user's block list removed must stay removed. Carrying the
        // pre-AutoTag copy forward because it is uninterpretable would resurrect
        // exactly the value the user asked to have gone, which is the one outcome
        // carry-forward must never produce.
        var blocked = (removedByNormalization ?? Array.Empty<string>())
            .Select(PersonalGenreTaxonomy.Normalize)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var carried = new List<GenreTagObservation>();
        foreach (var original in originalObservations)
        {
            var normalized = PersonalGenreTaxonomy.Normalize(original.RawValue);
            if (normalized.Length == 0 || present.Contains(normalized) || blocked.Contains(normalized))
            {
                continue;
            }

            var probe = new GenreTagObservation(
                original.RawValue.Trim(),
                original.InputField,
                0,
                GenreObservationOrigin.PreservedOriginal);
            if (ResolveObservation(probe, mappings, rules, catalog).Taxon is not null)
            {
                // The taxonomy understands this value. A platform replacing it is
                // a deliberate correction, not a loss.
                continue;
            }

            present.Add(normalized);
            carried.Add(probe);
        }

        return carried;
    }

    private static ResolutionAttempt ResolveObservation(
        GenreTagObservation observation,
        IReadOnlyList<PersonalGenreMapping> mappings,
        IReadOnlyList<PersonalGenreRule> rules,
        PersonalGenreCatalog catalog)
    {
        var normalized = PersonalGenreTaxonomy.Normalize(observation.RawValue);
        if (normalized.Length == 0)
        {
            return new ResolutionAttempt(null, null, "unmapped", "The value contained no usable characters.");
        }

        var rule = rules
            .Where(item => item.Enabled)
            .Where(item => PersonalGenreTaxonomy.Normalize(item.MatchValue) == normalized)
            .Where(item => InputFieldMatches(item.InputField, observation.InputField))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Id)
            .FirstOrDefault();

        if (rule is not null && catalog.TryGetById(rule.TargetTaxonId, out var ruleTaxon))
        {
            return new ResolutionAttempt(ruleTaxon, rule.Id, "rule_applied", $"Applied user rule {rule.Id}.");
        }

        var mapping = mappings
            .Where(item => item.Enabled)
            .Where(item => PersonalGenreTaxonomy.Normalize(item.MatchValue) == normalized)
            .Where(item => InputFieldMatches(item.InputField, observation.InputField))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Id)
            .FirstOrDefault();

        if (mapping is not null)
        {
            if (mapping.Action == PersonalGenreMappingAction.Ignore)
            {
                return new ResolutionAttempt(null, null, "ignored", $"Rule {mapping.Id} deliberately ignores this value.");
            }

            if (mapping.Action == PersonalGenreMappingAction.Ambiguous)
            {
                return new ResolutionAttempt(null, null, "ambiguous", $"Rule {mapping.Id} marks this value as ambiguous.");
            }

            if (catalog.TryGetById(mapping.TargetTaxonId, out var mappedTaxon))
            {
                if (mapping.Action == PersonalGenreMappingAction.ContextOnly)
                {
                    return new ResolutionAttempt(
                        mappedTaxon with { Kind = PersonalGenreTaxonKind.Context, ContextOnly = true },
                        null,
                        "context_only",
                        $"Rule {mapping.Id} restricts this value to Context.");
                }

                return new ResolutionAttempt(mappedTaxon, null, "mapping_applied", $"Applied user mapping {mapping.Id}.");
            }
        }

        // The researched catalog is consulted before the built-in vocabulary because
        // it decides what a value *is*. A term the research declared not to be a
        // Genre or a Style is excluded from those fields even if a legacy entry would
        // otherwise match it, and a term the research has held is not forced into
        // either. Neither of those is the same as being unknown, so both are
        // reported with the reason the research gave.
        if (catalog.IsResearchedExclusion(observation.RawValue))
        {
            return new ResolutionAttempt(
                null,
                null,
                "excluded",
                catalog.ResearchedExclusionReason(observation.RawValue) is { } exclusionReason
                    ? "The researched master excludes this value: " + exclusionReason
                    : "The researched master excludes this value as not a Genre or a Style.");
        }

        if (catalog.IsResearchedAmbiguity(observation.RawValue))
        {
            return new ResolutionAttempt(
                null,
                null,
                "ambiguous",
                catalog.ResearchedAmbiguityReason(observation.RawValue) is { } ambiguityReason
                    ? "The researched master has not established this term: " + ambiguityReason
                    : "The researched master has not established this term.");
        }

        // The taxonomy outcome distinguishes canonical from personal, alias and
        // direct matches. They resolve to the same taxon, but the history has to be
        // able to say which, because "the user's own term matched" and "a built-in
        // term matched" are different reasons for the same result.
        if (catalog.TryMatch(observation.RawValue, out var matched, out var match))
        {
            if (matched.ContextOnly)
            {
                return new ResolutionAttempt(
                    matched,
                    null,
                    "context_only",
                    match.IsCustom
                        ? $"Matched the personal term '{match.MatchedValue}', which is a non-genre dimension."
                        : $"Matched the canonical term '{match.MatchedValue}', which is a non-genre dimension.");
            }

            var outcome = match switch
            {
                { IsCustom: true, ViaAlias: true } => "alias_match",
                { IsCustom: true } => "personal_taxonomy_match",
                { ViaAlias: true } => "alias_match",
                _ => "canonical_match"
            };

            return new ResolutionAttempt(
                matched,
                null,
                outcome,
                match.ViaAlias
                    ? $"'{observation.RawValue}' is an alias of {matched.Name}."
                    : $"Matched {(match.IsCustom ? "personal" : "canonical")} term {matched.Name}.");
        }

        return new ResolutionAttempt(
            null,
            null,
            "unmapped",
            "No user rule, user mapping, custom taxon or alias matched this value.");
    }

    private static List<PersonalGenreClassification> Collect(IReadOnlyList<Candidate> candidates)
    {
        var output = new List<PersonalGenreClassification>();
        foreach (var group in candidates.GroupBy(item => item.Taxon.Id, StringComparer.OrdinalIgnoreCase))
        {
            var taxon = group.First().Taxon;
            var fields = group
                .Select(item => item.InputField)
                .Distinct()
                .OrderBy(field => field)
                .ToArray();
            output.Add(new PersonalGenreClassification(
                taxon.Id,
                taxon.Name,
                taxon.Kind,
                fields,
                UserLocked: false,
                Status: "suggested"));
        }

        return output;
    }

    /// <summary>
    /// Records which classifications were a user's explicit decision rather than
    /// an automatic reading of the file.
    ///
    /// A lock is the strongest statement the user can make, so it is marked
    /// separately from an automatic classification. A parent genre is derived from
    /// another term the user has in the file, which is a different reason again.
    /// </summary>
    private static List<PersonalGenreClassification> MarkLocksAndParents(
        IReadOnlyList<PersonalGenreClassification> classifications,
        IReadOnlyList<Candidate> candidates)
    {
        var observed = candidates
            .Select(item => item.Taxon.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return classifications
            .Select(item => item switch
            {
                { UserLocked: true } => item with { Status = "locked" },
                _ when !observed.Contains(item.TaxonId) => item with { Status = "derived_parent" },
                _ => item
            })
            .ToList();
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

        var lockedKinds = effectiveLocks.Select(item => item.Taxon.Kind).ToHashSet();
        var output = classifications.Where(item => !lockedKinds.Contains(item.Kind)).ToList();

        foreach (var item in effectiveLocks
            .GroupBy(item => item.Taxon.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            var scope = NormalizeLockScope(item.Lock.ScopeType);
            output.Add(new PersonalGenreClassification(
                item.Taxon.Id,
                item.Taxon.Name,
                item.Taxon.Kind,
                [item.Taxon.Kind],
                UserLocked: true,
                Status: "locked"));
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
                    classification.OriginFields));
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

    private static int FirstOrder(PersonalGenreClassification classification, IReadOnlyList<Candidate> candidates)
    {
        if (classification.UserLocked)
        {
            return int.MinValue;
        }

        var match = candidates
            .Where(item => string.Equals(item.Taxon.Id, classification.TaxonId, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Order)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        return match;
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

    private static bool InputFieldMatches(PersonalGenreTaxonKind? configured, PersonalGenreTaxonKind observed)
        => configured is null || configured.Value == observed;

    private sealed record ResolutionAttempt(
        PersonalGenreTaxon? Taxon,
        long? AppliedRuleId,
        string Outcome,
        string Reason);

    private sealed record Candidate(
        PersonalGenreTaxon Taxon,
        PersonalGenreTaxonKind InputField,
        int Order,
        int ObservationIndex);
}
