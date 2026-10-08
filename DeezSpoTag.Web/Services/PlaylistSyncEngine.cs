using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// The half of a playlist push that is the same for every destination: resolve the playlist,
/// create it if absent, refuse to write an unresolvable source, hold destructive changes when the
/// target's read looks broken, write, verify, and report.
/// <para>
/// Ported from the reference implementation, whose <c>base.py</c> keeps diff, safety rails and
/// stats in one place while each target owns only its own create/read/write calls. The diff
/// itself is the app's existing <see cref="PlaylistSyncService.ComputePlaylistMembershipDelta"/>
/// rather than a second copy.
/// </para>
/// </summary>
public sealed class PlaylistSyncEngine
{
    private readonly PlaylistSyncTargetRegistry _registry;
    /// <summary>
    /// A target read that returns far fewer entries than the last time this app saw the playlist
    /// is treated as a broken read, and its "removals" are ignored. One failed fetch must not
    /// cascade into a mass delete across every destination. Ported from the reference's
    /// COLLAPSE_FRACTION; the app already used 0.4 and that value is kept.
    /// </summary>
    internal const double RemovalReadCollapseFraction = 0.4;

    private readonly Func<string, PlaylistSyncBinding, CancellationToken, Task<string?>> _targetPlaylistIdReader;
    private readonly Func<string, PlaylistSyncBinding, string?, CancellationToken, Task> _bindingWriter;
    private readonly Func<string, string, CancellationToken, Task<int?>>? _baselineReader;

    /// <summary>
    /// Creates an engine bound to the given target registry, playlist id reader, binding
    /// writer, and optional baseline reader.
    /// </summary>
    /// <param name="baselineReader">
    /// How many entries this app last recorded for a destination playlist, or null when it has
    /// never seen it. Optional so a caller with no recorded history still gets the collapse
    /// guard, which simply has nothing to compare against.
    /// </param>
    public PlaylistSyncEngine(
        PlaylistSyncTargetRegistry registry,
        Func<string, PlaylistSyncBinding, CancellationToken, Task<string?>> targetPlaylistIdReader,
        Func<string, PlaylistSyncBinding, string?, CancellationToken, Task> bindingWriter,
        Func<string, string, CancellationToken, Task<int?>>? baselineReader = null)
    {
        _registry = registry;
        _targetPlaylistIdReader = targetPlaylistIdReader;
        _bindingWriter = bindingWriter;
        _baselineReader = baselineReader;
    }

    /// <summary>
    /// A broken read is one that returns far fewer entries than the recorded baseline. The
    /// physical-id half of the original predicate compared a stored playlist identity to the
    /// current one; the engine resolves the playlist through the target instead, so a changed
    /// identity is already a different playlist and cannot be diffed against the old baseline.
    /// </summary>
    internal static bool IsReadIncompleteForRemoval(int baselineCount, int currentCount)
        => baselineCount > 0 && currentCount < RemovalReadCollapseFraction * baselineCount;

    /// <summary>
    /// Caps one pass's removals, as the reference does. A cap of zero means removal mirroring is
    /// off: nothing is removed and the count is reported. Over the cap, the excess is held for a
    /// later pass rather than dropped.
    /// </summary>
    internal static IReadOnlyList<T> CapRemovals<T>(IReadOnlyList<T> removals, int maxRemovals)
    {
        if (maxRemovals <= 0)
        {
            return Array.Empty<T>();
        }

        return removals.Count <= maxRemovals ? removals : removals.Take(maxRemovals).ToList();
    }

    public IReadOnlyCollection<string> RegisteredTargetIds => _registry.TargetIds;

    /// <summary>Every registered destination, so a caller can read the kind of each.</summary>
    public IReadOnlyList<IPlaylistSyncTarget> Targets => _registry.Targets;

    /// <summary>
    /// Pushes one playlist to one destination.
    /// </summary>
    /// <param name="requestedPlaylistId">
    /// The id the caller already holds, if any. When absent the engine asks the link store, then
    /// falls back to matching by name, so a repeat pass reuses the playlist it already created
    /// instead of creating a second one.
    /// </param>
    public async Task<PlaylistSyncTargetOutcome> SyncAsync(
        string service,
        string playlistName,
        string? playlistDescription,
        IReadOnlyList<string> orderedItemIds,
        bool appendMissingOnly,
        PlaylistSyncBinding binding,
        string? requestedPlaylistId = null,
        string? forceAppendReason = null,
        CancellationToken cancellationToken = default)
    {
        var target = _registry.Find(service);
        if (target is null)
        {
            return PlaylistSyncTargetOutcome.NotConfigured(service);
        }

        if (orderedItemIds.Count == 0)
        {
            return PlaylistSyncTargetOutcome.Failed(service, null, "No tracks resolved for this playlist.");
        }

        var knownId = requestedPlaylistId;
        if (string.IsNullOrWhiteSpace(knownId))
        {
            knownId = await _targetPlaylistIdReader(service, binding, cancellationToken);
        }

        var lookup = await target.FindPlaylistAsync(knownId, playlistName, cancellationToken);
        if (lookup.Status == TargetLookupStatus.Transient)
        {
            return PlaylistSyncTargetOutcome.Failed(
                service, knownId, $"{DisplayName(service)} is not responding. Nothing was written.");
        }

        var playlistId = lookup.Value;
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            playlistId = await target.CreatePlaylistAsync(playlistName, playlistDescription, cancellationToken);
            if (string.IsNullOrWhiteSpace(playlistId))
            {
                // A destination that can say WHY is worth saying. The common case this exists for is
                // a playlist that was created but could not be filed into the library, which reads
                // as an ordinary creation failure and sends the user looking in the wrong place.
                var detail = target.CreateFailureDetail;
                return PlaylistSyncTargetOutcome.Failed(
                    service,
                    null,
                    string.IsNullOrWhiteSpace(detail)
                        ? $"{DisplayName(service)} failed to create the playlist."
                        : $"{DisplayName(service)} failed to create the playlist. {detail}");
            }
        }

        var read = await target.ReadItemIdsAsync(playlistId, cancellationToken);
        if (!read.Success)
        {
            // A failed read is not an empty playlist. Mirroring against "empty" would delete every
            // track on the destination, so nothing is written and the pass is retryable.
            return PlaylistSyncTargetOutcome.Failed(
                service,
                playlistId,
                $"{DisplayName(service)} playlist could not be read, so nothing was written.",
                retryable: true);
        }

        // Safety rails, applied before anything destructive. Both are the reference's, and both
        // only ever *reduce* what a pass removes.
        var removalsHeld = 0;
        var appendReason = forceAppendReason;
        var effectiveAppend = appendMissingOnly || !string.IsNullOrWhiteSpace(forceAppendReason);

        if (!effectiveAppend && _baselineReader is not null)
        {
            var baseline = await _baselineReader(service, playlistId, cancellationToken);
            if (baseline is > 0 && IsReadIncompleteForRemoval(baseline.Value, read.ItemIds.Count))
            {
                // The destination read far fewer entries than this app last recorded. That is a
                // broken read, not a real deletion, so mirror it as add-only rather than deleting
                // whatever the incomplete read failed to return.
                effectiveAppend = true;
                removalsHeld = Math.Max(0, read.ItemIds.Count);
            }
        }

        var write = await target.WriteMembershipAsync(
            new PlaylistMembershipWrite(
                playlistId,
                playlistName,
                orderedItemIds,
                read.ItemIds,
                effectiveAppend),
            cancellationToken);

        if (!write.Success)
        {
            return PlaylistSyncTargetOutcome.Failed(service, playlistId, write.Message ?? "Write failed.");
        }

        await _bindingWriter(service, binding, playlistId, cancellationToken);

        var outcome = PlaylistSyncTargetOutcome.Succeeded(
            service,
            playlistId,
            write.AddedCount,
            write.RemovedCount,
            effectiveAppend) with { WriteComplete = write.WriteComplete };

        if (!string.IsNullOrWhiteSpace(appendReason))
        {
            outcome = outcome with
            {
                Message = outcome.Message + $" Removals held: {appendReason}."
            };
        }

        return removalsHeld > 0
            ? outcome with
            {
                Message = outcome.Message + $" Held {removalsHeld} removal(s): the destination read looked incomplete."
            }
            : outcome;
    }

    private static string DisplayName(string service) => service switch
    {
        "plex" => "Plex",
        "jellyfin" => "Jellyfin",
        "navidrome" => "Navidrome",
        "ytmusic" => "YouTube Music",
        "spotify" => "Spotify",
        "deezer" => "Deezer",
        "qobuz" => "Qobuz",
        "tidal" => "TIDAL",
        "applemusic" => "Apple Music",
        _ => service
    };
}

/// <summary>
/// Which source playlist a push is for. The sync link is keyed on the source playlist's own
/// service and id, so a binding can only be read or written when the engine is told which
/// playlist it is pushing.
/// </summary>
public sealed record PlaylistSyncBinding(string SourceService, string SourcePlaylistId);

public sealed record PlaylistSyncTargetOutcome(
    string Service,
    string? PlaylistId,
    bool Success,
    string? Message,
    int AddedCount,
    int RemovedCount,
    bool Retryable)
{
    /// <summary>
    /// Whether the destination reported the write as fully applied. False means the target
    /// accepted the request but could not confirm every entry landed, so the caller must not treat
    /// the membership as verified.
    /// </summary>
    public bool WriteComplete { get; init; } = true;
    public static PlaylistSyncTargetOutcome Succeeded(
        string service, string playlistId, int added, int removed, bool appendMissingOnly)
        => new(
            service,
            playlistId,
            true,
            appendMissingOnly
                ? $"Appended {added} track(s)."
                : $"Synced {added} track(s), removed {removed}.",
            added,
            removed,
            false);

    public static PlaylistSyncTargetOutcome Failed(
        string service, string? playlistId, string message, bool retryable = false)
        => new(service, playlistId, false, message, 0, 0, retryable);

    public static PlaylistSyncTargetOutcome NotConfigured(string service)
        => new(service, null, false, $"{service} is not configured.", 0, 0, true);
}
