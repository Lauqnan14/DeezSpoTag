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

    public async Task<PlaylistMetadataWriteResult> WriteMetadataAsync(PlaylistMetadataWrite write, CancellationToken cancellationToken)
    {
        var token = await _accessToken(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(token) ? PlaylistMetadataWriteResult.Failed(write, "YouTube Music is not connected.")
            : await _client.WritePlaylistMetadataAsync(token, write, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a YouTube access token is stored, answered without calling the API.</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken)
        => !string.IsNullOrWhiteSpace(await _accessToken(cancellationToken).ConfigureAwait(false));

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
        // All of the account's playlists, not the first page of them: a playlist created by an
        // earlier pass but sitting on page two would otherwise be created a second time.
        var playlists = await _client.GetMyPlaylistsAsync(
            token,
            int.MaxValue,
            cancellationToken: cancellationToken);
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

        // The whole membership, not one page: a 50-item view of a 100-item playlist is a read that
        // looks complete enough to mirror against and then removes the other half.
        var items = await _client.GetPlaylistItemsAsync(
            token,
            playlistId,
            int.MaxValue,
            cancellationToken: cancellationToken);
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

        // The write needs the destination's video ids, not the caller's ids: ReadItemIdsAsync
        // returns PLAYLIST ITEM ids for this destination, because that is the only handle a
        // removal can address. So the membership is re-read here in append mode too rather than
        // trusting write.CurrentItemIds, which is the wrong namespace to diff against.
        var current = await _client.GetPlaylistItemsAsync(
            token,
            write.PlaylistId,
            int.MaxValue,
            cancellationToken: cancellationToken);
        if (current is null)
        {
            return PlaylistMembershipWriteResult.Failed(
                "YouTube Music playlist could not be read, so nothing was written.");
        }

        var removed = 0;
        if (!write.AppendMissingOnly)
        {
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

        // Only what is actually missing, in append mode AND in mirror mode. YouTube permits
        // duplicates, so re-sending ids the playlist already holds appends a second copy of the
        // whole playlist and the pass never converges. The mode decides only whether the removals
        // above may run.
        var toAdd = PlaylistMembershipDiff.MissingAdditions(
            write.OrderedItemIds,
            current.Select(static item => item.VideoId ?? string.Empty));

        if (toAdd.Count > 0
            && !await _client.AddPlaylistItemsAsync(token, write.PlaylistId, toAdd, cancellationToken))
        {
            return PlaylistMembershipWriteResult.Failed("YouTube Music failed to write the playlist.");
        }

        return PlaylistMembershipWriteResult.Ok(toAdd.Count, removed);
    }
}
