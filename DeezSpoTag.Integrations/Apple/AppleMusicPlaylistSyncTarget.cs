using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace DeezSpoTag.Integrations.Apple;

/// <summary>The Apple Music credentials a playlist destination needs.</summary>
public sealed record AppleTargetCredentials(string AuthorizationToken, string MediaUserToken, string? Storefront = null)
{
    public static bool IsUsable(AppleTargetCredentials? credentials)
        => credentials is not null
            && !string.IsNullOrWhiteSpace(credentials.AuthorizationToken)
            && !string.IsNullOrWhiteSpace(credentials.MediaUserToken);

    /// <summary>Where the account's catalog lives. The default is only a last resort.</summary>
    public string Store => string.IsNullOrWhiteSpace(Storefront) ? "us" : Storefront!.Trim().ToLowerInvariant();
}

/// <summary>
/// The Apple Music destination, over the web player's library API.
/// <para>
/// Ported from the reference's <c>AppleTarget</c>. This is deliberately NOT the app's existing
/// <c>AppleMusicCatalogService</c>: the Apple Music <i>catalog</i> API is read-only and cannot
/// create or modify a playlist. Writing needs the signed-in library surface, reached with the web
/// player's bearer token plus a Media-User-Token.
/// </para>
/// <para>
/// The behaviours that are requirements:
/// </para>
/// <list type="bullet">
/// <item>Tracks are added ONE PER REQUEST. Apple stamps each added item with its own added-at time
/// and offers no positional insert, so a batched append that lands out of order cannot be
/// repaired afterwards.</item>
/// <item>A failure part-way through stops the pass rather than continuing. Skipping a rejected
/// track would give every track after it an earlier added-at stamp than the ones before it.</item>
/// <item>Apple's error 40015 is a paid-subscription denial, not an expired token. It is reported as
/// "needs a subscription" so the user is not told to reconnect a session that is perfectly valid.</item>
/// </list>
/// </summary>
public sealed class AppleMusicPlaylistSyncTarget : IPlaylistSyncTarget
{
    /// <summary>
    /// The web player's API. Deliberately not <c>api.music.apple.com</c>: that is the catalog
    /// surface and cannot write to a library playlist.
    /// </summary>
    private const string AmpApiBase = "https://amp-api.music.apple.com/v1";

    /// <summary>
    /// Apple's error code for a valid session without the paid Cloud Library privilege. Distinct
    /// from an expired token, and the two need different things from the user.
    /// </summary>
    private const int CloudLibraryDeniedCode = 40015;

    private readonly TargetApiTransport _transport;
    private readonly Func<CancellationToken, Task<AppleTargetCredentials?>> _credentials;

    public AppleMusicPlaylistSyncTarget(
        TargetApiTransport transport,
        Func<CancellationToken, Task<AppleTargetCredentials?>> credentials)
    {
        _transport = transport;
        _credentials = credentials;
    }

    public string TargetId => "applemusic";

    public PlaylistTargetKind TargetKind => PlaylistTargetKind.Platform;

    public async Task<TargetPlaylistLookup<string>> FindPlaylistAsync(
        string? playlistId,
        string name,
        CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!AppleTargetCredentials.IsUsable(credentials))
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            var byId = await _transport.GetAsync(
                $"{AmpApiBase}/me/library/playlists/{Uri.EscapeDataString(playlistId)}",
                headers: Headers(credentials!),
                cancellationToken: cancellationToken);
            if (!byId.Reached)
            {
                return TargetPlaylistLookup<string>.Unavailable(byId.Status == 0 ? null : byId.Status);
            }

            if (byId.TryParseJson() is not { } json)
            {
                return TargetPlaylistLookup<string>.Unavailable();
            }

            var data = json.ReadArray("data");
            var id = data.Count > 0 ? data[0].ReadString("id") : null;
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
            var page = await _transport.GetAsync(
                $"{AmpApiBase}/me/library/playlists?limit=100&offset={offset}&extend=tags",
                headers: Headers(credentials!),
                cancellationToken: cancellationToken);
            if (!page.Reached)
            {
                return TargetPlaylistLookup<string>.Unavailable(page.Status == 0 ? null : page.Status);
            }

            if (page.TryParseJson() is not { } json)
            {
                return TargetPlaylistLookup<string>.Unavailable();
            }

            var rows = json.ReadArray("data");
            foreach (var playlist in rows)
            {
                if (string.Equals(
                        playlist.ReadString("attributes.name")?.Trim(),
                        wanted,
                        StringComparison.OrdinalIgnoreCase)
                    && playlist.ReadString("id") is { Length: > 0 } matchId)
                {
                    return TargetPlaylistLookup<string>.Found(matchId);
                }
            }

            total = json.ReadInt("meta.total");
            if (rows.Count == 0)
            {
                // `next` present with no rows means an incomplete listing, not an empty library.
                return json.ReadString("next") is { Length: > 0 }
                    ? TargetPlaylistLookup<string>.Unavailable()
                    : TargetPlaylistLookup<string>.Missing();
            }

            offset += rows.Count;
        }

        return TargetPlaylistLookup<string>.Missing();
    }

    public async Task<string?> CreatePlaylistAsync(string name, string? description, CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!AppleTargetCredentials.IsUsable(credentials))
        {
            return null;
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = name ?? string.Empty };
        if (!string.IsNullOrWhiteSpace(description))
        {
            attributes["description"] = description!;
        }

        var created = await _transport.PostAsync(
            $"{AmpApiBase}/me/library/playlists",
            jsonBody: JsonBody(new Dictionary<string, object>(StringComparer.Ordinal) { ["attributes"] = attributes }),
            headers: Headers(credentials!),
            cancellationToken: cancellationToken);

        return created.TryParseJson()?.ReadString("data.0.id") is { Length: > 0 } id ? id : null;
    }

    /// <summary>
    /// One track as Apple represents it, keeping the two identities apart.
    /// <para>
    /// <c>LibrarySongId</c> addresses the signed-in library and is what a removal must name.
    /// <c>CatalogSongId</c> addresses the public catalog and is what an addition must name. They are
    /// different namespaces, and conflating them is destructive: a mirror pass compares the catalog
    /// ids it wants against library ids it read, finds no match for any of them, and concludes every
    /// existing track is unwanted.
    /// </para>
    /// </summary>
    private sealed record ApplePlaylistEntry(string LibrarySongId, string? CatalogSongId);

    public async Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var entries = await ReadEntriesAsync(playlistId, cancellationToken);
        if (entries is null)
        {
            return TargetPlaylistItemsRead.Unreadable;
        }

        return TargetPlaylistItemsRead.Ok(entries.Select(static entry => entry.LibrarySongId).ToList());
    }

    private async Task<IReadOnlyList<ApplePlaylistEntry>?> ReadEntriesAsync(
        string playlistId,
        CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!AppleTargetCredentials.IsUsable(credentials))
        {
            return null;
        }

        var entries = new List<ApplePlaylistEntry>();
        var offset = 0;
        var total = int.MaxValue;

        while (offset < total)
        {
            var page = await _transport.GetAsync(
                $"{AmpApiBase}/me/library/playlists/{Uri.EscapeDataString(playlistId)}/tracks?limit=100&offset={offset}",
                headers: Headers(credentials!),
                cancellationToken: cancellationToken);

            // An empty Apple playlist 404s this endpoint. That is an empty playlist, not a failure,
            // and it is the one case where reporting "empty" is correct.
            if (page.Status == (int)HttpStatusCode.NotFound)
            {
                return offset == 0
                    ? new List<ApplePlaylistEntry>()
                    : null;
            }

            if (!page.Reached)
            {
                return null;
            }

            if (page.TryParseJson() is not { } json)
            {
                return null;
            }

            var rows = json.ReadArray("data");
            foreach (var row in rows)
            {
                // Without the library song id the entry cannot be retired at all.
                if (row.ReadString("id") is not { Length: > 0 } librarySongId)
                {
                    return null;
                }

                entries.Add(new ApplePlaylistEntry(
                    librarySongId,
                    // Apple prefixes catalog ids with "i." and library ids with "p.". Anything that
                    // is not a catalog id is recorded as absent rather than assumed.
                    row.ReadString("attributes.playParameters.catalogId") is { Length: > 0 } catalogId
                            && catalogId.StartsWith("i.", StringComparison.Ordinal)
                        ? catalogId
                        : null));
            }

            total = json.ReadInt("meta.total");
            if (rows.Count == 0)
            {
                return json.ReadString("next") is { Length: > 0 } && total > offset
                    ? null
                    : entries;
            }

            offset += rows.Count;
        }

        return entries;
    }

    public async Task<PlaylistMembershipWriteResult> WriteMembershipAsync(
        PlaylistMembershipWrite write,
        CancellationToken cancellationToken)
    {
        var credentials = await ResolveAsync(cancellationToken);
        if (!AppleTargetCredentials.IsUsable(credentials))
        {
            return PlaylistMembershipWriteResult.Failed("Apple Music is not authorized. Reconnect it in Login.");
        }

        var headers = Headers(credentials!);
        var tracksUrl = $"{AmpApiBase}/me/library/playlists/{Uri.EscapeDataString(write.PlaylistId)}/tracks";

        // The destination's current rows, in Apple's own two identities. A removal must name the
        // library id; an addition names the catalog id.
        var entries = await ReadEntriesAsync(write.PlaylistId, cancellationToken);
        if (entries is null)
        {
            return PlaylistMembershipWriteResult.Failed(
                "Apple Music playlist could not be read, so nothing was written.");
        }

        var presentCatalogIds = entries
            .Where(static entry => entry.CatalogSongId is { Length: > 0 })
            .Select(static entry => entry.CatalogSongId!)
            .ToHashSet(StringComparer.Ordinal);

        if (!write.AppendMissingOnly)
        {
            // Remove by the entry's own catalog identity so a row is only retired when the track it
            // holds is genuinely not wanted.
            foreach (var entry in entries.Where(entry =>
                         entry.CatalogSongId is { Length: > 0 }
                         && !write.OrderedItemIds.Contains(entry.CatalogSongId, StringComparer.Ordinal)))
            {
                var removed = await _transport.DeleteAsync(
                    $"{tracksUrl}?ids[library-songs]={Uri.EscapeDataString(entry.LibrarySongId)}&mode=all",
                    headers,
                    cancellationToken);
                if (!removed.Success)
                {
                    return PlaylistMembershipWriteResult.Failed(DescribeFailure(removed, "removal"));
                }
            }
        }

        // Only what is actually missing. Apple allows duplicates, so re-sending every id appends
        // another copy of the whole playlist on each pass.
        var toAdd = write.AppendMissingOnly
            ? write.OrderedItemIds.Where(id => !presentCatalogIds.Contains(id)).ToList()
            : write.OrderedItemIds.ToList();

        var added = 0;
        foreach (var trackId in toAdd)
        {
            if (string.IsNullOrWhiteSpace(trackId))
            {
                continue;
            }

            var response = await _transport.PostAsync(
                tracksUrl,
                jsonBody: JsonBody(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["data"] = new[]
                    {
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            // A catalog id, which is what the library-add endpoint expects.
                            ["id"] = trackId,
                            ["type"] = "songs",
                        },
                    },
                }),
                headers: headers,
                cancellationToken: cancellationToken);

            if (response.Success)
            {
                added++;
                continue;
            }

            // Stop at the first rejection. Continuing would give the tracks after it earlier
            // added-at stamps, and Apple offers no positional insert to undo that.
            var reason = DescribeFailure(response, "addition");
            return PlaylistMembershipWriteResult.Failed(
                reason.StartsWith("Apple Music is not authorized", StringComparison.Ordinal)
                    || reason.Contains("subscription", StringComparison.OrdinalIgnoreCase)
                    ? reason
                    : $"{reason} Nothing after it was written, so the order is not inverted.");
        }

        return PlaylistMembershipWriteResult.Ok(added, 0);
    }

    /// <summary>
    /// Turns a failed response into a message that says what to do.
    /// <para>
    /// The paid-subscription denial is separated from an expired token on purpose: reconnecting does
    /// not fix a missing subscription, and telling someone to reconnect when their session is fine
    /// sends them down the wrong path entirely.
    /// </para>
    /// </summary>
    public static string DescribeFailure(TargetApiResponse response, string operation)
    {
        if (IsCloudLibraryDenial(response))
        {
            return "Apple Music needs an active subscription to write a library playlist. "
                + "Without it Apple refuses the change, and nothing was written.";
        }

        if (response.IsAuthRejection)
        {
            return "Apple Music rejected the "
                + operation
                + "; the session has expired. Reconnect Apple Music and nothing was written.";
        }

        if (response.TransportError is not null)
        {
            return "Apple Music could not be reached for the " + operation + ".";
        }

        return "Apple Music failed to write the playlist.";
    }

    /// <summary>
    /// Whether the response is Apple's "valid credentials, no paid Cloud Library" denial. Apple
    /// returns HTTP 400 with error code 40015, which is the only way to tell this apart from an
    /// ordinary bad request.
    /// </summary>
    public static bool IsCloudLibraryDenial(TargetApiResponse response)
    {
        if (response.Status != 400 || response.TryParseJson() is not { } json)
        {
            return false;
        }

        return json.ReadArray("errors")
            .Any(error => string.Equals(
                error.ReadString("code"),
                CloudLibraryDeniedCode.ToString(),
                StringComparison.Ordinal));
    }

    private async Task<AppleTargetCredentials?> ResolveAsync(CancellationToken cancellationToken)
        => await _credentials(cancellationToken);

    private static IReadOnlyDictionary<string, string> Headers(AppleTargetCredentials credentials)
    {
        // The value is often copied from a request header already prefixed with "Bearer ".
        var bearer = credentials.AuthorizationToken.Trim();
        if (bearer.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
        {
            bearer = bearer["bearer ".Length..];
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + bearer,
            ["Media-User-Token"] = credentials.MediaUserToken,
            ["Origin"] = "https://music.apple.com",
            ["Referer"] = "https://music.apple.com/",
        };
    }

    private static JsonElement JsonBody(object value)
        => JsonSerializer.SerializeToElement(value);
}
