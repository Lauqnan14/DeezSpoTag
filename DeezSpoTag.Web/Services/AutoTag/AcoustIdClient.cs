using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
/// AcoustID lookup client: fingerprint → matching MusicBrainz recording IDs. Uses the
/// same public client key Picard ships and honors AcoustID's minimum request delay.
/// The lookup returns recording IDs ordered by match score; each recording is then
/// resolved against MusicBrainz through the normal match pipeline.
/// </summary>
public sealed class AcoustIdClient
{
    // Public AcoustID client application key — the same key the Picard client ships
    // (MusicBrainz Picard, picard/const: ACOUSTID_KEY). Client keys identify the
    // application, not a user account.
    private const string ClientKey = "v8pQ6oyB";
    private static readonly TimeSpan MinRequestDelay = TimeSpan.FromMilliseconds(333);

    private readonly HttpClient _httpClient;
    private readonly ILogger<AcoustIdClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public AcoustIdClient(HttpClient httpClient, ILogger<AcoustIdClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri("https://api.acoustid.org");
        }
    }

    public async Task<AcoustIdLookupResponse?> LookupAsync(string fingerprint, int? durationSeconds, CancellationToken cancellationToken)
    {
        var duration = durationSeconds is > 0 ? durationSeconds.Value.ToString() : string.Empty;
        var path = $"/v2/lookup?client={Uri.EscapeDataString(ClientKey)}&meta={Uri.EscapeDataString("recordings+releases+compress+sources")}"
            + (duration.Length > 0 ? $"&duration={duration}" : string.Empty)
            + $"&fingerprint={Uri.EscapeDataString(fingerprint)}";

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await WaitForRateLimitAsync(cancellationToken);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(path, cancellationToken);
            }
            catch (Exception ex) when (attempt < 3 && ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "AcoustID lookup request failed (attempt {Attempt}).", attempt + 1);
                continue;
            }

            try
            {
                response.EnsureSuccessStatusCode();
                return await DeserializeAsync<AcoustIdLookupResponse>(response, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to read the AcoustID lookup response.");
                return null;
            }
        }

        return null;
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
            _logger.LogWarning(ex, "AcoustID response deserialization failed.");
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

            _nextAllowed = DateTimeOffset.UtcNow.Add(MinRequestDelay);
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}

public sealed class AcoustIdLookupResponse
{
    public string? Status { get; set; }
    public AcoustIdError? Error { get; set; }
    [JsonPropertyName("results")]
    public List<AcoustIdResult>? Results { get; set; }
}

public sealed class AcoustIdError
{
    public string? Message { get; set; }
    public int? Code { get; set; }
}

public sealed class AcoustIdResult
{
    public double Score { get; set; }
    [JsonPropertyName("recordings")]
    public List<AcoustIdRecording>? Recordings { get; set; }
}

public sealed class AcoustIdRecording
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public int? Duration { get; set; }
    [JsonPropertyName("artistcredit")]
    public List<AcoustIdCredit>? ArtistCredit { get; set; }
    [JsonPropertyName("artists")]
    public List<AcoustIdArtist>? Artists { get; set; }
    [JsonPropertyName("releases")]
    public List<AcoustIdRelease>? Releases { get; set; }
}

public sealed class AcoustIdCredit
{
    public string? Name { get; set; }
    [JsonPropertyName("joinphrase")]
    public string? JoinPhrase { get; set; }
}

public sealed class AcoustIdArtist
{
    public string? Name { get; set; }
}

public sealed class AcoustIdRelease
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    [JsonPropertyName("date")]
    public AcoustIdReleaseDate? Date { get; set; }
}

public sealed class AcoustIdReleaseDate
{
    public string? Year { get; set; }
}