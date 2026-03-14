using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class AppleVideoAtmosCapabilityService
{
    private const int MaxConcurrentProbes = 6;
    private static readonly TimeSpan KnownCapabilityCacheLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan UnknownCapabilityCacheLifetime = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MasterManifestCacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CapabilityBatchBudgetSmallSet = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CapabilityBatchBudgetLargeSet = TimeSpan.FromSeconds(8);

    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly AppleWebPlaybackClient _webPlaybackClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AppleVideoAtmosCapabilityService> _logger;

    public AppleVideoAtmosCapabilityService(
        DeezSpoTagSettingsService settingsService,
        AppleWebPlaybackClient webPlaybackClient,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<AppleVideoAtmosCapabilityService> logger)
    {
        _settingsService = settingsService;
        _webPlaybackClient = webPlaybackClient;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, bool?>> GetAtmosCapabilitiesAsync(
        IEnumerable<string> appleIds,
        CancellationToken cancellationToken = default)
    {
        var ids = appleIds
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        }

        var (authorizationToken, mediaUserToken, getM3u8Port) = await ReadAuthTokensAsync(cancellationToken);

        var batchBudget = ids.Count <= 30
            ? CapabilityBatchBudgetSmallSet
            : CapabilityBatchBudgetLargeSet;

        using var semaphore = new SemaphoreSlim(MaxConcurrentProbes);
        using var batchBudgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        batchBudgetCts.CancelAfter(batchBudget);
        var probeToken = batchBudgetCts.Token;
        var results = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        var tasks = ids.Select(async id =>
        {
            var acquired = false;
            try
            {
                await semaphore.WaitAsync(probeToken);
                acquired = true;
                bool? capability;
                try
                {
                    capability = await ProbeWithCacheAsync(id, authorizationToken, mediaUserToken, getM3u8Port, probeToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Probe-level timeout/cancel should not fail the whole batch.
                    capability = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Apple video Atmos probe worker failed for {AppleId}", id);
                    capability = null;
                }

                lock (results)
                {
                    results[id] = capability;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Batch budget elapsed: preserve responsiveness and return unknown for remaining IDs.
                lock (results)
                {
                    if (!results.ContainsKey(id))
                    {
                        results[id] = null;
                    }
                }
            }
            finally
            {
                if (acquired)
                {
                    semaphore.Release();
                }
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Budget timeout: return partial probe results.
        }
        return results;
    }

    public static string ResolveAppleId(string? appleId, string? appleUrl)
        => AppleIdParser.Resolve(appleId, appleUrl) ?? string.Empty;

    private async Task<bool?> ProbeWithCacheAsync(
        string appleId,
        string authorizationToken,
        string mediaUserToken,
        string getM3u8Port,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildCapabilityCacheKey(appleId);
        if (_cache.TryGetValue(cacheKey, out CapabilityCacheEntry? cached) && cached != null)
        {
            return cached.HasAtmos;
        }

        var capability = await ProbeAtmosCapabilityInternalAsync(appleId, authorizationToken, mediaUserToken, getM3u8Port, cancellationToken);
        var ttl = capability.HasValue ? KnownCapabilityCacheLifetime : UnknownCapabilityCacheLifetime;
        _cache.Set(cacheKey, new CapabilityCacheEntry(capability), ttl);
        return capability;
    }

    private async Task<bool?> ProbeAtmosCapabilityInternalAsync(
        string appleId,
        string authorizationToken,
        string mediaUserToken,
        string getM3u8Port,
        CancellationToken cancellationToken)
    {
        try
        {
            string? playlistUrl = null;
            if (!string.IsNullOrWhiteSpace(authorizationToken) && !string.IsNullOrWhiteSpace(mediaUserToken))
            {
                playlistUrl = await _webPlaybackClient.GetWebPlaybackPlaylistAsync(
                    appleId,
                    authorizationToken,
                    mediaUserToken,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(playlistUrl))
            {
                playlistUrl = await TryGetDeviceEnhancedHlsAsync(getM3u8Port, appleId, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(playlistUrl))
            {
                return null;
            }

            var masterText = await GetMasterManifestWithCacheAsync(
                playlistUrl,
                authorizationToken,
                mediaUserToken,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(masterText))
            {
                return null;
            }

            if (HasAtmosMarkersInManifestText(masterText))
            {
                return true;
            }

            var master = AppleHlsManifestParser.ParseMaster(masterText, new Uri(playlistUrl));
            if (master.Variants.Count == 0 && master.Media.Count == 0)
            {
                return null;
            }

            return HasAtmosDownloadCapability(master);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Local probe timeout: treat as unknown capability.
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple video Atmos capability probe failed for {AppleId}", appleId);
            return null;
        }
    }

    private async Task<string?> GetMasterManifestWithCacheAsync(
        string playlistUrl,
        string authorizationToken,
        string mediaUserToken,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildManifestCacheKey(playlistUrl);
        if (_cache.TryGetValue(cacheKey, out ManifestCacheEntry? cached) && cached != null)
        {
            return cached.Manifest;
        }

        try
        {
            var manifest = await FetchMasterManifestAsync(
                playlistUrl,
                includeAuthHeaders: false,
                authorizationToken,
                mediaUserToken,
                cancellationToken);

            // Some signed HLS playlists still require Apple auth headers.
            if (string.IsNullOrWhiteSpace(manifest)
                && !string.IsNullOrWhiteSpace(authorizationToken)
                && !string.IsNullOrWhiteSpace(mediaUserToken))
            {
                manifest = await FetchMasterManifestAsync(
                    playlistUrl,
                    includeAuthHeaders: true,
                    authorizationToken,
                    mediaUserToken,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(manifest))
            {
                return null;
            }

            _cache.Set(cacheKey, new ManifestCacheEntry(manifest), MasterManifestCacheLifetime);
            return manifest;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Request timed out while probing a single manifest.
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple master manifest fetch failed for {PlaylistUrl}", playlistUrl);
            return null;
        }
    }

    private async Task<string?> FetchMasterManifestAsync(
        string playlistUrl,
        bool includeAuthHeaders,
        string authorizationToken,
        string mediaUserToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(8));

            using var request = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.apple.mpegurl, application/x-mpegURL;q=0.9, */*;q=0.8");
            request.Headers.TryAddWithoutValidation("User-Agent", AppleUserAgentPool.GetAuthenticatedUserAgent());
            request.Headers.TryAddWithoutValidation("Origin", "https://music.apple.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://music.apple.com/");

            if (includeAuthHeaders)
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authorizationToken}");
                request.Headers.TryAddWithoutValidation("x-apple-music-user-token", mediaUserToken);
                request.Headers.TryAddWithoutValidation("Media-User-Token", mediaUserToken);
                request.Headers.TryAddWithoutValidation("Cookie", $"media-user-token={mediaUserToken}");
                request.Headers.TryAddWithoutValidation("x-apple-renewal", "true");
            }

            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Apple master manifest fetch returned {StatusCode} for {PlaylistUrl} (authHeaders={AuthHeaders}).",
                    response.StatusCode,
                    playlistUrl,
                    includeAuthHeaders);
                return null;
            }

            var manifest = await response.Content.ReadAsStringAsync(linkedCts.Token);
            return string.IsNullOrWhiteSpace(manifest) ? null : manifest;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool HasAtmosDownloadCapability(AppleHlsMasterManifest master)
    {
        var audioEntries = master.Media
            .Where(entry => string.Equals(entry.Type, "AUDIO", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var atmosGroups = audioEntries
            .Where(IsAtmosAudioEntry)
            .Select(entry => entry.GroupId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var variant in master.Variants)
        {
            if (AppleAtmosHeuristics.ContainsAtmosToken(variant.AudioGroup) || AppleAtmosHeuristics.ContainsAtmosToken(variant.Codecs))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(variant.AudioGroup) && atmosGroups.Contains(variant.AudioGroup))
            {
                return true;
            }
        }

        return audioEntries.Any(IsAtmosAudioEntry);
    }

    private static bool IsAtmosAudioEntry(AppleHlsMediaEntry entry)
    {
        return AppleAtmosHeuristics.ContainsAtmosToken(entry.GroupId)
            || AppleAtmosHeuristics.ContainsAtmosToken(entry.Name)
            || AppleAtmosHeuristics.ContainsAtmosToken(entry.Uri)
            || AppleAtmosHeuristics.ContainsAtmosToken(entry.Characteristics)
            || AppleAtmosHeuristics.IsAtmosChannels(entry.Channels);
    }

    private static bool HasAtmosMarkersInManifestText(string manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest))
        {
            return false;
        }

        return manifest.Contains("GROUP-ID=\"audio-atmos\"", StringComparison.OrdinalIgnoreCase)
            || manifest.Contains("CHANNELS=\"16/JOC\"", StringComparison.OrdinalIgnoreCase)
            || manifest.Contains("VALUE=\"Atmos\"", StringComparison.OrdinalIgnoreCase)
            || manifest.Contains("com.apple.hls.quality\",VALUE=\"Atmos\"", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(string AuthorizationToken, string MediaUserToken, string GetM3u8Port)> ReadAuthTokensAsync(
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var authorizationToken = string.IsNullOrWhiteSpace(settings.AppleMusic?.AuthorizationToken)
            ? settings.AuthorizationToken
            : settings.AppleMusic.AuthorizationToken;
        var mediaUserToken = settings.AppleMusic?.MediaUserToken ?? string.Empty;
        var getM3u8Port = settings.AppleMusic?.GetM3u8Port ?? "127.0.0.1:20020";

        authorizationToken = authorizationToken?.Trim() ?? string.Empty;
        mediaUserToken = mediaUserToken.Trim();
        getM3u8Port = getM3u8Port.Trim();

        if (!string.IsNullOrWhiteSpace(authorizationToken) && !string.IsNullOrWhiteSpace(mediaUserToken))
        {
            return (authorizationToken, mediaUserToken, getM3u8Port);
        }

        var wrapperTokens = await TryReadWrapperTokensAsync(getM3u8Port, cancellationToken);
        if (!string.IsNullOrWhiteSpace(wrapperTokens.AuthorizationToken))
        {
            authorizationToken = wrapperTokens.AuthorizationToken;
        }

        if (!string.IsNullOrWhiteSpace(wrapperTokens.MediaUserToken))
        {
            mediaUserToken = wrapperTokens.MediaUserToken;
        }

        return (authorizationToken, mediaUserToken, getM3u8Port);
    }

    private async Task<(string AuthorizationToken, string MediaUserToken)> TryReadWrapperTokensAsync(
        string getM3u8Port,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(getM3u8Port))
        {
            return (string.Empty, string.Empty);
        }

        var parsedEndpoint = TryParseHostAndPort(getM3u8Port, 20020);
        var host = parsedEndpoint?.Host ?? "127.0.0.1";

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            var client = _httpClientFactory.CreateClient();
            var wrapperUri = new UriBuilder(Uri.UriSchemeHttp, host, 30020).Uri;
            using var response = await client.GetAsync(wrapperUri, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return (string.Empty, string.Empty);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
            var root = doc.RootElement;

            var authorizationToken = ReadWrapperToken(root, "dev_token");
            var mediaUserToken = ReadWrapperToken(root, "music_token");
            if (string.IsNullOrWhiteSpace(mediaUserToken))
            {
                mediaUserToken = ReadWrapperToken(root, "music_user_token");
            }

            if (!string.IsNullOrWhiteSpace(authorizationToken) || !string.IsNullOrWhiteSpace(mediaUserToken))
            {
                _logger.LogDebug(
                    "Apple Atmos probe using wrapper tokens from {Host}:30020 (auth={HasAuth}, music={HasMusic}).",
                    host,
                    !string.IsNullOrWhiteSpace(authorizationToken),
                    !string.IsNullOrWhiteSpace(mediaUserToken));
            }

            return (authorizationToken, mediaUserToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (string.Empty, string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple Atmos probe wrapper token read failed for host {Host}.", host);
            return (string.Empty, string.Empty);
        }
    }

    private static string ReadWrapperToken(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return value.GetString()?.Trim() ?? string.Empty;
    }

    private static async Task<string?> TryGetDeviceEnhancedHlsAsync(
        string hostAndPort,
        string appleId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostAndPort) || string.IsNullOrWhiteSpace(appleId))
        {
            return null;
        }

        var parsedEndpoint = TryParseHostAndPort(hostAndPort, 20020);
        if (parsedEndpoint == null)
        {
            return null;
        }

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(parsedEndpoint.Value.Host, parsedEndpoint.Value.Port, connectCts.Token);

            await using var stream = client.GetStream();
            var idBytes = Encoding.UTF8.GetBytes(appleId);
            if (idBytes.Length > byte.MaxValue)
            {
                return null;
            }

            ReadOnlyMemory<byte> idLengthPrefix = new byte[] { (byte)idBytes.Length };
            await stream.WriteAsync(idLengthPrefix, cancellationToken);
            await stream.WriteAsync((ReadOnlyMemory<byte>)idBytes, cancellationToken);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(CancellationToken.None);
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static (string Host, int Port)? TryParseHostAndPort(string input, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var value = input.Trim();

        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                var uriPort = uri.Port > 0 ? uri.Port : defaultPort;
                return (uri.Host, uriPort);
            }

            return null;
        }

        if (Uri.TryCreate($"tcp://{value}", UriKind.Absolute, out var tcpUri) && !string.IsNullOrWhiteSpace(tcpUri.Host))
        {
            var tcpPort = tcpUri.Port > 0 ? tcpUri.Port : defaultPort;
            return (tcpUri.Host, tcpPort);
        }

        var split = value.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (split.Length == 2 && int.TryParse(split[1], out var explicitPort) && !string.IsNullOrWhiteSpace(split[0]))
        {
            return (split[0], explicitPort);
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            return (value, defaultPort);
        }

        return null;
    }

    private static string BuildCapabilityCacheKey(string appleId)
        => $"apple:video:atmos-capability:v3:{appleId}";

    private static string BuildManifestCacheKey(string playlistUrl)
        => $"apple:video:master-manifest:{playlistUrl}";

    private sealed record CapabilityCacheEntry(bool? HasAtmos);
    private sealed record ManifestCacheEntry(string Manifest);
}
