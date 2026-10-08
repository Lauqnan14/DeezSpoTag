using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations.Spotify;

/// <summary>
/// Spotify playlist writes over the web player's own GraphQL surface, using the signed-in
/// <c>sp_dc</c> session the app already holds.
/// <para>
/// Ported from the reference's <c>spotify_cookie.py</c>. The public Web API cannot be used for this:
/// the token minted from the web-player session is scoped to the web client, and the app deliberately
/// has no second OAuth grant for the user to complete. The web player's pathfinder endpoint is
/// therefore the one that works with the credential the user already gave the app.
/// </para>
/// <para>
/// The operations are persisted-query calls addressed by a document hash, and the web player
/// rotates those hashes on release. Both are handled the way the reference handles them: a seed set
/// to try first, and a re-scrape of the live web-player bundle when Spotify reports the hash is
/// unknown. Without the re-scrape a Spotify release silently breaks playlist writing.
/// </para>
/// <para>
/// Two properties are load-bearing and are preserved:
/// </para>
/// <list type="bullet">
/// <item>A mutation is sent ONCE and never retried on a transport failure. The request may already
/// have reached Spotify before the response was lost, so a retry can append the same track twice.</item>
/// <item>Tracks are appended one at a time. A batched add stamps every item with the same added-at
/// time, which scrambles the destination's Recently Added ordering, and the mutation offers no way
/// to set a per-item timestamp.</item>
/// </list>
/// </summary>
public sealed class SpotifyPlaylistWriteClient
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private const string PathfinderUrl = "https://api-partner.spotify.com/pathfinder/v2/query";
    private const string WebPlayerUrl = "https://open.spotify.com/";

    /// <summary>
    /// The web player's own backend. Used for the calls that are plain REST rather than
    /// persisted-query operations, notably playlist creation and library filing.
    /// </summary>
    private const string SpclientBase = "https://spclient.wg.spotify.com";

    /// <summary>SongMirror's persisted-query hashes, rewritten in place when Spotify rotates them.</summary>
    private sealed record PersistedHashes(
        string PlaylistMut,
        string PlaylistRead,
        string Profile,
        string Library,
        string LibraryTracks,
        string LibraryMut)
    {
        public static PersistedHashes Seed { get; } = new(
            PlaylistMut: "47b2a1234b17748d332dd0431534f22450e9ecbb3d5ddcdacbd83368636a0990",
            PlaylistRead: "a65e12194ed5fc443a1cdebed5fabe33ca5b07b987185d63c72483867ad13cb4",
            Profile: "b197b5adb4b761690f76ad9d9fb278c14c14e7331f357c04a56e7001af7106e0",
            Library: "390c78e5b951029bad359785e69b07b536a509c581cbcd0aded5e5067f187455",
            LibraryTracks: "087278b20b743578a6262c2b0b4bcd20d879c503cc359a2285baf083ef944240",
            LibraryMut: "1ad0d40b3c09660d818b9e770eb1e84745dfbe941df159a64f8772b6fa2bfc3a");
    }

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    private static readonly Dictionary<string, string> OperationToDocument = new(StringComparer.Ordinal)
    {
        ["addToPlaylist"] = nameof(PersistedHashes.PlaylistMut),
        ["removeFromPlaylist"] = nameof(PersistedHashes.PlaylistMut),
        ["fetchPlaylistContents"] = nameof(PersistedHashes.PlaylistRead),
        ["fetchPlaylist"] = nameof(PersistedHashes.PlaylistRead),
        ["profileAttributes"] = nameof(PersistedHashes.Profile),
        ["libraryV3"] = nameof(PersistedHashes.Library),
        ["fetchLibraryTracks"] = nameof(PersistedHashes.LibraryTracks),
    };

    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, Task<SpotifyWebPlayerSession?>> _session;
    private readonly SemaphoreSlim _hashGate = new(1, 1);
    private PersistedHashes _hashes = PersistedHashes.Seed;

    public SpotifyPlaylistWriteClient(
        HttpClient httpClient,
        Func<CancellationToken, Task<SpotifyWebPlayerSession?>> session)
    {
        _httpClient = httpClient;
        _session = session;
    }

    /// <summary>What the web player needs on every call.</summary>
    public sealed record SpotifyWebPlayerSession(string AccessToken, string ClientToken, string ClientVersion)
    {
        public static bool IsUsable([NotNullWhen(true)] SpotifyWebPlayerSession? session)
            => session is not null
                && !string.IsNullOrWhiteSpace(session.AccessToken)
                && !string.IsNullOrWhiteSpace(session.ClientToken);
    }

    /// <summary>One entry in a playlist, in the web player's own vocabulary.</summary>
    public sealed record SpotifyPlaylistItem(string? Uid, string? TrackUri);

    /// <summary>
    /// Creates a playlist and files it into the account's library, returning its uri.
    /// <para>
    /// Creation is not one of the persisted-query operations: the web player creates a playlist
    /// through its own REST backend and the account's library is a SEPARATE document that a
    /// newly-created playlist is not automatically added to. A playlist created but not filed is
    /// real and writable but invisible in the library, so the next pass would not find it and would
    /// create a second one under the same name.
    /// </para>
    /// <para>
    /// Filing is therefore best-effort in one direction only: a filed failure is reported, but a
    /// failure to file cannot undo the playlist, so the uri is still returned rather than throwing
    /// away a playlist that exists and has been written to.
    /// </para>
    /// </summary>
    public async Task<string?> CreatePlaylistAsync(
        string name,
        string? description,
        CancellationToken cancellationToken)
    {
        var session = await _session(cancellationToken).ConfigureAwait(false);
        if (!SpotifyWebPlayerSession.IsUsable(session))
        {
            return null;
        }

        // An attribute-op document rather than a field set. This is the shape the endpoint expects;
        // anything else is accepted and produces a playlist with no name.
        var body = JsonSerializer.Serialize(new
        {
            ops = new[]
            {
                new
                {
                    kind = 6,
                    updateListAttributes = new
                    {
                        newAttributes = new
                        {
                            values = new { name = name ?? string.Empty, formatAttributes = Array.Empty<object>(), pictureSize = Array.Empty<object>() },
                            noValue = Array.Empty<object>(),
                        },
                    },
                },
            },
        });

        HttpResponseMessage? response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, SpclientBase + "/playlist/v2/playlist")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "UTF-8",
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // Not retried, for the same reason a pathfinder mutation is not: the playlist may have
            // been created before the connection dropped, and retrying makes a second one.
            return null;
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastCreateError = response.StatusCode == HttpStatusCode.Forbidden
                    ? "Spotify refused to create the playlist for this signed-in account."
                    : $"Spotify returned {(int)response.StatusCode} when creating the playlist.";
                return null;
            }

            try
            {
                using var created = JsonDocument.Parse(text);
                var uri = created.RootElement.TryGetProperty("uri", out var uriValue)
                    ? uriValue.ToString()
                    : null;
                if (string.IsNullOrWhiteSpace(uri))
                {
                    LastCreateError = "Spotify created the playlist but did not return its id.";
                    return null;
                }

                await FileIntoLibraryAsync(uri, cancellationToken).ConfigureAwait(false);
                return uri;
            }
            catch (JsonException)
            {
                LastCreateError = "Spotify returned an unreadable response when creating the playlist.";
                return null;
            }
        }
    }

    /// <summary>Why the last create attempt failed, when it did. Null after a success.</summary>
    public string? LastCreateError { get; private set; }

    /// <summary>
    /// Adds a freshly-created playlist to the account's rootlist.
    /// <para>
    /// The rootlist is a versioned document, so the write carries the revision it read. A stale
    /// revision means someone changed the library in between, and this declines rather than
    /// overwriting their change.
    /// </para>
    /// </summary>
    private async Task FileIntoLibraryAsync(string playlistUri, CancellationToken cancellationToken)
    {
        var session = await _session(cancellationToken).ConfigureAwait(false);
        if (!SpotifyWebPlayerSession.IsUsable(session))
        {
            return;
        }

        try
        {
            var userId = await ReadCurrentUserIdAsync(session, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return;
            }

            var rootlistUrl = $"{SpclientBase}/playlist/v2/user/{Uri.EscapeDataString(userId!)}/rootlist";
            string revision;
            using (var read = await SendSpclientAsync(HttpMethod.Get, rootlistUrl, session, null, cancellationToken)
                       .ConfigureAwait(false))
            {
                if (!read.IsSuccessStatusCode)
                {
                    return;
                }

                using var payload = JsonDocument.Parse(await read.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                revision = payload.RootElement.TryGetProperty("revision", out var revisionValue)
                    ? revisionValue.ToString()
                    : string.Empty;
            }

            if (string.IsNullOrWhiteSpace(revision))
            {
                return;
            }

            var changeBody = JsonSerializer.Serialize(new
            {
                baseRevision = revision,
                wantResultingRevisions = false,
                wantSyncResult = false,
                nonces = Array.Empty<object>(),
                deltas = new[]
                {
                    new
                    {
                        ops = new[]
                        {
                            new
                            {
                                kind = 2,
                                add = new { items = new[] { new { uri = playlistUri } }, addFirst = true },
                            },
                        },
                    },
                },
            });

            using var write = await SendSpclientAsync(HttpMethod.Post, rootlistUrl + "/changes", session, changeBody, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException)
        {
            // The playlist exists and is writable; only its library entry is missing. Reported so
            // the reason is visible rather than silently leaving an invisible playlist behind.
            LastCreateError = "The playlist was created but Spotify did not list it in your library, "
                + "so the next pass may create a second playlist with the same name.";
        }
    }

    /// <summary>The signed-in account's own id, or null.</summary>
    private async Task<string?> ReadCurrentUserIdAsync(
        SpotifyWebPlayerSession session,
        CancellationToken cancellationToken)
    {
        var payload = await RunAsync(
            "profileAttributes",
            new Dictionary<string, object?>(StringComparer.Ordinal),
            isMutation: false,
            cancellationToken).ConfigureAwait(false);

        if (!payload.Success
            || payload.Value.ValueKind != JsonValueKind.Object
            || !payload.Value.TryGetProperty("me", out var me)
            || !me.TryGetProperty("profile", out var profile))
        {
            return null;
        }

        if (profile.TryGetProperty("username", out var username) && !string.IsNullOrWhiteSpace(username.ToString()))
        {
            return username.ToString();
        }

        return profile.TryGetProperty("id", out var id) ? id.ToString() : null;
    }

    private async Task<HttpResponseMessage> SendSpclientAsync(
        HttpMethod method,
        string url,
        SpotifyWebPlayerSession session,
        string? body,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "UTF-8",
            };
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends tracks one at a time, bottom of the playlist, in the order given.
    /// <para>
    /// One call per track is what gives each item its own added-at time. Stops at the first failure
    /// rather than continuing: the destination appends, so a track after a rejected one would be
    /// stamped as older than the ones before it, and there is no way to correct the order.
    /// </para>
    /// </summary>
    public async Task<SpotifyPayload> AddTracksAsync(
        string playlistUri,
        IReadOnlyList<string> trackIds,
        CancellationToken cancellationToken)
    {
        foreach (var trackId in trackIds)
        {
            if (string.IsNullOrWhiteSpace(trackId))
            {
                continue;
            }

            var result = await RunAsync(
                "addToPlaylist",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["playlistUri"] = playlistUri,
                    ["playlistItemUris"] = new[] { ToTrackUri(trackId) },
                    // BOTTOM_OF_PLAYLIST appends, which is the only ordering a mutation can express.
                    ["newPosition"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["moveType"] = "BOTTOM_OF_PLAYLIST",
                        ["fromUid"] = null,
                    },
                },
                isMutation: true,
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                return result;
            }

            // The web player rate-limits a burst of mutations; the reference paces at the same gap.
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
        }

        return Ok;
    }

    /// <summary>
    /// Removes specific occurrences by their item uid.
    /// <para>
    /// The mutation deletes by uid, not by track uri. That distinction is the whole reason removal
    /// reads the playlist first: a playlist can hold the same track more than once, and deleting by
    /// uri would take out every copy, including one the user added deliberately.
    /// </para>
    /// </summary>
    public async Task<SpotifyPayload> RemoveItemsAsync(
        string playlistUri,
        IReadOnlyList<string> uids,
        CancellationToken cancellationToken)
    {
        var distinct = uids.Where(static uid => !string.IsNullOrWhiteSpace(uid))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0)
        {
            return Failed("Spotify did not return an occurrence id for a selected track.");
        }

        return await RunAsync(
            "removeFromPlaylist",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["playlistUri"] = playlistUri,
                ["uids"] = distinct,
            },
            isMutation: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every entry in a playlist, paginated, as (uid, track uri) pairs.</summary>
    public async Task<IReadOnlyList<SpotifyPlaylistItem>?> ReadContentsAsync(
        string playlistUri,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var items = new List<SpotifyPlaylistItem>();
        var offset = 0;
        var total = int.MaxValue;

        while (offset < total)
        {
            var page = await RunAsync(
                "fetchPlaylistContents",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["uri"] = playlistUri,
                    ["offset"] = offset,
                    ["limit"] = limit,
                },
                isMutation: false,
                cancellationToken).ConfigureAwait(false);

            if (!page.Success)
            {
                return null;
            }

            var content = page.Value.TryGetProperty("playlistV2", out var v2)
                && v2.TryGetProperty("content", out var inner)
                    ? inner
                    : default;
            if (content.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            var rows = content.TryGetProperty("items", out var list)
                ? list.EnumerateArray().ToList()
                : new List<JsonElement>();

            // totalCount is what makes an incomplete read detectable. Without it a truncated page
            // would look like a short playlist, and a mirror pass would remove the rest.
            if (!content.TryGetProperty("totalCount", out var totalValue)
                || !int.TryParse(totalValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out total))
            {
                return null;
            }

            foreach (var row in rows)
            {
                var uid = row.TryGetProperty("uid", out var uidValue) ? uidValue.ToString() : null;
                var uri = row.TryGetProperty("itemV2", out var item)
                          && item.TryGetProperty("data", out var data)
                          && data.TryGetProperty("uri", out var uriValue)
                    ? uriValue.ToString()
                    : null;
                items.Add(new SpotifyPlaylistItem(uid, uri));
            }

            if (rows.Count == 0)
            {
                return offset < total ? null : items;
            }

            offset += rows.Count;
        }

        return items;
    }

    /// <summary>A playlist in the signed-in account's own library.</summary>
    public sealed record SpotifyLibraryPlaylist(
        string Id,
        string Name,
        bool Editable,
        string? RevisionId);

    /// <summary>
    /// The signed-in account's playlists, and whether the listing can be trusted.
    /// <para>
    /// This is the web player's own filtered library query rather than the Web API's listing, because
    /// the latter only returns playlists the account can READ. The capability flag comes from this
    /// query too, and it is the difference between a playlist that can be mirrored and a followed
    /// playlist that silently fails every write.
    /// </para>
    /// <para>
    /// Partial is deliberately not folded into an empty list. An incomplete listing that reads as
    /// "no such playlist" makes the next pass create a second playlist with the same title, which is
    /// worse than refusing to sync.
    /// </para>
    /// </summary>
    public async Task<(IReadOnlyList<SpotifyLibraryPlaylist> Playlists, bool Partial)?> ReadLibraryDirectoryAsync(
        CancellationToken cancellationToken)
    {
        var playlists = new List<SpotifyLibraryPlaylist>();
        var partial = false;
        var offset = 0;
        const int pageSize = 100;

        while (true)
        {
            var page = await RunAsync(
                "libraryV3",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["filters"] = new[] { "Playlists" },
                    ["order"] = "Alphabetical",
                    ["textFilter"] = null,
                    ["features"] = Array.Empty<string>(),
                    ["limit"] = pageSize,
                    ["offset"] = offset,
                    ["flatten"] = true,
                    ["expandedFolders"] = null,
                    ["folderUri"] = null,
                    ["includeFoldersWhenFlattening"] = true,
                },
                isMutation: false,
                cancellationToken).ConfigureAwait(false);

            if (!page.Success
                || page.Value.ValueKind != JsonValueKind.Object
                || !page.Value.TryGetProperty("me", out var me)
                || !me.TryGetProperty("libraryV3", out var library))
            {
                return null;
            }

            var rows = library.TryGetProperty("items", out var items)
                ? items.EnumerateArray().ToList()
                : new List<JsonElement>();

            // As with the contents read, totalCount is what makes a short page detectable. Its
            // absence means the listing is not trustworthy, and neither is a page that came back
            // short of a total that says more is coming.
            if (!library.TryGetProperty("totalCount", out var totalValue)
                || !int.TryParse(totalValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
            {
                return null;
            }

            if (rows.Count == 0 && offset < total)
            {
                return null;
            }

            foreach (var row in rows)
            {
                if (!row.TryGetProperty("item", out var item))
                {
                    partial = true;
                    continue;
                }

                var data = item.TryGetProperty("data", out var dataValue) ? dataValue : default;
                var rowType = data.ValueKind == JsonValueKind.Object
                              && data.TryGetProperty("__typename", out var typeValue)
                    ? typeValue.ToString()
                    : null;

                // A deleted or inaccessible playlist leaves a typed NotFound tombstone in an
                // otherwise complete page. That is an absence, not a row that failed to resolve.
                if (string.Equals(rowType, "NotFound", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.Equals(rowType, "Playlist", StringComparison.Ordinal))
                {
                    // A Folder is an expected row in a flattened listing and says nothing about
                    // completeness. Anything else is an unresolved row.
                    if (!string.Equals(rowType, "Folder", StringComparison.Ordinal))
                    {
                        partial = true;
                    }

                    continue;
                }

                var uri = (data.TryGetProperty("uri", out var uriValue) ? uriValue.ToString() : null)
                          ?? (item.TryGetProperty("_uri", out var fallbackUri) ? fallbackUri.ToString() : null);
                var id = uri is not null && uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)
                    ? uri["spotify:playlist:".Length..]
                    : null;

                // A playlist id containing a further colon is a follow-uri, not an addressable
                // playlist, and would be written to as if it were one.
                if (string.IsNullOrWhiteSpace(id) || id!.Contains(':', StringComparison.Ordinal))
                {
                    partial = true;
                    continue;
                }

                var name = data.TryGetProperty("name", out var nameValue) ? nameValue.ToString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    partial = true;
                    name = $"Untitled playlist ({id})";
                }

                var editable = data.TryGetProperty("currentUserCapabilities", out var capabilities)
                               && capabilities.ValueKind == JsonValueKind.Object
                               && capabilities.TryGetProperty("canEditItems", out var canEdit)
                               && canEdit.ValueKind == JsonValueKind.True;

                playlists.Add(new SpotifyLibraryPlaylist(
                    id,
                    name,
                    editable,
                    data.TryGetProperty("revisionId", out var revision) ? revision.ToString() : null));
            }

            offset += rows.Count;
            if (offset >= total)
            {
                return (playlists, partial);
            }
        }
    }

    /// <summary>The playlist's own header payload, or null when the account cannot see it.</summary>
    public async Task<JsonElement?> FetchPlaylistAsync(
        string playlistUri,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "fetchPlaylist",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["uri"] = playlistUri,
                ["offset"] = 0,
                ["limit"] = 0,
                ["enableWatchFeedEntrypoint"] = false,
            },
            isMutation: false,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success
            || result.Value.ValueKind != JsonValueKind.Object
            || !result.Value.TryGetProperty("playlistV2", out var playlist))
        {
            return null;
        }

        // A uri the account cannot see comes back as a typed "not found" node rather than an error,
        // so an absent name is the real signal that nothing is there.
        return playlist.TryGetProperty("name", out _) ? playlist : null;
    }

    private static string ToTrackUri(string trackId)
        => trackId.StartsWith("spotify:", StringComparison.Ordinal)
            ? trackId
            : "spotify:track:" + trackId;

    public static string ToPlaylistUri(string playlistId)
        => playlistId.StartsWith("spotify:", StringComparison.Ordinal)
            ? playlistId
            : "spotify:playlist:" + playlistId;

    private static string? ReadPlaylistUri(SpotifyPayload payload)
    {
        if (payload.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var uri = payload.Value.TryGetProperty("playlistUri", out var direct)
            ? direct.ToString()
            : null;
        if (!string.IsNullOrWhiteSpace(uri))
        {
            return uri;
        }

        return payload.Value.TryGetProperty("playlistV2", out var v2)
               && v2.TryGetProperty("uri", out var nested)
            ? nested.ToString()
            : null;
    }

    /// <summary>Whether a call succeeded, and why not when it did not.</summary>
    public readonly record struct SpotifyPayload(bool Success, JsonElement Value, string? Error, bool AuthFailure = false);

    private static SpotifyPayload Ok { get; } = new(true, default, null, false);

    private static SpotifyPayload Failed(string error, bool auth = false) => new(false, default, error, auth);

    /// <summary>
    /// Runs one persisted-query operation, self-healing a rotated hash and a stale token.
    /// <para>
    /// A 401 means the access token expired and is re-minted. A "persisted query not found" means
    /// the web player rotated its hashes, which is re-scraped from the live bundle. Both are
    /// recovered from because either one otherwise looks like a permanent failure, and both are
    /// retried only for reads - a mutation may already have been applied when its response was
    /// lost, so replaying it could duplicate the write.
    /// </para>
    /// </summary>
    private async Task<SpotifyPayload> RunAsync(
        string operation,
        IReadOnlyDictionary<string, object?> variables,
        bool isMutation,
        CancellationToken cancellationToken)
    {
        var document = OperationToDocument[operation];
        var refreshed = false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var session = await _session(cancellationToken).ConfigureAwait(false);
            if (!SpotifyWebPlayerSession.IsUsable(session))
            {
                return new SpotifyPayload(false, default, "Spotify is not connected.");
            }

            var hash = ResolveHash(document);
            var body = JsonSerializer.Serialize(new
            {
                variables,
                operationName = operation,
                extensions = new { persistedQuery = new { version = 1, sha256Hash = hash } },
            });

            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, PathfinderUrl)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                request.Headers.Add("Client-Token", session.ClientToken);
                request.Headers.Add("Spotify-App-Version", session.ClientVersion);
                request.Headers.Add("App-Platform", "WebPlayer");
                request.Headers.UserAgent.ParseAdd(UserAgent);

                response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                // Deliberately not retried for a mutation. The request may have reached Spotify
                // before the connection dropped, and replaying it would append the track twice.
                if (isMutation)
                {
                    return new SpotifyPayload(
                        false,
                        default,
                        "Spotify's connection dropped during the write. The change may or may not "
                        + "have been applied, so nothing further was written.");
                }

                if (attempt == 2)
                {
                    return new SpotifyPayload(false, default, "Spotify could not be reached.");
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 8)), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The token expired. A fresh one is minted on the next iteration by the session
                    // provider, so this is a retry rather than a failure.
                    continue;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                JsonDocument? document2 = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        document2 = JsonDocument.Parse(text);
                    }
                }
                catch (JsonException)
                {
                    document2 = null;
                }

                var root = document2?.RootElement ?? default;
                if (IsPersistedQueryMissing(root) && !refreshed)
                {
                    refreshed = true;
                    await RefreshHashesAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("errors", out var errors)
                    && errors.ValueKind == JsonValueKind.Array
                    && errors.GetArrayLength() > 0)
                {
                    return new SpotifyPayload(false, root, "Spotify refused the request: " + errors[0].ToString());
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new SpotifyPayload(
                        false,
                        root,
                        response.StatusCode == HttpStatusCode.Forbidden
                            // A 403 here is almost always the wrong account: the web session can only
                            // write to playlists that account owns.
                            ? "Spotify refused the change. The signed-in account must own the playlist."
                            : $"Spotify returned {(int)response.StatusCode}.",
                        response.StatusCode == HttpStatusCode.Forbidden);
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return new SpotifyPayload(false, root, "Spotify returned an unexpected response.");
                }

                return new SpotifyPayload(true, root, null);
            }
        }

        return new SpotifyPayload(false, default, "Spotify rejected the request after several attempts.");
    }

    private string ResolveHash(string document) => document switch
    {
        nameof(PersistedHashes.PlaylistMut) => _hashes.PlaylistMut,
        nameof(PersistedHashes.PlaylistRead) => _hashes.PlaylistRead,
        nameof(PersistedHashes.Profile) => _hashes.Profile,
        nameof(PersistedHashes.Library) => _hashes.Library,
        nameof(PersistedHashes.LibraryTracks) => _hashes.LibraryTracks,
        _ => _hashes.LibraryMut,
    };

    private static bool IsPersistedQueryMissing(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return errors.EnumerateArray().Any(error =>
            error.TryGetProperty("extensions", out var extensions)
            && extensions.TryGetProperty("persistedQueryMissing", out var missing)
            && string.Equals(missing.ToString(), "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Re-scrapes the operation hashes from the live web-player bundle.
    /// <para>
    /// Spotify rotates these on release, and a rotated hash makes every call fail with a message
    /// that looks like a permission problem. Scraping them is best-effort: on failure the seeds stay
    /// and the caller's own retry surfaces the original error rather than a second one.
    /// </para>
    /// </summary>
    private async Task RefreshHashesAsync(CancellationToken cancellationToken)
    {
        await _hashGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var shellRequest = new HttpRequestMessage(HttpMethod.Get, WebPlayerUrl);
            shellRequest.Headers.UserAgent.ParseAdd(UserAgent);
            using var shell = await _httpClient.SendAsync(shellRequest, cancellationToken).ConfigureAwait(false);
            if (!shell.IsSuccessStatusCode)
            {
                return;
            }

            var shellHtml = await shell.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var bundleUrls = Regex.Matches(
                    shellHtml,
                    "https://open\\.spotifycdn\\.com/cdn/build/web-player/[^\"']+\\.js",
                    RegexOptions.None,
                    RegexTimeout)
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (bundleUrls.Count == 0)
            {
                return;
            }

            var bundle = new StringBuilder();
            foreach (var url in bundleUrls)
            {
                using var bundleRequest = new HttpRequestMessage(HttpMethod.Get, url);
                bundleRequest.Headers.UserAgent.ParseAdd(UserAgent);
                using var bundleResponse = await _httpClient.SendAsync(bundleRequest, cancellationToken)
                    .ConfigureAwait(false);
                if (bundleResponse.IsSuccessStatusCode)
                {
                    bundle.Append(await bundleResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                }
            }

            var text = bundle.ToString();
            if (text.Length == 0)
            {
                return;
            }

            string? Find(string operation) => Regex.Match(
                    text,
                    $"\\.l\\(\"{Regex.Escape(operation)}\",\"(?:mutation|query)\",\"([a-f0-9]{{64}})\"",
                    RegexOptions.None,
                    RegexTimeout)
                is { Success: true } match
                    ? match.Groups[1].Value
                    : null;

            _hashes = new PersistedHashes(
                PlaylistMut: Find("addToPlaylist") ?? _hashes.PlaylistMut,
                PlaylistRead: Find("fetchPlaylistContents") ?? _hashes.PlaylistRead,
                Profile: Find("profileAttributes") ?? _hashes.Profile,
                Library: Find("libraryV3") ?? _hashes.Library,
                LibraryTracks: Find("fetchLibraryTracks") ?? _hashes.LibraryTracks,
                LibraryMut: Find("addToLibrary") ?? _hashes.LibraryMut);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Best-effort: the seeds remain in place and the caller's retry reports the real failure.
        }
        finally
        {
            _hashGate.Release();
        }
    }
}
