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
    string Endpoint,
    bool Enabled,
    string Status,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSuccessAt,
    string? FailureCategory,
    string? FailureMessage,
    long? ResponseTimeMs);

public interface ITidalPublicProviderRegistry
{
    Task<IReadOnlyList<TidalPublicProvider>> GetProvidersAsync(CancellationToken cancellationToken);
    Task<TidalPublicProvider?> SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken);
    Task RecordSuccessAsync(string endpoint, long responseTimeMs, CancellationToken cancellationToken);
    Task RecordFailureAsync(string endpoint, string category, long responseTimeMs, CancellationToken cancellationToken);
}

public sealed record TidalPublicProviderDefinition(string Id, string DisplayName, string Endpoint);

public static class TidalPublicProviderDefaults
{
    private static readonly (string Id, string DisplayName, string EncodedEndpoint)[] EncodedProviders =
    [
        ("geeked", "Geeked", "aHR0cHM6Ly9oaWZpLmdlZWtlZC53dGY="),
        ("pink-hamster", "Pink Hamster", "aHR0cHM6Ly9oaWZpLnAxbmtoYW1zdGVyLnh5eg=="),
        ("qqdl-vogel", "QQDL Vogel", "aHR0cHM6Ly92b2dlbC5xcWRsLnNpdGU="),
        ("spotisaver-one", "SpotiSaver One", "aHR0cHM6Ly9oaWZpLW9uZS5zcG90aXNhdmVyLm5ldA=="),
        ("spotisaver-two", "SpotiSaver Two", "aHR0cHM6Ly9oaWZpLXR3by5zcG90aXNhdmVyLm5ldA=="),
        ("kinoplus", "KinoPlus", "aHR0cHM6Ly90aWRhbC5raW5vcGx1cy5vbmxpbmU="),
        ("binimum", "Binimum", "aHR0cHM6Ly90aWRhbC1hcGkuYmluaW11bS5vcmc=")
    ];

    public static IReadOnlyList<TidalPublicProviderDefinition> Providers { get; } = EncodedProviders
        .Select(static provider => new TidalPublicProviderDefinition(
            provider.Id,
            provider.DisplayName,
            Encoding.UTF8.GetString(Convert.FromBase64String(provider.EncodedEndpoint))))
        .ToArray();

    public static IReadOnlyList<string> Endpoints { get; } = Providers.Select(static provider => provider.Endpoint).ToArray();
}
