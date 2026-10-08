using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// N-way reconcile. Ported from the reference's reconcile(): every member is diffed against a
/// stored snapshot, a change on any one member propagates to all, and the link does not fight
/// itself. These pin the decisions that are easy to get wrong.
/// </summary>
public sealed class PlaylistSyncReconcilerTest
{
    private static PlaylistSyncReconciler.ReconcileMember Member(
        string targetId,
        IReadOnlyList<string> tracks,
        bool authority = false,
        PlaylistTargetKind kind = PlaylistTargetKind.Library,
        IReadOnlyList<string>? itemIds = null)
        => new(
            targetId,
            kind,
            $"{targetId}-playlist",
            $"Playlist on {targetId}",
            tracks,
            itemIds ?? tracks,
            authority);

    private static Task<PlaylistSyncTargetOutcome> Ok(int added = 0, int removed = 0)
        => Task.FromResult(PlaylistSyncTargetOutcome.Succeeded("x", "x", added, removed, false));

    [Fact]
    public async Task Reconcile_LeavesEveryMemberAlone_WhenTheyAllAgree()
    {
        var reconciler = new PlaylistSyncReconciler(new PlaylistSyncEngine(
            new PlaylistSyncTargetRegistry(Array.Empty<IPlaylistSyncTarget>()),
            (_, _, _) => Task.FromResult<string?>(null),
            (_, _, _, _) => Task.CompletedTask));

        var writes = 0;
        var result = await reconciler.ReconcileAsync(
            "plex:pl-1",
            new[] { Member("plex", new[] { "t1", "t2" }, authority: true), Member("jellyfin", new[] { "t2", "t1" }) },
            (_, _) => Task.FromResult<IReadOnlyList<string>?>(new[] { "t1", "t2" }),
            (_, _, _, _) => { writes++; return Ok(); },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Reconcile_PropagatesAChangeOnANonAuthorityMemberToTheRest()
    {
        // The user added a track on Jellyfin. Plex must pick it up.
        var reconciler = new PlaylistSyncReconciler(new PlaylistSyncEngine(
            new PlaylistSyncTargetRegistry(Array.Empty<IPlaylistSyncTarget>()),
            (_, _, _) => Task.FromResult<string?>(null),
            (_, _, _, _) => Task.CompletedTask));

        var written = new List<(string Target, IReadOnlyList<string> Tracks)>();
        var result = await reconciler.ReconcileAsync(
            "plex:pl-1",
            new[]
            {
                // No named authority: this is a plain nway link, so a change on any member spreads.
                Member("plex", new[] { "t1" }),
                Member("jellyfin", new[] { "t1", "t2" }),
            },
            (_, _) => Task.FromResult<IReadOnlyList<string>?>(new[] { "t1" }),
            (member, tracks, _, _) =>
            {
                written.Add((member.TargetId, tracks));
                return Ok();
            },
            CancellationToken.None);

        Assert.True(result.Success);
        // The member that lagged is written, and it receives the changed member's set.
        Assert.Equal(new[] { ("plex", (IReadOnlyList<string>)new[] { "t1", "t2" }) }, written);
    }

    [Fact]
    public async Task Reconcile_LetsTheAuthorityWinOverAChangedNonAuthority()
    {
        // A non-authority member drifting must not silently override the library playlist.
        var reconciler = new PlaylistSyncReconciler(new PlaylistSyncEngine(
            new PlaylistSyncTargetRegistry(Array.Empty<IPlaylistSyncTarget>()),
            (_, _, _) => Task.FromResult<string?>(null),
            (_, _, _, _) => Task.CompletedTask));

        var written = new List<(string Target, IReadOnlyList<string> Tracks)>();
        await reconciler.ReconcileAsync(
            "plex:pl-1",
            new[]
            {
                Member("plex", new[] { "t1", "t2" }, authority: true),
                Member("jellyfin", new[] { "t1" }),
            },
            (_, _) => Task.FromResult<IReadOnlyList<string>?>(new[] { "t1", "t2" }),
            (member, tracks, _, _) =>
            {
                written.Add((member.TargetId, tracks));
                return Ok();
            },
            CancellationToken.None);

        Assert.Equal(new[] { ("jellyfin", (IReadOnlyList<string>)new[] { "t1", "t2" }) }, written);
    }

    [Fact]
    public async Task Reconcile_TreatsAMemberWithNoSnapshotAsChanged()
    {
        // A destination joining an existing link has no baseline. Treating "no snapshot" as
        // "unchanged" would mean a new member is never brought into step.
        var reconciler = new PlaylistSyncReconciler(new PlaylistSyncEngine(
            new PlaylistSyncTargetRegistry(Array.Empty<IPlaylistSyncTarget>()),
            (_, _, _) => Task.FromResult<string?>(null),
            (_, _, _, _) => Task.CompletedTask));

        var written = new List<string>();
        await reconciler.ReconcileAsync(
            "plex:pl-1",
            new[]
            {
                Member("plex", new[] { "t1" }, authority: true),
                Member("navidrome", new[] { "t9" }),
            },
            (target, _) => Task.FromResult<IReadOnlyList<string>?>(
                target == "plex" ? new[] { "t1" } : null),
            (member, _, _, _) => { written.Add(member.TargetId); return Ok(); },
            CancellationToken.None);

        Assert.Equal(new[] { "navidrome" }, written);
    }

    [Fact]
    public async Task Reconcile_StopsAndReportsWhenAMemberFails()
    {
        var reconciler = new PlaylistSyncReconciler(new PlaylistSyncEngine(
            new PlaylistSyncTargetRegistry(Array.Empty<IPlaylistSyncTarget>()),
            (_, _, _) => Task.FromResult<string?>(null),
            (_, _, _, _) => Task.CompletedTask));

        var result = await reconciler.ReconcileAsync(
            "plex:pl-1",
            new[]
            {
                Member("plex", new[] { "t1" }, authority: true),
                Member("jellyfin", new[] { "t1", "t2" }),
                Member("navidrome", new[] { "t1" }),
            },
            (_, _) => Task.FromResult<IReadOnlyList<string>?>(new[] { "t1" }),
            (member, _, _, _) => member.TargetId == "jellyfin"
                ? Task.FromResult(PlaylistSyncTargetOutcome.Failed("jellyfin", null, "upstream refused"))
                : Ok(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("jellyfin", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_RefusesAnEmptyLink()
    {
        var reconciler = new PlaylistSyncReconciler(new PlaylistSyncEngine(
            new PlaylistSyncTargetRegistry(Array.Empty<IPlaylistSyncTarget>()),
            (_, _, _) => Task.FromResult<string?>(null),
            (_, _, _, _) => Task.CompletedTask));

        var result = await reconciler.ReconcileAsync(
            "plex:pl-1", Array.Empty<PlaylistSyncReconciler.ReconcileMember>(),
            (_, _) => Task.FromResult<IReadOnlyList<string>?>(null),
            (_, _, _, _) => Ok(),
            CancellationToken.None);

        Assert.False(result.Success);
    }

    /// <summary>
    /// Reordering is not a change. A member that reorders itself must not push its order onto
    /// every other destination, which is why the comparison is a set comparison.
    /// </summary>
    [Fact]
    public void MembershipComparison_IgnoresOrderButNotContent()
    {
        Assert.True(PlaylistSyncReconciler.SameMembership(new[] { "a", "b" }, new[] { "b", "a" }));
        Assert.False(PlaylistSyncReconciler.SameMembership(new[] { "a", "b" }, new[] { "a" }));
        // Ids are compared case-insensitively, the same as the shared membership diff.
        Assert.True(PlaylistSyncReconciler.SameMembership(new[] { "a" }, new[] { "A" }));
        Assert.True(PlaylistSyncReconciler.SameMembership(Array.Empty<string>(), Array.Empty<string>()));
    }
}
