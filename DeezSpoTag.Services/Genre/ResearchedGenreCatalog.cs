namespace DeezSpoTag.Services.Genre;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// One researched canonical term from the runtime catalog.
/// </summary>
/// <summary>
    /// Research notes for one term. Context prose and source URLs are not stored.
    /// Location is not prose either: <see cref="Places"/> and
    /// <see cref="MemberCountries"/> are slugs from
    /// <see cref="GenreResearchGeoCatalog"/>, so a city cannot be mistaken for a
    /// country and neither list is free text.
    /// </summary>
    public sealed record ResearchedGenreResearch(
    string Status,
    string? Label,
    ResearchEra? Era,
    IReadOnlyList<string> RegionTokens,
    IReadOnlyList<string> Places,
    IReadOnlyList<string> MemberCountries,
    string? ResearchedKind);

/// <summary>
/// One researched era. <see cref="Label"/> is the research wording.
/// <see cref="Ranges"/> are the years a file update may match. A label with no
/// range, such as colonial or medieval, matches no year.
/// </summary>
public sealed record ResearchEra(string Label, IReadOnlyList<ResearchEraRange> Ranges)
{
    public bool Contains(int year) => Ranges.Any(range => range.Contains(year));
}

/// <summary>An inclusive year span. A null <see cref="End"/> is still open.</summary>
public sealed record ResearchEraRange(int Start, int? End)
{
    public bool Contains(int year) => year >= Start && (End is null || year <= End.Value);
}

public sealed record ResearchedGenreTerm(
    string Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> ParentIds,
    ResearchedGenreResearch? Research = null);

/// <summary>
/// A researched value that is known not to be a Genre or a Style.
/// </summary>
/// <remarks>
/// This is deliberately not the same as unknown. An exclusion is a decision the
/// research made, so the value is kept out of Genre and Style and the decision is
/// reported. An unknown value has no such decision and is preserved instead.
/// </remarks>
public sealed record ResearchedGenreExclusion(string Value, string Reason, string Source);

/// <summary>
/// A researched term that is not known to be wrong but is not safe to classify.
/// </summary>
public sealed record ResearchedGenreAmbiguity(string Value, string Reason, string Source);

/// <summary>
/// One alias the research reconciled onto a canonical term.
/// </summary>
public sealed record ResearchedGenreAlias(string Value, string TargetId, string? Source);

/// <summary>
/// The researched Genre/Style vocabulary, loaded once and validated.
/// </summary>
/// <remarks>
/// <para>
/// This catalog extends the existing built-in taxonomy rather than replacing it.
/// The researched master contains only Genres and Styles, while the built-in
/// taxonomy owns Context, Scene, Language and Substyle, which the master has no
/// equivalent for. Legacy ids also have to keep resolving because saved user locks,
/// mappings and rules refer to them.
/// </para>
/// <para>
/// Regional membership is organizational only. It never decides a term's kind and
/// is never a rule for creating a classification.
/// </para>
/// </remarks>
public sealed class ResearchedGenreCatalog
{
    /// <summary>The embedded catalog resource.</summary>
    private const string ResourceName =
        "DeezSpoTag.Services.Genre.Data.genre-intelligence-taxonomy.json";

    /// <summary>The kind values the research master is allowed to use.</summary>
    private static readonly string[] ValidKinds = [nameof(PersonalGenreTaxonKind.Genre), nameof(PersonalGenreTaxonKind.Style)];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Lazy<ResearchedGenreCatalog> Instance = new(Load, isThreadSafe: true);

    private readonly Dictionary<string, ResearchedGenreTerm> _byId;
    private readonly Dictionary<string, ResearchedGenreTerm> _byKey;
    private readonly Dictionary<string, string> _aliasToId;
    private readonly HashSet<string> _exclusionKeys;
    private readonly Dictionary<string, string> _ambiguityReasons;

    private ResearchedGenreCatalog(
        string version,
        string sourceSha256,
        IReadOnlyList<ResearchedGenreSource> sources,
        IReadOnlyList<ResearchedGenreTerm> terms,
        IReadOnlyList<ResearchedGenreAlias> aliases,
        IReadOnlyDictionary<string, string> exclusions,
        IReadOnlyDictionary<string, string> ambiguities,
        IReadOnlyDictionary<string, string> reconciledSpellings,
        IReadOnlyDictionary<string, string> resolvedExclusionCollisions,
        ResearchedGenreImportDiff? importDiff)
    {
        Version = version;
        SourceSha256 = sourceSha256;
        Sources = sources;
        Terms = terms;
        Aliases = aliases;
        ExclusionReasons = exclusions;
        AmbiguityReasons = ambiguities;
        ReconciledSpellings = reconciledSpellings;
        ResolvedExclusionCollisions = resolvedExclusionCollisions;
        ImportDiff = importDiff;

        _byId = terms.ToDictionary(term => term.Id, StringComparer.OrdinalIgnoreCase);
        _byKey = new Dictionary<string, ResearchedGenreTerm>(StringComparer.Ordinal);
        _aliasToId = new Dictionary<string, string>(StringComparer.Ordinal);
        _exclusionKeys = exclusions.Keys.ToHashSet(StringComparer.Ordinal);
        _ambiguityReasons = new Dictionary<string, string>(ambiguities, StringComparer.Ordinal);

        foreach (var term in terms)
        {
            _byKey[Key(term.Name)] = term;
            _byKey[Key(term.Id)] = term;
        }

        // Canonical keys are registered before any alias, so a researched canonical
        // name can never be shadowed by a spelling variant that reconciles onto it.
        foreach (var alias in aliases)
        {
            if (_byKey.ContainsKey(Key(alias.Value)))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: alias '{alias.Value}' shadows a canonical term. "
                    + "Regenerate the catalog; the generator drops such aliases.");
            }

            _byKey[Key(alias.Value)] = _byId[alias.TargetId];
            _aliasToId[Key(alias.Value)] = alias.TargetId;
        }
    }

    /// <summary>The catalog version, so a run can report which vocabulary it used.</summary>
    public string Version { get; }

    /// <summary>
    /// The SHA-256 of the vocabulary spreadsheet the terms were generated from.
    /// Parentage added by a later batch is not covered by this hash. See
    /// <see cref="Sources"/> for every research artifact.
    /// </summary>
    public string SourceSha256 { get; }

    /// <summary>Every research artifact that produced this catalog, in import order.</summary>
    public IReadOnlyList<ResearchedGenreSource> Sources { get; }

    public IReadOnlyList<ResearchedGenreTerm> Terms { get; }

    public IReadOnlyList<ResearchedGenreAlias> Aliases { get; }

    /// <summary>Keyed by the shared lookup key.</summary>
    public IReadOnlyDictionary<string, string> ExclusionReasons { get; }

    /// <summary>Keyed by the shared lookup key.</summary>
    public IReadOnlyDictionary<string, string> AmbiguityReasons { get; }

    /// <summary>
    /// Researched spellings the generator reconciled rather than dropped.
    /// </summary>
    /// <remarks>
    /// Keyed by the spelling that collided, valued by the canonical term it now
    /// reaches. This is deliberately runtime state rather than a comment in the
    /// generator: the spreadsheet publishes 3,746 terms. One spelling collision
    /// (hypertechno) is an alias. Bollywood, OPM and Swahili stay canonical musical
    /// terms and are disambiguated at runtime.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ReconciledSpellings { get; }

    /// <summary>
    /// Exclusion lookup keys that collide with a canonical term and were reconciled
    /// onto that term. They are not active exclusions. Keyed by the lookup key,
    /// valued by the canonical id that keeps the identity.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResolvedExclusionCollisions { get; }

    /// <summary>
    /// The generator's diff against the previous researched catalog.
    /// Null on a document that did not record one.
    /// </summary>
    public ResearchedGenreImportDiff? ImportDiff { get; }

    public int Genres => Terms.Count(term => term.Kind == nameof(PersonalGenreTaxonKind.Genre));

    public int Styles => Terms.Count(term => term.Kind == nameof(PersonalGenreTaxonKind.Style));

    public int ParentEdges => Terms.Sum(term => term.ParentIds.Count);

    public int MultiParentTerms => Terms.Count(term => term.ParentIds.Count > 1);

    /// <summary>The single loaded instance.</summary>
    public static ResearchedGenreCatalog Current => Instance.Value;

    /// <summary>
    /// Validates and builds a catalog from its JSON document.
    /// </summary>
    /// <remarks>
    /// This is public so a candidate catalog can be validated before it is ever
    /// adopted, which is what makes a future import path safe: the same checks run
    /// on an untrusted document as on the embedded one.
    /// </remarks>
    public static ResearchedGenreCatalog BuildFromJson(string json)
    {
        var document = JsonSerializer.Deserialize<CatalogDocument>(json, Options)
                       ?? throw new InvalidOperationException("The researched genre taxonomy document is empty.");
        return Build(document);
    }

    /// <summary>
    /// Whether a value is a researched canonical term, an alias of one, or neither.
    /// </summary>
    public bool TryMatch(string? value, out ResearchedGenreTerm term, out bool viaAlias)
    {
        viaAlias = false;
        term = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var key = Key(value);
        if (key.Length == 0)
        {
            return false;
        }

        if (_byKey.TryGetValue(key, out var found))
        {
            term = found;
            viaAlias = _aliasToId.ContainsKey(key);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the research decided this value is not a Genre or a Style.
    /// </summary>
    public bool IsExcluded(string? value)
        => !string.IsNullOrWhiteSpace(value) && _exclusionKeys.Contains(Key(value));

    /// <summary>
    /// Whether the research knows this term but has held it as not safe to classify.
    /// </summary>
    public bool IsAmbiguous(string? value)
        => !string.IsNullOrWhiteSpace(value) && _ambiguityReasons.ContainsKey(Key(value));

    public string? AmbiguityReason(string? value)
        => !string.IsNullOrWhiteSpace(value) && _ambiguityReasons.TryGetValue(Key(value), out var reason)
            ? reason
            : null;

    public string? ExclusionReason(string? value)
        => !string.IsNullOrWhiteSpace(value) && ExclusionReasons.TryGetValue(Key(value), out var reason)
            ? reason
            : null;

    public bool TryGetById(string? id, out ResearchedGenreTerm term)
    {
        term = null!;
        return !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim(), out term!);
    }

    /// <summary>
    /// Terms whose researched era contains <paramref name="year"/>. This is how a
    /// file update selects the terms that apply to a file's year. It does not
    /// invent a genre from the year.
    /// </summary>
    public IReadOnlyList<ResearchedGenreTerm> TermsActiveIn(int year)
        => Terms.Where(term => term.Research?.Era?.Contains(year) == true).ToArray();

    /// <summary>The shared lookup key. Mirrors the generator's normalize().</summary>
    private static string Key(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Trim().Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static ResearchedGenreCatalog Load()
    {
        using var stream = typeof(ResearchedGenreCatalog).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"The researched genre taxonomy resource '{ResourceName}' is missing from the assembly. "
                + "The build must embed DeezSpoTag.Services/Genre/Data/genre-intelligence-taxonomy.json.");
        }

        using var reader = new StreamReader(stream);
        var document = JsonSerializer.Deserialize<CatalogDocument>(reader.ReadToEnd(), Options)
                       ?? throw new InvalidOperationException("The researched genre taxonomy resource is empty.");

        return Build(document);
    }

    /// <summary>
    /// Validates and builds the catalog.
    /// </summary>
    /// <remarks>
    /// Every check here is a hard failure on purpose. A corrupt catalog must stop
    /// the classification rather than quietly recognise fewer terms, because a
    /// silently smaller vocabulary would make unknown values look preserved when
    /// they were really dropped.
    /// </remarks>
    public static ResearchedGenreCatalog Build(CatalogDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Version))
        {
            throw new InvalidOperationException("Researched taxonomy is corrupt: no version.");
        }

        if (document.Terms is null || document.Terms.Count == 0)
        {
            throw new InvalidOperationException("Researched taxonomy is corrupt: no terms.");
        }

        var terms = new List<ResearchedGenreTerm>(document.Terms.Count);
        var rawParents = new List<IReadOnlyList<string>>(document.Terms.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliasKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in document.Terms)
        {
            if (raw is null)
            {
                throw new InvalidOperationException("Researched taxonomy is corrupt: a null term.");
            }

            if (string.IsNullOrWhiteSpace(raw.Name))
            {
                throw new InvalidOperationException("Researched taxonomy is corrupt: a term with no name.");
            }

            var kind = raw.Kind?.Trim() ?? string.Empty;
            if (!ValidKinds.Contains(kind))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: term '{raw.Name}' has invalid kind '{raw.Kind}'.");
            }

            if (string.IsNullOrWhiteSpace(raw.Id))
            {
                throw new InvalidOperationException($"Researched taxonomy is corrupt: term '{raw.Name}' has no id.");
            }

            if (!ids.Add(raw.Id))
            {
                throw new InvalidOperationException($"Researched taxonomy is corrupt: duplicate id '{raw.Id}'.");
            }

            var nameKey = Key(raw.Name);
            if (nameKey.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: term '{raw.Name}' has no usable characters.");
            }

            if (keys.TryGetValue(nameKey, out var existing))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: '{raw.Name}' and '{existing}' share the same lookup key.");
            }

            keys[nameKey] = raw.Name;

            var regions = raw.Regions ?? [];
            foreach (var region in regions)
            {
                if (!PersonalGenreRegions.IsKnown(region))
                {
                    throw new InvalidOperationException(
                        $"Researched taxonomy is corrupt: term '{raw.Name}' has unknown region '{region}'.");
                }
            }

            // Missing parentIds is an older document. It means no researched
            // hierarchy, not a corrupt file.
            rawParents.Add(raw.ParentIds ?? []);
            terms.Add(new ResearchedGenreTerm(
                raw.Id.Trim(),
                raw.Name.Trim(),
                kind,
                regions,
                raw.Aliases ?? [],
                [],
                ReadResearch(raw.Id.Trim(), raw.Research)));
        }

        var idSet = terms.Select(term => term.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<ResearchedGenreAlias>();
        var aliasTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        var labelByKey = new Dictionary<string, string>(StringComparer.Ordinal);

        // Every alias must be listed on exactly one canonical term. That is what
        // proves an alias cannot point at two canonicals, and it is checked before
        // the alias map is trusted.
        foreach (var term in terms)
        {
            foreach (var alias in term.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    throw new InvalidOperationException(
                        $"Researched taxonomy is corrupt: term '{term.Name}' has a blank alias.");
                }

                var aliasKey = Key(alias);
                if (aliasKey.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Researched taxonomy is corrupt: term '{term.Name}' has an alias with no usable characters.");
                }

                // Checked before the duplicate-listing guard, because the same alias
                // on two different terms is the case that matters: it means the
                // alias has no single canonical meaning, and picking either one
                // would be arbitrary.
                if (aliasTargets.TryGetValue(aliasKey, out var previous)
                    && !string.Equals(previous, term.Id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Researched taxonomy is corrupt: alias '{alias}' resolves to multiple canonical terms.");
                }

                if (!aliasKeys.Add(aliasKey))
                {
                    throw new InvalidOperationException(
                        $"Researched taxonomy is corrupt: alias '{alias}' is listed more than once.");
                }

                aliasTargets[aliasKey] = term.Id;
                labelByKey[aliasKey] = alias;
            }
        }

        foreach (var entry in document.Aliases ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new InvalidOperationException("Researched taxonomy is corrupt: an alias entry with no key.");
            }

            var target = entry.Value?.Target;
            if (string.IsNullOrWhiteSpace(target) || !idSet.Contains(target!.Trim()))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: alias '{entry.Key}' targets unknown term '{target}'.");
            }

            aliases.Add(new ResearchedGenreAlias(
                labelByKey.GetValueOrDefault(entry.Key) ?? entry.Key,
                target!.Trim(),
                entry.Value?.Source));
        }

        foreach (var alias in aliases)
        {
            var aliasKey = Key(alias.Value);
            if (keys.TryGetValue(aliasKey, out var canonical))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: alias '{alias.Value}' shadows canonical term '{canonical}'.");
            }
        }

        var canonicalKeys = new HashSet<string>(keys.Keys, StringComparer.Ordinal);
        foreach (var term in terms)
        {
            canonicalKeys.Add(Key(term.Id));
            canonicalKeys.Add(Key(term.Name));
        }

        foreach (var aliasKey in aliasKeys)
        {
            canonicalKeys.Add(aliasKey);
        }

        var resolvedExclusions = new Dictionary<string, string>(
            document.ResolvedExclusionCollisions ?? new Dictionary<string, string>(),
            StringComparer.Ordinal);
        foreach (var (collisionKey, targetId) in resolvedExclusions)
        {
            if (string.IsNullOrWhiteSpace(targetId) || !idSet.Contains(targetId.Trim()))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: exclusion collision '{collisionKey}' targets unknown term '{targetId}'.");
            }
        }

        var exclusions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, entry) in document.Exclusions ?? new Dictionary<string, CatalogEntry>())
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Value))
            {
                throw new InvalidOperationException("Researched taxonomy is corrupt: an exclusion with no value.");
            }

            var exclusionKey = Key(key);
            if (exclusionKey.Length == 0)
            {
                throw new InvalidOperationException(
                    "Researched taxonomy is corrupt: an exclusion with no usable characters.");
            }

            if (canonicalKeys.Contains(exclusionKey))
            {
                if (!resolvedExclusions.TryGetValue(exclusionKey, out var target)
                    || !idSet.Contains(target.Trim()))
                {
                    throw new InvalidOperationException(
                        $"Researched taxonomy is corrupt: exclusion '{entry.Value}' collides with a canonical term "
                        + "and has no reconciliation record.");
                }

                continue;
            }

            exclusions[exclusionKey] = entry.Reason ?? "Excluded by the researched master.";
        }

        var ambiguities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, entry) in document.Ambiguous ?? new Dictionary<string, CatalogEntry>())
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Value))
            {
                throw new InvalidOperationException("Researched taxonomy is corrupt: an ambiguity with no value.");
            }

            var ambiguityKey = Key(key);
            if (ambiguityKey.Length == 0)
            {
                throw new InvalidOperationException(
                    "Researched taxonomy is corrupt: an ambiguity with no usable characters.");
            }

            if (canonicalKeys.Contains(ambiguityKey) && !resolvedExclusions.ContainsKey(ambiguityKey))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: ambiguity '{entry.Value}' collides with a canonical term "
                    + "and has no reconciliation record.");
            }

            ambiguities[ambiguityKey] = entry.Reason ?? "Held: insufficient evidence to classify.";
        }

        var byId = terms.ToDictionary(term => term.Id, StringComparer.OrdinalIgnoreCase);
        var validated = new List<ResearchedGenreTerm>(terms.Count);
        for (var index = 0; index < terms.Count; index++)
        {
            validated.Add(terms[index] with
            {
                ParentIds = NormalizeParents(terms[index], rawParents[index], byId, aliasKeys, exclusions, ambiguities)
            });
        }

        EnsureAcyclic(validated);
        EnsureCounts(document, validated, aliases, exclusions, ambiguities);

        return new ResearchedGenreCatalog(
            document.Version.Trim(),
            document.Source?.Sha256 ?? string.Empty,
            ReadSources(document.Source),
            validated,
            aliases,
            exclusions,
            ambiguities,
            document.ResolvedCanonicalCollisions ?? new Dictionary<string, string>(),
            resolvedExclusions,
            document.ImportDiff);
    }

    /// <summary>
    /// Resolves parent references to canonical term ids.
    /// </summary>
    /// <remarks>
    /// A parent is an id, never a display name, an alias, or an ambiguous label.
    /// Duplicate ids are dropped in first-seen order. A missing id, a self-parent,
    /// an alias, an exclusion, or an ambiguous label fails the catalog.
    /// </remarks>
    private static IReadOnlyList<string> NormalizeParents(
        ResearchedGenreTerm term,
        IReadOnlyList<string> rawParents,
        IReadOnlyDictionary<string, ResearchedGenreTerm> byId,
        IReadOnlySet<string> aliasKeys,
        IReadOnlyDictionary<string, string> exclusions,
        IReadOnlyDictionary<string, string> ambiguities)
    {
        var parents = new List<string>(rawParents.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawParents)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: term '{term.Id}' has a blank parent id.");
            }

            var token = raw.Trim();
            if (!byId.TryGetValue(token, out var parent))
            {
                var key = Key(token);
                if (aliasKeys.Contains(key))
                {
                    throw new InvalidOperationException(
                        $"Researched taxonomy is corrupt: term '{term.Id}' parents alias '{token}'. "
                        + "Resolve the alias to its canonical id before writing parentIds.");
                }

                if (ambiguities.ContainsKey(key))
                {
                    throw new InvalidOperationException(
                        $"Researched taxonomy is corrupt: term '{term.Id}' uses ambiguous label '{token}' as a parent id.");
                }

                if (exclusions.ContainsKey(key))
                {
                    throw new InvalidOperationException(
                        $"Researched taxonomy is corrupt: term '{term.Id}' parents exclusion '{token}'.");
                }

                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: term '{term.Id}' parents unknown id '{token}'.");
            }

            if (string.Equals(parent.Id, term.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: term '{term.Id}' parents itself.");
            }

            if (parent.Kind is not (nameof(PersonalGenreTaxonKind.Genre) or nameof(PersonalGenreTaxonKind.Style)))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: parent '{parent.Id}' of '{term.Id}' is not a Genre or Style.");
            }

            var parentNameKey = Key(parent.Name);
            if (exclusions.ContainsKey(parentNameKey) || exclusions.ContainsKey(Key(parent.Id)))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: term '{term.Id}' parents exclusion '{parent.Id}'.");
            }

            if (!seen.Add(parent.Id))
            {
                continue;
            }

            parents.Add(parent.Id);
        }

        return parents;
    }

    /// <summary>
    /// Rejects a parent cycle and names every id on it.
    /// </summary>
    private static void EnsureAcyclic(IReadOnlyList<ResearchedGenreTerm> terms)
    {
        var byId = terms.ToDictionary(term => term.Id, StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();

        foreach (var term in terms)
        {
            Visit(term.Id);
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
                        "Researched taxonomy is corrupt: parent cycle " + string.Join(" -> ", cycle) + ".");
                }

                return;
            }

            state[id] = 1;
            stack.Add(id);
            if (byId.TryGetValue(id, out var term))
            {
                foreach (var parentId in term.ParentIds)
                {
                    Visit(parentId);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }
    }

    private static void EnsureCounts(
        CatalogDocument document,
        IReadOnlyList<ResearchedGenreTerm> terms,
        IReadOnlyList<ResearchedGenreAlias> aliases,
        IReadOnlyDictionary<string, string> exclusions,
        IReadOnlyDictionary<string, string> ambiguities)
    {
        if (document.Counts is null)
        {
            return;
        }

        var counts = document.Counts;
        var genres = terms.Count(term => term.Kind == nameof(PersonalGenreTaxonKind.Genre));
        var styles = terms.Count(term => term.Kind == nameof(PersonalGenreTaxonKind.Style));
        var parentEdges = terms.Sum(term => term.ParentIds.Count);
        var multiParent = terms.Count(term => term.ParentIds.Count > 1);
        RequireCount("genres", counts.Genres, genres);
        RequireCount("styles", counts.Styles, styles);
        RequireCount("terms", counts.Terms, terms.Count);
        RequireCount("aliases", counts.Aliases, aliases.Count);
        RequireCount("exclusions", counts.Exclusions, exclusions.Count);
        RequireCount("ambiguous", counts.Ambiguous, ambiguities.Count);
        RequireCount("parentEdges", counts.ParentEdges, parentEdges);
        RequireCount("multiParentTerms", counts.MultiParentTerms, multiParent);
    }

    private static void RequireCount(string name, int? declared, int actual)
    {
        if (declared is null || declared.Value == actual)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Researched taxonomy is corrupt: counts.{name} is {declared.Value} but the document contains {actual}.");
    }

    private static ResearchedGenreResearch? ReadResearch(string termId, CatalogResearch? research)
    {
        if (research is null || string.IsNullOrWhiteSpace(research.Status))
        {
            return null;
        }

        var geo = GenreResearchGeoCatalog.Current;
        return new ResearchedGenreResearch(
            research.Status.Trim(),
            BlankToNull(research.Label),
            ReadEra(termId, research.Era),
            research.RegionTokens ?? [],
            ReadGeoSlugs(termId, "place", research.Places, geo.IsPlace),
            ReadGeoSlugs(termId, "country", research.MemberCountries, geo.IsCountry),
            BlankToNull(research.ResearchedKind));
    }

    private static ResearchEra? ReadEra(string termId, CatalogEra? era)
    {
        if (era is null || string.IsNullOrWhiteSpace(era.Label) && era.Ranges is not { Count: > 0 })
        {
            return null;
        }

        var ranges = new List<ResearchEraRange>();
        foreach (var range in era.Ranges ?? [])
        {
            if (range is null)
            {
                throw new InvalidOperationException($"Researched taxonomy is corrupt: term '{termId}' has an empty era range.");
            }

            if (range.Start is < 1 or > 2100)
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: term '{termId}' has era start '{range.Start}'.");
            }

            if (range.End is < 1 or > 2100 || range.End < range.Start)
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: term '{termId}' has era end '{range.End}'.");
            }

            ranges.Add(new ResearchEraRange(range.Start, range.End));
        }

        return new ResearchEra(era.Label?.Trim() ?? string.Empty, ranges);
    }

    private static IReadOnlyList<string> ReadGeoSlugs(
        string termId,
        string kind,
        List<string>? values,
        Func<string, bool> known)
    {
        var slugs = new List<string>();
        foreach (var value in values ?? [])
        {
            var slug = value?.Trim() ?? string.Empty;
            if (slug.Length == 0)
            {
                continue;
            }

            if (!known(slug))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: term '{termId}' cites unknown {kind} '{slug}'.");
            }

            if (!slugs.Contains(slug))
            {
                slugs.Add(slug);
            }
        }

        return slugs;
    }

    private static string? BlankToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<ResearchedGenreSource> ReadSources(CatalogSource? source)
    {
        if (source?.Inputs is { Count: > 0 })
        {
            return source.Inputs
                .Select(input => new ResearchedGenreSource(
                    input.Role ?? string.Empty,
                    input.File ?? string.Empty,
                    input.Sha256 ?? string.Empty))
                .ToArray();
        }

        if (string.IsNullOrWhiteSpace(source?.Sha256) && string.IsNullOrWhiteSpace(source?.File))
        {
            return [];
        }

        return
        [
            new ResearchedGenreSource("vocabulary", source?.File ?? string.Empty, source?.Sha256 ?? string.Empty)
        ];
    }

    // ------------------------------------------------------------------ document shape

    public sealed class CatalogDocument
    {
        public string? Version { get; set; }
        public CatalogSource? Source { get; set; }
        public CatalogCounts? Counts { get; set; }
        public ResearchedGenreImportDiff? ImportDiff { get; set; }
        public List<CatalogTerm>? Terms { get; set; }
        public Dictionary<string, CatalogAlias>? Aliases { get; set; }
        public Dictionary<string, CatalogEntry>? Exclusions { get; set; }
        public Dictionary<string, CatalogEntry>? Ambiguous { get; set; }

        /// <summary>Researched spellings the generator reconciled. Empty means none.</summary>
        public Dictionary<string, string>? ResolvedCanonicalCollisions { get; set; }

        /// <summary>
        /// Exclusion keys that collide with a canonical term. The value is the canonical
        /// id that remains reachable. Those exclusions are not active.
        /// </summary>
        public Dictionary<string, string>? ResolvedExclusionCollisions { get; set; }
    }

    public sealed class CatalogSource
    {
        public string? File { get; set; }
        public string? Sha256 { get; set; }
        public string? Note { get; set; }
        public List<CatalogSourceInput>? Inputs { get; set; }
    }

    public sealed class CatalogSourceInput
    {
        public string? Role { get; set; }
        public string? File { get; set; }
        public string? Sha256 { get; set; }
    }

    public sealed class CatalogCounts
    {
        public int? Genres { get; set; }
        public int? Styles { get; set; }
        public int? Terms { get; set; }
        public int? Aliases { get; set; }
        public int? Regions { get; set; }
        public int? Exclusions { get; set; }
        public int? Ambiguous { get; set; }
        public int? ParentEdges { get; set; }
        public int? MultiParentTerms { get; set; }
    }

    public sealed class CatalogTerm
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public List<string>? Regions { get; set; }
        public List<string>? Aliases { get; set; }
        public List<string>? ParentIds { get; set; }
        public CatalogResearch? Research { get; set; }
    }

    public sealed class CatalogResearch
    {
        public string? Status { get; set; }
        public string? Label { get; set; }
        public CatalogEra? Era { get; set; }
        public List<string>? RegionTokens { get; set; }
        public List<string>? Places { get; set; }
        public List<string>? MemberCountries { get; set; }
        public string? ResearchedKind { get; set; }
    }

    public sealed class CatalogEra
    {
        public string? Label { get; set; }

        public List<CatalogEraRange>? Ranges { get; set; }
    }

    public sealed class CatalogEraRange
    {
        public int Start { get; set; }

        public int? End { get; set; }
    }

    public sealed class CatalogAlias
    {
        public string? Value { get; set; }
        public string? Target { get; set; }
        public string? Source { get; set; }
    }

    public sealed class CatalogEntry
    {
        public string? Value { get; set; }
        public string? Reason { get; set; }
        public string? Source { get; set; }
    }
}

/// <summary>One research artifact recorded in the catalog provenance manifest.</summary>
public sealed record ResearchedGenreSource(string Role, string File, string Sha256);

/// <summary>
/// Generator summary of what an import changed relative to the previous catalog.
/// Destructive counts are expected to be zero for an additive batch.
/// </summary>
public sealed record ResearchedGenreImportDiff
{
    public string? PreviousVersion { get; set; }
    public int TermsAdded { get; set; }
    public int TermsRemoved { get; set; }
    public int TermsChanged { get; set; }
    public int GenresAdded { get; set; }
    public int StylesAdded { get; set; }
    public int ParentEdgesAdded { get; set; }
    public int ParentEdgesRemoved { get; set; }
    public int MultiParentTermsAdded { get; set; }
    public int AliasesAdded { get; set; }
    public int AliasesRemoved { get; set; }
    public int KindChanges { get; set; }
    public int IdChanges { get; set; }
    public int RegionChanges { get; set; }
    public int ExcludedRows { get; set; }
}
