namespace DeezSpoTag.Core.Models.Settings;

public class VerificationSettings
{
    public bool EnableShazamVerification { get; set; } = false;
    public VerificationTrigger Trigger { get; set; } = VerificationTrigger.LibraryUpgradeOnly;
    public VerificationStrictness Strictness { get; set; } = VerificationStrictness.Normal;
    public VerificationFailureAction FailureAction { get; set; } = VerificationFailureAction.Quarantine;
    public string? QuarantinePath { get; set; }
    public int MaxDurationDifferenceSeconds { get; set; } = 5;
    public int RateLimitSeconds { get; set; } = 2;
    public bool IsrcFirstStrategy { get; set; } = true;
}

public enum VerificationTrigger
{
    LibraryUpgradeOnly = 0,
    AllDownloads = 1,
    Never = 2
}

public enum VerificationStrictness
{
    Relaxed = 0,
    Normal = 1,
    Strict = 2
}

public enum VerificationFailureAction
{
    Quarantine = 0,
    KeepOriginal = 1,
    ReplaceAnyway = 2,
    AskUser = 3
}
