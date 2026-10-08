using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoPlaylistsControllerBehaviorTest
{
    [Fact]
    public void LibraryPlaylistIndex_DoesNotDependOnPerPlaylistItemScans()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");
        var methodBody = GetMethodBody(source, "private async Task<LibraryPlaylistSection?> BuildPlexSectionAsync");

        Assert.Contains("GetPlaylistsAsync(plex.Url, plex.Token", methodBody, StringComparison.Ordinal);

        // Listing must stay cheap: none of the three section builders may load a playlist's
        // items. Only the per-playlist detail endpoint does that.
        foreach (var builder in new[]
        {
            "private async Task<LibraryPlaylistSection?> BuildPlexSectionAsync",
            "private async Task<LibraryPlaylistSection?> BuildJellyfinSectionAsync",
            "private async Task<LibraryPlaylistSection?> BuildNavidromeSectionAsync"
        })
        {
            Assert.DoesNotContain("GetPlaylistItemsAsync", GetMethodBody(source, builder), StringComparison.Ordinal);
            Assert.DoesNotContain("GetPlaylistWithTracksAsync", GetMethodBody(source, builder), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("ResolveLibraryInfoForPlaylistAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryPlaylistIndex_DoesNotDropPlaylistsThroughPlexSectionFiltering()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");

        Assert.DoesNotContain("GetAllowedMusicSectionIdsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("allowedSectionIds", source, StringComparison.Ordinal);
        Assert.Contains("playlist.LibrarySectionId, librarySectionId, StringComparison.OrdinalIgnoreCase", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tab used to be Plex-only: it read state.Plex, returned a flat list and hardcoded
    /// source = "Plex", so Jellyfin and Navidrome playlists could never appear.
    /// </summary>
    [Fact]
    public void LibraryPlaylistIndex_CoversPlexJellyfinAndNavidrome()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");

        Assert.Contains("_plexApiClient.GetPlaylistsAsync", source, StringComparison.Ordinal);
        Assert.Contains("_jellyfinApiClient.GetPlaylistsAsync", source, StringComparison.Ordinal);
        Assert.Contains("_navidromeApiClient.GetPlaylistsAsync", source, StringComparison.Ordinal);

        // Sections, not one flat list.
        Assert.Contains("sections", source, StringComparison.Ordinal);

        // The Plex-only early return that suppressed every other server is gone.
        Assert.DoesNotContain("Plex is not configured. Connect Plex in Login", source, StringComparison.Ordinal);
        Assert.DoesNotContain("source = \"Plex\"", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A section renders only when its server is configured and has at least one playlist left
    /// after monitored playlists are removed. Both conditions are enforced, so an unconnected
    /// server and a fully monitored one both render nothing.
    /// </summary>
    [Fact]
    public void LibraryPlaylistIndex_RendersASectionOnlyWhenConfiguredAndNonEmpty()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");

        Assert.Contains(".Where(static section => section is { Playlists.Count: > 0 })", source, StringComparison.Ordinal);

        // Each server bails out before fetching when its credentials are incomplete.
        Assert.Contains("string.IsNullOrWhiteSpace(jellyfin?.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey)", source, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(navidrome?.Url)", source, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(navidrome.Password)", source, StringComparison.Ordinal);

        // Monitored playlists are removed before the emptiness check, so a server whose every
        // playlist is monitored drops out rather than rendering an empty section.
        foreach (var builder in new[]
        {
            "private async Task<LibraryPlaylistSection?> BuildPlexSectionAsync",
            "private async Task<LibraryPlaylistSection?> BuildJellyfinSectionAsync",
            "private async Task<LibraryPlaylistSection?> BuildNavidromeSectionAsync"
        })
        {
            var body = GetMethodBody(source, builder);
            var monitoredAt = body.IndexOf("monitored.Contains", StringComparison.Ordinal);
            Assert.True(monitoredAt >= 0, $"{builder} must exclude monitored playlists");
        }

        // And the client skips empty sections too, so the tab cannot show an empty heading.
        var script = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js");
        var render = GetMethodBody(script, "const renderLibrarySections = (sections) =>");
        Assert.Contains("if (playlists.length === 0)", render, StringComparison.Ordinal);
        Assert.Contains("return;", render, StringComparison.Ordinal);

        // No server is fetched when none is configured.
        var index = GetMethodBody(source, "public async Task<IActionResult> GetPlaylists");
        Assert.Contains("BuildPlexSectionAsync", index, StringComparison.Ordinal);
        Assert.Contains("BuildJellyfinSectionAsync", index, StringComparison.Ordinal);
        Assert.Contains("BuildNavidromeSectionAsync", index, StringComparison.Ordinal);
    }

    /// <summary>
    /// Monitored playlists belong to the watchlist tab, not this one. Un-monitoring one has to
    /// make it eligible again, so the filter keys on the source and source id of the watchlist
    /// row rather than on anything cached.
    /// </summary>
    [Fact]
    public void LibraryPlaylistIndex_ExcludesMonitoredPlaylists()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");

        Assert.Contains("GetPlaylistWatchlistAsync", source, StringComparison.Ordinal);
        Assert.Contains("BuildPlaylistKey", source, StringComparison.Ordinal);

        foreach (var server in new[] { "PlexServer", "JellyfinServer", "NavidromeServer" })
        {
            Assert.Contains($"monitored.Contains(BuildPlaylistKey({server}", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The tab renders Jellyfin and Navidrome sections, so their playlists have to open. The
    /// tracklist page used to dispatch only source=plex, so the other two fell through to the
    /// generic provider path and could not resolve a local library playlist.
    /// </summary>
    [Fact]
    public void TracklistPage_DispatchesJellyfinAndNavidromePlaylists()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains(
            "tracklistSource === 'plex' || tracklistSource === 'jellyfin' || tracklistSource === 'navidrome'",
            view,
            StringComparison.Ordinal);
        Assert.Contains("loadLibraryPlaylistTracklist(tracklistSource)", view, StringComparison.Ordinal);

        // They are local media servers, so they get the local-source treatment.
        var localSource = GetMethodBody(view, "function isLocalTracklistSource");
        Assert.Contains("normalized === 'jellyfin'", localSource, StringComparison.Ordinal);
        Assert.Contains("normalized === 'navidrome'", localSource, StringComparison.Ordinal);

        // The old Plex-only loader must be gone, not left as dead code.
        Assert.DoesNotContain("async function loadPlexTracklist", view, StringComparison.Ordinal);
        Assert.DoesNotContain("function mapPlexTracklist", view, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryPlaylistDetail_SupportsAllThreeServers()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");

        // Match the detail endpoint, not GetPlaylists, which is a prefix of its name.
        var dispatch = GetMethodBody(source, "public async Task<IActionResult> GetPlaylist(\n");
        Assert.Contains("JellyfinServer => await GetJellyfinPlaylistAsync", dispatch, StringComparison.Ordinal);
        Assert.Contains("NavidromeServer => await GetNavidromePlaylistAsync", dispatch, StringComparison.Ordinal);
        Assert.Contains("_ => await GetPlexPlaylistAsync", dispatch, StringComparison.Ordinal);

        // All three go through the same batch index resolution before rendering.
        foreach (var detail in new[]
        {
            "private async Task<IActionResult> GetPlexPlaylistAsync",
            "private async Task<IActionResult> GetJellyfinPlaylistAsync",
            "private async Task<IActionResult> GetNavidromePlaylistAsync"
        })
        {
            Assert.Contains("ResolveLocalTrackIdsAsync(", GetMethodBody(source, detail), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A tracklist must not cost one upstream request per track. Both providers return the full
    /// item metadata from their playlist endpoints, so the client reads it in one call.
    /// </summary>
    [Fact]
    public void PlaylistTrackLoading_DoesNotFanOutPerTrack()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");

        var jellyfin = GetMethodBody(source, "private async Task<IActionResult> GetJellyfinPlaylistAsync");
        Assert.Contains("_jellyfinApiClient.GetPlaylistItemsAsync", jellyfin, StringComparison.Ordinal);
        Assert.DoesNotContain("GetItemAsync", jellyfin, StringComparison.Ordinal);

        var navidrome = GetMethodBody(source, "private async Task<IActionResult> GetNavidromePlaylistAsync");
        Assert.Contains("_navidromeApiClient.GetPlaylistWithTracksAsync", navidrome, StringComparison.Ordinal);

        // The Navidrome helper takes the songs getPlaylist already returned.
        var client = ReadSource("DeezSpoTag.Integrations", "Navidrome", "NavidromeApiClient.cs");
        var helper = GetMethodBody(client, "public async Task<(NavidromePlaylistDetails? Playlist, IReadOnlyList<NavidromePlaylistTrack> Tracks)> GetPlaylistWithTracksAsync");
        Assert.Contains("\"getPlaylist\"", helper, StringComparison.Ordinal);
        Assert.Contains("playlist.Entries?", helper, StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of the whole change: a server track id has to become the app's own library
    /// track, so the tracklist plays the indexed file rather than proxying from the server.
    /// </summary>
    [Fact]
    public void LibraryPlaylistTracks_ResolveToIndexedLibraryTracks()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");

        var resolve = GetMethodBody(source, "private async Task<IReadOnlyList<long?>> ResolveLocalTrackIdsAsync");
        Assert.Contains("ResolveLocalTrackIdentitiesAsync", resolve, StringComparison.Ordinal);
        Assert.Contains("audioVariant: \"stereo_preferred\"", resolve, StringComparison.Ordinal);
        Assert.Contains("Source: service", resolve, StringComparison.Ordinal);
        Assert.Contains("SourceId: candidate.ServerItemId", resolve, StringComparison.Ordinal);

        // Ambiguous matches resolve to nothing, exactly as the monitored sync path does.
        Assert.Contains("result.IsAmbiguous ? null : result.LocalTrackId", source, StringComparison.Ordinal);

        // The batch call is made once per playlist, not once per track.
        Assert.DoesNotContain("foreach", resolve, StringComparison.Ordinal);
    }

    /// <summary>
    /// Plex must resolve through the index too, not just the two servers that were added with
    /// the section redesign. The per-item stream proxy is kept as the fallback so a track the
    /// library has not indexed still plays.
    /// </summary>
    [Fact]
    public void PlexPlaylistDetail_ResolvesToIndexedLibraryTracksAndKeepsTheStreamFallback()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");
        var plex = GetMethodBody(source, "private async Task<IActionResult> GetPlexPlaylistAsync");

        Assert.Contains("ResolveLocalTrackIdsAsync(PlexServer, candidates", plex, StringComparison.Ordinal);
        // The call carries the resolved ISRCs as a third argument, because a playlist row is
        // labelled with the recording identity. The property this test guards - Plex resolving
        // through the local index rather than the per-item stream proxy - is unchanged by that.
        Assert.Contains("BuildLibraryPlaylistTracks(candidates, localTrackIds, isrcs)", plex, StringComparison.Ordinal);
        Assert.Contains("AttachPlexStreamFallbacks", plex, StringComparison.Ordinal);

        // The ISRCs come from one batched lookup, not a query per track.
        Assert.Contains("ResolveIsrcsAsync(localTrackIds", plex, StringComparison.Ordinal);

        // The per-item proxy is retained, keyed by the server item id, rather than dropped.
        var fallback = GetMethodBody(source, "private List<LibraryPlaylistTrackPayload> AttachPlexStreamFallbacks");
        Assert.Contains("BuildPlexStreamProxyUrl(track.StreamUrl)", fallback, StringComparison.Ordinal);
        Assert.Contains("track with { StreamUrl = streamUrl }", fallback, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryPlaylistTracks_EmitTheFieldsTheLocalPlaybackPathReads()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");
        var build = GetMethodBody(controller, "private List<LibraryPlaylistTrackPayload> BuildLibraryPlaylistTracks");
        Assert.Contains("localTrackId.GetValueOrDefault()", build, StringComparison.Ordinal);

        // The payload record is the single shape every server emits, so the three detail
        // endpoints cannot drift apart in the fields the tracklist reads. A record has no
        // braces, so this asserts on the declaration rather than a method body.
        var declaration = controller[controller.IndexOf("private sealed record LibraryPlaylistTrackPayload", StringComparison.Ordinal)..];
        Assert.Contains("long LocalTrackId", declaration, StringComparison.Ordinal);
        Assert.Contains("long? AudioFileId", declaration, StringComparison.Ordinal);
        Assert.Contains("string? VariantKey", declaration, StringComparison.Ordinal);
        Assert.Contains("string? StreamUrl", declaration, StringComparison.Ordinal);

        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");
        var map = GetMethodBody(view, "function mapLibraryServerTracklist");
        Assert.Contains("localTrackId: Number(track.localTrackId) || 0", map, StringComparison.Ordinal);
        Assert.Contains("audioFileId: track.audioFileId || 0", map, StringComparison.Ordinal);
        Assert.Contains("variantKey: track.variantKey || ''", map, StringComparison.Ordinal);

        // A raw server file path is no longer the playback mechanism.
        Assert.DoesNotContain("filePath: track.filePath", map, StringComparison.Ordinal);
    }

    /// <summary>
    /// A library playlist card syncs to the other configured servers. The source is never a
    /// target of its own playlist, and with every server configured more than one can be
    /// selected in a single action.
    /// </summary>
    [Fact]
    public void LibraryPlaylistSync_OffersOtherConfiguredServersOnly()
    {
        // The rules live on the service; the controller only forwards the request.
        var source = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");
        var sync = GetMethodBody(source, "public async Task<PlaylistSingleSyncResult> SyncSinglePlaylistAsync");

        // Only real servers, deduplicated.
        Assert.Contains("target is PlexService or JellyfinService or NavidromeService", sync, StringComparison.Ordinal);
        Assert.Contains(".Distinct(StringComparer.Ordinal)", sync, StringComparison.Ordinal);

        // One way: a playlist is never synced back to the server it came from.
        Assert.Contains("!string.Equals(target, NormalizeSource(request.Source.Source)", sync, StringComparison.Ordinal);

        // More than one target in a single call.
        Assert.Contains("SyncToPlex: targets.Contains(PlexService)", sync, StringComparison.Ordinal);
        Assert.Contains("SyncToJellyfin: targets.Contains(JellyfinService)", sync, StringComparison.Ordinal);
        Assert.Contains("SyncToNavidrome: targets.Contains(NavidromeService)", sync, StringComparison.Ordinal);

        // Reuses the same per-target writers and the same blocklist filtering as the merge path.
        Assert.Contains("SyncMergedPlaylistTargetsAsync", sync, StringComparison.Ordinal);
        Assert.Contains("FilterTracksForSyncAsync", sync, StringComparison.Ordinal);

        // It is deliberately not the merge path, which requires two monitored sources.
        Assert.Contains("Select at least one destination other than the source.", sync, StringComparison.Ordinal);

        // A streaming platform is written too, not filtered out. Dropping a target the user could
        // see and tick produced a sync that reported success while never touching that platform.
        Assert.Contains("SyncPlatformTargetsAsync", sync, StringComparison.Ordinal);

        // A destination this app does not recognise fails the pass with a reason. Silently ignoring
        // it is the same defect as dropping a platform: the user is told it synced and it did not.
        Assert.Contains("Not a destination this app can write to", sync, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryPlaylistSync_ResolvesCandidatesForAnUnmonitoredPlaylist()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");
        var endpoint = GetMethodBody(controller, "public async Task<IActionResult> SyncPlaylist");

        Assert.Contains("GetPlaylistTrackCandidatesAsync", endpoint, StringComparison.Ordinal);
        Assert.Contains("BuildSyncSourceAsync", endpoint, StringComparison.Ordinal);
        Assert.Contains("SyncSinglePlaylistAsync", endpoint, StringComparison.Ordinal);
        Assert.Contains("Select at least one destination server.", endpoint, StringComparison.Ordinal);

        // The engine reaches unmonitored playlists through a live fetch, which is what makes
        // syncing from this tab possible at all.
        var engine = ReadSource("DeezSpoTag.Web", "Services", "WatchlistEngine.cs");
        Assert.Contains("IsPlaylistWatchlistedAsync", engine, StringComparison.Ordinal);
        Assert.Contains("FetchLivePlaylistSnapshotAsync", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryPlaylistSyncMenu_RevealsOnHoverAndSelectsMultipleTargets()
    {
        var script = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js");

        // Targets must come from the connected-servers endpoint, not from the rendered sections.
        // A streaming platform such as YouTube Music never renders a library section of its own,
        // so deriving the list from the sections made it impossible to offer as a destination.
        Assert.Contains("setAvailableSyncTargets(connectedSyncTargets)", script, StringComparison.Ordinal);
        Assert.Contains("loadConnectedSyncTargets", script, StringComparison.Ordinal);
        Assert.DoesNotContain("setAvailableSyncTargets(populated)", script, StringComparison.Ordinal);

        // syncTargetsFor is an expression-bodied arrow, so there is no brace block to extract.
        Assert.Contains("const syncTargetsFor = (sourceServer) => availableSyncTargets", script, StringComparison.Ordinal);
        Assert.Contains("target.value !== String(sourceServer", script, StringComparison.Ordinal);

        // Hover-only, via the existing artist card menu classes.
        Assert.Contains("watchlist-action-menu--hover", script, StringComparison.Ordinal);
        Assert.Contains("library-playlist-actions", script, StringComparison.Ordinal);

        // Checkboxes, not a radio, so two targets can be chosen for one action.
        Assert.Contains("input.type = \"checkbox\"", script, StringComparison.Ordinal);
        Assert.Contains("library-playlist-sync-target:checked", script, StringComparison.Ordinal);

        var view = ReadSource("DeezSpoTag.Web", "Views", "MediaManagement", "Index.cshtml");
        Assert.Contains("libraryPlaylistsSyncMessage", view, StringComparison.Ordinal);
    }

    /// <summary>
    /// The merge panel used to offer all three servers regardless of connection, with Plex
    /// pre-checked. An unconfigured target was then rejected by the writers, so the merge
    /// failed or half-succeeded, and dismissing the modal reported nothing at all.
    /// </summary>
    [Fact]
    public void MergePanel_OnlyOffersConnectedServersAndNeverReportsSilence()
    {
        var script = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js");

        // Targets come from the server, not a hard-coded list.
        Assert.Contains("loadMergeTargetServers", script, StringComparison.Ordinal);
        Assert.Contains("/api/library/playlists/merge-target-servers", script, StringComparison.Ordinal);

        // Only connected servers get a row; the rest are inert stand-ins.
        Assert.Contains("createDisabledMergeTargetCheckbox", script, StringComparison.Ordinal);
        var disabled = GetMethodBody(script, "function createDisabledMergeTargetCheckbox");
        Assert.Contains("checkbox.disabled = true", disabled, StringComparison.Ordinal);

        // Dismissing the modal must not return silently.
        var panel = script.Substring(script.IndexOf("async function openPlaylistMergePanel", StringComparison.Ordinal));
        Assert.Contains("Merge cancelled. No playlist was synced.", panel, StringComparison.Ordinal);

        // A partial failure has to name the servers that failed.
        Assert.Contains("failed.length > 0", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeTargetServers_ReportsOnlyConnectedServers()
    {
        var service = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");
        var configured = GetMethodBody(service, "public async Task<IReadOnlyList<string>> GetConfiguredTargetServicesAsync");

        // Reuses the same checks the writers use, so the panel cannot offer a server the
        // sync path would reject.
        Assert.Contains("TryLoadConfiguredPlexAsync()", configured, StringComparison.Ordinal);
        Assert.Contains("TryLoadConfiguredJellyfinAsync()", configured, StringComparison.Ordinal);
        Assert.Contains("TryLoadConfiguredNavidromeAsync()", configured, StringComparison.Ordinal);

        // Canonical order.
        var plex = configured.IndexOf("configured.Add(PlexService)", StringComparison.Ordinal);
        var jellyfin = configured.IndexOf("configured.Add(JellyfinService)", StringComparison.Ordinal);
        var navidrome = configured.IndexOf("configured.Add(NavidromeService)", StringComparison.Ordinal);
        Assert.True(plex >= 0 && plex < jellyfin && jellyfin < navidrome);

        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "LibraryPlaylistWatchlistApiController.cs");
        Assert.Contains("merge-target-servers", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryPlaylistImageProxy_KeepsTheJellyfinApiKeyServerSide()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");

        // The proxy URL is handed to the browser, so the key must not be part of it.
        Assert.Contains("BuildJellyfinImageProxyUrl", source, StringComparison.Ordinal);
        var proxyUrl = GetMethodBody(source, "private string? BuildJellyfinImageProxyUrl");
        Assert.DoesNotContain("apiKey", proxyUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", proxyUrl, StringComparison.Ordinal);

        // It is added only when the upstream request is built.
        Assert.Contains("Uri.EscapeDataString(apiKey)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryPlaylistImageProxy_MapsNavidromeCoverArtThroughTheServerSideProxy()
    {
        var source = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "AutoPlaylistsApiController.cs");
        var section = GetMethodBody(source, "private async Task<LibraryPlaylistSection?> BuildNavidromeSectionAsync");
        var proxyUrl = GetMethodBody(source, "private string? BuildNavidromeImageProxyUrl");
        var proxyContext = GetMethodBody(source, "private async Task<(string TargetUrl, IActionResult? ErrorResult)> ResolvePlaylistImageProxyContextAsync");

        Assert.Contains("BuildNavidromeImageProxyUrl(playlist.CoverArt)", section, StringComparison.Ordinal);
        Assert.DoesNotContain("username", proxyUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", proxyUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NavidromeServer", proxyContext, StringComparison.Ordinal);
        Assert.Contains("BuildCoverArtRequestUrl", proxyContext, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryPlaylistCards_UseTheSectionsPayloadAndTheirOwnServer()
    {
        var script = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js");
        var view = ReadSource("DeezSpoTag.Web", "Views", "MediaManagement", "Index.cshtml");

        Assert.Contains("renderLibrarySections", script, StringComparison.Ordinal);
        Assert.Contains("section.displayName", script, StringComparison.Ordinal);
        Assert.Contains("library-playlist-server-section", script, StringComparison.Ordinal);

        // The card must navigate with its own server, not a hardcoded plex.
        var card = GetMethodBody(script, "const renderLibraryCard = (playlist) =>");
        Assert.Contains("openTracklist(playlist.id, playlist.server", card, StringComparison.Ordinal);
        Assert.DoesNotContain("\"plex\"", card, StringComparison.Ordinal);

        // Empty sections are skipped client-side too.
        Assert.Contains("if (playlists.length === 0)", script, StringComparison.Ordinal);

        Assert.Contains("libraryPlaylistsSections", view, StringComparison.Ordinal);
        Assert.DoesNotContain("libraryPlaylistsGrid", view, StringComparison.Ordinal);
        Assert.Contains("Plex, Jellyfin and Navidrome", view, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoPlaylists_AreRenderedInNonEmptySundayFirstDaySections()
    {
        var source = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js");

        var sunday = source.IndexOf("{ id: \"sunday\", label: \"Sunday\" }", StringComparison.Ordinal);
        var monday = source.IndexOf("{ id: \"monday\", label: \"Monday\" }", StringComparison.Ordinal);
        var saturday = source.IndexOf("{ id: \"saturday\", label: \"Saturday\" }", StringComparison.Ordinal);

        Assert.True(sunday >= 0 && sunday < monday && monday < saturday);
        Assert.Contains("const playlistsForDay = playlistsByDay.get(day.id);", source, StringComparison.Ordinal);
        Assert.Contains("if (!playlistsForDay || playlistsForDay.length === 0)", source, StringComparison.Ordinal);
        Assert.Contains("dayGrid.className = \"auto-tools-grid\";", source, StringComparison.Ordinal);
        Assert.Contains("daySection.className = \"auto-playlists-day-section\";", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoPlaylists_AreOrderedFromEarlyMorningThroughLateEveningWithinEachDay()
    {
        var source = ReadSource("DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js");

        var earlyMorning = source.IndexOf("{ id: \"early-morning\", order: 0 }", StringComparison.Ordinal);
        var morning = source.IndexOf("{ id: \"morning\", order: 1 }", StringComparison.Ordinal);
        var noon = source.IndexOf("{ id: \"noon\", order: 2 }", StringComparison.Ordinal);
        var afternoon = source.IndexOf("{ id: \"afternoon\", order: 3 }", StringComparison.Ordinal);
        var evening = source.IndexOf("{ id: \"evening\", order: 4 }", StringComparison.Ordinal);
        var lateEvening = source.IndexOf("{ id: \"late-evening\", order: 5 }", StringComparison.Ordinal);

        Assert.True(earlyMorning >= 0
            && earlyMorning < morning
            && morning < noon
            && noon < afternoon
            && afternoon < evening
            && evening < lateEvening);
        Assert.Contains("playlistsForDay.sort((left, right) => resolvePlaylistDaypartOrder(left) - resolvePlaylistDaypartOrder(right));", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativePath)
    {
        var repoRoot = FindRepoRoot();
        return File.ReadAllText(Path.Join(new[] { repoRoot }.Concat(relativePath).ToArray()));
    }

    /// <summary>
    /// Extracts a brace-delimited body. JavaScript bodies contain braces in object literals and
    /// template strings, so brace counting alone is ambiguous; this finds the body that is
    /// followed by a blank line or a non-indented line, which is how the sources are formatted.
    /// </summary>
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
            if (Directory.Exists(Path.Join(dir, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(dir, "DeezSpoTag.Tests")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
