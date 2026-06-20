using System.Text.Json;
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
    private static readonly TimeSpan HealthFreshness = TimeSpan.FromMinutes(30);
    private readonly ProtectedCredentialFileStore _store;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<TidalPublicProviderRegistry> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TidalPublicProviderRegistry(
        IWebHostEnvironment environment,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<TidalPublicProviderRegistry> logger)
    {
        _logger = logger;
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

    public Task RecordSuccessAsync(string endpoint, long responseTimeMs, CancellationToken cancellationToken)
        => UpdateHealthAsync(endpoint, "online", null, responseTimeMs, cancellationToken);

    public Task RecordFailureAsync(string endpoint, string category, long responseTimeMs, CancellationToken cancellationToken)
        => UpdateHealthAsync(endpoint, ResolveFailureStatus(category), category, responseTimeMs, cancellationToken);

    private async Task UpdateHealthAsync(string endpoint, string status, string? category, long responseTimeMs, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadNoLockAsync(cancellationToken);
            var normalized = NormalizeEndpoint(endpoint);
            var provider = state.Providers.FirstOrDefault(item => string.Equals(item.Endpoint, normalized, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                return;
            }
            var now = DateTimeOffset.UtcNow;
            provider.Status = provider.Enabled ? status : DisabledStatus;
            provider.LastCheckedAt = now;
            provider.LastSuccessAt = status == "online" ? now : provider.LastSuccessAt;
            provider.FailureCategory = category;
            provider.FailureMessage = ResolveFailureMessage(category);
            provider.ResponseTimeMs = Math.Max(0, responseTimeMs);
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
        var allowedEndpoints = definitions.Select(static definition => definition.Endpoint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = state.Providers.RemoveAll(provider => !allowedEndpoints.Contains(provider.Endpoint)) > 0;

        foreach (var definition in definitions)
        {
            var existing = state.Providers.FirstOrDefault(provider => string.Equals(provider.Endpoint, definition.Endpoint, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                state.Providers.Add(CreateState(definition));
                changed = true;
                continue;
            }

            if (existing.Id != definition.Id || existing.DisplayName != definition.DisplayName || existing.Endpoint != definition.Endpoint)
            {
                existing.Id = definition.Id;
                existing.DisplayName = definition.DisplayName;
                existing.Endpoint = definition.Endpoint;
                changed = true;
            }
        }

        state.Providers = definitions
            .Select(definition => state.Providers.Single(provider => string.Equals(provider.Endpoint, definition.Endpoint, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return changed;
    }

    private static ProviderState CreateState(TidalPublicProviderDefinition definition)
        => new()
        {
            Id = definition.Id,
            DisplayName = definition.DisplayName,
            Endpoint = definition.Endpoint,
            Enabled = true,
            Status = UnknownStatus
        };

    private static TidalPublicProvider ToPublicProvider(ProviderState provider)
    {
        var status = provider.Enabled ? provider.Status : DisabledStatus;
        if (provider.Enabled && provider.LastCheckedAt.HasValue && DateTimeOffset.UtcNow - provider.LastCheckedAt.Value > HealthFreshness) status = UnknownStatus;
        return new TidalPublicProvider(provider.Id, provider.DisplayName, provider.Endpoint, provider.Enabled, status, provider.LastCheckedAt, provider.LastSuccessAt, provider.FailureCategory, provider.FailureMessage, provider.ResponseTimeMs);
    }

    private static string NormalizeEndpoint(string? endpoint) => (endpoint ?? string.Empty).Trim().TrimEnd('/');
    private static string ResolveFailureStatus(string category) => category is "timeout" or "transient" ? "degraded" : "offline";
    private static string? ResolveFailureMessage(string? category) => category switch
    {
        null => null,
        "timeout" => "Provider check timed out.",
        "empty_response" => "Provider returned no usable stream manifest.",
        "transient" => "Provider is temporarily unavailable.",
        _ => "Provider is unavailable."
    };

    private sealed class RegistryState { public List<ProviderState> Providers { get; set; } = new(); }
    private sealed class ProviderState
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public string Status { get; set; } = UnknownStatus;
        public DateTimeOffset? LastCheckedAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public string? FailureCategory { get; set; }
        public string? FailureMessage { get; set; }
        public long? ResponseTimeMs { get; set; }
    }
}
