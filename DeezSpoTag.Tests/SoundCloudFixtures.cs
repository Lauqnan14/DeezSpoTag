using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DeezSpoTag.Tests;

/// <summary>
///     Loads the SoundCloud protocol fixtures.
/// </summary>
/// <remarks>
///     Resolved by walking up from the test binary until the repository root is found, which matches how the
///     existing fixture-based tests locate their data and needs no extra csproj wiring.
/// </remarks>
internal static class SoundCloudFixtures
{
    private static readonly Lazy<string> Root = new(FindFixturesRoot);

    /// <summary>Gets the <c>track-page.html</c> hydration fixture.</summary>
    public static string TrackPageHtml => Read("track-page.html");

    /// <summary>Gets the set page, wrapped in the hydration assignment a real page uses.</summary>
    public static string SetPageHtml => BuildHydrationPage(Read("set-page.json"));

    /// <summary>Gets the <c>client-asset.js</c> bundle fixture.</summary>
    public static string ClientAssetJs => Read("client-asset.js");

    /// <summary>Gets the homepage fixture that links the asset bundles.</summary>
    public static string HomepageHtml => Read("homepage.html");

    /// <summary>
    ///     A search response in the shape api-v2 actually serves.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Observed live: <c>/search/tracks</c> returns an <b>object</b> whose results sit under
    ///         <c>collection</c>, not a bare JSON array. A fixture built as a bare array let the parser pass
    ///         its whole suite while every live search failed with "not a result array", so the wrapped shape
    ///         is what the fixtures use.
    ///     </para>
    /// </remarks>
    /// <param name="tracksJson">The individual track objects, as a JSON array body.</param>
    public static string SearchCollection(string tracksJson)
        => "{\"collection\":" + tracksJson + ",\"next_href\":null}";

    /// <summary>
    ///     One api-v2 search result, trimmed to the fields the parser reads.
    /// </summary>
    /// <param name="id">Numeric id, also used to derive the URN.</param>
    /// <param name="title">Track title.</param>
    /// <param name="permalink">Canonical permalink.</param>
    /// <param name="genre">Advertised genre.</param>
    public static string SearchTrackJson(long id, string title, string permalink, string genre = "Hip-Hop")
        => "{\"id\":" + id
           + ",\"kind\":\"track\""
           + ",\"title\":\"" + title + "\""
           + ",\"permalink_url\":\"" + permalink + "\""
           + ",\"genre\":\"" + genre + "\""
           + ",\"duration\":30000"
           + ",\"streamable\":true"
           + ",\"policy\":\"ALLOW\""
           + ",\"user\":{\"id\":1,\"username\":\"some-uploader\"}"
           + ",\"media\":{\"transcodings\":[{\"url\":\"https://api-v2.soundcloud.com/media/soundcloud:tracks:" + id
           + ":stream/hls\",\"preset\":\"mp3_0_0\",\"format\":\"mp3\",\"quality\":\"sq\",\"protocol\":\"hls\"}]}}";

    /// <summary>
    ///     A full search response holding the supplied track objects.
    /// </summary>
    /// <param name="tracksJson">Comma separated track objects.</param>
    public static string SearchResponse(string tracksJson)
        => SearchCollection("[" + tracksJson + "]");

    /// <summary>
    ///     The public <c>client_id</c> the real homepage publishes in its <c>apiClient</c> hydration.
    /// </summary>
    /// <remarks>
    ///     Taken from a live SoundCloud homepage so the discovery test asserts against the shape and value
    ///     SoundCloud actually serves rather than an invented one. The value rotates; discovery must read
    ///     whatever the page currently publishes.
    /// </remarks>
    public const string PublishedClientId = "Wq8jpsB4RfUsrezgEFDFfBGhkClF0sUN";

    /// <summary>Gets the plain (unencrypted) media playlist fixture.</summary>
    public static string PlainPlaylist => Read("plain.m3u8");

    /// <summary>Gets the AES-128 encrypted media playlist fixture.</summary>
    public static string EncryptedPlaylist => Read("encrypted.m3u8");

    /// <summary>
    ///     Builds a set page whose playlist carries exactly the supplied stub ids, in order.
    /// </summary>
    /// <remarks>
    ///     Stubs are what SoundCloud publishes when a set is large: ids only, no permalink. The client has to
    ///     expand them through api-v2 without disturbing the order they arrived in.
    /// </remarks>
    public static string BuildSetPageHtml(IReadOnlyList<string> stubIds)
    {
        var tracks = stubIds
            .Select(id => $$"""{"id":{{id}}}""")
            .ToList();

        var joined = string.Join(",", tracks);
        var payload =
            "[{\"hydratable\":\"playlist\",\"data\":{"
            + "\"hydratable\":\"playlist\",\"kind\":\"playlist\",\"id\":7,\"title\":\"Test Set\","
            + "\"permalink_url\":\"https://soundcloud.com/test-artist/sets/test-set\","
            + "\"user\":{\"username\":\"test-artist\",\"display_name\":\"Test Artist\"},"
            + "\"tracks\":[" + joined + "]}}]";

        return BuildHydrationPage(payload);
    }

    /// <summary>
    ///     Builds a <c>/discover/sets/...</c> page: the algorithmic shape SoundCloud actually serves.
    /// </summary>
    /// <remarks>
    ///     Mirrors a real trending-by-genre page, which carries its collection under
    ///     <c>systemPlaylist</c> with id-only stub tracks and a <c>soundcloud:system-playlists:...</c> urn
    ///     rather than a numeric id.
    /// </remarks>
    public static string BuildDiscoverPageHtml(IReadOnlyList<string> stubIds)
        => BuildDiscoverPageHtml(
            stubIds,
            title: "Hip Hop",
            description: "Trending tracks in Hip Hop",
            shortDescription: "Trending");

    /// <summary>
    ///     Builds a <c>/discover/sets/...</c> page whose header fields can be varied.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <paramref name="artworkUrl" /> defaults to null on purpose. A real algorithmic page sets
    ///         <c>artwork_url</c> to null and carries its only usable image in
    ///         <c>calculated_artwork_url</c>, which is what made the header cover-less.
    ///     </para>
    /// </remarks>
    public static string BuildDiscoverPageHtml(
        IReadOnlyList<string> stubIds,
        string title,
        string? description,
        string? shortDescription,
        string? artworkUrl = null,
        string? calculatedArtworkUrl = "https://i1.sndcdn.com/artworks-discover-large.jpg")
    {
        var tracks = stubIds
            .Select(id => "{\"id\":" + id + "}")
            .ToList();

        var joined = string.Join(",", tracks);
        var payload =
            "[{\"hydratable\":\"anonymousId\",\"data\":\"anon\"},"
            + "{\"hydratable\":\"apiClient\",\"data\":{\"clientId\":\"test-client-id\"}},"
            + "{\"hydratable\":\"systemPlaylist\",\"data\":{"
            + "\"urn\":\"soundcloud:system-playlists:trending-by-genre:hip-hop\","
            + "\"kind\":\"system-playlist\","
            + "\"permalink\":\"discover/sets/trending-by-genre:hip-hop\","
            + "\"permalink_url\":\"https://soundcloud.com/discover/sets/trending-by-genre:hip-hop\","
            + "\"title\":" + Json(title) + ","
            + "\"description\":" + Json(description) + ","
            + "\"short_description\":" + Json(shortDescription) + ","
            + "\"artwork_url\":" + (artworkUrl is null ? "null" : Json(artworkUrl)) + ","
            + "\"calculated_artwork_url\":" + (calculatedArtworkUrl is null ? "null" : Json(calculatedArtworkUrl)) + ","
            + "\"user\":{\"username\":\"soundcloud\",\"display_name\":\"SoundCloud\"},"
            + "\"tracks\":[" + joined + "]}}]";

        return BuildHydrationPage(payload);
    }

    private static string Json(string? value)
        => value is null ? "null" : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    ///     Wraps a raw hydration array in the page shape the parser scans for.
    /// </summary>
    public static string BuildHydrationPage(string hydrationJson)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html><html><head>");
        builder.AppendLine("<script>window.__sc_hydration = ").Append(hydrationJson).AppendLine(";</script>");
        builder.AppendLine("</head><body></body></html>");
        return builder.ToString();
    }

    /// <summary>Gets the api-v2 stream wrapper that points at a media playlist.</summary>
    public static string StreamResponse(string playlistUrl = "https://media.sndcdn.com/hls/playlist.m3u8")
        => "{\"url\":\"" + playlistUrl + "\",\"snipped\":false}";

    private static string Read(string name)
        => File.ReadAllText(Path.Combine(Root.Value, name));

    private static string FindFixturesRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "DeezSpoTag.Tests", "Fixtures", "SoundCloud");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the SoundCloud fixtures directory above '{AppContext.BaseDirectory}'.");
    }
}