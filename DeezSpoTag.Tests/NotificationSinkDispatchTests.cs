using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Services.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class NotificationSinkDispatchTests : IDisposable
{
    private readonly string _root;
    private readonly NotificationStore _store;
    private readonly NotificationService _service;
    private readonly RecordingListener _listener = new();

    public NotificationSinkDispatchTests()
    {
        _root = Path.Join(Path.GetTempPath(), "deezspotag-sink-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _store = new NotificationStore(new StubEnvironment(_root), NullLogger<NotificationStore>.Instance);
        _service = new NotificationService(
            _store,
            new ThrowingHttpClientFactory(),
            _listener,
            NullLogger<NotificationService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class RecordingListener : IDeezSpoTagListener
    {
        public int SendCount { get; private set; }

        public void Send(string eventName, object? data = null)
        {
            if (eventName == "notificationRaised")
            {
                SendCount++;
            }
        }
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("Webhook must not be used when it is disabled.");
    }

    private sealed class StubEnvironment : IWebHostEnvironment, DeezSpoTag.Web.Services.IAppDataRootOverride
    {
        public string? AppDataRoot { get; }

        public StubEnvironment(string root)
        {
            AppDataRoot = root;
            ContentRootPath = root;
            WebRootPath = root;
            EnvironmentName = "Test";
            ApplicationName = "DeezSpoTag.Tests";
            ContentRootFileProvider = new Microsoft.Extensions.FileProviders.NullFileProvider();
            WebRootFileProvider = new Microsoft.Extensions.FileProviders.NullFileProvider();
        }

        public string WebRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
        public string ApplicationName { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; }
    }

    private async Task DrainAsync()
    {
        await _service.StartAsync(CancellationToken.None);
        for (var attempt = 0; attempt < 50 && await _store.GetUnreadCountAsync() == 0; attempt++)
        {
            await Task.Delay(20);
        }
        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SinkRaise_ReachesTheStoreAndPushesInApp()
    {
        INotificationSink sink = _service;
        sink.Raise(
            "provider_unhealthy",
            "Tidal provider zarz is unavailable",
            "Provider is rate limited.",
            "Warning",
            "provider_unhealthy:tidal:zarz",
            "provider",
            "zarz");

        await DrainAsync();

        var entries = await _store.GetAsync();
        var entry = Assert.Single(entries);
        Assert.Equal("provider_unhealthy", entry.Kind);
        Assert.Equal(NotificationSeverity.Warning, entry.Severity);
        Assert.Equal("zarz", entry.EntityId);
        Assert.Equal(1, _listener.SendCount);
    }

    [Fact]
    public async Task SinkRaise_IgnoresUnknownKinds()
    {
        INotificationSink sink = _service;
        sink.Raise("not_a_real_kind", "nope", "nope");

        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await _service.StopAsync(CancellationToken.None);

        Assert.Empty(await _store.GetAsync());
        Assert.Equal(0, _listener.SendCount);
    }

    [Fact]
    public async Task RepeatedRaises_CollapseIntoOneUnreadEntry()
    {
        INotificationSink sink = _service;
        for (var index = 0; index < 5; index++)
        {
            sink.Raise(
                "download_failed",
                "Download failed: Lil Wayne - Let It All Work Out",
                "All enabled sources were tried.",
                "Warning",
                "download_failed:abc123",
                "download",
                "abc123");
        }

        await DrainAsync();
        await Task.Delay(100);

        var entry = Assert.Single(await _store.GetAsync());
        Assert.Equal("download_failed", entry.Kind);
        Assert.True(entry.OccurrenceCount >= 1);
        Assert.Equal(1, await _store.GetUnreadCountAsync());
    }
}
