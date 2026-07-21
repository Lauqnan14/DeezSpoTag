using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MediaServerLibrarySettingsBehaviorTests
{
    [Fact]
    public void PinUnlockService_UsesServerSideUnlockStatePerUser()
    {
        var service = new MediaServerLibraryPinUnlockService();
        var user = CreateUser("user-a");
        var otherUser = CreateUser("user-b");

        Assert.False(service.IsUnlocked(user));

        service.Unlock(user);

        Assert.True(service.IsUnlocked(user));
        Assert.False(service.IsUnlocked(otherUser));

        service.Lock(user);

        Assert.False(service.IsUnlocked(user));
    }

    [Fact]
    public void SettingsView_DoesNotUseLegacyBrowserPinAsSourceOfTruth()
    {
        var source = ReadSource("DeezSpoTag.Web", "Views", "Settings", "Index.cshtml");

        Assert.Contains("Checking PIN state", source, StringComparison.Ordinal);
        Assert.Contains("PIN state unavailable", source, StringComparison.Ordinal);
        Assert.Contains("payload?.configured === true", source, StringComparison.Ordinal);
        Assert.Contains("payload?.unlocked === true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SERVER_LIBRARY_LEGACY_PIN_STORAGE_KEY", source, StringComparison.Ordinal);
        Assert.DoesNotContain("deezspotag-server-library-pin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Falling back to local browser PIN state", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_SeparatesIncludeAndHiddenLibraryState()
    {
        var source = ReadSource("DeezSpoTag.Web", "Views", "Settings", "Index.cshtml");

        Assert.Contains("sortedLibraries.filter(library => library?.ignored !== true)", source, StringComparison.Ordinal);
        Assert.Contains("const hidden = library?.ignored === true;", source, StringComparison.Ordinal);
        Assert.Contains("library.ignored = hideInput?.checked === true;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("library?.ignored === true || library?.enabled === false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored: hidden || !include", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_UsesPostForServerLibraryRefreshAndProtectsAutosaveChanges()
    {
        var source = ReadSource("DeezSpoTag.Web", "Views", "Settings", "Index.cshtml");

        Assert.Contains("fetch('/api/media-server/soundtracks/sync'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("configuration?refresh=true", source, StringComparison.Ordinal);
        Assert.Contains("mediaServerLibraryAutoSavePending = true;", source, StringComparison.Ordinal);
        Assert.Contains("while (mediaServerLibraryAutoSavePending)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaServerLibraryApi_EnforcesHiddenLibraryUnlockServerSide()
    {
        var controllerSource = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "MediaServerSoundtracksApiController.cs");
        var pinControllerSource = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "MediaServerSoundtracksPinApiController.cs");
        var serviceSource = ReadSource("DeezSpoTag.Web", "Services", "MediaServerSoundtrackService.cs");

        Assert.Contains("includeHiddenLibraries: _pinUnlockService.IsUnlocked(User)", controllerSource, StringComparison.Ordinal);
        Assert.Contains("RefreshDiscoveredLibrariesAsync(\n            includeHiddenLibraries: _pinUnlockService.IsUnlocked(User)", controllerSource, StringComparison.Ordinal);
        Assert.Contains("refreshDiscovery: false", controllerSource, StringComparison.Ordinal);
        Assert.Contains("HasHiddenLibraryMutation(request)", controllerSource, StringComparison.Ordinal);
        Assert.Contains("StatusCodes.Status403Forbidden", controllerSource, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"lock\")]", pinControllerSource, StringComparison.Ordinal);
        Assert.Contains("_unlockService.Unlock(User)", pinControllerSource, StringComparison.Ordinal);
        Assert.Contains("_unlockService.Lock(User)", pinControllerSource, StringComparison.Ordinal);
        Assert.Contains("HiddenLibraryCount", serviceSource, StringComparison.Ordinal);
        Assert.Contains("allLibraries.Where(static library => !library.Ignored)", serviceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaManagementSoundtracks_PreloadWithoutTabActivationRefresh()
    {
        var librarySource = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "library.js");
        var bootstrapSource = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "library-bootstrap.js");
        var soundtrackSource = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "library-soundtracks.js");

        Assert.Contains("if (targets.shouldLoadSoundtracks)", librarySource, StringComparison.Ordinal);
        Assert.Contains("tasks.push(initializeSoundtracksTab());", librarySource, StringComparison.Ordinal);
        Assert.DoesNotContain("bindDeferredSoundtrackInitialization", librarySource, StringComparison.Ordinal);
        Assert.DoesNotContain("bindDeferredSoundtrackInitialization", bootstrapSource, StringComparison.Ordinal);
        Assert.Contains("if (!elements.grid || soundtrackState.initialized)", soundtrackSource, StringComparison.Ordinal);
        Assert.DoesNotContain("renderSoundtrackLazyLoadProgress", soundtrackSource, StringComparison.Ordinal);
        Assert.DoesNotContain("onProgress:", soundtrackSource, StringComparison.Ordinal);
        Assert.Contains("loading=\"lazy\" decoding=\"async\"", soundtrackSource, StringComparison.Ordinal);
        Assert.Contains("id=\"soundtrackSearchInput\"", ReadSource("DeezSpoTag.Web", "Views", "MediaManagement", "Index.cshtml"), StringComparison.Ordinal);
        Assert.Contains("filterSoundtrackRowsBySearch", soundtrackSource, StringComparison.Ordinal);
        Assert.Contains("soundtrackState.itemsByCategory", soundtrackSource, StringComparison.Ordinal);
        Assert.Contains("renderCurrentSoundtrackSearchResults();", soundtrackSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SoundtrackAlphabetNavigation_AllowsWheelAndTouchScrollingWhenVisible()
    {
        var libraryStyles = ReadSource("DeezSpoTag.Web", "wwwroot", "css", "library.css");

        Assert.Contains(".alpha-jump-nav.is-visible", libraryStyles, StringComparison.Ordinal);
        Assert.Contains("pointer-events: auto;", libraryStyles, StringComparison.Ordinal);
        Assert.Contains("touch-action: pan-y;", libraryStyles, StringComparison.Ordinal);
        Assert.Contains("overscroll-behavior-y: contain;", libraryStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void SoundtrackPosters_RetryTransientProxyFailuresWithoutGridReload()
    {
        var soundtrackSource = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "library-soundtracks.js");

        Assert.Contains("handleSoundtrackImageError", soundtrackSource, StringComparison.Ordinal);
        Assert.Contains("elements.grid.addEventListener('error', handleSoundtrackImageError, true)", soundtrackSource, StringComparison.Ordinal);
        Assert.Contains("retryUrl.searchParams.set('_retry'", soundtrackSource, StringComparison.Ordinal);
        Assert.Contains("image.replaceWith(placeholder)", soundtrackSource, StringComparison.Ordinal);
        Assert.DoesNotContain("loadSoundtrackItems();\n        }, attempt", soundtrackSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SoundtrackMonitor_UsesWeeklyReconciliationAndResolvesOnlyNewRecentItems()
    {
        var monitorSource = ReadSource("DeezSpoTag.Web", "Services", "MediaServerSoundtrackMonitorService.cs");
        var serviceSource = ReadSource("DeezSpoTag.Web", "Services", "MediaServerSoundtrackService.cs");

        Assert.Contains("WeeklyRefreshInterval = TimeSpan.FromDays(7)", monitorSource, StringComparison.Ordinal);
        Assert.Contains("NewItemProbeInterval = TimeSpan.FromMinutes(15)", monitorSource, StringComparison.Ordinal);
        Assert.Contains("RunWeeklyBackgroundSyncAsync", monitorSource, StringComparison.Ordinal);
        Assert.Contains("DetectAndResolveNewItemsAsync", monitorSource, StringComparison.Ordinal);
        Assert.Contains("SyncPersistentMediaCacheAsync(fullRefresh: true", serviceSource, StringComparison.Ordinal);
        Assert.Contains("Where(item => !existingByItemId.ContainsKey", serviceSource, StringComparison.Ordinal);
        Assert.Contains("BuildSoundtrackItemDto(item, persistedRow: null)", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("MonthlySyncInterval", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncRecentlyAddedItemsForLibraryAsync", serviceSource, StringComparison.Ordinal);
    }

    private static ClaimsPrincipal CreateUser(string id)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id),
            new(ClaimTypes.Name, id)
        }, "test"));
    }

    private static string ReadSource(params string[] relativePath)
    {
        var root = ResolveRepoRoot();
        var path = Path.Join(new[] { root }.Concat(relativePath).ToArray());
        Assert.True(File.Exists(path), $"Missing source file: {path}");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not resolve repository root.");
    }
}
