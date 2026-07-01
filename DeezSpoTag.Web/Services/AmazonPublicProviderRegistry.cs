using System.Diagnostics;
using System.Text.Json;
using DeezSpoTag.Integrations.Amazon;
using DeezSpoTag.Services.Security;
using Microsoft.AspNetCore.DataProtection;

namespace DeezSpoTag.Web.Services;

public sealed class AmazonPublicProviderRegistry : IAmazonPublicProviderRegistry
{
    private const string ProtectionPurpose = "DeezSpoTag.Amazon.PublicProviders";
    private const string FileName = "amazon-public-providers.json";
    private const string DisabledStatus = "disabled";
    private const string UnknownStatus = "unknown";
    private readonly ProtectedCredentialFileStore _store;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<AmazonPublicProviderRegistry> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AmazonPublicProviderRegistry(
        IWebHostEnvironment environment,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AmazonPublicProviderRegistry> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _store = new ProtectedCredentialFileStore(dataProtectionProvider, ProtectionPurpose);
        _path = Path.Join(AppDataPaths.GetDataRoot(environment), "autotag", FileName);
    }

    public async Task<IReadOnlyList<AmazonPublicProvider>> GetProvidersAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadNoLockAsync(cancellationToken)).Providers.Select(ToPublicProvider).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AmazonPublicProvider?> SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadNoLockAsync(cancellationToken);
            var provider = state.Providers.FirstOrDefault(item => string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return null;
            provider.Enabled = enabled;
            provider.Status = enabled ? UnknownStatus : DisabledStatus;
            await SaveNoLockAsync(state, cancellationToken);
            return ToPublicProvider(provider);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AmazonPublicProvider>> CheckEnabledProvidersAsync(CancellationToken cancellationToken)
    {
        var providers = (await GetProvidersAsync(cancellationToken))
            .Where(static provider => provider.Enabled)
            .ToArray();

        await Task.WhenAll(providers.Select(provider => CheckProviderAsync(provider, cancellationToken)));
        return await GetProvidersAsync(cancellationToken);
    }

    public Task RecordSuccessAsync(string providerId, long responseTimeMs, CancellationToken cancellationToken)
        => UpdateHealthAsync(providerId, "online", null, responseTimeMs, null, cancellationToken);

    public Task RecordFailureAsync(string providerId, string category, long responseTimeMs, CancellationToken cancellationToken)
        => UpdateHealthAsync(providerId, ResolveFailureStatus(category), category, responseTimeMs, ResolveCooldown(category), cancellationToken);

    private async Task UpdateHealthAsync(
        string providerId,
        string status,
        string? category,
        long responseTimeMs,
        DateTimeOffset? cooldownUntil,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadNoLockAsync(cancellationToken);
            var normalized = NormalizeEndpoint(providerId);
            var provider = state.Providers.FirstOrDefault(item => string.Equals(item.Id, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Endpoint, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.HealthEndpoint, normalized, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return;

            var now = DateTimeOffset.UtcNow;
            provider.Status = provider.Enabled ? status : DisabledStatus;
            provider.LastCheckedAt = now;
            provider.LastSuccessAt = status == "online" ? now : provider.LastSuccessAt;
            provider.FailureCategory = category;
            provider.FailureMessage = ResolveFailureMessage(category);
            provider.ResponseTimeMs = Math.Max(0, responseTimeMs);
            provider.CooldownUntil = cooldownUntil;
            await SaveNoLockAsync(state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckProviderAsync(AmazonPublicProvider provider, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var category = await ProbeEndpointAsync(provider, cancellationToken);
            stopwatch.Stop();
            if (category is null)
            {
                await RecordSuccessAsync(provider.Id, stopwatch.ElapsedMilliseconds, cancellationToken);
                return;
            }

            await RecordFailureAsync(provider.Id, category, stopwatch.ElapsedMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            await RecordFailureAsync(provider.Id, "timeout", stopwatch.ElapsedMilliseconds, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogDebug(ex, "Amazon public provider health check failed for {ProviderId}.", provider.Id);
            await RecordFailureAsync(provider.Id, "transient", stopwatch.ElapsedMilliseconds, cancellationToken);
        }
    }

    private async Task<string?> ProbeEndpointAsync(AmazonPublicProvider provider, CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrWhiteSpace(provider.HealthEndpoint)
            ? provider.Endpoint
            : provider.HealthEndpoint;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var client = CreateHealthClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        return ClassifyHealthResponse(response.StatusCode);
    }

    private HttpClient CreateHealthClient()
    {
        var client = _httpClientFactory?.CreateClient() ?? new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        return client;
    }

    private static string? ClassifyHealthResponse(System.Net.HttpStatusCode statusCode)
    {
        var numeric = (int)statusCode;
        if (numeric is >= 200 and < 500 && statusCode != System.Net.HttpStatusCode.TooManyRequests)
        {
            return null;
        }

        return statusCode == System.Net.HttpStatusCode.TooManyRequests
            ? "rate_limited"
            : numeric >= 500
                ? "transient"
                : "offline";
    }

    private async Task<RegistryState> LoadNoLockAsync(CancellationToken cancellationToken)
    {
        RegistryState? state = null;
        if (File.Exists(_path))
        {
            try
            {
                var json = await _store.ReadTextAndMigrateAsync(_path, cancellationToken);
                state = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<RegistryState>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to load protected Amazon public provider registry.");
            }
        }

        state ??= new RegistryState();
        var changed = ReconcileDefaults(state);
        if (changed || !File.Exists(_path)) await SaveNoLockAsync(state, cancellationToken);
        return state;
    }

    private async Task SaveNoLockAsync(RegistryState state, CancellationToken cancellationToken)
    {
        await _store.WriteTextAsync(_path, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool ReconcileDefaults(RegistryState state)
    {
        var definitions = AmazonPublicProviderDefaults.Providers;
        var allowedIds = definitions.Select(static definition => definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = state.Providers.RemoveAll(provider => !allowedIds.Contains(provider.Id)) > 0;

        foreach (var definition in definitions)
        {
            var existing = state.Providers.FirstOrDefault(provider => string.Equals(provider.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                state.Providers.Add(CreateState(definition));
                changed = true;
                continue;
            }

            if (existing.DisplayName != definition.DisplayName
                || existing.Kind != definition.Kind
                || existing.Endpoint != definition.Endpoint
                || existing.HealthEndpoint != definition.HealthEndpoint
                || existing.HealthServiceKey != definition.HealthServiceKey)
            {
                existing.DisplayName = definition.DisplayName;
                existing.Kind = definition.Kind;
                existing.Endpoint = definition.Endpoint;
                existing.HealthEndpoint = definition.HealthEndpoint;
                existing.HealthServiceKey = definition.HealthServiceKey;
                changed = true;
            }
        }

        state.Providers = definitions
            .Select(definition => state.Providers.Single(provider => string.Equals(provider.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return changed;
    }

    private static ProviderState CreateState(AmazonPublicProviderDefinition definition)
        => new()
        {
            Id = definition.Id,
            DisplayName = definition.DisplayName,
            Kind = definition.Kind,
            Endpoint = definition.Endpoint,
            HealthEndpoint = definition.HealthEndpoint,
            HealthServiceKey = definition.HealthServiceKey,
            Enabled = true,
            Status = UnknownStatus
        };

    private static AmazonPublicProvider ToPublicProvider(ProviderState provider)
    {
        var status = provider.Enabled ? provider.Status : DisabledStatus;
        return new AmazonPublicProvider(
            provider.Id,
            provider.DisplayName,
            provider.Kind,
            provider.Endpoint,
            provider.HealthEndpoint,
            provider.HealthServiceKey,
            provider.Enabled,
            status,
            provider.LastCheckedAt,
            provider.LastSuccessAt,
            provider.FailureCategory,
            provider.FailureMessage,
            provider.ResponseTimeMs,
            provider.CooldownUntil);
    }

    private static string NormalizeEndpoint(string? endpoint) => (endpoint ?? string.Empty).Trim().TrimEnd('/');
    private static string ResolveFailureStatus(string category) => category switch
    {
        "rate_limited" => "rate_limited",
        "timeout" or "transient" => "degraded",
        _ => "offline"
    };
    private static DateTimeOffset? ResolveCooldown(string category)
        => category is "rate_limited" or "offline" or "empty_response"
            ? DateTimeOffset.UtcNow.AddMinutes(15)
            : null;
    private static string? ResolveFailureMessage(string? category) => category switch
    {
        null => null,
        "timeout" => "Provider check timed out.",
        "empty_response" => "Provider returned no usable stream manifest.",
        "rate_limited" => "Provider is rate limited.",
        "transient" => "Provider is temporarily unavailable.",
        _ => "Provider is unavailable."
    };

    private sealed class RegistryState { public List<ProviderState> Providers { get; set; } = new(); }
    private sealed class ProviderState
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Kind { get; set; } = AmazonPublicProviderDefaults.DownloadProviderKind;
        public string Endpoint { get; set; } = string.Empty;
        public string? HealthEndpoint { get; set; }
        public string? HealthServiceKey { get; set; }
        public bool Enabled { get; set; }
        public string Status { get; set; } = UnknownStatus;
        public DateTimeOffset? LastCheckedAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public string? FailureCategory { get; set; }
        public string? FailureMessage { get; set; }
        public long? ResponseTimeMs { get; set; }
        public DateTimeOffset? CooldownUntil { get; set; }
    }
}
