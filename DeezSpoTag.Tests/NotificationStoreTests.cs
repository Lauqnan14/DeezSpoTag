using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class NotificationStoreTests : IDisposable
{
    private readonly string _root;
    private readonly NotificationStore _store;

    public NotificationStoreTests()
    {
        _root = Path.Join(Path.GetTempPath(), "deezspotag-notifications-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _store = new NotificationStore(new StubEnvironment(_root), NullLogger<NotificationStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
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

    private static NotificationRequest Request(string dedupeKey, string title = "Provider is rate limited")
        => new(
            NotificationKinds.ProviderUnhealthy,
            title,
            "zarz is cooling down.",
            NotificationSeverity.Warning,
            dedupeKey);

    [Fact]
    public async Task AddOrCoalesce_CollapsesRepeatsOfTheSameUnreadEvent()
    {
        await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);
        await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);
        var third = await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);

        var entries = await _store.GetAsync();
        Assert.Single(entries);
        Assert.Equal(3, third.Entry.OccurrenceCount);
        Assert.Equal(1, await _store.GetUnreadCountAsync());
    }

    [Fact]
    public async Task AddOrCoalesce_StartsANewEntry_AfterTheEarlierOneWasRead()
    {
        var first = await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);
        await _store.MarkReadAsync([first.Entry.Id]);

        await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);

        var entries = await _store.GetAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(1, await _store.GetUnreadCountAsync());
        Assert.Single(entries, entry => entry.IsOpen);
    }

    [Fact]
    public async Task DistinctDedupeKeys_AreNotCollapsed()
    {
        await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);
        await _store.AddOrCoalesceAsync(Request("provider_unhealthy:other"), 30);

        Assert.Equal(2, (await _store.GetAsync()).Count);
    }

    [Fact]
    public async Task MarkAllRead_ClearsTheUnreadCount()
    {
        await _store.AddOrCoalesceAsync(Request("a"), 30);
        await _store.AddOrCoalesceAsync(Request("b"), 30);

        Assert.Equal(2, await _store.MarkAllReadAsync());
        Assert.Equal(0, await _store.GetUnreadCountAsync());
    }

    [Fact]
    public async Task Clear_RemovesReadAndUnreadNotifications()
    {
        var read = await _store.AddOrCoalesceAsync(Request("read"), 30);
        await _store.AddOrCoalesceAsync(Request("unread"), 30);
        await _store.MarkReadAsync([read.Entry.Id]);

        Assert.Equal(2, await _store.ClearAsync());

        Assert.Empty(await _store.GetAsync());
        Assert.Equal(0, await _store.GetUnreadCountAsync());
    }

    [Fact]
    public async Task OnlyTheFirstRaiseOpensAnIncident()
    {
        var first = await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);
        var second = await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);

        Assert.True(first.IsNewIncident);
        Assert.False(second.IsNewIncident);
    }

    [Fact]
    public async Task RepeatsAreStillSuppressedAfterTheAlertWasRead()
    {
        var first = await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);
        await _store.MarkReadAsync([first.Entry.Id]);

        var afterRead = await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);

        Assert.True(afterRead.IsNewIncident);
    }

    [Fact]
    public async Task ResolvingClosesTheIncidentSoALaterFailureOpensANewOne()
    {
        await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);
        var resolved = await _store.ResolveIncidentAsync("provider_unhealthy:zarz", manuallyResolved: false);

        Assert.True(resolved.HadOpenIncident);

        var reopened = await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);
        Assert.True(reopened.IsNewIncident);
    }

    [Fact]
    public async Task ResolvingWithNoOpenIncidentReportsNothingToAnnounce()
    {
        var resolved = await _store.ResolveIncidentAsync("provider_unhealthy:never-failed", manuallyResolved: false);

        Assert.False(resolved.HadOpenIncident);
    }

    [Fact]
    public async Task ManualResolutionIsRecordedOnTheIncident()
    {
        await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);
        await _store.ResolveIncidentAsync("provider_unhealthy:zarz", manuallyResolved: true);

        var entry = Assert.Single(await _store.GetAsync());
        Assert.True(entry.ManuallyResolved);
        Assert.False(entry.IsOpen);
    }

    [Fact]
    public async Task ReadingClosesTheIncidentWithoutClaimingTheUserFixedIt()
    {
        var first = await _store.AddOrCoalesceAsync(Request("provider_unhealthy:zarz"), 30);
        await _store.MarkReadAsync([first.Entry.Id]);

        var entry = Assert.Single(await _store.GetAsync());
        Assert.False(entry.IsOpen);
        Assert.False(entry.ManuallyResolved);
    }

    [Fact]
    public void Prune_KeepsUnreadForever_AndDropsReadPastRetention()
    {
        var stale = DateTimeOffset.UtcNow.AddDays(-90);
        var entries = new List<NotificationEntry>
        {
            new() { Id = "unread-old", Kind = NotificationKinds.DownloadFailed, DedupeKey = "1", Title = "t", CreatedUtc = stale, LastSeenUtc = stale },
            new() { Id = "read-old", Kind = NotificationKinds.DownloadFailed, DedupeKey = "2", Title = "t", CreatedUtc = stale, LastSeenUtc = stale, ReadUtc = stale },
            new() { Id = "read-recent", Kind = NotificationKinds.DownloadFailed, DedupeKey = "3", Title = "t", ReadUtc = DateTimeOffset.UtcNow }
        };

        var kept = NotificationStore.Prune(entries, 30).Select(entry => entry.Id).ToList();

        Assert.Contains("unread-old", kept);
        Assert.Contains("read-recent", kept);
        Assert.DoesNotContain("read-old", kept);
    }

    [Fact]
    public async Task Preferences_DefaultInAppOnAndWebhookOff_ForEveryKind()
    {
        var preferences = await _store.LoadPreferencesAsync();

        Assert.Equal(NotificationKinds.All.Count, preferences.Events.Count);
        Assert.All(NotificationKinds.All, kind =>
        {
            Assert.True(preferences.Resolve(kind).InApp, $"{kind} should default to in-app on.");
            Assert.False(preferences.Resolve(kind).Webhook, $"{kind} should default to webhook off.");
        });
    }

    [Fact]
    public async Task Preferences_SeedNewKindsOnLoad_WithoutDiscardingExistingChoices()
    {
        var saved = await _store.LoadPreferencesAsync();
        saved.Events[NotificationKinds.DownloadFailed].Webhook = true;
        saved.Events.Remove(NotificationKinds.RunCompleted);
        await _store.SavePreferencesAsync(saved);

        var reloaded = await _store.LoadPreferencesAsync();

        Assert.True(reloaded.Resolve(NotificationKinds.DownloadFailed).Webhook);
        Assert.True(reloaded.Events.ContainsKey(NotificationKinds.RunCompleted));
        Assert.False(reloaded.Resolve(NotificationKinds.RunCompleted).Webhook);
    }
}
