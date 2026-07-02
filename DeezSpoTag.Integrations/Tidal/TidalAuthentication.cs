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
    DateTimeOffset? CooldownUntil);

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
    string? HealthServiceKey);

public static class TidalPublicProviderDefaults
{
    public const string LegacyProviderKind = "legacy";
    public const string ZarzProviderKind = "zarz";

    private static readonly (string Id, string DisplayName, string Kind, string EncodedEndpoint, string? EncodedHealthEndpoint, string? HealthServiceKey)[] EncodedProviders =
    [
        ("zarz", "Zarz", ZarzProviderKind, "aHR0cHM6Ly9hcGkuemFyei5tb2UvdjEvZGwvdGlkMg==", "aHR0cHM6Ly9hcGkuemFyei5tb2UvdjEvaGVhbHRo", "tidal"),
        ("geeked", "Geeked", LegacyProviderKind, "aHR0cHM6Ly9oaWZpLmdlZWtlZC53dGY=", null, null),
        ("pink-hamster", "Pink Hamster", LegacyProviderKind, "aHR0cHM6Ly9oaWZpLnAxbmtoYW1zdGVyLnh5eg==", null, null),
        ("qqdl-vogel", "QQDL Vogel", LegacyProviderKind, "aHR0cHM6Ly92b2dlbC5xcWRsLnNpdGU=", null, null),
        ("spotisaver-one", "SpotiSaver One", LegacyProviderKind, "aHR0cHM6Ly9oaWZpLW9uZS5zcG90aXNhdmVyLm5ldA==", null, null),
        ("spotisaver-two", "SpotiSaver Two", LegacyProviderKind, "aHR0cHM6Ly9oaWZpLXR3by5zcG90aXNhdmVyLm5ldA==", null, null),
        ("kinoplus", "KinoPlus", LegacyProviderKind, "aHR0cHM6Ly90aWRhbC5raW5vcGx1cy5vbmxpbmU=", null, null),
        ("binimum", "Binimum", LegacyProviderKind, "aHR0cHM6Ly90aWRhbC1hcGkuYmluaW11bS5vcmc=", null, null)
    ];

    public static IReadOnlyList<TidalPublicProviderDefinition> Providers { get; } = EncodedProviders
        .Select(static provider => new TidalPublicProviderDefinition(
            provider.Id,
            provider.DisplayName,
            provider.Kind,
            Decode(provider.EncodedEndpoint),
            string.IsNullOrWhiteSpace(provider.EncodedHealthEndpoint) ? null : Decode(provider.EncodedHealthEndpoint),
            provider.HealthServiceKey))
        .ToArray();

    public static IReadOnlyList<string> Endpoints { get; } = Providers.Select(static provider => provider.Endpoint).ToArray();

    private static string Decode(string encoded) => Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
}
