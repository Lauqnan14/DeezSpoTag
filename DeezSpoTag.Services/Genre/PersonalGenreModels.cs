namespace DeezSpoTag.Services.Genre;

public enum PersonalGenreTaxonKind
{
    Genre,
    Style,
    Substyle,
    Context
}

public sealed record PersonalGenreTaxon(
    string Id,
    string Name,
    PersonalGenreTaxonKind Kind,
    IReadOnlyList<string>? ParentIds = null,
    bool ContextOnly = false,
    IReadOnlyList<string>? Aliases = null);

public sealed record PersonalGenreEvidence(
    string Source,
    string RawValue,
    PersonalGenreTaxonKind? Kind,
    double Weight = 1d,
    string? Scope = null,
    string? CanonicalValue = null);

public sealed record PersonalGenreMapping(
    long Id,
    string MatchValue,
    string TargetTaxonId,
    string? Source = null,
    int Priority = 100,
    bool Enabled = true);

public sealed record PersonalGenreRule(
    long Id,
    string MatchValue,
    string TargetTaxonId,
    string? Source = null,
    int Priority = 1000,
    bool Enabled = true);

public sealed record PersonalGenreLock(
    long TrackId,
    string TaxonId,
    bool Enabled = true,
    DateTimeOffset? UpdatedAtUtc = null);

public sealed record PersonalGenreSettings(
    bool Enabled = true,
    int MaxGenres = 3,
    bool PreserveProviderFallback = true,
    bool IncludeParentGenres = false);

public sealed record PersonalGenreClassification(
    string TaxonId,
    string Name,
    PersonalGenreTaxonKind Kind,
    double Confidence,
    IReadOnlyList<string> Sources,
    bool UserLocked = false);

public sealed record PersonalGenreResolution(
    string? PrimaryGenre,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Styles,
    IReadOnlyList<string> Substyles,
    IReadOnlyList<string> Contexts,
    IReadOnlyList<PersonalGenreClassification> Classifications,
    IReadOnlyList<string> AppliedRuleIds,
    IReadOnlyList<PersonalGenreEvidence> Evidence,
    string ResolverVersion);

public sealed record PersonalGenreTrackResult(
    long TrackId,
    PersonalGenreResolution Resolution,
    DateTimeOffset ResolvedAtUtc);


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
