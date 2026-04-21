using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Caching.Memory;

namespace DeezSpoTag.Web.Services;

public sealed class BoomplayMetadataService
{
    private const string HttpsScheme = "https";
    private const string BoomplaySource = "boomplay";
    private const string PropertyAttribute = "property";
    private const string ValueField = "value";
    private const string GenreField = "genre";
    private const string TitleField = "title";
    private const string ImgXPath = ".//img";
    private const string RecommendedPlaylistsTitle = "Recommended Playlists";
    private const string BoomplayWebHost = "www.boomplay.com";
    private const string BoomplayRootHost = "boomplay.com";
    private const string DefaultUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36";
    private const int PlaylistSongFetchConcurrency = 3;
    private const int SongMetadataMaxAttempts = 3;
    private const int StreamTagMaxAttempts = 3;
    private const int StreamTagProbeBytes = 4096;
    private const int StreamTagMaxBytes = 2 * 1024 * 1024;
    private const int SongCacheSizeLimit = 5000;
    private const int PlaylistCacheSizeLimit = 500;
    private const int SearchCacheSizeLimit = 500;
    private static readonly byte[] ResourceAesKey = Encoding.ASCII.GetBytes("boomplayVr3xopAM");
    private static readonly byte[] ResourceAesIv = Encoding.ASCII.GetBytes("boomplay8xIsKTn9");
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex SongPathRegex = CreateRegex(@"(?:^|/)songs/(?<id>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlaylistPathRegex = CreateRegex(@"(?:^|/)playlists/(?<id>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrendingSongsPathRegex = CreateRegex(@"(?:^|/)trending-songs(?:/)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SongIdInHtmlRegex = CreateRegex(@"/songs/(?<id>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmbeddedMusicItemRegex = CreateRegex(
        "\"itemType\"\\s*:\\s*\"MUSIC\"\\s*,\\s*\"itemID\"\\s*:\\s*\"?(?<id>\\d{6,})\"?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmbeddedMusicItemReverseRegex = CreateRegex(
        "\"itemID\"\\s*:\\s*\"?(?<id>\\d{6,})\"?\\s*,\\s*\"itemType\"\\s*:\\s*\"MUSIC\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmbeddedSongIdRegex = CreateRegex(
        "\"(?:songId|songID|musicId|musicID|trackId|trackID)\"\\s*:\\s*\"?(?<id>\\d{6,})\"?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmbeddedPlaylistIdRegex = CreateRegex(
        "\"(?:playlistId|playlistID|itemID|id)\"\\s*:\\s*\"?(?<id>\\d{6,})\"?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmbeddedPlaylistNameRegex = CreateRegex(
        "\"(?:itemName|playlistName|title|name)\"\\s*:\\s*\"(?<value>[^\"]{1,200})\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmbeddedPlaylistImageRegex = CreateRegex(
        "\"(?:itemPic|cover|coverUrl|image|imageUrl|img)\"\\s*:\\s*\"(?<value>(?:https?:)?//[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CssUrlRegex = CreateRegex(
        @"url\((['""]?)(?<url>[^'"")]+)\1\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MultiWhitespaceRegex = CreateRegex(@"\s+", RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = CreateRegex(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlBreakRegex = CreateRegex(@"<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JsonGenreRegex = CreateRegex("\"genre\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JsonGenreNameRegex = CreateRegex("\"(?:genreName|primaryGenre|primaryGenreName|musicGenre)\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JsonGenresArrayRegex = CreateRegex("\"(?:genreNames|genres|musicGenres)\"\\s*:\\s*\\[(?<value>[^\\]]{1,1200})\\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JsonArrayStringRegex = CreateRegex("\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
    private static readonly Regex JsonIsrcRegex = CreateRegex("\"isrc\"\\s*:\\s*\"(?<value>[A-Za-z0-9]{8,20})\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReleaseYearRegex = CreateRegex(@"\b(?<year>19\d{2}|20\d{2}|21\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex BoomplayNoiseSuffixRegex = CreateRegex(
        @"\s*(?:" +
        @"MP3\s+Download(?:\s*&\s*Lyrics)?" +
        @"|Download(?:\s*&\s*Lyrics)?" +
        @"|Lyrics\s+Video" +
        @"|Lyric\s+Video" +
        @"|Official\s+(?:Music\s+)?Video" +
        @"|Official\s+Audio" +
        @"|Official\s+Visualizer" +
        @"|Visualizer" +
        @"|Audio" +
        @"|Video" +
        @"|Lyrics" +
        @")\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ProdCreditRegex = CreateRegex(
        @"\s*[\(\[]\s*Prod(?:uced)?\.\s+(?:by\s+)?[^\)\]]+[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StandaloneYearRegex = CreateRegex(
        @"\s*(?:[\(\[]\s*(?:19|20)\d{2}\s*[\)\]]|-\s*(?:19|20)\d{2})\s*$",
        RegexOptions.Compiled);
    private static readonly Regex FeaturingNormalizeRegex = CreateRegex(
        @"(?<=[\(\[,])\s*(?:featuring|feat\.?|ft\.?)\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BoomplayPromoDescriptionSuffixRegex = CreateRegex(
        @"\s*listen\s+and\s+download\s+music\s+for\s+free\s+on\s+boomplay!?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BoomplayCoverLinePrefixRegex = CreateRegex(
        @"^\s*cover:\s*[^\r\n]{1,160}(?:\r?\n)+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StreamTrackNumberPrefixRegex = CreateRegex(
        @"^\s*track\s*no\.?\s*\d+\s*[_\-\.:)]*\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StreamNumericPrefixRegex = CreateRegex(
        @"^\s*\d{1,2}\s*[\.\-_)]\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StreamMasterSuffixRegex = CreateRegex(
        @"\s*(?:[-_:]\s*)?(?:\(\s*)?master(?:\s*\))?\s*(?:\(\s*\d+\s*\)|\d+)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LeadingArtistDashRegex = CreateRegex(
        @"^\s*(?<artist>[^-]{2,80}?)\s*[-–]\s*(?<title>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArtistFeaturingTailRegex = CreateRegex(
        @"\s*(?:feat\.?|ft\.?|featuring|with|x)\b.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NonWordSeparatorRegex = CreateRegex(
        @"[^\p{L}\p{Nd}]+",
        RegexOptions.Compiled);
    private static readonly Regex MultiUnderscoreRegex = CreateRegex(
        @"_+",
        RegexOptions.Compiled);
    private static Regex CreateRegex(string pattern, RegexOptions options)
        => new(pattern, options, RegexTimeout);
    private static string BuildUrl(string host) => $"{HttpsScheme}://{host}";
    private static readonly string BoomplayBaseUrl = BuildUrl(BoomplayWebHost);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BoomplayMetadataService> _logger;
    private readonly MemoryCache _songCache = new(new MemoryCacheOptions { SizeLimit = SongCacheSizeLimit });
    private readonly MemoryCache _playlistCache = new(new MemoryCacheOptions { SizeLimit = PlaylistCacheSizeLimit });
    private readonly MemoryCache _searchCache = new(new MemoryCacheOptions { SizeLimit = SearchCacheSizeLimit });
    private static readonly HashSet<string> GenreNoiseValues = new(StringComparer.OrdinalIgnoreCase)
    {
        BoomplaySource,
        "boomplay music",
        "music",
        "song",
        "songs",
        "album",
        "albums",
        "artist",
        "artists",
        "video",
        "videos",
        "lyrics",
        "download",
        "free",
        "offline",
        "streaming",
        "podcast",
        "podcasts",
        "unknown"
    };
    private static readonly string[] ArtistDescriptionMarkers =
    [
        " by ",
        "By "
    ];
    private static readonly char[] GenreSeparators = ['/', ';', ','];

    public BoomplayMetadataService(
        IHttpClientFactory httpClientFactory,
        ILogger<BoomplayMetadataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public static bool TryParseBoomplayUrl(string? url, out string type, out string id)
    {
        type = string.Empty;
        id = string.Empty;

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsBoomplayHost(uri.Host))
        {
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var songMatch = SongPathRegex.Match(path);
        if (songMatch.Success)
        {
            type = "track";
            id = songMatch.Groups["id"].Value;
            return true;
        }

        var playlistMatch = PlaylistPathRegex.Match(path);
        if (playlistMatch.Success)
        {
            type = "playlist";
            id = playlistMatch.Groups["id"].Value;
            return true;
        }

        if (TrendingSongsPathRegex.IsMatch(path))
        {
            type = "trending";
            id = "trending-songs";
            return true;
        }

        return false;
    }

    public static bool IsBoomplayUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsBoomplayHost(uri.Host);
    }

    public static bool IsBoomplayHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalizedHost = host.Trim().TrimEnd('.');
        return normalizedHost.Equals(BoomplayRootHost, StringComparison.OrdinalIgnoreCase)
               || normalizedHost.EndsWith("." + BoomplayRootHost, StringComparison.OrdinalIgnoreCase);
    }

    public Task<BoomplayTrackMetadata?> GetSongAsync(
        string songId,
        CancellationToken cancellationToken)
    {
        return GetSongWithPolicyAsync(
            songId,
            bypassCache: false,
            maxAttempts: SongMetadataMaxAttempts,
            streamTagAttempts: StreamTagMaxAttempts,
            cacheLowConfidence: false,
            cancellationToken: cancellationToken);
    }

    public async Task<string?> ResolveSongStreamUrlAsync(string songId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(songId))
        {
            return null;
        }

        var normalizedSongId = songId.Trim();
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var mediaUrl = await ResolveSongStreamUrlOnceAsync(normalizedSongId, cancellationToken);
                if (!string.IsNullOrWhiteSpace(mediaUrl))
                {
                    return mediaUrl;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Boomplay stream URL resolve failed for SongId (attempt Attempt)");
            }

            if (attempt < maxAttempts)
            {
                await DelayForRetryAsync(attempt, cancellationToken);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<BoomplayTrackMetadata>> GetSongsAsync(
        IEnumerable<string> songIds,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeSongIds(songIds);

        if (ids.Count == 0)
        {
            return Array.Empty<BoomplayTrackMetadata>();
        }

        var results = await FetchSongsFirstPassAsync(ids, cancellationToken);
        var firstPassById = results.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var retryIds = ResolveRetrySongIds(ids, firstPassById);
        await FetchSongsSecondPassAsync(retryIds, firstPassById, cancellationToken);

        return ids
            .Where(firstPassById.ContainsKey)
            .Select(id => firstPassById[id])
            .ToList();
    }

    private static List<string> NormalizeSongIds(IEnumerable<string> songIds) =>
        songIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private async Task<ConcurrentBag<BoomplayTrackMetadata>> FetchSongsFirstPassAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<BoomplayTrackMetadata>();
        using var gate = new SemaphoreSlim(PlaylistSongFetchConcurrency);
        var tasks = ids.Select(id => FetchSongFirstPassAsync(id, results, gate, cancellationToken));

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task FetchSongFirstPassAsync(
        string id,
        ConcurrentBag<BoomplayTrackMetadata> results,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var track = await GetSongWithPolicyAsync(
                id,
                bypassCache: false,
                maxAttempts: SongMetadataMaxAttempts,
                streamTagAttempts: Math.Max(1, StreamTagMaxAttempts - 1),
                cacheLowConfidence: false,
                cancellationToken: cancellationToken);
            if (track != null)
            {
                results.Add(track);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Boomplay song fetch failed for {SongId}", id);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static List<string> ResolveRetrySongIds(
        IReadOnlyList<string> ids,
        Dictionary<string, BoomplayTrackMetadata> firstPassById) =>
        ids
            .Where(id => !firstPassById.TryGetValue(id, out var track) || track == null || IsLowConfidenceSongMetadata(track))
            .ToList();

    private async Task FetchSongsSecondPassAsync(
        List<string> retryIds,
        Dictionary<string, BoomplayTrackMetadata> firstPassById,
        CancellationToken cancellationToken)
    {
        if (retryIds.Count == 0)
        {
            return;
        }

        foreach (var id in retryIds)
        {
            try
            {
                await DelayForRetryAsync(3, cancellationToken);
                var retryTrack = await GetSongWithPolicyAsync(
                    id,
                    bypassCache: true,
                    maxAttempts: SongMetadataMaxAttempts + 2,
                    streamTagAttempts: StreamTagMaxAttempts + 1,
                    cacheLowConfidence: false,
                    cancellationToken: cancellationToken);
                if (retryTrack == null)
                {
                    continue;
                }

                firstPassById[id] = retryTrack;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Boomplay second-pass fetch failed for {SongId}", id);
                }
            }
        }
    }

    public async Task<IReadOnlyList<BoomplayTrackMetadata>> SearchSongsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<BoomplayTrackMetadata>();
        }

        limit = Math.Clamp(limit, 1, 30);
        var cacheKey = $"{query.Trim().ToLowerInvariant()}::{limit}";
        if (_searchCache.TryGetValue(cacheKey, out List<BoomplayTrackMetadata>? cached) && cached != null)
        {
            return cached;
        }

        var encodedQuery = Uri.EscapeDataString(query.Trim());
        var searchUrls = new[]
        {
            $"{BoomplayBaseUrl}/search/default/{encodedQuery}",
            $"{BoomplayBaseUrl}/search/{encodedQuery}",
            $"{BoomplayBaseUrl}/search?q={encodedQuery}"
        };

        var songIds = new List<string>();
        var songHints = new Dictionary<string, BoomplayTrackHint>(StringComparer.Ordinal);
        for (var urlIndex = 0; urlIndex < searchUrls.Length; urlIndex++)
        {
            var htmlText = await GetHtmlAsync(searchUrls[urlIndex], cancellationToken);
            if (string.IsNullOrWhiteSpace(htmlText))
            {
                continue;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(htmlText);
            songIds = ResolvePlaylistSongIds(doc, htmlText)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Take(limit * 2)
                .ToList();

            var hints = ExtractPlaylistTrackHints(doc);
            foreach (var (id, hint) in hints)
            {
                if (!songHints.ContainsKey(id))
                {
                    songHints[id] = hint;
                }
            }

            if (songIds.Count > 0)
            {
                break;
            }
        }

        if (songIds.Count == 0)
        {
            return Array.Empty<BoomplayTrackMetadata>();
        }

        var fetchIds = songHints.Count > 0
            ? songIds.Take(limit).ToList()
            : songIds;
        var fetchedTracks = await GetSongsAsync(fetchIds, cancellationToken);
        var tracks = MergeSearchTracks(songIds, fetchedTracks, songHints, limit);

        _searchCache.Set(cacheKey, tracks, BuildSearchCacheOptions());
        return tracks;
    }

    private static List<BoomplayTrackMetadata> MergeSearchTracks(
        List<string> orderedSongIds,
        IReadOnlyList<BoomplayTrackMetadata> fetchedTracks,
        IReadOnlyDictionary<string, BoomplayTrackHint> songHints,
        int limit)
    {
        var fetchedById = fetchedTracks
            .Where(static track => track != null && !string.IsNullOrWhiteSpace(track.Id))
            .GroupBy(static track => track.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var merged = new List<BoomplayTrackMetadata>(Math.Min(limit, orderedSongIds.Count));
        var addedIds = new HashSet<string>(StringComparer.Ordinal);
        AddOrderedMergedTracks(orderedSongIds, fetchedById, songHints, limit, merged, addedIds);
        AddFallbackFetchedTracks(fetchedTracks, limit, merged, addedIds);
        return merged;
    }

    private static void AddOrderedMergedTracks(
        IReadOnlyList<string> orderedSongIds,
        Dictionary<string, BoomplayTrackMetadata> fetchedById,
        IReadOnlyDictionary<string, BoomplayTrackHint> songHints,
        int limit,
        List<BoomplayTrackMetadata> merged,
        HashSet<string> addedIds)
    {
        foreach (var songId in orderedSongIds)
        {
            if (string.IsNullOrWhiteSpace(songId) || !addedIds.Add(songId))
            {
                continue;
            }

            if (TrySelectMergedTrack(songId, fetchedById, songHints, out var selected))
            {
                merged.Add(selected);
            }

            if (merged.Count >= limit)
            {
                return;
            }
        }
    }

    private static void AddFallbackFetchedTracks(
        IReadOnlyList<BoomplayTrackMetadata> fetchedTracks,
        int limit,
        List<BoomplayTrackMetadata> merged,
        HashSet<string> addedIds)
    {
        if (merged.Count >= limit)
        {
            return;
        }

        foreach (var track in fetchedTracks)
        {
            if (track == null || string.IsNullOrWhiteSpace(track.Id) || !addedIds.Add(track.Id))
            {
                continue;
            }

            merged.Add(track);
            if (merged.Count >= limit)
            {
                return;
            }
        }
    }

    private static bool TrySelectMergedTrack(
        string songId,
        Dictionary<string, BoomplayTrackMetadata> fetchedById,
        IReadOnlyDictionary<string, BoomplayTrackHint> songHints,
        out BoomplayTrackMetadata selected)
    {
        selected = null!;
        fetchedById.TryGetValue(songId, out var fetched);
        if (fetched != null && !IsLowConfidenceSongMetadata(fetched))
        {
            selected = fetched;
            return true;
        }

        if (songHints.TryGetValue(songId, out var hint)
            && TryBuildTrackFromHint(songId, hint, out var hintedTrack))
        {
            selected = hintedTrack;
            return true;
        }

        if (fetched != null)
        {
            selected = fetched;
            return true;
        }

        return false;
    }

    private static bool TryBuildTrackFromHint(string songId, BoomplayTrackHint hint, out BoomplayTrackMetadata track)
    {
        track = new BoomplayTrackMetadata();
        if (string.IsNullOrWhiteSpace(songId))
        {
            return false;
        }

        var title = DecodeAndTrim(hint.Title);
        var artist = DecodeAndTrim(hint.Artist);
        if (IsPlaceholderText(title) || IsPlaceholderText(artist))
        {
            return false;
        }

        track = new BoomplayTrackMetadata
        {
            Id = songId.Trim(),
            Url = $"{BoomplayBaseUrl}/songs/{songId.Trim()}",
            Title = title,
            Artist = artist,
            Album = DecodeAndTrim(hint.Album),
            CoverUrl = DecodeAndTrim(hint.CoverUrl)
        };

        SanitizeTrackMetadata(track);
        return !IsLowConfidenceSongMetadata(track);
    }

    private async Task<BoomplayTrackMetadata?> GetSongWithPolicyAsync(
        string songId,
        bool bypassCache,
        int maxAttempts,
        int streamTagAttempts,
        bool cacheLowConfidence,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(songId))
        {
            return null;
        }

        if (TryGetCachedSong(songId, bypassCache, out var cached))
        {
            return cached;
        }

        var url = $"{BoomplayBaseUrl}/songs/{songId}";
        BoomplayTrackMetadata? best = null;
        maxAttempts = Math.Max(1, maxAttempts);
        streamTagAttempts = Math.Max(1, streamTagAttempts);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var attemptContext = new SongAttemptContext(
                Attempt: attempt,
                MaxAttempts: maxAttempts,
                StreamTagAttempts: streamTagAttempts);
            var attemptOutcome = await EvaluateSongAttemptAsync(
                songId,
                url,
                best,
                attemptContext,
                cancellationToken);

            if (attemptOutcome.Completed)
            {
                return attemptOutcome.Value;
            }

            best = attemptOutcome.Best ?? best;
            if (attemptOutcome.Delay)
            {
                await DelayForRetryAsync(attempt, cancellationToken);
            }
        }

        CacheLowConfidenceBest(songId, best, cacheLowConfidence);
        return best;
    }

    private void CacheLowConfidenceBest(string songId, BoomplayTrackMetadata? best, bool cacheLowConfidence)
    {
        if (best != null && cacheLowConfidence)
        {
            _songCache.Set(songId, best, BuildSongCacheOptions());
        }
    }

    private bool TryGetCachedSong(string songId, bool bypassCache, out BoomplayTrackMetadata? cached)
    {
        if (!bypassCache && _songCache.TryGetValue(songId, out BoomplayTrackMetadata? cachedTrack) && cachedTrack != null)
        {
            cached = cachedTrack;
            return true;
        }

        cached = null;
        return false;
    }

    private bool TryCacheAndReturnHighConfidenceSong(
        string songId,
        BoomplayTrackMetadata parsed,
        out BoomplayTrackMetadata resolvedTrack)
    {
        if (IsLowConfidenceSongMetadata(parsed))
        {
            resolvedTrack = null!;
            return false;
        }

        _songCache.Set(songId, parsed, BuildSongCacheOptions());
        resolvedTrack = parsed;
        return true;
    }

    private async Task<SongAttemptParseResult> ParseSongAttemptAsync(
        string songId,
        string url,
        string html,
        int streamTagAttempts,
        CancellationToken cancellationToken)
    {
        var parsed = ParseSongHtml(songId, html, url);
        await ApplyStreamTagsAsync(songId, parsed, streamTagAttempts, cancellationToken);
        return new SongAttemptParseResult(parsed);
    }

    private async Task<SongAttemptOutcome> EvaluateSongAttemptAsync(
        string songId,
        string url,
        BoomplayTrackMetadata? best,
        SongAttemptContext context,
        CancellationToken cancellationToken)
    {
        var html = await GetHtmlAsync(url, cancellationToken);
        if (HandleEmptySongHtml(html, context.Attempt, context.MaxAttempts, best, out var emptyResolution))
        {
            return new SongAttemptOutcome(
                Completed: !emptyResolution.Delay,
                Value: emptyResolution.Value,
                Delay: emptyResolution.Delay,
                Best: best);
        }

        if (LooksLikeNotFoundSongPage(html))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Boomplay returned not-found page for song {SongId}", songId);
            }

            return new SongAttemptOutcome(
                Completed: true,
                Value: null,
                Delay: false,
                Best: best);
        }

        var parseResult = await ParseSongAttemptAsync(
            songId,
            url,
            html!,
            context.StreamTagAttempts,
            cancellationToken);
        var parsed = parseResult.Metadata;
        if (IsSongMetadataEmpty(parsed))
        {
            return new SongAttemptOutcome(
                Completed: false,
                Value: null,
                Delay: context.Attempt < context.MaxAttempts,
                Best: best);
        }

        if (TryCacheAndReturnHighConfidenceSong(songId, parsed, out var resolvedTrack))
        {
            return new SongAttemptOutcome(
                Completed: true,
                Value: resolvedTrack,
                Delay: false,
                Best: best);
        }

        return new SongAttemptOutcome(
            Completed: false,
            Value: null,
            Delay: context.Attempt < context.MaxAttempts,
            Best: PickBetterMetadata(best, parsed));
    }

    private static bool IsSongMetadataEmpty(BoomplayTrackMetadata metadata)
    {
        return string.IsNullOrWhiteSpace(metadata.Title)
               && string.IsNullOrWhiteSpace(metadata.Artist)
               && string.IsNullOrWhiteSpace(metadata.Album);
    }

    private sealed record SongAttemptOutcome(
        bool Completed,
        BoomplayTrackMetadata? Value,
        bool Delay,
        BoomplayTrackMetadata? Best);

    private readonly record struct SongAttemptContext(
        int Attempt,
        int MaxAttempts,
        int StreamTagAttempts);

    private static async Task DelayIfRetryAvailableAsync(int attempt, int maxAttempts, CancellationToken cancellationToken)
    {
        if (attempt < maxAttempts)
        {
            await DelayForRetryAsync(attempt, cancellationToken);
        }
    }

    private static bool HandleEmptySongHtml(
        string? html,
        int attempt,
        int maxAttempts,
        BoomplayTrackMetadata? best,
        out (bool Delay, BoomplayTrackMetadata? Value) resolved)
    {
        resolved = default;
        if (!string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        if (attempt < maxAttempts)
        {
            resolved = (true, null);
            return true;
        }

        resolved = (false, best);
        return true;
    }

    private async Task ApplyStreamTagsAsync(
        string songId,
        BoomplayTrackMetadata parsed,
        int streamTagAttempts,
        CancellationToken cancellationToken)
    {
        var streamTags = await TryReadSongTagsFromStreamAsync(songId, streamTagAttempts, cancellationToken);
        if (streamTags.Count > 0)
        {
            ApplyStreamTags(parsed, streamTags);
        }
    }

    private static bool LooksLikeNotFoundSongPage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        return html.Contains("Boomplay Music: Not Found", StringComparison.OrdinalIgnoreCase);
    }

    private static BoomplayTrackMetadata PickBetterMetadata(BoomplayTrackMetadata? current, BoomplayTrackMetadata candidate)
    {
        if (current == null)
        {
            return candidate;
        }

        var currentScore = ScoreMetadata(current);
        var candidateScore = ScoreMetadata(candidate);
        if (candidateScore > currentScore)
        {
            return candidate;
        }

        return current;
    }

    private static int ScoreMetadata(BoomplayTrackMetadata track)
    {
        var score = 0;
        if (!IsPlaceholderText(track.Title)) score += 3;
        if (!IsPlaceholderText(track.Artist)) score += 3;
        if (!IsPlaceholderText(track.Album)) score += 2;
        if (!string.IsNullOrWhiteSpace(track.Isrc)) score += 4;
        if (track.Genres.Count > 0) score += 1;
        return score;
    }

    public async Task<BoomplayPlaylistMetadata?> GetPlaylistAsync(
        string playlistId,
        bool includeTracks,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        if (_playlistCache.TryGetValue(playlistId, out BoomplayPlaylistMetadata? cached)
            && cached != null
            && (!includeTracks || cached.Tracks.Count > 0 || cached.TrackIds.Count == 0))
        {
            return cached;
        }

        var url = $"{BoomplayBaseUrl}/playlists/{playlistId}";
        var html = await GetHtmlAsync(url, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var parsed = ParsePlaylistHtml(playlistId, html, url);
        if (includeTracks && parsed.TrackIds.Count > 0)
        {
            var tracks = await GetSongsAsync(parsed.TrackIds, cancellationToken);
            parsed.Tracks = tracks.ToList();
        }

        _playlistCache.Set(playlistId, parsed, BuildPlaylistCacheOptions());
        return parsed;
    }

    public async Task<BoomplayPlaylistMetadata?> GetTrendingSongsAsync(
        bool includeTracks,
        CancellationToken cancellationToken)
    {
        return await GetCollectionByUrlAsync(
            collectionId: "trending-songs",
            url: $"{BoomplayBaseUrl}/trending-songs",
            includeTracks,
            cancellationToken);
    }

    public async Task<IReadOnlyList<BoomplayRecommendationSection>> GetPlaylistRecommendationsAsync(
        string playlistId,
        bool isTrending,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 48);

        BoomplayPlaylistMetadata? playlist = isTrending
            ? await GetTrendingSongsAsync(includeTracks: false, cancellationToken)
            : await GetPlaylistAsync(playlistId, includeTracks: false, cancellationToken);

        if (playlist != null && playlist.RecommendationSections.Count > 0)
        {
            return TrimRecommendationSections(playlist.RecommendationSections, limit);
        }

        var url = isTrending
            ? $"{BoomplayBaseUrl}/trending-songs"
            : $"{BoomplayBaseUrl}/playlists/{playlistId}";

        var html = await GetHtmlAsync(url, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return Array.Empty<BoomplayRecommendationSection>();
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var sections = ExtractPlaylistRecommendationSections(doc, html, isTrending ? "trending-songs" : playlistId);

        if (playlist != null)
        {
            playlist.RecommendationSections = sections;
        }

        return TrimRecommendationSections(sections, limit);
    }

    private async Task<BoomplayPlaylistMetadata?> GetCollectionByUrlAsync(
        string collectionId,
        string url,
        bool includeTracks,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return null;
        }

        if (_playlistCache.TryGetValue(collectionId, out BoomplayPlaylistMetadata? cached)
            && cached != null
            && (!includeTracks || cached.Tracks.Count > 0 || cached.TrackIds.Count == 0))
        {
            return cached;
        }

        var html = await GetHtmlAsync(url, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var parsed = ParsePlaylistHtml(collectionId, html, url);
        if (includeTracks && parsed.TrackIds.Count > 0)
        {
            var tracks = await GetSongsAsync(parsed.TrackIds, cancellationToken);
            parsed.Tracks = tracks.ToList();
        }

        _playlistCache.Set(collectionId, parsed, BuildPlaylistCacheOptions());
        return parsed;
    }

    private async Task<string> GetHtmlAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("accept-language", "en-US,en;q=0.9");
            request.Headers.Referrer = new Uri(BoomplayBaseUrl);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Boomplay request failed for Url");
            return string.Empty;
        }
    }

    private static BoomplayPlaylistMetadata ParsePlaylistHtml(string playlistId, string html, string url)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rawTitle = FirstNonEmpty(
            GetMetaContent(doc, "og:title", PropertyAttribute),
            GetMetaContent(doc, "twitter:title", "name"),
            doc.DocumentNode.SelectSingleNode("//title")?.InnerText);
        var title = CleanBoomplayTitle(rawTitle);

        var imageUrl = FirstNonEmpty(
            GetMetaContent(doc, "og:image", PropertyAttribute),
            GetMetaContent(doc, "twitter:image", "name"));
        var metaDescription = FirstNonEmpty(
            GetMetaContent(doc, "og:description", PropertyAttribute),
            GetMetaContent(doc, "description", "name"));
        var bodyDescription = ExtractPlaylistBodyDescription(doc);
        var description = CleanBoomplayPlaylistDescription(FirstNonEmpty(bodyDescription, metaDescription));

        var trackIds = ResolvePlaylistSongIds(doc, html);
        var trackHints = ExtractPlaylistTrackHints(doc);

        return new BoomplayPlaylistMetadata
        {
            Id = playlistId,
            Url = url,
            Title = title,
            Description = description,
            ImageUrl = imageUrl ?? string.Empty,
            TrackIds = trackIds,
            TrackHints = trackHints,
            RecommendationSections = ExtractPlaylistRecommendationSections(doc, html, playlistId)
        };
    }

    private static string ExtractPlaylistBodyDescription(HtmlDocument doc)
    {
        var node = doc.DocumentNode.SelectSingleNode(
            "//section[contains(@class,'text')]//div[contains(@class,'description')]//*[contains(@class,'description_content')]");
        if (node == null)
        {
            return string.Empty;
        }

        var html = node.InnerHtml ?? string.Empty;
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        html = HtmlBreakRegex.Replace(html, "\n");
        var stripped = HtmlTagRegex.Replace(html, " ");
        return stripped;
    }

    private static string CleanBoomplayPlaylistDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var cleaned = raw;
        // Some Boomplay payloads are double-encoded (&amp;quot;...), decode a few rounds.
        for (var i = 0; i < 3; i++)
        {
            var decoded = WebUtility.HtmlDecode(cleaned);
            if (string.Equals(decoded, cleaned, StringComparison.Ordinal))
            {
                break;
            }

            cleaned = decoded;
        }

        cleaned = BoomplayCoverLinePrefixRegex.Replace(cleaned, string.Empty);
        cleaned = cleaned
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        cleaned = MultiWhitespaceRegex.Replace(cleaned, " ").Trim();
        cleaned = BoomplayPromoDescriptionSuffixRegex.Replace(cleaned, string.Empty).Trim();

        return cleaned;
    }

    private static IReadOnlyList<BoomplayRecommendationSection> TrimRecommendationSections(
        List<BoomplayRecommendationSection> sections,
        int limit)
    {
        if (sections.Count == 0 || limit <= 0)
        {
            return Array.Empty<BoomplayRecommendationSection>();
        }

        var remaining = limit;
        var trimmed = new List<BoomplayRecommendationSection>();
        foreach (var section in sections)
        {
            if (remaining <= 0)
            {
                break;
            }

            var sectionItems = section.Items
                .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
                .Take(remaining)
                .ToList();

            if (sectionItems.Count == 0)
            {
                continue;
            }

            trimmed.Add(new BoomplayRecommendationSection
            {
                Title = NormalizeRecommendationText(section.Title) ?? RecommendedPlaylistsTitle,
                Items = sectionItems
            });
            remaining -= sectionItems.Count;
        }

        return trimmed;
    }

    private static List<BoomplayRecommendationSection> ExtractPlaylistRecommendationSections(
        HtmlDocument doc,
        string html,
        string currentPlaylistId)
    {
        var buckets = new Dictionary<string, List<BoomplayPlaylistRecommendation>>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        void AddRecommendation(string sectionTitle, BoomplayPlaylistRecommendation recommendation)
        {
            if (string.IsNullOrWhiteSpace(recommendation.Id) || !seenIds.Add(recommendation.Id))
            {
                return;
            }

            var key = NormalizeRecommendationText(sectionTitle) ?? RecommendedPlaylistsTitle;
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<BoomplayPlaylistRecommendation>();
                buckets[key] = list;
            }

            list.Add(recommendation);
        }

        foreach (var (sectionTitle, recommendation) in ExtractRecommendationsFromKnownSections(doc, currentPlaylistId))
        {
            AddRecommendation(sectionTitle, recommendation);
        }

        foreach (var (sectionTitle, recommendation) in ExtractRecommendationsFromAnchors(doc, currentPlaylistId))
        {
            AddRecommendation(sectionTitle, recommendation);
        }

        if (buckets.Count == 0)
        {
            foreach (var recommendation in ExtractRecommendationsFromEmbeddedJson(html, currentPlaylistId))
            {
                AddRecommendation(RecommendedPlaylistsTitle, recommendation);
            }
        }

        return buckets
            .Select(entry => new BoomplayRecommendationSection
            {
                Title = entry.Key,
                Items = entry.Value
            })
            .Where(static section => section.Items.Count > 0)
            .ToList();
    }

    private static IEnumerable<(string SectionTitle, BoomplayPlaylistRecommendation Recommendation)> ExtractRecommendationsFromKnownSections(
        HtmlDocument doc,
        string currentPlaylistId)
    {
        var sections = doc.DocumentNode.SelectNodes("//article[.//h1 or .//h2 or .//h3]");
        if (sections == null)
        {
            yield break;
        }

        foreach (var section in sections)
        {
            var heading = section.SelectSingleNode(".//h1|.//h2|.//h3");
            var headingText = NormalizeRecommendationText(heading?.InnerText);
            if (string.IsNullOrWhiteSpace(headingText))
            {
                continue;
            }

            var normalizedHeading = headingText.ToLowerInvariant();
            if (!normalizedHeading.Contains("you may also like", StringComparison.OrdinalIgnoreCase)
                && !normalizedHeading.Contains("recommended", StringComparison.OrdinalIgnoreCase)
                && !normalizedHeading.Contains("similar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var anchors = section.SelectNodes(".//a[contains(@href,'/playlists/')]");
            if (anchors == null)
            {
                continue;
            }

            foreach (var anchor in anchors)
            {
                var recommendation = ExtractPlaylistRecommendationFromAnchor(anchor, currentPlaylistId, includeStrongTitle: true);
                if (recommendation != null)
                {
                    yield return (headingText, recommendation);
                }
            }
        }
    }

    private static IEnumerable<(string SectionTitle, BoomplayPlaylistRecommendation Recommendation)> ExtractRecommendationsFromAnchors(
        HtmlDocument doc,
        string currentPlaylistId)
    {
        var anchors = doc.DocumentNode.SelectNodes("//a[contains(@href,'/playlists/')]");
        if (anchors == null)
        {
            yield break;
        }

        foreach (var anchor in anchors)
        {
            var recommendation = ExtractPlaylistRecommendationFromAnchor(anchor, currentPlaylistId, includeStrongTitle: false);
            if (recommendation == null)
            {
                continue;
            }
            var sectionTitle = ResolveRecommendationSectionTitle(anchor);

            yield return (sectionTitle, recommendation);
        }
    }

    private static BoomplayPlaylistRecommendation? ExtractPlaylistRecommendationFromAnchor(
        HtmlNode anchor,
        string currentPlaylistId,
        bool includeStrongTitle)
    {
        var href = anchor.GetAttributeValue("href", string.Empty);
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var playlistMatch = PlaylistPathRegex.Match(href);
        if (!playlistMatch.Success)
        {
            return null;
        }

        var playlistId = playlistMatch.Groups["id"].Value.Trim();
        if (string.IsNullOrWhiteSpace(playlistId)
            || playlistId.Equals(currentPlaylistId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var title = NormalizeRecommendationText(FirstNonEmpty(
                anchor.GetAttributeValue(TitleField, string.Empty),
                anchor.GetAttributeValue("aria-label", string.Empty),
                anchor.SelectSingleNode(".//*[@title]")?.GetAttributeValue(TitleField, string.Empty),
                includeStrongTitle ? anchor.SelectSingleNode(".//strong")?.InnerText : null,
                anchor.SelectSingleNode(".//*[contains(@class,'title') or contains(@class,'name')]")?.InnerText,
                anchor.SelectSingleNode(ImgXPath)?.GetAttributeValue("alt", string.Empty),
                anchor.InnerText))
            ?? $"Playlist {playlistId}";

        if (!IsMeaningfulRecommendationTitle(title))
        {
            return null;
        }

        var description = NormalizeRecommendationText(FirstNonEmpty(
            anchor.SelectSingleNode(".//*[contains(@class,'subtitle') or contains(@class,'desc') or contains(@class,'author')]")?.InnerText,
            anchor.GetAttributeValue("data-description", string.Empty)));

        return new BoomplayPlaylistRecommendation
        {
            Id = playlistId,
            Url = TryBuildAbsoluteBoomplayUrl(href),
            Name = title,
            Description = description ?? string.Empty,
            ImageUrl = TryExtractImageUrlFromNode(anchor)
        };
    }

    private static IEnumerable<BoomplayPlaylistRecommendation> ExtractRecommendationsFromEmbeddedJson(
        string html,
        string currentPlaylistId)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in EmbeddedPlaylistIdRegex.Matches(html))
        {
            var id = match.Groups["id"].Value.Trim();
            if (string.IsNullOrWhiteSpace(id)
                || id.Equals(currentPlaylistId, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(id))
            {
                continue;
            }

            var snippetStart = Math.Max(0, match.Index - 320);
            var snippetLength = Math.Min(1200, html.Length - snippetStart);
            var snippet = html.Substring(snippetStart, snippetLength);
            var name = NormalizeRecommendationText(EmbeddedPlaylistNameRegex.Match(snippet).Groups[ValueField].Value)
                ?? $"Playlist {id}";
            var image = TryBuildAbsoluteBoomplayUrl(EmbeddedPlaylistImageRegex.Match(snippet).Groups[ValueField].Value);

            if (!IsMeaningfulRecommendationTitle(name))
            {
                continue;
            }

            yield return new BoomplayPlaylistRecommendation
            {
                Id = id,
                Url = $"{BoomplayBaseUrl}/playlists/{id}",
                Name = name,
                Description = string.Empty,
                ImageUrl = image
            };
        }
    }

    private static string ResolveRecommendationSectionTitle(HtmlNode anchor)
    {
        HtmlNode? current = anchor;
        for (var depth = 0; depth < 6 && current != null; depth++)
        {
            var heading = current.SelectSingleNode("./h1|./h2|./h3|./h4|./header//*[self::h1 or self::h2 or self::h3 or self::h4]");
            var text = NormalizeRecommendationText(heading?.InnerText);
            if (IsMeaningfulRecommendationTitle(text))
            {
                return text!;
            }

            var siblingHeading = current.SelectSingleNode("./preceding-sibling::*[self::h1 or self::h2 or self::h3 or self::h4][1]");
            text = NormalizeRecommendationText(siblingHeading?.InnerText);
            if (IsMeaningfulRecommendationTitle(text))
            {
                return text!;
            }

            current = current.ParentNode;
        }

        return "Recommended Playlists";
    }

    private static string NormalizeRecommendationText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = WebUtility.HtmlDecode(value);
        normalized = MultiWhitespaceRegex.Replace(normalized, " ").Trim();
        return normalized;
    }

    private static bool IsMeaningfulRecommendationTitle(string? value)
    {
        var text = NormalizeRecommendationText(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Length > 140)
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        return lower != BoomplaySource
               && lower != "playlist"
               && lower != "playlists";
    }

    private static string TryBuildAbsoluteBoomplayUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = WebUtility.HtmlDecode(value.Trim());
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{trimmed}";
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (trimmed.StartsWith('/'))
        {
            return $"{BoomplayBaseUrl}{trimmed}";
        }

        return string.Empty;
    }

    private static string TryExtractImageUrlFromNode(HtmlNode node)
    {
        var imageNode = node.SelectSingleNode(ImgXPath);
        if (imageNode != null)
        {
            var src = FirstNonEmpty(
                imageNode.GetAttributeValue("data-src", string.Empty),
                imageNode.GetAttributeValue("data-original", string.Empty),
                imageNode.GetAttributeValue("data-lazy", string.Empty),
                imageNode.GetAttributeValue("src", string.Empty),
                ParseFirstSrcSetCandidate(imageNode.GetAttributeValue("srcset", string.Empty)));
            var normalizedSrc = TryBuildAbsoluteBoomplayUrl(src);
            if (!string.IsNullOrWhiteSpace(normalizedSrc))
            {
                return normalizedSrc;
            }
        }

        var directAttr = FirstNonEmpty(
            node.GetAttributeValue("data-src", string.Empty),
            node.GetAttributeValue("data-image", string.Empty),
            node.GetAttributeValue("data-cover", string.Empty),
            node.GetAttributeValue("data-bg", string.Empty),
            node.GetAttributeValue("data-background", string.Empty));
        var normalizedDirect = TryBuildAbsoluteBoomplayUrl(directAttr);
        if (!string.IsNullOrWhiteSpace(normalizedDirect))
        {
            return normalizedDirect;
        }

        var styleImage = ExtractImageUrlFromStyle(node.GetAttributeValue("style", string.Empty));
        if (!string.IsNullOrWhiteSpace(styleImage))
        {
            return styleImage;
        }

        var styledNodes = node.SelectNodes(".//*[contains(@style,'background-image') or contains(@style,'background')]");
        if (styledNodes != null)
        {
            foreach (var styledNode in styledNodes)
            {
                var styledImage = ExtractImageUrlFromStyle(styledNode.GetAttributeValue("style", string.Empty));
                if (!string.IsNullOrWhiteSpace(styledImage))
                {
                    return styledImage;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractImageUrlFromStyle(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(style);
        var match = CssUrlRegex.Match(decoded);
        if (!match.Success)
        {
            return string.Empty;
        }

        var rawUrl = match.Groups["url"].Value;
        return TryBuildAbsoluteBoomplayUrl(rawUrl);
    }

    private static string ParseFirstSrcSetCandidate(string? srcSet)
    {
        if (string.IsNullOrWhiteSpace(srcSet))
        {
            return string.Empty;
        }

        var first = srcSet
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.Empty;
        }

        var url = first.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return url ?? string.Empty;
    }

    private static List<string> ResolvePlaylistSongIds(HtmlDocument doc, string html)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var embeddedIds = ExtractSongIdsFromEmbeddedMeta(doc, html);
        foreach (var id in embeddedIds.Where(seen.Add))
        {
            ids.Add(id);
        }

        if (ids.Count > 0)
        {
            return ids;
        }

        foreach (var id in ExtractSongIdsFromAnchorLinks(doc).Where(seen.Add))
        {
            ids.Add(id);
        }

        if (ids.Count > 0)
        {
            return ids;
        }

        foreach (var id in SongIdInHtmlRegex
            .Matches(html)
            .Cast<Match>()
            .Select(match => match.Groups["id"].Value)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Where(seen.Add))
        {
            ids.Add(id);
        }

        return ids;
    }

    private static List<string> ExtractSongIdsFromEmbeddedMeta(HtmlDocument doc, string html)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var id = value.Trim();
            if (id.Length < 6 || !id.All(char.IsDigit))
            {
                return;
            }

            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        var scriptNodes = doc.DocumentNode.SelectNodes("//script");
        if (scriptNodes != null)
        {
            foreach (var scriptText in scriptNodes
                .Select(static script => script.InnerText)
                .Where(static scriptText => !string.IsNullOrWhiteSpace(scriptText)))
            {
                AddIdsFromRegex(EmbeddedMusicItemRegex, scriptText, AddId);
                AddIdsFromRegex(EmbeddedMusicItemReverseRegex, scriptText, AddId);
                AddIdsFromRegex(EmbeddedSongIdRegex, scriptText, AddId);
            }
        }

        var attrNodes = doc.DocumentNode.SelectNodes(
            "//*[@data-song-id or @data-track-id or @data-music-id or @itemid or @data-id]");
        if (attrNodes != null)
        {
            foreach (var node in attrNodes)
            {
                AddId(node.GetAttributeValue("data-song-id", string.Empty));
                AddId(node.GetAttributeValue("data-track-id", string.Empty));
                AddId(node.GetAttributeValue("data-music-id", string.Empty));
                AddId(node.GetAttributeValue("itemid", string.Empty));
                AddId(node.GetAttributeValue("data-id", string.Empty));
            }
        }

        if (ids.Count > 0)
        {
            return ids;
        }

        AddIdsFromRegex(EmbeddedMusicItemRegex, html, AddId);
        AddIdsFromRegex(EmbeddedMusicItemReverseRegex, html, AddId);
        AddIdsFromRegex(EmbeddedSongIdRegex, html, AddId);

        return ids;
    }

    private static void AddIdsFromRegex(Regex regex, string input, Action<string?> addId)
    {
        foreach (Match match in regex.Matches(input))
        {
            addId(match.Groups["id"].Value);
        }
    }

    private static List<string> ExtractSongIdsFromAnchorLinks(HtmlDocument doc)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var anchors = doc.DocumentNode.SelectNodes("//a[contains(@href,'/songs/')]");
        if (anchors == null)
        {
            return ids;
        }

        foreach (var id in anchors
            .Select(static anchor => anchor.GetAttributeValue("href", string.Empty))
            .Where(static href => !string.IsNullOrWhiteSpace(href))
            .Select(static href => SongPathRegex.Match(href).Groups["id"].Value)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Where(seen.Add))
        {
            ids.Add(id);
        }

        return ids;
    }

    private static Dictionary<string, BoomplayTrackHint> ExtractPlaylistTrackHints(HtmlDocument doc)
    {
        var hints = new Dictionary<string, BoomplayTrackHint>(StringComparer.Ordinal);
        var anchors = doc.DocumentNode.SelectNodes("//a[contains(@href,'/songs/') and contains(@class,'songName')]");
        if (anchors == null)
        {
            return hints;
        }

        foreach (var anchor in anchors)
        {
            var href = anchor.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var songMatch = SongPathRegex.Match(href);
            if (!songMatch.Success)
            {
                continue;
            }

            var songId = songMatch.Groups["id"].Value.Trim();
            if (string.IsNullOrWhiteSpace(songId) || hints.ContainsKey(songId))
            {
                continue;
            }

            var scope = anchor.Ancestors().FirstOrDefault(static node =>
                    node.Name.Equals("li", StringComparison.OrdinalIgnoreCase)
                    || node.Name.Equals("tr", StringComparison.OrdinalIgnoreCase)
                    || HasClassToken(node, "listItem")
                    || HasClassToken(node, "songInfo")
                    || HasClassToken(node, "searchSongsMenuWrap_list"))
                ?? anchor.ParentNode;

            var title = NormalizePlaylistHintText(anchor.InnerText);
            var artist = NormalizePlaylistHintText(scope?.SelectSingleNode(".//a[contains(@class,'artistName')]")?.InnerText);
            var album = NormalizePlaylistHintText(scope?.SelectSingleNode(".//a[contains(@class,'albumName')]")?.InnerText);
            var imageNode = scope?.SelectSingleNode(ImgXPath);
            var cover = TryBuildAbsoluteBoomplayUrl(FirstNonEmpty(
                imageNode?.GetAttributeValue("data-src", string.Empty),
                imageNode?.GetAttributeValue("data-original", string.Empty),
                imageNode?.GetAttributeValue("data-lazy", string.Empty),
                imageNode?.GetAttributeValue("src", string.Empty),
                ParseFirstSrcSetCandidate(imageNode?.GetAttributeValue("srcset", string.Empty))));

            if (string.IsNullOrWhiteSpace(title)
                && string.IsNullOrWhiteSpace(artist)
                && string.IsNullOrWhiteSpace(album))
            {
                continue;
            }

            hints[songId] = new BoomplayTrackHint
            {
                Title = title,
                Artist = artist,
                Album = album,
                CoverUrl = cover
            };
        }

        return hints;
    }

    private static bool HasClassToken(HtmlNode node, string token)
    {
        if (node == null || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var classValue = node.GetAttributeValue("class", string.Empty);
        if (string.IsNullOrWhiteSpace(classValue))
        {
            return false;
        }

        return classValue
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(token, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePlaylistHintText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = WebUtility.HtmlDecode(value).Trim();
        normalized = MultiWhitespaceRegex.Replace(normalized, " ").Trim();
        return normalized;
    }

    private static BoomplayTrackMetadata ParseSongHtml(string songId, string html, string url)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = CleanBoomplayTitle(FirstNonEmpty(
            GetMetaContent(doc, "og:title", PropertyAttribute),
            GetMetaContent(doc, "twitter:title", "name"),
            doc.DocumentNode.SelectSingleNode("//title")?.InnerText));
        var description = FirstNonEmpty(
            GetMetaContent(doc, "og:description", PropertyAttribute),
            GetMetaContent(doc, "description", "name"));
        var imageUrl = FirstNonEmpty(
            GetMetaContent(doc, "og:image", PropertyAttribute),
            GetMetaContent(doc, "twitter:image", "name"));

        var track = new BoomplayTrackMetadata
        {
            Id = songId,
            Url = url,
            Title = title ?? string.Empty,
            CoverUrl = imageUrl ?? string.Empty
        };

        ParseSongJsonLd(doc, track);
        ApplySongDetailMetadata(doc, track);
        ApplyEmbeddedGenreMetadata(html, songId, track);

        if (string.IsNullOrWhiteSpace(track.Artist))
        {
            track.Artist = TryExtractFromDescription(description, ArtistDescriptionMarkers) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(track.Isrc))
        {
            var regexIsrc = JsonIsrcRegex.Match(html);
            if (regexIsrc.Success)
            {
                track.Isrc = regexIsrc.Groups["value"].Value.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(track.Title))
        {
            track.Title = GuessTitleFromDescription(description) ?? string.Empty;
        }

        SanitizeTrackMetadata(track);
        return track;
    }

    private async Task<Dictionary<string, string>> TryReadSongTagsFromStreamAsync(
        string songId,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var normalizedAttempts = Math.Max(1, maxAttempts);
        for (var attempt = 1; attempt <= normalizedAttempts; attempt++)
        {
            try
            {
                var client = CreateClient();
                var (retryMediaUrl, mediaUrl) = await TryResolveMediaUrlForTagAttemptAsync(client, songId, cancellationToken);
                if (retryMediaUrl)
                {
                    await DelayIfRetryAvailableAsync(attempt, normalizedAttempts, cancellationToken);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(mediaUrl))
                {
                    return CreateEmptyTagDictionary();
                }

                var (retryProbe, bytes) = await TryReadTagProbeBytesAsync(client, mediaUrl, cancellationToken);
                if (retryProbe)
                {
                    await DelayIfRetryAvailableAsync(attempt, normalizedAttempts, cancellationToken);
                    continue;
                }
                if (bytes.Length == 0)
                {
                    return CreateEmptyTagDictionary();
                }

                bytes = await ExpandTagProbeBytesIfNeededAsync(client, mediaUrl, bytes, cancellationToken);
                var parsed = ParseId3v2TextFrames(bytes);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Boomplay stream-tag fallback failed for SongId (attempt Attempt)");
            }

            await DelayIfRetryAvailableAsync(attempt, normalizedAttempts, cancellationToken);
        }

        return CreateEmptyTagDictionary();
    }

    private static async Task<(bool Retry, string? MediaUrl)> TryResolveMediaUrlForTagAttemptAsync(
        HttpClient client,
        string songId,
        CancellationToken cancellationToken)
    {
        using var request = CreateResourceAddressRequest(songId);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        var mediaUrl = await ResolveSongStreamUrlAsync(response, ["source"], cancellationToken);
        return string.IsNullOrWhiteSpace(mediaUrl) ? (true, null) : (false, mediaUrl);
    }

    private static async Task<(bool Retry, byte[] Bytes)> TryReadTagProbeBytesAsync(
        HttpClient client,
        string mediaUrl,
        CancellationToken cancellationToken)
    {
        using var mediaRequest = BuildTagRangeRequest(mediaUrl, StreamTagProbeBytes - 1);
        using var mediaResponse = await client.SendAsync(mediaRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!mediaResponse.IsSuccessStatusCode)
        {
            return (true, Array.Empty<byte>());
        }

        var bytes = await ReadLimitedBytesAsync(mediaResponse.Content, StreamTagProbeBytes, cancellationToken);
        return bytes.Length == 0 ? (true, Array.Empty<byte>()) : (false, bytes);
    }

    private static async Task<byte[]> ExpandTagProbeBytesIfNeededAsync(
        HttpClient client,
        string mediaUrl,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (!HasId3Header(bytes))
        {
            return bytes;
        }

        var tagSize = DecodeSynchSafeInt(bytes, 6);
        if (tagSize <= 0)
        {
            return bytes;
        }

        var fullTagLength = Math.Min(StreamTagMaxBytes, 10 + tagSize);
        if (fullTagLength <= bytes.Length)
        {
            return bytes;
        }

        using var tagRequest = BuildTagRangeRequest(mediaUrl, fullTagLength - 1);
        using var tagResponse = await client.SendAsync(tagRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!tagResponse.IsSuccessStatusCode)
        {
            return bytes;
        }

        var expandedBytes = await ReadLimitedBytesAsync(tagResponse.Content, fullTagLength, cancellationToken);
        return expandedBytes.Length > bytes.Length ? expandedBytes : bytes;
    }

    private static HttpRequestMessage BuildTagRangeRequest(string mediaUrl, int rangeEnd)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
        request.Headers.TryAddWithoutValidation("x-boomplay-ref", "Boomplay_WEBV1");
        request.Headers.Range = new RangeHeaderValue(0, rangeEnd);
        return request;
    }

    private static Dictionary<string, string> CreateEmptyTagDictionary()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string?> ResolveSongStreamUrlOnceAsync(string songId, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        using var request = CreateResourceAddressRequest(songId);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await ResolveSongStreamUrlAsync(response, ["source", "resources"], cancellationToken);
    }

    private static HttpRequestMessage CreateResourceAddressRequest(string songId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            itemID = songId,
            itemType = "MUSIC"
        });
        var encryptedPayload = EncryptAesCbcBase64(payload, ResourceAesKey, ResourceAesIv);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{BoomplayBaseUrl}/getResourceAddr")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["param"] = encryptedPayload
            })
        };
        request.Headers.Referrer = new Uri(BoomplayBaseUrl);
        request.Headers.TryAddWithoutValidation("accept", "application/json, text/plain, */*");
        return request;
    }

    private static async Task<string?> ResolveSongStreamUrlAsync(
        HttpResponseMessage response,
        string[] candidateKeys,
        CancellationToken cancellationToken)
    {
        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var responseDoc = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        foreach (var key in candidateKeys)
        {
            if (!responseDoc.RootElement.TryGetProperty(key, out var sourceElement))
            {
                continue;
            }

            var encryptedSource = sourceElement.GetString();
            if (string.IsNullOrWhiteSpace(encryptedSource))
            {
                continue;
            }

            var mediaUrl = DecryptAesCbcBase64(encryptedSource, ResourceAesKey, ResourceAesIv);
            if (!string.IsNullOrWhiteSpace(mediaUrl))
            {
                return mediaUrl.Trim();
            }
        }

        return null;
    }

    private static void ApplyStreamTags(BoomplayTrackMetadata track, Dictionary<string, string> tags)
    {
        var appliedAnyCore = false;
        appliedAnyCore |= TryApplyTitleTag(track, tags);
        appliedAnyCore |= TryApplyArtistTag(track, tags);
        ApplyFillOnlyTag(track, tags, "TALB", static item => item.Album, static (item, value) => item.Album = value);
        appliedAnyCore |= TryApplyIsrcTag(track, tags);
        TryApplyGenreTag(track, tags);
        TryApplyNumericTag(tags, "TRCK", track.TrackNumber, static (item, value) => item.TrackNumber = value, track);
        TryApplyReleaseDateTag(track, tags);
        ApplyFillOnlyTag(track, tags, "TPE2", static item => item.AlbumArtist, static (item, value) => item.AlbumArtist = value);
        ApplyFillOnlyTag(track, tags, "TCOM", static item => item.Composer, static (item, value) => item.Composer = value);
        ApplyFillOnlyTag(track, tags, "TPUB", static item => item.Publisher, static (item, value) => item.Publisher = value);
        TryApplyNumericTag(tags, "TPOS", track.DiscNumber, static (item, value) => item.DiscNumber = value, track);
        TryApplyNumericTag(tags, "TBPM", track.Bpm, static (item, value) => item.Bpm = value, track);
        ApplyFillOnlyTag(track, tags, "TKEY", static item => item.Key, static (item, value) => item.Key = value);
        ApplyFillOnlyTag(track, tags, "TLAN", static item => item.Language, static (item, value) => item.Language = value);

        if (appliedAnyCore)
        {
            track.HasStreamTagMetadata = true;
        }

        SanitizeTrackMetadata(track);
    }

    private static bool TryApplyTitleTag(BoomplayTrackMetadata track, Dictionary<string, string> tags)
    {
        if (!tags.TryGetValue("TIT2", out var streamTitle) || string.IsNullOrWhiteSpace(streamTitle))
        {
            return false;
        }

        var htmlTitleBad = string.IsNullOrWhiteSpace(track.Title)
            || IsPlaceholderText(track.Title)
            || LooksLikeDecoratedTitle(track.Title, track.Artist)
            || ContainsNoiseSuffix(track.Title);
        var streamTitleCleaner = !string.IsNullOrWhiteSpace(track.Title) && streamTitle.Length < track.Title.Length;
        if (!htmlTitleBad && !streamTitleCleaner)
        {
            return false;
        }

        track.Title = streamTitle;
        return true;
    }

    private static bool TryApplyArtistTag(BoomplayTrackMetadata track, Dictionary<string, string> tags)
    {
        if (!tags.TryGetValue("TPE1", out var streamArtist) || string.IsNullOrWhiteSpace(streamArtist))
        {
            return false;
        }

        var htmlArtistBad = string.IsNullOrWhiteSpace(track.Artist) || IsPlaceholderText(track.Artist);
        var streamArtistCleaner = !string.IsNullOrWhiteSpace(track.Artist) && streamArtist.Length < track.Artist.Length;
        if (!htmlArtistBad && !streamArtistCleaner)
        {
            return false;
        }

        track.Artist = streamArtist;
        return true;
    }

    private static bool TryApplyIsrcTag(BoomplayTrackMetadata track, Dictionary<string, string> tags)
    {
        if (!tags.TryGetValue("TSRC", out var isrc) || string.IsNullOrWhiteSpace(isrc))
        {
            return false;
        }

        track.Isrc = isrc;
        return true;
    }

    private static void TryApplyGenreTag(BoomplayTrackMetadata track, Dictionary<string, string> tags)
    {
        if (!tags.TryGetValue("TCON", out var genre) || string.IsNullOrWhiteSpace(genre))
        {
            return;
        }

        track.Genres.Clear();
        AddGenre(track, genre);
        track.HasStreamGenreMetadata = true;
    }

    private static void TryApplyReleaseDateTag(BoomplayTrackMetadata track, Dictionary<string, string> tags)
    {
        if (!string.IsNullOrWhiteSpace(track.ReleaseDate))
        {
            return;
        }

        if (tags.TryGetValue("TDRC", out var date) || tags.TryGetValue("TYER", out date))
        {
            track.ReleaseDate = date;
        }
    }

    private static void ApplyFillOnlyTag(
        BoomplayTrackMetadata track,
        Dictionary<string, string> tags,
        string tagName,
        Func<BoomplayTrackMetadata, string> getter,
        Action<BoomplayTrackMetadata, string> setter)
    {
        if (!string.IsNullOrWhiteSpace(getter(track)) && !IsPlaceholderText(getter(track)))
        {
            return;
        }

        if (tags.TryGetValue(tagName, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            setter(track, value);
        }
    }

    private static void TryApplyNumericTag(
        Dictionary<string, string> tags,
        string tagName,
        int currentValue,
        Action<BoomplayTrackMetadata, int> setter,
        BoomplayTrackMetadata track)
    {
        if (currentValue > 0 || !tags.TryGetValue(tagName, out var rawValue))
        {
            return;
        }

        var parsed = ParseIntegerPrefix(rawValue);
        if (parsed > 0)
        {
            setter(track, parsed);
        }
    }

    private static bool ContainsNoiseSuffix(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return BoomplayNoiseSuffixRegex.IsMatch(title);
    }

    private static bool IsLowConfidenceSongMetadata(BoomplayTrackMetadata track)
    {
        if (IsPlaceholderText(track.Title) || IsPlaceholderText(track.Artist))
        {
            return true;
        }

        if (LooksLikeRawStreamTitle(track.Title))
        {
            return true;
        }

        if (LooksLikeDecoratedTitle(track.Title, track.Artist))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeRawStreamTitle(string? title)
    {
        var cleaned = DecodeAndTrim(title);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false;
        }

        return StreamTrackNumberPrefixRegex.IsMatch(cleaned)
               || StreamNumericPrefixRegex.IsMatch(cleaned)
               || StreamMasterSuffixRegex.IsMatch(cleaned)
               || cleaned.Contains(" (master", StringComparison.OrdinalIgnoreCase)
               || cleaned.Contains(" master)", StringComparison.OrdinalIgnoreCase);
    }

    private static void ParseSongJsonLd(HtmlDocument doc, BoomplayTrackMetadata target)
    {
        var scripts = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (scripts == null || scripts.Count == 0)
        {
            return;
        }

        foreach (var scriptText in scripts
                     .Select(static script => script.InnerText)
                     .Where(static text => !string.IsNullOrWhiteSpace(text)))
        {
            try
            {
                using var jsonDoc = JsonDocument.Parse(scriptText);
                ParseSongJsonElement(jsonDoc.RootElement, target);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ignore malformed JSON-LD payloads.
            }
        }
    }

    private static void ParseSongJsonElement(JsonElement element, BoomplayTrackMetadata target)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    if (LooksLikeSongObject(element))
                    {
                        target.Title = FirstNonEmpty(
                            target.Title,
                            GetJsonString(element, "name"),
                            GetJsonString(element, TitleField)) ?? string.Empty;
                        target.Artist = FirstNonEmpty(
                            target.Artist,
                            ReadArtistFromJson(element)) ?? string.Empty;
                        target.Album = FirstNonEmpty(
                            target.Album,
                            ReadAlbumFromJson(element)) ?? string.Empty;
                        target.Isrc = FirstNonEmpty(
                            target.Isrc,
                            GetJsonString(element, "isrc"),
                            GetJsonString(element, "isrcCode")) ?? string.Empty;
                        target.ReleaseDate = FirstNonEmpty(
                            target.ReleaseDate,
                            GetJsonString(element, "datePublished"),
                            GetJsonString(element, "releaseDate")) ?? string.Empty;

                        var genre = FirstNonEmpty(
                            GetJsonString(element, GenreField),
                            GetJsonArrayValue(element, GenreField));
                        AddGenre(target, genre);

                        var duration = FirstNonEmpty(
                            GetJsonString(element, "duration"),
                            GetJsonString(element, "durationIso8601"));
                        if (target.DurationMs <= 0)
                        {
                            target.DurationMs = ParseDurationMs(duration);
                        }

                        target.Publisher = FirstNonEmpty(
                            target.Publisher,
                            ReadNamedEntityFromJson(element, "publisher"),
                            ReadNamedEntityFromJson(element, "recordLabel"),
                            GetJsonString(element, "publisher"),
                            GetJsonString(element, "recordLabel")) ?? string.Empty;

                        target.Composer = FirstNonEmpty(
                            target.Composer,
                            ReadNamedEntityFromJson(element, "composer"),
                            GetJsonString(element, "composer")) ?? string.Empty;

                        target.Language = FirstNonEmpty(
                            target.Language,
                            GetJsonString(element, "inLanguage"),
                            GetJsonString(element, "language")) ?? string.Empty;
                    }

                    foreach (var property in element.EnumerateObject())
                    {
                        ParseSongJsonElement(property.Value, target);
                    }

                    break;
                }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ParseSongJsonElement(item, target);
                }
                break;
        }
    }

    private static bool LooksLikeSongObject(JsonElement element)
    {
        var type = GetJsonString(element, "@type");
        if (string.IsNullOrWhiteSpace(type))
        {
            type = GetJsonArrayValue(element, "@type");
        }

        if (!string.IsNullOrWhiteSpace(type) &&
            (type.Contains("MusicRecording", StringComparison.OrdinalIgnoreCase)
             || type.Contains("Song", StringComparison.OrdinalIgnoreCase)
             || type.Contains("Track", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return element.TryGetProperty("isrc", out _)
               || element.TryGetProperty("byArtist", out _)
               || element.TryGetProperty("inAlbum", out _);
    }

    private static string? ReadArtistFromJson(JsonElement element)
    {
        if (!element.TryGetProperty("byArtist", out var byArtist)
            && !element.TryGetProperty("artist", out byArtist))
        {
            return null;
        }

        if (byArtist.ValueKind == JsonValueKind.String)
        {
            return byArtist.GetString();
        }

        if (byArtist.ValueKind == JsonValueKind.Object)
        {
            return FirstNonEmpty(
                GetJsonString(byArtist, "name"),
                GetJsonString(byArtist, TitleField));
        }

        if (byArtist.ValueKind == JsonValueKind.Array)
        {
            var firstArtist = byArtist
                .EnumerateArray()
                .Select(static item => item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object => FirstNonEmpty(
                        GetJsonString(item, "name"),
                        GetJsonString(item, TitleField)),
                    _ => null
                })
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(firstArtist))
            {
                return firstArtist;
            }
        }

        return null;
    }

    private static string? ReadAlbumFromJson(JsonElement element)
    {
        if (!element.TryGetProperty("inAlbum", out var inAlbum)
            && !element.TryGetProperty("album", out inAlbum))
        {
            return null;
        }

        return inAlbum.ValueKind switch
        {
            JsonValueKind.String => inAlbum.GetString(),
            JsonValueKind.Object => FirstNonEmpty(
                GetJsonString(inAlbum, "name"),
                GetJsonString(inAlbum, TitleField)),
            _ => null
        };
    }

    private static string? ReadNamedEntityFromJson(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var entity))
        {
            return null;
        }

        return entity.ValueKind switch
        {
            JsonValueKind.String => entity.GetString(),
            JsonValueKind.Object => FirstNonEmpty(
                GetJsonString(entity, "name"),
                GetJsonString(entity, TitleField)),
            _ => null
        };
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? GetJsonArrayValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .FirstOrDefault(static raw => !string.IsNullOrWhiteSpace(raw));
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var remaining = Math.Max(1, maxBytes);
        var buffer = new byte[16 * 1024];
        while (remaining > 0)
        {
            var readSize = Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, readSize), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }

        return output.ToArray();
    }

    private static Dictionary<string, string> ParseId3v2TextFrames(byte[] bytes)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!HasId3Header(bytes))
        {
            return output;
        }

        var version = bytes[3];
        var tagSize = DecodeSynchSafeInt(bytes, 6);
        var end = Math.Min(bytes.Length, 10 + tagSize);
        var offset = 10;

        while (TryReadFrame(bytes, version, end, offset, out var frame))
        {
            AddDecodedTextFrame(output, frame.FrameId, bytes, frame.FrameOffset, frame.FrameSize);
            offset = frame.FrameOffset + frame.FrameSize;
        }

        return output;
    }

    private static bool HasId3Header(byte[] bytes)
    {
        return bytes.Length >= 10
               && bytes[0] == (byte)'I'
               && bytes[1] == (byte)'D'
               && bytes[2] == (byte)'3';
    }

    private static bool TryReadFrame(
        byte[] bytes,
        byte version,
        int end,
        int offset,
        out Id3Frame frame)
    {
        frame = default;
        if (offset + 10 > end)
        {
            return false;
        }

        var frameId = Encoding.ASCII.GetString(bytes, offset, 4);
        if (string.IsNullOrWhiteSpace(frameId) || frameId.All(ch => ch == '\0'))
        {
            return false;
        }

        var frameSize = version == 4
            ? DecodeSynchSafeInt(bytes, offset + 4)
            : DecodeBigEndianInt(bytes, offset + 4);
        if (frameSize <= 0 || offset + 10 + frameSize > end)
        {
            return false;
        }

        frame = new Id3Frame(frameId, offset + 10, frameSize);
        return true;
    }

    private static void AddDecodedTextFrame(
        Dictionary<string, string> output,
        string frameId,
        byte[] bytes,
        int frameOffset,
        int frameSize)
    {
        if (!frameId.StartsWith('T') || frameId == "TXXX")
        {
            return;
        }

        var decoded = DecodeTextFrame(bytes, frameOffset, frameSize);
        if (!string.IsNullOrWhiteSpace(decoded))
        {
            output[frameId] = decoded.Trim();
        }
    }

    private static int DecodeSynchSafeInt(byte[] bytes, int offset)
    {
        if (offset + 3 >= bytes.Length)
        {
            return 0;
        }

        return (bytes[offset] & 0x7F) << 21
               | (bytes[offset + 1] & 0x7F) << 14
               | (bytes[offset + 2] & 0x7F) << 7
               | (bytes[offset + 3] & 0x7F);
    }

    private static int DecodeBigEndianInt(byte[] bytes, int offset)
    {
        if (offset + 3 >= bytes.Length)
        {
            return 0;
        }

        return (bytes[offset] << 24)
               | (bytes[offset + 1] << 16)
               | (bytes[offset + 2] << 8)
               | bytes[offset + 3];
    }

    private static string? DecodeTextFrame(byte[] bytes, int offset, int size)
    {
        if (size < 1 || offset + size > bytes.Length)
        {
            return null;
        }

        var encodingByte = bytes[offset];
        var payload = bytes.AsSpan(offset + 1, size - 1).ToArray();

        string raw = encodingByte switch
        {
            0 => Encoding.Latin1.GetString(payload),
            1 => DecodeUtf16(payload),
            2 => Encoding.BigEndianUnicode.GetString(payload),
            3 => Encoding.UTF8.GetString(payload),
            _ => Encoding.UTF8.GetString(payload)
        };

        var nullIndex = raw.IndexOf('\0');
        if (nullIndex >= 0)
        {
            raw = raw[..nullIndex];
        }

        return raw.Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    private static string DecodeUtf16(byte[] bytes)
    {
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
        }

        return Encoding.Unicode.GetString(bytes);
    }

    private static string EncryptAesCbcBase64(string plaintext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        return Convert.ToBase64String(encrypted);
    }

    private static string DecryptAesCbcBase64(string encryptedBase64, byte[] key, byte[] iv)
    {
        try
        {
            var encryptedBytes = Convert.FromBase64String(encryptedBase64);
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static int ParseDurationMs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        if (TryParseMilliseconds(raw, out var millis))
        {
            return millis;
        }

        if (TryParseTimeSpanMilliseconds(raw, out var timeSpanMillis))
        {
            return timeSpanMillis;
        }

        return TryParseIso8601DurationMilliseconds(raw, out var isoMillis) ? isoMillis : 0;
    }

    private static bool TryParseMilliseconds(string raw, out int milliseconds)
    {
        milliseconds = 0;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        milliseconds = parsed > 0 ? parsed : 0;
        return true;
    }

    private static bool TryParseTimeSpanMilliseconds(string raw, out int milliseconds)
    {
        milliseconds = 0;
        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        milliseconds = (int)Math.Max(parsed.TotalMilliseconds, 0);
        return true;
    }

    private static bool TryParseIso8601DurationMilliseconds(string raw, out int milliseconds)
    {
        milliseconds = 0;
        if (!raw.StartsWith("PT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var seconds = ParseIso8601DurationSeconds(raw[2..].ToUpperInvariant());
        if (seconds <= 0)
        {
            return false;
        }

        milliseconds = (int)Math.Round(seconds * 1000d);
        return true;
    }

    private static double ParseIso8601DurationSeconds(string remaining)
    {
        var seconds = 0d;
        var number = new StringBuilder();
        foreach (var ch in remaining)
        {
            if (char.IsDigit(ch) || ch == '.')
            {
                number.Append(ch);
                continue;
            }

            if (number.Length == 0)
            {
                continue;
            }

            if (!double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                number.Clear();
                continue;
            }

            seconds += ch switch
            {
                'H' => value * 3600d,
                'M' => value * 60d,
                'S' => value,
                _ => 0d
            };
            number.Clear();
        }

        return seconds;
    }

    private static int ParseIntegerPrefix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        var value = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static string? GetMetaContent(HtmlDocument doc, string key, string attributeType)
    {
        var node = doc.DocumentNode.SelectSingleNode($"//meta[translate(@{attributeType},'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='{key.ToLowerInvariant()}']");
        var content = node?.GetAttributeValue("content", string.Empty);
        return string.IsNullOrWhiteSpace(content) ? null : WebUtility.HtmlDecode(content.Trim());
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?.Trim();
    }

    private static string CleanBoomplayTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var title = WebUtility.HtmlDecode(value).Trim();
        var separators = new[] { " | ", " - Boomplay", "_ Boomplay", " | Boomplay" };
        foreach (var separator in separators)
        {
            var idx = title.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                title = title[..idx].Trim();
            }
        }

        return title;
    }

    private static void SanitizeTrackMetadata(BoomplayTrackMetadata track)
    {
        track.Artist = DecodeAndTrim(track.Artist);
        track.Album = DecodeAndTrim(track.Album);
        track.CoverUrl = DecodeAndTrim(track.CoverUrl);
        track.Isrc = DecodeAndTrim(track.Isrc).ToUpperInvariant();
        track.ReleaseDate = DecodeAndTrim(track.ReleaseDate);
        track.Title = NormalizeBoomplayTrackTitle(track.Title, track.Artist, track.HasStreamTagMetadata);
    }

    private static string DecodeAndTrim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return WebUtility.HtmlDecode(value).Trim();
    }

    private static string NormalizeBoomplayTrackTitle(string? title, string? artist, bool aggressiveStreamCleanup)
    {
        var cleaned = DecodeAndTrim(title);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        if (aggressiveStreamCleanup)
        {
            cleaned = MultiUnderscoreRegex.Replace(cleaned, " ").Trim();
            cleaned = StreamTrackNumberPrefixRegex.Replace(cleaned, string.Empty).Trim();
            cleaned = StreamNumericPrefixRegex.Replace(cleaned, string.Empty).Trim();
        }

        cleaned = BoomplayNoiseSuffixRegex.Replace(cleaned, string.Empty).Trim();
        cleaned = cleaned.TrimEnd('-', '|', ':', '&').Trim();

        var artistName = DecodeAndTrim(artist);
        if (!string.IsNullOrWhiteSpace(artistName))
        {
            if (aggressiveStreamCleanup)
            {
                cleaned = StripLeadingArtistPrefix(cleaned, artistName);
            }

            if (cleaned.StartsWith(artistName + " - ", StringComparison.OrdinalIgnoreCase)
                || cleaned.StartsWith(artistName + " – ", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[(artistName.Length + 3)..].Trim();
            }
        }

        cleaned = BoomplayNoiseSuffixRegex.Replace(cleaned, string.Empty).Trim();
        if (aggressiveStreamCleanup)
        {
            cleaned = StreamMasterSuffixRegex.Replace(cleaned, string.Empty).Trim();
        }

        cleaned = ProdCreditRegex.Replace(cleaned, string.Empty).Trim();
        cleaned = StandaloneYearRegex.Replace(cleaned, string.Empty).Trim();
        cleaned = FeaturingNormalizeRegex.Replace(cleaned, " feat. ");
        cleaned = cleaned.TrimEnd('-', '|', ':', '&').Trim();
        return cleaned;
    }

    private static string StripLeadingArtistPrefix(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return title;
        }

        var match = LeadingArtistDashRegex.Match(title);
        if (!match.Success)
        {
            return title;
        }

        var prefixArtist = match.Groups["artist"].Value;
        if (!LooksLikeArtistAlias(prefixArtist, artist))
        {
            return title;
        }

        var stripped = match.Groups["title"].Value.Trim();
        return string.IsNullOrWhiteSpace(stripped) ? title : stripped;
    }

    private static bool LooksLikeArtistAlias(string prefixArtist, string trackArtist)
    {
        var normalizedPrefix = NormalizeArtistComparable(prefixArtist);
        var normalizedTrackArtist = NormalizeArtistComparable(trackArtist);
        if (string.IsNullOrWhiteSpace(normalizedPrefix) || string.IsNullOrWhiteSpace(normalizedTrackArtist))
        {
            return false;
        }

        return normalizedPrefix.Equals(normalizedTrackArtist, StringComparison.Ordinal)
               || normalizedPrefix.StartsWith(normalizedTrackArtist + " ", StringComparison.Ordinal)
               || normalizedTrackArtist.StartsWith(normalizedPrefix + " ", StringComparison.Ordinal);
    }

    private static string NormalizeArtistComparable(string? value)
    {
        var cleaned = DecodeAndTrim(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        cleaned = ArtistFeaturingTailRegex.Replace(cleaned, string.Empty);
        cleaned = NonWordSeparatorRegex.Replace(cleaned, " ").Trim();
        cleaned = MultiWhitespaceRegex.Replace(cleaned, " ").Trim();
        return cleaned.ToLowerInvariant();
    }

    private static bool IsPlaceholderText(string? value)
    {
        var cleaned = DecodeAndTrim(value).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return true;
        }

        return cleaned == "unknown"
               || cleaned == "boomplay music"
               || cleaned == BoomplaySource
               || cleaned.StartsWith("unknown ", StringComparison.Ordinal);
    }

    private static bool LooksLikeDecoratedTitle(string? title, string? artist)
    {
        var cleaned = DecodeAndTrim(title);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false;
        }

        if (BoomplayNoiseSuffixRegex.IsMatch(cleaned))
        {
            return true;
        }

        var artistName = DecodeAndTrim(artist);
        return !string.IsNullOrWhiteSpace(artistName)
               && (cleaned.StartsWith(artistName + " - ", StringComparison.OrdinalIgnoreCase)
                   || cleaned.StartsWith(artistName + " – ", StringComparison.OrdinalIgnoreCase))
               && cleaned.Contains("mp3", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GuessTitleFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var cleaned = description.Trim();
        var markers = new[] { " by ", " from ", " on Boomplay" };
        foreach (var marker in markers)
        {
            var idx = cleaned.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                return cleaned[..idx].Trim(' ', '"', '\'');
            }
        }

        return cleaned.Length <= 120 ? cleaned : null;
    }

    private static string? TryExtractFromDescription(string? description, IEnumerable<string> markers)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        foreach (var marker in markers)
        {
            var idx = description.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            var suffix = description[(idx + marker.Length)..];
            var end = suffix.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
            if (end > 0)
            {
                suffix = suffix[..end];
            }

            end = suffix.IndexOf(" from ", StringComparison.OrdinalIgnoreCase);
            if (end > 0)
            {
                suffix = suffix[..end];
            }

            var value = suffix.Trim(' ', '"', '\'');
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static void ApplySongDetailMetadata(HtmlDocument doc, BoomplayTrackMetadata track)
    {
        var detailItems = doc.DocumentNode.SelectNodes("//section[contains(concat(' ', normalize-space(@class), ' '), ' songDetailInfo ')]//li");
        if (detailItems == null || detailItems.Count == 0)
        {
            return;
        }

        foreach (var item in detailItems)
        {
            if (!TryReadSongDetailEntry(item, out var normalizedLabel, out var valueText))
            {
                continue;
            }

            TryApplySongDetailField(track, normalizedLabel, valueText);
        }
    }

    private static bool TryReadSongDetailEntry(HtmlNode item, out string normalizedLabel, out string valueText)
    {
        normalizedLabel = string.Empty;
        valueText = DecodeAndTrim(item.SelectSingleNode("./span")?.InnerText);
        var labelText = DecodeAndTrim(item.SelectSingleNode("./text()[normalize-space()]")?.InnerText);

        if (string.IsNullOrWhiteSpace(valueText))
        {
            var inline = DecodeAndTrim(item.InnerText);
            var separator = inline.IndexOf(':');
            if (separator >= 0)
            {
                labelText = DecodeAndTrim(inline[..separator]);
                valueText = DecodeAndTrim(inline[(separator + 1)..]);
            }
        }

        if (string.IsNullOrWhiteSpace(valueText))
        {
            return false;
        }

        normalizedLabel = DecodeAndTrim(labelText).Trim(':').ToLowerInvariant();
        return true;
    }

    private static void TryApplySongDetailField(BoomplayTrackMetadata track, string normalizedLabel, string valueText)
    {
        if (normalizedLabel == "genre" || normalizedLabel.Contains("genre", StringComparison.Ordinal))
        {
            AddGenre(track, valueText);
            return;
        }

        if (!string.IsNullOrWhiteSpace(track.ReleaseDate)
            || !IsReleaseDateLabel(normalizedLabel))
        {
            return;
        }

        var yearMatch = ReleaseYearRegex.Match(valueText);
        if (yearMatch.Success)
        {
            track.ReleaseDate = yearMatch.Groups["year"].Value;
        }
    }

    private static bool IsReleaseDateLabel(string normalizedLabel)
    {
        return normalizedLabel == "year"
               || normalizedLabel == "release year"
               || normalizedLabel == "year of release"
               || normalizedLabel.Contains("release date", StringComparison.Ordinal);
    }

    private static void AddGenre(BoomplayTrackMetadata target, string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return;
        }

        var normalized = genre.Trim();
        if (normalized.StartsWith('(') && normalized.EndsWith(')'))
        {
            normalized = normalized[1..^1];
        }

        var candidates = normalized
            .Split(GenreSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!IsLikelyGenreValue(candidate))
            {
                continue;
            }

            if (!target.Genres.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                target.Genres.Add(candidate);
            }
        }
    }

    private static bool IsLikelyGenreValue(string? value)
    {
        var cleaned = DecodeAndTrim(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false;
        }

        if (cleaned.Length is < 2 or > 64)
        {
            return false;
        }

        if (GenreNoiseValues.Contains(cleaned))
        {
            return false;
        }

        if (cleaned.Any(char.IsDigit) && cleaned.All(ch => char.IsDigit(ch) || ch == ' ' || ch == '-' || ch == '/'))
        {
            return false;
        }

        if (cleaned.Contains("http", StringComparison.OrdinalIgnoreCase)
            || cleaned.Contains(BoomplaySource, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static void ApplyEmbeddedGenreMetadata(string html, string songId, BoomplayTrackMetadata target)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return;
        }

        var addedFromScopedSnippet = false;
        if (!string.IsNullOrWhiteSpace(songId))
        {
            addedFromScopedSnippet = EnumerateSongScopedSnippets(html, songId)
                .Any(snippet => TryExtractGenresFromSnippet(snippet, target));
        }

        if (!addedFromScopedSnippet)
        {
            TryExtractGenresFromSnippet(html, target);
        }
    }

    private static HashSet<string> EnumerateSongScopedSnippets(string html, string songId)
    {
        var snippets = new HashSet<string>(StringComparer.Ordinal);
        var token = songId.Trim();
        var start = 0;
        while (start < html.Length)
        {
            var idx = html.IndexOf(token, start, StringComparison.Ordinal);
            if (idx < 0)
            {
                break;
            }

            var from = Math.Max(0, idx - 1800);
            var to = Math.Min(html.Length, idx + token.Length + 1800);
            var snippet = html[from..to];
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                snippets.Add(snippet);
            }

            start = idx + token.Length;
        }

        return snippets;
    }

    private static bool TryExtractGenresFromSnippet(string snippet, BoomplayTrackMetadata target)
    {
        var before = target.Genres.Count;

        foreach (Match match in JsonGenreRegex.Matches(snippet))
        {
            AddGenre(target, match.Groups[ValueField].Value);
        }

        foreach (Match match in JsonGenreNameRegex.Matches(snippet))
        {
            AddGenre(target, match.Groups[ValueField].Value);
        }

        foreach (var raw in JsonGenresArrayRegex
            .Matches(snippet)
            .Cast<Match>()
            .Select(match => match.Groups[ValueField].Value)
            .SelectMany(arrayPayload => JsonArrayStringRegex
                .Matches(arrayPayload)
                .Cast<Match>()
                .Select(entry => entry.Groups[ValueField].Value)))
        {
            AddGenre(target, DecodeAndTrim(Regex.Unescape(raw)));
        }

        return target.Genres.Count > before;
    }

    private readonly record struct SongAttemptParseResult(BoomplayTrackMetadata Metadata);
    private readonly record struct Id3Frame(string FrameId, int FrameOffset, int FrameSize);

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(nameof(BoomplayMetadataService));
        client.Timeout = TimeSpan.FromSeconds(25);
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        }

        return client;
    }

    private static Task DelayForRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var normalizedAttempt = Math.Max(1, attempt);
        var baseMs = 300 * normalizedAttempt;
        var jitterMs = Random.Shared.Next(80, 300);
        return Task.Delay(baseMs + jitterMs, cancellationToken);
    }

    private static MemoryCacheEntryOptions BuildSongCacheOptions()
    {
        return new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = TimeSpan.FromMinutes(30),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6)
        };
    }

    private static MemoryCacheEntryOptions BuildPlaylistCacheOptions()
    {
        return new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = TimeSpan.FromMinutes(20),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2)
        };
    }

    private static MemoryCacheEntryOptions BuildSearchCacheOptions()
    {
        return new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = TimeSpan.FromMinutes(10),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
        };
    }
}

public sealed class BoomplayTrackMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public string Isrc { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public string ReleaseDate { get; set; } = string.Empty;
    public List<string> Genres { get; set; } = new();
    public string Composer { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public int Bpm { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public bool HasStreamTagMetadata { get; set; }
    public bool HasStreamGenreMetadata { get; set; }
}

public sealed class BoomplayPlaylistMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public List<string> TrackIds { get; set; } = new();
    public Dictionary<string, BoomplayTrackHint> TrackHints { get; set; } = new(StringComparer.Ordinal);
    public List<BoomplayTrackMetadata> Tracks { get; set; } = new();
    public List<BoomplayRecommendationSection> RecommendationSections { get; set; } = new();
}

public sealed class BoomplayTrackHint
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
}

public sealed class BoomplayRecommendationSection
{
    public string Title { get; set; } = string.Empty;
    public List<BoomplayPlaylistRecommendation> Items { get; set; } = new();
}

public sealed class BoomplayPlaylistRecommendation
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
}
