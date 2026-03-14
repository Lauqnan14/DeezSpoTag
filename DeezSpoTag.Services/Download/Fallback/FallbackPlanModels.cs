namespace DeezSpoTag.Services.Download.Fallback;

public sealed record FallbackPlanStep(
    string StepId,
    string Engine,
    string? Quality,
    IReadOnlyList<string> RequiredInputs,
    string ResolutionStrategy);

public sealed record FallbackAttempt(
    string StepId,
    string Status,
    string ErrorClass,
    string Detail);
