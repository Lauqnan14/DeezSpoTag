using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Source-text guardrails for immediate playlist container provisioning: the moment a user saves
/// monitored-playlist target settings, the playlist should exist on every enabled target server
/// right away instead of only after the first batch of tracks finishes downloading. These checks
/// don't exercise real Jellyfin/Navidrome/Plex HTTP calls (PlaylistSyncService talks to concrete
/// API client classes, not interfaces, so that would need a live or mocked server) -- they lock
/// in the wiring so a future refactor can't silently drop the synchronous call or reintroduce the
/// "needs at least one track" gate for the two targets that don't require it.
/// </summary>
public sealed class PlaylistProvisioningGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void SavingPlaylistPreferences_SynchronouslyProvisionsTargetPlaylistContainers()
    {
        var controllerSource = ReadSource("DeezSpoTag.Web/Controllers/Api/LibraryPlaylistWatchlistApiController.cs");

        Assert.Contains("EnsureTargetPlaylistContainersAsync", controllerSource, StringComparison.Ordinal);
        // Must be awaited directly inside SaveSinglePreferenceAsync, not merely referenced/queued
        // via the coordinator's async trigger -- the whole point is it happens before the HTTP
        // response, not on the coordinator's next cycle.
        var saveMethodStart = controllerSource.IndexOf(
            "private async Task<object?> SaveSinglePreferenceAsync(",
            StringComparison.Ordinal);
        Assert.True(saveMethodStart >= 0, "SaveSinglePreferenceAsync method not found.");
        var saveMethodBody = controllerSource.Substring(saveMethodStart, 3200);
        Assert.Contains("await _playlistSyncService.EnsureTargetPlaylistContainersAsync(", saveMethodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void JellyfinAndNavidromeContainers_CanBeCreatedWithoutAnyTrack()
    {
        var syncSource = ReadSource("DeezSpoTag.Web/Services/PlaylistSyncService.cs");

        // The create calls in the eager-provisioning helpers must pass an empty item/song list --
        // if this ever grows a "resolve tracks first" step, provisioning stops being immediate.
        Assert.Contains("itemIds: [],\n                cancellationToken);", syncSource.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("songIds: [],", syncSource, StringComparison.Ordinal);

        var jellyfinClientSource = ReadSource("DeezSpoTag.Integrations/Jellyfin/JellyfinApiClient.cs");
        Assert.DoesNotContain(
            "if (normalizedItemIds.Count == 0)\n        {\n            return null;",
            jellyfinClientSource.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlexContainerCreation_IsNotAttemptedEagerlyBecauseItRequiresASeedItem()
    {
        var syncSource = ReadSource("DeezSpoTag.Web/Services/PlaylistSyncService.cs");
        var ensureMethodStart = syncSource.IndexOf(
            "private async Task<IReadOnlyList<PlaylistProvisioningOutcome>> EnsureTargetPlaylistContainersForServicesAsync(",
            StringComparison.Ordinal);
        Assert.True(ensureMethodStart >= 0, "EnsureTargetPlaylistContainersForServicesAsync method not found.");
        var ensureMethodBody = syncSource.Substring(ensureMethodStart, 1200);

        // Plex must be reported as deferred, not routed through a create call -- its classic
        // playlist endpoint requires a seed item and cannot create a truly empty playlist.
        Assert.Contains("PlexService => new PlaylistProvisioningOutcome(", ensureMethodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsurePlexPlaylistContainerAsync", syncSource, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
