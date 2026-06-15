using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QobuzAccountProfileServiceTests
{
    [Fact]
    public async Task FetchAsync_ParsesOfficialCountryAndAccountDetails()
    {
        const string payload = """
        {
          "user": {
            "id": 12345,
            "display_name": "Qobuz Listener",
            "country": "KE",
            "zone": "europe",
            "credential": { "parameters": { "short_label": "Studio" } },
            "subscription": { "offer": "studio" }
          }
        }
        """;
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            Assert.Equal("app-id", request.Headers.GetValues("X-App-Id").Single());
            Assert.Equal("user-token", request.Headers.GetValues("X-User-Auth-Token").Single());
            Assert.Contains("extra=partner", request.RequestUri?.Query, StringComparison.Ordinal);
            return JsonResponse(HttpStatusCode.OK, payload);
        }));
        var service = new QobuzAccountProfileService(client, NullLogger<QobuzAccountProfileService>.Instance);

        var result = await service.FetchAsync("app-id", "user-token", CancellationToken.None);

        Assert.Equal(QobuzAccountProfileStatus.Valid, result.Status);
        Assert.Equal("12345", result.Profile?.UserId);
        Assert.Equal("Qobuz Listener", result.Profile?.DisplayName);
        Assert.Equal("KE", result.Profile?.Country);
        Assert.Equal("europe", result.Profile?.Zone);
        Assert.Equal("Studio", result.Profile?.CredentialLabel);
        Assert.Equal("studio", result.Profile?.SubscriptionOffer);
    }

    [Fact]
    public async Task FetchAsync_IdentifiesInvalidToken()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            JsonResponse(HttpStatusCode.Unauthorized, "{}")));
        var service = new QobuzAccountProfileService(client, NullLogger<QobuzAccountProfileService>.Instance);

        var result = await service.FetchAsync("app-id", "bad-token", CancellationToken.None);

        Assert.Equal(QobuzAccountProfileStatus.InvalidToken, result.Status);
        Assert.Null(result.Profile);
    }

    [Fact]
    public async Task FetchAsync_DoesNotTreatServerFailureAsInvalidToken()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            JsonResponse(HttpStatusCode.ServiceUnavailable, "{}")));
        var service = new QobuzAccountProfileService(client, NullLogger<QobuzAccountProfileService>.Instance);

        var result = await service.FetchAsync("app-id", "user-token", CancellationToken.None);

        Assert.Equal(QobuzAccountProfileStatus.Unavailable, result.Status);
        Assert.Null(result.Profile);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
