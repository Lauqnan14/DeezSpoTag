using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// N-way reconcile across every member of a sync link. Ported from the reference's
/// <c>reconcile()</c>: each member is diffed against a stored snapshot, a change on ANY member
/// propagates to all of them, and the first pass over a new link only records a baseline rather
/// than writing destructively.
/// <para>
/// The one identity every member shares is the source track id. The app already stores that
/// mapping in <c>playlist_watch_target_membership</c>, so a member's playlist is readable as a set
/// of source tracks and writable by translating those back into its own item ids. That is what
/// makes a cross-platform reconcile possible without a new identity scheme.
/// </para>
/// </summary>
public sealed class PlaylistSyncReconciler
{
    private readonly PlaylistSyncEngine _engine;

    public PlaylistSyncReconciler(PlaylistSyncEngine engine)
    {
        _engine = engine;
    }

    /// <summary>One destination in a link.</summary>
    public sealed record ReconcileMember(
        string TargetId,
        PlaylistTargetKind Kind,
        string? PlaylistId,
        string PlaylistName,
        IReadOnlyList<string> TrackSourceIds,
        IReadOnlyList<string> OrderedItemIds,
        bool IsAuthority);

    /// <summary>What each member did in one reconcile pass.</summary>
    public sealed record MemberOutcome(
        string TargetId,
        bool Success,
        bool Changed,
        string? Message,
        int Added,
        int Removed,
        bool BaselineOnly);

    public sealed record ReconcileResult(
        bool Success,
        bool BaselineEstablished,
        string? Message,
        IReadOnlyList<MemberOutcome> Members)
    {
        public static ReconcileResult Failed(string message)
            => new(false, false, message, Array.Empty<MemberOutcome>());

        /// <summary>
        /// The membership every member was reconciled to. The caller records this for each member:
        /// a snapshot has to be the group's agreed state, not each destination's own, or the
        /// snapshots become self-fulfilling and no divergence is ever detected.
        /// </summary>
        public IReadOnlyList<string> AgreedTrackSourceIds { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Reconciles one link.
    /// </summary>
    /// <param name="linkId">The link being reconciled; the snapshot namespace.</param>
    /// <param name="members">Every destination in the link, in authority order.</param>
    /// <param name="readSnapshot">The member's stored snapshot, or null when never recorded.</param>
    /// <param name="write">Writes a membership to one member and returns what happened.</param>
    public async Task<ReconcileResult> ReconcileAsync(
        string linkId,
        IReadOnlyList<ReconcileMember> members,
        Func<string, CancellationToken, Task<IReadOnlyList<string>?>> readSnapshot,
        Func<ReconcileMember, IReadOnlyList<string>, bool, CancellationToken, Task<PlaylistSyncTargetOutcome>> write,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(linkId) || members.Count == 0)
        {
            return ReconcileResult.Failed("A link with at least one destination is required.");
        }

        // Which members moved since the last pass.
        //
        // A member with NO snapshot is kept separate from one whose membership changed. A missing
        // snapshot means "never measured", which is not evidence that the user edited anything:
        // on a link's first pass every member is unmeasured, and folding them into `changed` made
        // `changed` equal the whole member list, so origin selection fell through to ordering and
        // members[0] became the source of truth. A first pass then rewrote every other member to
        // match members[0] instead of merely recording what each held, which is a destructive
        // write performed by a pass that claims to only be establishing a baseline.
        var changed = new List<ReconcileMember>();
        var hasUnmeasured = false;
        foreach (var member in members)
        {
            var snapshot = await readSnapshot(member.TargetId, cancellationToken);
            if (snapshot is null)
            {
                hasUnmeasured = true;
                continue;
            }

            if (!SameMembership(snapshot, member.TrackSourceIds))
            {
                changed.Add(member);
            }
        }

        // Who decides the membership. Order matters, and the rule is that a MIRROR NEVER DECIDES.
        // A mirror is a destination that receives the authoritative membership; letting it become
        // the origin would let a mirror's own state overwrite the authority, which is the exact
        // inversion the mirror/authority distinction exists to prevent.
        //
        // 1. An authority that is a self-hosted library wins outright. That is the normal case.
        // 2. Otherwise a library member that actually changed. The libraries are the sources the
        //    user curates in, so a change on one of them is the edit that spreads. Preferring a
        //    library over a changed platform is deliberate: a platform that drifted (or that a
        //    previous pass left half-written) must not outrank a library the user is editing.
        // 3. Otherwise the first member that changed, whatever its kind. This is what makes a
        //    change on a single platform destination propagate when there is no library at all.
        // 4. Otherwise any authority, then the first member.
        //
        // Step 2 is why the previous "any changed member" rule was wrong: with no library authority
        // and nothing changed since the last snapshot, `changed` is empty, so the origin fell
        // through to members[0] - a mirror. Its shorter membership then looked authoritative and
        // the real change (a track added on another member) was silently dropped.
        var origin = members.FirstOrDefault(member => member.IsAuthority && member.Kind == PlaylistTargetKind.Library)
                     ?? changed.FirstOrDefault(member => member.Kind == PlaylistTargetKind.Library)
                     ?? changed.FirstOrDefault()
                     ?? members.FirstOrDefault(member => member.IsAuthority && member.Kind == PlaylistTargetKind.Library)
                     ?? members.FirstOrDefault(member => member.IsAuthority)
                     ?? members[0];

        // A link nobody has measured yet has no origin at all. Naming one would let an arbitrary
        // member's current contents become the group truth and rewrite the rest to match it, so the
        // pass is limited to the add-only baseline the caller already requested. `changed` is empty
        // in exactly this situation, which is why the check comes after origin selection.
        var baselineOnlyPass = hasUnmeasured && changed.Count == 0;

        var intended = origin.TrackSourceIds;
        var outcomes = new List<MemberOutcome>();

        foreach (var member in members)
        {
            if (SameMembership(member.TrackSourceIds, intended))
            {
                outcomes.Add(new MemberOutcome(member.TargetId, true, false, "Already in step.", 0, 0, baselineOnlyPass));
                continue;
            }

            // On a baseline pass every member is add-only: the pass records what each destination
            // holds, so removals are held rather than applied.
            var result = await write(member, intended, baselineOnlyPass, cancellationToken);
            outcomes.Add(new MemberOutcome(
                member.TargetId,
                result.Success,
                result.Success,
                result.Message,
                result.AddedCount,
                result.RemovedCount,
                baselineOnlyPass));

            if (!result.Success)
            {
                return new ReconcileResult(
                    false,
                    baselineOnlyPass,
                    $"{member.TargetId} failed: {result.Message ?? "write failed"}",
                    outcomes)
                {
                    AgreedTrackSourceIds = intended,
                };
            }
        }

        return new ReconcileResult(true, baselineOnlyPass, "Reconciled.", outcomes)
        {
            AgreedTrackSourceIds = intended,
        };
    }

    /// <summary>
    /// Order-insensitive membership comparison. Reordering is not a change: the reference treats
    /// a re-order as its own concern, and a member reordering itself must not push that order onto
    /// every other destination.
    /// </summary>
    internal static bool SameMembership(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var a = left.Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = right.Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return a.SetEquals(b);
    }
}
