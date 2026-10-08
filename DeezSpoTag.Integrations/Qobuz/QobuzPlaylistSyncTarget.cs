using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations.Qobuz;

/// <summary>The Qobuz credentials a playlist destination needs.</summary>
public sealed record QobuzTargetCredentials(string AppId, string AuthToken, string? UserId = null)
{
    public static bool IsUsable(QobuzTargetCredentials? credentials)
        => credentials is not null
            && !string.IsNullOrWhiteSpace(credentials.AppId)
            && !string.IsNullOrWhiteSpace(credentials.AuthToken);
}

/// <summary>
/// The Qobuz destination, over Qobuz's own web API at <c>api.json/0.2</c>.
/// <para>
/// Ported from the reference's <c>QobuzTarget</c>. Two behaviours there are requirements and are
/// preserved:
/// </para>
/// <list type="bullet">
/// <item>Removal needs the <c>playlist_track_id</c>, not the catalog track id. Qobuz gives every
/// physical playlist entry its own id, so the right occurrence can be retired; a target that
/// resolved removal by catalog id would take out every copy of a duplicated track. A missing entry
/// id is an error, never a silent skip, because a skip means the playlist ends up holding
/// something the user removed.</item>
/// <item>Tracks are added one request at a time with duplicate suppression on, which is what
/// preserves the source ordering and keeps a re-run idempotent.</item>
/// </list>
/// <para>
/// This endpoint is private and undocumented, and can change without notice. A failure is reported
/// as the destination being unavailable rather than as an empty playlist, so a broken API degrades
/// into "cannot sync there" instead of deleting a user's playlist.
/// </para>
/// </summary>
public sealed class QobuzPlaylistSyncTarget : IPlaylistSyncTarget
{
    private const string ApiBase = "https://www.qobuz.com/api.json/0.2";

    private readonly TargetApiTransport _transport;
    private readonly Func<CancellationToken, Task<QobuzTargetCredentials?>> _credentials;

    public QobuzPlaylistSyncTarget(
        TargetApiTransport transport,
        Func<CancellationToken, Task<QobuzTargetCredentials?>> credentials)
    {
        _transport = transport;
        _credentials = credentials;
    }

    public string TargetId => "qobuz";

    public PlaylistTargetKind TargetKind => PlaylistTargetKind.Platform;

    public async Task<TargetPlaylistLookup<string>> FindPlaylistAsync(
        string? playlistId,
        string name,
        CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!QobuzTargetCredentials.IsUsable(credentials))
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        var headers = Headers(credentials!);

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            // playlist/get is not scoped to the caller's own playlists, so one call answers both
            // "is it there" and "what is it called".
            var byId = await _transport.GetAsync(
                $"{ApiBase}/playlist/get?playlist_id={Uri.EscapeDataString(playlistId)}&extra=tracks",
                headers,
                cancellationToken);
            if (!byId.Reached)
            {
                return TargetPlaylistLookup<string>.Unavailable(byId.Status == 0 ? null : byId.Status);
            }

            if (byId.IsAuthRejection)
            {
                return TargetPlaylistLookup<string>.Unavailable(byId.Status);
            }

            var id = byId.TryParseJson()?.ReadString("id");
            return string.IsNullOrWhiteSpace(id)
                ? TargetPlaylistLookup<string>.Missing()
                : TargetPlaylistLookup<string>.Found(id);
        }

        var wanted = name?.Trim() ?? string.Empty;
        if (wanted.Length == 0)
        {
            return TargetPlaylistLookup<string>.Missing();
        }

        var offset = 0;
        var total = int.MaxValue;
        while (offset < total)
        {
            var userClause = string.IsNullOrWhiteSpace(credentials!.UserId)
                ? string.Empty
                : $"&user_id={Uri.EscapeDataString(credentials.UserId!)}";
            var page = await _transport.GetAsync(
                $"{ApiBase}/playlist/getUserPlaylists?limit=100&offset={offset}{userClause}",
                headers,
                cancellationToken);
            if (!page.Reached)
            {
                return TargetPlaylistLookup<string>.Unavailable(page.Status == 0 ? null : page.Status);
            }

            if (page.TryParseJson() is not { } json)
            {
                return TargetPlaylistLookup<string>.Unavailable();
            }

            // Qobuz returns the container under "playlists", or at the root for some responses.
            var container = json.ReadObject("playlists") ?? json;
            var items = container.ReadArray("items");
            foreach (var matchId in items
                         .Where(playlist => string.Equals(playlist.ReadString("name")?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
                             && playlist.ReadString("id") is { Length: > 0 })
                         .Select(playlist => playlist.ReadString("id")!))
            {
                return TargetPlaylistLookup<string>.Found(matchId);
            }

            if (container.ReadString("total") is { Length: > 0 } totalText
                && int.TryParse(totalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                total = parsed;
            }

            if (items.Count == 0)
            {
                return TargetPlaylistLookup<string>.Missing();
            }

            offset += items.Count;
        }

        return TargetPlaylistLookup<string>.Missing();
    }

    public async Task<string?> CreatePlaylistAsync(string name, string? description, CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!QobuzTargetCredentials.IsUsable(credentials))
        {
            return null;
        }

        var fields = new Dictionary<string, string>
        {
            ["name"] = name ?? string.Empty,
            ["is_public"] = "false",
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            fields["description"] = description!;
        }

        var created = await _transport.PostAsync(
            $"{ApiBase}/playlist/create",
            formFields: fields,
            headers: Headers(credentials!),
            cancellationToken: cancellationToken);

        return created.TryParseJson()?.ReadString("id") is { Length: > 0 } id ? id : null;
    }

    public async Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!QobuzTargetCredentials.IsUsable(credentials))
        {
            return TargetPlaylistItemsRead.Unreadable;
        }

        var ids = new List<string>();
        var offset = 0;
        var total = int.MaxValue;

        while (offset < total)
        {
            var page = await _transport.GetAsync(
                $"{ApiBase}/playlist/get?playlist_id={Uri.EscapeDataString(playlistId)}"
                + $"&extra=tracks&limit=100&offset={offset}",
                Headers(credentials!),
                cancellationToken);
            if (!page.Reached)
            {
                return TargetPlaylistItemsRead.Unreadable;
            }

            if (page.TryParseJson() is not { } json)
            {
                return TargetPlaylistItemsRead.Unreadable;
            }

            var container = json.ReadObject("tracks") ?? json;
            var items = container.ReadArray("items");

            // A track with no id cannot be addressed later, so a page containing one is a broken
            // read rather than a short playlist. Reporting it as a partial membership would make
            // the caller remove every track it never saw.
            if (items.Any(static item => string.IsNullOrWhiteSpace(item.ReadString("id"))))
            {
                return TargetPlaylistItemsRead.Unreadable;
            }

            ids.AddRange(items
                .Select(static item => item.ReadString("id"))
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id!));

            if (container.ReadString("total") is { Length: > 0 } totalText
                && int.TryParse(totalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                total = parsed;
            }

            if (items.Count == 0)
            {
                return total > offset
                    ? TargetPlaylistItemsRead.Unreadable
                    : TargetPlaylistItemsRead.Ok(ids);
            }

            offset += items.Count;
        }

        return TargetPlaylistItemsRead.Ok(ids);
    }

    public async Task<PlaylistMembershipWriteResult> WriteMembershipAsync(
        PlaylistMembershipWrite write,
        CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!QobuzTargetCredentials.IsUsable(credentials))
        {
            return PlaylistMembershipWriteResult.Failed("Qobuz is not authorized. Reconnect it in Login.");
        }

        var headers = Headers(credentials!);
        var wanted = new HashSet<string>(write.OrderedItemIds, StringComparer.Ordinal);

        if (!write.AppendMissingOnly)
        {
            // Qobuz deletes by playlist_track_id, the per-entry id. The caller's current ids are
            // catalog ids, so the entry ids are re-read here rather than guessed from them.
            var entries = await ReadEntryIdsAsync(write.PlaylistId, credentials!, cancellationToken);
            if (entries is null)
            {
                return PlaylistMembershipWriteResult.Failed(
                    "Qobuz playlist could not be read, so nothing was written.");
            }

            foreach (var stale in entries.Where(entry => !wanted.Contains(entry.CatalogTrackId)))
            {
                var removed = await _transport.PostAsync(
                    $"{ApiBase}/playlist/deleteTracks",
                    formFields: new Dictionary<string, string>
                    {
                        ["playlist_id"] = write.PlaylistId,
                        ["playlist_track_ids"] = stale.EntryId,
                    },
                    headers: headers,
                    cancellationToken: cancellationToken);

                if (!removed.Success)
                {
                    return PlaylistMembershipWriteResult.Failed(
                        removed.IsAuthRejection
                            ? "Qobuz rejected the removal. Reconnect it in Login."
                            : "Qobuz failed to write the playlist.");
                }
            }
        }

        // Only what is actually missing. Re-sending ids the playlist already holds is what a batch
        // does, and although no_duplicate stops the insert, the request itself is wasted and the
        // ordering guarantee below depends on sending each id exactly once.
        var present = new HashSet<string>(write.CurrentItemIds, StringComparer.Ordinal);
        var toAdd = write.AppendMissingOnly
            ? write.OrderedItemIds.Where(id => !present.Contains(id)).ToList()
            : write.OrderedItemIds.ToList();

        // One request per track, with duplicate suppression on. A batched append can land out of
        // order, and Qobuz offers no positional insert to undo that.
        var added = 0;
        foreach (var trackId in toAdd)
        {
            if (string.IsNullOrWhiteSpace(trackId))
            {
                continue;
            }

            var response = await _transport.PostAsync(
                $"{ApiBase}/playlist/addTracks",
                formFields: new Dictionary<string, string>
                {
                    ["playlist_id"] = write.PlaylistId,
                    ["track_ids"] = trackId,
                    ["no_duplicate"] = "true",
                },
                headers: headers,
                cancellationToken: cancellationToken);

            if (response.Success)
            {
                added++;
                continue;
            }

            // Stop at the first rejection: the destination appends, so continuing would give the
            // tracks after it earlier added-at stamps than the ones that belong before it.
            return PlaylistMembershipWriteResult.Failed(
                response.IsAuthRejection
                    ? "Qobuz rejected the addition. Reconnect it in Login."
                    : $"Qobuz failed to add a track after {added}. Nothing after it was written, so the order is not inverted.");
        }

        return PlaylistMembershipWriteResult.Ok(added, 0);
    }

    private sealed record QobuzPlaylistEntry(string EntryId, string CatalogTrackId);

    /// <summary>
    /// The playlist's entries as (playlist_track_id, catalog track id) pairs, or null when the read
    /// failed. The entry id is what deletion addresses, so it cannot be derived from the catalog id.
    /// </summary>
    private async Task<IReadOnlyList<QobuzPlaylistEntry>?> ReadEntryIdsAsync(
        string playlistId,
        QobuzTargetCredentials credentials,
        CancellationToken cancellationToken)
    {
        var entries = new List<QobuzPlaylistEntry>();
        var offset = 0;
        var total = int.MaxValue;

        while (offset < total)
        {
            var page = await _transport.GetAsync(
                $"{ApiBase}/playlist/get?playlist_id={Uri.EscapeDataString(playlistId)}"
                + $"&extra=tracks&limit=100&offset={offset}",
                Headers(credentials),
                cancellationToken);
            if (!page.Reached || page.TryParseJson() is not { } json)
            {
                return null;
            }

            var container = json.ReadObject("tracks") ?? json;
            var items = container.ReadArray("items");
            foreach (var item in items)
            {
                var entryId = item.ReadString("playlist_track_id");
                var trackId = item.ReadString("id");
                if (string.IsNullOrWhiteSpace(entryId) || string.IsNullOrWhiteSpace(trackId))
                {
                    // Without both ids the entry cannot be retired individually, and deleting by
                    // catalog id would take every copy. Reported as an unreadable playlist instead.
                    return null;
                }

                entries.Add(new QobuzPlaylistEntry(entryId!, trackId!));
            }

            if (container.ReadString("total") is { Length: > 0 } totalText
                && int.TryParse(totalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                total = parsed;
            }

            if (items.Count == 0)
            {
                return entries.Count > 0 || offset >= total ? entries : null;
            }

            offset += items.Count;
        }

        return entries;
    }

    private async Task<QobuzTargetCredentials?> ResolveAsync(CancellationToken cancellationToken)
        => await _credentials(cancellationToken);

    private static IReadOnlyDictionary<string, string> Headers(QobuzTargetCredentials credentials)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-App-Id"] = credentials.AppId,
            ["X-User-Auth-Token"] = credentials.AuthToken,
        };
}
