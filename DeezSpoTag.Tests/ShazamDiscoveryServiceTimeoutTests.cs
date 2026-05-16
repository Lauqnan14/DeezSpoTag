using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ShazamDiscoveryServiceTimeoutTests
{
    [Fact]
    public async Task GetRelatedTracksAsync_WhenHttpClientTimesOut_ReturnsEmptyList()
    {
        using var httpClient = new HttpClient(new DelayedHandler(TimeSpan.FromMilliseconds(200)))
        {
            Timeout = TimeSpan.FromMilliseconds(25)
        };
        using var environment = new TestWebHostEnvironment();
        var service = new ShazamDiscoveryService(httpClient, NullLogger<ShazamDiscoveryService>.Instance, environment);

        var tracks = await service.GetRelatedTracksAsync("123456", cancellationToken: CancellationToken.None);

        Assert.Empty(tracks);
    }

    [Fact]
    public async Task GetRelatedTracksAsync_WhenCallerCancels_ThrowsCancellation()
    {
        using var httpClient = new HttpClient(new DelayedHandler(TimeSpan.FromMilliseconds(200)))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using var environment = new TestWebHostEnvironment(createSlowDiscoverScript: true);
        var service = new ShazamDiscoveryService(httpClient, NullLogger<ShazamDiscoveryService>.Instance, environment);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetRelatedTracksAsync("123456", cancellationToken: cts.Token));
    }

    private sealed class DelayedHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public DelayedHandler(TimeSpan delay)
        {
            _delay = delay;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment, IDisposable
    {
        private readonly string _rootPath = Path.Join(Path.GetTempPath(), "deezspotag-shazam-tests-" + Path.GetRandomFileName());

        public TestWebHostEnvironment(bool createSlowDiscoverScript = false)
        {
            Directory.CreateDirectory(_rootPath);
            if (createSlowDiscoverScript)
            {
                CreateSlowDiscoverScript();
            }

            ContentRootPath = _rootPath;
            ContentRootFileProvider = new PhysicalFileProvider(_rootPath);
            WebRootPath = _rootPath;
            WebRootFileProvider = new PhysicalFileProvider(_rootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }

        private void CreateSlowDiscoverScript()
        {
            var scriptDirectory = Path.Combine(_rootPath, "Tools", "shazam_port");
            Directory.CreateDirectory(scriptDirectory);
            File.WriteAllText(
                Path.Combine(scriptDirectory, "discover.py"),
                "import time\n"
                + "time.sleep(5)\n"
                + "print('{\"ok\": true, \"tracks\": []}')\n");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
