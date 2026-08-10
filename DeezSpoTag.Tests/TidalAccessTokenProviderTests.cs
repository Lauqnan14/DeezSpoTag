using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TidalAccessTokenProviderTests
{
    private sealed class StubCredentialProvider : ITidalCredentialProvider
    {
        private readonly TidalOfficialCredentials _credentials;
        public int CallCount { get; private set; }

        public StubCredentialProvider(TidalOfficialCredentials credentials) => _credentials = credentials;

        public Task<TidalOfficialCredentials> GetCredentialsAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_credentials);
        }
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("No HTTP call was expected for a supplied access token.");
    }

    private static string BuildJwt(DateTimeOffset expiry)
    {
        static string Encode(string json)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        var payload = JsonSerializer.Serialize(new { exp = expiry.ToUnixTimeSeconds() });
        return $"{Encode("{\"alg\":\"none\"}")}.{Encode(payload)}.signature";
    }

    private static TidalAccessTokenProvider CreateProvider(string accessToken, out StubCredentialProvider credentials)
    {
        credentials = new StubCredentialProvider(new TidalOfficialCredentials(
            ClientId: string.Empty,
            ClientSecret: string.Empty,
            AccessToken: accessToken,
            RefreshToken: string.Empty,
            UserId: string.Empty,
            CountryCode: "US",
            CredentialsValid: true));
        return new TidalAccessTokenProvider(new UnusedHttpClientFactory(), credentials);
    }

    [Fact]
    public async Task GetAccessTokenAsync_UsesJwtExpiry_InsteadOfAssumedLifetime()
    {
        var token = BuildJwt(DateTimeOffset.UtcNow.AddHours(2));
        var provider = CreateProvider(token, out var credentials);

        Assert.Equal(token, await provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(token, await provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.Equal(1, credentials.CallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_RereadsCredentials_WhenSuppliedTokenAlreadyExpired()
    {
        var token = BuildJwt(DateTimeOffset.UtcNow.AddSeconds(-30));
        var provider = CreateProvider(token, out var credentials);

        await provider.GetAccessTokenAsync(CancellationToken.None);
        await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal(2, credentials.CallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_DoesNotServeTokenExpiringInsideRefreshBuffer()
    {
        var token = BuildJwt(DateTimeOffset.UtcNow.AddSeconds(45));
        var provider = CreateProvider(token, out var credentials);

        await provider.GetAccessTokenAsync(CancellationToken.None);
        await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal(2, credentials.CallCount);
    }

    [Fact]
    public async Task Invalidate_ForcesCredentialReread()
    {
        var provider = CreateProvider(BuildJwt(DateTimeOffset.UtcNow.AddHours(2)), out var credentials);

        await provider.GetAccessTokenAsync(CancellationToken.None);
        provider.Invalidate();
        await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal(2, credentials.CallCount);
    }
}
