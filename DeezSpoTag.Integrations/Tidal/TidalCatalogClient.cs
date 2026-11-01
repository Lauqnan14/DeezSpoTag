using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeezSpoTag.Integrations.Tidal;

/// <summary>A TIDAL catalog track, with the metadata a matcher scores it against.</summary>
public sealed record TidalCatalogTrack(
    string Id,
    string Title,
    string? Artist,
    string? Album,
    int? DurationMs,
    string? Isrc,
    bool? Ai = null);

/// <summary>
/// Reads TIDAL's catalog - by ISRC and by query - for identity resolution.
/// <para>
/// This is the same <c>openapi.tidal.com/v2</c> service the playlist destination already writes
/// to, reached through the same transport, so a TIDAL sync needs one credential and one pacing
/// policy rather than a second account. It is a read-only catalog client: it never touches a
/// playlist.
/// </para>
/// <para>
/// The two routes are the reference's, in the reference's order. <c>filter[isrc]</c> first,
/// because an ISRC is a hard cross-provider identifier and a hit there is not a guess;
/// <c>searchResults</c> only when there is no ISRC or the ISRC matched nothing. What comes back is
/// then put through the app's own validator rather than taken as the first row, because a catalog
/// id that lands the wrong recording on a user's playlist is worse than no id at all.
/// </para>
/// </summary>
public sealed class TidalCatalogClient
{
    private const string TitleProperty = "title";
    private const string AttributesProperty = "attributes";
    private const string ApiBase = "https://openapi.tidal.com/v2";

    private const string CatalogInclude = "artists,albums";

    private const string SearchInclude = "tracks,tracks.artists,tracks.albums";

    private readonly TargetApiTransport _transport;
    private readonly Uri _radioApiBase;

    /// <summary>Supplies a current TIDAL access token, for the reason documented on the destination.</summary>
    private readonly Func<CancellationToken, Task<TidalTargetCredentials?>> _credentials;

    public TidalCatalogClient(
        TargetApiTransport transport,
        Func<CancellationToken, Task<TidalTargetCredentials?>> credentials,
        Uri? radioApiBase = null)
    {
        _transport = transport;
        _credentials = credentials;
        _radioApiBase = radioApiBase ?? new UriBuilder(Uri.UriSchemeHttps, "api.tidal.com") { Path = "v1/" }.Uri;
    }

    /// <summary>Tracks carrying this ISRC. Empty means the catalog holds no such recording.</summary>
    public Task<IReadOnlyList<TidalCatalogTrack>> GetTracksByIsrcAsync(
        string isrc,
        CancellationToken cancellationToken = default)
        => ReadAsync(
            $"tracks?filter[isrc]={Uri.EscapeDataString(isrc ?? string.Empty)}&include={CatalogInclude}",
            cancellationToken);

    /// <summary>Tracks matching this query, in TIDAL's own relevance order.</summary>
    public Task<IReadOnlyList<TidalCatalogTrack>> SearchTracksAsync(
        string query,
        CancellationToken cancellationToken = default)
        => ReadAsync(
            $"searchResults?filter[query]={Uri.EscapeDataString(query ?? string.Empty)}&include={SearchInclude}",
            cancellationToken);

    /// <summary>
    /// Runs one catalog request. A transport failure or an unreadable body yields an empty list
    /// rather than throwing: the resolver already treats "nothing found" as one track's problem
    /// and moves on, so a throwing client would fail the whole pass over one lookup.
    /// </summary>
    private async Task<IReadOnlyList<TidalCatalogTrack>> ReadAsync(
        string pathAndQuery,
        CancellationToken cancellationToken)
    {
        var credentials = await _credentials(cancellationToken).ConfigureAwait(false);
        if (!TidalTargetCredentials.IsUsable(credentials))
        {
            return Array.Empty<TidalCatalogTrack>();
        }

        var response = await _transport.GetAsync(
            $"{ApiBase}/{pathAndQuery}&countryCode={credentials!.Country}",
            Headers(credentials),
            cancellationToken).ConfigureAwait(false);

        if (!response.Reached || response.TryParseJson() is not { } json)
        {
            return Array.Empty<TidalCatalogTrack>();
        }

        return ReadTracks(json);
    }

    public async Task<TidalTrackRadioResult> GetTrackRadioAsync(string trackId, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var credentials = await _credentials(cancellationToken).ConfigureAwait(false);
        if (!TidalTargetCredentials.IsUsable(credentials))
            return RadioFailure("tidal_application_token_unavailable", TidalTrackRadioOutcome.Unavailable);
        if (string.IsNullOrWhiteSpace(trackId) || !trackId.All(char.IsAsciiDigit))
            return RadioFailure("tidal_invalid_seed");
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + credentials!.AccessToken,
            ["Accept"] = "application/json"
        };
        var request = new RadioRequest(trackId, Math.Clamp(limit, 1, 300), headers, Uri.EscapeDataString(credentials.Country));
        var mixId = await ReadRadioMixIdAsync(request, cancellationToken).ConfigureAwait(false);
        if (mixId is null) return RadioFailure("tidal_track_mix_unavailable");
        var response = await _transport.GetAsync(
            $"{_radioApiBase}pages/mix?mixId={Uri.EscapeDataString(mixId)}&countryCode={request.Country}&deviceType=BROWSER",
            headers, cancellationToken).ConfigureAwait(false);
        if (!response.Reached || response.TryParseJson() is not { } page || page.ValueKind != JsonValueKind.Object
            || !page.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return RadioFailure("tidal_mix_page_unavailable");
        return await ReadRadioRowsAsync(rows, request, cancellationToken).ConfigureAwait(false);
    }

    private sealed record RadioRequest(string TrackId, int Limit, IReadOnlyDictionary<string, string> RequestHeaders, string Country);

    private static TidalTrackRadioResult RadioFailure(string reason, TidalTrackRadioOutcome outcome = TidalTrackRadioOutcome.Failed)
        => new(outcome, [], reason);

    private async Task<string?> ReadRadioMixIdAsync(RadioRequest request, CancellationToken cancellationToken)
    {
        var metadata = await _transport.GetAsync(
            $"{_radioApiBase}tracks/{request.TrackId}?countryCode={request.Country}", request.RequestHeaders, cancellationToken).ConfigureAwait(false);
        if (!metadata.Reached || metadata.TryParseJson() is not { } seed || seed.ValueKind != JsonValueKind.Object
            || !seed.TryGetProperty("mixes", out var mixes) || mixes.ValueKind != JsonValueKind.Object)
            return null;
        if (!mixes.TryGetProperty("TRACK_MIX", out var mix) || mix.ValueKind != JsonValueKind.String)
            return null;
        var mixId = mix.GetString();
        return string.IsNullOrWhiteSpace(mixId) ? null : mixId;
    }

    private async Task<TidalTrackRadioResult> ReadRadioRowsAsync(JsonElement rows, RadioRequest request, CancellationToken cancellationToken)
    {
        var tracks = new List<TidalCatalogTrack>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var foundModule = false;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
                return RadioFailure("tidal_malformed_mix");
            foreach (var module in modules.EnumerateArray().Where(module => ReadString(module, "type") == "TRACK_LIST"))
            {
                foundModule = true;
                var result = await AppendRadioModuleAsync(module, request, tracks, seen, requested, cancellationToken).ConfigureAwait(false);
                if (result is not null) return result;
            }
        }
        if (!foundModule) return RadioFailure("tidal_track_list_missing");
        return tracks.Count > 0
            ? new(TidalTrackRadioOutcome.Ready, tracks, null)
            : new(TidalTrackRadioOutcome.Empty, tracks, "tidal_track_mix_empty");
    }

    private async Task<TidalTrackRadioResult?> AppendRadioModuleAsync(
        JsonElement module, RadioRequest request, List<TidalCatalogTrack> tracks,
        HashSet<string> seen, HashSet<string> requested, CancellationToken cancellationToken)
    {
        if (!TryReadPagedList(module, out var list))
            return RadioFailure("tidal_malformed_mix");
        var supportsPaging = SupportsRadioPaging(module);
        for (var pageIndex = 0; pageIndex < request.Limit; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!list.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return RadioFailure("tidal_malformed_mix");
            var result = AppendRadioTracks(items, request, tracks, seen);
            if (result is not null) return result;
            if (!supportsPaging) break;
            var next = await ReadNextRadioPageAsync(list, items.GetArrayLength(), request, tracks.Count, requested, cancellationToken).ConfigureAwait(false);
            if (next.Error is not null) return RadioFailure(next.Error);
            if (next.Page is null) break;
            list = next.Page.Value;
            if (pageIndex == request.Limit - 1) return RadioFailure("tidal_paging_incomplete");
        }
        return null;
    }

    private static bool TryReadPagedList(JsonElement module, out JsonElement list)
        => module.TryGetProperty("pagedList", out list) && list.ValueKind == JsonValueKind.Object;

    private static bool SupportsRadioPaging(JsonElement module)
        => module.TryGetProperty("supportsPaging", out var paging) && paging.ValueKind == JsonValueKind.True;

    private static TidalTrackRadioResult? AppendRadioTracks(
        JsonElement items, RadioRequest request, List<TidalCatalogTrack> tracks, HashSet<string> seen)
    {
        foreach (var item in items.EnumerateArray())
        {
            var track = ReadRadioTrack(item);
            if (track is null) return RadioFailure("tidal_invalid_track");
            if (track.Id != request.TrackId && seen.Add(track.Id)) tracks.Add(track);
            if (tracks.Count >= request.Limit) return new(TidalTrackRadioOutcome.Ready, tracks, null);
        }
        return null;
    }

    private async Task<(JsonElement? Page, string? Error)> ReadNextRadioPageAsync(
        JsonElement list, int itemCount, RadioRequest request, int requestedTrackCount, HashSet<string> requested, CancellationToken cancellationToken)
    {
        if (!list.TryGetProperty("offset", out var offsetNode) || !offsetNode.TryGetInt32(out var offset)
            || !list.TryGetProperty("totalNumberOfItems", out var totalNode) || !totalNode.TryGetInt32(out var total))
            return (null, "tidal_invalid_paging");
        var nextOffset = offset + itemCount;
        if (nextOffset >= total) return (null, null);
        var path = ReadString(list, "dataApiPath");
        if (itemCount == 0 || path is null || !TryResolveRadioPage(path, out var uri))
            return (null, "tidal_invalid_paging");
        var url = uri.GetLeftPart(UriPartial.Path) + $"?countryCode={request.Country}&offset={nextOffset}&limit={request.Limit - requestedTrackCount}";
        if (!requested.Add(url)) return (null, "tidal_paging_cycle");
        var more = await _transport.GetAsync(url, request.RequestHeaders, cancellationToken).ConfigureAwait(false);
        if (!more.Reached || more.TryParseJson() is not { } next || next.ValueKind != JsonValueKind.Object)
            return (null, "tidal_page_failed");
        return (next, null);
    }

    private bool TryResolveRadioPage(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri)
        => Uri.TryCreate(_radioApiBase, path, out uri)
            && uri.Host == _radioApiBase.Host && uri.Scheme == _radioApiBase.Scheme
            && uri.AbsolutePath.StartsWith(_radioApiBase.AbsolutePath, StringComparison.Ordinal);

    private static TidalCatalogTrack? ReadRadioTrack(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var id = ReadString(item, "id");
        var title = ReadString(item, TitleProperty);
        if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(title)
            || !item.TryGetProperty("duration", out var duration) || !duration.TryGetInt32(out var seconds) || seconds <= 0 || seconds > int.MaxValue / 1000) return null;
        string? artist = null, album = null;
        if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
            artist = string.Join(", ", artists.EnumerateArray().Select(a => ReadString(a, "name")).Where(n => !string.IsNullOrWhiteSpace(n)));
        if (item.TryGetProperty("album", out var albumNode) && albumNode.ValueKind == JsonValueKind.Object) album = ReadString(albumNode, TitleProperty);
        bool? ai = item.TryGetProperty("ai", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;
        return new(id, title, artist, album, seconds * 1000, ReadString(item, "isrc"), ai);
    }

    private static IReadOnlyDictionary<string, string> Headers(TidalTargetCredentials credentials)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + credentials.AccessToken,
            ["Accept"] = "application/vnd.api+json",
            ["Content-Type"] = "application/vnd.api+json",
        };

    /// <summary>
    /// Reads a JSON:API document into track rows.
    /// <para>
    /// A catalog response carries identifiers in <c>data</c> and the full resources in
    /// <c>included</c>, joined by <c>type/id</c>. A search response wraps those identifiers one
    /// level deeper, under a single <c>searchResults</c> row, so both shapes are unwrapped here
    /// rather than at every call site.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<TidalCatalogTrack> ReadTracks(JsonElement body)
    {
        var tracks = new List<TidalCatalogTrack>();
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return tracks;
        }

        var included = ReadIncludedResources(body);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in data.EnumerateArray())
        {
            var type = ReadString(node, "type");
            if (string.Equals(type, "searchResults", StringComparison.Ordinal))
            {
                foreach (var identifier in Relationship(node, "tracks"))
                {
                    AddTrack(identifier, included, tracks, seen);
                }
            }
            else if (string.Equals(type, "tracks", StringComparison.Ordinal))
            {
                AddTrack(node, included, tracks, seen);
            }
        }

        return tracks;
    }

    private static Dictionary<string, JsonElement> ReadIncludedResources(JsonElement body)
    {
        var included = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (body.TryGetProperty("included", out var includedNode) && includedNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var resource in includedNode.EnumerateArray())
            {
                var key = ResourceKey(resource);
                if (key is not null)
                {
                    included[key] = resource;
                }
            }
        }

        return included;
    }

    private static IEnumerable<JsonElement> Relationship(JsonElement node, string relationship)
        => node.TryGetProperty("relationships", out var relationships)
           && relationships.TryGetProperty(relationship, out var group)
           && group.TryGetProperty("data", out var list)
           && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static void AddTrack(
        JsonElement identifier,
        IReadOnlyDictionary<string, JsonElement> included,
        List<TidalCatalogTrack> tracks,
        HashSet<string> seen)
    {
        if (!string.Equals(ReadString(identifier, "type"), "tracks", StringComparison.Ordinal))
        {
            return;
        }

        // An identifier with no attributes tells us nothing on its own, and its catalog resource
        // was not asked for; a track id with no title is not a candidate, it is an address.
        var resource = Resolve(identifier, included);
        if (resource is null)
        {
            return;
        }

        var value = resource.Value;
        var id = ReadString(value, "id") ?? ReadString(identifier, "id");
        if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
        {
            return;
        }

        var attributes = value.TryGetProperty(AttributesProperty, out var node) ? node : default;
        var title = TitleWithVersion(ReadString(attributes, TitleProperty), ReadString(attributes, "version"));
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        tracks.Add(new TidalCatalogTrack(
            id,
            title,
            JoinNames(value, identifier, included, "artists"),
            ReadString(RelatedAttributes(value, identifier, included, "albums") ?? default, TitleProperty),
            ReadDurationMs(attributes),
            ReadString(attributes, "isrc")));
    }

    /// <summary>The full resource behind an identifier, or null when the catalog supplied none.</summary>
    private static JsonElement? Resolve(JsonElement identifier, IReadOnlyDictionary<string, JsonElement> included)
    {
        var key = ResourceKey(identifier);
        if (key is not null && included.TryGetValue(key, out var resource))
        {
            return resource;
        }

        return identifier.TryGetProperty(AttributesProperty, out _) ? identifier : null;
    }

    private static string? ResourceKey(JsonElement resource)
    {
        var type = ReadString(resource, "type");
        var id = ReadString(resource, "id");
        return type is null || id is null ? null : $"{type}/{id}";
    }

    private static string? JoinNames(
        JsonElement resource,
        JsonElement identifier,
        IReadOnlyDictionary<string, JsonElement> included,
        string relationship)
    {
        var names = new List<string>();
        foreach (var source in new[] { resource, identifier })
        {
            if (names.Count > 0)
            {
                break;
            }

            foreach (var related in Relationship(source, relationship))
            {
                var resolved = Resolve(related, included);
                if (resolved is null)
                {
                    continue;
                }

                var name = ReadString(
                    resolved.Value.TryGetProperty(AttributesProperty, out var value) ? value : default,
                    "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static JsonElement? RelatedAttributes(
        JsonElement resource,
        JsonElement identifier,
        IReadOnlyDictionary<string, JsonElement> included,
        string relationship)
    {
        foreach (var source in new[] { resource, identifier })
        {
            var related = Relationship(source, relationship).FirstOrDefault();
            if (related.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            var resolved = Resolve(related, included);
            if (resolved is not null)
            {
                return resolved.Value.TryGetProperty(AttributesProperty, out var value) ? value : default;
            }
        }

        return null;
    }

    /// <summary>
    /// A track's duration in milliseconds, however the endpoint chose to spell it: an integer
    /// count of seconds (the catalog routes) or an ISO-8601 period (the relationship routes).
    /// Anything unparseable becomes null rather than 0, because 0 fails every duration check as a
    /// confidently wrong value while null simply declines to check duration at all.
    /// </summary>
    private static int? ReadDurationMs(JsonElement attributes)
    {
        var text = ReadScalar(attributes, "duration");
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            return whole > 0 && whole <= int.MaxValue / 1000 ? (int)(whole * 1000) : null;
        }

        var days = 0d;
        var hours = 0d;
        var minutes = 0d;
        var seconds = 0d;
        var parsed = false;
        var digits = new StringBuilder();

        foreach (var character in text)
        {
            if (char.IsDigit(character) || character == '.')
            {
                digits.Append(character);
                continue;
            }

            switch (character)
            {
                case 'D':
                case 'd':
                    days = Number(digits.ToString());
                    parsed = true;
                    break;
                case 'H':
                case 'h':
                    hours = Number(digits.ToString());
                    parsed = true;
                    break;
                case 'M':
                case 'm':
                    minutes = Number(digits.ToString());
                    parsed = true;
                    break;
                case 'S':
                case 's':
                    seconds = Number(digits.ToString());
                    parsed = true;
                    break;
                case 'P':
                case 'p':
                case 'T':
                case 't':
                    break;
                default:
                    return null;
            }

            digits.Clear();
        }

        if (!parsed)
        {
            return null;
        }

        var totalMs = ((days * 86_400_000) + (hours * 3_600_000) + (minutes * 60_000) + (seconds * 1000));
        return totalMs is > 0 and <= int.MaxValue ? (int)totalMs : null;

        static double Number(string value)
            => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }

    /// <summary>Keeps a provider's separate recording label visible, as the reference does.</summary>
    private static string TitleWithVersion(string? title, string? version)
    {
        var baseTitle = (title ?? string.Empty).Trim();
        var label = (version ?? string.Empty).Trim().Trim('(', ')', '[', ']').Trim();
        if (label.Length == 0 || baseTitle.Contains(label, StringComparison.OrdinalIgnoreCase))
        {
            return baseTitle;
        }

        return $"{baseTitle} ({label})";
    }

    private static string? ReadString(JsonElement node, string property)
        => node.ValueKind == JsonValueKind.Object ? ReadScalar(node, property) : null;

    private static string? ReadScalar(JsonElement node, string property)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
    }
}

public enum TidalTrackRadioOutcome { Ready, Empty, Unavailable, Failed }
public sealed record TidalTrackRadioResult(TidalTrackRadioOutcome Outcome, IReadOnlyList<TidalCatalogTrack> Tracks, string? Reason);
