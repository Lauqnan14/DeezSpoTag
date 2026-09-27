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
    string? ParentId = null,
    bool ContextOnly = false,
    IReadOnlyList<string>? Aliases = null);

public sealed record PersonalGenreEvidence(
    string Source,
    string RawValue,
    PersonalGenreTaxonKind? Kind,
    double Weight = 1d,
    string? Scope = null);

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

public sealed record PersonalGenreSettings(
    bool Enabled = true,
    int MaxGenres = 3,
    bool PreserveProviderFallback = true,
    bool IncludeParentGenres = false);

public sealed record PersonalGenreResolution(
    string? PrimaryGenre,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Styles,
    IReadOnlyList<string> Substyles,
    IReadOnlyList<string> Contexts,
    IReadOnlyList<string> AppliedRuleIds,
    IReadOnlyList<PersonalGenreEvidence> Evidence,
    string ResolverVersion);

public sealed record PersonalGenreTrackResult(
    long TrackId,
    PersonalGenreResolution Resolution,
    DateTimeOffset ResolvedAtUtc);
