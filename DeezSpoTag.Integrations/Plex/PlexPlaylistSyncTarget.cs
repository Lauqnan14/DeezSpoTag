using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations.Plex;

/// <summary>The Plex connection a target needs. Lives here so the adapter needs no Web dependency.</summary>
public sealed record PlexTargetConnection(string Url, string Token, string MachineIdentifier);

/// <summary>
/// The Plex destination. Wraps <see cref="PlexApiClient"/> in the target contract without changing
/// what it does: Plex is a whole-playlist upsert rather than an add/remove pair, so
/// <see cref="WriteMembershipAsync"/> delegates to the same upsert the writer used to call and
/// ignores the current-item list, which Plex does not need.
/// </summary>
public sealed class PlexPlaylistSyncTarget : IPlaylistSyncTarget
{
    private readonly PlexApiClient _client;
    private readonly Func<CancellationToken, Task<PlexTargetConnection?>> _connection;

    public PlexPlaylistSyncTarget(
        PlexApiClient client,
        Func<CancellationToken, Task<PlexTargetConnection?>> connection)
    {
        _client = client;
        _connection = connection;
    }

    public string TargetId => "plex";

    public PlaylistTargetKind TargetKind => PlaylistTargetKind.Library;

    public async Task<TargetPlaylistLookup<string>> FindPlaylistAsync(
        string? playlistId,
        string name,
        CancellationToken cancellationToken)
    {
        var plex = await _connection(cancellationToken);
        if (plex is null)
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        var playlists = await _client.GetPlaylistsResult(
            plex.Url,
            plex.Token,
            cancellationToken: cancellationToken);
        if (playlists.Status == TargetLookupStatus.Transient)
        {
            return TargetPlaylistLookup<string>.Unavailable(playlists.HttpStatusCode);
        }

        var all = playlists.Value ?? Array.Empty<PlexPlaylist>();

        // A known id wins, but only if it is still among the account's playlists. A stale id must
        // fall through to the name match so the engine creates rather than writing to a dead one.
        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            var byId = all.FirstOrDefault(item =>
                string.Equals(item.Id, playlistId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return TargetPlaylistLookup<string>.Found(byId.Id);
            }
        }

        var match = all.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(item.Id)
            && string.Equals(item.Title?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null
            ? TargetPlaylistLookup<string>.Missing()
            : TargetPlaylistLookup<string>.Found(match.Id);
    }

    public async Task<string?> CreatePlaylistAsync(
        string name,
        string? description,
        CancellationToken cancellationToken)
    {
        // Plex creates and fills a playlist in one call, so an empty playlist is not useful here.
        // The engine reaches this only for a brand new target, and WriteMembershipAsync performs
        // the create; returning null here would make the engine report a creation failure.
        return null;
    }

    public async Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var plex = await _connection(cancellationToken);
        if (plex is null)
        {
            return TargetPlaylistItemsRead.Unreadable;
        }

        var items = await _client.GetPlaylistItemsAsync(plex.Url, plex.Token, playlistId, cancellationToken);
        if (items is null)
        {
            return TargetPlaylistItemsRead.Unreadable;
        }

        return TargetPlaylistItemsRead.Ok(
            items.Select(static item => item.Id)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToList());
    }

    public async Task<PlaylistMembershipWriteResult> WriteMembershipAsync(
        PlaylistMembershipWrite write,
        CancellationToken cancellationToken)
    {
        var plex = await _connection(cancellationToken);
        if (plex is null)
        {
            return PlaylistMembershipWriteResult.Failed(PlexConnectionNotConfiguredMessage);
        }

        var result = await _client.CreateOrUpdatePlaylistAsync(
            plex.Url,
            plex.Token,
            plex.MachineIdentifier,
            write.PlaylistName,
            write.OrderedItemIds,
            options: new PlexApiClient.PlaylistUpsertOptions(
                AppendMissingOnly: write.AppendMissingOnly,
                ExistingPlaylistId: string.IsNullOrWhiteSpace(write.PlaylistId) ? null : write.PlaylistId.Trim()),
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(result.PlaylistId))
        {
            return PlaylistMembershipWriteResult.Failed("Failed to create or update Plex playlist.");
        }

        return PlaylistMembershipWriteResult.Ok(
            write.OrderedItemIds.Count,
            write.AppendMissingOnly ? 0 : write.CurrentItemIds.Count,
            result.Complete);
    }

    private const string PlexConnectionNotConfiguredMessage = "Plex is not configured.";
}
