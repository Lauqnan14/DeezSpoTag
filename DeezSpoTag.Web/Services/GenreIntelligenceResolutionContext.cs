namespace DeezSpoTag.Web.Services;

using DeezSpoTag.Services.Genre;

/// <summary>
/// How a caller is allowed to obtain artist-location evidence.
/// </summary>
/// <remarks>
/// <para>
/// Location is annotation only: <see cref="PersonalGenreResolver"/> appends it to a
/// decision's reason string and never lets it introduce, rename or reclassify a
/// term. That is what makes the distinction below safe: the only thing a caller's
/// policy can change is whether a reason sentence mentions a corroborated place.
/// </para>
/// <para>
/// The policy exists so the network boundary is a stated decision rather than an
/// accident of which service method happened to omit an argument. A preview must
/// render without starting a provider lookup.
/// </para>
/// </remarks>
public enum GenreIntelligenceLocationPolicy
{
    /// <summary>
    /// Only evidence already stored on this machine. A manual artist-location
    /// override qualifies; resolving through MusicBrainz or Audiomack does not.
    /// </summary>
    StoredOnly,

    /// <summary>
    /// May resolve through the configured provider chain
    /// (manual override, then MusicBrainz, then Audiomack). Only an AutoTag run,
    /// which already spends network budget on metadata, uses this.
    /// </summary>
    ProviderChain,
}

/// <summary>
/// Artist location together with the provenance that produced it.
/// </summary>
/// <remarks>
/// Carried as structured evidence so a future consumer can reason about where a
/// location came from. Nothing today derives a Genre, a Style or any other term
/// from it: the only reader is the resolver's reason annotation, and no Style
/// Construction rule is enabled, so no <c>Country + Genre -&gt; Style</c> shortcut
/// exists to take.
/// </remarks>
public sealed record GenreArtistLocationProvenance(
    string? Country,
    string? City,
    string? Region,
    string Source,
    string? SourceReference,
    DateTimeOffset? KnownAt,
    string? ResolutionMethod)
{
    /// <summary>Whether this evidence carries anything worth annotating with.</summary>
    public bool HasValue =>
        !string.IsNullOrWhiteSpace(Country)
        || !string.IsNullOrWhiteSpace(Region)
        || !string.IsNullOrWhiteSpace(City);

    /// <summary>
    /// The resolver-facing projection. The resolver only reads the geographic
    /// strings and the main-artist flag; provenance is deliberately not passed on,
    /// because the resolver has no behaviour keyed to it.
    /// </summary>
    public IReadOnlyList<GenreArtistLocationContext> ToGenreContext()
        => HasValue
            ? [new GenreArtistLocationContext(Country, Region, City, IsMainArtist: true)]
            : [];
}

/// <summary>
/// Everything <see cref="PersonalGenreResolver.Resolve"/> needs, assembled once.
/// </summary>
/// <remarks>
/// <para>
/// Every path that interprets a file's semantic tags goes through
/// <see cref="Resolve"/>, so the AutoTag terminal stage, the cleanup preview, the
/// Style Construction preview and a manual re-resolve cannot disagree about what
/// the resolver was told. Building this is pure reading: it opens no second file,
/// writes nothing and persists nothing, which is what lets a preview endpoint use
/// it and stay read-only.
/// </para>
/// <para>
/// <see cref="RemovedByNormalization"/> is retained on the context because two
/// callers need it and neither should recompute it: the AutoTag stage builds the
/// cleanup preview that reports blocked values, and the evidence builder uses it
/// to keep a blocked value out of construction evidence.
/// </para>
/// </remarks>
public sealed record GenreIntelligenceResolutionContext(
    PersonalGenreSettings Settings,
    GenreNormalizationSnapshot Normalization,
    GenreSemanticSnapshot PostSnapshot,
    GenreNormalizationResult NormalizedPost,
    GenreNormalizationResult? NormalizedPre,
    IReadOnlyList<RemovedGenreTagValue> RemovedByNormalization,
    IReadOnlyList<PersonalGenreMapping> Mappings,
    IReadOnlyList<PersonalGenreRule> Rules,
    IReadOnlyList<PersonalGenreLock> Locks,
    IReadOnlyList<PersonalGenreTaxon> CustomTaxa,
    IReadOnlyList<GenreArtistLocationContext> ArtistLocations)
{
    /// <summary>
    /// Runs the shared, provider-agnostic resolver over the assembled inputs.
    /// </summary>
    public PersonalGenreResolution Resolve()
        => PersonalGenreResolver.Resolve(
            NormalizedPost.Observations,
            Mappings,
            Rules,
            Locks,
            CustomTaxa,
            Settings,
            artistLocations: ArtistLocations,
            // The file as the user left it, so a value AutoTag would otherwise drop
            // is carried forward. It was normalized the same way, so a blocked value
            // in either snapshot stays blocked and is never resurrected.
            originalObservations: NormalizedPre?.Observations,
            removedByNormalization: RemovedByNormalization);
}

/// <summary>
/// The outcome of resolving one file: the stored result plus the inputs that
/// produced it.
/// </summary>
/// <remarks>
/// The context is returned rather than discarded so the AutoTag stage can build
/// the same cleanup preview a user would see, from the same removals, instead of
/// reconstructing an approximation.
/// </remarks>
public sealed record GenreIntelligenceFileResolution(
    long? TrackId,
    PersonalGenreTrackResult Result,
    GenreIntelligenceResolutionContext Context);
