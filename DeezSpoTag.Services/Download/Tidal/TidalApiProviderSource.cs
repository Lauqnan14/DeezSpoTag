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
    {
        var enabledUrls = (await _providerRegistry.GetProvidersAsync(cancellationToken))
            .Where(static provider => provider.Enabled)
            .Select(static provider => provider.Endpoint)
            .ToList();
        return RotateUrls(enabledUrls, _lastUsedUrl);
    }

    public Task<IReadOnlyList<string>> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        _ = force;
        _ = cancellationToken;
        return Task.FromResult(TidalPublicProviderDefaults.Endpoints);
    }

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

    public Task RememberFailureAsync(string providerUrl, string category, long responseTimeMs, CancellationToken cancellationToken)
        => _providerRegistry.RecordFailureAsync(providerUrl, category, responseTimeMs, cancellationToken);

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
}
