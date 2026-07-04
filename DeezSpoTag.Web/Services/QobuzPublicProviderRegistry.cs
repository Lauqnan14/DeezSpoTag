using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Net;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Security;
using Microsoft.AspNetCore.DataProtection;

namespace DeezSpoTag.Web.Services;

public sealed class QobuzPublicProviderRegistry : IQobuzPublicProviderRegistry
{
    private const string ProtectionPurpose = "DeezSpoTag.Qobuz.PublicProviders";
    private const string FileName = "qobuz-public-providers.json";
    private const string DisabledStatus = "disabled";
    private const string SignedProviderKind = "zarz-v2";
    private const string UnknownStatus = "unknown";
    private readonly ProtectedCredentialFileStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<QobuzPublicProviderRegistry> _logger;
    private readonly DownloadQueueRepository _queueRepository;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public QobuzPublicProviderRegistry(
        IWebHostEnvironment environment,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<QobuzPublicProviderRegistry> logger,
        IHttpClientFactory httpClientFactory,
        DownloadQueueRepository queueRepository)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _queueRepository = queueRepository;
        _store = new ProtectedCredentialFileStore(dataProtectionProvider, ProtectionPurpose);
        _path = Path.Join(AppDataPaths.GetDataRoot(environment), "autotag", FileName);
    }

    public async Task<IReadOnlyList<QobuzPublicProvider>> GetProvidersAsync(CancellationToken cancellationToken)
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

    public async Task<QobuzPublicProvider?> SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadNoLockAsync(cancellationToken);
            var provider = state.Providers.FirstOrDefault(item => string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                return null;
            }

            provider.Enabled = enabled;
            if (!enabled)
            {
                provider.Status = DisabledStatus;
            }
            else if (string.Equals(provider.Status, DisabledStatus, StringComparison.OrdinalIgnoreCase))
            {
                provider.Status = UnknownStatus;
            }
            await SaveNoLockAsync(state, cancellationToken);
            return ToPublicProvider(provider);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<QobuzPublicProvider>> CheckEnabledProvidersAsync(CancellationToken cancellationToken)
    {
        var providers = (await GetProvidersAsync(cancellationToken))
            .Where(static provider => provider.Enabled)
            .ToArray();

        await Task.WhenAll(providers.Select(provider => CheckProviderAsync(provider, cancellationToken)));
        var updatedProviders = await GetProvidersAsync(cancellationToken);
        if (updatedProviders.Any(static provider => provider.Enabled && string.Equals(provider.Status, "online", StringComparison.OrdinalIgnoreCase)))
        {
            await _queueRepository.RequeueProviderWaitingAsync(["qobuz"], cancellationToken);
        }

        return updatedProviders;
    }

    public Task RecordSuccessAsync(string providerId, long responseTimeMs, CancellationToken cancellationToken)
        => UpdateDownloadOutcomeAsync(providerId, null, null, cancellationToken);

    public Task RecordFailureAsync(string providerId, string category, long responseTimeMs, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken)
        => UpdateDownloadOutcomeAsync(providerId, category, cooldownUntil, cancellationToken);

    private async Task UpdateDownloadOutcomeAsync(
        string providerId,
        string? category,
        DateTimeOffset? cooldownUntil,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadNoLockAsync(cancellationToken);
            var provider = state.Providers.FirstOrDefault(item => string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase));
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
        string providerId,
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
            var provider = state.Providers.FirstOrDefault(item => string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase));
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

    private static DateTimeOffset? ResolveCooldown(string category)
        => category is "rate_limited" or "timeout" or "transient"
            ? DateTimeOffset.UtcNow.AddMinutes(3)
            : null;

    private async Task CheckProviderAsync(QobuzPublicProvider provider, CancellationToken cancellationToken)
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
            _logger.LogDebug(ex, "Qobuz public provider health check failed for {ProviderId}.", provider.Id);
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

    private async Task<ProviderRegistryState> LoadNoLockAsync(CancellationToken cancellationToken)
    {
        ProviderRegistryState? state = null;
        if (File.Exists(_path))
        {
            try
            {
                var json = await _store.ReadTextAndMigrateAsync(_path, cancellationToken);
                state = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ProviderRegistryState>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to load protected Qobuz public provider registry.");
            }
        }

        state ??= new ProviderRegistryState();
        var changed = ReconcileDefaults(state);
        if (changed || !File.Exists(_path) || !await IsProtectedAsync(cancellationToken))
        {
            await SaveNoLockAsync(state, cancellationToken);
        }
        return state;
    }

    private async Task<bool> IsProtectedAsync(CancellationToken cancellationToken)
    {
        var stored = await File.ReadAllTextAsync(_path, cancellationToken);
        return ProtectedCredentialFileStore.IsProtectedText(stored);
    }

    private async Task SaveNoLockAsync(ProviderRegistryState state, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await _store.WriteTextAsync(_path, json, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static bool ReconcileDefaults(ProviderRegistryState state)
    {
        var definitions = DefaultProviders().ToArray();
        var allowedIds = definitions.Select(definition => definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        changed |= state.Providers.RemoveAll(provider => !allowedIds.Contains(provider.Id)) > 0;
        foreach (var definition in definitions)
        {
            var existing = state.Providers.FirstOrDefault(item => string.Equals(item.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                state.Providers.Add(definition);
                changed = true;
                continue;
            }

            if (existing.DisplayName != definition.DisplayName
                || existing.Kind != definition.Kind
                || existing.Endpoint != definition.Endpoint
                || existing.Region != definition.Region
                || existing.HealthEndpoint != definition.HealthEndpoint
                || existing.HealthServiceKey != definition.HealthServiceKey)
            {
                existing.DisplayName = definition.DisplayName;
                existing.Kind = definition.Kind;
                existing.Endpoint = definition.Endpoint;
                existing.Region = definition.Region;
                existing.HealthEndpoint = definition.HealthEndpoint;
                existing.HealthServiceKey = definition.HealthServiceKey;
                changed = true;
            }
        }
        return changed;
    }

    private static IEnumerable<ProviderState> DefaultProviders()
    {
        yield return Create(
            "zarz-v2",
            "zarz",
            SignedProviderKind,
            "aHR0cHM6Ly9hcGkuemFyei5tb2UvdjIvZGwvcWJ6",
            null,
            "aHR0cHM6Ly9hcGkuemFyei5tb2UvdjEvaGVhbHRo",
            "qobuz");
    }

    private static ProviderState Create(string id, string name, string kind, string endpoint, string? region, string? healthEndpoint, string? healthServiceKey)
        => new() { Id = id, DisplayName = name, Kind = kind, Endpoint = Decode(endpoint), Region = region, HealthEndpoint = DecodeNullable(healthEndpoint), HealthServiceKey = healthServiceKey, Enabled = true, Status = UnknownStatus };

    private static string Decode(string encoded) => Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    private static string? DecodeNullable(string? encoded) => string.IsNullOrWhiteSpace(encoded) ? null : Decode(encoded);

    private static QobuzPublicProvider ToPublicProvider(ProviderState provider)
    {
        var status = provider.Enabled ? provider.Status : DisabledStatus;
        return new(provider.Id, provider.DisplayName, provider.Kind, provider.Endpoint, provider.Region, provider.HealthEndpoint, provider.HealthServiceKey, provider.Enabled, status, provider.LastCheckedAt, provider.LastSuccessAt, provider.FailureCategory, provider.FailureMessage, provider.ResponseTimeMs, provider.CooldownUntil);
    }

    private static string ResolveFailureStatus(string category) => category switch
    {
        "rate_limited" => "rate_limited",
        "captcha_required" => "captcha_required",
        "timeout" or "transient" => "degraded",
        _ => "offline"
    };

    private static string? ResolveFailureMessage(string? category) => category switch
    {
        null => null,
        "rate_limited" => "Provider rate limit reached.",
        "captcha_required" => "Provider requires browser verification.",
        "verification_required" => "Public download verification is required.",
        "timeout" => "Provider check timed out.",
        "transient" => "Provider is temporarily unavailable.",
        "empty_response" => "Provider returned no usable stream URL.",
        _ => "Provider is unavailable."
    };

    private sealed class ProviderRegistryState { public List<ProviderState> Providers { get; set; } = new(); }
    private sealed class ProviderState
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string? Region { get; set; }
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
