using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     Reads SoundCloud's <c>window.__sc_hydration</c> payload out of a fetched page.
/// </summary>
/// <remarks>
///     <para>
///         SoundCloud renders server-side state as a JSON array assigned to <c>window.__sc_hydration</c> inside
///         a script tag. Each entry is a <c>hydratable</c> discriminator plus an opaque <c>data</c> object; the
///         only ones this engine cares about are <c>sound</c> (a track) and <c>playlist</c> (a set).
///     </para>
///     <para>
///         Nothing here guesses. A page with no hydration array, or one whose entry is missing a field the
///         contracts require, raises a typed failure instead of returning a half-populated object, because a
///         partially resolved track would silently queue a download that cannot succeed.
///     </para>
/// </remarks>
public static partial class SoundCloudHydrationParser
{
    /// <summary>
    ///     Tries to read a numeric SoundCloud track id out of a permalink.
    /// </summary>
    /// <remarks>
    ///     The id is not part of a canonical permalink path, so this only succeeds for the numeric api form
    ///     (<c>soundcloud.com/&lt;id&gt;</c>). A null result is the common case and is not an error: the
    ///     permalink itself is what the engine needs.
    /// </remarks>
    public static string? TryExtractTrackId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length > 0 && segment.All(char.IsDigit) && segment.Length <= 19)
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>
    ///     Whether a URL names a SoundCloud track rather than a set.
    /// </summary>
    /// <remarks>
    ///     A set URL is explicitly not a track URL: resolving one as a track hydrates the playlist and yields
    ///     the set, not something playable.
    /// </remarks>
    public static bool IsSoundCloudTrackUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsSoundCloudHost(uri))
        {
            return false;
        }

        return !IsSetUrl(url);
    }

    /// <summary>
    ///     Whether a host belongs to SoundCloud.
    /// </summary>
    private static bool IsSoundCloudHost(Uri uri)
    {
        var host = uri.Host;
        return host.Equals("soundcloud.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".soundcloud.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("sndcdn.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".sndcdn.com", StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>The hydration entry type for a track.</summary>
    public const string SoundHydratable = "sound";

    /// <summary>The hydration entry type for a user-owned set.</summary>
    public const string PlaylistHydratable = "playlist";

    /// <summary>
    ///     The hydration entry type for an algorithmic <c>/discover/sets/...</c> page.
    /// </summary>
    /// <remarks>
    ///     SoundCloud serves the same collection under a different discriminator, so a parser that only accepts
    ///     <see cref="PlaylistHydratable"/> turns every discover URL into a parse failure even though the page
    ///     carries a complete, ordered track list.
    /// </remarks>
    public const string SystemPlaylistHydratable = "systemPlaylist";

    /// <summary>
    ///     Locates the start of the hydration array.
    /// </summary>
    /// <remarks>
    ///     Only the assignment is matched. The array itself is extracted by bracket counting in
    ///     <see cref="ExtractHydrationJson"/>, because a real payload is deeply nested - a track carries its
    ///     transcodings array inside its media object - and no regular expression can find where that ends
    ///     without mis-reading an inner bracket as the close of the array.
    /// </remarks>
    [GeneratedRegex(@"window\.__sc_hydration\s*=\s*\[")]
    private static partial Regex HydrationAssignmentRegex();

    [GeneratedRegex(@"src\s*=\s*""(https://a-v2\.sndcdn\.com/assets/[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AssetUrlRegex();

    [GeneratedRegex(@"client_id:\s*""([^""]+)""")]
    private static partial Regex ClientIdRegex();

    /// <summary>
    ///     Captures the <c>apiClient</c> hydration entry's <c>data</c> object.
    /// </summary>
    /// <remarks>
    ///     Anchored on the hydratable name and balanced only to the end of that entry's object, which is enough
    ///     because <c>apiClient</c> publishes a short, flat object of scalars.
    /// </remarks>
    [GeneratedRegex(@"""hydratable""\s*:\s*""apiClient""\s*,\s*""data""\s*:\s*(\{[^{}]*\})")]
    private static partial Regex ApiClientHydrationRegex();

    [GeneratedRegex(@"/s-[A-Za-z0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPathRegex();

    /// <summary>
    ///     Parses a track out of a SoundCloud page.
    /// </summary>
    /// <param name="html">The fetched page.</param>
    /// <returns>The resolved track.</returns>
    /// <exception cref="SoundCloudHydrationException">The page carries no track hydration.</exception>
    public static SoundCloudTrack ParseTrack(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var entry = FindHydrationEntry(html, SoundHydratable);
        var data = entry.GetProperty("data");

        var id = ReadLong(data, "id")
                 ?? throw new SoundCloudHydrationException(
                     "The SoundCloud page hydration did not carry a track id.", SoundHydratable);
        var title = ReadString(data, "title");

        return WithExtendedMetadata(
            new SoundCloudTrack
            {
                Id = id,
                Title = title,
                PermalinkUrl = ReadPermalink(data),
                Artist = ReadUploaderName(data),
                ArtworkUrl = ReadArtworkUrl(data),
                DurationMs = ReadDurationMs(data),
                Genre = ReadFirstString(data, "genre") ?? ReadFirstTag(data),
                Isrc = ReadIsrc(data),
                Label = ReadLabel(data),
                ReleaseYear = ReadReleaseYear(data),
                TrackAuthorization = ReadString(data, "track_authorization"),
                Transcodings = ReadTranscodings(data)
            },
            data);
    }

    /// <summary>
    ///     Parses a set out of a SoundCloud page, preserving playlist order.
    /// </summary>
    /// <param name="html">The fetched page.</param>
    /// <returns>The resolved set, whose track list may still contain unresolved stubs.</returns>
    /// <exception cref="SoundCloudHydrationException">The page carries no playlist hydration.</exception>
    public static SoundCloudSet ParseSet(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var entry = FindSetHydrationEntry(html);
        var data = entry.GetProperty("data");

        var tracks = new List<SoundCloudTrack>();
        var position = 0;
        if (data.TryGetProperty("tracks", out var trackArray)
            && trackArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in trackArray.EnumerateArray())
            {
                position++;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var trackId = ReadLong(element, "id");
                if (trackId is null)
                {
                    // A playlist entry with no id at all cannot be expanded and cannot be reported, so it is
                    // dropped rather than queued as a track that resolves to nothing.
                    continue;
                }

                tracks.Add(WithExtendedMetadata(
                    new SoundCloudTrack
                    {
                        Id = trackId.Value,
                        PermalinkUrl = ReadPermalink(element),
                        Title = ReadString(element, "title"),
                        Artist = ReadUploaderName(element),
                        ArtworkUrl = ReadArtworkUrl(element),
                        DurationMs = ReadDurationMs(element),
                        Genre = ReadFirstString(element, "genre") ?? ReadFirstTag(element),
                        Isrc = ReadIsrc(element),
                        Position = position
                    },
                    element));
            }
        }

        return new SoundCloudSet
        {
            Id = ReadLong(data, "id") ?? 0L,
            Title = ReadString(data, "title"),
            PermalinkUrl = ReadPermalink(data),
            Artist = ReadUploaderName(data),
            ArtworkUrl = ReadArtworkUrl(data),

            // "description" first, then the abbreviated form. An algorithmic discover set publishes both and
            // the full one is the more useful of the two.
            Description = ReadFirstNonEmptyString(data, "description", "short_description"),
            Tracks = tracks
        };
    }

    /// <summary>
    ///     Parses an api-v2 search response into tracks, skipping anything that is not a playable sound.
    /// </summary>
    /// <param name="json">The api-v2 response body.</param>
    /// <returns>The playable tracks, in the order SoundCloud returned them.</returns>
    /// <exception cref="SoundCloudHydrationException">The body is not the expected JSON array.</exception>
    public static IReadOnlyList<SoundCloudTrack> ParseSearchResults(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SoundCloudHydrationException(
                "SoundCloud search returned a body that is not JSON.", SoundHydratable, ex);
        }

        using (document)
        {
            // api-v2 wraps results in an object under "collection". A bare array is also accepted because
            // older responses and some api-v2 routes use that shape directly. Reading only the array form
            // made every live search fail with "not a result array" while the fixtures, which used a bare
            // array, kept passing.
            JsonElement array;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                array = document.RootElement;
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object
                     && document.RootElement.TryGetProperty("collection", out var collection)
                     && collection.ValueKind == JsonValueKind.Array)
            {
                array = collection;
            }
            else
            {
                throw new SoundCloudHydrationException(
                    "SoundCloud search returned a body with no result collection.", SoundHydratable);
            }

            var tracks = new List<SoundCloudTrack>();
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!IsPlayableTrack(element))
                {
                    continue;
                }

                var id = ReadLong(element, "id");
                if (id is null)
                {
                    continue;
                }

                tracks.Add(WithExtendedMetadata(
                    new SoundCloudTrack
                    {
                        Id = id.Value,
                        Title = ReadString(element, "title"),
                        PermalinkUrl = ReadPermalink(element),
                        Artist = ReadUploaderName(element),
                        ArtworkUrl = ReadArtworkUrl(element),
                        DurationMs = ReadDurationMs(element),
                        Genre = ReadFirstString(element, "genre") ?? ReadFirstTag(element),
                        Isrc = ReadIsrc(element),
                        Label = ReadString(element, "label_name"),
                        ReleaseYear = ReadReleaseYear(element),
                        Transcodings = ReadTranscodings(element)
                    },
                    element));
            }

            return tracks;
        }
    }

    /// <summary>
    ///     Reads the public <c>client_id</c> out of a page's hydration.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is where SoundCloud actually publishes the value, in the <c>apiClient</c> hydration entry
    ///         it renders for its own front end:
    ///         <c>{"hydratable":"apiClient","data":{"id":"...","isExpiring":false}}</c>.
    ///     </para>
    ///     <para>
    ///         Scraping the JS asset bundles for a <c>client_id</c> literal is the older method and no longer
    ///         works: the bundles now only read the value through a getter, so the literal appears nowhere. It
    ///         is kept below purely as a fallback, not as the primary path.
    ///     </para>
    /// </remarks>
    /// <param name="html">A fetched SoundCloud page.</param>
    /// <returns>The client id, or <see langword="null"/> when the page does not publish one.</returns>
    public static string? ExtractClientIdFromHydration(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = ApiClientHydrationRegex().Match(html);
        if (!match.Success)
        {
            return null;
        }

        // Take the id out of the matched JSON rather than trying to bracket it with the regex, so a
        // re-ordered or extra field cannot leave a truncated value behind.
        try
        {
            using var document = JsonDocument.Parse(match.Groups[1].Value);
            if (document.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    ///     Finds a public <c>client_id</c> in a SoundCloud asset bundle.
    /// </summary>
    /// <param name="assetJavaScript">The fetched bundle.</param>
    /// <returns>The client id, or <see langword="null"/> when this bundle does not carry one.</returns>
    public static string? ExtractClientId(string assetJavaScript)
    {
        if (string.IsNullOrWhiteSpace(assetJavaScript))
        {
            return null;
        }

        var match = ClientIdRegex().Match(assetJavaScript);
        return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value : null;
    }

    /// <summary>
    ///     Lists the asset bundle URLs a SoundCloud homepage links, in document order.
    /// </summary>
    /// <param name="homepageHtml">The fetched homepage.</param>
    /// <returns>The absolute asset URLs.</returns>
    public static IReadOnlyList<string> ExtractAssetUrls(string homepageHtml)
    {
        if (string.IsNullOrWhiteSpace(homepageHtml))
        {
            return [];
        }

        var urls = new List<string>();
        foreach (Match match in AssetUrlRegex().Matches(homepageHtml))
        {
            var url = match.Groups[1].Value;
            if (!urls.Contains(url, StringComparer.Ordinal))
            {
                urls.Add(url);
            }
        }

        return urls;
    }

    /// <summary>
    ///     Whether a URL is a SoundCloud set/playlist rather than a single track.
    /// </summary>
    /// <remarks>
    ///     Both user playlists and the algorithmically curated <c>/discover/sets/...</c> paths contain
    ///     <c>/sets/</c>, which is the routing signal the ported implementation uses.
    /// </remarks>
    public static bool IsSetUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.AbsolutePath.Contains("/sets/", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Splits a private share token out of a track URL.
    /// </summary>
    /// <remarks>
    ///     SoundCloud publishes the token two ways: as <c>?secret_token=s-XXX</c>, or as a trailing
    ///     <c>/s-XXX</c> path segment. The path form is stripped from the URL that gets hydrated, because
    ///     hydrating the token-bearing path yields a different page.
    /// </remarks>
    /// <param name="rawUrl">The URL as the user supplied it.</param>
    /// <returns>The page URL and the token, which may be empty.</returns>
    /// <exception cref="SoundCloudInvalidUrlException">The text is not an absolute URL.</exception>
    public static SoundCloudTrackUrl SplitSecretToken(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)
            || !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
        {
            throw new SoundCloudInvalidUrlException("The SoundCloud track link is not a usable URL.");
        }

        var queryToken = ReadQueryParameter(uri.Query, "secret_token");
        if (queryToken.Length > 0)
        {
            return new SoundCloudTrackUrl(rawUrl.Trim(), queryToken);
        }

        var pathMatch = SecretPathRegex().Match(uri.AbsolutePath);
        if (!pathMatch.Success)
        {
            return new SoundCloudTrackUrl(rawUrl.Trim(), string.Empty);
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath[..^pathMatch.Value.Length]
        };

        return new SoundCloudTrackUrl(builder.Uri.ToString(), pathMatch.Value.TrimStart('/'));
    }

    /// <summary>
    ///     Reads one query parameter out of a raw query string.
    /// </summary>
    /// <remarks>
    ///     Hand-rolled rather than taken from a web helper so this file keeps to the BCL the Services project
    ///     already references.
    /// </remarks>
    private static string ReadQueryParameter(string query, string name)
    {
        if (query.Length <= 1)
        {
            return string.Empty;
        }

        foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (!pair[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return string.Empty;
    }

    /// <summary>
    ///     Returns the JSON array assigned to <c>window.__sc_hydration</c>.
    /// </summary>
    /// <remarks>
    ///     Scans for the matching close bracket while respecting string literals and escapes, because a
    ///     permalink or title inside the payload can contain a bracket character that must not end the scan.
    /// </remarks>
    private static string ExtractHydrationJson(string html, string hydratable)
    {
        var start = HydrationAssignmentRegex().Match(html);
        if (!start.Success)
        {
            throw new SoundCloudHydrationException(
                "The SoundCloud page carried no hydration data.", hydratable);
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        var arrayStart = start.Index + start.Length - 1;

        for (var index = arrayStart; index < html.Length; index++)
        {
            var character = html[index];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return html.Substring(arrayStart, index - arrayStart + 1);
                    }

                    break;
            }
        }

        throw new SoundCloudHydrationException(
            "The SoundCloud page hydration data was truncated.", hydratable);
    }

    /// <summary>
    ///     Finds the set hydration entry, accepting either discriminator SoundCloud publishes.
    /// </summary>
    /// <remarks>
    ///     A user-owned <c>/sets/</c> page hydrates <c>playlist</c>; an algorithmic
    ///     <c>/discover/sets/...</c> page hydrates <c>systemPlaylist</c>. Both carry the same shape -
    ///     <c>id</c>, <c>title</c>, and an ordered <c>tracks</c> array of id-only stubs - so they resolve
    ///     through one path. The user set is preferred when a page somehow carries both, because that is the
    ///     one the reader explicitly navigated to.
    /// </remarks>
    private static JsonElement FindSetHydrationEntry(string html)
    {
        return FindHydrationEntryOrDefault(html, PlaylistHydratable)
               ?? FindHydrationEntryOrDefault(html, SystemPlaylistHydratable)
               ?? throw new SoundCloudHydrationException(
                   $"The SoundCloud page carried no '{PlaylistHydratable}' or '{SystemPlaylistHydratable}' hydration entry.",
                   PlaylistHydratable);
    }

    /// <summary>
    ///     Finds the entry for one discriminator, or <see langword="null"/> when the page has none.
    /// </summary>
    private static JsonElement? FindHydrationEntryOrDefault(string html, string hydratable)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(ExtractHydrationJson(html, hydratable));
        }
        catch (JsonException ex)
        {
            throw new SoundCloudHydrationException(
                "The SoundCloud page hydration data could not be parsed.", hydratable, ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new SoundCloudHydrationException(
                    "The SoundCloud page hydration data is not an array.", hydratable);
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (ReadString(element, "hydratable").Equals(hydratable, StringComparison.OrdinalIgnoreCase))
                {
                    // Returned by value: JsonElement from a disposed document must not escape.
                    return element.Clone();
                }
            }
        }

        return null;
    }

    private static JsonElement FindHydrationEntry(string html, string hydratable)
        => FindHydrationEntryOrDefault(html, hydratable)
           ?? throw new SoundCloudHydrationException(
               $"The SoundCloud page carried no '{hydratable}' hydration entry.", hydratable);

    private static bool IsPlayableTrack(JsonElement element)
    {
        if (ReadLong(element, "kind") is { } kind && kind != 0)
        {
            // 0 = track, 1 = playlist, 2 = artist, 3 = station, 4 = show, 5 = episode.
            return false;
        }

        var streamable = ReadBool(element, "streamable");
        if (streamable == false)
        {
            return false;
        }

        var policy = ReadString(element, "policy");
        return !policy.Equals("block", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<SoundCloudTranscoding> ReadTranscodings(JsonElement data)
    {
        var transcodings = new List<SoundCloudTranscoding>();
        if (!data.TryGetProperty("media", out var media)
            || media.ValueKind != JsonValueKind.Object
            || !media.TryGetProperty("transcodings", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return transcodings;
        }

        foreach (var element in list.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = ReadString(element, "url");
            if (url.Length == 0)
            {
                continue;
            }

            var format = element.TryGetProperty("format", out var formatElement)
                             && formatElement.ValueKind == JsonValueKind.Object
                ? formatElement
                : default;

            transcodings.Add(new SoundCloudTranscoding(
                url,
                ReadString(element, "quality"),
                format.ValueKind == JsonValueKind.Object ? ReadString(format, "mime_type") : string.Empty,
                format.ValueKind == JsonValueKind.Object ? ReadString(format, "protocol") : string.Empty));
        }

        return transcodings;
    }

    private static string ReadPermalink(JsonElement element)
    {
        var permalink = ReadString(element, "permalink_url");
        if (permalink.Length > 0)
        {
            return permalink;
        }

        var uri = ReadUri(element);
        return uri.Length > 0 ? uri : string.Empty;
    }

    /// <summary>
    ///     Reads the uploader's display name.
    /// </summary>
    /// <remarks>
    ///     Hydration nests it under <c>user</c>, which is the authoritative place. The flat <c>username</c>
    ///     is only a fallback for the reduced objects api-v2 returns for playlist stubs, and <c>display_name</c>
    ///     is preferred over <c>username</c> because a user's handle can be a legacy slug while the display
    ///     name is what matching should compare against.
    /// </remarks>
    private static string ReadUploaderName(JsonElement element)
    {
        if (element.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            var displayName = ReadString(user, "display_name");
            if (displayName.Length > 0)
            {
                return displayName;
            }

            var username = ReadString(user, "username");
            if (username.Length > 0)
            {
                return username;
            }
        }

        var flatDisplayName = ReadString(element, "display_name");
        return flatDisplayName.Length > 0 ? flatDisplayName : ReadString(element, "username");
    }

    private static string ReadArtworkUrl(JsonElement element)
    {
        // "calculated_artwork_url" is checked alongside the plain field because an algorithmic
        // /discover/sets page sets "artwork_url" to null and carries the only usable image in the calculated
        // one. Reading the plain field alone left every discover playlist cover-less.
        foreach (var name in new[] { "artwork_url", "calculated_artwork_url", "artwork_url_https" })
        {
            if (element.TryGetProperty(name, out var artwork)
                && artwork.ValueKind == JsonValueKind.String)
            {
                var value = artwork.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    private static string ReadUri(JsonElement element)
    {
        if (!element.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return ReadString(uri, "permalink_url");
    }

    private static string? ReadIsrc(JsonElement element)
    {
        // A top-level isrc wins; the distributor's copy is the fallback. Real payloads carry it in one place or
        // the other, and the documented field is the top-level one.
        var direct = ReadString(element, "isrc");
        if (direct.Length > 0)
        {
            return direct;
        }

        if (!element.TryGetProperty("publisher_metadata", out var publisher)
            || publisher.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var isrc = ReadString(publisher, "isrc");
        return isrc.Length > 0 ? isrc : null;
    }

    /// <summary>
    ///     Applies the identity-bearing metadata that mapping and tagging need to every parsed track.
    /// </summary>
    /// <remarks>
    ///     One helper, applied at every construction site, so the four places a track is built cannot drift
    ///     apart. A field absent from a payload stays null rather than being invented.
    /// </remarks>
    internal static SoundCloudTrack WithExtendedMetadata(SoundCloudTrack track, JsonElement element)
    {
        return track with
        {
            Urn = ReadUrn(element, track.Id),
            MetadataArtist = ReadMetadataArtist(element),
            PublisherArtist = ReadPublisherString(element, "artist"),
            PublisherAlbumTitle = ReadPublisherString(element, "album_title"),
            UploaderUsername = ReadUploaderUsername(element),
            Bpm = ReadBpm(element),
            KeySignature = ReadFirstNonEmptyString(element, "key_signature"),
            TagList = ReadTagList(element),
            ReleaseDate = ReadReleaseDate(element)
        };
    }

    private static string ReadUrn(JsonElement element, long id)
    {
        var urn = ReadString(element, "urn");
        if (urn.Length > 0)
        {
            return urn;
        }

        // Only used when the payload omits the URN. The URN stays the identity either way; this never
        // substitutes the bare numeric id for it.
        return id > 0 ? $"soundcloud:tracks:{id.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
    }

    private static string? ReadMetadataArtist(JsonElement element)
        => ReadFirstNonEmptyString(element, "metadata_artist");

    private static string? ReadPublisherString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty("publisher_metadata", out var publisher)
            || publisher.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var value = ReadString(publisher, propertyName);
        return value.Length > 0 ? value : null;
    }

    private static string? ReadUploaderUsername(JsonElement element)
    {
        if (element.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            var username = ReadString(user, "username");
            if (username.Length > 0)
            {
                return username;
            }
        }

        var flat = ReadString(element, "username");
        return flat.Length > 0 ? flat : null;
    }

    private static int? ReadBpm(JsonElement element)
    {
        if (!element.TryGetProperty("bpm", out var bpm)
            || (bpm.ValueKind != JsonValueKind.Number && bpm.ValueKind != JsonValueKind.String))
        {
            return null;
        }

        if (bpm.ValueKind == JsonValueKind.Number)
        {
            // SoundCloud publishes fractional tempi; truncate the way a tempo tag expects.
            var rounded = bpm.GetDouble();
            return rounded > 0 && rounded < int.MaxValue ? (int)Math.Round(rounded) : null;
        }

        return int.TryParse(bpm.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
               && parsed > 0
            ? parsed
            : null;
    }

    private static IReadOnlyList<string> ReadTagList(JsonElement element)
    {
        if (!element.TryGetProperty("tag_list", out var tags))
        {
            return [];
        }

        var values = new List<string>();
        if (tags.ValueKind == JsonValueKind.String)
        {
            // SoundCloud also publishes this as a single space-separated string.
            foreach (var part in (tags.GetString() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    values.Add(trimmed);
                }
            }
        }
        else if (tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var trimmed = (tag.GetString() ?? string.Empty).Trim();
                if (trimmed.Length > 0)
                {
                    values.Add(trimmed);
                }
            }
        }

        return values;
    }

    private static DateTimeOffset? ReadReleaseDate(JsonElement element)
    {
        if (!element.TryGetProperty("release_date", out var release)
            || release.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            release.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string? ReadFirstNonEmptyString(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return value.Length > 0 ? value : null;
    }

    /// <summary>
    ///     Returns the first of several properties that carries a non-empty string, or null when none does.
    /// </summary>
    private static string? ReadFirstNonEmptyString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            var value = ReadString(element, name);
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadFirstTag(JsonElement element)
    {
        if (!element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var tag in tags.EnumerateArray())
        {
            var name = tag.ValueKind == JsonValueKind.Object ? ReadString(tag, "name") : string.Empty;
            if (name.Length > 0)
            {
                return name;
            }
        }

        return null;
    }

    private static string? ReadFirstString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        // SoundCloud publishes the same field as a bare string on a track and as an array on a collection, so
        // reading only the array form silently lost the genre of every ordinary track.
        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() is { Length: > 0 } text ? text : null;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var value in property.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>
    ///     Reads the record label.
    /// </summary>
    /// <remarks>
    ///     Published inside <c>publisher_metadata</c> as a string on a track, but as a list of label objects on
    ///     some set and api-v2 responses, so both shapes are accepted.
    /// </remarks>
    private static string? ReadLabel(JsonElement element)
    {
        if (element.TryGetProperty("publisher_metadata", out var publisher)
            && publisher.ValueKind == JsonValueKind.Object)
        {
            var name = ReadString(publisher, "label_name");
            if (name.Length > 0)
            {
                return name;
            }

            if (publisher.TryGetProperty("label", out var label))
            {
                if (label.ValueKind == JsonValueKind.String && label.GetString() is { Length: > 0 } text)
                {
                    return text;
                }

                if (label.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in label.EnumerateArray())
                    {
                        var entryName = entry.ValueKind == JsonValueKind.Object ? ReadString(entry, "name") : string.Empty;
                        if (entryName.Length > 0)
                        {
                            return entryName;
                        }
                    }
                }
            }
        }

        var flat = ReadString(element, "label_name");
        return flat.Length > 0 ? flat : null;
    }

    private static int? ReadReleaseYear(JsonElement element)
    {
        if (!element.TryGetProperty("publisher_metadata", out var publisher)
            || publisher.ValueKind != JsonValueKind.Object
            || !publisher.TryGetProperty("release_year", out var year)
            || year.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var value = year.GetInt32();
        return value > 0 ? value : null;
    }

    /// <summary>
    ///     Reads a SoundCloud duration.
    /// </summary>
    /// <remarks>
    ///     SoundCloud already publishes duration in milliseconds, which is the unit the contracts and the
    ///     shared validator both use. No conversion is applied; a value that arrives in seconds would be
    ///     visibly wrong in the tracklist and is better surfaced than silently rescaled.
    /// </remarks>
    /// <summary>
    ///     Reads a track's length, preferring <c>full_duration</c> over <c>duration</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         SoundCloud publishes both. Observed live on the same track: <c>duration: 30000</c> with
    ///         <c>full_duration: 183137</c>, where the 30-second value is the preview SoundCloud actually
    ///         streams and 183 seconds is the recording's real length - which is also what Deezer reports for
    ///         the same ISRC.
    ///     </para>
    ///     <para>
    ///         The full length is therefore the canonical one for matching and for written metadata such as
    ///         TLEN: using the preview length made an exact-ISRC match fail the duration tolerance against a
    ///         correct Deezer candidate. It does not affect transfer length, which the HLS downloader
    ///         determines from the segments it actually fetches.
    ///     </para>
    ///     <para>
    ///         <c>full_duration</c> is used only when it is present, numeric and positive; otherwise
    ///         <c>duration</c> is used, so a track that publishes no full length is unaffected.
    ///     </para>
    /// </remarks>
    private static int ReadDurationMs(JsonElement element)
    {
        var full = ReadPositiveDurationMs(element, "full_duration");
        return full > 0 ? full : ReadPositiveDurationMs(element, "duration");
    }

    private static int ReadPositiveDurationMs(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) && value > 0
                => (int)Math.Min(value, int.MaxValue),
            JsonValueKind.Number when property.TryGetDouble(out var fraction) && fraction > 0
                => (int)Math.Round(fraction * 1000d),
            _ => 0
        };
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}