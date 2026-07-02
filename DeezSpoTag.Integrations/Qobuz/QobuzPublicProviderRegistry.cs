namespace DeezSpoTag.Integrations.Qobuz;

public sealed record QobuzPublicProvider(
    string Id,
    string DisplayName,
    string Kind,
    string Endpoint,
    string? Region,
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

public interface IQobuzPublicProviderRegistry
{
    Task<IReadOnlyList<QobuzPublicProvider>> GetProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<QobuzPublicProvider>> CheckEnabledProvidersAsync(CancellationToken cancellationToken);
    Task<QobuzPublicProvider?> SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken);
    Task RecordSuccessAsync(string providerId, long responseTimeMs, CancellationToken cancellationToken);
    Task RecordFailureAsync(string providerId, string category, long responseTimeMs, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken);
}
