using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Mirror mode replaces a target playlist's whole track list, so a read that came back
/// truncated would delete real tracks from the user's server. These cover the rails that
/// stop that, which mirror previously had none of.
/// </summary>
public sealed class PlaylistRemovalSafetyTest
{
    [Theory]
    [InlineData(100, 100, false)]   // healthy read
    [InlineData(100, 50, false)]    // exactly half: still trusted
    [InlineData(100, 39, true)]     // under 40%: treated as a broken read
    [InlineData(100, 0, true)]      // empty read must never drive a mirror
    public void CollapsedRead_IsTreatedAsBrokenNotAsDeletion(int baseline, int current, bool expected)
    {
        Assert.Equal(expected, PlaylistSyncService.IsReadIncompleteForRemoval(baseline, current, null, null));
    }

    [Fact]
    public void CollapseGuard_DoesNotFireWithoutABaseline()
    {
        // A playlist this app has never synced has no baseline, so there is no evidence the
        // read is wrong and removals stay permitted.
        Assert.False(PlaylistSyncService.IsReadIncompleteForRemoval(0, 0, null, null));
    }

    [Fact]
    public void ReplacedPhysicalPlaylist_IsTreatedAsIncomplete()
    {
        // The target playlist id changed under us. Diffing the new playlist against the old
        // baseline would delete everything, so removals are held.
        Assert.True(PlaylistSyncService.IsReadIncompleteForRemoval(50, 50, "old-id", "new-id"));
    }

    [Fact]
    public void SamePhysicalPlaylist_IsNotTreatedAsIncomplete()
    {
        Assert.False(PlaylistSyncService.IsReadIncompleteForRemoval(50, 50, "same-id", "same-id"));
        Assert.False(PlaylistSyncService.IsReadIncompleteForRemoval(50, 50, "same-id", null));
        Assert.False(PlaylistSyncService.IsReadIncompleteForRemoval(50, 50, null, "some-id"));
    }

    [Fact]
    public void Removals_AreCappedPerPass()
    {
        var removals = Enumerable.Range(0, 30).ToList();

        Assert.Equal(30, PlaylistSyncService.CapRemovals(removals, 50).Count);
        Assert.Equal(10, PlaylistSyncService.CapRemovals(removals, 10).Count);
        Assert.Equal(Enumerable.Range(0, 10), PlaylistSyncService.CapRemovals(removals, 10));
    }

    [Fact]
    public void Removals_AreAllHeldWhenTheCapIsZero()
    {
        var removals = Enumerable.Range(0, 30).ToList();

        Assert.Empty(PlaylistSyncService.CapRemovals(removals, 0));
    }

    /// <summary>
    /// Every mirror-capable writer has to consult the guard, not just the one that was fixed
    /// first. A guard on a single target would leave the other two able to wipe a playlist.
    /// </summary>
    [Fact]
    public void EveryMirrorWriter_ConsultsTheRemovalGuard()
    {
        var source = ReadServiceSource();

        // Every mirror writer must reach a removal guard. A writer is safe if it either writes
        // through the engine - which owns the collapse check - or still consults the guard
        // directly. A writer that did neither could mirror destructively on a broken read, and
        // that is the failure this guardrail exists to catch.
        foreach (var writer in new[]
        {
            "private async Task<PlaylistSyncResult> SyncToPlexAsync",
            "private async Task<PlaylistSyncResult> SyncToJellyfinAsync",
            "private async Task<PlaylistSyncResult> SyncToNavidromeAsync"
        })
        {
            var body = GetMethodBody(source, writer);
            var throughEngine = body.Contains("_playlistSyncEngine.SyncAsync", StringComparison.Ordinal);
            var directGuard = body.Contains("EvaluateRemovalSafetyAsync", StringComparison.Ordinal)
                              && body.Contains("!removalSafety.MayRemove", StringComparison.Ordinal);
            Assert.True(
                throughEngine || directGuard,
                $"{writer} writes destructively with no removal guard: it neither uses the engine nor consults EvaluateRemovalSafetyAsync.");
        }

        // The engine is the one place that decides removals are unsafe.
        var engine = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncEngine.cs");
        Assert.Contains("IsReadIncompleteForRemoval", engine, StringComparison.Ordinal);
        Assert.Contains("looked incomplete", engine, StringComparison.Ordinal);
    }

    /// <summary>
    /// A mirror the guard downgraded must say so. Reporting it as a plain append looks
    /// identical to a bug, and the user cannot tell their playlist stopped being replaced.
    /// </summary>
    [Fact]
    public void HeldRemovals_AreExplainedRatherThanSilentlyDowngraded()
    {
        var source = ReadServiceSource();

        // Expression-bodied, so there is no brace block to extract: assert on the declaration
        // and the few lines that follow it.
        var start = source.IndexOf("private static string? BuildRemovalHoldSuffix", StringComparison.Ordinal);
        Assert.True(start > 0, "Could not find BuildRemovalHoldSuffix");
        var build = source.Substring(start, 400);
        Assert.Contains("SyncModeMirror", build, StringComparison.Ordinal);
        Assert.Contains("!removalSafety.MayRemove", build, StringComparison.Ordinal);
        Assert.Contains("Removals were held", build, StringComparison.Ordinal);

        // The engine explains a held removal in the outcome message, and a writer that surfaces
        // the outcome to the user must pass that message through.
        var engine = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncEngine.cs");
        var engineSync = Head(engine, "public async Task<PlaylistSyncTargetOutcome> SyncAsync", 6000);
        Assert.Contains("Removals held", engineSync, StringComparison.Ordinal);
        Assert.Contains("looked incomplete", engineSync, StringComparison.Ordinal);
        Assert.Contains("BuildEngineHoldSuffix", source, StringComparison.Ordinal);
    }

    private static string Head(string source, string marker, int length = 400)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start > 0, $"Could not find marker: {marker}");
        return source.Substring(start, length);
    }

    [Fact]
    public void RemovalGuard_ReadsTheRecordedBaselineFromTheRepository()
    {
        var service = ReadServiceSource();
        var guard = GetMethodBody(service, "private async Task<RemovalSafety> EvaluateRemovalSafetyAsync");
        Assert.Contains("GetPlaylistWatchTargetCountAsync", guard, StringComparison.Ordinal);

        // An unreadable target is unknown, not empty. Treating a failed read as an empty
        // playlist is the exact failure mode this guard exists to prevent.
        Assert.Contains(
            "return -1;",
            GetMethodBody(service, "private async Task<int> CountTargetPlaylistEntriesAsync"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every surface that pushes a playlist to a server offers the same append/match choice.
    /// Meloday previously hard-coded a replace on all three targets, so a short track
    /// resolution silently truncated the playlist on the user's server.
    /// </summary>
    [Fact]
    public void MelodaySync_HonoursTheUsersAppendOrMatchChoice()
    {
        var service = ReadSource("DeezSpoTag.Web", "Services", "MelodayService.cs");
        var sync = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");
        var script = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "meloday.js");
        var view = ReadSource("DeezSpoTag.Web", "Views", "Activities", "Index.cshtml");

        // The setting exists and is normalised.
        Assert.Contains("public string TargetSyncMode", service, StringComparison.Ordinal);
        Assert.Contains("public static string NormalizeSyncMode", service, StringComparison.Ordinal);
        Assert.Contains("== Append ? Append : Match", service, StringComparison.Ordinal);

        // Meloday passes the user's choice into the generated sync.
        Assert.Contains("MelodayTargetServers.NormalizeSyncMode(context.SimilarContext.Options.TargetSyncMode)", service, StringComparison.Ordinal);

        // The generated writers must all honour it rather than hard-coding a replace.
        Assert.Contains("AppendMissingOnly = false", sync, StringComparison.Ordinal);
        foreach (var writer in new[]
        {
            "private async Task<GeneratedLocalPlaylistTargetResult> SyncGeneratedLocalPlaylistToPlexAsync",
            "private async Task<GeneratedLocalPlaylistTargetResult> SyncGeneratedLocalPlaylistToJellyfinAsync",
            "private async Task<GeneratedLocalPlaylistTargetResult> SyncGeneratedLocalPlaylistToNavidromeAsync"
        })
        {
            var body = GetMethodBody(sync, writer);
            Assert.Contains("request.AppendMissingOnly", body, StringComparison.Ordinal);
        }

        // And the user can actually choose it.
        Assert.Contains("data-meloday-sync-mode=\"match\"", view, StringComparison.Ordinal);
        Assert.Contains("data-meloday-sync-mode=\"append\"", view, StringComparison.Ordinal);
        Assert.Contains("targetSyncMode: melodayGetSyncMode()", script, StringComparison.Ordinal);
        Assert.Contains("melodaySetSyncMode", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sync target that is not offered anywhere is not a feature. YouTube Music has to
    /// appear in the configured-target list, in Meloday's target set and in the sync menus.
    /// </summary>
    [Fact]
    public void YouTubeMusic_IsOfferedAsASyncTarget()
    {
        var service = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");
        var meloday = ReadSource("DeezSpoTag.Web", "Services", "MelodayService.cs");
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "LibraryPlaylistWatchlistApiController.cs");
        var script = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js");

        Assert.Contains("private const string YouTubeMusicService = \"ytmusic\"", service, StringComparison.Ordinal);
        Assert.Contains("private async Task<PlaylistSyncResult> SyncToYouTubeMusicAsync", service, StringComparison.Ordinal);
        Assert.Contains("if (request.SyncToYouTubeMusic)", service, StringComparison.Ordinal);
        Assert.Contains("TryLoadConfiguredYouTubeMusicAsync", service, StringComparison.Ordinal);

        // Reachable from the UI: configured-target list, Meloday, and both label maps.
        // The two label maps used to be copies written out inside the controller; they are one
        // shared map now, so this checks the map itself and that both endpoints still route
        // through it. Asserting on the returned label also beats matching source text: it proves
        // the id really maps to the name rather than that a line once contained those words.
        var configured = GetMethodBody(service, "public async Task<IReadOnlyList<string>> GetConfiguredTargetServicesAsync");
        Assert.Contains("configured.Add(YouTubeMusicService)", configured, StringComparison.Ordinal);
        Assert.Equal("YouTube Music", PlaylistTargetPresentation.Label("ytmusic"));
        Assert.Equal(2, controller.Split("PlaylistTargetPresentation.Label(target)").Length - 1);
        Assert.Contains("Navidrome, YouTubeMusic }", meloday, StringComparison.Ordinal);
        Assert.Contains("ytmusic: \"YouTube Music\"", script, StringComparison.Ordinal);

        // The asset the menu points at must exist, or the badge renders broken.
        var icon = Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "wwwroot", "images", "icons", "youtube-music.png");
        Assert.True(File.Exists(icon), $"Missing YouTube Music icon: {icon}");
        Assert.Contains("youtube-music.png", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// A target playlist that cannot be read, or a partial resolution, must leave the target
    /// untouched. Treating either as an empty playlist is how real tracks get deleted.
    /// </summary>
    [Fact]
    public void YouTubeMusic_NeverWritesOnAnUnreadableOrPartialRead()
    {
        var service = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");
        var writer = GetMethodBody(service, "private async Task<PlaylistSyncResult> SyncToYouTubeMusicAsync");

        // A partial resolution still refuses, in the writer.
        Assert.Contains("if (matchSummary.TargetIds.Count < tracks.Count)", writer, StringComparison.Ordinal);
        Assert.Contains("nothing was written", writer, StringComparison.Ordinal);

        // The unreadable-target rule moved into the target when the writer was put behind the
        // shared engine. It is asserted where the code now lives, and the ordering that makes it
        // safe is still pinned: the read is checked before anything is deleted.
        var target = ReadSource("DeezSpoTag.Integrations", "YouTube", "YouTubeMusicPlaylistSyncTarget.cs");

        Assert.Contains("if (items is null)", target, StringComparison.Ordinal);
        Assert.Contains("TargetPlaylistItemsRead.Unreadable", target, StringComparison.Ordinal);
        Assert.Contains("DeletePlaylistItemAsync", target, StringComparison.Ordinal);

        var readAt = target.IndexOf("if (items is null)", StringComparison.Ordinal);
        var deleteAt = target.IndexOf("DeletePlaylistItemAsync", StringComparison.Ordinal);
        Assert.True(readAt > 0 && deleteAt > readAt, "Items must be read and validated before any removal.");

        // And the engine refuses an unreadable target without writing at all.
        var engine = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncEngine.cs");
        Assert.Contains("if (!read.Success)", engine, StringComparison.Ordinal);
        Assert.Contains("could not be read, so nothing was written", engine, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sidebar status icons and the login tabs are both driven by /api/platform-registry.
    /// A platform missing from that registry is invisible in the sidebar no matter what the
    /// Login page contains, so the registry is the thing that has to be complete.
    /// </summary>
    [Fact]
    public void YouTubeMusic_IsRegisteredForTheSidebarAndLoginTab()
    {
        var registry = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "PlatformRegistryApiController.cs");
        var view = ReadSource("DeezSpoTag.Web", "Views", "Login", "Index.cshtml");

        Assert.Contains("private const string YouTubeMusicPlatform = \"ytmusic\"", registry, StringComparison.Ordinal);
        Assert.Contains("[YouTubeMusicPlatform] = \"ytmusic-login\"", registry, StringComparison.Ordinal);
        Assert.Contains("[YouTubeMusicPlatform] = \"YouTube Music\"", registry, StringComparison.Ordinal);
        Assert.Contains("return \"/images/icons/youtube-music.png\";", registry, StringComparison.Ordinal);

        // Auth required, so the sidebar shows it with a padlock until it is connected.
        var authRequired = GetMethodBody(registry, "private static PlatformRegistryEntry CreateEntry");
        Assert.Contains("AuthRequiredPlatforms.Contains(platformId)", authRequired, StringComparison.Ordinal);
        var loginTabs = registry.Substring(registry.IndexOf("AuthRequiredPlatforms", StringComparison.Ordinal), 900);
        Assert.Contains("YouTubeMusicPlatform", loginTabs, StringComparison.Ordinal);

        // And the login tab it points at actually exists.
        Assert.Contains("data-login-target=\"ytmusic-login\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"ytmusic-login\"", view, StringComparison.Ordinal);
        Assert.Contains("ytmusicLoginForm", view, StringComparison.Ordinal);
        Assert.Contains("ytmusicDisconnectBtn", view, StringComparison.Ordinal);
    }

    /// <summary>
    /// The library playlist kebab was permanently hidden: the app reveals
    /// <c>watchlist-kebab-btn</c> only under specific parent classes, and the library card used
    /// a class that was not among them. This pins the parent to the app's existing reveal rule.
    /// </summary>
    [Fact]
    public void LibraryPlaylistKebab_IsCoveredByTheAppsHoverRevealRule()
    {
        var css = ReadSource("DeezSpoTag.Web", "wwwroot", "css", "library.css");

        Assert.Contains(".library-playlist-card:hover .watchlist-kebab-btn", css, StringComparison.Ordinal);
        Assert.Contains(".library-playlist-card:focus-within .watchlist-kebab-btn", css, StringComparison.Ordinal);

        // The button really is hidden by default, which is why the parent rule matters.
        var button = Head(css, ".watchlist-kebab-btn {", 600);
        Assert.Contains("visibility: hidden;", button, StringComparison.Ordinal);
        Assert.Contains("opacity: 0;", button, StringComparison.Ordinal);
    }


    /// <summary>
    /// Three defects reported after the first attempt: the menu rendered top-right instead of
    /// bottom-right, picking a target also navigated, and the option rows did not use the app's
    /// own dropdown styling. Each is pinned here so it cannot regress silently.
    /// </summary>
    [Fact]
    public void SyncMenu_FollowsTheAppDesignAndDoesNotNavigateOnSelect()
    {
        var css = ReadSource("DeezSpoTag.Web", "wwwroot", "css", "auto-playlists.css");
        var lib = ReadSource("DeezSpoTag.Web", "wwwroot", "css", "library.css");
        var script = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js");

        // Bottom right: position comes from the app's own class and must not be overridden.
        var appPosition = Head(lib, ".watchlist-action-menu--hover {", 260);
        Assert.Contains("bottom: 10px", appPosition, StringComparison.Ordinal);
        Assert.Contains("right: 10px", appPosition, StringComparison.Ordinal);

        // The hover hook may set visibility, never a position. Checked on the rule itself so an
        // unrelated "padding-top: 8px" elsewhere in the file cannot satisfy or break this.
        var hook = Head(css, ".library-playlist-card:hover .library-playlist-actions,", 220);
        Assert.Contains("pointer-events: auto;", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("top:", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("right:", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("bottom:", hook, StringComparison.Ordinal);

        // The card cannot clip the panel, so it must not clip at all. The app's own cards use
        // overflow: visible for exactly this reason.
        var cardRule = Head(css, ".library-playlist-card {", 400);
        Assert.Contains("overflow: visible", cardRule, StringComparison.Ordinal);
        Assert.Contains("position: relative", cardRule, StringComparison.Ordinal);

        // Anchored bottom-right, the panel must open upward or it hangs off the card. This is the
        // app's own --hover variant; the default one opens downward.
        //
        // This used to demand the class twice, on the assumption that a Meloday menu was built here
        // too. It is not: the Activities page ships no Meloday action menu, and neither meloday.js
        // nor this file builds one, so the second occurrence has never existed and the fixed count
        // of 2 was aspirational. Counting occurrences would also pass if a menu were built twice
        // from one call site.
        //
        // The rule worth keeping is per menu, not per occurrence: every action menu this file builds
        // has to carry the hook. That still fails if a second menu is added later without it.
        var menuAssignments = Regex.Matches(script, @"className\s*=\s*'[^']*watchlist-action-dropdown[^']*'")
            .Select(match => match.Value)
            .ToList();
        Assert.NotEmpty(menuAssignments);
        Assert.All(
            menuAssignments,
            assignment => Assert.Contains("--hover", assignment, StringComparison.Ordinal));

        // Measured in a real browser against the app's own artist menu: the panel must keep the
        // app's 180px width. An earlier 168px override of ours was the only styling difference
        // left once the app's classes were used, so the panel is left entirely unstyled here.
        //
        // Asserted as "declares no rule for the panel" rather than by hunting the comment that
        // used to say so, because that comment did not survive the stylesheet being reorganised.
        // Comments are stripped first so the file can still explain that it adds no overrides.
        var cssWithoutComments = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        Assert.DoesNotContain(".watchlist-action-dropdown", cssWithoutComments, StringComparison.Ordinal);
        Assert.DoesNotContain(".watchlist-action-menu", cssWithoutComments, StringComparison.Ordinal);

        // And the panel is genuinely styled, by the app's own rules rather than by nothing.
        Assert.Contains(".watchlist-action-dropdown {", lib, StringComparison.Ordinal);

        // Stacking. The page holds library and Meloday playlists in two containers and the
        // Melody one is a positioned stacking context, so a panel growing out of a library card
        // was painted behind it - covering its own rows and making Sync unclickable. Both
        // containers and the card holding the open panel must be liftable above their siblings.
        Assert.Contains("#libraryPlaylistsSections.menu-open", css, StringComparison.Ordinal);
        Assert.Contains("#autoToolsGrid.menu-open", css, StringComparison.Ordinal);
        Assert.Contains(".library-playlist-card.menu-open", css, StringComparison.Ordinal);
        Assert.Contains(".watchlist-playlist-card-v2.menu-open", css, StringComparison.Ordinal);
        Assert.Contains("const syncMenuStacking = ()", script, StringComparison.Ordinal);
        Assert.Contains("const setMenuOpen = (wrapper, dropdown, toggle, open)", script, StringComparison.Ordinal);

        // The lift is re-derived from the panels' real hidden state on every change. Toggling it
        // in place left a section raised after its panel closed, which put a neighbouring card's
        // art back over the Sync button.
        var stack = Head(script, "const syncMenuStacking = ()", 900);
        Assert.Contains("section.querySelector(\".watchlist-action-dropdown:not([hidden])\")", stack, StringComparison.Ordinal);
        Assert.Contains("card.querySelector(\".watchlist-action-dropdown:not([hidden])\")", stack, StringComparison.Ordinal);
        // Every path that opens or closes a panel must go through the helper, or the lift is
        // left behind: the toggle, the outside-click closer and the close after a sync each used
        // to write dropdown.hidden directly, which left the section raised after every successful
        // sync.
        //
        // This counted call sites and expected 6, on the assumption that a Meloday menu duplicated
        // all three paths. That menu does not exist, so the count was aspirational. The property
        // worth keeping is not the number of calls but that nothing writes dropdown.hidden outside
        // the helper and the one creation-time default, which is what makes a lifted section
        // impossible rather than merely unlikely.
        Assert.True(
            script.Split("setMenuOpen(wrapper, dropdown, toggle").Length - 1 >= 3,
            "The toggle, the outside-click closer and the post-sync close must all go through setMenuOpen.");
        var directWrites = Regex.Matches(script, @"dropdown\.hidden = [^;]+;")
            .Select(match => match.Value)
            .ToList();
        Assert.Equal(2, directWrites.Count);
        var helper = Head(script, "const setMenuOpen = (wrapper, dropdown, toggle, open)", 900);
        Assert.Contains("dropdown.hidden = !open;", helper, StringComparison.Ordinal);
        Assert.Contains("dropdown.hidden = !open;", directWrites);
        Assert.Contains("dropdown.hidden = true;", directWrites);

        // The menu is a sibling of the clickable art, not a child of it, so selecting a target
        // cannot bubble into navigation.
        var card = Head(script, "const renderLibraryCard = (playlist) =>", 3000);
        Assert.Contains("card.append(art, body);", card, StringComparison.Ordinal);
        // Appended to the card, not to the art. Inside the art the click bubbled up and navigated
        // while the user was picking a target. Matched on the receiver and the call only: the
        // target list grew a platform half when playlist mirroring arrived, so pinning the whole
        // argument list made this fail on a signature change that had nothing to do with the rule.
        Assert.Contains("card.appendChild(renderSyncMenu(playlist,", card, StringComparison.Ordinal);
        Assert.DoesNotContain("art.appendChild(renderSyncMenu", card, StringComparison.Ordinal);

        // Options reuse the app's dropdown item look, and now reuse it completely. This used to append a
        // library-playlist-sync-option class of our own; the alignment class is gone, so the row is
        // the app's own .dropdown-item with nothing added.
        Assert.Contains("className = 'dropdown-item';", script, StringComparison.Ordinal);
        Assert.DoesNotContain("library-playlist-sync-option", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncSurfaces_OfferMirrorOrAppend()
    {
        var script = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js");
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");

        // The library playlist sync menu offers both, defaulting to mirror.
        Assert.Contains("value: \"mirror\"", script, StringComparison.Ordinal);
        Assert.Contains("value: \"append\"", script, StringComparison.Ordinal);
        Assert.Contains("library-playlist-sync-mode-option:checked", script, StringComparison.Ordinal);
        Assert.Contains("syncMode: selectedMode", script, StringComparison.Ordinal);

        // And the endpoint carries it through to the service.
        Assert.Contains("string? SyncMode", controller, StringComparison.Ordinal);
        Assert.Contains("request.SyncMode", controller, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativePath)
        => System.IO.File.ReadAllText(System.IO.Path.Join(new[] { FindRepoRoot() }.Concat(relativePath).ToArray()));

    private static string ReadServiceSource()
        => ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");

    private static string GetMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method signature: {signature}");
        var brace = source.IndexOf('{', start);
        Assert.True(brace >= 0, $"Could not find method body for: {signature}");

        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(brace, i - brace + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not parse method body for: {signature}");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (System.IO.Directory.Exists(System.IO.Path.Join(dir, "DeezSpoTag.Web"))
                && System.IO.Directory.Exists(System.IO.Path.Join(dir, "DeezSpoTag.Tests")))
            {
                return dir;
            }

            dir = System.IO.Directory.GetParent(dir)?.FullName;
        }

        throw new System.IO.DirectoryNotFoundException("Could not locate repository root.");
    }
}
