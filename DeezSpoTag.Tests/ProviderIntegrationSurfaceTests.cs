using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Integrations.Qobuz;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ProviderIntegrationSurfaceTests
{
    [Theory]
    [InlineData(401, "SESSION_INVALID", "gateway", "bootstrap_session", ZarzResponseDisposition.SessionInvalid)]
    [InlineData(428, "VERIFY_REQUIRED", "gateway", "verify", ZarzResponseDisposition.VerificationRequired)]
    [InlineData(403, "REQUEST_AUTH_INVALID", "gateway", "", ZarzResponseDisposition.RequestAuthenticationInvalid)]
    [InlineData(401, "SESSION_INVALID", "provider", "bootstrap_session", ZarzResponseDisposition.None)]
    [InlineData(403, "REQUEST_AUTH_INVALID", "gateway", "verify", ZarzResponseDisposition.None)]
    public void ZarzSessionCoordinator_ClassifiesOnlyCanonicalGatewayContracts(
        int statusCode,
        string code,
        string origin,
        string action,
        ZarzResponseDisposition expected)
    {
        var method = typeof(ZarzSignedSessionCoordinator).GetMethod(
            "Classify",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [
            (HttpStatusCode)statusCode,
            new ZarzErrorContract { Code = code, Origin = origin, Action = action }
        ]);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ZarzSessionRateLimitException_ParsesRetryAfterFromGatewayBody()
    {
        var exception = ZarzSessionRateLimitException.TryCreate(
            "Amazon session bootstrap",
            HttpStatusCode.TooManyRequests,
            """{"error":"Temporarily blocked. Please try again later.","retry_after":50239}""");

        Assert.NotNull(exception);
        Assert.Equal(50239, exception!.RetryAfterSeconds);
        Assert.Contains("temporarily rate limited", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZarzSignedSessionContract_MatchesSpotiflacChallengeAndBootstrapShape()
    {
        Assert.Equal("spotiflac://session-grant", ZarzSignedSessionContract.CallbackUrl);
        Assert.Equal("extension", ZarzSignedSessionContract.Platform);
        Assert.Equal("ZARZ-HMAC-V1", ZarzSignedSessionContract.SchemeLabel);

        var bootstrap = ZarzSignedSessionContract.BuildBootstrapQuery("abc123", "tidal-web@1.1.0");
        Assert.Contains("app_version=tidal-web%401.1.0", bootstrap, StringComparison.Ordinal);
        Assert.Contains("install_id=abc123", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("platform=", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("callback_url=", bootstrap, StringComparison.Ordinal);

        var mobileChallenge = ZarzSignedSessionContract.BuildChallengeUrl(
            "https://api.zarz.moe/v2",
            "/challenge",
            "challenge-1",
            "install-state");
        Assert.Contains("id=challenge-1", mobileChallenge, StringComparison.Ordinal);
        Assert.Contains("cb=", mobileChallenge, StringComparison.Ordinal);
        Assert.Contains("cb_version%3Dv2grant", mobileChallenge, StringComparison.Ordinal);
        Assert.Contains("state%3Dinstall-state", mobileChallenge, StringComparison.Ordinal);
        Assert.Contains("spotiflac%3A%2F%2Fsession-grant", mobileChallenge, StringComparison.Ordinal);

        var webChallenge = ZarzSignedSessionContract.BuildChallengeUrl(
            "https://api.zarz.moe/v2",
            "/challenge",
            "challenge-1",
            "install-state",
            publicAppBaseUrl: "http://192.168.28.24:8668");
        Assert.Contains("id=challenge-1", webChallenge, StringComparison.Ordinal);
        Assert.Contains("session-grant", webChallenge, StringComparison.Ordinal);
        Assert.Contains("192.168.28.24", webChallenge, StringComparison.Ordinal);
        Assert.DoesNotContain("spotiflac%3A%2F%2Fsession-grant", webChallenge, StringComparison.Ordinal);
    }

    [Fact]
    public void ZarzEnginePorts_MatchSpotiflacSignedSessionContract()
    {
        var sources = new[]
        {
            ReadSource("DeezSpoTag.Services", "Download", "Amazon", "AmazonDownloadService.cs"),
            ReadSource("DeezSpoTag.Services", "Download", "Qobuz", "QobuzDownloadService.cs"),
            ReadSource("DeezSpoTag.Services", "Download", "Tidal", "TidalDownloadService.cs")
        };

        foreach (var source in sources)
        {
            Assert.Contains("ZarzSignedSessionContract.BuildBootstrapQuery", source, StringComparison.Ordinal);
            Assert.Contains("ZarzSignedSessionContract.ResolveVerificationUrl", source, StringComparison.Ordinal);
            Assert.Contains("ZarzSignedSessionContract.ExchangeGrantAsync", source, StringComparison.Ordinal);
            Assert.Contains("ZarzSignedSessionContract.RefreshPath", source, StringComparison.Ordinal);
            Assert.Contains("ZarzSignedSessionContract.CallbackUrl", source, StringComparison.Ordinal);
            Assert.DoesNotContain("https://api.zarz.moe/v2/session/callback", source, StringComparison.Ordinal);
            Assert.DoesNotContain("""["callback_url"] = ZarzCallbackUrl""", source, StringComparison.Ordinal);
            Assert.DoesNotContain("""["platform"] = ZarzPlatform""", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task QobuzBuildProviders_UsesEnabledRegistryProviders()
    {
        var registry = new StubQobuzProviderRegistry();
        var service = new QobuzDownloadService(
            NullLogger<QobuzDownloadService>.Instance,
            Options.Create(new QobuzApiConfig()),
            credentialProvider: new StubQobuzCredentialProvider(),
            zarzSessions: new ZarzSignedSessionCoordinator(NullLogger<ZarzSignedSessionCoordinator>.Instance),
            publicProviderRegistry: registry);
        var method = typeof(QobuzDownloadService).GetMethod("BuildPublicProvidersAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(service, [123L, "27", CancellationToken.None]));
        await task;
        var result = task.GetType().GetProperty("Result")?.GetValue(task);

        var providers = Assert.IsAssignableFrom<Array>(result);
        var names = providers
            .Cast<object>()
            .Select(provider => provider.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(provider)?.ToString())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();

        Assert.Contains("Enabled provider", names);
        Assert.DoesNotContain("Disabled provider", names);
    }

    private sealed class StubQobuzProviderRegistry : IQobuzPublicProviderRegistry
    {
        public Task<IReadOnlyList<QobuzPublicProvider>> GetProvidersAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<QobuzPublicProvider>>([
                new("enabled", "Enabled provider", "zarz-v2", "https://example.com/enabled", null, null, null, true, "unknown", null, null, null, null, null, null),
                new("disabled", "Disabled provider", "zarz-v2", "https://example.com/disabled", null, null, null, false, "disabled", null, null, null, null, null, null)
            ]);

        public Task<IReadOnlyList<QobuzPublicProvider>> CheckEnabledProvidersAsync(CancellationToken cancellationToken)
            => GetProvidersAsync(cancellationToken);

        public Task<QobuzPublicProvider?> SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken) => Task.FromResult<QobuzPublicProvider?>(null);
        public Task RecordSuccessAsync(string providerId, long responseTimeMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordFailureAsync(string providerId, string category, long responseTimeMs, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubQobuzCredentialProvider : IQobuzCredentialProvider
    {
        public StubQobuzCredentialProvider(
            string appId = "app-id",
            string authToken = "auth-token",
            string appSecret = "app-secret")
        {
            Credentials = new QobuzOfficialCredentials(appId, authToken, appSecret);
        }

        private QobuzOfficialCredentials Credentials { get; }

        public Task<QobuzOfficialCredentials> GetCredentialsAsync(CancellationToken cancellationToken)
            => Task.FromResult(Credentials);
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !System.IO.Directory.Exists(System.IO.Path.Combine(directory.FullName, "DeezSpoTag.Services")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return System.IO.File.ReadAllText(System.IO.Path.Combine([directory!.FullName, .. segments]));
    }

    [Theory]
    [InlineData("", "auth-token")]
    [InlineData("app-secret", "")]
    public async Task QobuzOfficialStreamResolution_SkipsOfficialPath_WhenPersonalCredentialsAreMissing(
        string appSecret,
        string authToken)
    {
        var service = new QobuzDownloadService(
            NullLogger<QobuzDownloadService>.Instance,
            Options.Create(new QobuzApiConfig()),
            credentialProvider: new StubQobuzCredentialProvider(authToken: authToken, appSecret: appSecret),
            zarzSessions: new ZarzSignedSessionCoordinator(NullLogger<ZarzSignedSessionCoordinator>.Instance),
            publicProviderRegistry: new StubQobuzProviderRegistry());
        var method = typeof(QobuzDownloadService).GetMethod(
            "TryGetOfficialQobuzStreamUrlAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var result = await Assert.IsAssignableFrom<Task<string?>>(method!.Invoke(service, [123L, "6", CancellationToken.None]));

        Assert.Null(result);
    }

    [Fact]
    public void QobuzTryExtractProviderUrl_AcceptsDownloadUrlAtRoot()
    {
        using var document = JsonDocument.Parse("""{"success":true,"download_url":"https://example.test/file.flac"}""");
        var method = typeof(QobuzDownloadService).GetMethod("TryExtractProviderUrl", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var args = new object?[] { document.RootElement, null };
        var success = (bool)method!.Invoke(null, args)!;

        Assert.True(success);
        Assert.Equal("https://example.test/file.flac", args[1] as string);
    }

    [Fact]
    public void QobuzTryExtractProviderUrl_AcceptsDownloadUrlInDataNode()
    {
        using var document = JsonDocument.Parse("""{"data":{"download_url":"https://example.test/data.flac"}}""");
        var method = typeof(QobuzDownloadService).GetMethod("TryExtractProviderUrl", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var args = new object?[] { document.RootElement, null };
        var success = (bool)method!.Invoke(null, args)!;

        Assert.True(success);
        Assert.Equal("https://example.test/data.flac", args[1] as string);
    }

    [Fact]
    public async Task QobuzReadProviderResponseBody_RejectsEmptyBodyWithProviderLabel()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(" ")
        };
        var method = typeof(QobuzDownloadService).GetMethod(
            "ReadProviderResponseBodyAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task<string>>(method!.Invoke(null, [response, "MusicDL provider", CancellationToken.None]));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("MusicDL provider returned an empty response.", exception.Message);
    }

    [Fact]
    public void QobuzTryExtractQualityResolution_RejectsHtmlWithProviderLabel()
    {
        var method = typeof(QobuzDownloadService).GetMethod(
            "TryExtractQualityResolution",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var args = new object?[] { "<html></html>", "Provider", 123L, "6", "provider", null };
        var exception = Assert.Throws<TargetInvocationException>(() => method!.Invoke(null, args));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal("Provider returned HTML instead of JSON.", exception.InnerException!.Message);
    }

    [Fact]
    public void QobuzTryExtractQualityResolution_AcceptsDirectUrlPayload()
    {
        var method = typeof(QobuzDownloadService).GetMethod(
            "TryExtractQualityResolution",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var args = new object?[] { "\"https://example.test/file.flac\"", "Provider", 123L, "6", "provider", null };
        var success = (bool)method!.Invoke(null, args)!;

        Assert.True(success);
        var resolution = Assert.IsType<QobuzQualityResolution>(args[5]);
        Assert.Equal("https://example.test/file.flac", resolution.DownloadUrl);
    }

    [Fact]
    public void QobuzTryExtractMonochromeQobuzTrackId_PrefersIsrcMatch()
    {
        var method = typeof(QobuzDownloadService).GetMethod(
            "TryExtractMonochromeQobuzTrackId",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.Null(method);
    }

    [Fact]
    public void QobuzTryExtractMonochromeQobuzTrackId_UsesTrackItemsWhenAlbumItemsAppearFirst()
    {
        var method = typeof(QobuzDownloadService).GetMethod(
            "TryExtractMonochromeQobuzTrackId",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.Null(method);
    }

}
