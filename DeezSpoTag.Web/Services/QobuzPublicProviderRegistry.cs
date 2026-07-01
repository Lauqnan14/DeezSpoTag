using System.Text;
using System.Text.Json;
using System.Diagnostics;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Security;
using Microsoft.AspNetCore.DataProtection;

namespace DeezSpoTag.Web.Services;

public sealed class QobuzPublicProviderRegistry : IQobuzPublicProviderRegistry
{
    private const string ProtectionPurpose = "DeezSpoTag.Qobuz.PublicProviders";
    private const string FileName = "qobuz-public-providers.json";
    private const string DisabledStatus = "disabled";
    private const string MusicDlProviderKind = "musicdl";
    private const string UnknownStatus = "unknown";
    private readonly ProtectedCredentialFileStore _store;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<QobuzPublicProviderRegistry> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public QobuzPublicProviderRegistry(
        IWebHostEnvironment environment,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<QobuzPublicProviderRegistry> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
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
        return await GetProvidersAsync(cancellationToken);
    }

    public Task RecordSuccessAsync(string providerId, long responseTimeMs, CancellationToken cancellationToken)
        => UpdateHealthAsync(providerId, "online", null, responseTimeMs, null, cancellationToken);

    public Task RecordFailureAsync(string providerId, string category, long responseTimeMs, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken)
        => UpdateHealthAsync(providerId, ResolveFailureStatus(category), category, responseTimeMs, cooldownUntil, cancellationToken);

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
            var provider = state.Providers.FirstOrDefault(item => string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase));
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
            provider.CooldownUntil = cooldownUntil;
            await SaveNoLockAsync(state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckProviderAsync(QobuzPublicProvider provider, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var category = await ProbeEndpointAsync(provider.Endpoint, cancellationToken);
            stopwatch.Stop();
            if (category is null)
            {
                await RecordSuccessAsync(provider.Id, stopwatch.ElapsedMilliseconds, cancellationToken);
                return;
            }

            await RecordFailureAsync(provider.Id, category, stopwatch.ElapsedMilliseconds, ResolveCooldown(category), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            await RecordFailureAsync(provider.Id, "timeout", stopwatch.ElapsedMilliseconds, ResolveCooldown("timeout"), cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogDebug(ex, "Qobuz public provider health check failed for {ProviderId}.", provider.Id);
            await RecordFailureAsync(provider.Id, "transient", stopwatch.ElapsedMilliseconds, ResolveCooldown("transient"), cancellationToken);
        }
    }

    private async Task<string?> ProbeEndpointAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var client = CreateHealthClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
        using var response = await SendHealthRequestAsync(client, request, timeout.Token);

        return ClassifyHealthResponse(response.StatusCode);
    }

    private HttpClient CreateHealthClient()
    {
        var client = _httpClientFactory?.CreateClient() ?? new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        return client;
    }

    private static async Task<HttpResponseMessage> SendHealthRequestAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException) when (request.Method == HttpMethod.Head)
        {
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
            return await client.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
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

    private static DateTimeOffset? ResolveCooldown(string category)
        => category is "rate_limited" or "timeout" or "transient"
            ? DateTimeOffset.UtcNow.AddMinutes(3)
            : null;

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

            if (existing.DisplayName != definition.DisplayName || existing.Kind != definition.Kind || existing.Endpoint != definition.Endpoint || existing.Region != definition.Region)
            {
                existing.DisplayName = definition.DisplayName;
                existing.Kind = definition.Kind;
                existing.Endpoint = definition.Endpoint;
                existing.Region = definition.Region;
                changed = true;
            }
        }
        return changed;
    }

    private static IEnumerable<ProviderState> DefaultProviders()
    {
        yield return Create("spotbye", "Spotbye", MusicDlProviderKind, "aHR0cHM6Ly9xb2J1ei5zcG90YnllLnF6ei5pby9kbC9xYno=", null);
        yield return Create("zarz", "Zarz", MusicDlProviderKind, "aHR0cHM6Ly9hcGkuemFyei5tb2UvdjEvZGwvcWJ6", null);
        yield return Create("musicdl", "MusicDL", MusicDlProviderKind, "aHR0cHM6Ly9kbC5tdXNpY2RsLm1lL2RsL3Fieg==", null);
        yield return Create("monochrome-trypt", "Monochrome Trypt", "monochrome", "aHR0cHM6Ly90cnlwdC1oaWZpLWRsLTQ1NjQ2MTkzMjY4Ni51cy13ZXN0MS5ydW4uYXBw", null);
        yield return Create("monochrome-kenny", "Monochrome Kenny", "monochrome", "aHR0cHM6Ly9xb2J1ei5rZW5ueXkuY29tLmJy", null);
    }

    private static ProviderState Create(string id, string name, string kind, string endpoint, string? region)
        => new() { Id = id, DisplayName = name, Kind = kind, Endpoint = Decode(endpoint), Region = region, Enabled = true, Status = UnknownStatus };

    private static string Decode(string encoded) => Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

    private static QobuzPublicProvider ToPublicProvider(ProviderState provider)
    {
        var status = provider.Enabled ? provider.Status : DisabledStatus;
        return new(provider.Id, provider.DisplayName, provider.Kind, provider.Endpoint, provider.Region, provider.Enabled, status, provider.LastCheckedAt, provider.LastSuccessAt, provider.FailureCategory, provider.FailureMessage, provider.ResponseTimeMs, provider.CooldownUntil);
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
