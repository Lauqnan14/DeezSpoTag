namespace DeezSpoTag.Services.Genre;

/// <summary>
/// The effective taxonomy: the researched master vocabulary plus DeezSpoTag's
/// built-in non-musical dimensions plus the user's own terms.
/// </summary>
/// <remarks>
/// <para>
/// The researched master supplies Genre and Style recognition. It does not replace
/// the built-in taxonomy, because it has no Context, Scene or Language
/// equivalents and those dimensions stay live. Sub-genre is Style, not a third
/// musical kind. It also does not replace the built-in
/// ids: a saved user lock, mapping or rule refers to an id, so where a built-in term
/// and a researched term are the same musical thing the built-in id and display
/// spelling are kept and only the classification is adopted.
/// </para>
/// <para>
/// Where the two disagree about Genre versus Style, the research decides. A term's
/// kind is a property of the vocabulary, not of whichever field or legacy
/// classification a value happened to arrive in.
/// </para>
/// <para>
/// Regional membership is organizational metadata in both sources. It never decides
/// a kind and is never a rule for creating a classification.
/// </para>
/// </remarks>
public sealed class PersonalGenreCatalog
{
    /// <summary>
    /// The merged built-in and researched vocabulary.
    /// </summary>
    /// <remarks>
    /// Built once. The lookup covers several thousand terms and this catalog is
    /// constructed once per file during an AutoTag run, so rebuilding it per file
    /// would make every track pay for the whole vocabulary.
    /// </remarks>
    private static readonly Lazy<CatalogBase> Base = new(BuildBase, isThreadSafe: true);

    private readonly IReadOnlyDictionary<string, PersonalGenreTaxon> _byId;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CatalogEntry>> _byLookup;

    /// <summary>
    /// The shared read-only catalog: built-ins plus the researched vocabulary.
    /// </summary>
    /// <remarks>
    /// Guards need to ask "is this id one of ours?" and must get an answer about the
    /// whole vocabulary, not only the 156-term built-in array. Anything checking the
    /// built-ins alone would treat a researched term as unknown and let a user
    /// silently shadow it.
    /// </remarks>
    public static PersonalGenreCatalog Default { get; } = new();

    public PersonalGenreCatalog(
        IReadOnlyList<PersonalGenreTaxon>? customTaxa = null,
        IReadOnlyList<PersonalGenreTermEdit>? edits = null)
    {
        var baseCatalog = Base.Value;
        _byId = baseCatalog.ById;
        Taxa = baseCatalog.Taxa;

        if (customTaxa is null || customTaxa.Count == 0)
        {
            // The shared lookup is used directly, so the common path allocates
            // nothing beyond the catalog itself.
            _byLookup = baseCatalog.Lookup;
            if (edits is { Count: > 0 })
            {
                var edited = ApplyEdits(Taxa.ToList(), edits);
                _byLookup = BuildLookup(edited, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                _byId = edited.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
                Taxa = edited;
            }
            return;
        }

        var merged = new List<PersonalGenreTaxon>(baseCatalog.Taxa);
        var builtInIds = baseCatalog.Taxa
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var taxon in customTaxa)
        {
            if (string.IsNullOrWhiteSpace(taxon.Id)
                || string.IsNullOrWhiteSpace(taxon.Name)
                || builtInIds.Contains(taxon.Id))
            {
                continue;
            }

            var existingIndex = merged.FindIndex(item =>
                string.Equals(item.Id, taxon.Id, StringComparison.OrdinalIgnoreCase));
            var normalized = NormalizeTaxon(taxon);
            if (existingIndex >= 0)
            {
                merged[existingIndex] = normalized;
            }
            else
            {
                merged.Add(normalized);
            }

            customIds.Add(taxon.Id);
        }

        // A user's own term is registered alongside the built-ins in one lookup, so a
        // name the user defined still wins over a built-in spelling that reduces to
        // the same key.
        if (edits is { Count: > 0 })
        {
            merged = ApplyEdits(merged, edits);
        }

        _byLookup = BuildLookup(merged, customIds);
        _byId = merged.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        Taxa = merged;
    }

    /// <summary>
    /// Applies a user's vocabulary edits without changing the shipped term id.
    /// An unknown parent is ignored. A merge keeps the source name as an alias of
    /// the target and removes the source from the effective catalog.
    /// </summary>
    private static List<PersonalGenreTaxon> ApplyEdits(
        List<PersonalGenreTaxon> merged,
        IReadOnlyList<PersonalGenreTermEdit> edits)
    {
        foreach (var edit in edits)
        {
            if (string.IsNullOrWhiteSpace(edit.TaxonId))
            {
                continue;
            }

            var index = merged.FindIndex(item =>
                string.Equals(item.Id, edit.TaxonId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }

            var taxon = merged[index];
            if (!string.IsNullOrWhiteSpace(edit.MergedInto)
                && merged.Any(item => string.Equals(item.Id, edit.MergedInto, StringComparison.OrdinalIgnoreCase)))
            {
                var targetIndex = merged.FindIndex(item =>
                    string.Equals(item.Id, edit.MergedInto, StringComparison.OrdinalIgnoreCase));
                var target = merged[targetIndex];
                var aliases = (target.Aliases ?? []).Append(taxon.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                merged[targetIndex] = target with { Aliases = aliases };
                merged.RemoveAt(index);
                continue;
            }

            var name = taxon.Name;
            var renameRefused = false;
            var aliasList = (taxon.Aliases ?? []).ToList();
            if (!string.IsNullOrWhiteSpace(edit.OriginalName)
                && (string.Equals(edit.OriginalName, taxon.Name, StringComparison.OrdinalIgnoreCase)
                    || aliasList.Any(alias => string.Equals(alias, edit.OriginalName, StringComparison.OrdinalIgnoreCase))))
            {
                if (!string.Equals(edit.OriginalName, taxon.Name, StringComparison.Ordinal))
                {
                    // A rename must not hide another canonical term. If the alias the
                    // user promotes is also a different term's name (or id), the
                    // spelling choice is refused and both terms keep their spelling.
                    // The resolver is built on every read path, so a stale or
                    // colliding saved edit degrades to "no rename" here rather than
                    // breaking recognition; the vocabulary editor validates the same
                    // rule before saving so the user sees the refusal.
                    var collideName = Normalize(edit.OriginalName);
                    renameRefused = merged.Any(other =>
                        !string.Equals(other.Id, taxon.Id, StringComparison.OrdinalIgnoreCase)
                        && (Normalize(other.Name) == collideName
                            || Normalize(other.Id) == collideName));
                    if (!renameRefused)
                    {
                        aliasList.RemoveAll(alias => string.Equals(alias, edit.OriginalName, StringComparison.OrdinalIgnoreCase));
                        aliasList.Insert(0, taxon.Name);
                        name = edit.OriginalName;
                    }
                }
            }

            if (edit.Aliases is not null)
            {
                aliasList = edit.Aliases
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Select(alias => alias.Trim())
                    .Where(alias => !string.Equals(alias, name, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (renameRefused
                && !string.IsNullOrWhiteSpace(edit.OriginalName)
                && !string.Equals(edit.OriginalName, name, StringComparison.OrdinalIgnoreCase)
                && aliasList.All(alias => !string.Equals(alias, edit.OriginalName, StringComparison.OrdinalIgnoreCase)))
            {
                // The refusal keeps the promoted spelling as an alias even when the
                // client's own alias list already reflected the rejected swap.
                aliasList.Insert(0, edit.OriginalName.Trim());
            }

            IReadOnlyList<string>? parents = taxon.ParentIds;
            if (edit.ParentIds is not null)
            {
                parents = edit.ParentIds
                    .Where(id => merged.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                        && !string.Equals(id, taxon.Id, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            merged[index] = taxon with
            {
                Name = name,
                Aliases = aliasList.Count == 0 ? null : aliasList,
                ParentIds = parents is { Count: 0 } ? null : parents
            };
        }

        return merged;
    }

    /// <summary>Every term in the effective taxonomy, built-ins and researched first.</summary>
    public IReadOnlyList<PersonalGenreTaxon> Taxa { get; }

    public bool TryGetById(string? id, out PersonalGenreTaxon taxon)
        => _byId.TryGetValue(id?.Trim() ?? string.Empty, out taxon!);

    public bool TryMatch(string? value, out PersonalGenreTaxon taxon)
        => TryMatch(value, out taxon, out _);

    /// <summary>
    /// Matches a value and reports how it matched, so a decision can distinguish a
    /// canonical term from a user's own and name the alias that was used.
    /// </summary>
    /// <remarks>
    /// A label with more than one semantic identity does not match. Callers that
    /// need the candidates use <see cref="LookupLabel"/>. Insertion order is not
    /// a way to pick one of them.
    /// </remarks>
    public bool TryMatch(string? value, out PersonalGenreTaxon taxon, out CatalogMatch match)
    {
        var lookup = LookupLabel(value);
        if (lookup.Status == LabelLookupStatus.Unique
            && _byLookup.TryGetValue(Normalize(value), out var entries))
        {
            var entry = entries[0];
            taxon = entry.Taxon;
            match = new CatalogMatch(entry.IsCustom, entry.MatchedValue, entry.MatchedAlias);
            return true;
        }

        taxon = null!;
        match = default;
        return false;
    }

    /// <summary>
    /// Resolves a visible label without choosing among several identities.
    /// </summary>
    public LabelLookup LookupLabel(string? value)
    {
        if (!_byLookup.TryGetValue(Normalize(value), out var entries) || entries.Count == 0)
        {
            return new LabelLookup(LabelLookupStatus.None, []);
        }

        var candidates = entries
            .GroupBy(entry => entry.Taxon.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Taxon)
            .ToList();
        var status = candidates.Count == 1 ? LabelLookupStatus.Unique : LabelLookupStatus.Ambiguous;
        return new LabelLookup(status, candidates);
    }

    /// <summary>Every lookup key that names more than one taxon, with the match path of each.</summary>
    public IReadOnlyList<LookupCollision> LookupCollisions()
        => _byLookup
            .Where(pair => pair.Value.Select(entry => entry.Taxon.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(pair => new LookupCollision(
                pair.Key,
                pair.Value.Select(entry => new LookupCollisionMember(entry.Taxon, entry.MatchedAlias is not null)).ToList()))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>Whether the research decided this value is not a Genre or a Style.</summary>
    public bool IsResearchedExclusion(string? value)
        => ResearchedGenreCatalog.Current.IsExcluded(value);

    /// <summary>Whether the research holds this term as not safe to classify.</summary>
    public bool IsResearchedAmbiguity(string? value)
        => ResearchedGenreCatalog.Current.IsAmbiguous(value);

    public string? ResearchedExclusionReason(string? value)
        => ResearchedGenreCatalog.Current.ExclusionReason(value);

    public string? ResearchedAmbiguityReason(string? value)
        => ResearchedGenreCatalog.Current.AmbiguityReason(value);

    /// <summary>How a value matched the taxonomy.</summary>
    public enum LabelLookupStatus
    {
        None,
        Unique,
        Ambiguous
    }

    /// <summary>A label lookup that refuses to pick a winner when several identities share it.</summary>
    public readonly record struct LabelLookup(LabelLookupStatus Status, IReadOnlyList<PersonalGenreTaxon> Candidates);

    /// <summary>One normalized label that currently indexes more than one taxon.</summary>
    public sealed record LookupCollision(string Key, IReadOnlyList<LookupCollisionMember> Members);

    /// <summary>One taxon reached by an overloaded lookup key.</summary>
    public sealed record LookupCollisionMember(PersonalGenreTaxon Taxon, bool ViaAlias);

    /// <summary>How a value matched the taxonomy.</summary>
    public readonly record struct CatalogMatch(bool IsCustom, string MatchedValue, string? MatchedAlias)
    {
        /// <summary>True when the value matched through an alias rather than the term's own name or id.</summary>
        public bool ViaAlias => MatchedAlias is not null;
    }

    private readonly record struct CatalogEntry(PersonalGenreTaxon Taxon, bool IsCustom, string MatchedValue, string? MatchedAlias);

    private sealed record CatalogBase(
        IReadOnlyList<PersonalGenreTaxon> Taxa,
        IReadOnlyDictionary<string, IReadOnlyList<CatalogEntry>> Lookup,
        IReadOnlyDictionary<string, PersonalGenreTaxon> ById);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Trim()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static PersonalGenreTaxon NormalizeTaxon(PersonalGenreTaxon taxon)
    {
        if (taxon.Kind == PersonalGenreTaxonKind.Substyle)
        {
            taxon = taxon with { Kind = PersonalGenreTaxonKind.Style };
        }

        return taxon.ContextOnly
           && taxon.Kind is PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style
            ? taxon with { Kind = PersonalGenreTaxonKind.Context }
            : taxon;
    }

    /// <summary>
    /// Merges the researched master into the built-in taxonomy.
    /// </summary>
    /// <remarks>
    /// A built-in term that the research also covers keeps its id, its display
    /// spelling and its parents, and adopts the researched kind, regions, and any
    /// verified parent ids the research adds. Parents are a union: a researched
    /// batch does not delete a built-in parent by omitting it. A researched Genre
    /// or Style whose label is already a Context, Scene or Language is added
    /// beside it, with a distinct id when the built-in id has to stay. A researched
    /// term the built-ins do not have is added as a new entry. Research parent ids
    /// are translated through the research-id to runtime-id map, because a saved
    /// built-in id can differ from the research id of the same identity.
    /// </remarks>
    private static CatalogBase BuildBase()
    {
        var builtIns = PersonalGenreTaxonomy.GetDefaultTaxa();
        var researched = ResearchedGenreCatalog.Current;

        var merged = new List<PersonalGenreTaxon>(builtIns.Count + researched.Terms.Count);
        var researchRuntimeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var builtIn in builtIns)
        {
            PersonalGenreTaxon result = builtIn;

            // Only Genre and Style are researched, and only those two can have their
            // kind taken from the research. Context, Scene, Language and Substyle
            // stay exactly as the built-ins define them.
            if (builtIn.Kind is PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style
                && researched.TryMatch(builtIn.Name, out var match, out _)
                && match.Kind is nameof(PersonalGenreTaxonKind.Genre) or nameof(PersonalGenreTaxonKind.Style))
            {
                var researchedKind = Enum.Parse<PersonalGenreTaxonKind>(match.Kind);

                var regions = (builtIn.Regions ?? []).Concat(match.Regions)
                    .Where(region => !string.IsNullOrWhiteSpace(region))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var aliases = (builtIn.Aliases ?? [])
                    .Concat(match.Aliases)
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                result = builtIn with
                {
                    Kind = researchedKind,
                    Regions = regions.Length > 0 ? regions : null,
                    Aliases = aliases.Length > 0 ? aliases : null
                };
                AssignResearchRuntimeId(researchRuntimeIds, match.Id, builtIn.Id);
            }

            merged.Add(NormalizeTaxon(result));
        }

        var researchedOnly = new List<(ResearchedGenreTerm Term, string RuntimeId)>();
        foreach (var term in researched.Terms)
        {
            var nameKey = Normalize(term.Name);
            var idKey = Normalize(term.Id);
            // A built-in Genre or Style with this name already carries the musical
            // identity. A Context, Scene or Language with the same label does not.
            var musicalMatches = merged
                .Where(item => item.Kind is PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style
                    && (Normalize(item.Name) == nameKey || Normalize(item.Id) == idKey))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (musicalMatches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Researched {term.Kind} '{term.Name}' ({term.Id}) matches multiple runtime identities: "
                    + string.Join(", ", musicalMatches.Select(item => item.Id)) + ".");
            }

            if (musicalMatches.Count == 1)
            {
                AssignResearchRuntimeId(researchRuntimeIds, term.Id, musicalMatches[0].Id);
                continue;
            }

            var runtimeId = term.Id.Trim();
            if (merged.Any(item => string.Equals(item.Id, runtimeId, StringComparison.OrdinalIgnoreCase))
                || researchedOnly.Any(item => string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase)))
            {
                // The saved built-in id stays on the non-musical identity.
                runtimeId = term.Id.Trim() + "-" + term.Kind.ToLowerInvariant();
            }

            if (merged.Any(item => string.Equals(item.Id, runtimeId, StringComparison.OrdinalIgnoreCase))
                || researchedOnly.Any(item => string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Researched {term.Kind} '{term.Name}' ({term.Id}) has no free runtime id.");
            }

            AssignResearchRuntimeId(researchRuntimeIds, term.Id, runtimeId);
            researchedOnly.Add((term, runtimeId));
        }

        foreach (var (term, runtimeId) in researchedOnly)
        {
            var parents = ResolveResearchParents(term, researchRuntimeIds);
            merged.Add(NormalizeTaxon(new PersonalGenreTaxon(
                runtimeId,
                term.Name,
                Enum.Parse<PersonalGenreTaxonKind>(term.Kind),
                ParentIds: parents,
                ContextOnly: false,
                Aliases: term.Aliases.Count > 0 ? term.Aliases.ToArray() : null,
                Regions: term.Regions.Count > 0 ? term.Regions.ToArray() : null)));
        }

        for (var index = 0; index < merged.Count; index++)
        {
            var taxon = merged[index];
            if (taxon.Kind is not (PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style)
                || !researched.TryMatch(taxon.Name, out var match, out _)
                || match.Kind is not (nameof(PersonalGenreTaxonKind.Genre) or nameof(PersonalGenreTaxonKind.Style))
                || !researchRuntimeIds.TryGetValue(match.Id, out var runtimeId)
                || !string.Equals(runtimeId, taxon.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var union = UnionParents(taxon.ParentIds, ResolveResearchParents(match, researchRuntimeIds));
            if (!SameParents(taxon.ParentIds, union))
            {
                merged[index] = taxon with { ParentIds = union };
            }
        }

        EnsureParentGraphIsSafe(merged);
        GenreTaxonomyReconciliations.EnsureNoSuppressedMusicalIdentities(merged, researched);

        var byId = merged.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        // Nothing in the shared base is user-defined, so no id here is custom. The
        // distinction matters because the decision trail reports it, and reporting a
        // researched term as personal would misattribute it.
        var lookup = BuildLookup(merged, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return new CatalogBase(merged, lookup, byId);
    }

    private static void AssignResearchRuntimeId(
        Dictionary<string, string> researchRuntimeIds,
        string researchId,
        string runtimeId)
    {
        if (researchRuntimeIds.TryGetValue(researchId, out var existing)
            && !string.Equals(existing, runtimeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Researched id '{researchId}' maps to both runtime ids '{existing}' and '{runtimeId}'.");
        }

        researchRuntimeIds[researchId] = runtimeId;
    }

    /// <summary>
    /// Translates researched parent ids to the runtime ids that actually exist
    /// after built-in identities keep their saved ids.
    /// </summary>
    private static IReadOnlyList<string>? ResolveResearchParents(
        ResearchedGenreTerm term,
        IReadOnlyDictionary<string, string> researchRuntimeIds)
    {
        if (term.ParentIds.Count == 0)
        {
            return null;
        }

        var resolved = new List<string>(term.ParentIds.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parentResearchId in term.ParentIds)
        {
            if (!researchRuntimeIds.TryGetValue(parentResearchId, out var runtimeId))
            {
                throw new InvalidOperationException(
                    $"Researched term '{term.Id}' parents '{parentResearchId}', which has no runtime id.");
            }

            if (seen.Add(runtimeId))
            {
                resolved.Add(runtimeId);
            }
        }

        return resolved.Count == 0 ? null : resolved;
    }

    private static IReadOnlyList<string>? UnionParents(
        IReadOnlyList<string>? existing,
        IReadOnlyList<string>? researchedParents)
    {
        var union = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parentId in existing ?? [])
        {
            if (!string.IsNullOrWhiteSpace(parentId) && seen.Add(parentId))
            {
                union.Add(parentId);
            }
        }

        foreach (var parentId in researchedParents ?? [])
        {
            if (seen.Add(parentId))
            {
                union.Add(parentId);
            }
        }

        return union.Count == 0 ? null : union;
    }

    private static bool SameParents(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        var a = left ?? [];
        var b = right ?? [];
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var index = 0; index < a.Count; index++)
        {
            if (!string.Equals(a[index], b[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The effective parent graph has to name real Genre or Style ids and must not cycle.
    /// </summary>
    private static void EnsureParentGraphIsSafe(IReadOnlyList<PersonalGenreTaxon> taxa)
    {
        var byId = taxa.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();

        foreach (var taxon in taxa)
        {
            if (taxon.Kind is PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style)
            {
                Visit(taxon.Id);
            }
        }

        void Visit(string id)
        {
            if (state.TryGetValue(id, out var mark))
            {
                if (mark == 1)
                {
                    var start = stack.FindIndex(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));
                    var cycle = start < 0 ? stack.Append(id) : stack.Skip(start).Append(id);
                    throw new InvalidOperationException(
                        "Effective taxonomy parent cycle: " + string.Join(" -> ", cycle) + ".");
                }

                return;
            }

            state[id] = 1;
            stack.Add(id);
            if (byId.TryGetValue(id, out var taxon) && taxon.ParentIds is not null)
            {
                foreach (var parentId in taxon.ParentIds)
                {
                    if (!byId.TryGetValue(parentId, out var parent))
                    {
                        throw new InvalidOperationException(
                            $"Taxonomy term '{id}' parents '{parentId}', which is not in the effective taxonomy.");
                    }

                    if (parent.Kind is not (PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style))
                    {
                        throw new InvalidOperationException(
                            $"Taxonomy term '{id}' parents '{parentId}', which is not a Genre or Style.");
                    }

                    Visit(parentId);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }
    }

    /// <summary>
    /// Builds the value lookup.
    /// </summary>
    /// <remarks>
    /// A term's own name and id are indexed before its aliases. An alias does not
    /// replace a canonical name and does not add a second identity. Two taxa that
    /// each own the same visible label are both indexed. Neither one wins because
    /// it was inserted first.
    /// </remarks>
    private static Dictionary<string, IReadOnlyList<CatalogEntry>> BuildLookup(
        IReadOnlyList<PersonalGenreTaxon> taxa,
        IReadOnlySet<string> customIds)
    {
        var result = new Dictionary<string, List<CatalogEntry>>(taxa.Count * 2, StringComparer.Ordinal);

        foreach (var taxon in taxa)
        {
            var isCustom = customIds.Contains(taxon.Id);
            Add(taxon, taxon.Name, isCustom, null);
            Add(taxon, taxon.Id, isCustom, null);
        }

        foreach (var taxon in taxa)
        {
            var isCustom = customIds.Contains(taxon.Id);
            if (taxon.Aliases is null)
            {
                continue;
            }

            foreach (var alias in taxon.Aliases)
            {
                Add(taxon, alias, isCustom, alias);
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CatalogEntry>)pair.Value,
            StringComparer.Ordinal);

        void Add(PersonalGenreTaxon taxon, string value, bool isCustom, string? alias)
        {
            var normalized = Normalize(value);
            if (normalized.Length == 0)
            {
                return;
            }

            if (!result.TryGetValue(normalized, out var entries))
            {
                entries = [];
                result[normalized] = entries;
            }

            if (entries.Any(entry => string.Equals(entry.Taxon.Id, taxon.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // An alias is a spelling of one identity. It must not hide a canonical
            // name and it must not invent a second identity for a label that
            // already has one.
            if (alias is not null && entries.Count > 0)
            {
                return;
            }

            entries.Add(new CatalogEntry(taxon, isCustom, value, alias));
        }
    }
}
