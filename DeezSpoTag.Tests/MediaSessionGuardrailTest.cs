using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guards the OS-level now playing surface: the Android media notification, the lock
/// screen transport controls, and the separate notification that carries the Download
/// action the closed Media Session action set cannot render.
/// </summary>
public sealed class MediaSessionGuardrailTest
{
    [Fact]
    public void ServiceWorker_StaysNotificationOnlyAndNeverCaches()
    {
        var sw = ReadWebRoot("sw.js");

        // The caching worker was decommissioned in commit 54224db63 because it served
        // stale API JSON. A fetch handler here would silently reintroduce that class of
        // bug, so the worker must never intercept a request or touch Cache Storage.
        Assert.DoesNotContain("addEventListener(\"fetch\"", sw, StringComparison.Ordinal);
        Assert.DoesNotContain("addEventListener('fetch'", sw, StringComparison.Ordinal);
        Assert.DoesNotContain("event.respondWith", sw, StringComparison.Ordinal);
        Assert.DoesNotContain("caches.open", sw, StringComparison.Ordinal);
        Assert.DoesNotContain("cache.put", sw, StringComparison.Ordinal);
        Assert.DoesNotContain("cache.add", sw, StringComparison.Ordinal);

        // It must also not unregister itself, which is what the old stub did.
        Assert.DoesNotContain("self.unregister", sw, StringComparison.Ordinal);
        Assert.DoesNotContain("registration.unregister", sw, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceWorker_PostsTheDownloadNotificationAndForwardsTheAction()
    {
        var sw = ReadWebRoot("sw.js");

        Assert.Contains("addEventListener(\"message\"", sw, StringComparison.Ordinal);
        Assert.Contains("showNotification", sw, StringComparison.Ordinal);
        Assert.Contains("addEventListener(\"notificationclick\"", sw, StringComparison.Ordinal);
        Assert.Contains("deezspotag:download-current", sw, StringComparison.Ordinal);
        Assert.Contains("deezspotag:show-download-notification", sw, StringComparison.Ordinal);
        Assert.Contains("action: \"download\"", sw, StringComparison.Ordinal);

        // A stable tag keeps one notification per playing track instead of stacking.
        Assert.Contains("tag:", sw, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_StopsDestroyingServiceWorkersOnEveryPageLoad()
    {
        var layout = ReadSource("Views", "Shared", "_Layout.cshtml");

        // Unregistering every worker on each page load would kill the notification
        // worker before it could ever post a Download action.
        Assert.DoesNotContain("getRegistrations", layout, StringComparison.Ordinal);
        Assert.DoesNotContain(".unregister()", layout, StringComparison.Ordinal);

        // Wiping every cache is replaced by deleting only the one the old worker made.
        Assert.DoesNotContain("cacheKeys.forEach", layout, StringComparison.Ordinal);
        Assert.Contains("deezspotag-pwa-v7", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceWorker_RegistrationBypassesTheScriptCache()
    {
        var footer = ReadSource("Views", "Shared", "_CommonFooterScripts.cshtml");

        // The app serves every response no-store; updateViaCache:'none' additionally
        // bypasses the browser's service-worker script cache so updates land at once.
        Assert.Contains("navigator.serviceWorker.register('/sw.js'", footer, StringComparison.Ordinal);
        Assert.Contains("updateViaCache: 'none'", footer, StringComparison.Ordinal);
        Assert.Contains("~/js/deezer-media-session.js", footer, StringComparison.Ordinal);
    }

    [Fact]
    public void Playback_ExposesPauseResumeAndQueueNavigation()
    {
        var playback = ReadSource("wwwroot", "js", "deezer-unified-playback.js");

        Assert.Contains("normalized === 'paused'", playback, StringComparison.Ordinal);
        Assert.Contains("async function pause()", playback, StringComparison.Ordinal);
        Assert.Contains("async function resume()", playback, StringComparison.Ordinal);
        Assert.Contains("async function playAdjacentRequest(direction)", playback, StringComparison.Ordinal);
        Assert.Contains("function getCurrentRequest()", playback, StringComparison.Ordinal);
        Assert.Contains("getAudio: () => audio", playback, StringComparison.Ordinal);

        // A true pause must keep the loaded source; clearing it is what used to make a
        // media-session pause destroy the session instead of pausing it.
        var pauseBody = ExtractFunction(playback, "async function pause()");
        Assert.DoesNotContain("clearAudioSource", pauseBody, StringComparison.Ordinal);
        Assert.Contains("audio.pause()", pauseBody, StringComparison.Ordinal);
        Assert.Contains("'paused'", pauseBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wwwroot/js/home-index.js")]
    [InlineData("wwwroot/js/library.js")]
    public void PlaybackRequests_ExposeAPreviousTrackFactory(string relativePath)
    {
        var source = ReadSource(relativePath.Split('/'));

        Assert.Contains("getPreviousRequest:", source, StringComparison.Ordinal);
        Assert.Contains("getNextRequest:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TracklistRequests_ExposeAPreviousTrackFactory()
    {
        var tracklist = ReadSource("Views", "Tracklist", "Index.cshtml");

        Assert.Contains("getPreviousRequest:", tracklist, StringComparison.Ordinal);
        Assert.Contains("getNextRequest:", tracklist, StringComparison.Ordinal);
        // The backward walk must step away from the current row, not toward it.
        Assert.Contains("for (let i = startIndex - 1; i >= 0; i -= 1)", tracklist, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaSession_RegistersEveryTransportActionDefensively()
    {
        var mediaSession = ReadSource("wwwroot", "js", "deezer-media-session.js");

        foreach (var action in new[]
                 {
                     "play", "pause", "stop", "nexttrack", "previoustrack",
                     "seekbackward", "seekforward", "seekto"
                 })
        {
            Assert.Contains($"setActionHandler('{action}'", mediaSession, StringComparison.Ordinal);
        }

        // Support varies per action, so every registration must be probed.
        Assert.Contains("try {\n            mediaSession.setActionHandler(action, handler);", mediaSession, StringComparison.Ordinal);

        Assert.Contains("'mediaSession' in navigator", ReadSource("Views", "Shared", "_CommonFooterScripts.cshtml"), StringComparison.Ordinal);
    }

    [Fact]
    public void MediaSession_PublishesMetadataPositionAndState()
    {
        var mediaSession = ReadSource("wwwroot", "js", "deezer-media-session.js");

        Assert.Contains("new global.MediaMetadata(", mediaSession, StringComparison.Ordinal);
        Assert.Contains("mediaSession.playbackState", mediaSession, StringComparison.Ordinal);
        Assert.Contains("setPositionState", mediaSession, StringComparison.Ordinal);

        // The scrubber needs a real range, so an unknown stream duration must be skipped.
        Assert.Contains("if (!Number.isFinite(duration) || duration <= 0) {", mediaSession, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaSession_ArtworkFallsBackToTheAppIcon()
    {
        var mediaSession = ReadSource("wwwroot", "js", "deezer-media-session.js");

        Assert.Contains("const FALLBACK_ARTWORK = '/images/pwa/icon-512.png';", mediaSession, StringComparison.Ordinal);
        Assert.Contains("return FALLBACK_ARTWORK;", mediaSession, StringComparison.Ordinal);
        // Row cover first, then the Deezer cover for a matched id, then the icon.
        Assert.Contains("e-cdns-images.dzcdn.net/images/cover/", mediaSession, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaSession_DownloadActionReusesTheGlobalDownloadClient()
    {
        var mediaSession = ReadSource("wwwroot", "js", "deezer-media-session.js");

        // The tracklist's download helpers are inline in that view, so the notification
        // must go through the page-independent client instead.
        Assert.Contains("global.DeezSpoTagDownload", mediaSession, StringComparison.Ordinal);
        Assert.Contains("downloader.downloadSelectedTracks", mediaSession, StringComparison.Ordinal);
        Assert.Contains("deezspotag:download-current", mediaSession, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaSession_AsksForNotificationPermissionOnFirstPlayNotOnLoad()
    {
        var footer = ReadSource("Views", "Shared", "_CommonFooterScripts.cshtml");
        var mediaSession = ReadSource("wwwroot", "js", "deezer-media-session.js");

        Assert.Contains("__deezspotRequestNotificationPermissionOnPlay", footer, StringComparison.Ordinal);
        Assert.DoesNotContain("Notification.requestPermission()", footer, StringComparison.Ordinal);
        Assert.Contains("Notification.requestPermission", mediaSession, StringComparison.Ordinal);
        // The request must hang off a real play event, not page load.
        Assert.Contains("audio.addEventListener('play'", mediaSession, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackState_TreatsPausedAsAnActiveSession()
    {
        var state = ReadSource("wwwroot", "js", "deezer-playback-state.js");
        var library = ReadSource("wwwroot", "js", "library.js");
        var tracklist = ReadSource("Views", "Tracklist", "Index.cshtml");

        // A paused track must keep its row highlighted, otherwise the control reads as
        // "play" for a track that is still loaded and resumable.
        Assert.Contains("normalized === 'paused'", state, StringComparison.Ordinal);
        Assert.Contains("const isActive = isRequested || isPlaying || normalized === 'paused';", state, StringComparison.Ordinal);
        Assert.Contains("state === 'paused'", library, StringComparison.Ordinal);
        Assert.Contains("normalized !== 'paused'", tracklist, StringComparison.Ordinal);
    }

    private static string ReadWebRoot(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "DeezSpoTag.Web", "wwwroot", fileName));

    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine(
            new[] { RepositoryRoot(), "DeezSpoTag.Web" }.Concat(segments).ToArray()));

    private static string ExtractFunction(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Function not found: {signature}");

        var bodyStart = source.IndexOf('{', start);
        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }

            if (source[index] != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Unbalanced braces in: {signature}");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeezSpoTag.Web", "DeezSpoTag.Web.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
