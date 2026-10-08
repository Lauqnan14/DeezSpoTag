namespace DeezSpoTag.Services.Genre;

using DeezSpoTag.Core.Utils;

/// <summary>
/// Which point in the AutoTag workflow a file snapshot was taken at.
/// </summary>
public enum GenreSnapshotStage
{
    /// <summary>The file's own tags, read before the first AutoTag platform ran.</summary>
    PreAutoTag,

    /// <summary>The file's tags after the AutoTag platforms wrote theirs.</summary>
    PostAutoTag,

    /// <summary>Evidence captured by the removed provider-based implementation.</summary>
    Legacy
}

public enum PersonalGenreTaxonKind
{
    Genre,
    Style,
    Substyle,
    Context,
    Scene,
    Language
}

/// <summary>
/// Where a semantic observation came from within the AutoTag workflow.
///
/// This is a workflow position, not a metadata-provider identity. The resolver
/// has no opinion about which service produced a value; the only thing it can
/// legitimately know is whether the value was present before AutoTag ran or
/// appeared because AutoTag wrote it.
/// </summary>
public enum GenreObservationOrigin
{
    /// <summary>Read from the audio file after the AutoTag platforms wrote their tags.</summary>
    PostPlatform,

    /// <summary>
    /// Present in the pre-AutoTag semantic snapshot and not replaced by any
    /// platform. It is carried forward so a personal value is never erased just
    /// because the taxonomy does not know it.
    /// </summary>
    PreservedOriginal
}

/// <summary>
/// One taxonomy term.
///
/// <para>
/// <paramref name="Regions"/> is organizational metadata only: it says which regional
/// vocabulary views this term belongs to, using the slugs in
/// <see cref="PersonalGenreRegions"/>. It never gates resolution. A null or empty
/// collection means the term is unscoped, which is how every universal genre
/// (Pop, Rock, Hip-Hop) is expressed and how every pre-region custom term keeps
/// working unchanged.
/// </para>
/// <para>
/// Region membership is many-to-many on purpose. A term such as Rai belongs to both
/// Africa and MENA, because those groupings overlap; giving it one owner would either
/// duplicate the canonical term or force a false choice. Universal terms are not
/// duplicated per region, so the same <c>hip-hop</c> entry is visible from several
/// region views while remaining exactly one taxonomy entry.
/// </para>
/// </summary>
public sealed record PersonalGenreTaxon(
    string Id,
    string Name,
    PersonalGenreTaxonKind Kind,
    IReadOnlyList<string>? ParentIds = null,
    bool ContextOnly = false,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Regions = null);

/// <summary>
/// One semantic value Genre Intelligence actually read out of an audio file tag.
///
/// An observation records what was in the file, in which field, and in which
/// order. It carries no provider identity, no weight and no authority: the file
/// is the source of truth for this stage, so there is nothing to weigh it
/// against.
///
/// <para>
/// <paramref name="RawValue"/> is the spelling the cleanup engine classifies. Once
/// the shared normalization preferences have run, that is the spelling the user's
/// configuration produces, and <paramref name="OriginalValue"/> is what the file
/// actually held. Keeping both is what lets a run say "the file said Hip Hop, the
/// saved preference writes Hip-Hop" instead of losing one of the two facts.
/// </para>
/// </summary>
public sealed record GenreTagObservation(
    string RawValue,
    PersonalGenreTaxonKind InputField,
    int Order = 0,
    GenreObservationOrigin Origin = GenreObservationOrigin.PostPlatform)
{
    /// <summary>
    /// The value as it was found in the file, before the shared normalization
    /// preferences ran. Null means no normalization changed it, so
    /// <see cref="RawValue"/> is both the original and the current spelling.
    /// </summary>
    public string? OriginalValue { get; init; }

    /// <summary>
    /// Whether the shared normalization preferences changed this value's spelling.
    /// </summary>
    public bool WasNormalized
        => OriginalValue is not null
           && !string.Equals(OriginalValue, RawValue, StringComparison.Ordinal);
}

/// <summary>
/// The semantic state of one audio file as read from its tags, before any
/// resolution or rewrite happened.
/// </summary>
public sealed record GenreSemanticSnapshot(
    IReadOnlyList<GenreTagObservation> Observations,
    DateTimeOffset ReadAtUtc);

/// <summary>
/// Optional geographic context for disambiguating a value that is already in
/// the file. Genre Intelligence never creates a term from this: a location can
/// support a value the file already contains and nothing more.
/// </summary>
public sealed record GenreArtistLocationContext(
    string? Country,
    string? Region,
    string? City,
    bool IsMainArtist = true);

public enum PersonalGenreMappingAction
{
    Map,
    ContextOnly,
    Ignore,
    Ambiguous
}

/// <summary>
/// How one value found in a file should be interpreted. Matching is scoped to
/// the file field the value was read from, not to a metadata provider.
/// </summary>
public sealed record PersonalGenreMapping(
    long Id,
    string MatchValue,
    string TargetTaxonId,
    PersonalGenreTaxonKind? InputField = null,
    int Priority = 100,
    bool Enabled = true,
    PersonalGenreMappingAction Action = PersonalGenreMappingAction.Map);

/// <summary>
/// An explicit user instruction about one observed value. Rules outrank
/// provider-equivalent mappings and built-in taxonomy matching.
/// </summary>
public sealed record PersonalGenreRule(
    long Id,
    string MatchValue,
    string TargetTaxonId,
    PersonalGenreTaxonKind? InputField = null,
    int Priority = 1000,
    bool Enabled = true);

public sealed record PersonalGenreLock(
    long TrackId,
    string TaxonId,
    bool Enabled = true,
    DateTimeOffset? UpdatedAtUtc = null,
    string? ScopeType = null,
    long? ScopeId = null);

public sealed record PersonalGenreScopedLock(
    string ScopeType,
    long ScopeId,
    string TaxonId,
    bool Enabled = true,
    DateTimeOffset? UpdatedAtUtc = null);

public sealed record PersonalGenreTrackScope(
    long TrackId,
    long AlbumId,
    long ArtistId);

/// <summary>
/// One user-defined spelling preference: a value the file may contain, and the
/// form the user wants instead.
///
/// This is deliberately not a taxonomy alias. A taxonomy alias is part of the
/// term's definition and is fixed once the term exists; a normalization rule is an
/// editable preference about how the user's files are written, and has to stay
/// editable for as long as the user can edit it.
/// </summary>
public sealed record PersonalGenreAliasRule(string Alias, string Canonical);

/// <summary>
/// Shared Genre Intelligence configuration.
///
/// The three normalization fields are owned here rather than in the general
/// application settings. They are applied before classification, as a spelling
/// step, so moving their ownership does not change what they mean to any
/// consumer.
/// </summary>
public sealed record PersonalGenreSettings(
    bool Enabled = true,
    int MaxGenres = 3,
    bool PreserveUnmappedTags = true,
    bool IncludeParentGenres = false,
    bool NormalizeGenreTags = false,
    IReadOnlyList<PersonalGenreAliasRule>? GenreTagAliasRules = null,
    IReadOnlyList<string>? GenreTagBlockList = null)
{
    /// <summary>
    /// The effective alias rules, falling back to the shipped defaults when a user
    /// has never configured any. Kept as a property so a record comparison does
    /// not see null and an empty list as different intents.
    /// </summary>
    public IReadOnlyList<PersonalGenreAliasRule> EffectiveAliasRules
        => GenreTagAliasRules ?? DefaultAliasRules;

    public IReadOnlyList<string> EffectiveBlockList
        => GenreTagBlockList ?? GenreTagAliasNormalizer.DefaultBlockedGenres;

    /// <summary>
    /// The aliases DeezSpoTag ships with. They are the values a user starts from,
    /// not a floor: a user who edits the list is expected to see their own values
    /// back, so an explicitly emptied list stays empty.
    /// </summary>
    public static IReadOnlyList<PersonalGenreAliasRule> DefaultAliasRules { get; } =
    [
        new("Afro-Pop", "Afropop"),
        new("Afro Pop", "Afropop"),
        new("Hip-Hop", "HipHop"),
        new("Hip Hop", "HipHop")
    ];
}

/// <summary>
/// One taxon Genre Intelligence decided about, together with the file fields it
/// was observed in. <see cref="OriginFields"/> is what makes a field correction
/// legible: a value read from GENRE that resolves to a Style shows up as Genre
/// in its origin fields and Style in its kind.
/// </summary>
public sealed record PersonalGenreClassification(
    string TaxonId,
    string Name,
    PersonalGenreTaxonKind Kind,
    IReadOnlyList<PersonalGenreTaxonKind> OriginFields,
    bool UserLocked = false,
    string Status = "suggested");

/// <summary>
/// A value found in the file that the taxonomy does not (yet) know, kept intact
/// so it is written back to the field it came from.
///
/// This is deliberately not a taxon. It is not classified, it does not compete
/// for the genre slots and it carries no confidence. It exists so the user can
/// see it and then create a custom taxon, an alias, a mapping or a rule for it.
/// </summary>
public sealed record PreservedTagValue(
    string Value,
    PersonalGenreTaxonKind InputField,
    int Order,
    GenreObservationOrigin Origin);

/// <summary>
/// Why one value found in a file ended up where it did.
/// </summary>
/// <param name="ObservationIndex">Position in the file's own field ordering.</param>
/// <param name="RawValue">
/// The value exactly as the file held it. This deliberately keeps its original
/// meaning so history written before normalization ran stays readable.
/// </param>
/// <param name="InputField">The file field it was read from.</param>
/// <param name="Outcome">
/// The outcome as a stable token, e.g. <c>canonical_match</c>, <c>alias_match</c>,
/// <c>preserved_unmapped</c>, <c>blocked</c>.
/// </param>
/// <param name="TaxonId">The taxonomy term it resolved to, when it resolved to one.</param>
/// <param name="Reason">A human-readable explanation of the outcome.</param>
/// <param name="NormalizedValue">
/// The spelling the shared Genre Normalization preferences produced from
/// <paramref name="RawValue"/>, when they changed it. Null when they did not.
/// </param>
/// <param name="CanonicalValue">
/// The canonical term name the value was recognised as. Null when the value was
/// not recognised, or when it was deliberately not classified.
/// </param>
public sealed record PersonalGenreEvidenceDecision(
    int ObservationIndex,
    string RawValue,
    PersonalGenreTaxonKind InputField,
    string Outcome,
    string? TaxonId,
    string Reason,
    string? NormalizedValue = null,
    string? CanonicalValue = null);

public sealed record PersonalGenreResolution(
    string? PrimaryGenre,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Styles,
    IReadOnlyList<string> Substyles,
    IReadOnlyList<string> Contexts,
    IReadOnlyList<string> Scenes,
    IReadOnlyList<string> Languages,
    IReadOnlyList<PreservedTagValue> Preserved,
    IReadOnlyList<PersonalGenreClassification> Classifications,
    IReadOnlyList<PersonalGenreEvidenceDecision> Decisions,
    IReadOnlyList<string> AppliedRuleIds,
    IReadOnlyList<GenreTagObservation> Observations,
    string ResolverVersion);

public sealed record PersonalGenreTrackResult(
    long TrackId,
    PersonalGenreResolution Resolution,
    DateTimeOffset ResolvedAtUtc,
    GenreSemanticSnapshot? PreAutoTagSnapshot = null,
    GenreSemanticSnapshot? PostAutoTagSnapshot = null);


public sealed record PersonalGenreConfiguration(
    int SchemaVersion,
    string TaxonomyVersion,
    DateTimeOffset ExportedAtUtc,
    PersonalGenreSettings Settings,
    IReadOnlyList<PersonalGenreTaxon> CustomTaxa,
    IReadOnlyList<PersonalGenreMapping> Mappings,
    IReadOnlyList<PersonalGenreRule> Rules);

public sealed record PersonalGenreImportResult(
    int CustomTaxaImported,
    int MappingsImported,
    int RulesImported,
    int DuplicatesSkipped);


public sealed record PersonalGenreRebuildResult(
    int Processed,
    int Resolved,
    int Skipped,
    long LastTrackId,
    bool HasMore);


public sealed record PersonalGenreResolutionHistoryItem(
    long Id,
    long TrackId,
    string? PrimaryGenre,
    string ResolverVersion,
    DateTimeOffset ResolvedAtUtc,
    int ObservationCount,
    int ClassificationCount,
    IReadOnlyList<PersonalGenreEvidenceDecision> Decisions);
