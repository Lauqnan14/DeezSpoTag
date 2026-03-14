namespace DeezSpoTag.Services.Matching;

public sealed class MatchResult
{
    public string Provider { get; init; } = "";
    public string ProviderTrackId { get; init; } = "";
    public MatchConfidence Confidence { get; init; }
    public string Reason { get; init; } = "";
}
