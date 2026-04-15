using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyAppTokenService
{
    private const string SpotifyAccountsHost = "accounts.spotify.com";
    private const string SpotifyTokenPath = "api/token";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpotifyAppTokenService> _logger;
    private readonly PlatformAuthService _platformAuthService;
    private readonly LibraryConfigStore _configStore;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt;
    private string _clientId = string.Empty;
    private string _clientSecret = string.Empty;

    public SpotifyAppTokenService(
        IHttpClientFactory httpClientFactory,
        PlatformAuthService platformAuthService,
        LibraryConfigStore configStore,
        ILogger<SpotifyAppTokenService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _platformAuthService = platformAuthService;
        _configStore = configStore;
        _logger = logger;
    }

    public async Task<string?> GetAppTokenAsync(string? clientId, string? clientSecret, CancellationToken cancellationToken)
    {
        await LoadCredentialsAsync(clientId, clientSecret);
        if (!HasCredentials())
        {
            _logger.LogWarning("Spotify app token unavailable: missing client credentials.");
            return null;
        }

        if (TryGetCachedToken(out var cachedToken))
        {
            return cachedToken;
        }

        return await RequestNewTokenAsync(cancellationToken);
    }

    public void InvalidateToken()
    {
        _cachedToken = null;
        _expiresAt = DateTimeOffset.MinValue;
    }

    private bool HasCredentials()
    {
        return !string.IsNullOrWhiteSpace(_clientId) && !string.IsNullOrWhiteSpace(_clientSecret);
    }

    private bool TryGetCachedToken(out string? token)
    {
        if (!string.IsNullOrWhiteSpace(_cachedToken) && DateTimeOffset.UtcNow < _expiresAt)
        {
            token = _cachedToken;
            return true;
        }

        token = null;
        return false;
    }

    private async Task<string?> RequestNewTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildTokenUri())
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials"
                })
            };
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_clientId}:{_clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var snippet = BuildPayloadSnippet(body);
                _logger.LogWarning("Spotify app token request failed: {Status}", response.StatusCode);
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "warn",
                    $"[spotify] app token failed: {(int)response.StatusCode} {response.StatusCode} body={snippet}"));
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var token = JsonSerializer.Deserialize<AppTokenPayload>(payload, _jsonOptions);
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                var snippet = BuildPayloadSnippet(payload);
                _logger.LogWarning("Spotify app token response missing access_token. Payload: {Snippet}", snippet);
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "warn",
                    $"[spotify] app token response missing access_token. payload={snippet}"));
                return null;
            }

            var expiresIn = token.ExpiresIn <= 0 ? 3600 : token.ExpiresIn;
            _cachedToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);
            return _cachedToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify app token request failed.");
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"[spotify] app token request error: {ex.Message}"));
        }

        return null;
    }

    private static Uri BuildTokenUri()
    {
        return new UriBuilder(Uri.UriSchemeHttps, SpotifyAccountsHost)
        {
            Path = SpotifyTokenPath
        }.Uri;
    }

    private static string BuildPayloadSnippet(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "(empty)";
        }

        return payload.Length > 200 ? payload[..200] : payload;
    }

    private async Task LoadCredentialsAsync(string? clientId, string? clientSecret)
    {
        try
        {
            var resolvedClientId = clientId?.Trim();
            var resolvedClientSecret = clientSecret?.Trim();

            if (string.IsNullOrWhiteSpace(resolvedClientId) || string.IsNullOrWhiteSpace(resolvedClientSecret))
            {
                var state = await _platformAuthService.LoadAsync();
                (resolvedClientId, resolvedClientSecret) = SpotifyCredentialParser.ParseClientCredentials(
                    state.Spotify?.ClientId,
                    state.Spotify?.ClientSecret);
            }

            _clientId = resolvedClientId ?? string.Empty;
            _clientSecret = resolvedClientSecret ?? string.Empty;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Spotify API credentials loaded. configured={Configured} clientIdLen={ClientIdLen} clientSecretLen={ClientSecretLen}",
                    !string.IsNullOrWhiteSpace(_clientId) && !string.IsNullOrWhiteSpace(_clientSecret),
                    _clientId.Length,
                    _clientSecret.Length);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Spotify API credentials.");
        }
    }

    private sealed class AppTokenPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
