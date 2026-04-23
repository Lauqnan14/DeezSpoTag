using System.Text.Json;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Tidal;

public sealed class TidalApiProviderSource
{
    private const string HttpsScheme = "https";
    private const string GistHost = "gist.githubusercontent.com";
    private const string GistPath = "afkarxyz/2ce772b943321b9448b454f39403ce25/raw";
    private const string MonochromeInstancesHost = "monochrome.tf";
    private const string MonochromeInstancesPath = "instances.json";
    private const string CacheFileName = "tidal-api-urls.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(12);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SeedProviderHosts =
    [
        "eu-central.monochrome.tf",
        "us-west.monochrome.tf",
        "arran.monochrome.tf",
        "api.monochrome.tf",
        "monochrome-api.samidy.com",
        "hifi.geeked.wtf",
        "hifi.p1nkhamster.xyz",
        "vogel.qqdl.site",
        "maus.qqdl.site",
        "hund.qqdl.site",
        "katze.qqdl.site",
        "wolf.qqdl.site",
        "tidal.kinoplus.online",
        "tidal-api.binimum.org",
        "triton.squid.wtf"
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TidalApiProviderSource> _logger;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private TidalApiProviderState? _state;

    public TidalApiProviderSource(
        IHttpClientFactory httpClientFactory,
        ILogger<TidalApiProviderSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        var dataRoot = AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir());
        _cachePath = Path.Join(dataRoot, "deezspotag", "tidal", CacheFileName);
    }

    public async Task<IReadOnlyList<string>> GetRotatedProvidersAsync(CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken);
        if (ShouldRefresh(state))
        {
            state = await RefreshCoreAsync(force: false, cancellationToken);
        }

        if (state.Urls.Count == 0)
        {
            state = await RefreshCoreAsync(force: true, cancellationToken);
        }

        if (state.Urls.Count == 0)
        {
            return Array.Empty<string>();
        }

        return RotateUrls(state.Urls, state.LastUsedUrl);
    }

    public async Task<IReadOnlyList<string>> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        var state = await RefreshCoreAsync(force, cancellationToken);
        return state.Urls;
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
            var state = await LoadStateInternalAsync(cancellationToken);
            if (state.Urls.Count == 0)
            {
                state.Urls = NormalizeUrls(SeedProviderHosts.Select(BuildHttpsBaseUrl));
            }

            if (!state.Urls.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            state.LastUsedUrl = normalized;
            if (state.UpdatedAtUnix <= 0)
            {
                state.UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            await SaveStateInternalAsync(state, cancellationToken);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private static bool ShouldRefresh(TidalApiProviderState state)
    {
        if (state.Urls.Count == 0 || state.UpdatedAtUnix <= 0)
        {
            return true;
        }

        var updatedAt = DateTimeOffset.FromUnixTimeSeconds(state.UpdatedAtUnix);
        return DateTimeOffset.UtcNow - updatedAt >= CacheTtl;
    }

    private async Task<TidalApiProviderState> RefreshCoreAsync(bool force, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateInternalAsync(cancellationToken);
            if (!force && !ShouldRefresh(state))
            {
                return state.Clone();
            }

            try
            {
                var fetched = await FetchProviderUrlsAsync(cancellationToken);
                if (fetched.Count > 0)
                {
                    state.Urls = fetched;
                    state.UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    state.Source = "gist";
                    if (!string.IsNullOrWhiteSpace(state.LastUsedUrl)
                        && !state.Urls.Contains(state.LastUsedUrl, StringComparer.OrdinalIgnoreCase))
                    {
                        state.LastUsedUrl = string.Empty;
                    }

                    await SaveStateInternalAsync(state, cancellationToken);
                    return state.Clone();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to refresh Tidal provider list from remote sources.");
            }

            if (state.Urls.Count == 0)
            {
                state.Urls = NormalizeUrls(SeedProviderHosts.Select(BuildHttpsBaseUrl));
                state.UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                state.Source = "seed";
                state.LastUsedUrl = state.Urls.Contains(state.LastUsedUrl, StringComparer.OrdinalIgnoreCase)
                    ? state.LastUsedUrl
                    : string.Empty;
                await SaveStateInternalAsync(state, cancellationToken);
            }

            return state.Clone();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<TidalApiProviderState> LoadStateAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateInternalAsync(cancellationToken);
            return state.Clone();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<TidalApiProviderState> LoadStateInternalAsync(CancellationToken cancellationToken)
    {
        if (_state != null)
        {
            return _state;
        }

        if (!File.Exists(_cachePath))
        {
            _state = new TidalApiProviderState
            {
                Urls = NormalizeUrls(SeedProviderHosts.Select(BuildHttpsBaseUrl)),
                UpdatedAtUnix = 0,
                Source = "seed"
            };
            return _state;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_cachePath, cancellationToken);
            var parsed = JsonSerializer.Deserialize<TidalApiProviderState>(json, JsonOptions) ?? new TidalApiProviderState();
            parsed.Urls = NormalizeUrls(parsed.Urls);
            if (!parsed.Urls.Contains(parsed.LastUsedUrl, StringComparer.OrdinalIgnoreCase))
            {
                parsed.LastUsedUrl = string.Empty;
            }

            if (parsed.Urls.Count == 0)
            {
                parsed.Urls = NormalizeUrls(SeedProviderHosts.Select(BuildHttpsBaseUrl));
                parsed.Source = string.IsNullOrWhiteSpace(parsed.Source) ? "seed" : parsed.Source;
            }

            _state = parsed;
            return _state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load cached Tidal provider list from {Path}. Using seed providers.", _cachePath);
            _state = new TidalApiProviderState
            {
                Urls = NormalizeUrls(SeedProviderHosts.Select(BuildHttpsBaseUrl)),
                Source = "seed"
            };
            return _state;
        }
    }

    private async Task SaveStateInternalAsync(TidalApiProviderState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tempPath = _cachePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _cachePath, overwrite: true);
        _state = state.Clone();
    }

    private async Task<List<string>> FetchProviderUrlsAsync(CancellationToken cancellationToken)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRange(IEnumerable<string> urls)
        {
            foreach (var value in urls)
            {
                var normalized = NormalizeUrl(value);
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                merged.Add(normalized);
            }
        }

        try
        {
            AddRange(await FetchProviderUrlsFromGistAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to refresh Tidal provider list from gist.");
        }

        try
        {
            AddRange(await FetchProviderUrlsFromMonochromeInstancesAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to refresh Tidal provider list from Monochrome instances manifest.");
        }

        return merged;
    }

    private async Task<List<string>> FetchProviderUrlsFromGistAsync(CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(FetchTimeout);

        using var client = _httpClientFactory.CreateClient("TidalProviderList");
        using var response = await client.GetAsync(BuildProviderListGistUrl(), linkedCts.Token);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(linkedCts.Token);
        var urls = JsonSerializer.Deserialize<List<string>>(body, JsonOptions);
        return NormalizeUrls(urls);
    }

    private async Task<List<string>> FetchProviderUrlsFromMonochromeInstancesAsync(CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(FetchTimeout);

        using var client = _httpClientFactory.CreateClient("TidalProviderList");
        using var response = await client.GetAsync(BuildMonochromeInstancesUrl(), linkedCts.Token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
        var manifest = await JsonSerializer.DeserializeAsync<MonochromeInstancesManifest>(stream, JsonOptions, linkedCts.Token);
        return NormalizeUrls(manifest?.Api);
    }

    private static List<string> NormalizeUrls(IEnumerable<string>? urls)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (urls == null)
        {
            return normalized;
        }

        foreach (var value in urls)
        {
            var normalizedUrl = NormalizeUrl(value);
            if (string.IsNullOrWhiteSpace(normalizedUrl) || !seen.Add(normalizedUrl))
            {
                continue;
            }

            normalized.Add(normalizedUrl);
        }

        return normalized;
    }

    private static string NormalizeUrl(string? value) => (value ?? string.Empty).Trim().TrimEnd('/');

    private static string BuildHttpsBaseUrl(string host) => new UriBuilder(HttpsScheme, host).Uri.ToString().TrimEnd('/');

    private static string BuildProviderListGistUrl() => new UriBuilder(HttpsScheme, GistHost) { Path = GistPath }.Uri.ToString();

    private static string BuildMonochromeInstancesUrl() => new UriBuilder(HttpsScheme, MonochromeInstancesHost)
    {
        Path = MonochromeInstancesPath
    }.Uri.ToString();

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

    private sealed class TidalApiProviderState
    {
        public List<string> Urls { get; set; } = new();
        public string LastUsedUrl { get; set; } = string.Empty;
        public long UpdatedAtUnix { get; set; }
        public string Source { get; set; } = string.Empty;

        public TidalApiProviderState Clone() => new()
        {
            Urls = [.. Urls],
            LastUsedUrl = LastUsedUrl,
            UpdatedAtUnix = UpdatedAtUnix,
            Source = Source
        };
    }

    private sealed class MonochromeInstancesManifest
    {
        public List<string> Api { get; set; } = new();
    }
}
