using System.Net;
using System.Text.Json;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class MusicBrainzClient
{
    private const string MusicBrainzHost = "musicbrainz.org";

    private readonly HttpClient _httpClient;
    private readonly ILogger<MusicBrainzClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
    private int _backoffMs = 1000;

    public MusicBrainzClient(HttpClient httpClient, ILogger<MusicBrainzClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new UriBuilder(Uri.UriSchemeHttps, MusicBrainzHost)
            {
                Path = "ws/2/"
            }.Uri;
        }
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DeezSpoTag/1.0 (MusicBrainz)");
        }

    }

    public async Task<RecordingSearchResults?> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var response = await GetAsync($"recording?query={Uri.EscapeDataString(query)}&limit=100&fmt=json", cancellationToken);
        if (response == null)
        {
            return null;
        }

        return await DeserializeAsync<RecordingSearchResults>(response, cancellationToken);
    }

    public async Task<BrowseReleases?> GetReleasesAsync(string recordingId, CancellationToken cancellationToken)
    {
        var response = await GetAsync($"release?recording={Uri.EscapeDataString(recordingId)}&inc=labels+isrcs+recordings+genres+tags+release-groups+media&fmt=json", cancellationToken);
        if (response == null)
        {
            return null;
        }

        return await DeserializeAsync<BrowseReleases>(response, cancellationToken);
    }

    public async Task<Recording?> GetRecordingAsync(string recordingId, CancellationToken cancellationToken)
    {
        var response = await GetAsync($"recording/{Uri.EscapeDataString(recordingId)}?inc=artists+releases+isrcs&fmt=json", cancellationToken);
        if (response == null)
        {
            return null;
        }

        return await DeserializeAsync<Recording>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage?> GetAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await WaitForRateLimitAsync(cancellationToken);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(path, cancellationToken);
            }
            catch (Exception ex) when (attempt < 4)
            {
                _logger.LogWarning(ex, "MusicBrainz request failed (attempt {Attempt}): {Detail}", attempt + 1, BuildExceptionDetail(ex));
                continue;
            }

            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or (HttpStatusCode)429)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                Backoff(retryAfter.HasValue ? (int?)Math.Round(retryAfter.Value) : null);
                _logger.LogWarning("MusicBrainz rate limit hit, backing off.");
                continue;
            }

            ResetBackoff();
            response.EnsureSuccessStatusCode();
            return response;
        }

        return null;
    }

    private static string BuildExceptionDetail(Exception ex)
    {
        var inner = ex.InnerException;
        if (inner == null)
        {
            return ex.Message;
        }

        return $"{ex.Message} | inner: {inner.GetType().Name}: {inner.Message}";
    }

    private async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MusicBrainz response deserialization failed.");
            return default;
        }
    }

    private async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_nextAllowed > now)
            {
                await Task.Delay(_nextAllowed - now, cancellationToken);
            }
            _nextAllowed = DateTimeOffset.UtcNow.AddSeconds(1);
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private void Backoff(int? retryAfterSeconds)
    {
        var baseMs = retryAfterSeconds.HasValue ? retryAfterSeconds.Value * 1000 : _backoffMs;
        var jitter = Random.Shared.Next(0, 250);
        var next = Math.Min(baseMs + jitter, 30000);
        _backoffMs = Math.Min(next * 2, 30000);
        _nextAllowed = DateTimeOffset.UtcNow.AddMilliseconds(next);
    }

    private void ResetBackoff()
    {
        _backoffMs = 1000;
    }
}
