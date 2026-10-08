using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations.Spotify;

/// <summary>
/// The Spotify credentials a playlist destination needs.
/// <para>
/// Only the presence of a session is checked. The destination does not use a bearer token of its
/// own: the web player issues a short-lived token per request and the client mints a fresh one on
/// demand, which is what makes an unattended scheduled pass work without anything going stale.
/// </para>
/// </summary>
public sealed record SpotifyTargetCredentials(string AccessToken, string? RefreshToken = null, string? ClientId = null, string? ClientSecret = null)
{
    public static bool IsUsable(SpotifyTargetCredentials? credentials)
        => credentials is not null && !string.IsNullOrWhiteSpace(credentials.AccessToken);
}

/// <summary>
/// The Spotify destination, over the web player's own backend, using the signed-in <c>sp_dc</c>
/// session the app already holds.
/// <para>
/// Ported from the reference's <c>SpotifyTarget</c> in its cookie mode. The official Web API is
/// not an option here: its playlist endpoints require a client/secret pair granted through a second
/// OAuth consent screen, which the app has no way to ask the user for. The web player's backend
/// works with the credential the user has already given, so this is the same call the reference
/// makes.
/// </para>
/// <para>
/// Three behaviours there are requirements rather than preferences and are preserved here:
/// </para>
/// <list type="bullet">
/// <item>Tracks are added ONE PER REQUEST. Each added item is stamped with its own added-at time, so
/// a batched append whose items land out of order leaves the destination's Recently Added ordering
/// permanently wrong, and there is no positional insert to repair it afterwards.</item>
/// <item>Removal is by occurrence uid, not by track id. A playlist can hold the same track twice,
/// and removing by track id takes out every copy - including one the user added deliberately. The
/// uid read alongside the track is what makes a single copy retirable.</item>
/// <item>An incomplete read is never treated as an empty playlist. Mirroring against "empty" would
/// delete the destination's tracks, so an incomplete read fails the pass instead.</item>
/// </list>
/// </summary>
public sealed class SpotifyPlaylistSyncTarget : IPlaylistSyncTarget
{
    private readonly SpotifyPlaylistWriteClient _client;
    private readonly Func<CancellationToken, Task<SpotifyTargetCredentials?>> _credentials;

    public SpotifyPlaylistSyncTarget(
        SpotifyPlaylistWriteClient client,
        Func<CancellationToken, Task<SpotifyTargetCredentials?>> credentials)
    {
        _client = client;
        _credentials = credentials;
    }

    public string TargetId => "spotify";

    /// <summary>
    /// Carries the client's reason when the playlist was created but something after it did not
    /// complete - filing it into the library, most often - which the null id alone cannot express.
    /// </summary>
    public string? CreateFailureDetail => _client.LastCreateError;

    public PlaylistTargetKind TargetKind => PlaylistTargetKind.Platform;

    public async Task<TargetPlaylistLookup<string>> FindPlaylistAsync(
        string? playlistId,
        string name,
        CancellationToken cancellationToken)
    {
        if (!await IsConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            var playlist = await _client.FetchPlaylistAsync(
                SpotifyPlaylistWriteClient.ToPlaylistUri(playlistId),
                cancellationToken).ConfigureAwait(false);
            if (playlist is null)
            {
                return TargetPlaylistLookup<string>.Missing();
            }

            var uri = playlist.Value.TryGetProperty("uri", out var uriValue) ? uriValue.ToString() : null;
            var id = ReadPlaylistId(uri) ?? playlistId;
            return string.IsNullOrWhiteSpace(id)
                ? TargetPlaylistLookup<string>.Missing()
                : TargetPlaylistLookup<string>.Found(id!);
        }

        var wanted = name?.Trim() ?? string.Empty;
        if (wanted.Length == 0)
        {
            return TargetPlaylistLookup<string>.Missing();
        }

        var directory = await _client.ReadLibraryDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            // Unavailable and missing are different answers. Reporting "missing" here would make the
            // next pass create a second playlist under the same name.
            return TargetPlaylistLookup<string>.Unavailable();
        }

        // Partial means a row failed to resolve. The playlist being looked for could be one of them,
        // so an absent name is not authoritative and must not become a create.
        if (directory.Value.Partial)
        {
            return TargetPlaylistLookup<string>.Unavailable();
        }

        // A followed playlist and an owned one can share a name. Preferring the editable one is what
        // keeps the pass pointed at something it can actually write to.
        var match = directory.Value.Playlists
            .Where(playlist => string.Equals(playlist.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static playlist => playlist.Editable)
            .FirstOrDefault();

        return match is null
            ? TargetPlaylistLookup<string>.Missing()
            : TargetPlaylistLookup<string>.Found(match.Id);
    }

    public async Task<string?> CreatePlaylistAsync(string name, string? description, CancellationToken cancellationToken)
    {
        if (!await IsConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var uri = await _client.CreatePlaylistAsync(name, description, cancellationToken).ConfigureAwait(false);
        return ReadPlaylistId(uri) ?? uri;
    }

    public async Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken cancellationToken)
    {
        if (!await IsConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return TargetPlaylistItemsRead.Unreadable;
        }

        var items = await ReadContentsAsync(playlistId, cancellationToken).ConfigureAwait(false);
        if (items is null)
        {
            return TargetPlaylistItemsRead.Unreadable;
        }

        // A local file has no catalog id and cannot be addressed later, so it is skipped rather
        // than recorded as an id that would never resolve.
        return TargetPlaylistItemsRead.Ok(
            items
                .Select(static item => ReadTrackId(item.TrackUri))
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id!)
                .ToList());
    }

    public async Task<PlaylistMembershipWriteResult> WriteMembershipAsync(
        PlaylistMembershipWrite write,
        CancellationToken cancellationToken)
    {
        if (!await IsConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return PlaylistMembershipWriteResult.Failed("Spotify is not connected. Sign in to Spotify and try again.");
        }

        var playlistUri = SpotifyPlaylistWriteClient.ToPlaylistUri(write.PlaylistId);
        var wanted = new HashSet<string>(write.OrderedItemIds, StringComparer.Ordinal);
        var removedCount = 0;

        if (!write.AppendMissingOnly && write.CurrentItemIds.Count > 0)
        {
            // Distinct because the occurrences of a stale track are collected per track below. Leaving the
            // duplicates in would collect the same occurrence twice and report more removals than
            // the pass actually performed.
            var stale = write.CurrentItemIds
                .Where(id => !wanted.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (stale.Count > 0)
            {
                // The uids are only in the occurrence read, and they are the only handle that can
                // address ONE copy. Re-reading here rather than trusting a cached one is what makes
                // the positions below trustworthy: they are evaluated against a single snapshot, so
                // the removal cannot shift its own targets.
                var contents = await ReadContentsAsync(write.PlaylistId, cancellationToken).ConfigureAwait(false);
                if (contents is null)
                {
                    return PlaylistMembershipWriteResult.Failed(
                        "Spotify's playlist could not be read, so nothing was removed.");
                }

                var visible = contents
                    .Where(static item => !string.IsNullOrWhiteSpace(ReadTrackId(item.TrackUri)))
                    .ToList();

                var uids = new List<string>();
                // Every visible copy of a stale id is stale: the source no longer wants that
                // track at all, so retiring all of them is correct, not over-removal.
                foreach (var occurrences in stale.Select(id => visible
                    .Where(item => string.Equals(ReadTrackId(item.TrackUri), id, StringComparison.Ordinal))
                    .ToList()))
                {
                    if (occurrences.Any(static occurrence => string.IsNullOrWhiteSpace(occurrence.Uid)))
                    {
                        return PlaylistMembershipWriteResult.Failed(
                            "Spotify returned a playlist entry without an occurrence id. Removing by track id "
                            + "would delete every copy of a duplicated track, so nothing was written.");
                    }

                    uids.AddRange(occurrences.Select(static occurrence => occurrence.Uid!));
                }

                if (uids.Count > 0)
                {
                    var removal = await _client.RemoveItemsAsync(playlistUri, uids, cancellationToken)
                        .ConfigureAwait(false);
                    if (!removal.Success)
                    {
                        return PlaylistMembershipWriteResult.Failed(Describe(removal));
                    }

                    removedCount = uids.Count;
                }
            }
        }

        // Only what is actually missing. Spotify permits duplicates, so re-sending ids the playlist
        // already holds appends a second copy of the whole playlist and the pass never converges.
        var present = new HashSet<string>(write.CurrentItemIds, StringComparer.Ordinal);
        var toAdd = write.AppendMissingOnly
            ? write.OrderedItemIds.Where(id => !present.Contains(id)).ToList()
            : write.OrderedItemIds.ToList();

        if (toAdd.Count > 0)
        {
            var added = await _client.AddTracksAsync(playlistUri, toAdd, cancellationToken).ConfigureAwait(false);
            if (!added.Success)
            {
                return PlaylistMembershipWriteResult.Failed(Describe(added));
            }
        }

        return PlaylistMembershipWriteResult.Ok(write.OrderedItemIds.Count, removedCount);
    }

    private async Task<IReadOnlyList<SpotifyPlaylistWriteClient.SpotifyPlaylistItem>?> ReadContentsAsync(
        string playlistId,
        CancellationToken cancellationToken)
        => await _client
            .ReadContentsAsync(SpotifyPlaylistWriteClient.ToPlaylistUri(playlistId), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    private async Task<bool> IsConnectedAsync(CancellationToken cancellationToken)
        => SpotifyTargetCredentials.IsUsable(await _credentials(cancellationToken).ConfigureAwait(false));

    private static string Describe(SpotifyPlaylistWriteClient.SpotifyPayload payload)
        => string.IsNullOrWhiteSpace(payload.Error)
            ? "Spotify failed to write the playlist."
            : payload.Error!;

    /// <summary>
    /// The playlist id out of a Spotify uri. Returns null for anything that is not a playlist uri,
    /// so a follow-uri is never written to as though it were addressable.
    /// </summary>
    internal static string? ReadPlaylistId(string? uri)
        => uri is not null && uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)
            ? uri["spotify:playlist:".Length..]
            : null;

    /// <summary>The catalog id out of a track uri, or null when it is not an addressable track.</summary>
    internal static string? ReadTrackId(string? uri)
        => uri is not null && uri.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? uri["spotify:track:".Length..]
            : null;
}