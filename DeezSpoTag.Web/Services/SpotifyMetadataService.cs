using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Linq;
using System.Text.RegularExpressions;
using System.Globalization;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyMetadataService
{
    private sealed record ParsedArtistContributors(string? Artists, IReadOnlyList<string>? ArtistIds);
    private sealed record ParsedAlbumContext(
        string? AlbumName,
        string? AlbumId,
        string? AlbumArtist,
        string? ImageUrl,
        string? ReleaseDate,
        string? ReleaseDatePrecision,
        string? Label,
        IReadOnlyList<string>? Genres,
        IReadOnlyList<string>? AvailableMarkets,
        IReadOnlyList<SpotifyCopyrightInfo>? Copyrights,
        string? CopyrightText,
        int? TrackTotal);
    private sealed record ParsedAudioFeatureHydration(
        Dictionary<string, SpotifyAudioFeatures> Cached,
        Dictionary<string, SpotifyAudioFeatures>? Fetched);
    private sealed record ParsedTrackNumbers(
        int? DurationMs,
        int? TrackNumber,
        int? DiscNumber,
        bool? ExplicitFlag);
    private sealed record ResolvedSpotifyAccounts(
        SpotifyUserAccount? UserAccount,
        SpotifyAccount? PlatformAccount);

    private const string PlaylistType = "playlist";
    private const string TrackType = "track";
    private const string AlbumType = "album";
    private const string ArtistType = "artist";
    private const string LibrespotSource = "librespot";
    private const string PopularityKey = "popularity";
    private const string PortraitGroupKey = "portrait_group";
    private const string EastAfricaRegion = "east_africa";
    private const string WestAfricaRegion = "west_africa";
    private const string SouthernAfricaRegion = "southern_africa";
    private const string CentralFrancophoneAfricaRegion = "central_francophone_africa";
    private const string PanAfricanRegion = "pan_african";
    private const string GlobalRegion = "global";
    private const string AfrobeatsGenre = "afrobeats";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private const string InitialStateStartMarker = "<script id=\"initialState\" type=\"text/plain\">";
    private const string ScriptEndMarker = "</script>";
    private static readonly Regex InitialStateRegex = new(
        "<script id=\"initialState\" type=\"text/plain\">(?<data>[^<]+)</script>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);
    private static readonly Regex PlaylistNameRegex = new(
        "\"name\":\"(?<name>(?:\\\\.|[^\"\\\\])+)\".{0,1200}?\"uri\":\"spotify:playlist:[^\"]+\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline,
        RegexTimeout);
    private static readonly Regex ArtistUriRegex = new(
        "spotify:artist:(?<id>[A-Za-z0-9]{22})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);
    private static readonly SpotifyGenreSignal[] ArtistPageGenreSignals =
    {
        new("bongo flava", "bongo flava", EastAfricaRegion, 4),
        new("singeli", "singeli", EastAfricaRegion, 4),
        new("gengetone", "gengetone", EastAfricaRegion, 4),
        new("arbantone", "arbantone", EastAfricaRegion, 4),
        new("genge", "genge", EastAfricaRegion, 4),
        new("kapuka", "kapuka", EastAfricaRegion, 4),
        new("taarab", "taarab", EastAfricaRegion, 4),
        new("naija pop", "naija pop", WestAfricaRegion, 4),
        new("afro fusion", "afrofusion", WestAfricaRegion, 4),
        new("afrofusion", "afrofusion", WestAfricaRegion, 4),
        new("afro pop", "afropop", WestAfricaRegion, 4),
        new("afropop", "afropop", WestAfricaRegion, 4),
        new("afroswing", "afroswing", WestAfricaRegion, 4),
        new(AfrobeatsGenre, AfrobeatsGenre, WestAfricaRegion, 4),
        new("afrobeat", AfrobeatsGenre, WestAfricaRegion, 4),
        new("highlife", "highlife", WestAfricaRegion, 4),
        new("fuji", "fuji", WestAfricaRegion, 4),
        new("juju", "juju", WestAfricaRegion, 4),
        new("apala", "apala", WestAfricaRegion, 4),
        new("alté", "alté", WestAfricaRegion, 4),
        new("alte", "alté", WestAfricaRegion, 4),
        new("amapiano", "amapiano", SouthernAfricaRegion, 4),
        new("gqom", "gqom", SouthernAfricaRegion, 4),
        new("kwaito", "kwaito", SouthernAfricaRegion, 4),
        new("mbaqanga", "mbaqanga", SouthernAfricaRegion, 4),
        new("maskandi", "maskandi", SouthernAfricaRegion, 4),
        new("kuduro", "kuduro", CentralFrancophoneAfricaRegion, 4),
        new("kizomba", "kizomba", CentralFrancophoneAfricaRegion, 4),
        new("mbalax", "mbalax", CentralFrancophoneAfricaRegion, 4),
        new("zouglou", "zouglou", CentralFrancophoneAfricaRegion, 4),
        new("coupé décalé", "coupé-décalé", CentralFrancophoneAfricaRegion, 4),
        new("coupe decale", "coupé-décalé", CentralFrancophoneAfricaRegion, 4),
        new("bikutsi", "bikutsi", CentralFrancophoneAfricaRegion, 4),
        new("makossa", "makossa", CentralFrancophoneAfricaRegion, 4),
        new("afro house", "afro house", PanAfricanRegion, 3),
        new("afro soul", "afro soul", PanAfricanRegion, 3),
        new("afro rap", "afro rap", PanAfricanRegion, 3),
        new("afro gospel", "afro gospel", PanAfricanRegion, 3),
        new("afro", AfrobeatsGenre, PanAfricanRegion, 1),
        new("african", AfrobeatsGenre, PanAfricanRegion, 1),
        new("dancehall", "dancehall", GlobalRegion, 1),
        new("reggae", "reggae", GlobalRegion, 1),
        new("hip hop", "hip hop", GlobalRegion, 1),
        new("r&b", "r&b", GlobalRegion, 1),
        new("rnb", "r&b", GlobalRegion, 1),
        new("soul", "soul", GlobalRegion, 1),
        new("house", "house", GlobalRegion, 1),
        new("drill", "drill", GlobalRegion, 1),
        new("pop", "pop", GlobalRegion, 1)
    };
    private readonly PlatformAuthService _platformAuthService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SpotifyBlobService _blobService;
    private readonly SpotifyUserAuthStore _userAuthStore;
    private readonly ISpotifyUserContextAccessor _userContext;
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly ILogger<SpotifyMetadataService> _logger;
    private static readonly TimeSpan PlaylistCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Stamp, SpotifyUrlMetadata Data)> PlaylistCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Stamp, SpotifyUrlMetadata Data)> PlaylistMetadataCache = new();
    private static readonly TimeSpan PlaylistTrackCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PlaylistTrackCache> PlaylistTrackCache = new();
    private static readonly TimeSpan LibrespotTrackCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Stamp, SpotifyTrackSummary Track)> LibrespotTrackCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SpotifyAudioFeatures> AudioFeatureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _userAgentLock = new();
    private readonly Random _userAgentRandom = new();
    private readonly string _userAgent;

    public SpotifyMetadataService(
        PlatformAuthService platformAuthService,
        IHttpClientFactory httpClientFactory,
        SpotifyBlobService blobService,
        SpotifyUserAuthStore userAuthStore,
        ISpotifyUserContextAccessor userContext,
        SpotifyPathfinderMetadataClient pathfinderMetadataClient,
        ILogger<SpotifyMetadataService> logger)
    {
        _platformAuthService = platformAuthService;
        _httpClientFactory = httpClientFactory;
        _blobService = blobService;
        _userAuthStore = userAuthStore;
        _userContext = userContext;
        _pathfinderMetadataClient = pathfinderMetadataClient;
        _logger = logger;
        _userAgent = SpotifyUserAgentGenerator.BuildRandom(_userAgentRandom, _userAgentLock);
    }

    public async Task<SpotifyUrlMetadata?> FetchByUrlAsync(string url, CancellationToken cancellationToken)
    {
        var parsed = ParseSpotifyUrl(url);
        if (parsed == null)
        {
            return null;
        }

        if (parsed.Type == PlaylistType && TryGetPlaylistFromCache(parsed.Id, out var cached))
        {
            return cached;
        }

        if (parsed.Type == PlaylistType)
        {
            return await FetchPlaylistUrlMetadataAsync(parsed.Id, cancellationToken);
        }

        var pathfinderMetadata = await _pathfinderMetadataClient.FetchByUrlAsync(url, cancellationToken);
        if (pathfinderMetadata is null)
        {
            return null;
        }

        if (parsed.Type == TrackType && pathfinderMetadata.TrackList.Count > 0)
        {
            pathfinderMetadata = await HydrateTrackMetadataAsync(pathfinderMetadata, cancellationToken);
        }
        else if (parsed.Type == AlbumType && pathfinderMetadata.AlbumList.Count > 0)
        {
            pathfinderMetadata = await MergeAlbumFallbackAsync(parsed.Id, pathfinderMetadata, cancellationToken);
        }

        return pathfinderMetadata;
    }

    private async Task<SpotifyUrlMetadata?> FetchPlaylistUrlMetadataAsync(string playlistId, CancellationToken cancellationToken)
    {
        var spotiFlacPayload = await _pathfinderMetadataClient.FetchSpotiFlacPlaylistAsync(playlistId, cancellationToken);
        if (spotiFlacPayload == null)
        {
            return null;
        }

        var playlistMetadata = MapSpotiFlacPlaylistMetadata(playlistId, spotiFlacPayload, includeTracks: true);
        CachePlaylist(playlistId, playlistMetadata);
        return playlistMetadata;
    }

    private async Task<SpotifyUrlMetadata> HydrateTrackMetadataAsync(SpotifyUrlMetadata metadata, CancellationToken cancellationToken)
    {
        var trackList = metadata.TrackList;
        var detailHydratedTracks = await HydrateTrackDetailsWithBlobAsync(trackList, cancellationToken);
        if (detailHydratedTracks.Count > 0)
        {
            trackList = detailHydratedTracks;
        }

        var audioHydratedTracks = await HydrateTrackAudioFeaturesAsync(trackList, cancellationToken);
        if (audioHydratedTracks.Count > 0)
        {
            trackList = audioHydratedTracks;
        }

        return metadata with { TrackList = trackList };
    }

    private async Task<SpotifyUrlMetadata> MergeAlbumFallbackAsync(string albumId, SpotifyUrlMetadata metadata, CancellationToken cancellationToken)
    {
        var fallbackAlbum = await FetchAlbumFallbackWithLibrespotAsync(albumId, cancellationToken);
        if (fallbackAlbum is null)
        {
            return metadata;
        }

        return metadata with
        {
            AlbumList = metadata.AlbumList
                .Select(album => string.Equals(album.Id, albumId, StringComparison.OrdinalIgnoreCase)
                    ? MergeAlbumFallback(album, fallbackAlbum)
                    : album)
                .ToList(),
            TrackList = metadata.TrackList
                .Select(track => MergeTrackWithAlbumFallback(track, fallbackAlbum))
                .ToList()
        };
    }

    private static SpotifyAlbumSummary MergeAlbumFallback(SpotifyAlbumSummary album, SpotifyAlbumSummary fallbackAlbum)
    {
        return album with
        {
            Genres = PreferNonEmptyList(album.Genres, fallbackAlbum.Genres),
            Label = PreferNonEmptyString(album.Label, fallbackAlbum.Label),
            Popularity = album.Popularity ?? fallbackAlbum.Popularity,
            ReleaseDatePrecision = PreferNonEmptyString(album.ReleaseDatePrecision, fallbackAlbum.ReleaseDatePrecision),
            AvailableMarkets = PreferNonEmptyList(album.AvailableMarkets, fallbackAlbum.AvailableMarkets),
            Copyrights = PreferNonEmptyList(album.Copyrights, fallbackAlbum.Copyrights),
            CopyrightText = PreferNonEmptyString(album.CopyrightText, fallbackAlbum.CopyrightText),
            Review = PreferNonEmptyString(album.Review, fallbackAlbum.Review),
            RelatedAlbumIds = PreferNonEmptyList(album.RelatedAlbumIds, fallbackAlbum.RelatedAlbumIds),
            OriginalTitle = PreferNonEmptyString(album.OriginalTitle, fallbackAlbum.OriginalTitle),
            VersionTitle = PreferNonEmptyString(album.VersionTitle, fallbackAlbum.VersionTitle),
            SalePeriods = PreferNonEmptyList(album.SalePeriods, fallbackAlbum.SalePeriods),
            Availability = PreferNonEmptyList(album.Availability, fallbackAlbum.Availability)
        };
    }

    private static SpotifyTrackSummary MergeTrackWithAlbumFallback(SpotifyTrackSummary track, SpotifyAlbumSummary fallbackAlbum)
    {
        return track with
        {
            Label = PreferNonEmptyString(track.Label, fallbackAlbum.Label),
            Genres = PreferNonEmptyList(track.Genres, fallbackAlbum.Genres),
            ReleaseDatePrecision = PreferNonEmptyString(track.ReleaseDatePrecision, fallbackAlbum.ReleaseDatePrecision),
            AvailableMarkets = PreferNonEmptyList(track.AvailableMarkets, fallbackAlbum.AvailableMarkets),
            Copyrights = PreferNonEmptyList(track.Copyrights, fallbackAlbum.Copyrights),
            CopyrightText = PreferNonEmptyString(track.CopyrightText, fallbackAlbum.CopyrightText)
        };
    }

    public async Task<List<string>> FetchArtistGenresFromSpotifyAsync(string artistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return new List<string>();
        }

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await AddArtistPageGenreSignalsAsync(artistId, 3, isPrimary: true, scores, visited, cancellationToken);

        return scores
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key)
            .ToList();
    }

    private async Task AddArtistPageGenreSignalsAsync(
        string artistId,
        int relatedBudget,
        bool isPrimary,
        Dictionary<string, int> scores,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(artistId))
        {
            return;
        }

        var snapshot = await FetchArtistPageSnapshotAsync(artistId, cancellationToken);
        if (snapshot is null)
        {
            return;
        }

        var weight = isPrimary ? 3 : 1;
        foreach (var genre in ExtractGenresFromPlaylistTitles(snapshot.PlaylistTitles))
        {
            scores[genre] = scores.TryGetValue(genre, out var current)
                ? current + weight
                : weight;
        }

        if (relatedBudget <= 0)
        {
            return;
        }

        foreach (var relatedArtistId in snapshot.RelatedArtistIds.Take(relatedBudget))
        {
            await AddArtistPageGenreSignalsAsync(
                relatedArtistId,
                relatedBudget: 0,
                isPrimary: false,
                scores,
                visited,
                cancellationToken);
        }
    }

    private async Task<SpotifyArtistPageSnapshot?> FetchArtistPageSnapshotAsync(string artistId, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://open.spotify.com/artist/{artistId}");
            request.Headers.UserAgent.ParseAdd(_userAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseArtistPageSnapshot(html, artistId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify artist page fallback fetch failed for {ArtistId}.", artistId);
            return null;
        }
    }

    private static SpotifyArtistPageSnapshot? ParseArtistPageSnapshot(string html, string artistId)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(artistId))
        {
            return null;
        }

        var encoded = ExtractInitialStateBlob(html);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        try
        {
            var decoded = DecodeInitialStateBlob(encoded);
            return TryBuildSnapshotFromStructuredState(decoded, artistId)
                ?? BuildSnapshotFromDecodedText(decoded, artistId);
        }
        catch
        {
            return BuildSnapshotFromDecodedText(DecodeInitialStateBlob(encoded), artistId);
        }
    }

    private static string DecodeInitialStateBlob(string encoded) =>
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

    private static SpotifyArtistPageSnapshot? TryBuildSnapshotFromStructuredState(string decoded, string artistId)
    {
        using var doc = JsonDocument.Parse(decoded);
        if (!TryResolveArtistNode(doc.RootElement, artistId, out var artistNode))
        {
            return null;
        }

        var playlistTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSpotifyPlaylistTitles(artistNode, playlistTitles);
        var relatedArtistIds = ExtractRelatedArtistIds(artistNode);
        return new SpotifyArtistPageSnapshot(playlistTitles.ToList(), relatedArtistIds);
    }

    private static bool TryResolveArtistNode(JsonElement root, string artistId, out JsonElement artistNode)
    {
        artistNode = default;
        if (!TryGetNested(root, out var items, "entities", "items")
            || items.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var artistUri = $"spotify:artist:{artistId}";
        return items.TryGetProperty(artistUri, out artistNode)
            && artistNode.ValueKind == JsonValueKind.Object;
    }

    private static List<string> ExtractRelatedArtistIds(JsonElement artistNode)
    {
        if (!TryGetNested(artistNode, out var relatedItems, "relatedContent", "relatedArtists", "items")
            || relatedItems.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return relatedItems
            .EnumerateArray()
            .Select(ResolveRelatedArtistItem)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private static string? ResolveRelatedArtistItem(JsonElement item)
    {
        var candidate = TryGetNested(item, out var data, "data") ? data : item;
        return ExtractSpotifyId(TryGetString(candidate, "uri"), ArtistType);
    }

    private static string? ExtractInitialStateBlob(string html)
    {
        var start = html.IndexOf(InitialStateStartMarker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start += InitialStateStartMarker.Length;
            var end = html.IndexOf(ScriptEndMarker, start, StringComparison.OrdinalIgnoreCase);
            if (end > start)
            {
                return html[start..end].Trim();
            }
        }

        var match = InitialStateRegex.Match(html);
        return match.Success ? match.Groups["data"].Value.Trim() : null;
    }

    private static SpotifyArtistPageSnapshot? BuildSnapshotFromDecodedText(string decoded, string artistId)
    {
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return null;
        }

        var playlistTitles = PlaylistNameRegex.Matches(decoded)
            .Select(match => Regex.Unescape(match.Groups["name"].Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var relatedArtistIds = ArtistUriRegex.Matches(decoded)
            .Select(match => match.Groups["id"].Value)
            .Where(value => !string.Equals(value, artistId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return playlistTitles.Count == 0 && relatedArtistIds.Count == 0
            ? null
            : new SpotifyArtistPageSnapshot(playlistTitles, relatedArtistIds);
    }

    private static void CollectSpotifyPlaylistTitles(JsonElement node, HashSet<string> titles)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var uri = TryGetString(node, "uri");
                    var name = TryGetString(node, "name");
                    if (!string.IsNullOrWhiteSpace(name) &&
                        uri?.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        titles.Add(name.Trim());
                    }

                    foreach (var property in node.EnumerateObject())
                    {
                        CollectSpotifyPlaylistTitles(property.Value, titles);
                    }

                    break;
                }
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    CollectSpotifyPlaylistTitles(item, titles);
                }
                break;
        }
    }

    private static List<string> ExtractGenresFromPlaylistTitles(IEnumerable<string> titles)
    {
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var matchedSignals = CollectGenreSignals(titles, scores);
        ApplyAfricanGenrePenalties(scores, matchedSignals);
        return OrderScoredGenres(scores);
    }

    private static List<SpotifyGenreSignal> CollectGenreSignals(
        IEnumerable<string> titles,
        Dictionary<string, int> scores)
    {
        var matchedSignals = new List<SpotifyGenreSignal>();
        foreach (var normalized in titles.Select(NormalizeGenreSignalText).Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (var signal in ArtistPageGenreSignals.Where(signal => ContainsWholeWordPhrase(normalized, signal.Token)))
            {
                matchedSignals.Add(signal);
                scores[signal.Canonical] = scores.TryGetValue(signal.Canonical, out var current)
                    ? current + signal.Weight
                    : signal.Weight;
            }
        }

        return matchedSignals;
    }

    private static void ApplyAfricanGenrePenalties(
        Dictionary<string, int> scores,
        IReadOnlyCollection<SpotifyGenreSignal> matchedSignals)
    {
        var matchedRegions = matchedSignals
            .Select(signal => signal.Region)
            .Where(IsSpecificAfricanRegion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (matchedSignals.Any(signal => signal.Weight >= 3))
        {
            ReduceGenreScores(scores, matchedSignals.Where(signal => signal.Weight < 3));
        }

        if (matchedRegions.Count > 0)
        {
            ReduceGenreScores(
                scores,
                matchedSignals.Where(signal => string.Equals(signal.Region, PanAfricanRegion, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static bool IsSpecificAfricanRegion(string region) =>
        !string.Equals(region, GlobalRegion, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(region, PanAfricanRegion, StringComparison.OrdinalIgnoreCase);

    private static void ReduceGenreScores(
        Dictionary<string, int> scores,
        IEnumerable<SpotifyGenreSignal> signals)
    {
        foreach (var canonical in signals.Select(static signal => signal.Canonical))
        {
            if (scores.TryGetValue(canonical, out var current))
            {
                scores[canonical] = Math.Max(0, current - 1);
            }
        }
    }

    private static List<string> OrderScoredGenres(Dictionary<string, int> scores) =>
        scores
            .Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key)
            .ToList();

    private static string NormalizeGenreSignalText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace('é', 'e')
            .Replace('&', ' ')
            .Replace('/', ' ')
            .ToLowerInvariant();

        normalized = Regex.Replace(normalized, "[^a-z0-9 ]+", " ", RegexOptions.None, RegexTimeout);
        normalized = Regex.Replace(normalized, "\\s+", " ", RegexOptions.None, RegexTimeout).Trim();
        return normalized;
    }

    private static bool ContainsWholeWordPhrase(string haystack, string needle)
    {
        var normalizedNeedle = NormalizeGenreSignalText(needle);
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(normalizedNeedle))
        {
            return false;
        }

        return haystack.Equals(normalizedNeedle, StringComparison.Ordinal)
            || haystack.StartsWith(normalizedNeedle + " ", StringComparison.Ordinal)
            || haystack.EndsWith(" " + normalizedNeedle, StringComparison.Ordinal)
            || haystack.Contains(" " + normalizedNeedle + " ", StringComparison.Ordinal);
    }

    private static string? ExtractSpotifyId(string? uri, string type)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        var prefix = $"spotify:{type}:";
        return uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? uri[prefix.Length..]
            : null;
    }

    private static bool TryGetNested(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        if (!TryGetNested(element, out var value, path))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private sealed record SpotifyArtistPageSnapshot(
        List<string> PlaylistTitles,
        List<string> RelatedArtistIds);

    private sealed record SpotifyGenreSignal(
        string Token,
        string Canonical,
        string Region,
        int Weight);


    private static SpotifyUrlMetadata MapSpotiFlacPlaylistMetadata(
        string playlistId,
        SpotiFlacPlaylistPayload payload,
        bool includeTracks)
    {
        var info = payload.PlaylistInfo;
        var playlistName = info.Owner.Name ?? string.Empty;
        var ownerName = info.Owner.DisplayName;
        var imageUrl = info.Cover ?? string.Empty;

        var tracks = new List<SpotifyTrackSummary>();
        if (includeTracks)
        {
            foreach (var track in payload.TrackList)
            {
                var trackId = NormalizeSpotifyTrackId(track.SpotifyId, track.ExternalUrl);
                if (trackId == null)
                {
                    continue;
                }

                var artistIds = track.ArtistsData?
                    .Select(artist => artist.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList();
                if ((artistIds == null || artistIds.Count == 0) && !string.IsNullOrWhiteSpace(track.ArtistId))
                {
                    artistIds = new List<string> { track.ArtistId };
                }

                tracks.Add(new SpotifyTrackSummary(
                    trackId,
                    track.Name ?? string.Empty,
                    track.Artists,
                    track.AlbumName,
                    track.DurationMs > 0 ? track.DurationMs : null,
                    BuildSpotifyTrackUrl(trackId, track.ExternalUrl),
                    track.Images,
                    track.Isrc)
                {
                    AlbumArtist = track.AlbumArtist,
                    AlbumId = track.AlbumId,
                    ArtistIds = artistIds
                });
            }
        }

        return new SpotifyUrlMetadata(
            PlaylistType,
            playlistId,
            playlistName,
            $"https://open.spotify.com/playlist/{playlistId}",
            imageUrl,
            info.Description,
            info.Tracks.Total,
            null,
            tracks,
            new List<SpotifyAlbumSummary>(),
            ownerName,
            info.Followers.Total,
            null)
        {
            OwnerImageUrl = info.Owner.Images
        };
    }

    private static string? NormalizeSpotifyTrackId(string? spotifyId, string? sourceUrl)
    {
        if (LooksLikeSpotifyTrackId(spotifyId))
        {
            return spotifyId!.Trim();
        }

        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var match = SpotifyTrackUrlRegex.Match(sourceUrl);
        return match.Success && match.Groups.Count > 1
            ? match.Groups[1].Value
            : null;
    }

    private static string BuildSpotifyTrackUrl(string trackId, string? sourceUrl)
    {
        return !string.IsNullOrWhiteSpace(sourceUrl) &&
               SpotifyTrackUrlRegex.IsMatch(sourceUrl)
            ? sourceUrl
            : $"https://open.spotify.com/track/{trackId}";
    }

    public async Task<SpotifyUrlMetadata?> FetchPlaylistMetadataAsync(string playlistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        if (TryGetPlaylistMetadataFromCache(playlistId, out var cached))
        {
            return cached with { TrackList = new List<SpotifyTrackSummary>() };
        }

        var spotiFlacPayload = await _pathfinderMetadataClient.FetchSpotiFlacPlaylistMetadataAsync(
            playlistId,
            cancellationToken);
        if (spotiFlacPayload != null)
        {
            var metadata = MapSpotiFlacPlaylistMetadata(playlistId, spotiFlacPayload, includeTracks: false);
            CachePlaylistMetadata(playlistId, metadata);
            return metadata;
        }

        var fallbackUrl = $"https://open.spotify.com/playlist/{playlistId}";
        var fallback = await FetchByUrlAsync(fallbackUrl, cancellationToken);
        return fallback is null
            ? null
            : fallback with { TrackList = new List<SpotifyTrackSummary>() };
    }

    public async Task<SpotifyPlaylistPage?> FetchPlaylistTrackPageAsync(
        string playlistId,
        int offset,
        int limit,
        string trackSource,
        bool hydrate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        var metadata = await FetchPlaylistMetadataAsync(playlistId, cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        var normalizedSource = NormalizeTrackSource(trackSource);
        var boundedOffset = Math.Max(0, offset);
        var boundedLimit = Math.Clamp(limit, 1, 100);

        if (string.Equals(normalizedSource, LibrespotSource, StringComparison.OrdinalIgnoreCase))
        {
            return await BuildLibrespotPlaylistPageAsync(
                metadata,
                playlistId,
                boundedOffset,
                boundedLimit,
                hydrate,
                cancellationToken);
        }

        var tracks = await GetPathfinderPlaylistTracksAsync(playlistId, cancellationToken);
        var totalTracks = metadata.TotalTracks ?? tracks.Count;
        var pageTracks = tracks
            .Skip(boundedOffset)
            .Take(boundedLimit)
            .ToList();
        var hasMoreTracks = boundedOffset + pageTracks.Count < totalTracks;

        return new SpotifyPlaylistPage(
            metadata.SnapshotId,
            totalTracks,
            metadata.Name,
            metadata.Subtitle,
            metadata.ImageUrl,
            pageTracks,
            hasMoreTracks);
    }

    private async Task<SpotifyPlaylistPage> BuildLibrespotPlaylistPageAsync(
        SpotifyUrlMetadata metadata,
        string playlistId,
        int boundedOffset,
        int boundedLimit,
        bool hydrate,
        CancellationToken cancellationToken)
    {
        var trackIds = await GetLibrespotPlaylistTrackIdsAsync(playlistId, cancellationToken);
        if (trackIds.Count == 0)
        {
            return await BuildFallbackLibrespotPlaylistPageAsync(metadata, playlistId, boundedOffset, boundedLimit, cancellationToken);
        }

        var pageIds = trackIds.Skip(boundedOffset).Take(boundedLimit).ToList();
        var hydrated = await BuildLibrespotPageTracksAsync(playlistId, pageIds, cancellationToken);
        if (hydrate)
        {
            hydrated = await HydratePlaylistPageTracksAsync(hydrated, cancellationToken);
        }

        var hasMore = boundedOffset + hydrated.Count < trackIds.Count;
        return new SpotifyPlaylistPage(
            metadata.SnapshotId,
            metadata.TotalTracks ?? trackIds.Count,
            metadata.Name,
            metadata.Subtitle,
            metadata.ImageUrl,
            hydrated,
            hasMore);
    }

    private async Task<SpotifyPlaylistPage> BuildFallbackLibrespotPlaylistPageAsync(
        SpotifyUrlMetadata metadata,
        string playlistId,
        int boundedOffset,
        int boundedLimit,
        CancellationToken cancellationToken)
    {
        var fallbackTracks = await _pathfinderMetadataClient.FetchPlaylistTracksWithBlobAuthAsync(playlistId, cancellationToken);
        if (fallbackTracks is not { Count: > 0 })
        {
            return BuildPlaylistPage(metadata, metadata.TotalTracks, new List<SpotifyTrackSummary>(), false);
        }

        var fallbackPageTracks = fallbackTracks
            .Skip(boundedOffset)
            .Take(boundedLimit)
            .ToList();
        return BuildPlaylistPage(
            metadata,
            metadata.TotalTracks ?? fallbackTracks.Count,
            fallbackPageTracks,
            boundedOffset + fallbackPageTracks.Count < fallbackTracks.Count);
    }

    private async Task<List<SpotifyTrackSummary>> BuildLibrespotPageTracksAsync(
        string playlistId,
        List<string> pageIds,
        CancellationToken cancellationToken)
    {
        var pathfinderTracks = await _pathfinderMetadataClient.FetchPlaylistTracksWithBlobAuthAsync(playlistId, cancellationToken);
        if (pathfinderTracks is not { Count: > 0 })
        {
            pathfinderTracks = await GetPathfinderPlaylistTracksAsync(playlistId, cancellationToken);
        }

        if (pathfinderTracks is not { Count: > 0 })
        {
            return pageIds.Select(CreatePlaceholderTrack).ToList();
        }

        var byId = pathfinderTracks.ToDictionary(track => track.Id, StringComparer.OrdinalIgnoreCase);
        return pageIds
            .Select(id => byId.TryGetValue(id, out var track) ? track : CreatePlaceholderTrack(id))
            .ToList();
    }

    private async Task<List<SpotifyTrackSummary>> HydratePlaylistPageTracksAsync(
        List<SpotifyTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!tracks.Any(IsPlaceholderTrackSummary))
            {
                CacheHydratedTracks(tracks);
                return tracks;
            }

            var hydrated = await HydrateTrackDetailsWithBlobAsync(tracks, cancellationToken);
            var stillMissingIds = hydrated
                .Where(IsPlaceholderTrackSummary)
                .Select(track => track.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (stillMissingIds.Count > 0)
            {
                hydrated = await ApplyFallbackTrackHydrationAsync(hydrated, stillMissingIds, cancellationToken);
            }

            CacheHydratedTracks(hydrated);
            return hydrated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Librespot playlist page hydration failed.");
            return tracks;
        }
    }

    private async Task<List<SpotifyTrackSummary>> ApplyFallbackTrackHydrationAsync(
        List<SpotifyTrackSummary> hydrated,
        List<string> stillMissingIds,
        CancellationToken cancellationToken)
    {
        var fallbackTracks = await _pathfinderMetadataClient.FetchTrackSummariesByIdsAsync(
            stillMissingIds,
            cancellationToken,
            maxConcurrency: 8);
        if (fallbackTracks.Count == 0)
        {
            return hydrated;
        }

        var fallbackById = fallbackTracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Id) && HasCoreTrackMetadata(track))
            .ToDictionary(track => track.Id, StringComparer.OrdinalIgnoreCase);
        if (fallbackById.Count == 0)
        {
            return hydrated;
        }

        return hydrated
            .Select(track => fallbackById.TryGetValue(track.Id, out var fallbackTrack) ? fallbackTrack : track)
            .ToList();
    }

    private static SpotifyPlaylistPage BuildPlaylistPage(
        SpotifyUrlMetadata metadata,
        int? totalTracks,
        List<SpotifyTrackSummary> tracks,
        bool hasMore)
    {
        return new SpotifyPlaylistPage(
            metadata.SnapshotId,
            totalTracks,
            metadata.Name,
            metadata.Subtitle,
            metadata.ImageUrl,
            tracks,
            hasMore);
    }

    private static SpotifyTrackSummary CreatePlaceholderTrack(string id)
    {
        return new SpotifyTrackSummary(
            id,
            string.Empty,
            null,
            null,
            null,
            $"https://open.spotify.com/track/{id}",
            null,
            null);
    }

    private static void CacheHydratedTracks(IEnumerable<SpotifyTrackSummary> tracks)
    {
        foreach (var track in tracks.Where(HasCoreTrackMetadata))
        {
            CacheLibrespotTrack(track);
        }
    }


    public async Task<List<SpotifyTrackSummary>> FetchPlaylistTracksForSourceAsync(
        string playlistId,
        string trackSource,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizeTrackSource(trackSource);
        if (string.Equals(normalizedSource, LibrespotSource, StringComparison.OrdinalIgnoreCase))
        {
            var pathfinderTracks = await _pathfinderMetadataClient.FetchPlaylistTracksWithBlobAuthAsync(
                playlistId,
                cancellationToken);
            if (pathfinderTracks is { Count: > 0 })
            {
                return pathfinderTracks;
            }

            return await GetPathfinderPlaylistTracksAsync(playlistId, cancellationToken);
        }

        return await GetPathfinderPlaylistTracksAsync(playlistId, cancellationToken);
    }

    public async Task<List<SpotifyTrackSummary>> FetchLibrespotTracksAsync(
        IReadOnlyList<string> trackIds,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeTrackIds(trackIds);
        if (ids.Count == 0)
        {
            return new List<SpotifyTrackSummary>();
        }

        var (cachedTracks, missingIds) = PartitionCachedLibrespotTracks(ids);

        if (missingIds.Count > 0)
        {
            await HydrateMissingLibrespotTracksAsync(cachedTracks, missingIds, cancellationToken);
            await HydrateFallbackLibrespotTracksAsync(cachedTracks, missingIds, cancellationToken);
        }

        return OrderLibrespotTracks(ids, cachedTracks);
    }

    private static List<string> NormalizeTrackIds(IReadOnlyList<string> trackIds) =>
        trackIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static (Dictionary<string, SpotifyTrackSummary> CachedTracks, List<string> MissingIds) PartitionCachedLibrespotTracks(
        IReadOnlyList<string> ids)
    {
        var cachedTracks = new Dictionary<string, SpotifyTrackSummary>(StringComparer.OrdinalIgnoreCase);
        var missingIds = new List<string>();
        foreach (var id in ids)
        {
            if (TryGetLibrespotTrackCache(id, out var cached))
            {
                cachedTracks[id] = cached;
            }
            else
            {
                missingIds.Add(id);
            }
        }

        return (cachedTracks, missingIds);
    }

    private async Task HydrateMissingLibrespotTracksAsync(
        Dictionary<string, SpotifyTrackSummary> cachedTracks,
        IReadOnlyList<string> missingIds,
        CancellationToken cancellationToken)
    {
        var blobPath = await TryResolveActiveLibrespotBlobPathAsync();
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return;
        }

        var hydrated = await HydrateTrackDetailsWithLibrespotAsync(
            blobPath,
            missingIds.Select(CreatePlaceholderTrackSummary).ToList(),
            cancellationToken);
        CacheResolvedTracks(cachedTracks, hydrated);
    }

    private async Task HydrateFallbackLibrespotTracksAsync(
        Dictionary<string, SpotifyTrackSummary> cachedTracks,
        IReadOnlyList<string> missingIds,
        CancellationToken cancellationToken)
    {
        var unresolvedIds = missingIds
            .Where(id => !cachedTracks.TryGetValue(id, out var hydratedTrack) || IsPlaceholderTrackSummary(hydratedTrack))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unresolvedIds.Count == 0)
        {
            return;
        }

        var fallbackTracks = await _pathfinderMetadataClient.FetchTrackSummariesByIdsAsync(
            unresolvedIds,
            cancellationToken,
            maxConcurrency: 8);
        CacheResolvedTracks(cachedTracks, fallbackTracks.Where(HasCoreTrackMetadata));
    }

    private static void CacheResolvedTracks(
        IDictionary<string, SpotifyTrackSummary> cache,
        IEnumerable<SpotifyTrackSummary> tracks)
    {
        foreach (var track in tracks)
        {
            cache[track.Id] = track;
            CacheLibrespotTrack(track);
        }
    }

    private static List<SpotifyTrackSummary> OrderLibrespotTracks(
        IReadOnlyList<string> ids,
        Dictionary<string, SpotifyTrackSummary> cachedTracks) =>
        ids.Select(id => cachedTracks.TryGetValue(id, out var track) ? track : CreatePlaceholderTrackSummary(id)).ToList();

    private static SpotifyTrackSummary CreatePlaceholderTrackSummary(string id) =>
        new(
            id,
            string.Empty,
            null,
            null,
            null,
            $"https://open.spotify.com/track/{id}",
            null,
            null);

    private static string NormalizeTrackSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "pathfinder";
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized == "spotiflac" ? "pathfinder" : normalized;
    }

    private async Task<List<SpotifyTrackSummary>> GetPathfinderPlaylistTracksAsync(
        string playlistId,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"pathfinder:{playlistId}";
        if (TryGetTrackCache(cacheKey, out var cachedTracks))
        {
            return cachedTracks;
        }

        var spotiFlacPayload = await _pathfinderMetadataClient.FetchSpotiFlacPlaylistAsync(playlistId, cancellationToken);
        var tracks = spotiFlacPayload != null
            ? MapSpotiFlacPlaylistMetadata(playlistId, spotiFlacPayload, includeTracks: true).TrackList
            : await _pathfinderMetadataClient.FetchPlaylistTracksAsync(playlistId, cancellationToken)
                ?? new List<SpotifyTrackSummary>();
        CacheTrackList(cacheKey, "pathfinder", tracks);
        return tracks;
    }

    private async Task<List<string>> GetLibrespotPlaylistTrackIdsAsync(
        string playlistId,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"librespot:{playlistId}";
        if (TryGetTrackCache(cacheKey, out var cachedTracks))
        {
            return cachedTracks.Select(track => track.Id).ToList();
        }

        var blobPath = await TryResolveActiveLibrespotBlobPathAsync();
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return new List<string>();
        }

        var librespotResult = await _blobService.GetLibrespotPlaylistAsync(
            blobPath,
            playlistId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(librespotResult.PayloadJson))
        {
            return new List<string>();
        }

        var parsed = ParseLibrespotPlaylistPayload(playlistId, librespotResult.PayloadJson);
        if (parsed is null)
        {
            return new List<string>();
        }

        var stubs = parsed.TrackIds
            .Select(id => new SpotifyTrackSummary(
                id,
                string.Empty,
                null,
                null,
                null,
                $"https://open.spotify.com/track/{id}",
                null,
                null))
            .ToList();
        CacheTrackList(cacheKey, LibrespotSource, stubs);
        return parsed.TrackIds;
    }

    private static bool TryGetTrackCache(string key, out List<SpotifyTrackSummary> tracks)
    {
        tracks = new List<SpotifyTrackSummary>();
        if (!PlaylistTrackCache.TryGetValue(key, out var cached))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - cached.Stamp > PlaylistTrackCacheTtl)
        {
            PlaylistTrackCache.TryRemove(key, out _);
            return false;
        }

        var sanitizedTracks = FilterInvalidSpotifyTracks(cached.Tracks);
        if (sanitizedTracks.Count != cached.Tracks.Count)
        {
            PlaylistTrackCache[key] = cached with { Tracks = sanitizedTracks };
        }

        tracks = sanitizedTracks;
        return true;
    }

    private static void CacheTrackList(string key, string source, List<SpotifyTrackSummary> tracks)
    {
        PlaylistTrackCache[key] = new PlaylistTrackCache(
            DateTimeOffset.UtcNow,
            source,
            FilterInvalidSpotifyTracks(tracks));
    }

    private static List<SpotifyTrackSummary> FilterInvalidSpotifyTracks(List<SpotifyTrackSummary> tracks)
    {
        if (tracks.Count == 0)
        {
            return tracks;
        }

        var filtered = new List<SpotifyTrackSummary>(tracks.Count);
        foreach (var track in tracks)
        {
            if (!LooksLikeSpotifyTrackId(track.Id))
            {
                continue;
            }

            var sourceUrl = BuildSpotifyTrackUrl(track.Id, track.SourceUrl);
            filtered.Add(sourceUrl == track.SourceUrl
                ? track
                : track with { SourceUrl = sourceUrl });
        }

        return filtered;
    }

    private static bool TryGetLibrespotTrackCache(string id, out SpotifyTrackSummary track)
    {
        track = default!;
        if (!LibrespotTrackCache.TryGetValue(id, out var cached))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - cached.Stamp > LibrespotTrackCacheTtl)
        {
            LibrespotTrackCache.TryRemove(id, out _);
            return false;
        }

        track = cached.Track;
        return true;
    }

    private static bool IsPlaceholderTrackSummary(SpotifyTrackSummary track)
    {
        return !HasCoreTrackMetadata(track);
    }

    private static bool HasCoreTrackMetadata(SpotifyTrackSummary track)
    {
        if (track == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(track.Name))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(track.Artists)
            || !string.IsNullOrWhiteSpace(track.Album)
            || track.DurationMs is > 0;
    }

    private static void CacheLibrespotTrack(SpotifyTrackSummary track)
    {
        if (string.IsNullOrWhiteSpace(track.Id))
        {
            return;
        }

        LibrespotTrackCache[track.Id] = (DateTimeOffset.UtcNow, track);
    }

    private static bool TryGetPlaylistMetadataFromCache(string playlistId, out SpotifyUrlMetadata metadata)
    {
        metadata = default!;
        if (!PlaylistMetadataCache.TryGetValue(playlistId, out var cached))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - cached.Stamp > PlaylistCacheTtl)
        {
            PlaylistMetadataCache.TryRemove(playlistId, out _);
            return false;
        }

        metadata = cached.Data;
        return true;
    }

    private static void CachePlaylistMetadata(string playlistId, SpotifyUrlMetadata metadata)
    {
        PlaylistMetadataCache[playlistId] = (DateTimeOffset.UtcNow, metadata);
    }

    public async Task<SpotifyTrackArtwork?> FetchTrackArtworkAsync(string trackId, CancellationToken cancellationToken, string? requestedAlbumTitle = null)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        var url = $"https://open.spotify.com/track/{trackId}";
        var metadata = await _pathfinderMetadataClient.FetchByUrlAsync(url, cancellationToken);
        if (metadata == null || metadata.TrackList.Count == 0)
        {
            return null;
        }

        var track = metadata.TrackList[0];
        if (DeezSpoTag.Services.Download.Utils.ArtworkFallbackHelper.ShouldRejectAlbumArtworkCandidate(
                requestedAlbumTitle,
                track.Album))
        {
            _logger.LogDebug(
                "Rejected Spotify artwork for track {TrackId}: resolved album '{ResolvedAlbum}' did not match requested album '{RequestedAlbum}'.",
                trackId,
                track.Album,
                requestedAlbumTitle);
            return null;
        }

        var cover = track.ImageUrl ?? metadata.ImageUrl;
        return new SpotifyTrackArtwork(cover, null);
    }

    public async Task<SpotifyPlaylistPage?> FetchPlaylistPageAsync(string playlistId, int offset, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        var url = $"https://open.spotify.com/playlist/{playlistId}";
        var metadata = await FetchByUrlAsync(url, cancellationToken);
        if (metadata == null)
        {
            return null;
        }

        var boundedOffset = Math.Max(0, offset);
        var boundedLimit = Math.Clamp(limit, 1, 1000);
        var totalTracks = metadata.TotalTracks ?? metadata.TrackList.Count;
        var pageTracks = metadata.TrackList
            .Skip(boundedOffset)
            .Take(boundedLimit)
            .ToList();
        var hasMore = boundedOffset + pageTracks.Count < totalTracks;

        return new SpotifyPlaylistPage(
            metadata.SnapshotId,
            totalTracks,
            metadata.Name,
            metadata.Subtitle,
            metadata.ImageUrl,
            pageTracks,
            hasMore);
    }

    public async Task<SpotifyPlaylistSnapshot?> FetchPlaylistSnapshotAsync(string playlistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        var url = $"https://open.spotify.com/playlist/{playlistId}";
        var metadata = await FetchByUrlAsync(url, cancellationToken);
        if (metadata == null)
        {
            return null;
        }

        var totalTracks = metadata.TotalTracks ?? metadata.TrackList.Count;
        return new SpotifyPlaylistSnapshot(
            metadata.SnapshotId,
            totalTracks,
            metadata.Name,
            metadata.Subtitle,
            metadata.ImageUrl,
            metadata.TrackList);
    }

    public async Task<List<SpotifyTrackSummary>> FetchAlbumTracksAsync(string albumId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return new List<SpotifyTrackSummary>();
        }

        var pathfinderTracks = await _pathfinderMetadataClient.FetchAlbumTracksAsync(albumId, cancellationToken);
        return pathfinderTracks ?? new List<SpotifyTrackSummary>();
    }

    public async Task<List<SpotifyTrackSummary>> HydrateTrackDetailsWithBlobAsync(
        List<SpotifyTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return tracks;
        }

        var librespotBlobPath = await TryResolveActiveLibrespotBlobPathAsync();
        if (!string.IsNullOrWhiteSpace(librespotBlobPath))
        {
            var librespotHydrated = await HydrateTrackDetailsWithLibrespotAsync(
                librespotBlobPath,
                tracks,
                cancellationToken);
            if (librespotHydrated.Any(track =>
                    !string.IsNullOrWhiteSpace(track.Isrc) || !string.IsNullOrWhiteSpace(track.Label)))
            {
                return librespotHydrated;
            }
        }

        var context = await BuildLibrespotContextAsync(cancellationToken);
        if (context is null)
        {
            _logger.LogWarning("Spotify blob auth unavailable: skipping track hydration.");
            return tracks;
        }

        if (!string.IsNullOrWhiteSpace(context.BlobPath))
        {
            var librespotHydrated = await HydrateTrackDetailsWithLibrespotAsync(
                context.BlobPath,
                tracks,
                cancellationToken);
            return librespotHydrated;
        }

        return await HydrateTrackDetailsAsync(tracks, cancellationToken);
    }

    public async Task<List<SpotifyTrackSummary>> HydrateTrackIsrcsWithPathfinderAsync(
        List<SpotifyTrackSummary> tracks,
        CancellationToken cancellationToken,
        int? maxConcurrency = null)
    {
        if (tracks.Count == 0)
        {
            return tracks;
        }

        var missing = tracks
            .Where(track => !IsValidIsrc(track.Isrc))
            .Select(track => track.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
        {
            return tracks;
        }

        var isrcMap = await _pathfinderMetadataClient.FetchTrackIsrcsAsync(
            missing,
            cancellationToken,
            maxConcurrency);
        if (isrcMap.Count == 0)
        {
            return tracks;
        }

        var updated = new List<SpotifyTrackSummary>(tracks.Count);
        foreach (var track in tracks)
        {
            if (!string.IsNullOrWhiteSpace(track.Id)
                && isrcMap.TryGetValue(track.Id, out var isrc)
                && IsValidIsrc(isrc))
            {
                updated.Add(track with { Isrc = isrc });
                continue;
            }

            updated.Add(track);
        }

        return updated;
    }

    public async Task<List<SpotifyTrackSummary>> HydrateTrackAudioFeaturesAsync(
        List<SpotifyTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return tracks;
        }

        var hydration = await LoadAudioFeatureHydrationAsync(tracks, cancellationToken);
        if (hydration.Cached.Count == 0 && (hydration.Fetched == null || hydration.Fetched.Count == 0))
        {
            return tracks;
        }

        return tracks.Select(track => ApplyAvailableAudioFeatures(track, hydration)).ToList();
    }

    private static (Dictionary<string, SpotifyAudioFeatures> Cached, List<string> Missing) PartitionTracksByAudioFeatureState(
        IEnumerable<SpotifyTrackSummary> tracks)
    {
        var cached = new Dictionary<string, SpotifyAudioFeatures>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.Id) || HasAudioFeatures(track))
            {
                continue;
            }

            if (AudioFeatureCache.TryGetValue(track.Id, out var existing))
            {
                cached[track.Id] = existing;
            }
            else
            {
                missing.Add(track.Id);
            }
        }

        return (cached, missing);
    }

    private async Task<ParsedAudioFeatureHydration> LoadAudioFeatureHydrationAsync(
        List<SpotifyTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var (cached, missing) = PartitionTracksByAudioFeatureState(tracks);
        Dictionary<string, SpotifyAudioFeatures>? fetched = null;
        if (missing.Count > 0)
        {
            fetched = await TryFetchAudioFeaturesAsync(missing, cancellationToken);
        }

        return new ParsedAudioFeatureHydration(cached, fetched);
    }

    private async Task<Dictionary<string, SpotifyAudioFeatures>?> TryFetchAudioFeaturesAsync(
        List<string> missing,
        CancellationToken cancellationToken)
    {
        try
        {
            var fetched = await FetchAudioFeaturesByIdsAsync(missing, cancellationToken);
            foreach (var (id, features) in fetched)
            {
                AudioFeatureCache[id] = features;
            }

            return fetched;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify pathfinder audio feature hydration failed.");
            return null;
        }
    }

    private static SpotifyTrackSummary ApplyAvailableAudioFeatures(
        SpotifyTrackSummary track,
        ParsedAudioFeatureHydration hydration)
    {
        if (string.IsNullOrWhiteSpace(track.Id))
        {
            return track;
        }

        if (hydration.Cached.TryGetValue(track.Id, out var cachedFeatures))
        {
            return ApplyAudioFeatures(track, cachedFeatures);
        }

        return hydration.Fetched != null && hydration.Fetched.TryGetValue(track.Id, out var fetchedFeatures)
            ? ApplyAudioFeatures(track, fetchedFeatures)
            : track;
    }

    public async Task<SpotifyAlbumSummary?> FetchAlbumFallbackWithLibrespotAsync(
        string albumId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return null;
        }

        var blobPath = await TryResolveActiveLibrespotBlobPathAsync();
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return null;
        }

        var result = await _blobService.GetLibrespotAlbumAsync(blobPath, albumId, includeTracks: false, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.PayloadJson))
        {
            return null;
        }

        return ParseLibrespotAlbum(albumId, result.PayloadJson);
    }

    public async Task<SpotifyArtistFallbackMetadata?> FetchArtistFallbackWithLibrespotAsync(
        string artistId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return null;
        }

        var blobPath = await TryResolveActiveLibrespotBlobPathAsync();
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return null;
        }

        var result = await _blobService.GetLibrespotArtistAsync(blobPath, artistId, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.PayloadJson))
        {
            return null;
        }

        return ParseLibrespotArtist(artistId, result.PayloadJson);
    }

    private async Task<SearchContext?> BuildLibrespotContextAsync(CancellationToken cancellationToken)
    {
        var blobPath = await TryResolveActiveWebPlayerBlobPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return null;
        }

        // Try web player token first (uses blob cookies directly, no Librespot)
        var webPlayerToken = await _blobService.GetWebPlayerTokenInfoAsync(blobPath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(webPlayerToken?.AccessToken))
        {
            var market = await ResolveMarketAsync();
            _logger.LogInformation(
                "Spotify metadata auth ready: tokenLen={TokenLen} market={Market} source=webplayer",
                webPlayerToken.AccessToken.Length,
                market);
            return new SearchContext(webPlayerToken.AccessToken, market, "webplayer", blobPath);
        }

        // Fallback to Librespot if web player token fails
        var tokenResult = await _blobService.GetWebApiAccessTokenAsync(blobPath, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(tokenResult.AccessToken))
        {
            return null;
        }

        var fallbackMarket = await ResolveMarketAsync();
        _logger.LogInformation(
            "Spotify metadata auth ready: tokenLen={TokenLen} market={Market} source=librespot",
            tokenResult.AccessToken.Length,
            fallbackMarket);
        return new SearchContext(tokenResult.AccessToken, fallbackMarket, LibrespotSource, blobPath);
    }


    private async Task<string> ResolveMarketAsync()
    {
        try
        {
            var resolvedAccounts = await ResolveActiveSpotifyAccountsAsync();
            var market = resolvedAccounts.UserAccount?.Region ?? resolvedAccounts.PlatformAccount?.Region;
            return string.IsNullOrWhiteSpace(market) ? "US" : market;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Spotify market.");
        }

        return "US";
    }

    private async Task<string?> TryResolveActiveLibrespotBlobPathAsync()
    {
        try
        {
            var resolvedAccounts = await ResolveActiveSpotifyAccountsAsync();
            var userBlobPath = resolvedAccounts.UserAccount?.LibrespotBlobPath ?? resolvedAccounts.UserAccount?.BlobPath;
            if (!string.IsNullOrWhiteSpace(userBlobPath) && _blobService.BlobExists(userBlobPath))
            {
                return userBlobPath;
            }

            return NormalizeOptionalPath(resolvedAccounts.PlatformAccount?.BlobPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Spotify librespot blob path.");
            return null;
        }
    }

    private async Task<string?> TryResolveActiveWebPlayerBlobPathAsync(CancellationToken cancellationToken)
    {
        try
        {
            var resolvedAccounts = await ResolveActiveSpotifyAccountsAsync();
            var userBlobPath = resolvedAccounts.UserAccount?.WebPlayerBlobPath ?? resolvedAccounts.UserAccount?.BlobPath;
            if (await IsValidWebPlayerBlobAsync(userBlobPath, cancellationToken))
            {
                return userBlobPath;
            }

            var platformBlobPath = NormalizeOptionalPath(resolvedAccounts.PlatformAccount?.BlobPath);
            return await IsValidWebPlayerBlobAsync(platformBlobPath, cancellationToken)
                ? platformBlobPath
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Spotify web-player blob path.");
            return null;
        }
    }

    private async Task<ResolvedSpotifyAccounts> ResolveActiveSpotifyAccountsAsync()
    {
        var userState = await TryLoadUserSpotifyStateAsync();
        var userAccount = ResolveActiveUserAccount(userState);
        var platformState = await _platformAuthService.LoadAsync();
        var platformAccount = ResolveActivePlatformAccount(platformState.Spotify);
        return new ResolvedSpotifyAccounts(userAccount, platformAccount);
    }

    private static SpotifyUserAccount? ResolveActiveUserAccount(SpotifyUserAuthState? state)
    {
        if (string.IsNullOrWhiteSpace(state?.ActiveAccount))
        {
            return null;
        }

        return state.Accounts.FirstOrDefault(account =>
            account.Name.Equals(state.ActiveAccount, StringComparison.OrdinalIgnoreCase));
    }

    private static SpotifyAccount? ResolveActivePlatformAccount(SpotifyConfig? spotifyConfig)
    {
        if (string.IsNullOrWhiteSpace(spotifyConfig?.ActiveAccount))
        {
            return null;
        }

        return spotifyConfig.Accounts.FirstOrDefault(account =>
            account.Name.Equals(spotifyConfig.ActiveAccount, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path;

    private async Task<bool> IsValidWebPlayerBlobAsync(string? blobPath, CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(blobPath)
        && _blobService.BlobExists(blobPath)
        && await _blobService.IsWebPlayerBlobAsync(blobPath, cancellationToken);

    private async Task<SpotifyUserAuthState?> TryLoadUserSpotifyStateAsync()
    {
        var userId = _userContext.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        return await _userAuthStore.LoadAsync(userId);
    }

    private async Task<List<SpotifyTrackSummary>> FetchTrackSummariesByIdsAsync(
        IReadOnlyList<string> trackIds,
        CancellationToken cancellationToken)
    {
        var ids = trackIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return new List<SpotifyTrackSummary>();
        }

        var tracks = await _pathfinderMetadataClient.FetchTrackSummariesByIdsAsync(
            ids,
            cancellationToken,
            maxConcurrency: 8);

        return tracks.Count == 0
            ? new List<SpotifyTrackSummary>()
            : tracks;
    }

    private async Task<Dictionary<string, SpotifyAudioFeatures>> FetchAudioFeaturesByIdsAsync(
        IReadOnlyList<string> trackIds,
        CancellationToken cancellationToken)
    {
        var ids = trackIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var results = new Dictionary<string, SpotifyAudioFeatures>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return results;
        }

        var fetched = await _pathfinderMetadataClient.FetchTrackAudioFeaturesByIdsAsync(ids, cancellationToken);
        foreach (var (id, value) in fetched)
        {
            results[id] = new SpotifyAudioFeatures(
                id,
                value.Danceability,
                value.Energy,
                value.Valence,
                value.Acousticness,
                value.Instrumentalness,
                value.Speechiness,
                value.Loudness,
                value.Tempo,
                value.TimeSignature,
                value.Liveness,
                value.Key,
                value.Mode);
        }

        return results;
    }

    private async Task<List<SpotifyTrackSummary>> HydrateTrackDetailsAsync(
        List<SpotifyTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var missingIds = tracks
            .Where(track =>
                string.IsNullOrWhiteSpace(track.Isrc) ||
                string.IsNullOrWhiteSpace(track.Artists) ||
                string.IsNullOrWhiteSpace(track.Album) ||
                !track.DurationMs.HasValue)
            .Select(track => track.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingIds.Count == 0)
        {
            return tracks;
        }

        var hydrated = await FetchTrackSummariesByIdsAsync(missingIds, cancellationToken);
        if (hydrated.Count == 0)
        {
            return tracks;
        }

        var hydratedById = hydrated.ToDictionary(track => track.Id, StringComparer.OrdinalIgnoreCase);
        var merged = new List<SpotifyTrackSummary>(tracks.Count);
        foreach (var track in tracks)
        {
            if (!hydratedById.TryGetValue(track.Id, out var hydratedTrack))
            {
                merged.Add(track);
                continue;
            }

            merged.Add(MergeTrackSummary(track, hydratedTrack));
        }

        return merged;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var number))
        {
            return number;
        }

        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static SpotifyTrackSummary ApplyAudioFeatures(SpotifyTrackSummary track, SpotifyAudioFeatures features)
    {
        return track with
        {
            Danceability = track.Danceability ?? features.Danceability,
            Energy = track.Energy ?? features.Energy,
            Valence = track.Valence ?? features.Valence,
            Acousticness = track.Acousticness ?? features.Acousticness,
            Instrumentalness = track.Instrumentalness ?? features.Instrumentalness,
            Speechiness = track.Speechiness ?? features.Speechiness,
            Loudness = track.Loudness ?? features.Loudness,
            Tempo = track.Tempo ?? features.Tempo,
            TimeSignature = track.TimeSignature ?? features.TimeSignature,
            Liveness = track.Liveness ?? features.Liveness,
            Key = track.Key ?? features.Key,
            Mode = track.Mode ?? features.Mode
        };
    }

    private static bool HasAudioFeatures(SpotifyTrackSummary track)
    {
        return track.Danceability.HasValue ||
               track.Energy.HasValue ||
               track.Valence.HasValue ||
               track.Acousticness.HasValue ||
               track.Instrumentalness.HasValue ||
               track.Speechiness.HasValue ||
               track.Loudness.HasValue ||
               track.Tempo.HasValue ||
               track.TimeSignature.HasValue ||
               track.Liveness.HasValue ||
               track.Key.HasValue ||
               track.Mode.HasValue;
    }

    private async Task<List<SpotifyTrackSummary>> HydrateTrackDetailsWithLibrespotAsync(
        string blobPath,
        List<SpotifyTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var trackIds = GetDistinctTrackIds(tracks);
        if (trackIds.Count == 0)
        {
            return tracks;
        }

        var hydratedById = await FetchHydratedLibrespotTracksByIdAsync(blobPath, trackIds, cancellationToken);
        if (hydratedById.Count == 0)
        {
            return tracks;
        }

        return MergeHydratedTracks(tracks, hydratedById);
    }

    private static List<string> GetDistinctTrackIds(IEnumerable<SpotifyTrackSummary> tracks) =>
        tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Id))
            .Select(track => track.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task<Dictionary<string, SpotifyTrackSummary>> FetchHydratedLibrespotTracksByIdAsync(
        string blobPath,
        IReadOnlyList<string> trackIds,
        CancellationToken cancellationToken)
    {
        var hydratedById = new Dictionary<string, SpotifyTrackSummary>(StringComparer.OrdinalIgnoreCase);
        const int batchSize = 50;
        var batchCount = (int)Math.Ceiling(trackIds.Count / (double)batchSize);
        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var batch = trackIds.Skip(batchIndex * batchSize).Take(batchSize).ToList();
            await HydrateLibrespotBatchAsync(hydratedById, blobPath, batch, cancellationToken);
            if (batchIndex < batchCount - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        return hydratedById;
    }

    private async Task HydrateLibrespotBatchAsync(
        Dictionary<string, SpotifyTrackSummary> hydratedById,
        string blobPath,
        IReadOnlyList<string> batchTrackIds,
        CancellationToken cancellationToken)
    {
        var batchResult = await _blobService.GetLibrespotTracksAsync(blobPath, batchTrackIds, cancellationToken);
        if (!string.IsNullOrWhiteSpace(batchResult.PayloadJson))
        {
            AppendLibrespotTrackResults(hydratedById, batchResult.PayloadJson);
            return;
        }

        foreach (var trackId in batchTrackIds)
        {
            var singleResult = await _blobService.GetLibrespotTracksAsync(blobPath, new List<string> { trackId }, cancellationToken);
            if (!string.IsNullOrWhiteSpace(singleResult.PayloadJson))
            {
                AppendLibrespotTrackResults(hydratedById, singleResult.PayloadJson);
            }
        }
    }

    private static List<SpotifyTrackSummary> MergeHydratedTracks(
        IReadOnlyList<SpotifyTrackSummary> tracks,
        Dictionary<string, SpotifyTrackSummary> hydratedById)
    {
        var merged = new List<SpotifyTrackSummary>(tracks.Count);
        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.Id) || !hydratedById.TryGetValue(track.Id, out var hydrated))
            {
                merged.Add(track);
                continue;
            }

            merged.Add(MergeTrackSummary(track, hydrated));
        }

        return merged;
    }

    private static SpotifyTrackSummary MergeTrackSummary(SpotifyTrackSummary track, SpotifyTrackSummary hydrated)
    {
        return track with
        {
            Name = PreferNonEmptyString(track.Name, hydrated.Name) ?? string.Empty,
            Isrc = PreferNonEmptyString(track.Isrc, hydrated.Isrc),
            Artists = PreferNonEmptyString(track.Artists, hydrated.Artists),
            Album = PreferNonEmptyString(track.Album, hydrated.Album),
            DurationMs = track.DurationMs ?? hydrated.DurationMs,
            SourceUrl = PreferNonEmptyString(track.SourceUrl, hydrated.SourceUrl) ?? track.SourceUrl,
            ImageUrl = PreferNonEmptyString(track.ImageUrl, hydrated.ImageUrl),
            ReleaseDate = PreferNonEmptyString(track.ReleaseDate, hydrated.ReleaseDate),
            TrackNumber = track.TrackNumber ?? hydrated.TrackNumber,
            DiscNumber = track.DiscNumber ?? hydrated.DiscNumber,
            TrackTotal = track.TrackTotal ?? hydrated.TrackTotal,
            Explicit = track.Explicit ?? hydrated.Explicit,
            AlbumId = PreferNonEmptyString(track.AlbumId, hydrated.AlbumId),
            AlbumArtist = PreferNonEmptyString(track.AlbumArtist, hydrated.AlbumArtist),
            ArtistIds = PreferNonEmptyList(track.ArtistIds, hydrated.ArtistIds),
            Label = PreferNonEmptyString(track.Label, hydrated.Label),
            Genres = PreferNonEmptyList(track.Genres, hydrated.Genres),
            Danceability = track.Danceability ?? hydrated.Danceability,
            Energy = track.Energy ?? hydrated.Energy,
            Valence = track.Valence ?? hydrated.Valence,
            Acousticness = track.Acousticness ?? hydrated.Acousticness,
            Instrumentalness = track.Instrumentalness ?? hydrated.Instrumentalness,
            Speechiness = track.Speechiness ?? hydrated.Speechiness,
            Loudness = track.Loudness ?? hydrated.Loudness,
            Tempo = track.Tempo ?? hydrated.Tempo,
            TimeSignature = track.TimeSignature ?? hydrated.TimeSignature,
            Liveness = track.Liveness ?? hydrated.Liveness,
            Key = track.Key ?? hydrated.Key,
            Mode = track.Mode ?? hydrated.Mode,
            Popularity = track.Popularity ?? hydrated.Popularity,
            PreviewUrl = PreferNonEmptyString(track.PreviewUrl, hydrated.PreviewUrl),
            HasLyrics = track.HasLyrics ?? hydrated.HasLyrics,
            LicensorUuid = PreferNonEmptyString(track.LicensorUuid, hydrated.LicensorUuid),
            AvailableMarkets = PreferNonEmptyList(track.AvailableMarkets, hydrated.AvailableMarkets),
            ReleaseDatePrecision = PreferNonEmptyString(track.ReleaseDatePrecision, hydrated.ReleaseDatePrecision),
            Copyrights = PreferNonEmptyList(track.Copyrights, hydrated.Copyrights),
            CopyrightText = PreferNonEmptyString(track.CopyrightText, hydrated.CopyrightText)
        };
    }

    private static void AppendLibrespotTrackResults(
        Dictionary<string, SpotifyTrackSummary> updated,
        string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("id", out var idProp))
            {
                continue;
            }

            var id = idProp.GetString();
            if (string.IsNullOrWhiteSpace(id) || !entry.TryGetProperty("track", out var trackProp))
            {
                continue;
            }

            var parsed = ParseLibrespotTrack(id, trackProp);
            if (parsed is not null)
            {
                updated[id] = parsed;
            }
        }
    }

    private static SpotifyTrackSummary? ParseLibrespotTrack(string id, JsonElement track)
    {
        if (track.TryGetProperty("artists", out var webArtistsProp) &&
            track.TryGetProperty("duration_ms", out var durationMsProp))
        {
            return ParseLibrespotWebApiTrack(id, track, webArtistsProp, durationMsProp);
        }

        var name = track.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var album = ParseLibrespotAlbumContext(track, isWebApiTrack: false);
        var artists = ParseArtistContributors(track.TryGetProperty(ArtistType, out var artistsProp) ? artistsProp : default);
        var isrc = TryReadExternalIsrc(track, "external_id", "type", "id");
        var numbers = ParseTrackNumbers(track, "duration", "number");
        var popularity = ReadInt(track, PopularityKey);
        var previewUrl = track.TryGetProperty("preview_url", out var previewUrlProp) ? previewUrlProp.GetString() : null;
        var hasLyrics = track.TryGetProperty("has_lyrics", out var hasLyricsProp) &&
                        hasLyricsProp.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? (bool?)hasLyricsProp.GetBoolean()
            : null;
        var licensorUuid = track.TryGetProperty("licensor_uuid", out var licensorProp) ? licensorProp.GetString() : null;

        return new SpotifyTrackSummary(
            id,
            name ?? string.Empty,
            artists.Artists,
            album.AlbumName,
            numbers.DurationMs,
            $"https://open.spotify.com/track/{id}",
            album.ImageUrl,
            isrc,
            album.ReleaseDate,
            numbers.TrackNumber,
            numbers.DiscNumber,
            album.TrackTotal,
            numbers.ExplicitFlag)
        {
            AlbumId = album.AlbumId,
            AlbumArtist = album.AlbumArtist,
            ArtistIds = artists.ArtistIds,
            Label = album.Label,
            Genres = album.Genres,
            Popularity = popularity,
            PreviewUrl = previewUrl,
            HasLyrics = hasLyrics,
            LicensorUuid = licensorUuid,
            AvailableMarkets = album.AvailableMarkets,
            ReleaseDatePrecision = album.ReleaseDatePrecision,
            Copyrights = album.Copyrights,
            CopyrightText = album.CopyrightText
        };
    }

    private static SpotifyTrackSummary ParseLibrespotWebApiTrack(
        string id,
        JsonElement track,
        JsonElement artistsProp,
        JsonElement durationMsProp)
    {
        var name = track.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var artists = ParseArtistContributors(artistsProp);
        var album = ParseLibrespotAlbumContext(track, isWebApiTrack: true);
        var isrc = TryReadExternalIsrc(track, "external_ids", "isrc");
        var numbers = ParseTrackNumbers(track, durationMsProp, "track_number");
        var popularity = ReadInt(track, PopularityKey);
        var previewUrl = track.TryGetProperty("preview_url", out var previewUrlProp) ? previewUrlProp.GetString() : null;
        var hasLyrics = track.TryGetProperty("has_lyrics", out var hasLyricsProp) &&
                        hasLyricsProp.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? (bool?)hasLyricsProp.GetBoolean()
            : null;
        var licensorUuid = track.TryGetProperty("licensor_uuid", out var licensorProp) ? licensorProp.GetString() : null;

        var sourceUrl = track.TryGetProperty("external_urls", out var externalUrlsProp) &&
                        externalUrlsProp.ValueKind == JsonValueKind.Object &&
                        externalUrlsProp.TryGetProperty("spotify", out var spotifyUrlProp)
            ? spotifyUrlProp.GetString()
            : null;

        return new SpotifyTrackSummary(
            id,
            name ?? string.Empty,
            artists.Artists,
            album.AlbumName,
            numbers.DurationMs,
            sourceUrl ?? $"https://open.spotify.com/track/{id}",
            album.ImageUrl,
            isrc,
            album.ReleaseDate,
            numbers.TrackNumber,
            numbers.DiscNumber,
            album.TrackTotal,
            numbers.ExplicitFlag)
        {
            AlbumId = album.AlbumId,
            AlbumArtist = album.AlbumArtist,
            ArtistIds = artists.ArtistIds,
            Label = album.Label,
            Genres = album.Genres,
            Popularity = popularity,
            PreviewUrl = previewUrl,
            HasLyrics = hasLyrics,
            LicensorUuid = licensorUuid,
            AvailableMarkets = album.AvailableMarkets,
            ReleaseDatePrecision = album.ReleaseDatePrecision,
            Copyrights = album.Copyrights,
            CopyrightText = album.CopyrightText
        };
    }

    private static ParsedArtistContributors ParseArtistContributors(JsonElement artistsProp)
    {
        var artists = new List<string>();
        var artistIds = new List<string>();
        if (artistsProp.ValueKind != JsonValueKind.Array)
        {
            return new ParsedArtistContributors(null, null);
        }

        foreach (var artist in artistsProp.EnumerateArray())
        {
            var artistName = artist.TryGetProperty("name", out var artistNameProp)
                ? artistNameProp.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(artistName))
            {
                artists.Add(artistName);
            }

            var artistId = TryReadEntityId(artist, ArtistType);
            if (!string.IsNullOrWhiteSpace(artistId))
            {
                artistIds.Add(artistId);
            }
        }

        return new ParsedArtistContributors(
            artists.Count > 0 ? string.Join(", ", artists) : null,
            artistIds.Count > 0 ? artistIds : null);
    }

    private static ParsedAlbumContext ParseLibrespotAlbumContext(JsonElement track, bool isWebApiTrack)
    {
        if (!track.TryGetProperty(AlbumType, out var albumProp) || albumProp.ValueKind != JsonValueKind.Object)
        {
            return new ParsedAlbumContext(null, null, null, null, null, null, null, null, null, null, null, null);
        }

        var copyrights = ParseCopyrights(albumProp, "copyrights");
        return new ParsedAlbumContext(
            albumProp.TryGetProperty("name", out var albumNameProp) ? albumNameProp.GetString() : null,
            TryReadEntityId(albumProp, AlbumType),
            isWebApiTrack ? ExtractArtists(albumProp) : ExtractArrayArtistNames(albumProp, ArtistType),
            isWebApiTrack ? ExtractFirstImageUrl(albumProp, "images") : ExtractCoverGroupImageUrl(albumProp),
            ReadAlbumReleaseDate(albumProp),
            albumProp.TryGetProperty("release_date_precision", out var precisionProp) ? precisionProp.GetString() : null,
            albumProp.TryGetProperty("label", out var labelProp) ? labelProp.GetString() : null,
            ExtractStringValues(albumProp, "genres"),
            ExtractStringValues(albumProp, "available_markets"),
            copyrights,
            JoinCopyrights(copyrights),
            ReadAlbumTrackTotal(albumProp, isWebApiTrack));
    }

    private static string? TryReadEntityId(JsonElement element, string type)
    {
        if (element.TryGetProperty("id", out var idProp))
        {
            return idProp.GetString();
        }

        return element.TryGetProperty("uri", out var uriProp)
            ? ExtractSpotifyIdFromUri(uriProp.GetString(), type)
            : null;
    }

    private static string? ExtractArrayArtistNames(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var artistsProp) || artistsProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = artistsProp.EnumerateArray()
            .Select(artist => artist.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static string? ReadAlbumReleaseDate(JsonElement albumProp)
    {
        if (albumProp.TryGetProperty("date", out var releaseDateProp))
        {
            return releaseDateProp.GetString();
        }

        return albumProp.TryGetProperty("release_date", out releaseDateProp)
            ? releaseDateProp.GetString()
            : null;
    }

    private static int? ReadAlbumTrackTotal(JsonElement albumProp, bool isWebApiTrack)
    {
        if (isWebApiTrack)
        {
            return albumProp.TryGetProperty("total_tracks", out var trackTotalProp) &&
                   trackTotalProp.ValueKind == JsonValueKind.Number
                ? trackTotalProp.GetInt32()
                : null;
        }

        return albumProp.TryGetProperty("tracks", out var tracksProp) &&
               tracksProp.ValueKind == JsonValueKind.Array
            ? tracksProp.GetArrayLength()
            : null;
    }

    private static string? ExtractFirstImageUrl(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var imagesProp) || imagesProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return imagesProp
            .EnumerateArray()
            .Select(image => image.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
    }

    private static string? ExtractCoverGroupImageUrl(JsonElement albumProp)
    {
        if (!albumProp.TryGetProperty("cover_group", out var coverProp) ||
            !coverProp.TryGetProperty("image", out var imagesProp) ||
            imagesProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var fileId in imagesProp
                     .EnumerateArray()
                     .Select(image => image.TryGetProperty("file_id", out var fileIdProp) ? fileIdProp.GetString() : null)
                     .Where(fileId => !string.IsNullOrWhiteSpace(fileId)))
        {
            var imageUrl = BuildImageUrlFromPicture(fileId);
            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                return imageUrl;
            }
        }

        return null;
    }

    private static readonly Regex SpotifyTrackUrlRegex = new(
        @"open\.spotify\.com/track/([A-Za-z0-9]{22})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static bool LooksLikeSpotifyTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 22)
        {
            return false;
        }

        if (value.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetPlaylistFromCache(string playlistId, out SpotifyUrlMetadata metadata)
    {
        metadata = null!;
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return false;
        }

        if (PlaylistCache.TryGetValue(playlistId, out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.Stamp <= PlaylistCacheTtl)
            {
                metadata = entry.Data;
                return true;
            }

            PlaylistCache.TryRemove(playlistId, out _);
        }

        return false;
    }

    private static void CachePlaylist(string playlistId, SpotifyUrlMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return;
        }

        PlaylistCache[playlistId] = (DateTimeOffset.UtcNow, metadata);
    }

    private static string? ExtractArtists(JsonElement item)
    {
        if (!item.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = artists.EnumerateArray()
            .Select(artist => artist.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static List<string>? ExtractStringValues(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parsed = values.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String)
            .Select(entry => entry.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parsed.Count == 0 ? null : parsed;
    }

    private static SpotifyAlbumSummary? ParseLibrespotAlbum(string albumId, string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (!TryResolveEntityId(root, albumId, out var resolvedAlbumId))
            {
                return null;
            }

            var sourceUrl = ResolveAlbumSourceUrl(root, resolvedAlbumId);
            var imageUrl = ResolveAlbumImageUrl(root);
            var copyrights = ParseCopyrights(root, "copyrights");
            return BuildLibrespotAlbumSummary(root, resolvedAlbumId, sourceUrl, imageUrl, copyrights);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool TryResolveEntityId(JsonElement item, string fallbackId, out string id)
    {
        id = item.TryGetProperty("id", out var idProp)
            ? (idProp.GetString() ?? string.Empty)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = fallbackId;
        }

        return !string.IsNullOrWhiteSpace(id);
    }

    private static string ResolveAlbumSourceUrl(JsonElement root, string albumId)
    {
        var url = TryGetString(root, "external_urls", "spotify");
        return string.IsNullOrWhiteSpace(url) ? $"https://open.spotify.com/album/{albumId}" : url;
    }

    private static string? ResolveAlbumImageUrl(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var image in images.EnumerateArray())
        {
            var url = TryReadJsonStringProperty(image, "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    private static SpotifyAlbumSummary BuildLibrespotAlbumSummary(
        JsonElement root,
        string albumId,
        string sourceUrl,
        string? imageUrl,
        List<SpotifyCopyrightInfo>? copyrights)
    {
        return new SpotifyAlbumSummary(
            albumId,
            TryReadJsonStringProperty(root, "name") ?? "Spotify album",
            ExtractArtists(root),
            imageUrl,
            sourceUrl,
            ReadInt(root, "total_tracks"),
            TryReadJsonStringProperty(root, "release_date"),
            null,
            TryReadJsonStringProperty(root, "album_type"))
        {
            Genres = ExtractStringValues(root, "genres"),
            Label = TryReadJsonStringProperty(root, "label"),
            Popularity = ReadInt(root, PopularityKey),
            ReleaseDatePrecision = TryReadJsonStringProperty(root, "release_date_precision"),
            AvailableMarkets = ExtractStringValues(root, "available_markets"),
            Copyrights = copyrights,
            CopyrightText = JoinCopyrights(copyrights),
            Review = JoinStringArray(root, "review"),
            RelatedAlbumIds = ExtractRelatedEntityIds(root, "related"),
            OriginalTitle = TryReadJsonStringProperty(root, "original_title"),
            VersionTitle = TryReadJsonStringProperty(root, "version_title"),
            SalePeriods = ParseSalePeriods(root, "sale_period"),
            Availability = ParseAvailability(root, "availability")
        };
    }

    private static SpotifyArtistFallbackMetadata? ParseLibrespotArtist(string artistId, string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            id ??= artistId;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var imageUrl = TryGetNestedImageUrl(root, PortraitGroupKey)
                ?? TryGetNestedImageUrl(root, "portrait")
                ?? TryGetNestedImageUrl(root, "biography", PortraitGroupKey);
            return new SpotifyArtistFallbackMetadata(
                id,
                root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null,
                imageUrl)
            {
                Biography = ParseArtistBiography(root),
                Genres = ExtractStringValues(root, "genre"),
                Popularity = ReadInt(root, PopularityKey),
                RelatedArtists = ParseLibrespotRelatedArtists(root),
                ActivityPeriods = ParseActivityPeriods(root, "activity_period"),
                SalePeriods = ParseSalePeriods(root, "sale_period"),
                Availability = ParseAvailability(root, "availability"),
                IsPortraitAlbumCover = root.TryGetProperty("is_portrait_album_cover", out var portraitCoverProp) &&
                                       portraitCoverProp.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? portraitCoverProp.GetBoolean()
                    : (bool?)null,
                Gallery = ParseArtistGallery(root)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string? PreferNonEmptyString(string? preferred, string? fallback)
    {
        return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
    }

    private static IReadOnlyList<T>? PreferNonEmptyList<T>(IReadOnlyList<T>? preferred, IReadOnlyList<T>? fallback)
    {
        return preferred is { Count: > 0 } ? preferred : fallback;
    }

    private static List<SpotifyCopyrightInfo>? ParseCopyrights(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parsed = values.EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => new SpotifyCopyrightInfo(
                entry.TryGetProperty("text", out var textProp) ? (textProp.GetString() ?? string.Empty) : string.Empty,
                entry.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null))
            .Where(static value => !string.IsNullOrWhiteSpace(value.Text))
            .ToList();

        return parsed.Count == 0 ? null : parsed;
    }

    private static string? JoinCopyrights(IReadOnlyList<SpotifyCopyrightInfo>? copyrights)
    {
        if (copyrights is null || copyrights.Count == 0)
        {
            return null;
        }

        var parts = copyrights
            .Select(static value => value.Text?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    private static string? JoinStringArray(JsonElement item, string property)
    {
        var values = ExtractStringValues(item, property);
        return values is { Count: > 0 } ? string.Join(Environment.NewLine, values) : null;
    }

    private static List<string>? ExtractRelatedEntityIds(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var ids = values.EnumerateArray()
            .Select(static entry =>
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    return entry.GetString();
                }

                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString();
                }

                return null;
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ids.Count == 0 ? null : ids;
    }

    private static List<SpotifySalePeriod>? ParseSalePeriods(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var periods = values.EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => new SpotifySalePeriod(
                ParseRestrictionCatalogs(entry),
                ParseDateValue(entry, "start"),
                ParseDateValue(entry, "end")))
            .Where(static entry => entry.RestrictionCatalogs.Count > 0 || entry.Start is not null || entry.End is not null)
            .ToList();

        return periods.Count == 0 ? null : periods;
    }

    private static List<SpotifyAvailabilityInfo>? ParseAvailability(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var availability = values.EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => new SpotifyAvailabilityInfo(
                ExtractStringValues(entry, "catalogue_str") ?? new List<string>(),
                ParseDateValue(entry, "start")))
            .Where(static entry => entry.Catalogs.Count > 0 || entry.Start is not null)
            .ToList();

        return availability.Count == 0 ? null : availability;
    }

    private static IReadOnlyList<string> ParseRestrictionCatalogs(JsonElement item)
    {
        if (!item.TryGetProperty("restriction", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var catalogs = new List<string>();
        foreach (var entry in values.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (ExtractStringValues(entry, "catalogue_str") is { Count: > 0 } strings)
            {
                catalogs.AddRange(strings);
            }
        }

        return catalogs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SpotifyDateValue? ParseDateValue(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var year = ReadJsonString(value, "year");
        var month = ReadJsonString(value, "month");
        var day = ReadJsonString(value, "day");
        var hour = ReadJsonString(value, "hour");
        var minute = ReadJsonString(value, "minute");
        if (year is null && month is null && day is null && hour is null && minute is null)
        {
            return null;
        }

        return new SpotifyDateValue(year, month, day, hour, minute);
    }

    private static string? ReadJsonString(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.TryGetInt32(out var value) ? value.ToString(CultureInfo.InvariantCulture) : null,
            _ => null
        };
    }

    private static string? TryReadJsonStringProperty(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return prop.GetString();
    }

    private static string? ParseArtistBiography(JsonElement item)
    {
        if (!item.TryGetProperty("biography", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in values.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object &&
                entry.TryGetProperty("text", out var textProp) &&
                textProp.ValueKind == JsonValueKind.String)
            {
                var text = textProp.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static List<SpotifyRelatedArtist>? ParseLibrespotRelatedArtists(JsonElement item)
    {
        if (!item.TryGetProperty("related", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var related = values.EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry =>
            {
                var id = entry.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var name = entry.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    return null;
                }

                var imageUrl = TryGetNestedImageUrl(entry, PortraitGroupKey) ?? TryGetNestedImageUrl(entry, "portrait");
                var images = string.IsNullOrWhiteSpace(imageUrl)
                    ? new List<SpotifyImage>()
                    : new List<SpotifyImage> { new SpotifyImage(imageUrl, null, null) };
                return new SpotifyRelatedArtist(id, name, images, $"https://open.spotify.com/artist/{id}");
            })
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .ToList();

        return related.Count == 0 ? null : related;
    }

    private static List<SpotifyActivityPeriod>? ParseActivityPeriods(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var periods = values.EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => new SpotifyActivityPeriod(
                ReadInt(entry, "start_year"),
                ReadInt(entry, "end_year"),
                ReadInt(entry, "decade")))
            .Where(static entry => entry.StartYear.HasValue || entry.EndYear.HasValue || entry.Decade.HasValue)
            .ToList();

        return periods.Count == 0 ? null : periods;
    }

    private static List<string>? ParseArtistGallery(JsonElement item)
    {
        if (!item.TryGetProperty("biography", out var biography) || biography.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var images = new List<string>();
        foreach (var entry in biography.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = TryGetNestedImageUrl(entry, PortraitGroupKey) ?? TryGetNestedImageUrl(entry, "portrait");
            if (!string.IsNullOrWhiteSpace(url))
            {
                images.Add(url);
            }
        }

        return images.Count == 0
            ? null
            : images.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? TryGetNestedImageUrl(JsonElement item, params string[] path)
    {
        if (!TryResolveNestedElement(item, path, out var current))
        {
            return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.Array => TryReadImageUrlFromArray(current),
            JsonValueKind.Object => TryReadImageUrlFromObject(current),
            _ => null
        };
    }

    private static bool TryResolveNestedElement(JsonElement item, IReadOnlyList<string> path, out JsonElement current)
    {
        current = item;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                current = default;
                return false;
            }
        }

        return true;
    }

    private static string? TryReadImageUrlFromArray(JsonElement imageArray)
    {
        foreach (var entry in imageArray.EnumerateArray())
        {
            var imageUrl = TryReadImageUrlFromObject(entry);
            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                return imageUrl;
            }
        }

        return null;
    }

    private static string? TryReadImageUrlFromObject(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (item.TryGetProperty("image", out var imageArray) && imageArray.ValueKind == JsonValueKind.Array)
        {
            return TryReadImageUrlFromArray(imageArray);
        }

        return BuildImageUrlFromPicture(TryReadJsonStringProperty(item, "file_id"))
            ?? TryReadJsonStringProperty(item, "url");
    }

    private static LibrespotPlaylistPayload? ParseLibrespotPlaylistPayload(string playlistId, string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var attributes = root.TryGetProperty("attributes", out var attributesProp) ? attributesProp : default;
            var (name, description, imageUrl) = ParsePlaylistAttributes(attributes);
            var ownerName = TryGetString(root, "owner_username");
            var snapshotId = TryGetString(root, "revision");
            var totalTracks = TryReadInt32(root, "length");
            var trackIds = ParsePlaylistTrackIds(root);

            return new LibrespotPlaylistPayload(
                playlistId,
                name,
                description,
                imageUrl,
                ownerName,
                snapshotId,
                totalTracks,
                trackIds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }

    private static (string? Name, string? Description, string? ImageUrl) ParsePlaylistAttributes(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        return (
            TryGetString(attributes, "name"),
            CleanSpotifyDescription(TryGetString(attributes, "description")),
            BuildImageUrlFromPicture(TryGetString(attributes, "picture")));
    }

    private static List<string> ParsePlaylistTrackIds(JsonElement root)
    {
        if (!TryGetNested(root, out var itemsProp, "contents", "items")
            || itemsProp.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return itemsProp
            .EnumerateArray()
            .Select(item => item.TryGetProperty("uri", out var uriProp) ? uriProp.GetString() : null)
            .Select(uri => ExtractSpotifyIdFromUri(uri, TrackType))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList()!;
    }

    private static string? TryReadExternalIsrc(
        JsonElement track,
        string propertyName,
        string nestedTypePropertyName,
        string nestedIdPropertyName)
    {
        if (!track.TryGetProperty(propertyName, out var externalProp) || externalProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var external in externalProp.EnumerateArray())
        {
            var type = external.TryGetProperty(nestedTypePropertyName, out var typeProp) ? typeProp.GetString() : null;
            if (!string.Equals(type, "isrc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = external.TryGetProperty(nestedIdPropertyName, out var idProp) ? idProp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return null;
    }

    private static string? TryReadExternalIsrc(
        JsonElement track,
        string propertyName,
        string nestedIdPropertyName)
    {
        if (!track.TryGetProperty(propertyName, out var externalProp)
            || externalProp.ValueKind != JsonValueKind.Object
            || !externalProp.TryGetProperty(nestedIdPropertyName, out var idProp))
        {
            return null;
        }

        return idProp.GetString();
    }

    private static ParsedTrackNumbers ParseTrackNumbers(
        JsonElement track,
        string durationPropertyName,
        string trackNumberPropertyName)
    {
        var durationMs = TryReadInt32(track, durationPropertyName);
        return BuildParsedTrackNumbers(track, durationMs, trackNumberPropertyName);
    }

    private static ParsedTrackNumbers ParseTrackNumbers(
        JsonElement track,
        JsonElement durationElement,
        string trackNumberPropertyName)
    {
        var durationMs = durationElement.ValueKind == JsonValueKind.Number && durationElement.TryGetInt32(out var durationValue)
            ? durationValue
            : (int?)null;
        return BuildParsedTrackNumbers(track, durationMs, trackNumberPropertyName);
    }

    private static ParsedTrackNumbers BuildParsedTrackNumbers(
        JsonElement track,
        int? durationMs,
        string trackNumberPropertyName)
    {
        var trackNumber = TryReadInt32(track, trackNumberPropertyName);
        var discNumber = TryReadInt32(track, "disc_number");
        var explicitFlag = TryReadBoolean(track, "explicit");
        return new ParsedTrackNumbers(durationMs, trackNumber, discNumber, explicitFlag);
    }

    private static int? TryReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out var value)
                ? value
                : null;

    private static bool? TryReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? prop.GetBoolean()
                : (bool?)null;

    private static string? BuildImageUrlFromPicture(string? picture)
    {
        if (string.IsNullOrWhiteSpace(picture))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(picture);
            var hex = BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            return string.IsNullOrWhiteSpace(hex) ? null : $"https://i.scdn.co/image/{hex}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }

    private static string? ExtractSpotifyIdFromUri(string? uri, string type)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        var token = $"spotify:{type}:";
        if (uri.StartsWith(token, StringComparison.OrdinalIgnoreCase))
        {
            return uri[token.Length..].Trim();
        }

        return null;
    }

    private static bool IsValidIsrc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Replace("-", string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 12)
        {
            return false;
        }

        if (normalized.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            return false;
        }

        return true;
    }

    public static bool TryParseSpotifyUrl(string input, out string type, out string id)
    {
        var parsed = ParseSpotifyUrl(input);
        if (parsed is null)
        {
            type = string.Empty;
            id = string.Empty;
            return false;
        }

        type = parsed.Type;
        id = parsed.Id;
        return true;
    }

    private static ParsedSpotifyUrl? ParseSpotifyUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                var type = parts[1];
                var id = parts[2];
                return new ParsedSpotifyUrl(type, id);
            }
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Host is not ("open.spotify.com" or "play.spotify.com"))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return null;
        }

        var typeSegment = segments[0].ToLowerInvariant();
        var idSegment = segments[1];
        if (typeSegment is TrackType or AlbumType or PlaylistType or ArtistType or "show" or "episode")
        {
            return new ParsedSpotifyUrl(typeSegment, idSegment);
        }

        return null;
    }

    private sealed record SearchContext(string AccessToken, string Market, string Source, string? BlobPath);
    private sealed record ParsedSpotifyUrl(string Type, string Id);
    private sealed record LibrespotPlaylistPayload(
        string PlaylistId,
        string? Name,
        string? Description,
        string? ImageUrl,
        string? OwnerName,
        string? SnapshotId,
        int? TotalTracks,
        List<string> TrackIds);

    private static string? CleanSpotifyDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var decoded = WebUtility.HtmlDecode(description);
        var stripped = Regex.Replace(decoded, "<.*?>", string.Empty, RegexOptions.Singleline, RegexTimeout);
        var normalized = Regex.Replace(stripped, "\\s+", " ", RegexOptions.None, RegexTimeout).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed record SpotifyUrlMetadata(
    string Type,
    string Id,
    string Name,
    string SourceUrl,
    string? ImageUrl,
    string? Subtitle,
    int? TotalTracks,
    int? DurationMs,
    List<SpotifyTrackSummary> TrackList,
    List<SpotifyAlbumSummary> AlbumList,
    string? OwnerName,
    int? Followers,
    string? SnapshotId)
{
    public string? OwnerImageUrl { get; init; }
}

public sealed record SpotifyTrackArtwork(
    string? AlbumCoverUrl,
    string? ArtistImageUrl);

public sealed record SpotifyPlaylistSnapshot(
    string? SnapshotId,
    int? TotalTracks,
    string? Name,
    string? Description,
    string? ImageUrl,
    List<SpotifyTrackSummary> Tracks);

public sealed record SpotifyPlaylistPage(
    string? SnapshotId,
    int? TotalTracks,
    string? Name,
    string? Description,
    string? ImageUrl,
    List<SpotifyTrackSummary> Tracks,
    bool HasMore);

public sealed record SpotifyTrackSummary(
    string Id,
    string Name,
    string? Artists,
    string? Album,
    int? DurationMs,
    string SourceUrl,
    string? ImageUrl,
    string? Isrc,
    string? ReleaseDate = null,
    int? TrackNumber = null,
    int? DiscNumber = null,
    int? TrackTotal = null,
    bool? Explicit = null,
    double? Danceability = null,
    double? Energy = null,
    double? Valence = null,
    double? Acousticness = null,
    double? Instrumentalness = null,
    double? Speechiness = null,
    double? Loudness = null,
    double? Tempo = null,
    int? TimeSignature = null,
    double? Liveness = null,
    int? Key = null,
    int? Mode = null)
{
    public string? AlbumId { get; init; }
    public string? AlbumArtist { get; init; }
    public IReadOnlyList<string>? ArtistIds { get; init; }
    public string? Label { get; init; }
    public IReadOnlyList<string>? Genres { get; init; }
    public string? AlbumGroup { get; init; }
    public string? ReleaseType { get; init; }
    public int? Popularity { get; init; }
    public string? PreviewUrl { get; init; }
    public bool? HasLyrics { get; init; }
    public string? LicensorUuid { get; init; }
    public IReadOnlyList<string>? AvailableMarkets { get; init; }
    public string? ReleaseDatePrecision { get; init; }
    public IReadOnlyList<SpotifyCopyrightInfo>? Copyrights { get; init; }
    public string? CopyrightText { get; init; }
}

public sealed record SpotifyAlbumSummary(
    string Id,
    string Name,
    string? Artists,
    string? ImageUrl,
    string SourceUrl,
    int? TotalTracks,
    string? ReleaseDate = null,
    string? AlbumGroup = null,
    string? ReleaseType = null) : SpotifyAlbumMetadataFields;

public sealed record SpotifyCopyrightInfo(string Text, string? Type);

public sealed record SpotifyDateValue(
    string? Year,
    string? Month,
    string? Day,
    string? Hour = null,
    string? Minute = null);

public sealed record SpotifyAvailabilityInfo(
    IReadOnlyList<string> Catalogs,
    SpotifyDateValue? Start = null);

public sealed record SpotifySalePeriod(
    IReadOnlyList<string> RestrictionCatalogs,
    SpotifyDateValue? Start = null,
    SpotifyDateValue? End = null);

public sealed record SpotifyActivityPeriod(
    int? StartYear,
    int? EndYear,
    int? Decade);

public sealed record SpotifyArtistFallbackMetadata(
    string Id,
    string? Name,
    string? ImageUrl)
{
    public string? Biography { get; init; }
    public IReadOnlyList<string>? Genres { get; init; }
    public int? Popularity { get; init; }
    public IReadOnlyList<SpotifyRelatedArtist>? RelatedArtists { get; init; }
    public IReadOnlyList<SpotifyActivityPeriod>? ActivityPeriods { get; init; }
    public IReadOnlyList<SpotifySalePeriod>? SalePeriods { get; init; }
    public IReadOnlyList<SpotifyAvailabilityInfo>? Availability { get; init; }
    public bool? IsPortraitAlbumCover { get; init; }
    public IReadOnlyList<string>? Gallery { get; init; }
}

internal sealed record SpotifyAudioFeatures(
    string Id,
    double? Danceability,
    double? Energy,
    double? Valence,
    double? Acousticness,
    double? Instrumentalness,
    double? Speechiness,
    double? Loudness,
    double? Tempo,
    int? TimeSignature,
    double? Liveness,
    int? Key,
    int? Mode);

internal sealed record PlaylistTrackCache(
    DateTimeOffset Stamp,
    string Source,
    List<SpotifyTrackSummary> Tracks);
