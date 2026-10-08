namespace DeezSpoTag.Services.Genre;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// One researched canonical term from the runtime catalog.
/// </summary>
public sealed record ResearchedGenreTerm(
    string Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Aliases);

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
        IReadOnlyList<ResearchedGenreTerm> terms,
        IReadOnlyList<ResearchedGenreAlias> aliases,
        IReadOnlyDictionary<string, string> exclusions,
        IReadOnlyDictionary<string, string> ambiguities,
        IReadOnlyDictionary<string, string> reconciledSpellings)
    {
        Version = version;
        SourceSha256 = sourceSha256;
        Terms = terms;
        Aliases = aliases;
        ExclusionReasons = exclusions;
        AmbiguityReasons = ambiguities;
        ReconciledSpellings = reconciledSpellings;

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
    /// The SHA-256 of the research spreadsheet the catalog was generated from. It is
    /// provenance, not a runtime check: the spreadsheet is not a runtime dependency.
    /// </summary>
    public string SourceSha256 { get; }

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
    /// generator: the research publishes 3,746 terms while the catalog carries 3,745
    /// canonical identities, and that difference has to stay inspectable rather than
    /// looking like a lost term.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ReconciledSpellings { get; }

    public int Genres => Terms.Count(term => term.Kind == nameof(PersonalGenreTaxonKind.Genre));

    public int Styles => Terms.Count(term => term.Kind == nameof(PersonalGenreTaxonKind.Style));

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

            terms.Add(new ResearchedGenreTerm(
                raw.Id.Trim(),
                raw.Name.Trim(),
                kind,
                regions,
                raw.Aliases ?? []));
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
            if (keys.ContainsKey(aliasKey))
            {
                throw new InvalidOperationException(
                    $"Researched taxonomy is corrupt: alias '{alias.Value}' shadows canonical term '{keys[aliasKey]}'.");
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

            ambiguities[ambiguityKey] = entry.Reason ?? "Held: insufficient evidence to classify.";
        }

        return new ResearchedGenreCatalog(
            document.Version.Trim(),
            document.Source?.Sha256 ?? string.Empty,
            terms,
            aliases,
            exclusions,
            ambiguities,
            document.ResolvedCanonicalCollisions ?? new Dictionary<string, string>());
    }

    // ------------------------------------------------------------------ document shape

    public sealed class CatalogDocument
    {
        public string? Version { get; set; }
        public CatalogSource? Source { get; set; }
        public List<CatalogTerm>? Terms { get; set; }
        public Dictionary<string, CatalogAlias>? Aliases { get; set; }
        public Dictionary<string, CatalogEntry>? Exclusions { get; set; }
        public Dictionary<string, CatalogEntry>? Ambiguous { get; set; }

        /// <summary>Researched spellings the generator reconciled. Empty means none.</summary>
        public Dictionary<string, string>? ResolvedCanonicalCollisions { get; set; }
    }

    public sealed class CatalogSource
    {
        public string? Sha256 { get; set; }
    }

    public sealed class CatalogTerm
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public List<string>? Regions { get; set; }
        public List<string>? Aliases { get; set; }
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
