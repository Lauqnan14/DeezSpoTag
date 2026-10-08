using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations;

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
