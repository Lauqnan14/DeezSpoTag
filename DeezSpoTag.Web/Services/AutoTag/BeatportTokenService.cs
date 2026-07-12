using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed record BeatportAuthorizationRequest(string AuthorizationUrl, string State);

public sealed class BeatportTokenService
{
    private const string AuthorizeUrl = "https://api.beatport.com/v4/auth/o/authorize/";
    private const string TokenUrl = "https://api.beatport.com/v4/auth/o/token/";
    private readonly HttpClient _httpClient;
    private readonly PlatformAuthService _authService;
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, string> Verifiers = new(StringComparer.Ordinal);

    public BeatportTokenService(HttpClient httpClient, PlatformAuthService authService)
    { _httpClient = httpClient; _authService = authService; }

    public async Task<BeatportAuthorizationRequest> CreateAuthorizationRequestAsync(CancellationToken cancellationToken)
    {
        var auth = (await _authService.LoadAsync()).Beatport
            ?? throw new InvalidOperationException("Beatport credentials are not configured.");
        if (string.IsNullOrWhiteSpace(auth.ClientId) || string.IsNullOrWhiteSpace(auth.RedirectUri))
            throw new InvalidOperationException("Beatport client ID and redirect URI are required.");
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Verifiers[state] = verifier;
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = auth.ClientId.Trim(), ["response_type"] = "code",
            ["redirect_uri"] = auth.RedirectUri.Trim(), ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256", ["scope"] = auth.Scope?.Trim()
        };
        return new(BuildUrl(AuthorizeUrl, query), state);
    }

    public async Task CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || !Verifiers.TryRemove(state, out var verifier))
            throw new InvalidOperationException("Invalid or expired Beatport authorization state.");
        var auth = (await _authService.LoadAsync()).Beatport
            ?? throw new InvalidOperationException("Beatport credentials are not configured.");
        await ExchangeAndSaveAsync(auth, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code,
            ["redirect_uri"] = auth.RedirectUri ?? string.Empty, ["client_id"] = auth.ClientId ?? string.Empty,
            ["code_verifier"] = verifier
        }, cancellationToken);
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var auth = (await _authService.LoadAsync()).Beatport;
        if (auth is null) throw new InvalidOperationException("Beatport is not configured.");
        if (!forceRefresh && !string.IsNullOrWhiteSpace(auth.AccessToken)
            && auth.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)) return auth.AccessToken;
        if (string.IsNullOrWhiteSpace(auth.RefreshToken)) throw new InvalidOperationException("Beatport must be connected.");
        await RefreshLock.WaitAsync(cancellationToken);
        try
        {
            auth = (await _authService.LoadAsync()).Beatport ?? auth;
            if (!forceRefresh && !string.IsNullOrWhiteSpace(auth.AccessToken)
                && auth.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)) return auth.AccessToken;
            return await ExchangeAndSaveAsync(auth, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = auth.RefreshToken ?? string.Empty,
                ["client_id"] = auth.ClientId ?? string.Empty
            }, cancellationToken);
        }
        finally { RefreshLock.Release(); }
    }

    private async Task<string> ExchangeAndSaveAsync(BeatportAuth auth, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(fields) };
        if (!string.IsNullOrWhiteSpace(auth.ClientSecret))
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.ClientId}:{auth.ClientSecret}")));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var token = await JsonSerializer.DeserializeAsync<BeatportOAuth>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Beatport returned an empty token response.");
        if (string.IsNullOrWhiteSpace(token.AccessToken)) throw new InvalidOperationException("Beatport returned no access token.");
        await _authService.UpdateAsync(state =>
        {
            state.Beatport ??= auth;
            state.Beatport.AccessToken = token.AccessToken;
            state.Beatport.RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? auth.RefreshToken : token.RefreshToken;
            state.Beatport.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn));
            return true;
        });
        return token.AccessToken;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string BuildUrl(string url, IReadOnlyDictionary<string, string?> values)
        => url + "?" + string.Join("&", values.Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));
}
