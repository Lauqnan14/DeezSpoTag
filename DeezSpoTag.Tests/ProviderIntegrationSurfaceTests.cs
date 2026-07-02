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
using DeezSpoTag.Integrations.Qobuz;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ProviderIntegrationSurfaceTests
{
    [Fact]
    public async Task QobuzBuildProviders_UsesEnabledRegistryProviders()
    {
        var registry = new StubQobuzProviderRegistry();
        var service = new QobuzDownloadService(
            NullLogger<QobuzDownloadService>.Instance,
            Options.Create(new QobuzApiConfig()),
            credentialProvider: new StubQobuzCredentialProvider(),
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
            publicProviderRegistry: new StubQobuzProviderRegistry());
        var method = typeof(QobuzDownloadService).GetMethod(
            "TryGetOfficialQobuzStreamUrlAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var result = await Assert.IsAssignableFrom<Task<string?>>(method!.Invoke(service, [123L, "6", CancellationToken.None]));

        Assert.Null(result);
    }

    [Fact]
    public void AmazonDirectStreamProviders_IncludeSpotByeProvider()
    {
        var field = typeof(AmazonDownloadService).GetField("StreamProviderHosts", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);

        var providers = Assert.IsAssignableFrom<string[]>(field!.GetValue(null));

        Assert.Contains("amazon.afkarxyz.fun", providers);
        Assert.Contains("amazon.spotbye.qzz.io", providers);
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
    public void QobuzTryExtractCommonProviderUrlPayload_RejectsHtmlWithProviderLabel()
    {
        var method = typeof(QobuzDownloadService).GetMethod(
            "TryExtractCommonProviderUrlPayload",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var args = new object?[] { "<html></html>", "Provider", null };
        var exception = Assert.Throws<TargetInvocationException>(() => method!.Invoke(null, args));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal("Provider returned HTML instead of JSON.", exception.InnerException!.Message);
    }

    [Fact]
    public void QobuzTryExtractCommonProviderUrlPayload_AcceptsDirectUrlPayload()
    {
        var method = typeof(QobuzDownloadService).GetMethod(
            "TryExtractCommonProviderUrlPayload",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var args = new object?[] { "\"https://example.test/file.flac\"", "Provider", null };
        var success = (bool)method!.Invoke(null, args)!;

        Assert.True(success);
        Assert.Equal("https://example.test/file.flac", args[2] as string);
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

    [Fact]
    public void TidalBuildTrackManifestsUrl_UsesMonochromeRouteAndFormats()
    {
        var method = typeof(TidalDownloadService).GetMethod(
            "BuildTrackManifestsUrl",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var url = Assert.IsType<string>(method!.Invoke(null, ["https://arran.monochrome.tf/", 123L, "LOSSLESS"]));

        Assert.StartsWith("https://arran.monochrome.tf/trackManifests/?", url);
        Assert.Contains("id=123", url);
        Assert.Contains("quality=LOSSLESS", url);
        Assert.Contains("formats=FLAC", url);
    }

    [Fact]
    public void TidalTryExtractManifestUri_ReadsNestedMonochromeResponse()
    {
        var method = typeof(TidalDownloadService).GetMethod(
            "TryExtractManifestUri",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var args = new object?[]
        {
            """{"data":{"data":{"attributes":{"uri":"https://manifest.example.test/signed.mpd"}}}}""",
            null
        };
        var success = (bool)method!.Invoke(null, args)!;

        Assert.True(success);
        Assert.Equal("https://manifest.example.test/signed.mpd", args[1] as string);
    }
}
