using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DeezSpoTag.Web.Services.Notifications;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class NotificationPreferenceWiringTest
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void SettingsEventRows_CoverEveryKnownNotificationKind()
    {
        var settings = ReadSource("DeezSpoTag.Web/Views/Settings/Index.cshtml");
        var labelsBlock = ExtractBetween(settings, "const NOTIFICATION_EVENT_LABELS = {", "};");

        foreach (var kind in NotificationKinds.All)
        {
            Assert.Contains($"{kind}:", labelsBlock, StringComparison.Ordinal);
        }

        var labeledKinds = Regex.Matches(labelsBlock, @"^\s*([a-z_]+):", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.Equal(NotificationKinds.All.Count, labeledKinds.Length);
        Assert.All(labeledKinds, kind => Assert.True(NotificationKinds.IsKnown(kind), kind));
    }

    [Fact]
    public void SettingsNotificationSaves_SendCsrfAndPersistAppleDownloadMode()
    {
        var settings = ReadSource("DeezSpoTag.Web/Views/Settings/Index.cshtml");

        Assert.Contains("headers['X-CSRF-TOKEN'] = csrf;", settings, StringComparison.Ordinal);
        Assert.Contains("fetch('/api/notifications/preferences'", settings, StringComparison.Ordinal);
        Assert.Contains("fetch('/api/notifications/webhook-test'", settings, StringComparison.Ordinal);
        Assert.Contains("document.getElementById('appleDownloadNotificationMode')?.addEventListener('change', persistAppleNotificationPreference);", settings, StringComparison.Ordinal);
        Assert.Contains("UserPrefs.set('appleDownloadNotificationMode', mode);", settings, StringComparison.Ordinal);
        Assert.Contains("new StorageEvent('storage'", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownKinds_AreRaisedFromRealProductPaths()
    {
        var autoTagHistory = ReadSource("DeezSpoTag.Web/Services/AutoTagService.RunHistory.cs");
        var autoTagPipeline = ReadSource("DeezSpoTag.Web/Services/AutoTagService.StagePipeline.cs");
        var autoTagRecovery = ReadSource("DeezSpoTag.Web/Services/AutoTagService.JobRecovery.cs");
        var autoTagResume = ReadSource("DeezSpoTag.Web/Services/AutoTagService.Resume.cs");
        var watchlist = ReadSource("DeezSpoTag.Web/Services/WatchlistEngine.cs");
        var watchlistReadiness = ReadSource("DeezSpoTag.Web/Services/WatchlistPublicApiReadinessService.cs");
        var fallback = ReadSource("DeezSpoTag.Services/Download/Fallback/EngineFallbackCoordinator.cs");
        var orchestration = ReadSource("DeezSpoTag.Web/Services/DownloadOrchestrationService.cs");
        var tidal = ReadSource("DeezSpoTag.Web/Services/TidalPublicProviderRegistry.cs");
        var qobuz = ReadSource("DeezSpoTag.Web/Services/QobuzPublicProviderRegistry.cs");
        var amazon = ReadSource("DeezSpoTag.Web/Services/AmazonPublicProviderRegistry.cs");

        Assert.Contains("\"verification_required\"", watchlistReadiness, StringComparison.Ordinal);
        Assert.Contains("\"artist_new_release\"", watchlist, StringComparison.Ordinal);
        Assert.Contains("\"playlist_updated\"", watchlist, StringComparison.Ordinal);
        Assert.Contains("\"download_failed\"", fallback, StringComparison.Ordinal);
        Assert.Contains("\"download_blocked\"", orchestration, StringComparison.Ordinal);
        Assert.Contains("\"provider_unhealthy\"", tidal, StringComparison.Ordinal);
        Assert.Contains("\"provider_unhealthy\"", qobuz, StringComparison.Ordinal);
        Assert.Contains("\"provider_unhealthy\"", amazon, StringComparison.Ordinal);
        Assert.Contains("NotifyProviderRecovered", tidal, StringComparison.Ordinal);
        Assert.Contains("NotifyProviderRecovered", qobuz, StringComparison.Ordinal);
        Assert.Contains("NotifyProviderRecovered", amazon, StringComparison.Ordinal);
        Assert.Contains("\"run_paused\"", autoTagHistory, StringComparison.Ordinal);
        Assert.Contains("NotifyRunStopped(job, job.Status, job.Error ?? string.Empty);", autoTagPipeline, StringComparison.Ordinal);
        Assert.Contains("NotifyRunStopped(job, job.Status, job.Error);", autoTagRecovery, StringComparison.Ordinal);
        Assert.Contains("\"run_resumed\"", autoTagHistory, StringComparison.Ordinal);
        Assert.Contains("NotifyRunResumed(job, resumed.Id);", autoTagResume, StringComparison.Ordinal);
        Assert.Contains("\"run_completed\"", autoTagHistory, StringComparison.Ordinal);
        Assert.Contains("NotifyRunFinished(job);", autoTagPipeline, StringComparison.Ordinal);
        Assert.Contains("NotifyRunFinished(job);", autoTagRecovery, StringComparison.Ordinal);
        Assert.Contains("\"run_resumed\"", orchestration, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadClient_HonorsAppleDownloadNotificationMode()
    {
        var client = ReadSource("DeezSpoTag.Web/wwwroot/js/download-client.js");

        Assert.Contains("notifyAppleAware(kind, message, type = 'info', context = {})", client, StringComparison.Ordinal);
        Assert.Contains("this.refreshAppleNotificationMode();", client, StringComparison.Ordinal);
        Assert.Contains("createQueueNotifier(options = {}, url = '')", client, StringComparison.Ordinal);
        Assert.Contains("this.notifyAppleAware('started', resolvedMessage, 'info', { linkType });", client, StringComparison.Ordinal);
        Assert.Contains("if (appleMode !== 'off')", client, StringComparison.Ordinal);
        Assert.Contains("this.getAppleNotificationMode() === 'detailed'", client, StringComparison.Ordinal);
        Assert.Contains("if (mode === 'off')", client, StringComparison.Ordinal);
        Assert.Contains("if (mode === 'merged')", client, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadBlocked_IsAKnownKind()
    {
        Assert.Contains(NotificationKinds.DownloadBlocked, NotificationKinds.All);
        Assert.True(NotificationKinds.IsKnown("download_blocked"));
        Assert.True(NotificationKinds.IsKnown("provider_recovered"));
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web", "Views", "Settings", "Index.cshtml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve repository root.");
    }
}
