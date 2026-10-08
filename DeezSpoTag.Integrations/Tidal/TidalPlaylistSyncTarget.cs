using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations.Tidal;

/// <summary>The TIDAL credentials a playlist destination needs.</summary>
public sealed record TidalTargetCredentials(string AccessToken, string? CountryCode = null)
{
    public static bool IsUsable(TidalTargetCredentials? credentials)
        => credentials is not null && !string.IsNullOrWhiteSpace(credentials.AccessToken);

    /// <summary>
    /// TIDAL's endpoints are storefront-scoped and reject an unknown country, so a resolved
    /// account's country is required. Falls back to the widely valid default rather than failing
    /// a pass outright, since a playlist read is still more useful than none.
    /// </summary>
    public string Country => string.IsNullOrWhiteSpace(CountryCode) ? "US" : CountryCode!.Trim().ToUpperInvariant();
}

/// <summary>
/// The TIDAL destination, over TIDAL's JSON:API v2.
/// <para>
/// Ported from the reference's <c>TidalTarget</c>. The behaviours that are requirements rather than
/// preferences:
/// </para>
/// <list type="bullet">
/// <item>Removal addresses the playlist ITEM (<c>meta.itemId</c>), not the catalog track id. TIDAL
/// numbers each physical entry separately, so the right occurrence can be retired; addressing by
/// catalog id would take out every copy of a duplicated track.</item>
/// <item>The track list is read through the <c>relationships/items</c> endpoint rather than the
/// playlist's own tracks collection, because a playlist can retain a track after the collection
/// stops returning that catalog id. Reading the collection would silently lose it, and the
/// reconciler would then treat the track as removed from the destination.</item>
/// <item>Items are sorted by <c>itemIndex</c>, because the destination's order is its own and is
/// what the write has to reproduce.</item>
/// </list>
/// <para>
/// A write carries an idempotency key, so a retried request cannot append the same track twice.
/// </para>
/// </summary>
public sealed class TidalPlaylistSyncTarget : IPlaylistSyncTarget
{
    private const string ApiBase = "https://openapi.tidal.com/v2";

    /// <summary>
    /// The item fields the read embeds. Cover art and album are included because a playlist row
    /// without them renders as a blank tile, and artists because the title alone is ambiguous.
    /// </summary>
    private const string ItemInclude = "items,items.artists,items.albums,items.albums.coverArt";

    private readonly TargetApiTransport _transport;

    /// <summary>
    /// Supplies a current TIDAL access token, and says so when there is no usable session.
    /// <para>
    /// The stored token is short-lived. Reading it straight out of the credential store means a
    /// scheduled pass that runs the next day sends an expired token, so every unattended run fails
    /// while a manual one happens to work. The app already owns a refreshing provider for TIDAL, so
    /// the destination asks for a token through that rather than reading the stored value itself.
    /// </para>
    /// </summary>
    private readonly Func<CancellationToken, Task<TidalTargetCredentials?>> _credentials;

    public TidalPlaylistSyncTarget(
        TargetApiTransport transport,
        Func<CancellationToken, Task<TidalTargetCredentials?>> credentials)
    {
        _transport = transport;
        _credentials = credentials;
    }

    public string TargetId => "tidal";

    public PlaylistTargetKind TargetKind => PlaylistTargetKind.Platform;

    public async Task<TargetPlaylistLookup<string>> FindPlaylistAsync(
        string? playlistId,
        string name,
        CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!TidalTargetCredentials.IsUsable(credentials))
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        var headers = Headers(credentials!);

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            // The single-resource route is not owner-filtered, so it also reaches a playlist the
            // account can read but does not own.
            var byId = await _transport.GetAsync(
                $"{ApiBase}/playlists/{Uri.EscapeDataString(playlistId)}?countryCode={credentials!.Country}",
                headers,
                cancellationToken);
            if (!byId.Reached)
            {
                return TargetPlaylistLookup<string>.Unavailable(byId.Status == 0 ? null : byId.Status);
            }

            var data = byId.TryParseJson()?.ReadObject("data");
            var id = data?.ReadString("id");
            return string.IsNullOrWhiteSpace(id)
                ? TargetPlaylistLookup<string>.Missing()
                : TargetPlaylistLookup<string>.Found(id);
        }

        var wanted = name?.Trim() ?? string.Empty;
        if (wanted.Length == 0)
        {
            return TargetPlaylistLookup<string>.Missing();
        }

        // filter[owners.id]=me is the owner's own playlists, which is the collection a destination
        // is written to.
        var url = $"{ApiBase}/playlists?filter[owners.id]=me&countryCode={credentials!.Country}"
            + $"&include=coverArt&page[limit]=100";

        while (!string.IsNullOrWhiteSpace(url))
        {
            var page = await _transport.GetAsync(url, headers, cancellationToken);
            if (!page.Reached)
            {
                return TargetPlaylistLookup<string>.Unavailable(page.Status == 0 ? null : page.Status);
            }

            if (page.TryParseJson() is not { } json)
            {
                return TargetPlaylistLookup<string>.Unavailable();
            }

            foreach (var playlist in json.ReadArray("data")
                         .Where(playlist => string.Equals(
                             playlist.ReadString("attributes.name")?.Trim(),
                             wanted,
                             StringComparison.OrdinalIgnoreCase)
                             && playlist.ReadString("id") is { Length: > 0 }))
            {
                return TargetPlaylistLookup<string>.Found(playlist.ReadString("id")!);
            }

            var next = json.ReadString("links.next");
            url = string.IsNullOrWhiteSpace(next) ? null : next;
        }

        return TargetPlaylistLookup<string>.Missing();
    }

    public async Task<string?> CreatePlaylistAsync(string name, string? description, CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!TidalTargetCredentials.IsUsable(credentials))
        {
            return null;
        }

        var attributes = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["name"] = name ?? string.Empty,
            // UNLISTED rather than public: a mirrored playlist is not meant to be published.
            ["accessType"] = "UNLISTED",
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            attributes["description"] = description!;
        }

        var created = await _transport.PostAsync(
            $"{ApiBase}/playlists?countryCode={credentials!.Country}",
            jsonBody: JsonBody(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["data"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "playlists",
                    ["attributes"] = attributes,
                },
            }),
            headers: Headers(credentials),
            cancellationToken: cancellationToken);

        return created.TryParseJson()?.ReadString("data.id") is { Length: > 0 } id ? id : null;
    }

    public async Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var (ids, _) = await ReadItemsAsync(playlistId, cancellationToken);
        return ids is null
            ? TargetPlaylistItemsRead.Unreadable
            : TargetPlaylistItemsRead.Ok(ids.Select(static item => item.CatalogTrackId).ToList());
    }

    public async Task<PlaylistMembershipWriteResult> WriteMembershipAsync(
        PlaylistMembershipWrite write,
        CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!TidalTargetCredentials.IsUsable(credentials))
        {
            return PlaylistMembershipWriteResult.Failed("TIDAL is not authorized. Reconnect it in Login.");
        }

        var headers = Headers(credentials!);
        var wanted = new HashSet<string>(write.OrderedItemIds, StringComparer.Ordinal);

        if (!write.AppendMissingOnly)
        {
            var (items, _) = await ReadItemsAsync(write.PlaylistId, cancellationToken);
            if (items is null)
            {
                return PlaylistMembershipWriteResult.Failed(
                    "TIDAL playlist could not be read, so nothing was written.");
            }

            foreach (var stale in items.Where(item => !wanted.Contains(item.CatalogTrackId)))
            {
                if (string.IsNullOrWhiteSpace(stale.EntryId))
                {
                    // Without the item id the deletion would address the catalog track and take out
                    // every copy. Refuse rather than lose one.
                    return PlaylistMembershipWriteResult.Failed(
                        "TIDAL did not return the playlist entry id for a track that is no longer "
                        + "wanted, so nothing was written.");
                }

                var removed = await _transport.DeleteAsync(
                    $"{ApiBase}/playlists/{Uri.EscapeDataString(write.PlaylistId)}/relationships/items"
                    + $"?countryCode={credentials!.Country}",
                    headers: WithIdempotencyKey(headers),
                    cancellationToken: cancellationToken,
                    jsonBody: JsonBody(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["data"] = new[]
                        {
                            new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["type"] = "tracks",
                                ["id"] = stale.CatalogTrackId,
                                ["meta"] = new Dictionary<string, object>(StringComparer.Ordinal)
                                {
                                    ["itemId"] = stale.EntryId!,
                                },
                            },
                        },
                    }));

                if (!removed.Success)
                {
                    return PlaylistMembershipWriteResult.Failed(
                        removed.IsAuthRejection
                            ? "TIDAL rejected the removal. Reconnect it in Login."
                            : "TIDAL failed to write the playlist.");
                }
            }
        }

        // One request per track, in order. TIDAL appends with its own added-at stamp and offers no
        // positional insert, so a batched or continued-after-failure write would invert the order
        // with no way to put it back.
        // Only what is actually missing. Sending every id again appends a duplicate of the whole
        // playlist on each pass.
        var present = new HashSet<string>(write.CurrentItemIds, StringComparer.Ordinal);
        var toAdd = write.AppendMissingOnly
            ? write.OrderedItemIds.Where(id => !present.Contains(id)).ToList()
            : write.OrderedItemIds.ToList();

        var added = 0;
        foreach (var trackId in toAdd)
        {
            if (string.IsNullOrWhiteSpace(trackId))
            {
                continue;
            }

            var response = await _transport.PostAsync(
                $"{ApiBase}/playlists/{Uri.EscapeDataString(write.PlaylistId)}/relationships/items"
                + $"?countryCode={credentials!.Country}",
                jsonBody: JsonBody(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["data"] = new[]
                    {
                        new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["type"] = "tracks",
                            ["id"] = trackId,
                        },
                    },
                }),
                headers: WithIdempotencyKey(headers),
                cancellationToken: cancellationToken);

            if (response.Success)
            {
                added++;
                continue;
            }

            return PlaylistMembershipWriteResult.Failed(
                response.IsAuthRejection
                    ? "TIDAL rejected the addition. Reconnect it in Login."
                    : $"TIDAL failed to add a track after {added}. Nothing after it was written, so the order is not inverted.");
        }

        return PlaylistMembershipWriteResult.Ok(added, 0);
    }

    private sealed record TidalPlaylistItem(string EntryId, string CatalogTrackId);

    /// <summary>
    /// The playlist's items as (entry id, catalog track id) pairs, or null when the read failed.
    /// Read through the relationships route for the reason documented on the class.
    /// </summary>
    private async Task<(IReadOnlyList<TidalPlaylistItem>? Items, bool Failed)> ReadItemsAsync(
        string playlistId,
        CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!TidalTargetCredentials.IsUsable(credentials))
        {
            return (null, true);
        }

        var headers = Headers(credentials!);
        var items = new List<TidalPlaylistItem>();
        var url = $"{ApiBase}/playlists/{Uri.EscapeDataString(playlistId)}/relationships/items"
            + $"?countryCode={credentials!.Country}&sort=itemIndex&include={ItemInclude}&page[limit]=100";

        while (!string.IsNullOrWhiteSpace(url))
        {
            var page = await _transport.GetAsync(url, headers, cancellationToken);
            if (!page.Reached)
            {
                return (null, true);
            }

            if (page.TryParseJson() is not { } json)
            {
                return (null, true);
            }

            var rows = json.ReadArray("data");
            foreach (var row in rows)
            {
                if (row.ReadString("id") is not { Length: > 0 } trackId)
                {
                    return (null, true);
                }

                items.Add(new TidalPlaylistItem(
                    row.ReadString("meta.itemId") ?? string.Empty,
                    trackId));
            }

            var next = json.ReadString("links.next");
            url = string.IsNullOrWhiteSpace(next) ? null : next;

            // A page that returns nothing while claiming more would silently truncate the
            // membership and make the caller remove tracks it never saw.
            if (rows.Count == 0)
            {
                return (null, json.ReadString("links.next") is { Length: > 0 });
            }
        }

        return (items, false);
    }

    private async Task<TidalTargetCredentials?> ResolveAsync(CancellationToken cancellationToken)
        => await _credentials(cancellationToken);

    private static IReadOnlyDictionary<string, string> Headers(TidalTargetCredentials credentials)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + credentials.AccessToken,
            // TIDAL's API speaks vendor-specific JSON:API rather than plain JSON.
            ["Accept"] = "application/vnd.api+json",
            ["Content-Type"] = "application/vnd.api+json",
        };

    /// <summary>
    /// Adds the idempotency key a write needs. Without it a retried request appends the same track
    /// a second time, and a track that was meant to appear once appears twice.
    /// </summary>
    private static IReadOnlyDictionary<string, string> WithIdempotencyKey(
        IReadOnlyDictionary<string, string> headers)
    {
        var withKey = new Dictionary<string, string>(headers, StringComparer.Ordinal)
        {
            ["Idempotency-Key"] = Guid.NewGuid().ToString(),
        };
        return withKey;
    }

    private static System.Text.Json.JsonElement JsonBody(object value)
        => System.Text.Json.JsonSerializer.SerializeToElement(value);
}
