using System.Text.Json;
using System.Diagnostics;
using System.Net;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Security;
using Microsoft.AspNetCore.DataProtection;

namespace DeezSpoTag.Web.Services;

public sealed class TidalPublicProviderRegistry : ITidalPublicProviderRegistry
{
    private const string ProtectionPurpose = "DeezSpoTag.Tidal.PublicProviders";
    private const string FileName = "tidal-public-providers.json";
    private const string DisabledStatus = "disabled";
    private const string UnknownStatus = "unknown";
    private readonly ProtectedCredentialFileStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<TidalPublicProviderRegistry> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TidalPublicProviderRegistry(
        IWebHostEnvironment environment,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<TidalPublicProviderRegistry> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _store = new ProtectedCredentialFileStore(dataProtectionProvider, ProtectionPurpose);
        _path = Path.Join(AppDataPaths.GetDataRoot(environment), "autotag", FileName);
    }

    public async Task<IReadOnlyList<TidalPublicProvider>> GetProvidersAsync(CancellationToken cancellationToken)
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

    public async Task<TidalPublicProvider?> SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken)
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

    public async Task<IReadOnlyList<TidalPublicProvider>> CheckEnabledProvidersAsync(CancellationToken cancellationToken)
    {
        var providers = (await GetProvidersAsync(cancellationToken))
            .Where(static provider => provider.Enabled)
            .ToArray();

        await Task.WhenAll(providers.Select(provider => CheckProviderAsync(provider, cancellationToken)));
        return await GetProvidersAsync(cancellationToken);
    }

    public Task RecordSuccessAsync(string endpoint, long responseTimeMs, CancellationToken cancellationToken)
        => UpdateDownloadOutcomeAsync(endpoint, null, null, cancellationToken);

    public Task RecordFailureAsync(string endpoint, string category, long responseTimeMs, CancellationToken cancellationToken)
        => RecordFailureAsync(endpoint, category, responseTimeMs, ResolveCooldown(category), cancellationToken);

    public Task RecordFailureAsync(string endpoint, string category, long responseTimeMs, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken)
        => UpdateDownloadOutcomeAsync(endpoint, category, cooldownUntil, cancellationToken);

    private async Task UpdateDownloadOutcomeAsync(
        string endpoint,
        string? category,
        DateTimeOffset? cooldownUntil,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadNoLockAsync(cancellationToken);
            var normalized = NormalizeEndpoint(endpoint);
            var provider = state.Providers.FirstOrDefault(item => string.Equals(item.Id, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Endpoint, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.HealthEndpoint, normalized, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                return;
            }

            provider.FailureCategory = category;
            provider.FailureMessage = ResolveFailureMessage(category);
            provider.CooldownUntil = cooldownUntil;
            await SaveNoLockAsync(state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpdateHealthAsync(
        string endpoint,
        string status,
        string? category,
        long responseTimeMs,
        DateTimeOffset? cooldownUntil,
        bool preserveActiveCooldown,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadNoLockAsync(cancellationToken);
            var normalized = NormalizeEndpoint(endpoint);
            var provider = state.Providers.FirstOrDefault(item => string.Equals(item.Id, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Endpoint, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.HealthEndpoint, normalized, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                return;
            }
            var now = DateTimeOffset.UtcNow;
            var activeCooldown = preserveActiveCooldown
                && provider.CooldownUntil.HasValue
                && provider.CooldownUntil.Value > now;
            provider.Status = provider.Enabled ? status : DisabledStatus;
            provider.LastCheckedAt = now;
            provider.LastSuccessAt = status == "online" ? now : provider.LastSuccessAt;
            provider.FailureCategory = activeCooldown ? provider.FailureCategory : category;
            provider.FailureMessage = activeCooldown ? provider.FailureMessage : ResolveFailureMessage(category);
            provider.ResponseTimeMs = Math.Max(0, responseTimeMs);
            provider.CooldownUntil = activeCooldown ? provider.CooldownUntil : cooldownUntil;
            await SaveNoLockAsync(state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
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
                _logger.LogWarning(ex, "Failed to load protected Tidal public provider registry.");
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
        var definitions = TidalPublicProviderDefaults.Providers;
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

    private static ProviderState CreateState(TidalPublicProviderDefinition definition)
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

    private static TidalPublicProvider ToPublicProvider(ProviderState provider)
    {
        var status = provider.Enabled ? provider.Status : DisabledStatus;
        var definition = TidalPublicProviderDefaults.Providers
            .FirstOrDefault(candidate => string.Equals(candidate.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
        return new TidalPublicProvider(
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
            provider.CooldownUntil,
            RequiresVerification: definition?.RequiresVerification == true,
            Capabilities: definition?.Capabilities);
    }

    private static string NormalizeEndpoint(string? endpoint) => (endpoint ?? string.Empty).Trim().TrimEnd('/');
    private async Task CheckProviderAsync(TidalPublicProvider provider, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider.HealthEndpoint))
        {
            await UpdateHealthAsync(provider.Id, UnknownStatus, null, 0, null, preserveActiveCooldown: true, cancellationToken);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var category = await ProbeHealthEndpointAsync(provider.HealthEndpoint, provider.HealthServiceKey, cancellationToken);
            stopwatch.Stop();
            if (category is null)
            {
                await UpdateHealthAsync(provider.Id, "online", null, stopwatch.ElapsedMilliseconds, null, preserveActiveCooldown: true, cancellationToken);
                return;
            }

            await UpdateHealthAsync(
                provider.Id,
                ResolveFailureStatus(category),
                category,
                stopwatch.ElapsedMilliseconds,
                null,
                preserveActiveCooldown: true,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            await UpdateHealthAsync(provider.Id, "degraded", "timeout", stopwatch.ElapsedMilliseconds, null, preserveActiveCooldown: true, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogDebug(ex, "Tidal public provider health check failed for {ProviderId}.", provider.Id);
            await UpdateHealthAsync(provider.Id, "degraded", "transient", stopwatch.ElapsedMilliseconds, null, preserveActiveCooldown: true, cancellationToken);
        }
    }

    private async Task<string?> ProbeHealthEndpointAsync(string healthEndpoint, string? serviceKey, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var request = new HttpRequestMessage(HttpMethod.Get, healthEndpoint);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return "rate_limited";
        }

        if (!response.IsSuccessStatusCode)
        {
            return (int)response.StatusCode >= 500 ? "transient" : "offline";
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        return ClassifyHealthPayload(document.RootElement, serviceKey);
    }

    private static string? ClassifyHealthPayload(JsonElement root, string? serviceKey)
    {
        if (!string.IsNullOrWhiteSpace(serviceKey)
            && root.TryGetProperty("services", out var services)
            && services.ValueKind == JsonValueKind.Object
            && services.TryGetProperty(serviceKey, out var service))
        {
            if (service.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return ok.GetBoolean() ? null : "offline";
            }

            if (service.TryGetProperty("status", out var serviceStatus))
            {
                return ClassifyStatusValue(serviceStatus);
            }
        }

        return root.TryGetProperty("status", out var status) ? ClassifyStatusValue(status) : null;
    }

    private static string? ClassifyStatusValue(JsonElement status)
    {
        if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code))
        {
            return code is >= 200 and < 300 ? null : code == 429 ? "rate_limited" : code >= 500 ? "transient" : "offline";
        }

        var value = status.GetString()?.Trim().ToLowerInvariant();
        return value switch
        {
            null or "" or "ok" or "up" or "online" or "healthy" or "operational" or "pass" or "passing" => null,
            "degraded" or "partial" or "warning" or "warn" => "transient",
            "down" or "offline" or "error" or "failed" or "fail" or "unhealthy" => "offline",
            _ => null
        };
    }

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
        public string Kind { get; set; } = TidalPublicProviderDefaults.ZarzProviderKind;
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
