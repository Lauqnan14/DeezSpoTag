using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations.Navidrome;

/// <summary>The Navidrome connection a target needs. Lives here so the adapter needs no Web dependency.</summary>
public sealed record NavidromeTargetConnection(string Url, string Username, string Password);

/// <summary>
/// The Navidrome destination. Navidrome is a whole-playlist upsert, the same shape as Plex, so
/// <see cref="WriteMembershipAsync"/> delegates to the same call the writer used to make and does
/// not need the current-item list.
/// </summary>
public sealed class NavidromePlaylistSyncTarget : IPlaylistSyncTarget
{
    private readonly NavidromeApiClient _client;
    private readonly Func<CancellationToken, Task<NavidromeTargetConnection?>> _connection;

    public NavidromePlaylistSyncTarget(
        NavidromeApiClient client,
        Func<CancellationToken, Task<NavidromeTargetConnection?>> connection)
    {
        _client = client;
        _connection = connection;
    }

    public string TargetId => "navidrome";

    public PlaylistTargetKind TargetKind => PlaylistTargetKind.Library;

    public async Task<TargetPlaylistLookup<string>> FindPlaylistAsync(
        string? playlistId,
        string name,
        CancellationToken cancellationToken)
    {
        var navidrome = await _connection(cancellationToken);
        if (navidrome is null)
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            var byId = await _client.GetPlaylistResult(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
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
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
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

    private string? _createFailureDetail;

    public string? CreateFailureDetail => _createFailureDetail;

    public Task<string?> CreatePlaylistAsync(
        string name,
        string? description,
        CancellationToken cancellationToken)
        => CreateMissingPlaylistAsync(name, description, Array.Empty<string>(), cancellationToken);

    public async Task<string?> CreateMissingPlaylistAsync(
        string name,
        string? description,
        IReadOnlyList<string> orderedItemIds,
        CancellationToken cancellationToken)
    {
        _createFailureDetail = null;
        var navidrome = await _connection(cancellationToken);
        if (navidrome is null)
        {
            _createFailureDetail = "Navidrome is not configured.";
            return null;
        }

        if (orderedItemIds.Count == 0)
        {
            _createFailureDetail = "Navidrome cannot create a playlist until it has tracks to put in it.";
            return null;
        }

        var playlistId = await _client.CreateOrUpdatePlaylistAsync(
            navidrome,
            name,
            orderedItemIds,
            existingPlaylistId: null,
            appendMissingOnly: false,
            cancellationToken,
            playlistComment: description);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            _createFailureDetail = "Navidrome did not return a playlist id.";
            return null;
        }

        return playlistId;
    }

    public async Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var navidrome = await _connection(cancellationToken);
        if (navidrome is null)
        {
            return TargetPlaylistItemsRead.Unreadable;
        }

        var details = await _client.GetPlaylistAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            playlistId,
            cancellationToken);
        if (details is null)
        {
            return TargetPlaylistItemsRead.Unreadable;
        }

        return TargetPlaylistItemsRead.Ok(
            details.Entries.Select(static entry => entry.ItemId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToList());
    }

    public async Task<PlaylistMembershipWriteResult> WriteMembershipAsync(
        PlaylistMembershipWrite write,
        CancellationToken cancellationToken)
    {
        var navidrome = await _connection(cancellationToken);
        if (navidrome is null)
        {
            return PlaylistMembershipWriteResult.Failed("Navidrome is not configured.");
        }

        var playlistId = await _client.CreateOrUpdatePlaylistAsync(
            new NavidromeTargetConnection(navidrome.Url, navidrome.Username, navidrome.Password),
            write.PlaylistName,
            write.OrderedItemIds,
            string.IsNullOrWhiteSpace(write.PlaylistId) ? null : write.PlaylistId,
            write.AppendMissingOnly,
            cancellationToken,
            write.PlaylistName);

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return PlaylistMembershipWriteResult.Failed("Failed to create or update the Navidrome playlist.");
        }

        return PlaylistMembershipWriteResult.Ok(
            write.OrderedItemIds.Count,
            write.AppendMissingOnly ? 0 : write.CurrentItemIds.Count);
    }
}
