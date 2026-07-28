namespace DeezSpoTag.Integrations.Tidal;

using System.Text;

public readonly record struct TidalOfficialCredentials(
    string ClientId,
    string ClientSecret,
    string AccessToken,
    string RefreshToken,
    string UserId,
    string CountryCode);

public interface ITidalCredentialProvider
{
    Task<TidalOfficialCredentials> GetCredentialsAsync(CancellationToken cancellationToken);
}

public interface ITidalAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
    Task<string> GetCountryCodeAsync(CancellationToken cancellationToken);
    Task<bool> ValidateCredentialsAsync(CancellationToken cancellationToken);
    void Invalidate();
}

public sealed record TidalPublicProvider(
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
    DateTimeOffset? CooldownUntil,
    bool RequiresVerification = false);

public interface ITidalPublicProviderRegistry
{
    Task<IReadOnlyList<TidalPublicProvider>> GetProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TidalPublicProvider>> CheckEnabledProvidersAsync(CancellationToken cancellationToken);
    Task<TidalPublicProvider?> SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken);
    Task RecordSuccessAsync(string endpoint, long responseTimeMs, CancellationToken cancellationToken);
    Task RecordFailureAsync(string endpoint, string category, long responseTimeMs, CancellationToken cancellationToken);
    Task RecordFailureAsync(string endpoint, string category, long responseTimeMs, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken);
}

public sealed record TidalPublicProviderDefinition(
    string Id,
    string DisplayName,
    string Kind,
    string Endpoint,
    string? HealthEndpoint,
    string? HealthServiceKey,
    bool RequiresVerification = false);

public static class TidalPublicProviderDefaults
{
    public const string ZarzProviderKind = "zarz";

    private static readonly (string Id, string DisplayName, string Kind, string EncodedEndpoint, string? EncodedHealthEndpoint, string? HealthServiceKey)[] EncodedProviders =
    [
        ("zarz", "zarz", ZarzProviderKind, "aHR0cHM6Ly9hcGkuemFyei5tb2UvdjIvZGwvdGlk", "aHR0cHM6Ly9hcGkuemFyei5tb2UvdjEvaGVhbHRo", "tidal")
    ];

    public static IReadOnlyList<TidalPublicProviderDefinition> Providers { get; } = EncodedProviders
        .Select(static provider => new TidalPublicProviderDefinition(
            provider.Id,
            provider.DisplayName,
            provider.Kind,
            Decode(provider.EncodedEndpoint),
            string.IsNullOrWhiteSpace(provider.EncodedHealthEndpoint) ? null : Decode(provider.EncodedHealthEndpoint),
            provider.HealthServiceKey,
            RequiresVerification: true))
        .ToArray();

    public static IReadOnlyList<string> Endpoints { get; } = Providers.Select(static provider => provider.Endpoint).ToArray();

    private static string Decode(string encoded) => Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
}
