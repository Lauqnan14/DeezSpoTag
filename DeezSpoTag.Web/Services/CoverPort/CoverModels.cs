namespace DeezSpoTag.Web.Services.CoverPort;

public enum CoverScoringMode
{
    Default = 0,
    SacadCompatibility = 1
}

public enum CoverSourceName
{
    CoverArtArchive,
    Deezer,
    Discogs,
    Itunes,
    LastFm
}

public readonly record struct CoverRelevance(
    bool Fuzzy,
    bool OnlyFrontCovers,
    bool UnrelatedRisk)
{
    public bool IsReference => !Fuzzy && OnlyFrontCovers && !UnrelatedRisk;
}

public sealed record CoverSearchQuery(string Artist, string Album);

public sealed record CoverSearchOptions(
    int TargetSize = 1200,
    int SizeTolerancePercent = 25,
    bool PreserveSourceFormat = false,
    bool PreferPng = false,
    bool CrunchPng = true,
    bool UsePerceptualHashScoring = true,
    string? ReferenceImagePath = null,
    byte[]? ReferenceImageBytes = null,
    int MaxCandidatesToTry = 20,
    CoverScoringMode ScoringMode = CoverScoringMode.SacadCompatibility,
    IReadOnlyCollection<CoverSourceName>? EnabledSources = null);

public sealed record CoverCandidate(
    CoverSourceName Source,
    string Url,
    int Width,
    int Height,
    string Format,
    double SourceReliability,
    double MatchConfidence,
    string? Artist = null,
    string? Album = null,
    int Rank = 0,
    CoverRelevance? Relevance = null,
    bool IsSizeKnown = true,
    bool IsFormatKnown = true,
    double? SimilarityScore = null,
    bool? IsSimilarToReference = null);

public sealed record RankedCoverCandidate(CoverCandidate Candidate, double Score);

public sealed record CoverDownloadResult(
    string OutputPath,
    CoverCandidate Candidate,
    int Width,
    int Height,
    string Format,
    double Score);
