using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations;

public sealed record PlaylistArtworkPayload(byte[] Bytes, string ContentType);

/// <summary>Null fields preserve the destination; an empty description explicitly clears it.</summary>
public sealed record PlaylistMetadataWrite(
    string PlaylistId, string PlaylistName, string? Description, PlaylistArtworkPayload? Artwork);

public enum PlaylistMetadataFieldStatus
{
    NotRequested,
    Applied,
    Unsupported,
    Failed
}

public sealed record PlaylistMetadataWriteResult(
    PlaylistMetadataFieldStatus DescriptionStatus,
    PlaylistMetadataFieldStatus ArtworkStatus,
    string? Message)
{
    public bool Complete => DescriptionStatus is PlaylistMetadataFieldStatus.NotRequested or PlaylistMetadataFieldStatus.Applied
        && ArtworkStatus is PlaylistMetadataFieldStatus.NotRequested or PlaylistMetadataFieldStatus.Applied;

    public static PlaylistMetadataWriteResult Unsupported(PlaylistMetadataWrite write, string message)
        => new(write.Description is null ? PlaylistMetadataFieldStatus.NotRequested : PlaylistMetadataFieldStatus.Unsupported,
            write.Artwork is null ? PlaylistMetadataFieldStatus.NotRequested : PlaylistMetadataFieldStatus.Unsupported,
            write.Description is null && write.Artwork is null ? null : message);

    public static PlaylistMetadataWriteResult Failed(PlaylistMetadataWrite write, string message)
        => new(write.Description is null ? PlaylistMetadataFieldStatus.NotRequested : PlaylistMetadataFieldStatus.Failed,
            write.Artwork is null ? PlaylistMetadataFieldStatus.NotRequested : PlaylistMetadataFieldStatus.Failed,
            message);
}

/// <summary>
/// The membership a sync wants one destination playlist to end up with.
/// <para>
/// <see cref="CurrentItemIds"/> is only meaningful when the target could actually read the
/// playlist. That distinction is the whole safety model: a read that failed must never be
/// mistaken for an empty playlist, because mirroring against "empty" would delete every track
/// on the target. <see cref="ReadItemIdsAsync"/> therefore reports failure separately rather
/// than returning an empty list.
/// </para>
/// </summary>
public sealed record PlaylistMembershipWrite(
    string PlaylistId,
    string PlaylistName,
    IReadOnlyList<string> OrderedItemIds,
    IReadOnlyList<string> CurrentItemIds,
    bool AppendMissingOnly);

/// <summary>Outcome of writing a membership, including whether anything destructive happened.</summary>
public sealed record PlaylistMembershipWriteResult(
    bool Success,
    string? Message,
    int AddedCount,
    int RemovedCount,
    bool WriteComplete = true)
{
    public static PlaylistMembershipWriteResult Failed(string message)
        => new(false, message, 0, 0);

    public static PlaylistMembershipWriteResult Ok(int added, int removed, bool writeComplete = true)
        => new(true, null, added, removed, writeComplete);
}

/// <summary>
/// The additions one membership write actually needs, shared by every destination.
/// <para>
/// Additions are computed the same way in append mode and in mirror mode, because the reference
/// does it that way: <c>plan = {src: (desired - ids, ids - desired)}</c>. What
/// <see cref="PlaylistMembershipWrite.AppendMissingOnly"/> governs is removal, never addition.
/// A writer that sends the whole desired list whenever removals are allowed re-appends every track
/// the destination already holds, and a playlist that mirrors against itself grows on every pass
/// instead of converging.
/// </para>
/// <para>
/// The difference is a multiset difference, not a set difference. Set semantics silently
/// under-fill a source that legitimately holds a track twice, while a per-occurrence filter that
/// never decrements over-fills for the same reason: against a destination holding one copy, it
/// would send both copies of a wanted pair. Each occurrence of an id in <paramref name="currentIds"/>
/// retires exactly one occurrence in <paramref name="orderedItemIds"/>, and the source's order is
/// preserved so the destination ends up in the order the platform can only append in.
/// </para>
/// </summary>
public static class PlaylistMembershipDiff
{
    public static IReadOnlyList<string> MissingAdditions(
        IReadOnlyList<string> orderedItemIds,
        IEnumerable<string> currentIds)
    {
        if (orderedItemIds.Count == 0)
        {
            return Array.Empty<string>();
        }

        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var currentId in currentIds)
        {
            if (string.IsNullOrWhiteSpace(currentId))
            {
                continue;
            }

            remaining.TryGetValue(currentId, out var count);
            remaining[currentId] = count + 1;
        }

        var missing = new List<string>(orderedItemIds.Count);
        foreach (var wanted in orderedItemIds)
        {
            if (string.IsNullOrWhiteSpace(wanted))
            {
                continue;
            }

            if (remaining.TryGetValue(wanted, out var count) && count > 0)
            {
                remaining[wanted] = count - 1;
                continue;
            }

            missing.Add(wanted);
        }

        return missing;
    }
}

/// <summary>The result of reading a destination playlist's current membership.</summary>
public sealed record TargetPlaylistItemsRead(bool Success, IReadOnlyList<string> ItemIds)
{
    public static TargetPlaylistItemsRead Ok(IReadOnlyList<string> itemIds)
        => new(true, itemIds);

    /// <summary>
    /// The read failed. <see cref="ItemIds"/> is meaningless here and must not be diffed against.
    /// This is the state the removal guard exists for.
    /// </summary>
    public static TargetPlaylistItemsRead Unreadable { get; } = new(false, Array.Empty<string>());
}

/// <summary>
/// What kind of thing a destination is. This is not cosmetic: a self-hosted library is both a
/// source and a valid destination, while a streaming platform is a destination only, and the two
/// behave differently when a link is reconciled. Calling both "services" hid that difference, so
/// it is recorded explicitly rather than inferred from the id.
/// </summary>
public enum PlaylistTargetKind
{
    /// <summary>A self-hosted library: Plex, Jellyfin, Navidrome.</summary>
    Library = 0,

    /// <summary>A streaming platform: YouTube Music, and the others a link can push to.</summary>
    Platform = 1
}

/// <summary>
/// A destination a library or Meloday playlist can be pushed to.
/// <para>
/// Ported from the reference implementation's <c>MirrorTarget</c>. The reference splits the work
/// the same way: each target owns playlist creation, reading its own items, and applying a
/// membership write, while diffing, the removal safety rails, and result assembly live once in
/// the engine above it. A platform is added by implementing this, not by adding a branch to a
/// writer.
/// </para>
/// <para>
/// Implementations must not treat <see cref="TargetPlaylistItemsRead.Unreadable"/> as an empty
/// playlist, and must not delete anything when <see cref="PlaylistMembershipWrite.AppendMissingOnly"/>
/// is set.
/// </para>
/// </summary>
public interface IPlaylistSyncTarget
{
    /// <summary>Stable destination id: plex, jellyfin, navidrome, ytmusic.</summary>
    string TargetId { get; }

    /// <summary>Whether this destination is a self-hosted library or a streaming platform.</summary>
    PlaylistTargetKind TargetKind { get; }

    /// <summary>
    /// Whether this destination holds the credential a write needs, answered from its own store.
    /// <para>
    /// This is NOT a lookup's <see cref="TargetLookupStatus"/>, and conflating the two is how an
    /// account with no credentials at all still turned up as somewhere the user could sync to: a
    /// target reports "nothing is stored for this account" and "the provider did not answer" with
    /// the same status, so a caller reading that status cannot tell an unconnected platform from a
    /// broken network and offers both. The two questions are different, so they get different
    /// answers - this one is unambiguous and does not depend on a provider that may be
    /// unreachable, while a lookup still reports whatever the provider said.
    /// </para>
    /// <para>
    /// A default of true keeps this off destinations that are gated elsewhere. Every streaming
    /// platform overrides it, because a platform with no stored credential is the case this exists
    /// for.
    /// </para>
    /// </summary>
    Task<bool> IsConnectedAsync(CancellationToken cancellationToken)
        => Task.FromResult(true);

    /// <summary>
    /// Resolves the destination playlist: by id when one is known, otherwise by name so a
    /// re-run finds the playlist an earlier pass created.
    /// </summary>
    Task<TargetPlaylistLookup<string>> FindPlaylistAsync(
        string? playlistId,
        string name,
        CancellationToken cancellationToken);

    /// <summary>Creates a playlist and returns its new id, or null when creation failed.</summary>
    Task<string?> CreatePlaylistAsync(
        string name,
        string? description,
        CancellationToken cancellationToken);

    /// <summary>
    /// Why the last <see cref="CreatePlaylistAsync"/> returned null, when the target can say.
    /// <para>
    /// A default of null keeps this off every other destination. It exists for the case where the
    /// playlist WAS created but something after it did not complete: the failure is real and
    /// actionable, and reporting only "creation failed" would send the user looking in the wrong
    /// place. Read only after a null return.
    /// </para>
    /// </summary>
    string? CreateFailureDetail => null;

    Task<PlaylistMetadataWriteResult> WriteMetadataAsync(PlaylistMetadataWrite write, CancellationToken cancellationToken)
        => Task.FromResult(PlaylistMetadataWriteResult.Unsupported(write, "Playlist metadata updates are unsupported by this destination."));

    /// <summary>Reads the destination's current membership, reporting failure separately.</summary>
    Task<TargetPlaylistItemsRead> ReadItemIdsAsync(
        string playlistId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies the membership. In append mode nothing may be removed. In match mode the target
    /// should end up holding exactly <see cref="PlaylistMembershipWrite.OrderedItemIds"/>.
    /// </summary>
    Task<PlaylistMembershipWriteResult> WriteMembershipAsync(
        PlaylistMembershipWrite write,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves a target by service id. Keeping this separate from the engine means a new platform
/// is registered here and the engine never learns its name.
/// </summary>
public sealed class PlaylistSyncTargetRegistry
{
    private readonly Dictionary<string, IPlaylistSyncTarget> _targets;

    public PlaylistSyncTargetRegistry(IEnumerable<IPlaylistSyncTarget> targets)
    {
        _targets = new Dictionary<string, IPlaylistSyncTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            _targets[target.TargetId] = target;
        }
    }

    public IReadOnlyCollection<string> TargetIds => _targets.Keys;

    /// <summary>Every registered destination. Callers read <see cref="IPlaylistSyncTarget.TargetKind"/> from these.</summary>
    public IReadOnlyList<IPlaylistSyncTarget> Targets => _targets.Values.ToList();

    /// <summary>Destinations of one kind, so a caller can offer libraries and platforms separately.</summary>
    public IReadOnlyList<IPlaylistSyncTarget> OfKind(PlaylistTargetKind kind)
        => _targets.Values.Where(target => target.TargetKind == kind).ToList();

    public IPlaylistSyncTarget? Find(string? targetId)
        => string.IsNullOrWhiteSpace(targetId) ? null : _targets.GetValueOrDefault(targetId.Trim());
}
