using DeezSpoTag.Integrations.Tidal;

namespace DeezSpoTag.Services.Download.Tidal;

public sealed class TidalApiProviderSource
{
    private readonly ITidalPublicProviderRegistry _providerRegistry;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private string _lastUsedUrl = string.Empty;

    public TidalApiProviderSource(ITidalPublicProviderRegistry providerRegistry)
    {
        _providerRegistry = providerRegistry;
    }

    public async Task<IReadOnlyList<string>> GetRotatedProvidersAsync(CancellationToken cancellationToken)
        => (await GetRotatedProviderRecordsAsync(cancellationToken))
            .Select(static provider => provider.Endpoint)
            .ToArray();

    public async Task<IReadOnlyList<TidalPublicProvider>> GetRotatedProviderRecordsAsync(CancellationToken cancellationToken)
    {
        var enabledProviders = (await _providerRegistry.GetProvidersAsync(cancellationToken))
            .Where(static provider => provider.Enabled)
            .ToList();
        return RotateProviders(enabledProviders, _lastUsedUrl);
    }

    public static Task<IReadOnlyList<string>> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        _ = force;
        _ = cancellationToken;
        return Task.FromResult(TidalPublicProviderDefaults.Endpoints);
    }

    public async Task RememberSuccessAsync(TidalPublicProvider provider, CancellationToken cancellationToken)
        => await RememberSuccessAsync(provider.Endpoint, cancellationToken);

    public async Task RememberSuccessAsync(string providerUrl, CancellationToken cancellationToken)
    {
        var normalized = NormalizeUrl(providerUrl);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (!TidalPublicProviderDefaults.Endpoints.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            _lastUsedUrl = normalized;
            await _providerRegistry.RecordSuccessAsync(normalized, 0, cancellationToken);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public Task RememberFailureAsync(TidalPublicProvider provider, string category, long responseTimeMs, CancellationToken cancellationToken)
        => _providerRegistry.RecordFailureAsync(provider.Id, category, responseTimeMs, cancellationToken);

    public Task RememberFailureAsync(string providerUrl, string category, long responseTimeMs, CancellationToken cancellationToken)
        => _providerRegistry.RecordFailureAsync(providerUrl, category, responseTimeMs, cancellationToken);

    public Task RememberHealthSuccessAsync(TidalPublicProvider provider, long responseTimeMs, CancellationToken cancellationToken)
        => _providerRegistry.RecordSuccessAsync(provider.Id, responseTimeMs, cancellationToken);

    public Task RememberHealthSuccessAsync(string providerUrl, long responseTimeMs, CancellationToken cancellationToken)
        => _providerRegistry.RecordSuccessAsync(providerUrl, responseTimeMs, cancellationToken);

    private static string NormalizeUrl(string? value) => (value ?? string.Empty).Trim().TrimEnd('/');

    private static List<string> RotateUrls(List<string> urls, string? lastUsedUrl)
    {
        if (urls.Count < 2)
        {
            return [.. urls];
        }

        var normalizedLastUsed = NormalizeUrl(lastUsedUrl);
        if (string.IsNullOrWhiteSpace(normalizedLastUsed))
        {
            return [.. urls];
        }

        var lastIndex = -1;
        for (var index = 0; index < urls.Count; index++)
        {
            if (string.Equals(NormalizeUrl(urls[index]), normalizedLastUsed, StringComparison.OrdinalIgnoreCase))
            {
                lastIndex = index;
                break;
            }
        }

        if (lastIndex < 0)
        {
            return [.. urls];
        }

        var rotated = new List<string>(urls.Count);
        for (var index = lastIndex + 1; index < urls.Count; index++)
        {
            rotated.Add(urls[index]);
        }

        for (var index = 0; index <= lastIndex; index++)
        {
            rotated.Add(urls[index]);
        }

        return rotated;
    }

    private static List<TidalPublicProvider> RotateProviders(List<TidalPublicProvider> providers, string? lastUsedUrl)
    {
        if (providers.Count < 2)
        {
            return [.. providers];
        }

        var normalizedLastUsed = NormalizeUrl(lastUsedUrl);
        if (string.IsNullOrWhiteSpace(normalizedLastUsed))
        {
            return [.. providers];
        }

        var lastIndex = -1;
        for (var index = 0; index < providers.Count; index++)
        {
            if (string.Equals(NormalizeUrl(providers[index].Endpoint), normalizedLastUsed, StringComparison.OrdinalIgnoreCase))
            {
                lastIndex = index;
                break;
            }
        }

        if (lastIndex < 0)
        {
            return [.. providers];
        }

        var rotated = new List<TidalPublicProvider>(providers.Count);
        for (var index = lastIndex + 1; index < providers.Count; index++)
        {
            rotated.Add(providers[index]);
        }

        for (var index = 0; index <= lastIndex; index++)
        {
            rotated.Add(providers[index]);
        }

        return rotated;
    }
}
