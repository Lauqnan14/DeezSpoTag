using System.Text;
using System.Text.Json;
using DeezSpoTag.Integrations.Tidal;

namespace DeezSpoTag.Web.Services;

public sealed class TidalAccessTokenProvider : ITidalAccessTokenProvider
{
    private const string AuthHost = "auth.tidal.com";
    private const string AuthPath = "v1/oauth2/token";
    private static readonly string[] EncodedClientIdSegments =
    [
        "NkJEU1Jk",
        "cEs5aHFF",
        "QlRnVQ=="
    ];
    private static readonly string[] EncodedClientKeySegments =
    [
        "eGV1UG1Z",
        "N25icFo5",
        "SUliTEFj",
        "UTkzc2hr",
        "YTFWTmhl",
        "VUFxTjZJ",
        "Y3N6alRH",
        "OD0="
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITidalCredentialProvider _credentialProvider;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string _cachedAccessToken = string.Empty;
    private DateTimeOffset _cachedAccessTokenExpiresUtc = DateTimeOffset.MinValue;

    public TidalAccessTokenProvider(IHttpClientFactory httpClientFactory, ITidalCredentialProvider credentialProvider)
    {
        _httpClientFactory = httpClientFactory;
        _credentialProvider = credentialProvider;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (IsCachedTokenValid())
        {
            return _cachedAccessToken;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (IsCachedTokenValid())
            {
                return _cachedAccessToken;
            }

            var credentials = await _credentialProvider.GetCredentialsAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(credentials.AccessToken)
                && string.IsNullOrWhiteSpace(credentials.RefreshToken))
            {
                _cachedAccessToken = credentials.AccessToken;
                _cachedAccessTokenExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10);
                return _cachedAccessToken;
            }

            var (defaultClientId, defaultPartnerKey) = DecodePartnerCredentials();
            var clientId = string.IsNullOrWhiteSpace(credentials.ClientId) ? defaultClientId : credentials.ClientId;
            var partnerKey = string.IsNullOrWhiteSpace(credentials.ClientSecret) ? defaultPartnerKey : credentials.ClientSecret;
            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{partnerKey}"));
            var authUrl = new UriBuilder(Uri.UriSchemeHttps, AuthHost) { Path = AuthPath }.Uri;

            using var request = new HttpRequestMessage(HttpMethod.Post, authUrl)
            {
                Content = new FormUrlEncodedContent(BuildTokenRequestFields(credentials, clientId))
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);

            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Tidal auth failed with status {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var token = ReadString(doc.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Tidal auth did not return an access token.");
            }

            var expiresIn = ReadInt(doc.RootElement, "expires_in");
            _cachedAccessToken = token;
            _cachedAccessTokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 300);
            return _cachedAccessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    public async Task<string> GetCountryCodeAsync(CancellationToken cancellationToken)
        => (await _credentialProvider.GetCredentialsAsync(cancellationToken)).CountryCode;

    public async Task<bool> ValidateCredentialsAsync(CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var countryCode = await GetCountryCodeAsync(cancellationToken);
        var url = $"https://api.tidal.com/v1/tracks/251380836?countryCode={Uri.EscapeDataString(countryCode)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public void Invalidate()
    {
        _cachedAccessToken = string.Empty;
        _cachedAccessTokenExpiresUtc = DateTimeOffset.MinValue;
    }

    private bool IsCachedTokenValid()
        => !string.IsNullOrWhiteSpace(_cachedAccessToken)
           && _cachedAccessTokenExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(30);

    private static (string ClientId, string PartnerKey) DecodePartnerCredentials()
    {
        var encodedClientId = string.Concat(EncodedClientIdSegments);
        var encodedPartnerKey = string.Concat(EncodedClientKeySegments);
        var clientId = Encoding.UTF8.GetString(Convert.FromBase64String(encodedClientId));
        var partnerKey = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPartnerKey));
        return (clientId, partnerKey);
    }

    private static Dictionary<string, string> BuildTokenRequestFields(TidalOfficialCredentials credentials, string clientId)
    {
        var fields = new Dictionary<string, string> { ["client_id"] = clientId };
        if (!string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            fields["grant_type"] = "refresh_token";
            fields["refresh_token"] = credentials.RefreshToken;
        }
        else
        {
            fields["grant_type"] = "client_credentials";
        }
        return fields;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var node))
        {
            return string.Empty;
        }

        return node.ValueKind switch
        {
            JsonValueKind.String => node.GetString() ?? string.Empty,
            JsonValueKind.Number => node.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => string.Empty
        };
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var node))
        {
            return 0;
        }

        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value))
        {
            return value;
        }

        if (node.ValueKind == JsonValueKind.String && int.TryParse(node.GetString(), out var parsed))
        {
            return parsed;
        }

        return 0;
    }
}
