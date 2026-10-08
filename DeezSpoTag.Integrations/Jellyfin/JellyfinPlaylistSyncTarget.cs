using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations.Jellyfin;

/// <summary>The Jellyfin connection a target needs. Lives here so the adapter needs no Web dependency.</summary>
public sealed record JellyfinTargetConnection(string Url, string ApiKey, string UserId);

/// <summary>
/// The Jellyfin destination. Unlike Plex and Navidrome, Jellyfin is entry-level: a track can appear
/// more than once and each copy has its own playlist entry id, so this works in entries rather than
/// whole-playlist replacement. The add/replace logic moved here from the writer unchanged.
/// </summary>
public sealed class JellyfinPlaylistSyncTarget : IPlaylistSyncTarget
{
    private readonly JellyfinApiClient _client;
    private readonly Func<CancellationToken, Task<JellyfinTargetConnection?>> _connection;

    public JellyfinPlaylistSyncTarget(
        JellyfinApiClient client,
        Func<CancellationToken, Task<JellyfinTargetConnection?>> connection)
    {
        _client = client;
        _connection = connection;
    }

    public string TargetId => "jellyfin";

    public PlaylistTargetKind TargetKind => PlaylistTargetKind.Library;

    public async Task<TargetPlaylistLookup<string>> FindPlaylistAsync(
        string? playlistId,
        string name,
        CancellationToken cancellationToken)
    {
        var jellyfin = await _connection(cancellationToken);
        if (jellyfin is null)
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            var byId = await _client.GetItemResult(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistId,
                cancellationToken);
            var resolvedId = byId.Value?.Id;
            return byId.Status switch
            {
                TargetLookupStatus.Success when !string.IsNullOrWhiteSpace(resolvedId) => TargetPlaylistLookup<string>.Found(resolvedId),
                TargetLookupStatus.NotFound => TargetPlaylistLookup<string>.Missing(),
                _ => TargetPlaylistLookup<string>.Unavailable(byId.HttpStatusCode)
            };
        }

        var byName = await _client.FindPlaylistIdByNameResult(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            name,
            cancellationToken);
        var matchedId = byName.Value;
        return byName.Status switch
        {
            TargetLookupStatus.Success when !string.IsNullOrWhiteSpace(matchedId) => TargetPlaylistLookup<string>.Found(matchedId),
            TargetLookupStatus.NotFound => TargetPlaylistLookup<string>.Missing(),
            _ => TargetPlaylistLookup<string>.Unavailable(byName.HttpStatusCode)
        };
    }

    public async Task<string?> CreatePlaylistAsync(
        string name,
        string? description,
        CancellationToken cancellationToken)
    {
        var jellyfin = await _connection(cancellationToken);
        if (jellyfin is null)
        {
            return null;
        }

        // The client omits the Ids parameter when the collection is empty, so this creates an empty
        // playlist that WriteMembershipAsync then fills.
        return await _client.CreatePlaylistAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            name,
            Array.Empty<string>(),
            cancellationToken);
    }

    public async Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var jellyfin = await _connection(cancellationToken);
        if (jellyfin is null)
        {
            return TargetPlaylistItemsRead.Unreadable;
        }

        var entries = await _client.GetPlaylistEntriesAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            playlistId,
            cancellationToken);
        if (entries is null)
        {
            // A failed read is not an empty playlist.
            return TargetPlaylistItemsRead.Unreadable;
        }

        return TargetPlaylistItemsRead.Ok(
            entries.Select(static entry => entry.ItemId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToList());
    }

    public async Task<PlaylistMembershipWriteResult> WriteMembershipAsync(
        PlaylistMembershipWrite write,
        CancellationToken cancellationToken)
    {
        var jellyfin = await _connection(cancellationToken);
        if (jellyfin is null)
        {
            return PlaylistMembershipWriteResult.Failed("Jellyfin is not configured.");
        }

        var entries = await _client.GetPlaylistEntriesAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            write.PlaylistId,
            cancellationToken);
        if (entries is null)
        {
            return PlaylistMembershipWriteResult.Failed(
                "Jellyfin playlist could not be read, so nothing was written.");
        }

        var (success, message, added) = write.AppendMissingOnly
            ? await AppendMissingAsync(jellyfin, write, entries, cancellationToken)
            : await ReplaceAsync(jellyfin, write, entries, cancellationToken);
        if (!success)
        {
            return PlaylistMembershipWriteResult.Failed(message ?? "Failed to sync Jellyfin playlist.");
        }

        // Reordering is deliberately not done here. It is gated on a capability record the sync
        // service owns, so the writer still performs it after this returns.
        return PlaylistMembershipWriteResult.Ok(
            write.OrderedItemIds.Count,
            write.AppendMissingOnly ? 0 : Math.Max(0, entries.Count - write.OrderedItemIds.Count));
    }

    private async Task<(bool Success, string? Message, int Added)> AppendMissingAsync(
        JellyfinTargetConnection jellyfin,
        PlaylistMembershipWrite write,
        IReadOnlyList<JellyfinPlaylistEntry> entries,
        CancellationToken cancellationToken)
    {
        var existing = entries
            .Select(static entry => entry.ItemId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = write.OrderedItemIds
            .Where(trackId => !string.IsNullOrWhiteSpace(trackId) && !existing.Contains(trackId))
            .ToList();
        if (pending.Count == 0)
        {
            return (true, null, 0);
        }

        var appended = await _client.AddPlaylistItemsAsync(
            jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId, write.PlaylistId, pending, cancellationToken);
        return appended
            ? (true, null, pending.Count)
            : (false, "Failed to append tracks to Jellyfin playlist.", 0);
    }

    private async Task<(bool Success, string? Message, int Added)> ReplaceAsync(
        JellyfinTargetConnection jellyfin,
        PlaylistMembershipWrite write,
        IReadOnlyList<JellyfinPlaylistEntry> entries,
        CancellationToken cancellationToken)
    {
        var expected = write.OrderedItemIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var staleEntryIds = new List<string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.PlaylistEntryId))
            {
                continue;
            }

            if (!expected.Contains(entry.ItemId) || !retained.Add(entry.ItemId))
            {
                staleEntryIds.Add(entry.PlaylistEntryId);
            }
        }

        var pending = write.OrderedItemIds
            .Where(trackId => !string.IsNullOrWhiteSpace(trackId) && !retained.Contains(trackId))
            .ToList();
        if (staleEntryIds.Count == 0 && pending.Count == 0)
        {
            return (true, null, 0);
        }

        if (pending.Count > 0
            && !await _client.AddPlaylistItemsAsync(
                jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId, write.PlaylistId, pending, cancellationToken))
        {
            return (false, "Failed to add tracks to Jellyfin playlist.", 0);
        }

        if (staleEntryIds.Count > 0
            && !await _client.RemovePlaylistEntriesAsync(
                jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId, write.PlaylistId, staleEntryIds, cancellationToken))
        {
            return (false, "Failed to remove stale Jellyfin playlist items.", 0);
        }

        return (true, null, pending.Count);
    }
}
