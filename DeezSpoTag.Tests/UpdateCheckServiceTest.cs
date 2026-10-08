using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the release lookup and the shared minimum-interval cache: a prerelease-only repository
/// must be handled, a second call must not reach GitHub again, and one notification must be raised
/// per newly seen tag.
/// </summary>
public sealed class UpdateCheckServiceTest
{
    private const string ReleasesPath = "repos/Lauqnan14/DeezSpoTag/releases";

    [Fact]
    public async Task PrereleaseBranch_PicksTheNewestPrereleaseAndReportsAnUpdate()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleasesJson(
            ("v0.1.27.6-pre", Prerelease: true),
            ("v0.1.27.5-pre", Prerelease: true),
            ("v0.1.26.0", Prerelease: false)));
        var service = BuildService(handler, branch: "main");

        var status = await service.GetStatusAsync();

        Assert.Equal("v0.1.27.6-pre", status.LatestVersion);
        Assert.True(status.UpdateAvailable);
        Assert.Equal("main", status.Branch);
        Assert.Contains("tree/main", status.BranchUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableBranch_NeverSeesPrereleases()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleasesJson(
            ("v0.1.27.6-pre", Prerelease: true),
            ("v0.1.27.5-pre", Prerelease: true),
            ("v0.1.26.0", Prerelease: false)));
        var service = BuildService(handler, branch: "stable");

        var status = await service.GetStatusAsync();

        Assert.Equal("v0.1.26.0", status.LatestVersion);
    }

    [Fact]
    public async Task StableBranch_WithNoStableRelease_ReportsNothingRatherThanBorrowingAPrerelease()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleasesJson(
            ("v0.1.27.6-pre", Prerelease: true),
            ("v0.1.27.5-pre", Prerelease: true)));
        var service = BuildService(handler, branch: "stable");

        var status = await service.GetStatusAsync();

        Assert.Null(status.LatestVersion);
        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    public async Task UnknownBranch_PerformsNoOutboundCallAtAll()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleasesJson(("v0.1.27.6-pre", Prerelease: true)));
        var sink = new RecordingNotificationSink();
        var service = BuildService(handler, branch: "feature/personal-genre", sink: sink);

        var status = await service.GetStatusAsync();

        Assert.Null(status.LatestVersion);
        Assert.False(status.UpdateAvailable);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(sink.Raised);
    }

    [Fact]
    public async Task SecondCallWithinTheInterval_IsServedFromCache()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleasesJson(("v0.1.27.6-pre", Prerelease: true)));
        var service = BuildService(handler, branch: "main", minimumIntervalMinutes: 15);

        var first = await service.GetStatusAsync();
        var second = await service.GetStatusAsync();

        Assert.Equal(1, handler.CallCount);
        Assert.False(first.IsStale);
        Assert.True(second.IsStale);
        Assert.Equal(first.LatestVersion, second.LatestVersion);
    }

    [Fact]
    public async Task RepeatedPolling_RaisesTheUpdateNotificationOnlyOnce()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleasesJson(("v0.1.27.6-pre", Prerelease: true)));
        var sink = new RecordingNotificationSink();
        var service = BuildService(handler, branch: "main", sink: sink);

        await service.GetStatusAsync();
        await service.GetStatusAsync();
        await service.GetStatusAsync();

        var raised = Assert.Single(sink.Raised);
        Assert.Equal("app_update_available", raised.kind);
        Assert.Contains("v0.1.27.6-pre", raised.title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpToDateBuild_DoesNotRaiseANotification()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleasesJson(("v0.1.27.5-pre", Prerelease: true)));
        var sink = new RecordingNotificationSink();
        var service = BuildService(handler, branch: "main", sink: sink);

        var status = await service.GetStatusAsync();

        Assert.False(status.UpdateAvailable);
        Assert.Empty(sink.Raised);
    }

    [Fact]
    public async Task RateLimited_KeepsTheCachedAnswerInsteadOfClearingIt()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleasesJson(("v0.1.27.6-pre", Prerelease: true)));
        var service = BuildService(handler, branch: "main", minimumIntervalMinutes: 0);

        var first = await service.GetStatusAsync();
        Assert.True(first.UpdateAvailable);

        // A later check fails; the already-known update must not vanish from the sidebar.
        handler.Response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(string.Empty)
        };
        handler.ThrowOnSend = false;

        var second = await service.GetStatusAsync();

        Assert.True(second.UpdateAvailable);
        Assert.Equal("v0.1.27.6-pre", second.LatestVersion);
    }

    private static UpdateCheckService BuildService(
        StubHandler handler,
        string branch,
        RecordingNotificationSink? sink = null,
        int minimumIntervalMinutes = 15)
    {
        var options = Options.Create(new AppVersionOptions
        {
            Owner = "Lauqnan14",
            Repository = "DeezSpoTag",
            Branch = branch,
            MinimumCheckIntervalMinutes = minimumIntervalMinutes
        });

        var versionInfo = new AppVersionInfo(
            options,
            NullLogger<AppVersionInfo>.Instance);
        var client = new GitHubReleaseClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            NullLogger<GitHubReleaseClient>.Instance);

        return new UpdateCheckService(
            client,
            versionInfo,
            options,
            sink ?? new RecordingNotificationSink(),
            NullLogger<UpdateCheckService>.Instance);
    }

    private static string ReleasesJson(params (string Tag, bool Prerelease)[] releases)
    {
        var entries = releases.Select(release => $$"""
            {
              "tag_name": "{{release.Tag}}",
              "name": "{{release.Tag}}",
              "html_url": "https://github.com/Lauqnan14/DeezSpoTag/releases/tag/{{release.Tag}}",
              "prerelease": {{(release.Prerelease ? "true" : "false")}},
              "draft": false,
              "published_at": "2026-09-27T14:19:06Z"
            }
            """);
        return "[" + string.Join(",", entries) + "]";
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            Response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        public HttpResponseMessage Response { get; set; }

        public bool ThrowOnSend { get; set; }

        public int CallCount { get; private set; }

        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri?.ToString();
            if (ThrowOnSend)
            {
                throw new HttpRequestException("network down");
            }

            if (Response.RequestMessage is null)
            {
                Response.RequestMessage = request;
            }

            return Task.FromResult(Response);
        }
    }

    private sealed class RecordingNotificationSink : INotificationSink
    {
        public List<(string kind, string title)> Raised { get; } = new();

        public void Raise(
            string kind,
            string title,
            string body,
            string severity = "Info",
            string? dedupeKey = null,
            string? entityType = null,
            string? entityId = null,
            string? link = null)
            => Raised.Add((kind, title));

        public void Resolve(string dedupeKey, bool manuallyResolved, string? recoveryTitle = null, string? recoveryBody = null)
        {
        }
    }
}
