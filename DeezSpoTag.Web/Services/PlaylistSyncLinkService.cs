using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// One destination in a link, as the app stores it.
/// </summary>
public sealed record PlaylistSyncLinkMember(
    string LinkId,
    string TargetId,
    PlaylistTargetKind Kind,
    string? PlaylistId,
    string PlaylistName,
    bool IsAuthority);

/// <summary>
/// Runs an N-way reconcile over a link: reads every member's current membership, diffs against the
/// stored snapshot, propagates a change on any member to all of them, and records the result.
/// <para>
/// Ported from the reference's <c>reconcile()</c>. The translation between vocabularies is the
/// part the app already had: a member's playlist is readable as source track ids and writable in
/// its own item ids, both through <c>playlist_watch_target_membership</c>.
/// </para>
/// <para>
/// The first pass over a link only records a baseline. A reconcile that starts by mirroring
/// whatever it happens to read would treat a half-populated destination as truth and delete the
/// rest, so an unseen link adds only and never removes.
/// </para>
/// </summary>
public sealed class PlaylistSyncLinkService
{
    private readonly PlaylistSyncEngine _engine;
    private readonly PlaylistSyncReconciler _reconciler;
    private readonly LibraryRepository _library;

    public PlaylistSyncLinkService(
        PlaylistSyncEngine engine,
        PlaylistSyncReconciler reconciler,
        LibraryRepository library)
    {
        _engine = engine;
        _reconciler = reconciler;
        _library = library;
    }

    /// <summary>
    /// The kind recorded on the row. An unrecognised value is treated as a platform rather than a
    /// library, so an unknown destination is never given a library's authority over a real one.
    /// </summary>
    internal static PlaylistTargetKind ParseTargetKind(string? value)
        => string.Equals(value, "library", StringComparison.OrdinalIgnoreCase)
            ? PlaylistTargetKind.Library
            : PlaylistTargetKind.Platform;

    public sealed record MemberResult(
        string TargetId,
        bool Success,
        bool Changed,
        bool SkippedUnresolved,
        string? Message,
        int Added,
        int Removed);

    public sealed record LinkReconcileResult(
        string LinkId,
        bool Success,
        bool BaselineOnly,
        string? Message,
        IReadOnlyList<MemberResult> Members)
    {
        public static LinkReconcileResult Failed(string linkId, string message)
            => new(linkId, false, false, message, Array.Empty<MemberResult>());
    }

    /// <summary>Reconciles one link across all of its members.</summary>
    public async Task<LinkReconcileResult> ReconcileAsync(
        string linkId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(linkId))
        {
            return LinkReconcileResult.Failed(linkId ?? string.Empty, "A link id is required.");
        }

        var memberRows = await _library.GetPlaylistSyncLinkMembersAsync(linkId, cancellationToken);
        if (memberRows.Count == 0)
        {
            return LinkReconcileResult.Failed(linkId, "That link has no destinations.");
        }

        // Read each member's current membership in the shared vocabulary.
        var members = new List<PlaylistSyncReconciler.ReconcileMember>(memberRows.Count);
        foreach (var row in memberRows)
        {
            var target = _engine.Targets.FirstOrDefault(t => string.Equals(t.TargetId, row.TargetId, StringComparison.OrdinalIgnoreCase));
            if (target is null || string.IsNullOrWhiteSpace(row.PlaylistId))
            {
                // Not a destination this deployment can write to. Skip rather than fail the link.
                continue;
            }

            var trackSourceIds = await _library.GetTargetPlaylistTrackSourceIdsAsync(row.TargetId, row.PlaylistId, cancellationToken);
            // What this destination currently holds, in its own ids. Reporting only: the write
            // path resolves identities from the per-service store instead.
            var ownedItemIds = (await _library.GetTargetPlaylistItemIdMapAsync(
                row.TargetId, row.PlaylistId, cancellationToken)).Values.ToList();
            members.Add(new PlaylistSyncReconciler.ReconcileMember(
                row.TargetId,
                ParseTargetKind(row.Kind),
                row.PlaylistId,
                row.PlaylistName ?? row.TargetId,
                trackSourceIds,
                ownedItemIds,
                row.IsAuthority));
        }

        if (members.Count == 0)
        {
            return LinkReconcileResult.Failed(linkId, "No destination in that link is available to write.");
        }

        // A member whose track has no recorded item id on that destination cannot be written
        // there. Writing the rest would silently drop the unresolved tracks, so the member is
        // reported as skipped instead.
        //
        // "First pass" means ANY member has never been snapshotted, not merely members[0]. Asking
        // one member made the answer depend on row order, so a link whose first-ordered member
        // happened to be measured was treated as established while another member had never been
        // seen - and that member's contents were then propagated as if they were the group truth.
        var firstPass = false;
        foreach (var member in members)
        {
            if (await _library.GetPlaylistSyncSnapshotAsync(linkId, member.TargetId, cancellationToken) is null)
            {
                firstPass = true;
                break;
            }
        }

        var results = new List<MemberResult>(members.Count);
        var baselineOnly = firstPass;

        var outcome = await _reconciler.ReconcileAsync(
            linkId,
            members,
            (targetId, ct) => _library.GetPlaylistSyncSnapshotAsync(linkId, targetId, ct),
            async (target, intended, reconcilerBaselineOnly, ct) =>
            {
                // Resolve through the per-service identity store, not this destination's
                // membership: a track the user just added elsewhere has no membership row
                // here yet, which is precisely the case this has to handle.
                var map = await _library.ResolveTargetItemIdsAsync(
                    intended, target.TargetId, ct);
                var ordered = intended.Where(map.ContainsKey).Select(id => map[id]).ToList();
                if (ordered.Count != intended.Count)
                {
                    // Refuse rather than write a shortened playlist, which would silently drop
                    // the unresolved tracks from the destination for good. On a baseline pass
                    // that is not a failure: the link is only recording what each destination
                    // currently holds, so the member is reported and skipped instead.
                    var reason =
                        $"{intended.Count - ordered.Count} track(s) have never resolved on {target.TargetId}";
                    return PlaylistSyncTargetOutcome.Failed(
                        target.TargetId,
                        target.PlaylistId,
                        baselineOnly
                            ? reason + "; baseline only, skipped."
                            : reason + "; nothing was written.");
                }

                return await _engine.SyncAsync(
                    target.TargetId,
                    target.PlaylistName,
                    null,
                    ordered,
                    appendMissingOnly: baselineOnly,
                    new PlaylistSyncBinding("link", linkId),
                    target.PlaylistId,
                    forceAppendReason: baselineOnly ? "first reconcile only records a baseline" : null,
                    cancellationToken: ct);
            },
            cancellationToken);

        foreach (var memberOutcome in outcome.Members)
        {
            results.Add(new MemberResult(
                memberOutcome.TargetId,
                memberOutcome.Success,
                memberOutcome.Changed,
                memberOutcome.Success && memberOutcome.Removed == 0 && memberOutcome.Changed && baselineOnly,
                memberOutcome.Message,
                memberOutcome.Added,
                memberOutcome.Removed));
        }

        if (!outcome.Success && !baselineOnly)
        {
            return new LinkReconcileResult(linkId, false, baselineOnly, outcome.Message, results);
        }

        // Record the agreed membership for EVERY member, not each member's own current contents.
        //
        // A snapshot answers "what was this link's membership when we last agreed?", so it is the
        // same value for every member: the membership the group just settled on. Recording each
        // destination's private state instead made the snapshots self-fulfilling - every member
        // trivially matched its own snapshot, so `changed` came back empty on the next pass and
        // origin selection fell through to member order. Two members that had genuinely diverged
        // were then reported as reconciled with no write at all, and the member that had gained a
        // track had its addition treated as the deviation to remove.
        foreach (var member in members)
        {
            await _library.SavePlaylistSyncSnapshotAsync(
                linkId, member.TargetId, outcome.AgreedTrackSourceIds, cancellationToken);
        }

        return new LinkReconcileResult(
            linkId,
            true,
            baselineOnly,
            baselineOnly
                ? "Baseline recorded. No removals are made on a link's first reconcile."
                : "Reconciled.",
            results);
    }
}
