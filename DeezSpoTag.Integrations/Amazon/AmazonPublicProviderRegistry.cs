namespace DeezSpoTag.Integrations.Amazon;

public sealed record AmazonPublicProvider(
    string Id,
    string DisplayName,
    string Kind,
    string Endpoint,
    string? HealthEndpoint,
    string? HealthServiceKey,
    bool Enabled,
    string Status,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSuccessAt,
    string? FailureCategory,
    string? FailureMessage,
    long? ResponseTimeMs,
    DateTimeOffset? CooldownUntil);

public interface IAmazonPublicProviderRegistry
{
    Task<IReadOnlyList<AmazonPublicProvider>> GetProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AmazonPublicProvider>> CheckEnabledProvidersAsync(CancellationToken cancellationToken);
    Task<AmazonPublicProvider?> SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken);
    Task RecordSuccessAsync(string providerId, long responseTimeMs, CancellationToken cancellationToken);
    Task RecordFailureAsync(string providerId, string category, long responseTimeMs, CancellationToken cancellationToken);
}

public sealed record AmazonPublicProviderDefinition(
    string Id,
    string DisplayName,
    string Kind,
    string Endpoint,
    string? HealthEndpoint,
    string? HealthServiceKey);

public static class AmazonPublicProviderDefaults
{
    public const string DownloadProviderKind = "download";

    public static readonly IReadOnlyList<AmazonPublicProviderDefinition> Providers =
    [
        new(
            "zarz-api",
            "Zarz API",
            DownloadProviderKind,
            "https://api.zarz.moe/v1/dl/amazeamazeamaze",
            "https://api.zarz.moe/v1/health",
            "amazon")
    ];
}
