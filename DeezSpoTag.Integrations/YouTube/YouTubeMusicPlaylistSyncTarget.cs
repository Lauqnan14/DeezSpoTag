using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations.YouTube;

/// <summary>
/// The YouTube Music destination. Wraps <see cref="YouTubeDataApiClient"/> in the target contract
/// without changing what it does: the same create, read, add, and delete calls in the same order.
/// </summary>
public sealed class YouTubeMusicPlaylistSyncTarget : IPlaylistSyncTarget
{
    private readonly YouTubeDataApiClient _client;
    private readonly Func<CancellationToken, Task<string?>> _accessToken;

    public YouTubeMusicPlaylistSyncTarget(
        YouTubeDataApiClient client,
        Func<CancellationToken, Task<string?>> accessToken)
    {
        _client = client;
        _accessToken = accessToken;
    }

    public string TargetId => "ytmusic";

    public PlaylistTargetKind TargetKind => PlaylistTargetKind.Platform;

    public async Task<TargetPlaylistLookup<string>> FindPlaylistAsync(
        string? playlistId,
        string name,
        CancellationToken cancellationToken)
    {
        var token = await _accessToken(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            var byId = await _client.GetPlaylistAsync(token, playlistId, cancellationToken);
            return byId is null
                ? TargetPlaylistLookup<string>.Missing()
                : TargetPlaylistLookup<string>.Found(byId.Id);
        }

        // No id yet: find by name so a re-run reuses the playlist an earlier pass created rather
        // than creating a second one with the same title.
        var playlists = await _client.GetMyPlaylistsAsync(token, cancellationToken: cancellationToken);
        if (playlists is null)
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        var match = playlists.FirstOrDefault(item =>
            string.Equals(item.Title?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null
            ? TargetPlaylistLookup<string>.Missing()
            : TargetPlaylistLookup<string>.Found(match.Id);
    }

    public async Task<string?> CreatePlaylistAsync(string name, string? description, CancellationToken cancellationToken)
    {
        var token = await _accessToken(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var created = await _client.CreatePlaylistAsync(token, name, description, cancellationToken: cancellationToken);
        return created is { Success: true } && !string.IsNullOrWhiteSpace(created.PlaylistId)
            ? created.PlaylistId
            : null;
    }

    public async Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var token = await _accessToken(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return TargetPlaylistItemsRead.Unreadable;
        }

        var items = await _client.GetPlaylistItemsAsync(token, playlistId, cancellationToken: cancellationToken);
        if (items is null)
        {
            // A failed read is not an empty playlist. Reporting it as empty would make a mirror
            // sync believe the target holds nothing and delete every track on it.
            return TargetPlaylistItemsRead.Unreadable;
        }

        return TargetPlaylistItemsRead.Ok(
            items.Select(item => item.PlaylistItemId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList());
    }

    public async Task<PlaylistMembershipWriteResult> WriteMembershipAsync(
        PlaylistMembershipWrite write,
        CancellationToken cancellationToken)
    {
        var token = await _accessToken(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return PlaylistMembershipWriteResult.Failed("YouTube Music authorization expired. Reconnect it in Login.");
        }

        var removed = 0;
        if (!write.AppendMissingOnly)
        {
            var current = await _client.GetPlaylistItemsAsync(token, write.PlaylistId, cancellationToken: cancellationToken);
            if (current is null)
            {
                return PlaylistMembershipWriteResult.Failed(
                    "YouTube Music playlist could not be read, so nothing was written.");
            }

            var wanted = new HashSet<string>(write.OrderedItemIds, StringComparer.Ordinal);
            foreach (var item in current)
            {
                if (string.IsNullOrWhiteSpace(item.PlaylistItemId)
                    || wanted.Contains(item.VideoId ?? string.Empty))
                {
                    continue;
                }

                if (!await _client.DeletePlaylistItemAsync(token, item.PlaylistItemId, cancellationToken))
                {
                    return PlaylistMembershipWriteResult.Failed("YouTube Music failed to write the playlist.");
                }

                removed++;
            }
        }

        if (!await _client.AddPlaylistItemsAsync(token, write.PlaylistId, write.OrderedItemIds, cancellationToken))
        {
            return PlaylistMembershipWriteResult.Failed("YouTube Music failed to write the playlist.");
        }

        return PlaylistMembershipWriteResult.Ok(write.OrderedItemIds.Count, removed);
    }
}
