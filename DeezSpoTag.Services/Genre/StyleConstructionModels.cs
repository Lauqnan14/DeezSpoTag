namespace DeezSpoTag.Services.Genre;

public enum ConstructionEvidenceScope { Track, Album, Artist }
public enum ConstructionEvidenceOrigin { ExplicitFile, UserMapped, UserRule }
public enum ConstructionTransformation { Normalized, AliasCanonicalized, FieldMoved }
public enum ConstructionArtistRole { Unknown, Main, Featured }

/// <summary>Qualified explicit evidence, never a constructed classification.</summary>
public sealed record StyleConstructionSemanticFact
{
    public required string FactId { get; init; }
    public required PersonalGenreTaxonKind Kind { get; init; }
    public required string CanonicalId { get; init; }
    public required string CanonicalValue { get; init; }
    public required ConstructionEvidenceScope Scope { get; init; }
    public required ConstructionEvidenceOrigin Origin { get; init; }
    public IReadOnlyList<ConstructionTransformation> Transformations { get; init; } = [];
    public required string Source { get; init; }
    public required string ProvenanceReference { get; init; }
    public required string OriginalValue { get; init; }
    public required PersonalGenreTaxonKind OriginalField { get; init; }
    public long? ArtistId { get; init; }
    public ConstructionArtistRole ArtistRole { get; init; } = ConstructionArtistRole.Unknown;
    public string? ArtistBindingReference { get; init; }
    public required DateTimeOffset KnownAt { get; init; }
}

public sealed record StyleConstructionUserAuthority(IReadOnlyList<PersonalGenreLock> StyleLocks);

/// <summary>
/// A structured binding from the track under review to one artist.
/// </summary>
/// <remarks>
/// <para>
/// Construction evidence used to carry <see cref="StyleConstructionSemanticFact.ArtistId"/>,
/// <see cref="ConstructionArtistRole"/> and a binding reference that nothing ever
/// populated, which left every role-qualified predicate satisfiable only by
/// hand-built test facts. The binding is now supplied by the caller and recorded on
/// each fact, so <c>HasArtistBinding</c> reflects real data.
/// </para>
/// <para>
/// <see cref="Role"/> is deliberately <see cref="ConstructionArtistRole.Unknown"/>
/// whenever the source cannot separate the track's own performers from the album
/// artist. An album artist is not a track's main artist: a compilation credits every
/// track to one album artist while each track has its own performer. Inferring
/// <c>Main</c> from that would assert something the data does not say, so the role
/// stays unknown and role-qualified predicates fail closed.
/// </para>
/// <para>
/// <see cref="BindingReference"/> records how the artist was reached — for example
/// <c>library-album-artist:42</c> — so a consumer can tell a weak album-artist
/// binding from a genuine track-credit one.
/// </para>
/// </remarks>
public sealed record StyleConstructionArtistIdentity(
    long ArtistId,
    ConstructionArtistRole Role,
    string BindingReference,
    ConstructionEvidenceScope Scope);

/// <summary>
/// Artist location retained as structured evidence.
/// </summary>
/// <remarks>
/// <para>
/// Carried with its provenance so a future consumer can reason about where a
/// location came from and how old it is. Nothing derives a Genre, a Style or any
/// other term from it: there is no <c>Country + Genre -&gt; Style</c> path anywhere
/// in this code, and no production rule is enabled.
/// </para>
/// <para>
/// The provenance is also retained by <see cref="PersonalGenreResolver"/> as an
/// annotation on a decision's reason when the value already mentions the place.
/// </para>
/// </remarks>
public sealed record StyleConstructionArtistLocation(
    string? Country,
    string? City,
    string? Region,
    string Source,
    string? SourceReference,
    DateTimeOffset? KnownAt,
    string? ResolutionMethod)
{
    /// <summary>Whether this evidence carries any geography at all.</summary>
    public bool HasValue =>
        !string.IsNullOrWhiteSpace(Country)
        || !string.IsNullOrWhiteSpace(Region)
        || !string.IsNullOrWhiteSpace(City);
}

public sealed record StyleConstructionEvidenceSnapshot(
    int SchemaVersion,
    long TrackId,
    DateTimeOffset SnapshotAt,
    string RecognitionCatalogVersion,
    string CleanupResolverVersion,
    IReadOnlyList<StyleConstructionSemanticFact> SemanticFacts,
    StyleConstructionUserAuthority UserAuthority,
    /// <summary>
    /// The artist the facts are bound to, or null when no identity could be
    /// established. A null binding leaves every fact's artist fields at their
    /// defaults, so a role-qualified predicate cannot match.
    /// </summary>
    StyleConstructionArtistIdentity? ArtistIdentity = null,
    /// <summary>
    /// Structured location evidence for that artist. Retained, never acted on.
    /// </summary>
    StyleConstructionArtistLocation? ArtistLocation = null);

public enum ConstructionResearchDisposition { RecognitionOnly, Inferable }
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum StyleConstructionOutcome { Qualified, MissingEvidence, Conflicted, SuppressedByUserAuthority, RecognitionOnly }

public sealed record StyleConstructionPredicateAlternative(
    PersonalGenreTaxonKind Kind, string CanonicalId, ConstructionArtistRole? ArtistRole = null);

public sealed record StyleConstructionPredicateGroup(string PredicateId, IReadOnlyList<StyleConstructionPredicateAlternative> AnyOf);

public sealed record StyleConstructionResearchSource(
    string Title, string Url, string SourceType, string Access, string ObservedAt, IReadOnlyList<string> SupportedClaims);

/// <summary>Research descriptions are not executable predicates.</summary>
public sealed record StyleConstructionResearch
{
    public required string Definition { get; init; }
    public required string MusicalAncestry { get; init; }
    public required string RequiredParentGenres { get; init; }
    public required string ExistingStyleRelationships { get; init; }
    public required string SceneRelationship { get; init; }
    public required string CountryRelationship { get; init; }
    public required string CityRelationship { get; init; }
    public required string LanguageRelationship { get; init; }
    public required string EmergencePeriod { get; init; }
    public required string EraRequirement { get; init; }
    public required string GeographyRequirement { get; init; }
    public required IReadOnlyList<string> RecognitionAliases { get; init; }
    public required string SufficientEvidence { get; init; }
    public required IReadOnlyList<string> InsufficientEvidence { get; init; }
    public required IReadOnlyList<string> FalsePositives { get; init; }
    public required IReadOnlyList<string> DisqualifyingCases { get; init; }
    public required IReadOnlyList<string> ResearchGaps { get; init; }
}

public sealed record StyleConstructionRule
{
    public required string RuleId { get; init; }
    public required string Version { get; init; }
    public required string TargetStyleId { get; init; }
    public required bool Enabled { get; init; }
    public required bool InferenceAllowed { get; init; }
    public required ConstructionResearchDisposition ResearchDisposition { get; init; }
    public required IReadOnlyList<StyleConstructionPredicateGroup> RequiredAll { get; init; }
    public required IReadOnlyList<StyleConstructionPredicateGroup> Supporting { get; init; }
    public required IReadOnlyList<StyleConstructionPredicateGroup> ForbiddenAny { get; init; }
    public required IReadOnlyList<ConstructionEvidenceOrigin> AllowedEvidenceOrigins { get; init; }
    public required IReadOnlyList<ConstructionEvidenceScope> AllowedScopes { get; init; }
    public required IReadOnlyList<StyleConstructionResearchSource> ResearchSources { get; init; }
    public required string ResearchRationale { get; init; }
    public required StyleConstructionResearch Research { get; init; }
}

public sealed record StyleConstructionRejectedAlternative(string PredicateId, string CanonicalId, string? FactId, string Reason);

public sealed record StyleConstructionDecision(
    string TargetStyleId, string TargetStyleName, string RuleId, string RuleVersion,
    StyleConstructionOutcome Outcome, IReadOnlyList<string> SatisfiedPredicates,
    IReadOnlyList<string> MissingPredicates, IReadOnlyList<StyleConstructionSemanticFact> ConflictingFacts,
    IReadOnlyList<StyleConstructionSemanticFact> EvidenceUsed,
    IReadOnlyList<StyleConstructionRejectedAlternative> RejectedAlternatives, string Explanation,
    StyleConstructionResearch Research, IReadOnlyList<StyleConstructionResearchSource> ResearchSources);

public sealed record StyleConstructionPreview(
    StyleConstructionEvidenceSnapshot Snapshot, string DatasetVersion, IReadOnlyList<StyleConstructionDecision> Decisions);
