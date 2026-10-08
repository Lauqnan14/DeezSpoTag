using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations.Deezer;

/// <summary>The Deezer session a playlist destination needs.</summary>
public sealed record DeezerTargetSession(string AccessToken, string? UserId = null)
{
    public static bool IsUsable(DeezerTargetSession? session)
        => session is not null && !string.IsNullOrWhiteSpace(session.AccessToken);
}

/// <summary>
/// The Deezer destination, over Deezer's REST API.
/// <para>
/// Ported from the reference's <c>DeezerTarget</c>, including the one behaviour that adapter
/// deliberately does not have: Deezer deletes by CATALOG TRACK ID rather than by playlist entry, so
/// removing a track takes out every copy of it. The reference therefore disables its
/// order-repair pass entirely (<c>replay_chronology = None</c>) and treats appending in source order
/// as the only ordering write Deezer can survive.
/// </para>
/// <para>
/// This adapter follows suit. In mirror mode a track that appears more than once on the destination
/// is refused rather than removed, because there is no way to retire a single copy.
/// </para>
/// </summary>
public sealed class DeezerPlaylistSyncTarget : IPlaylistSyncTarget
{
    private const string ApiBase = "https://api.deezer.com";

    private readonly TargetApiTransport _transport;
    private readonly Func<CancellationToken, Task<DeezerTargetSession?>> _session;

    public DeezerPlaylistSyncTarget(
        TargetApiTransport transport,
        Func<CancellationToken, Task<DeezerTargetSession?>> session)
    {
        _transport = transport;
        _session = session;
    }

    public string TargetId => "deezer";

    public PlaylistTargetKind TargetKind => PlaylistTargetKind.Platform;

    public async Task<TargetPlaylistLookup<string>> FindPlaylistAsync(
        string? playlistId,
        string name,
        CancellationToken cancellationToken)
    {
        var session = await ResolveAsync(cancellationToken);
        if (!DeezerTargetSession.IsUsable(session))
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            // Deezer's playlist endpoint is not scoped to the caller's library, so one GET answers
            // both "is it there" and "what is it called".
            var byId = await _transport.GetAsync(
                WithToken($"{ApiBase}/playlist/{Uri.EscapeDataString(playlistId)}", session!),
                headers: Token(session!),
                cancellationToken: cancellationToken);
            if (!byId.Reached)
            {
                return TargetPlaylistLookup<string>.Unavailable(byId.Status == 0 ? null : byId.Status);
            }

            if (HasApiError(byId))
            {
                return TargetPlaylistLookup<string>.Missing();
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

        var userId = session!.UserId ?? await ResolveUserIdAsync(session, cancellationToken);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        var index = 0;
        while (true)
        {
            var page = await _transport.GetAsync(
                WithToken($"{ApiBase}/user/{Uri.EscapeDataString(userId)}/playlists?limit=100&index={index}", session),
                headers: Token(session),
                cancellationToken: cancellationToken);
            if (!page.Reached)
            {
                return TargetPlaylistLookup<string>.Unavailable(page.Status == 0 ? null : page.Status);
            }

            if (page.TryParseJson() is not { } json)
            {
                return TargetPlaylistLookup<string>.Unavailable();
            }

            var matchId = json.ReadArray("data")
                .Where(playlist => string.Equals(playlist.ReadString("title")?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                .Select(playlist => playlist.ReadString("id"))
                .FirstOrDefault(id => id is { Length: > 0 });
            if (matchId is not null)
            {
                return TargetPlaylistLookup<string>.Found(matchId);
            }

            // Deezer signals the end with a null `next`; the offset otherwise advances by the page
            // size it actually returned.
            if (json.ReadString("next") is not { Length: > 0 })
            {
                return TargetPlaylistLookup<string>.Missing();
            }

            var received = json.ReadArray("data").Count;
            if (received == 0)
            {
                return TargetPlaylistLookup<string>.Missing();
            }

            index += received;
        }
    }

    public async Task<string?> CreatePlaylistAsync(string name, string? description, CancellationToken cancellationToken)
    {
        var session = await ResolveAsync(cancellationToken);
        if (!DeezerTargetSession.IsUsable(session))
        {
            return null;
        }

        var fields = new Dictionary<string, string> { ["title"] = name ?? string.Empty };
        if (!string.IsNullOrWhiteSpace(description))
        {
            // Deezer's create endpoint accepts a description, but the read endpoint returns an
            // object for it, so it is only sent when there is one to send.
            fields["description"] = description!;
        }

        var created = await _transport.PostAsync(
            WithToken($"{ApiBase}/user/me/playlists", session!),
            formFields: fields,
            headers: Token(session!),
            cancellationToken: cancellationToken);

        var id = created.TryParseJson()?.ReadString("id");
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    public async Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var session = await ResolveAsync(cancellationToken);
        if (!DeezerTargetSession.IsUsable(session))
        {
            return TargetPlaylistItemsRead.Unreadable;
        }

        var ids = new List<string>();
        var index = 0;
        var total = int.MaxValue;

        while (index < total)
        {
            var page = await _transport.GetAsync(
                WithToken($"{ApiBase}/playlist/{Uri.EscapeDataString(playlistId)}/tracks?limit=100&index={index}", session!),
                headers: Token(session!),
                cancellationToken: cancellationToken);
            if (!page.Reached)
            {
                // Never "empty" on failure: mirroring against an empty read would delete the
                // destination's whole playlist.
                return TargetPlaylistItemsRead.Unreadable;
            }

            if (page.TryParseJson() is not { } json)
            {
                return TargetPlaylistItemsRead.Unreadable;
            }

            // Deezer reports failures in the body with HTTP 200. An expired token comes back as
            // {"error": ...} with no data array, so without this check a failed read looks like a
            // playlist with no tracks - and a mirror pass would delete every track on it.
            if (HasApiError(page))
            {
                return TargetPlaylistItemsRead.Unreadable;
            }

            var rows = json.ReadArray("data");
            ids.AddRange(rows
                .Select(track => track.ReadString("id"))
                .Where(trackId => trackId is { Length: > 0 })
                .Cast<string>());

            total = json.ReadInt("total");
            if (rows.Count == 0)
            {
                // Deezer advertised a total this page did not reach. Reporting a short read as the
                // membership would make the caller remove the tracks it never saw.
                return total > index
                    ? TargetPlaylistItemsRead.Unreadable
                    : TargetPlaylistItemsRead.Ok(ids);
            }

            index += rows.Count;
        }

        return TargetPlaylistItemsRead.Ok(ids);
    }

    public async Task<PlaylistMembershipWriteResult> WriteMembershipAsync(
        PlaylistMembershipWrite write,
        CancellationToken cancellationToken)
    {
        var session = await ResolveAsync(cancellationToken);
        if (!DeezerTargetSession.IsUsable(session))
        {
            return PlaylistMembershipWriteResult.Failed("Deezer is not connected. Reconnect it in Login.");
        }

        var headers = Token(session!);
        var escaped = Uri.EscapeDataString(write.PlaylistId);
        var wanted = new HashSet<string>(write.OrderedItemIds, StringComparer.Ordinal);

        if (!write.AppendMissingOnly)
        {
            var duplicates = write.CurrentItemIds
                .GroupBy(static id => id, StringComparer.Ordinal)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var stale in write.CurrentItemIds.Where(id => !wanted.Contains(id)))
            {
                if (duplicates.Contains(stale))
                {
                    // Deezer's delete addresses the catalog track, not the entry, so removing a
                    // track that is present twice removes both copies. Refuse rather than lose one.
                    return PlaylistMembershipWriteResult.Failed(
                        "Deezer holds a duplicated track that is no longer wanted. "
                        + "Deezer can only remove every copy of a track, so nothing was written.");
                }

                var removed = await _transport.DeleteAsync(
                    WithToken($"{ApiBase}/playlist/{escaped}/tracks?songs={Uri.EscapeDataString(stale)}", session!),
                    headers,
                    cancellationToken);
                if (!removed.Success)
                {
                    return PlaylistMembershipWriteResult.Failed(
                        removed.IsAuthRejection
                            ? "Deezer rejected the removal; reconnect the account in Login."
                            : "Deezer failed to write the playlist.");
                }
            }
        }

        // Only what is actually missing. Sending every id again appends a duplicate of the whole
        // playlist on each pass, because Deezer permits duplicates.
        var present = new HashSet<string>(write.CurrentItemIds, StringComparer.Ordinal);
        var toAdd = write.AppendMissingOnly
            ? write.OrderedItemIds.Where(id => !present.Contains(id)).ToList()
            : write.OrderedItemIds.ToList();

        // One request per track, in order. Deezer has no positional insert, so an out-of-order batch
        // could not be repaired afterwards; and stopping at the first rejection keeps the tracks
        // that follow from receiving earlier added-at stamps than the ones before them.
        var added = 0;
        foreach (var trackId in toAdd)
        {
            if (string.IsNullOrWhiteSpace(trackId))
            {
                continue;
            }

            var response = await _transport.PostAsync(
                WithToken($"{ApiBase}/playlist/{escaped}/tracks", session!),
                formFields: new Dictionary<string, string> { ["songs"] = trackId },
                headers: headers,
                cancellationToken: cancellationToken);

            // Deezer answers a rejected add with HTTP 200 and an error object.
            if (response.Success && HasApiError(response))
            {
                return PlaylistMembershipWriteResult.Failed(
                    "Deezer rejected the addition. Nothing after it was written, so the order is not inverted.");
            }

            if (response.Success)
            {
                added++;
                continue;
            }

            if (response.IsAuthRejection)
            {
                return PlaylistMembershipWriteResult.Failed(
                    "Deezer rejected the addition; reconnect the account in Login.");
            }

            return PlaylistMembershipWriteResult.Failed(
                $"Deezer failed to add a track after {added}. Nothing after it was written, so the order is not inverted.");
        }

        return PlaylistMembershipWriteResult.Ok(added, 0);
    }

    private async Task<DeezerTargetSession?> ResolveAsync(CancellationToken cancellationToken)
        => await _session(cancellationToken);

    /// <summary>
    /// Deezer's API authenticates with an <c>access_token</c> query parameter, not an
    /// <c>Authorization</c> header. The header is ignored, so a call made with it returns the
    /// public unauthenticated view - which for a playlist endpoint means someone else's data, and
    /// for a write means a write that quietly did nothing.
    /// </summary>
    private static string WithToken(string url, DeezerTargetSession session)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{url}{separator}access_token={Uri.EscapeDataString(session.AccessToken)}";
    }

    private static IReadOnlyDictionary<string, string> Token(DeezerTargetSession session)
        // Kept for the endpoints that do accept a bearer, but Deezer's own clients in this repo use
        // the query parameter, so the query form is what the calls actually rely on.
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + session.AccessToken,
        };

    private async Task<string?> ResolveUserIdAsync(DeezerTargetSession session, CancellationToken cancellationToken)
    {
        var me = await _transport.GetAsync(WithToken($"{ApiBase}/user/me", session), headers: Token(session), cancellationToken);
        return me.TryParseJson()?.ReadString("id");
    }

    /// <summary>
    /// Deezer reports failures in the body with HTTP 200, so the status code alone says nothing
    /// about whether a call worked. Code 200 means the token is wrong; 300 means the account lacks
    /// the scope. Checked on every read, because an errored body has no data and would otherwise be
    /// read as an empty result.
    /// </summary>
    private static bool HasApiError(TargetApiResponse response)
        => response.TryParseJson() is { } json && json.ReadObject("error") is not null;
}
