using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>The first-party web-client identity needed to sign api.audiomack.com requests.</summary>
public sealed record AudiomackWebCredentials(string ApiBaseUrl, string ConsumerKey, string ConsumerSecret);

/// <summary>
/// Resolves the credentials Audiomack's own web player uses when calling
/// api.audiomack.com/v1. Audiomack publishes this identity inside its public
/// JavaScript bundle (it is neither a developer key nor user-bound, and no
/// cookies or accounts are involved), so the provider discovers the current
/// values from the live bundle and caches them. Last-known values are kept
/// only as a bootstrap/fallback for the case where discovery fails, and the
/// cache is invalidated so a rotated secret self-heals on the next call
/// instead of failing forever.
/// </summary>
public sealed class AudiomackWebCredentialsProvider
{
    private const string DiscoveryPageUrl = "https://audiomack.com/search";
    private const string ChunkUrlPrefix = "/_next/static/chunks/";
    private const int MaxChunkFetches = 20;

    private static readonly Regex ChunkUrlRegex = new("/_next/static/chunks/[^\"'<>]+?\\.js", RegexOptions.Compiled);
    private static readonly Regex ConsumerSecretRegex = new("API_CONSUMER_SECRET:\\\\?\"(?<secret>[0-9a-fA-F]{8,64})\\\\?\"", RegexOptions.Compiled);
    private static readonly Regex ConsumerKeyRegex = new("API_CONSUMER_KEY:\\\\?\"(?<key>[^\"]{1,128})\\\\?\"", RegexOptions.Compiled);
    private static readonly Regex ApiBaseUrlRegex = new("API_PUBLIC_API_URL:\\\\?\"(?<url>https://[^\"\\\\]+)\\\\?\"", RegexOptions.Compiled);

    /// <summary>Last-known web-client identity: a bootstrap/fallback, not a permanent design constant.</summary>
    private static readonly AudiomackWebCredentials FallbackCredentials = new(
        "https://api.audiomack.com/v1/",
        "audiomack-web",
        "bd8a07e9f23fbe9d808646b730f89b8e");

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailedDiscoveryRetryDelay = TimeSpan.FromHours(1);
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AudiomackWebCredentialsProvider> _logger;
    private readonly object _lock = new();
    private (AudiomackWebCredentials Credentials, DateTimeOffset FetchedUtc, bool Discovered)? _cache;

    public AudiomackWebCredentialsProvider(IHttpClientFactory httpClientFactory, ILogger<AudiomackWebCredentialsProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Drops the cached identity so the next call re-discovers from the live bundle.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cache = null;
        }
    }

    public async Task<AudiomackWebCredentials> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_cache.HasValue && DateTimeOffset.UtcNow - _cache.Value.FetchedUtc <= CacheTtl)
            {
                return _cache.Value.Credentials;
            }
        }

        var discovered = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var credentials = discovered ?? FallbackCredentials;
        var discoveredNow = discovered != null;
        lock (_lock)
        {
            // Cache the timestamp of THIS resolution; a failed discovery retries after
            // the (shorter) failure window instead of hammering the bundle on every call.
            _cache = (credentials, DateTimeOffset.UtcNow - (discoveredNow ? TimeSpan.Zero : CacheTtl - FailedDiscoveryRetryDelay), discoveredNow);
        }

        if (discovered == null)
        {
            _logger.LogWarning("Audiomack web credential discovery failed; using last-known client identity");
        }

        return credentials;
    }

    /// <summary>Fetches Audiomack's public search page and its script chunks until the client identity is found.</summary>
    private async Task<AudiomackWebCredentials?> DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            using var pageTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pageTimeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var pageResponse = await httpClient.GetAsync(DiscoveryPageUrl, pageTimeoutCts.Token).ConfigureAwait(false);
            if (!pageResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await pageResponse.Content.ReadAsStringAsync(pageTimeoutCts.Token).ConfigureAwait(false);

            // The identity also appears inline on some deployments; check before fetching chunks.
            var inline = TryExtractCredentials(html);
            if (inline != null)
            {
                return inline;
            }

            // Prefer numeric-id module chunks (where the env/config module lives), keeping page order.
            var chunkUrls = ChunkUrlRegex.Matches(html)
                .Select(m => m.Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var ordered = chunkUrls.Where(IsNumericModuleChunk).Concat(chunkUrls.Where(c => !IsNumericModuleChunk(c)));

            var fetched = 0;
            foreach (var chunkPath in ordered)
            {
                if (fetched >= MaxChunkFetches)
                {
                    break;
                }

                fetched++;
                var chunkUrl = new Uri(new Uri(DiscoveryPageUrl), chunkPath).ToString();
                AudiomackWebCredentials? credentials;
                using (var chunkTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    chunkTimeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
                    using var chunkResponse = await httpClient.GetAsync(chunkUrl, chunkTimeoutCts.Token).ConfigureAwait(false);
                    if (!chunkResponse.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var chunk = await chunkResponse.Content.ReadAsStringAsync(chunkTimeoutCts.Token).ConfigureAwait(false);
                    credentials = TryExtractCredentials(chunk);
                }

                if (credentials != null)
                {
                    return credentials;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Audiomack web credential discovery error");
            return null;
        }
    }

    internal static AudiomackWebCredentials? TryExtractCredentials(string javaScript)
    {
        if (string.IsNullOrWhiteSpace(javaScript))
        {
            return null;
        }

        var secret = ConsumerSecretRegex.Match(javaScript);
        if (!secret.Success)
        {
            return null;
        }

        var key = ConsumerKeyRegex.Match(javaScript);
        var url = ApiBaseUrlRegex.Match(javaScript);
        return new AudiomackWebCredentials(
            url.Success ? url.Groups["url"].Value : FallbackCredentials.ApiBaseUrl,
            key.Success ? key.Groups["key"].Value : FallbackCredentials.ConsumerKey,
            secret.Groups["secret"].Value);
    }

    private static bool IsNumericModuleChunk(string chunkPath)
    {
        var fileName = chunkPath.Split('/').LastOrDefault() ?? string.Empty;
        var dash = fileName.IndexOf('-');
        return dash > 0 && fileName[..dash].All(char.IsDigit);
    }
}
