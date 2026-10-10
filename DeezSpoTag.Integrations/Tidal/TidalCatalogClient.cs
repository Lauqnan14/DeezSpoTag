using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    string? Isrc);

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
    private const string ApiBase = "https://openapi.tidal.com/v2";

    private const string CatalogInclude = "artists,albums";

    private const string SearchInclude = "tracks,tracks.artists,tracks.albums";

    private readonly TargetApiTransport _transport;

    /// <summary>Supplies a current TIDAL access token, for the reason documented on the destination.</summary>
    private readonly Func<CancellationToken, Task<TidalTargetCredentials?>> _credentials;

    public TidalCatalogClient(
        TargetApiTransport transport,
        Func<CancellationToken, Task<TidalTargetCredentials?>> credentials)
    {
        _transport = transport;
        _credentials = credentials;
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
        if (string.IsNullOrWhiteSpace(id) || !seen.Add(id!))
        {
            return;
        }

        var attributes = value.TryGetProperty("attributes", out var node) ? node : default;
        var title = TitleWithVersion(ReadString(attributes, "title"), ReadString(attributes, "version"));
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        tracks.Add(new TidalCatalogTrack(
            id!,
            title!,
            JoinNames(value, identifier, included, "artists"),
            ReadString(RelatedAttributes(value, identifier, included, "albums") ?? default, "title"),
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

        return identifier.TryGetProperty("attributes", out _) ? identifier : null;
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
                    resolved.Value.TryGetProperty("attributes", out var value) ? value : default,
                    "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name!);
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
                return resolved.Value.TryGetProperty("attributes", out var value) ? value : default;
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
        var digits = string.Empty;

        foreach (var character in text)
        {
            if (char.IsDigit(character) || character == '.')
            {
                digits += character;
                continue;
            }

            switch (character)
            {
                case 'D':
                case 'd':
                    days = Number(digits);
                    parsed = true;
                    break;
                case 'H':
                case 'h':
                    hours = Number(digits);
                    parsed = true;
                    break;
                case 'M':
                case 'm':
                    minutes = Number(digits);
                    parsed = true;
                    break;
                case 'S':
                case 's':
                    seconds = Number(digits);
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

            digits = string.Empty;
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
