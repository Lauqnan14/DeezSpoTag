using System.Net.Http;
using System.Net;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Apple;

public static class AppleQueueHelpers
{
    private static readonly string[] ArtworkSearchBuckets = ["songs", "albums"];

    public sealed class AppleCatalogCoverLookup
    {
        public string? AppleId { get; init; }
        public string? Title { get; init; }
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public string Storefront { get; init; } = "us";
        public int Size { get; init; }
        public ILogger Logger { get; init; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public sealed class AppleArtworkDownloadRequest
    {
        public string RawUrl { get; init; } = string.Empty;
        public string OutputPath { get; init; } = string.Empty;
        public DeezSpoTagSettings Settings { get; init; } = new();
        public int Size { get; init; }
        public string Overwrite { get; init; } = "overwrite";
        public bool PreferMaxQuality { get; init; }
        public ILogger Logger { get; init; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public sealed class AnimatedArtworkSaveRequest
    {
        public string? AppleId { get; init; }
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public string? BaseFileName { get; init; }
        public string Storefront { get; init; } = "us";
        public int MaxResolution { get; init; }
        public string OutputDir { get; init; } = string.Empty;
        public ILogger Logger { get; init; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public string? CollectionType { get; init; }
        public string? CollectionId { get; init; }
        public IReadOnlyCollection<string>? OutputFormats { get; init; }
        public bool RenameExistingArtwork { get; init; }
        public int MaxSizeMb { get; init; } = DefaultAnimatedArtworkMaxSizeMb;
    }

    public sealed record AnimatedArtworkSaveResult(
        IReadOnlyList<string> Paths,
        string Status,
        string Message);

    private sealed class AnimatedArtworkResolveRequest
    {
        public string? AppleId { get; init; }
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public string Storefront { get; init; } = "us";
        public int MaxResolution { get; init; }
        public ILogger Logger { get; init; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public string? CollectionType { get; init; }
        public string? CollectionId { get; init; }
    }

    private sealed class CollectionMotionResolveRequest
    {
        public string CollectionType { get; init; } = string.Empty;
        public string CollectionId { get; init; } = string.Empty;
        public string Storefront { get; init; } = "us";
        public int MaxResolution { get; init; }
        public ILogger Logger { get; init; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    private sealed class CollectionMotionCacheRequest
    {
        public string? CollectionType { get; init; }
        public string? CollectionId { get; init; }
        public string Storefront { get; init; } = "us";
        public int MaxResolution { get; init; }
        public ILogger Logger { get; init; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    private sealed class AnimatedArtworkFromSongRequest
    {
        public string SongId { get; init; } = string.Empty;
        public string? ExpectedArtist { get; init; }
        public string? ExpectedAlbum { get; init; }
        public string Storefront { get; init; } = "us";
        public int MaxResolution { get; init; }
        public ILogger Logger { get; init; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    private const string DefaultArtworkFormat = "jpg";
    private const string DefaultLanguage = "en-US";
    public const int DefaultAnimatedArtworkMaxSizeMb = 10;
    private const string AnimatedArtworkMp4 = "mp4";
    private const string AnimatedArtworkWebp = "webp";
    private const string AnimatedArtworkGif = "gif";
    private const string ResultsKey = "results";
    private const string ArtistNameKey = "artistName";
    private const string AttributesKey = "attributes";
    private const string AlbumNameKey = "albumName";
    private const string VideoType = "video";
    private const string UnknownValue = "Unknown";
    private const string RawItunesArtworkMarker = "#deezspotag-itunes-raw";
    private static readonly MemoryCache AppleArtworkCache = new(new MemoryCacheOptions { SizeLimit = 512 });
    private static readonly ConcurrentDictionary<string, Lazy<Task<AnimatedArtworkUrls?>>> AnimatedArtworkLookups =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> AnimatedArtworkSourceDownloads =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> AnimatedArtworkConversions =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly string? FfmpegExecutable = ResolveExecutablePath(
        OperatingSystem.IsWindows()
            ? new[] { "ffmpeg.exe" }
            : new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/bin/ffmpeg" });
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static Match MatchWithTimeout(string input, string pattern, RegexOptions options = RegexOptions.None)
        => Regex.Match(input, pattern, options, RegexTimeout);
    private static string[] SplitWithTimeout(string input, string pattern, RegexOptions options = RegexOptions.None)
        => Regex.Split(input, pattern, options, RegexTimeout);
    private static string ReplaceWithTimeout(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => Regex.Replace(input, pattern, replacement, options, RegexTimeout);

    public static async Task<string?> ResolveAppleCoverFromCatalogAsync(
        AppleMusicCatalogService appleCatalog,
        AppleCatalogCoverLookup request,
        CancellationToken cancellationToken)
    {
        var appleId = request.AppleId;
        var title = request.Title;
        var artist = request.Artist;
        var album = request.Album;
        var storefront = request.Storefront;
        var size = request.Size;
        var logger = request.Logger;

        if (!string.IsNullOrWhiteSpace(appleId))
        {
            try
            {
                using var doc = await appleCatalog.GetSongAsync(appleId, storefront, DefaultLanguage, cancellationToken);
                var cover = TryExtractArtwork(doc.RootElement, artist, album, size);
                if (!string.IsNullOrWhiteSpace(cover))
                {
                    return cover;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(ex, "Apple catalog cover lookup failed for {AppleId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(appleId));                }
            }
        }

        var termParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist))
        {
            termParts.Add(artist);
        }
        if (!string.IsNullOrWhiteSpace(album))
        {
            termParts.Add(album);
        }
        if (termParts.Count == 0 && !string.IsNullOrWhiteSpace(title))
        {
            termParts.Add(title);
        }

        if (termParts.Count == 0)
        {
            return null;
        }

        var term = string.Join(" ", termParts);
        try
        {
            using var doc = await appleCatalog.SearchAsync(
                term,
                limit: 5,
                storefront: storefront,
                language: DefaultLanguage,
                cancellationToken,
                new AppleMusicCatalogService.AppleSearchOptions(TypesOverride: "songs,albums"));
            var cover = TryExtractArtwork(doc.RootElement, artist, album, size);
            return string.IsNullOrWhiteSpace(cover) ? null : cover;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Apple catalog cover search failed.");
        }

        return null;
    }

    public static async Task<string?> ResolveAppleCollectionCoverFromCatalogAsync(
        AppleMusicCatalogService appleCatalog,
        string collectionType,
        string collectionId,
        string storefront,
        int size,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(collectionType) || string.IsNullOrWhiteSpace(collectionId))
        {
            return null;
        }

        try
        {
            using var document = collectionType.Trim().ToLowerInvariant() switch
            {
                "album" => await appleCatalog.GetAlbumAsync(collectionId, storefront, DefaultLanguage, cancellationToken),
                "playlist" => await appleCatalog.GetPlaylistAsync(collectionId, storefront, DefaultLanguage, cancellationToken),
                "station" => await appleCatalog.GetStationAsync(collectionId, storefront, DefaultLanguage, cancellationToken),
                _ => null
            };
            if (document == null
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0
                || !data[0].TryGetProperty(AttributesKey, out var attributes)
                || attributes.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var artworkUrl = TryExtractArtworkFromAttrs(attributes, size);
            if (!string.IsNullOrWhiteSpace(artworkUrl))
            {
                try
                {
                    await AppleAnimatedArtworkCache.SaveStaticArtworkUrlAsync(
                        collectionType.Trim().ToLowerInvariant(),
                        collectionId,
                        storefront,
                        artworkUrl,
                        cancellationToken);
                }
                catch (Exception cacheException) when (cacheException is not OperationCanceledException)
                {
                    logger.LogDebug(cacheException, "Persistent Apple collection still-artwork cache write failed.");
                }
            }

            return artworkUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(
                ex,
                "Apple collection cover lookup failed for {CollectionType}:{CollectionId}.",
                LogSanitizer.OneLine(collectionType),
                LogSanitizer.OneLine(collectionId));
            return null;
        }
    }

    public static async Task<string?> ResolveAppleCoverAsync(
        IHttpClientFactory httpClientFactory,
        string? title,
        string? artist,
        string? album,
        int size,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var lookup = BuildItunesLookupContext(title, artist, album);
        if (lookup == null)
        {
            return null;
        }

        var cacheKey = $"itunes:cover:{lookup.Term}:{size}";
        if (AppleArtworkCache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(lookup.Term)}&entity=album&limit=5";

        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty(ResultsKey, out var results) || results.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }

            var selectedRaw = SelectItunesArtworkRaw(results, lookup.NormalizedArtist, lookup.NormalizedAlbum, lookup.AlbumRequested);
            if (string.IsNullOrWhiteSpace(selectedRaw))
            {
                return null;
            }

            var normalized = NormalizeArtworkUrl(selectedRaw, size);
            CacheArtworkValue(cacheKey, normalized, TimeSpan.FromHours(1));
            return normalized;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Apple cover lookup failed.");
        }

        return null;
    }

    private static string? TryExtractArtwork(System.Text.Json.JsonElement root, string? artist, string? album, int size)
    {
        if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != System.Text.Json.JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            if (root.TryGetProperty(ResultsKey, out var results)
                && results.ValueKind == System.Text.Json.JsonValueKind.Object
                && TryExtractArtworkFromSearch(results, artist, album, size, out var result))
            {
                return result;
            }
            return null;
        }

        var entry = dataArr[0];
        if (!entry.TryGetProperty(AttributesKey, out var attrs) || attrs.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        var candidateArtist = TryReadItunesString(attrs, ArtistNameKey);
        var candidateAlbum = TryReadItunesString(attrs, AlbumNameKey)
            ?? TryReadItunesString(attrs, "name");
        if (ShouldRejectArtworkCandidate(artist, album, candidateArtist, candidateAlbum))
        {
            return null;
        }

        return TryExtractArtworkFromAttrs(attrs, size);
    }

    /// <summary>
    /// Artist-image lookup. Unlike <see cref="TryExtractArtwork"/> this never falls back to the
    /// song or album artwork buckets: an artist without a portrait must report a miss so the
    /// configured artist artwork fallback order can try the next provider.
    /// </summary>
    private static string? TryExtractArtistArtwork(System.Text.Json.JsonElement root, int size)
    {
        if (!root.TryGetProperty("data", out var dataArr)
            || dataArr.ValueKind != System.Text.Json.JsonValueKind.Array
            || dataArr.GetArrayLength() == 0)
        {
            return null;
        }

        var entry = dataArr[0];
        return entry.TryGetProperty(AttributesKey, out var attrs)
               && attrs.ValueKind == System.Text.Json.JsonValueKind.Object
            ? TryExtractArtworkFromAttrs(attrs, size)
            : null;
    }

    private static bool TryExtractArtworkFromSearch(System.Text.Json.JsonElement results, string? artist, string? album, int size, out string? url)
    {
        url = null;
        var candidateUrl = ArtworkSearchBuckets
            .Select(key => TryExtractArtworkFromSearchBucket(results, key, artist, album, size, out var bucketUrl) ? bucketUrl : null)
            .FirstOrDefault(static bucketUrl => !string.IsNullOrWhiteSpace(bucketUrl));
        if (!string.IsNullOrWhiteSpace(candidateUrl))
        {
            url = candidateUrl;
            return true;
        }

        return false;
    }

    private static bool TryExtractArtworkFromSearchBucket(
        JsonElement results,
        string key,
        string? artist,
        string? album,
        int size,
        out string? url)
    {
        url = null;
        if (!results.TryGetProperty(key, out var bucket) || bucket.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!bucket.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return false;
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty(AttributesKey, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var candidateArtist = TryReadItunesString(attrs, ArtistNameKey);
            var candidateAlbum = TryReadItunesString(attrs, AlbumNameKey)
                ?? TryReadItunesString(attrs, "collectionName")
                ?? TryReadItunesString(attrs, "name");
            if (ShouldRejectArtworkCandidate(artist, album, candidateArtist, candidateAlbum))
            {
                continue;
            }

            url = TryExtractArtworkFromAttrs(attrs, size);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldRejectArtworkCandidate(
        string? requestedArtist,
        string? requestedAlbum,
        string? candidateArtist,
        string? candidateAlbum)
    {
        if (ArtworkFallbackHelper.ShouldRejectCompilationArtworkCandidate(requestedAlbum, candidateAlbum))
        {
            return true;
        }

        if (ArtworkFallbackHelper.ShouldRejectAlbumArtworkCandidate(requestedAlbum, candidateAlbum))
        {
            return true;
        }

        var normalizedRequestedArtist = string.IsNullOrWhiteSpace(requestedArtist) ? string.Empty : NormalizeLookupToken(requestedArtist);
        var normalizedCandidateArtist = string.IsNullOrWhiteSpace(candidateArtist) ? string.Empty : NormalizeLookupToken(candidateArtist);
        if (!string.IsNullOrWhiteSpace(normalizedRequestedArtist)
            && !string.IsNullOrWhiteSpace(normalizedCandidateArtist)
            && !IsLikelySameArtist(normalizedRequestedArtist, normalizedCandidateArtist))
        {
            return true;
        }

        return false;
    }

    private static string ExtractArtworkUrl(System.Text.Json.JsonElement attrs)
    {
        if (!attrs.TryGetProperty("artwork", out var art) || art.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!art.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return string.Empty;
        }

        var raw = urlEl.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw;
    }

    private static string? TryExtractArtworkFromAttrs(System.Text.Json.JsonElement attrs, int size)
    {
        var raw = ExtractArtworkUrl(attrs);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var sizeText = $"{size}x{size}";
        return BuildAppleArtworkUrl(raw, sizeText, size, size, DefaultArtworkFormat);
    }

    public static async Task<string?> ResolveAppleArtistImageAsync(
        AppleMusicCatalogService appleCatalog,
        string artist,
        string storefront,
        int size,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var cacheKey = $"apple:artist-image:{storefront}:{artist}:{size}";
        if (AppleArtworkCache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        using var doc = await appleCatalog.SearchAsync(
            artist,
            limit: 5,
            storefront: storefront,
            language: DefaultLanguage,
            cancellationToken,
            new AppleMusicCatalogService.AppleSearchOptions(TypesOverride: "artists"));

        if (!doc.RootElement.TryGetProperty(ResultsKey, out var results) || results.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        if (!results.TryGetProperty("artists", out var artists) || artists.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        if (!artists.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return null;
        }

        var resolved = TryResolveAppleArtistArtwork(data, artist, size);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            AppleArtworkCache.Set(cacheKey, resolved, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2),
                Size = 1
            });
        }

        return resolved;
    }

    public static async Task<string?> ResolveAppleArtistImageByIdAsync(
        AppleMusicCatalogService appleCatalog,
        string? artistId,
        string storefront,
        int size,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return null;
        }

        var cacheKey = $"apple:artist-image-id:{storefront}:{artistId}:{size}";
        if (AppleArtworkCache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        using var doc = await appleCatalog.GetArtistAsync(artistId, storefront, DefaultLanguage, cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
        {
            return null;
        }

        var entry = data[0];
        if (!entry.TryGetProperty(AttributesKey, out var attributes)
            || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var resolved = TryExtractArtworkFromAttrs(attributes, size);
        if (!string.IsNullOrWhiteSpace(resolved) && IsLikelyUsableItunesArtistPageArtwork(resolved))
        {
            CacheArtistArtwork(cacheKey, resolved);
            return resolved;
        }

        return null;
    }

    private static string? TryResolveAppleArtistArtwork(JsonElement data, string artist, int size)
    {
        var normalizedArtist = NormalizeLookupToken(artist);

        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty(AttributesKey, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var artwork = TryExtractArtworkFromAttrs(attrs, size);
            if (string.IsNullOrWhiteSpace(artwork))
            {
                continue;
            }

            var candidateName = TryReadItunesString(attrs, "name")
                ?? TryReadItunesString(attrs, ArtistNameKey);
            if (!string.IsNullOrWhiteSpace(candidateName)
                && IsExactArtistIdentity(normalizedArtist, NormalizeLookupToken(candidateName)))
            {
                return artwork;
            }
        }

        return null;
    }

    public static async Task<string?> ResolveItunesArtistImageAsync(
        IHttpClientFactory httpClientFactory,
        string? artist,
        int size,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var normalizedArtist = NormalizeLookupToken(artist);
        var cacheKey = $"itunes:artist-image:{normalizedArtist}:{size}";
        if (AppleArtworkCache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(artist)}&entity=musicArtist&limit=10";
        try
        {
            var client = httpClientFactory.CreateClient();
            var pageArtwork = await TryResolveItunesArtistSearchPageArtworkAsync(
                client,
                url,
                normalizedArtist,
                size,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(pageArtwork))
            {
                CacheArtistArtwork(cacheKey, pageArtwork);
                return pageArtwork;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "iTunes artist image lookup failed for {Artist}", artist);
            }
        }

        return null;
    }

    private static void CacheArtistArtwork(string cacheKey, string artworkUrl)
    {
        AppleArtworkCache.Set(cacheKey, artworkUrl, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
            Size = 1
        });
    }

    private static async Task<string?> TryResolveItunesArtistSearchPageArtworkAsync(
        HttpClient client,
        string searchUrl,
        string normalizedArtist,
        int size,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(searchUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty(ResultsKey, out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in results.EnumerateArray())
        {
            if (!TryResolveItunesArtistLink(entry, normalizedArtist, out var artistLinkUrl))
            {
                continue;
            }

            var pageArtwork = await TryResolveItunesArtistPageArtworkAsync(
                client,
                artistLinkUrl,
                size,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(pageArtwork))
            {
                return pageArtwork;
            }
        }

        return null;
    }

    private static bool TryResolveItunesArtistLink(
        JsonElement entry,
        string normalizedArtist,
        out string artistLinkUrl)
    {
        artistLinkUrl = string.Empty;
        var candidateArtist = TryReadItunesString(entry, ArtistNameKey);
        if (string.IsNullOrWhiteSpace(candidateArtist)
            || !IsExactArtistIdentity(normalizedArtist, NormalizeLookupToken(candidateArtist)))
        {
            return false;
        }

        artistLinkUrl = TryReadItunesString(entry, "artistLinkUrl") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(artistLinkUrl);
    }

    private static async Task<string?> TryResolveItunesArtistPageArtworkAsync(
        HttpClient client,
        string artistLinkUrl,
        int size,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(artistLinkUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var ogImage = MatchWithTimeout(
            html,
            "<meta\\s+property=\"og:image\"\\s+content=\"(?<url>[^\"]+)\"",
            RegexOptions.IgnoreCase);
        var raw = ogImage.Success
            ? WebUtility.HtmlDecode(ogImage.Groups["url"].Value)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || !IsLikelyUsableItunesArtistPageArtwork(raw))
        {
            raw = TryExtractSquareAppleArtistPageArtwork(html);
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (raw.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("{w}", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("{h}", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeArtworkUrl(raw, size);
        }

        return raw;
    }

    private static string? TryExtractSquareAppleArtistPageArtwork(string html)
    {
        var matches = Regex.Matches(
            html,
            "https://[^\\\"'\\s]+/image/thumb/AMCArtistImages[^\\\"'\\s]+/(?<width>\\d{2,5})x(?<height>\\d{2,5})[^\\\"'\\s]*\\.(?:jpg|jpeg|png|webp)",
            RegexOptions.IgnoreCase,
            RegexTimeout);
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["width"].Value, out var width)
                || !int.TryParse(match.Groups["height"].Value, out var height)
                || width <= 0
                || height <= 0
                || Math.Max(width, height) / (double)Math.Min(width, height) > 1.01d)
            {
                continue;
            }

            return WebUtility.HtmlDecode(match.Value);
        }

        return null;
    }

    private static bool IsLikelyUsableItunesArtistPageArtwork(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        var match = MatchWithTimeout(
            rawUrl,
            @"/(?<width>\d{2,5})x(?<height>\d{2,5})(?<suffix>[a-z]{0,8})\.[a-z0-9]+(?:$|\?)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return true;
        }

        if (!int.TryParse(match.Groups["width"].Value, out var width)
            || !int.TryParse(match.Groups["height"].Value, out var height)
            || width <= 0
            || height <= 0)
        {
            return true;
        }

        var longer = Math.Max(width, height);
        var shorter = Math.Min(width, height);
        if (shorter == 0 || (longer / (double)shorter) > 1.1d)
        {
            return false;
        }

        var suffix = match.Groups["suffix"].Value;
        return !suffix.Contains("cw", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string?> ResolveAppleArtistImageFromSongAsync(
        AppleMusicCatalogService appleCatalog,
        string? appleSongId,
        string storefront,
        int size,
        ILogger logger,
        CancellationToken cancellationToken,
        bool allowAlbumArtwork = false)
    {
        if (string.IsNullOrWhiteSpace(appleSongId))
        {
            return null;
        }

        var cacheKey = $"apple:artist-image-song:{storefront}:{appleSongId}:{size}:{allowAlbumArtwork}";
        if (AppleArtworkCache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        try
        {
            using var songDoc = await appleCatalog.GetSongAsync(appleSongId, storefront, DefaultLanguage, cancellationToken);
            var artistId = TryExtractPrimaryArtistId(songDoc.RootElement);
            if (string.IsNullOrWhiteSpace(artistId))
            {
                return null;
            }

            using var artistDoc = await appleCatalog.GetArtistAsync(artistId, storefront, DefaultLanguage, cancellationToken);
            var resolved = allowAlbumArtwork
                ? TryExtractArtwork(artistDoc.RootElement, null, null, size)
                : TryExtractArtistArtwork(artistDoc.RootElement, size);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                return null;
            }

            AppleArtworkCache.Set(cacheKey, resolved, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2),
                Size = 1
            });
            return resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Apple artist image lookup via song failed for {AppleSongId}", LogSanitizer.OneLine(appleSongId));
            }
            return null;
        }
    }

    public static (int Width, int Height, string SizeText) GetAppleArtworkDimensions(DeezSpoTagSettings settings)
    {
        var sizeText = settings.AppleArtworkSizeText;
        if (!IsValidArtworkSizeText(sizeText))
        {
            var fallback = settings.AppleArtworkSize > 0 ? settings.AppleArtworkSize : 1200;
            return (fallback, fallback, $"{fallback}x{fallback}");
        }

        var parts = sizeText.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var width = int.Parse(parts[0]);
        var height = int.Parse(parts[1]);
        return (width, height, sizeText);
    }

    private static string? TryExtractPrimaryArtistId(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataArr)
            || dataArr.ValueKind != JsonValueKind.Array
            || dataArr.GetArrayLength() == 0)
        {
            return null;
        }

        var entry = dataArr[0];
        if (!entry.TryGetProperty("relationships", out var relationships)
            || relationships.ValueKind != JsonValueKind.Object
            || !relationships.TryGetProperty("artists", out var artists)
            || artists.ValueKind != JsonValueKind.Object
            || !artists.TryGetProperty("data", out var artistData)
            || artistData.ValueKind != JsonValueKind.Array
            || artistData.GetArrayLength() == 0)
        {
            return null;
        }

        var primary = artistData[0];
        if (!primary.TryGetProperty("id", out var artistIdElement))
        {
            return null;
        }

        return artistIdElement.ValueKind == JsonValueKind.String
            ? artistIdElement.GetString()
            : artistIdElement.ToString();
    }

    public static int GetAppleArtworkSize(DeezSpoTagSettings settings)
        => GetAppleArtworkDimensions(settings).Width;

    public static string GetAppleArtworkFormat(DeezSpoTagSettings settings)
    {
        var formats = GetArtworkOutputFormats(settings);
        return formats.Count == 1 && string.Equals(formats[0], "png", StringComparison.OrdinalIgnoreCase)
            ? "png"
            : DefaultArtworkFormat;
    }

    public static IReadOnlyList<string> GetArtworkOutputFormats(DeezSpoTagSettings settings)
    {
        var configured = (settings.LocalArtworkFormat ?? DefaultArtworkFormat)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static format => format.ToLowerInvariant())
            .Where(static format => format is "jpg" or "png")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (configured.Count == 0)
        {
            configured.Add(DefaultArtworkFormat);
        }

        return configured;
    }

    public static string BuildAppleArtworkUrl(string raw, string sizeText, int width, int height, string format)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        var normalized = raw;
        if (string.Equals(format, "png", StringComparison.OrdinalIgnoreCase))
        {
            var match = MatchWithTimeout(normalized, @"\{w\}x\{h\}");
            if (match.Success)
            {
                var parts = SplitWithTimeout(normalized, @"\{w\}x\{h\}");
                if (parts.Length == 2)
                {
                    normalized = parts[0] + "{w}x{h}" + parts[1].Replace(".jpg", ".png", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        normalized = NormalizeArtworkUrl(normalized, sizeText, width, height);

        if (string.Equals(format, "original", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Replace("is1-ssl.mzstatic.com/image/thumb", "a5.mzstatic.com/us/r1000/0", StringComparison.OrdinalIgnoreCase);
            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash > 0)
            {
                normalized = normalized[..lastSlash];
            }
        }

        return normalized;
    }

    public static string? BuildAppleArtworkFallbackUrl(string raw, string sizeText, int width, int height, string format)
    {
        if (!string.Equals(format, "original", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var extension = GetAppleArtworkExtension(raw, format);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = "jpg";
        }

        var normalized = NormalizeArtworkUrl(raw, sizeText, width, height);
        if (!normalized.EndsWith($".{extension}", StringComparison.OrdinalIgnoreCase))
        {
            normalized = ReplaceWithTimeout(normalized, @"\.[a-z0-9]+$", $".{extension}", RegexOptions.IgnoreCase);
        }

        return normalized;
    }

    public static string GetAppleArtworkExtension(string raw, string format)
    {
        if (string.Equals(format, "png", StringComparison.OrdinalIgnoreCase))
        {
            return "png";
        }

        var match = MatchWithTimeout(raw ?? string.Empty, @"\.([a-zA-Z0-9]+)(?:$|\?)");
        if (match.Success)
        {
            return match.Groups[1].Value.ToLowerInvariant();
        }

        return "jpg";
    }

    public static async Task<string?> DownloadAppleArtworkAsync(
        ImageDownloader downloader,
        AppleArtworkDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var rawUrl = request.RawUrl;
        var outputPath = request.OutputPath;
        var settings = request.Settings;
        var size = request.Size;
        var overwrite = request.Overwrite;
        var preferMaxQuality = request.PreferMaxQuality;
        var logger = request.Logger;

        var isRawItunesArtwork = IsRawItunesArtworkUrl(rawUrl);
        var sourceUrl = StripRawItunesArtworkMarker(rawUrl);

        if (isRawItunesArtwork)
        {
            return await DownloadRawItunesArtworkAsync(
                downloader,
                request,
                sourceUrl,
                cancellationToken);
        }

        var (_, _, sizeText) = GetAppleArtworkDimensions(settings);
        var pathExtension = Path.GetExtension(outputPath)?.TrimStart('.').ToLowerInvariant();
        var format = pathExtension is "jpg" or "png"
            ? pathExtension
            : GetAppleArtworkFormat(settings);
        var effectiveSizeText = size > 0 ? $"{size}x{size}" : sizeText;
        var url = BuildAppleArtworkUrl(sourceUrl, effectiveSizeText, size, size, format);

        var downloaded = await downloader.DownloadImageAsync(
            url,
            outputPath,
            overwrite,
            preferMaxQuality,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(downloaded))
        {
            return downloaded;
        }

        var fallbackUrl = BuildAppleArtworkFallbackUrl(sourceUrl, effectiveSizeText, size, size, format);
        if (!string.IsNullOrWhiteSpace(fallbackUrl))
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Apple artwork fallback URL: {Url}", fallbackUrl);
            }
            return await downloader.DownloadImageAsync(
                fallbackUrl,
                outputPath,
                overwrite,
                preferMaxQuality,
                cancellationToken);
        }

        return null;
    }

    private static async Task<string?> DownloadRawItunesArtworkAsync(
        ImageDownloader downloader,
        AppleArtworkDownloadRequest request,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        var (preferredWidth, preferredHeight, preferredSizeText) = ResolveRawItunesArtworkDimensions(
            sourceUrl,
            request.Settings,
            request.Size,
            request.Logger);
        var preferredUrl = NormalizeArtworkUrl(sourceUrl, preferredSizeText, preferredWidth, preferredHeight);
        var effectivePath = ResolveRawItunesArtworkOutputPath(request.OutputPath, preferredUrl);

        if (request.Logger.IsEnabled(LogLevel.Debug))
        {
            request.Logger.LogDebug(
                "Downloading iTunes artwork with configured size preference: {Url} (target: {Path})",
                LogSanitizer.OneLine(preferredUrl),
                LogSanitizer.OneLine(effectivePath));
        }

        return await downloader.DownloadImageAsync(
            preferredUrl,
            effectivePath,
            request.Overwrite,
            request.PreferMaxQuality,
            cancellationToken);
    }

    private static (int Width, int Height, string SizeText) ResolveRawItunesArtworkDimensions(
        string sourceUrl,
        DeezSpoTagSettings settings,
        int size,
        ILogger logger)
    {
        var (preferredWidth, preferredHeight, preferredSizeText) = GetAppleArtworkDimensions(settings);
        if (!ShouldPreserveRawArtworkSize(sourceUrl, out var sourceWidth, out var sourceHeight))
        {
            return (preferredWidth, preferredHeight, preferredSizeText);
        }

        var requestedWidth = size > 0 ? size : preferredWidth;
        var requestedHeight = size > 0 ? size : preferredHeight;
        var safeWidth = sourceWidth > 0 ? Math.Min(requestedWidth, sourceWidth) : requestedWidth;
        var safeHeight = sourceHeight > 0 ? Math.Min(requestedHeight, sourceHeight) : requestedHeight;
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Apple raw artwork uses presentation crop suffix; clamping requested size to {Width}x{Height} for {Url}",
                safeWidth,
                safeHeight,
                sourceUrl);
        }

        return (safeWidth, safeHeight, $"{safeWidth}x{safeHeight}");
    }

    private static string ResolveRawItunesArtworkOutputPath(string outputPath, string preferredUrl)
    {
        var requestedExtension = Path.GetExtension(outputPath).TrimStart('.');
        var rawExtension = GetAppleArtworkExtension(preferredUrl, DefaultArtworkFormat);
        return !string.IsNullOrWhiteSpace(rawExtension)
               && !string.Equals(requestedExtension, rawExtension, StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(outputPath, rawExtension)
            : outputPath;
    }

    private static bool ShouldPreserveRawArtworkSize(string sourceUrl, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!TryExtractArtworkDimensionSuffix(sourceUrl, out var suffix, out var extractedWidth, out var extractedHeight))
        {
            return false;
        }

        width = extractedWidth;
        height = extractedHeight;

        // Apple "ac"/"cw" variants are presentation-style assets. Upscaling those to
        // 5000x5000 can produce mostly blank canvases with a tiny cropped corner.
        return suffix.Contains("ac", StringComparison.OrdinalIgnoreCase)
            || suffix.Contains("cw", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractArtworkDimensionSuffix(string sourceUrl, out string suffix, out int width, out int height)
    {
        suffix = string.Empty;
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return false;
        }

        var match = MatchWithTimeout(
            sourceUrl,
            @"/(?<width>\d{2,5})x(?<height>\d{2,5})(?<suffix>[a-z]{0,8})\.[a-z0-9]+(?:$|\?)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        _ = int.TryParse(match.Groups["width"].Value, out width);
        _ = int.TryParse(match.Groups["height"].Value, out height);
        suffix = match.Groups["suffix"].Value;
        return !string.IsNullOrWhiteSpace(suffix);
    }

    public static bool IsRawItunesArtworkUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        if (rawUrl.Contains(RawItunesArtworkMarker, StringComparison.Ordinal))
        {
            return true;
        }

        if (rawUrl.Contains("{w}", StringComparison.OrdinalIgnoreCase)
            || rawUrl.Contains("{h}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Host.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (uri.AbsolutePath.Contains("/source/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!uri.AbsolutePath.Contains("/image/thumb/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MatchWithTimeout(
            uri.AbsolutePath,
            @"/\d{2,5}x\d{2,5}[a-z]{0,8}\.[a-zA-Z0-9]+$",
            RegexOptions.IgnoreCase).Success;
    }

    private static string StripRawItunesArtworkMarker(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return rawUrl;
        }

        var markerIndex = rawUrl.IndexOf(RawItunesArtworkMarker, StringComparison.Ordinal);
        return markerIndex >= 0
            ? rawUrl[..markerIndex]
            : rawUrl;
    }

    public static string NormalizeArtworkUrl(string raw, string sizeText, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        var updated = raw
            .Replace("{w}x{h}", sizeText, StringComparison.OrdinalIgnoreCase)
            .Replace("{w}", width.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{h}", height.ToString(), StringComparison.OrdinalIgnoreCase);

        updated = ReplaceWithTimeout(updated, @"w=\d+", $"w={width}", RegexOptions.IgnoreCase);
        updated = ReplaceWithTimeout(updated, @"h=\d+", $"h={height}", RegexOptions.IgnoreCase);
        updated = ReplaceWithTimeout(updated, @"/\d{2,5}x\d{2,5}", $"/{sizeText}");
        updated = ReplaceWithTimeout(updated, @"\d{2,5}x\d{2,5}bb", $"{sizeText}bb", RegexOptions.IgnoreCase);

        return updated;
    }

    public static string NormalizeArtworkUrl(string raw, int size)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        var sizeText = size.ToString();
        var sizePair = $"{size}x{size}";

        var updated = raw
            .Replace("{w}x{h}", sizePair, StringComparison.OrdinalIgnoreCase)
            .Replace("{w}", sizeText, StringComparison.OrdinalIgnoreCase)
            .Replace("{h}", sizeText, StringComparison.OrdinalIgnoreCase);

        updated = ReplaceWithTimeout(updated, @"w=\d+", $"w={sizeText}", RegexOptions.IgnoreCase);
        updated = ReplaceWithTimeout(updated, @"h=\d+", $"h={sizeText}", RegexOptions.IgnoreCase);
        updated = ReplaceWithTimeout(updated, @"/\d{2,5}x\d{2,5}", $"/{sizePair}");
        updated = ReplaceWithTimeout(updated, @"\d{2,5}x\d{2,5}bb", $"{sizePair}bb", RegexOptions.IgnoreCase);

        return updated;
    }

    private static bool IsValidArtworkSizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        return int.TryParse(parts[0], out var width)
            && int.TryParse(parts[1], out var height)
            && width > 0
            && height > 0;
    }

    private static string? TryReadItunesString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string NormalizeLookupToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .ToLowerInvariant()
            .Replace("&", " and ", StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("\"", string.Empty, StringComparison.Ordinal);
        normalized = ReplaceWithTimeout(normalized, @"[^\p{L}\p{N}\s]+", " ");
        normalized = ReplaceWithTimeout(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static bool IsLikelySameArtist(string expected, string candidate)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (string.Equals(expected, candidate, StringComparison.Ordinal))
        {
            return true;
        }

        return expected.Contains(candidate, StringComparison.Ordinal)
            || candidate.Contains(expected, StringComparison.Ordinal);
    }

    private static bool IsExactArtistIdentity(string expected, string candidate)
        => !string.IsNullOrWhiteSpace(expected)
           && !string.IsNullOrWhiteSpace(candidate)
           && string.Equals(expected, candidate, StringComparison.Ordinal);

    public static async Task<AnimatedArtworkSaveResult> SaveAnimatedArtworkAsync(
        AppleMusicCatalogService appleCatalog,
        IHttpClientFactory httpClientFactory,
        AnimatedArtworkSaveRequest request,
        CancellationToken cancellationToken)
    {
        var outputDir = request.OutputDir;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return new AnimatedArtworkSaveResult(
                Array.Empty<string>(),
                "invalid_request",
                "Animated artwork output directory is not configured.");
        }

        var existingPaths = await SaveExistingAnimatedArtworkVariantsAsync(
            request,
            request.Logger,
            cancellationToken);
        if (AreAllCanonicalAnimatedArtworkOutputsPresent(request))
        {
            await RegisterAnimatedArtworkArtifactsAsync(request, existingPaths, cancellationToken);
            return new AnimatedArtworkSaveResult(
                existingPaths,
                "existing_complete",
                "All requested animated artwork variants already exist.");
        }

        var motion = await ResolveAnimatedArtworkUrlsAsync(
            appleCatalog,
            httpClientFactory,
            new AnimatedArtworkResolveRequest
            {
                AppleId = request.AppleId,
                Artist = request.Artist,
                Album = request.Album,
                Storefront = request.Storefront,
                MaxResolution = request.MaxResolution,
                Logger = request.Logger,
                CollectionType = request.CollectionType,
                CollectionId = request.CollectionId
            },
            cancellationToken);
        if (motion == null)
        {
            return new AnimatedArtworkSaveResult(
                existingPaths,
                "lookup_failed",
                "Apple animated artwork lookup did not return a usable resource.");
        }
        if (motion.NoArtwork)
        {
            return new AnimatedArtworkSaveResult(
                existingPaths,
                "no_animated_artwork",
                "The exact Apple catalog edition does not expose animated artwork.");
        }

        Directory.CreateDirectory(outputDir);
        var savedPaths = new List<string>(existingPaths);
        var baseName = BuildAnimatedArtworkBaseName(request.BaseFileName, request.Artist, request.Album);
        var outputFormats = ResolveAnimatedArtworkFormats(request.OutputFormats);
        var maxSizeBytes = ResolveAnimatedArtworkMaxSizeBytes(request);

        if (!string.IsNullOrWhiteSpace(motion.SquareUrl))
        {
            var squareSourcePath = await CacheAnimatedArtworkSourceAsync(
                httpClientFactory,
                motion.SquareUrl,
                request,
                "square",
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(squareSourcePath))
            {
                savedPaths.AddRange(await SaveAnimatedArtworkVariantAsync(
                    squareSourcePath,
                    Path.Join(outputDir, baseName),
                    outputFormats,
                    maxSizeBytes,
                    request.Logger,
                    cancellationToken));
            }
        }

        if (!string.IsNullOrWhiteSpace(motion.TallUrl))
        {
            var tallSourcePath = await CacheAnimatedArtworkSourceAsync(
                httpClientFactory,
                motion.TallUrl,
                request,
                "tall",
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(tallSourcePath))
            {
                savedPaths.AddRange(await SaveAnimatedArtworkVariantAsync(
                    tallSourcePath,
                    Path.Join(outputDir, $"{baseName}_tall"),
                    outputFormats,
                    maxSizeBytes,
                    request.Logger,
                    cancellationToken));
            }
        }

        var distinctSavedPaths = savedPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        await RegisterAnimatedArtworkArtifactsAsync(request, distinctSavedPaths, cancellationToken);
        await RegisterAnimatedArtworkConversionFailureAsync(
            request,
            motion,
            outputFormats,
            cancellationToken);
        var expectedOutputPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(motion.SquareUrl))
        {
            expectedOutputPaths.AddRange(outputFormats.Select(format =>
                Path.Join(outputDir, $"{baseName}.{format}")));
        }
        if (!string.IsNullOrWhiteSpace(motion.TallUrl))
        {
            expectedOutputPaths.AddRange(outputFormats.Select(format =>
                Path.Join(outputDir, $"{baseName}_tall.{format}")));
        }
        var conversionFailed = expectedOutputPaths.Any(path => !File.Exists(path));
        return new AnimatedArtworkSaveResult(
            distinctSavedPaths,
            conversionFailed ? "conversion_failure" : "resolved",
            conversionFailed
                ? "One or more requested animated artwork variants could not be converted."
                : "Animated artwork was resolved and saved.");
    }

    private static async Task RegisterAnimatedArtworkArtifactsAsync(
        AnimatedArtworkSaveRequest request,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0
            || string.IsNullOrWhiteSpace(request.CollectionType)
            || string.IsNullOrWhiteSpace(request.CollectionId))
        {
            return;
        }

        var cacheKey = $"apple:motion:{request.CollectionType}:{request.CollectionId}:{request.MaxResolution}";
        try
        {
            await AppleAnimatedArtworkCache.SaveArtifactsAsync(cacheKey, paths, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Logger.LogDebug(ex, "Persistent Apple animated artwork artifact cache write failed.");
        }
    }

    private static async Task RegisterAnimatedArtworkConversionFailureAsync(
        AnimatedArtworkSaveRequest request,
        AnimatedArtworkUrls motion,
        IReadOnlyCollection<string> outputFormats,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CollectionType)
            || string.IsNullOrWhiteSpace(request.CollectionId))
        {
            return;
        }

        var baseName = BuildAnimatedArtworkBaseName(request.BaseFileName, request.Artist, request.Album);
        var missingExpectedOutput = (!string.IsNullOrWhiteSpace(motion.SquareUrl)
                && outputFormats.Any(format => !File.Exists(Path.Join(request.OutputDir, $"{baseName}.{format}"))))
            || (!string.IsNullOrWhiteSpace(motion.TallUrl)
                && outputFormats.Any(format => !File.Exists(Path.Join(request.OutputDir, $"{baseName}_tall.{format}"))));
        if (!missingExpectedOutput)
        {
            return;
        }

        var cacheKey = $"apple:motion:{request.CollectionType}:{request.CollectionId}:{request.MaxResolution}";
        try
        {
            await AppleAnimatedArtworkCache.SaveStatusAsync(
                cacheKey,
                request.CollectionType,
                request.CollectionId,
                request.Storefront,
                request.MaxResolution,
                "conversion_failure",
                TimeSpan.FromMinutes(15),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Logger.LogDebug(ex, "Persistent Apple animated artwork conversion state write failed.");
        }
    }

    private static bool AreAllCanonicalAnimatedArtworkOutputsPresent(AnimatedArtworkSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputDir) || !Directory.Exists(request.OutputDir))
        {
            return false;
        }

        var baseName = BuildAnimatedArtworkBaseName(request.BaseFileName, request.Artist, request.Album);
        var outputFormats = ResolveAnimatedArtworkFormats(request.OutputFormats);
        var squarePaths = outputFormats
            .Select(format => Path.Join(request.OutputDir, $"{baseName}.{format}"))
            .ToList();
        if (!squarePaths.All(File.Exists))
        {
            return false;
        }

        var tallPaths = outputFormats
            .Select(format => Path.Join(request.OutputDir, $"{baseName}_tall.{format}"))
            .ToList();
        return !tallPaths.Any(File.Exists) || tallPaths.All(File.Exists);
    }

    public static async Task<IReadOnlyList<string>> SaveExistingAnimatedArtworkVariantsAsync(
        AnimatedArtworkSaveRequest request,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var outputDir = request.OutputDir;
        if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
        {
            return Array.Empty<string>();
        }

        var baseName = BuildAnimatedArtworkBaseName(request.BaseFileName, request.Artist, request.Album);
        var outputFormats = ResolveAnimatedArtworkFormats(request.OutputFormats);
        var savedPaths = new List<string>();
        foreach (var variant in new[] { "square", "tall" })
        {
            var outputBase = Path.Join(outputDir, variant == "square" ? baseName : $"{baseName}_tall");
            if (request.RenameExistingArtwork)
            {
                RenameLegacyAnimatedArtworkFiles(outputDir, baseName, variant, outputBase, logger);
            }

            var existingMp4 = $"{outputBase}.{AnimatedArtworkMp4}";
            if (File.Exists(existingMp4))
            {
                savedPaths.AddRange(await SaveAnimatedArtworkVariantAsync(
                    existingMp4,
                    outputBase,
                    outputFormats,
                    ResolveAnimatedArtworkMaxSizeBytes(request),
                    logger,
                    cancellationToken));
            }

            foreach (var format in outputFormats)
            {
                var existingOutput = $"{outputBase}.{format}";
                if (File.Exists(existingOutput))
                {
                    savedPaths.Add(existingOutput);
                }
            }
        }

        return savedPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void RenameLegacyAnimatedArtworkFiles(
        string outputDir,
        string baseName,
        string variant,
        string canonicalOutputBase,
        ILogger logger)
    {
        var legacySuffix = $"{variant}_animated_artwork";
        foreach (var sourcePath in Directory.EnumerateFiles(outputDir))
        {
            var extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            if (NormalizeAnimatedArtworkFormat(extension) == null)
            {
                continue;
            }

            var sourceStem = Path.GetFileNameWithoutExtension(sourcePath);
            var recognized = sourceStem.Equals(legacySuffix, StringComparison.OrdinalIgnoreCase)
                || sourceStem.Equals($"{baseName} - {legacySuffix}", StringComparison.OrdinalIgnoreCase)
                || sourceStem.EndsWith($" - {legacySuffix}", StringComparison.OrdinalIgnoreCase);
            if (!recognized)
            {
                continue;
            }

            var destinationPath = $"{canonicalOutputBase}.{extension}";
            if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(destinationPath))
            {
                if (FilesHaveSameContent(sourcePath, destinationPath))
                {
                    File.Delete(sourcePath);
                    continue;
                }

                logger.LogWarning(
                    "Animated artwork rename skipped because destination already exists: {SourcePath} -> {DestinationPath}",
                    LogSanitizer.OneLine(sourcePath),
                    LogSanitizer.OneLine(destinationPath));
                continue;
            }

            File.Move(sourcePath, destinationPath);
        }
    }

    private static bool FilesHaveSameContent(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var left = File.OpenRead(leftPath);
        using var right = File.OpenRead(rightPath);
        return SHA256.HashData(left).AsSpan().SequenceEqual(SHA256.HashData(right));
    }

    private static string ComputeSha256Hex(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static int ResolveAnimatedArtworkMaxSizeMb(DeezSpoTagSettings settings)
        => NormalizeAnimatedArtworkMaxSizeMb(settings.AnimatedArtworkMaxSizeMb);

    private static int NormalizeAnimatedArtworkMaxSizeMb(int configured)
        => configured <= 0 ? DefaultAnimatedArtworkMaxSizeMb : Math.Min(configured, 200);

    private static long ResolveAnimatedArtworkMaxSizeBytes(AnimatedArtworkSaveRequest request)
        => (long)NormalizeAnimatedArtworkMaxSizeMb(request.MaxSizeMb) * 1024L * 1024L;

    public static IReadOnlyList<string> ResolveAnimatedArtworkFormats(DeezSpoTagSettings settings)
        => ResolveAnimatedArtworkFormats((settings.AnimatedArtworkFormats ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static IReadOnlyList<string> ResolveAnimatedArtworkFormats(IReadOnlyCollection<string>? formats)
    {
        var resolved = (formats ?? Array.Empty<string>())
            .Select(NormalizeAnimatedArtworkFormat)
            .Where(static format => !string.IsNullOrWhiteSpace(format))
            .Select(static format => format!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (resolved.Count == 0)
        {
            resolved.Add(AnimatedArtworkMp4);
        }

        return resolved;
    }

    private static string? NormalizeAnimatedArtworkFormat(string? format)
    {
        var normalized = (format ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        return normalized switch
        {
            AnimatedArtworkMp4 => AnimatedArtworkMp4,
            AnimatedArtworkWebp => AnimatedArtworkWebp,
            AnimatedArtworkGif => AnimatedArtworkGif,
            _ => null
        };
    }

    private static async Task<string?> CacheAnimatedArtworkSourceAsync(
        IHttpClientFactory httpClientFactory,
        string sourceUrl,
        AnimatedArtworkSaveRequest request,
        string variant,
        CancellationToken cancellationToken)
    {
        var normalizedUrl = sourceUrl.Trim();
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            return null;
        }

        var cachePath = BuildAnimatedArtworkSourceCachePath(normalizedUrl, request, variant);
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
        {
            return cachePath;
        }

        var lazy = AnimatedArtworkSourceDownloads.GetOrAdd(
            normalizedUrl,
            _ => new Lazy<Task<string?>>(
                () => DownloadAnimatedArtworkSourceAsync(httpClientFactory, normalizedUrl, cachePath, request.Logger, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        finally
        {
            AnimatedArtworkSourceDownloads.TryRemove(
                new KeyValuePair<string, Lazy<Task<string?>>>(normalizedUrl, lazy));
        }
    }

    private static string BuildAnimatedArtworkSourceCachePath(
        string sourceUrl,
        AnimatedArtworkSaveRequest request,
        string variant)
    {
        var dataRoot = DeezSpoTagDataRootResolver.Resolve();
        var sourceRoot = Path.Join(dataRoot, "cache", "apple-motion", "source");
        var identity = string.Join(
            "|",
            sourceUrl,
            request.CollectionType ?? string.Empty,
            request.CollectionId ?? string.Empty,
            request.AppleId ?? string.Empty,
            request.MaxResolution.ToString(CultureInfo.InvariantCulture),
            variant);
        var fileName = $"{ComputeSha256Hex(identity)}.mp4";
        return Path.Join(sourceRoot, fileName);
    }

    private static async Task<string?> DownloadAnimatedArtworkSourceAsync(
        IHttpClientFactory httpClientFactory,
        string sourceUrl,
        string cachePath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? Path.GetTempPath());
            var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            using var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "Apple animated artwork source download failed with HTTP {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(temporaryPath))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length <= 0)
            {
                DeleteFileIfExists(temporaryPath);
                return null;
            }

            if (File.Exists(cachePath))
            {
                DeleteFileIfExists(temporaryPath);
                return cachePath;
            }

            File.Move(temporaryPath, cachePath);
            return cachePath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Apple animated artwork source cache write failed.");
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> SaveAnimatedArtworkVariantAsync(
        string inputPath,
        string outputPathWithoutExtension,
        IReadOnlyList<string> outputFormats,
        long maxSizeBytes,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var savedPaths = new List<string>();
        var conversions = new List<Task<string?>>();
        foreach (var format in outputFormats)
        {
            var outputPath = $"{outputPathWithoutExtension}.{format}";
            if (File.Exists(outputPath))
            {
                savedPaths.Add(outputPath);
                continue;
            }

            if (format.Equals(AnimatedArtworkMp4, StringComparison.OrdinalIgnoreCase)
                && TryCopyAnimatedArtworkMp4(inputPath, outputPath, maxSizeBytes, logger))
            {
                savedPaths.Add(outputPath);
                continue;
            }

            conversions.Add(RunAnimatedArtworkConversionCoalescedAsync(
                inputPath,
                outputPath,
                format,
                maxSizeBytes,
                logger,
                cancellationToken));
        }

        if (conversions.Count > 0)
        {
            var results = await Task.WhenAll(conversions);
            savedPaths.AddRange(results.Where(static path => !string.IsNullOrWhiteSpace(path))!);
        }

        return savedPaths;
    }

    private static bool TryCopyAnimatedArtworkMp4(
        string inputPath,
        string outputPath,
        long maxSizeBytes,
        ILogger logger)
    {
        try
        {
            if (!File.Exists(inputPath) || new FileInfo(inputPath).Length <= 0)
            {
                return false;
            }

            var extension = Path.GetExtension(inputPath).TrimStart('.');
            if (!extension.Equals(AnimatedArtworkMp4, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            if (maxSizeBytes > 0 && new FileInfo(inputPath).Length > maxSizeBytes)
            {
                logger.LogInformation(
                    "Animated artwork {Output} is {ActualBytes} bytes and exceeds the {BudgetBytes} byte budget.",
                    LogSanitizer.OneLine(Path.GetFileName(outputPath)),
                    new FileInfo(inputPath).Length,
                    maxSizeBytes);
            }

            File.Copy(inputPath, outputPath, overwrite: false);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogDebug(ex, "Apple animated artwork MP4 copy failed.");
            return false;
        }
    }

    private static async Task<string?> RunAnimatedArtworkConversionCoalescedAsync(
        string inputPath,
        string outputPath,
        string format,
        long maxSizeBytes,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var lazy = AnimatedArtworkConversions.GetOrAdd(
            outputPath,
            _ => new Lazy<Task<string?>>(
                () => RunFfmpegAnimatedArtworkAsync(inputPath, outputPath, format, maxSizeBytes, logger, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        finally
        {
            AnimatedArtworkConversions.TryRemove(
                new KeyValuePair<string, Lazy<Task<string?>>>(outputPath, lazy));
        }
    }

    private static async Task<AnimatedArtworkUrls?> ResolveAnimatedArtworkUrlsAsync(
        AppleMusicCatalogService appleCatalog,
        IHttpClientFactory httpClientFactory,
        AnimatedArtworkResolveRequest request,
        CancellationToken cancellationToken)
    {
        var appleId = request.AppleId;
        var artist = request.Artist;
        var album = request.Album;
        var storefront = request.Storefront;
        var maxResolution = request.MaxResolution;
        var logger = request.Logger;
        var collectionType = request.CollectionType;
        var collectionId = request.CollectionId;

        var motionFromCollection = await TryResolveCollectionMotionWithCacheAsync(
            appleCatalog,
            httpClientFactory,
            new CollectionMotionCacheRequest
            {
                CollectionType = collectionType,
                CollectionId = collectionId,
                Storefront = storefront,
                MaxResolution = maxResolution,
                Logger = logger
            },
            cancellationToken);
        if (motionFromCollection != null)
        {
            return motionFromCollection;
        }

        var songId = IsCollectionType(collectionType)
                     && !string.IsNullOrWhiteSpace(collectionId)
                     && string.Equals(appleId, collectionId, StringComparison.OrdinalIgnoreCase)
            ? null
            : appleId;
        if (string.IsNullOrWhiteSpace(songId))
        {
            return null;
        }

        var songCacheKey = $"apple:motion:song:{songId}:{maxResolution}";
        if (AppleArtworkCache.TryGetValue(songCacheKey, out AnimatedArtworkUrls? cachedSongMotion) && cachedSongMotion != null)
        {
            return cachedSongMotion;
        }

        var motion = await ResolveAnimatedArtworkFromSongAsync(
            appleCatalog,
            httpClientFactory,
            new AnimatedArtworkFromSongRequest
            {
                SongId = songId,
                ExpectedArtist = artist,
                ExpectedAlbum = album,
                Storefront = storefront,
                MaxResolution = maxResolution,
                Logger = logger
            },
            cancellationToken);
        if (motion == null)
        {
            return null;
        }

        CacheArtworkValue(songCacheKey, motion, TimeSpan.FromMinutes(30));
        return motion;
    }

    private static async Task<AnimatedArtworkUrls?> TryResolveCollectionMotionWithCacheAsync(
        AppleMusicCatalogService appleCatalog,
        IHttpClientFactory httpClientFactory,
        CollectionMotionCacheRequest request,
        CancellationToken cancellationToken)
    {
        var collectionType = request.CollectionType;
        var collectionId = request.CollectionId;
        var storefront = request.Storefront;
        var maxResolution = request.MaxResolution;
        var logger = request.Logger;

        if (string.IsNullOrWhiteSpace(collectionType) || string.IsNullOrWhiteSpace(collectionId))
        {
            return null;
        }

        var cacheKey = $"apple:motion:{collectionType}:{collectionId}:{maxResolution}";
        if (AppleArtworkCache.TryGetValue(cacheKey, out AnimatedArtworkUrls? cachedMotion) && cachedMotion != null)
        {
            return cachedMotion;
        }

        try
        {
            var persistent = await AppleAnimatedArtworkCache.TryGetAsync(cacheKey, cancellationToken);
            if (persistent != null)
            {
                var cached = new AnimatedArtworkUrls(
                    persistent.Value.SquareUrl,
                    persistent.Value.TallUrl,
                    persistent.Value.Status.Equals("no_animated_artwork", StringComparison.OrdinalIgnoreCase));
                CacheArtworkValue(cacheKey, cached, TimeSpan.FromMinutes(30));
                return cached;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Persistent Apple animated artwork cache read failed.");
        }

        var lazyLookup = AnimatedArtworkLookups.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<AnimatedArtworkUrls?>>(
                () => TryResolveCollectionMotionAsync(
                    appleCatalog,
                    httpClientFactory,
                    new CollectionMotionResolveRequest
                    {
                        CollectionType = collectionType,
                        CollectionId = collectionId,
                        Storefront = storefront,
                        MaxResolution = maxResolution,
                        Logger = logger
                    },
                    cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        AnimatedArtworkUrls? motion;
        try
        {
            motion = await lazyLookup.Value;
        }
        finally
        {
            AnimatedArtworkLookups.TryRemove(
                new KeyValuePair<string, Lazy<Task<AnimatedArtworkUrls?>>>(cacheKey, lazyLookup));
        }

        if (motion != null)
        {
            CacheArtworkValue(cacheKey, motion, TimeSpan.FromMinutes(30));
            try
            {
                if (motion.NoArtwork)
                {
                    await AppleAnimatedArtworkCache.SaveNoArtworkAsync(
                        cacheKey,
                        collectionType,
                        collectionId,
                        storefront,
                        maxResolution,
                        cancellationToken);
                }
                else
                {
                    await AppleAnimatedArtworkCache.SaveResolvedAsync(
                        cacheKey,
                        collectionType,
                        collectionId,
                        storefront,
                        maxResolution,
                        motion.SquareUrl,
                        motion.TallUrl,
                        cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Persistent Apple animated artwork cache write failed.");
            }
        }

        return motion;
    }

    private static async Task<AnimatedArtworkUrls?> ResolveAnimatedArtworkFromSongAsync(
        AppleMusicCatalogService appleCatalog,
        IHttpClientFactory httpClientFactory,
        AnimatedArtworkFromSongRequest request,
        CancellationToken cancellationToken)
    {
        var songId = request.SongId;
        var expectedArtist = request.ExpectedArtist;
        var expectedAlbum = request.ExpectedAlbum;
        var storefront = request.Storefront;
        var maxResolution = request.MaxResolution;
        var logger = request.Logger;

        var albumId = await TryResolveAnimatedArtworkAlbumIdAsync(
            appleCatalog,
            songId,
            expectedArtist,
            expectedAlbum,
            storefront,
            logger,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return null;
        }

        var videos = await TryResolveAnimatedArtworkVideosAsync(
            appleCatalog,
            albumId,
            storefront,
            logger,
            cancellationToken);
        if (videos == null)
        {
            return null;
        }

        var squareUrl = await ExtractMotionVariantAsync(httpClientFactory, videos.Value.Square, maxResolution, cancellationToken);
        var tallUrl = await ExtractMotionVariantAsync(httpClientFactory, videos.Value.Tall, maxResolution, cancellationToken);
        if (string.IsNullOrWhiteSpace(squareUrl) && string.IsNullOrWhiteSpace(tallUrl))
        {
            return null;
        }

        return new AnimatedArtworkUrls(squareUrl, tallUrl);
    }

    private static async Task<string?> TryResolveAnimatedArtworkAlbumIdAsync(
        AppleMusicCatalogService appleCatalog,
        string songId,
        string? expectedArtist,
        string? expectedAlbum,
        string storefront,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using var songDoc = await appleCatalog.GetSongAsync(songId, storefront, DefaultLanguage, cancellationToken);
            return ExtractAlbumId(songDoc.RootElement, expectedArtist, expectedAlbum);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Apple animated artwork song lookup failed.");
            return null;
        }
    }

    private static async Task<(string? Square, string? Tall)?> TryResolveAnimatedArtworkVideosAsync(
        AppleMusicCatalogService appleCatalog,
        string albumId,
        string storefront,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using var albumDoc = await appleCatalog.GetAlbumAsync(albumId, storefront, DefaultLanguage, cancellationToken);
            var videos = ExtractMotionVideos(albumDoc.RootElement);
            if (string.IsNullOrWhiteSpace(videos.Square) && string.IsNullOrWhiteSpace(videos.Tall))
            {
                return null;
            }

            return videos;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Apple animated artwork album lookup failed.");
            return null;
        }
    }

    private static bool IsCollectionType(string? collectionType)
    {
        if (string.IsNullOrWhiteSpace(collectionType))
        {
            return false;
        }

        return collectionType.Equals("album", StringComparison.OrdinalIgnoreCase)
            || collectionType.Equals("playlist", StringComparison.OrdinalIgnoreCase)
            || collectionType.Equals("station", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractAlbumId(JsonElement root, string? expectedArtist, string? expectedAlbum)
    {
        if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            return null;
        }

        var data = dataArr[0];
        if (!IsSongMetadataCompatible(data, expectedArtist, expectedAlbum))
        {
            return null;
        }

        if (!data.TryGetProperty("relationships", out var rel) || rel.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!rel.TryGetProperty("albums", out var albums) || albums.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!albums.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array || dataEl.GetArrayLength() == 0)
        {
            return null;
        }

        return dataEl[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
    }

    private static bool IsSongMetadataCompatible(JsonElement songData, string? expectedArtist, string? expectedAlbum)
    {
        var normalizedExpectedArtist = string.IsNullOrWhiteSpace(expectedArtist)
            ? string.Empty
            : NormalizeLookupToken(expectedArtist);
        var normalizedExpectedAlbum = string.IsNullOrWhiteSpace(expectedAlbum)
            ? string.Empty
            : NormalizeLookupToken(expectedAlbum);
        if (string.IsNullOrWhiteSpace(normalizedExpectedArtist) && string.IsNullOrWhiteSpace(normalizedExpectedAlbum))
        {
            return true;
        }

        if (!songData.TryGetProperty(AttributesKey, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var candidateArtist = attrs.TryGetProperty(ArtistNameKey, out var artistEl) && artistEl.ValueKind == JsonValueKind.String
            ? NormalizeLookupToken(artistEl.GetString() ?? string.Empty)
            : string.Empty;
        var candidateAlbum = attrs.TryGetProperty(AlbumNameKey, out var albumEl) && albumEl.ValueKind == JsonValueKind.String
            ? NormalizeLookupToken(albumEl.GetString() ?? string.Empty)
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(normalizedExpectedArtist)
            && !IsLikelySameArtist(normalizedExpectedArtist, candidateArtist))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(normalizedExpectedAlbum)
            && !IsLikelySameArtist(normalizedExpectedAlbum, candidateAlbum))
        {
            return false;
        }

        return true;
    }

    private static (string? Square, string? Tall) ExtractMotionVideos(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            return (null, null);
        }

        var data = dataArr[0];
        if (!data.TryGetProperty(AttributesKey, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return ExtractMotionVideosFromAttributes(attrs);
    }

    private static (string? Square, string? Tall) ExtractMotionVideosFromAttributes(JsonElement attrs)
    {
        var square = TryExtractNestedVideo(attrs, "editorialVideo", "motionDetailSquare")
            ?? TryExtractVideo(attrs, "motionSquareVideo1x1")
            ?? TryExtractVideo(attrs, "motionDetailSquare");
        var tall = TryExtractNestedVideo(attrs, "editorialVideo", "motionDetailTall")
            ?? TryExtractVideo(attrs, "motionTallVideo3x4")
            ?? TryExtractVideo(attrs, "motionDetailTall");
        return (square, tall);
    }

    private sealed class ItunesLookupContext
    {
        public string Term { get; init; } = string.Empty;
        public string NormalizedArtist { get; init; } = string.Empty;
        public string NormalizedAlbum { get; init; } = string.Empty;
        public bool AlbumRequested { get; init; }
    }

    private static ItunesLookupContext? BuildItunesLookupContext(string? title, string? artist, string? album)
    {
        var termParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist))
        {
            termParts.Add(artist);
        }
        if (!string.IsNullOrWhiteSpace(album))
        {
            termParts.Add(album);
        }
        if (termParts.Count == 0 && !string.IsNullOrWhiteSpace(title))
        {
            termParts.Add(title);
        }
        if (termParts.Count == 0)
        {
            return null;
        }

        var normalizedArtist = string.IsNullOrWhiteSpace(artist) ? string.Empty : NormalizeLookupToken(artist);
        var normalizedAlbum = string.IsNullOrWhiteSpace(album) ? string.Empty : NormalizeLookupToken(album);
        return new ItunesLookupContext
        {
            Term = string.Join(" ", termParts),
            NormalizedArtist = normalizedArtist,
            NormalizedAlbum = normalizedAlbum,
            AlbumRequested = !string.IsNullOrWhiteSpace(normalizedAlbum)
        };
    }

    private static string? SelectItunesArtworkRaw(
        JsonElement results,
        string normalizedArtist,
        string normalizedAlbum,
        bool albumRequested)
    {
        string? artistOnlyRaw = null;
        string? fallbackRaw = null;
        foreach (var entry in results.EnumerateArray().Where(HasItunesArtwork))
        {
            var raw = entry.GetProperty("artworkUrl100").GetString()!;
            fallbackRaw ??= raw;

            var (artistMatches, albumMatches) = EvaluateItunesCandidate(entry, normalizedArtist, normalizedAlbum);
            if (artistMatches && albumRequested && albumMatches)
            {
                return raw;
            }

            if (!albumRequested && artistMatches && string.IsNullOrWhiteSpace(artistOnlyRaw))
            {
                artistOnlyRaw = raw;
            }
        }

        if (albumRequested)
        {
            return null;
        }

        return artistOnlyRaw ?? fallbackRaw;
    }

    private static bool HasItunesArtwork(JsonElement entry)
    {
        return entry.TryGetProperty("artworkUrl100", out var art)
            && art.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(art.GetString());
    }

    private static (bool ArtistMatches, bool AlbumMatches) EvaluateItunesCandidate(
        JsonElement entry,
        string normalizedArtist,
        string normalizedAlbum)
    {
        var candidateArtist = TryReadItunesString(entry, ArtistNameKey)
            ?? TryReadItunesString(entry, "collectionArtistName");
        var candidateAlbum = TryReadItunesString(entry, "collectionName");
        var normalizedCandidateArtist = string.IsNullOrWhiteSpace(candidateArtist)
            ? string.Empty
            : NormalizeLookupToken(candidateArtist);
        var normalizedCandidateAlbum = string.IsNullOrWhiteSpace(candidateAlbum)
            ? string.Empty
            : NormalizeLookupToken(candidateAlbum);

        var artistMatches = string.IsNullOrWhiteSpace(normalizedArtist)
            || IsLikelySameArtist(normalizedArtist, normalizedCandidateArtist);
        var albumMatches = string.IsNullOrWhiteSpace(normalizedAlbum)
            || IsLikelySameArtist(normalizedAlbum, normalizedCandidateAlbum);
        return (artistMatches, albumMatches);
    }

    private static void CacheArtworkValue<T>(string cacheKey, T value, TimeSpan duration)
    {
        AppleArtworkCache.Set(cacheKey, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = duration,
            Size = 1
        });
    }

    private static bool TryGetRelationshipsObject(JsonElement root, out JsonElement rel)
    {
        rel = default;
        if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            return false;
        }

        var data = dataArr[0];
        if (!data.TryGetProperty("relationships", out rel) || rel.ValueKind != JsonValueKind.Object)
        {
            rel = default;
            return false;
        }

        return true;
    }

    private static string? TryExtractNestedVideo(JsonElement attrs, string containerProperty, string nestedProperty)
    {
        if (!attrs.TryGetProperty(containerProperty, out var container) || container.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryExtractVideo(container, nestedProperty);
    }

    private static string? TryExtractVideo(JsonElement container, string property)
    {
        if (!container.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!node.TryGetProperty(VideoType, out var videoNode) || videoNode.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return videoNode.GetString();
    }

    private static async Task<AnimatedArtworkUrls?> TryResolveCollectionMotionAsync(
        AppleMusicCatalogService appleCatalog,
        IHttpClientFactory httpClientFactory,
        CollectionMotionResolveRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonDocument? doc = request.CollectionType.ToLowerInvariant() switch
            {
                "playlist" => await appleCatalog.GetPlaylistAsync(request.CollectionId, request.Storefront, DefaultLanguage, cancellationToken),
                "station" => await appleCatalog.GetStationAsync(request.CollectionId, request.Storefront, DefaultLanguage, cancellationToken),
                "album" => await appleCatalog.GetAlbumAsync(request.CollectionId, request.Storefront, DefaultLanguage, cancellationToken),
                _ => null
            };

            if (doc == null)
            {
                return null;
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("data", out var dataArr)
                    || dataArr.ValueKind != JsonValueKind.Array
                    || dataArr.GetArrayLength() == 0)
                {
                    return null;
                }

                var data = dataArr[0];
                if (!data.TryGetProperty(AttributesKey, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var (squareVideo, tallVideo) = ExtractMotionVideosFromAttributes(attrs);
                if (string.IsNullOrWhiteSpace(squareVideo) && string.IsNullOrWhiteSpace(tallVideo))
                {
                    return new AnimatedArtworkUrls(null, null, NoArtwork: true);
                }

                var squareUrl = await ExtractMotionVariantAsync(httpClientFactory, squareVideo, request.MaxResolution, cancellationToken);
                var tallUrl = await ExtractMotionVariantAsync(httpClientFactory, tallVideo, request.MaxResolution, cancellationToken);
                if (string.IsNullOrWhiteSpace(squareUrl) && string.IsNullOrWhiteSpace(tallUrl))
                {
                    return null;
                }

                return new AnimatedArtworkUrls(squareUrl, tallUrl);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Logger.LogDebug(ex, "Apple animated artwork collection lookup failed.");
            var status = ex is HttpRequestException httpException
                && (httpException.StatusCode == HttpStatusCode.TooManyRequests
                    || httpException.StatusCode == HttpStatusCode.Forbidden)
                ? "rate_limited"
                : "transient_failure";
            var lifetime = status == "rate_limited" ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(15);
            var cacheKey = $"apple:motion:{request.CollectionType}:{request.CollectionId}:{request.MaxResolution}";
            try
            {
                await AppleAnimatedArtworkCache.SaveStatusAsync(
                    cacheKey,
                    request.CollectionType,
                    request.CollectionId,
                    request.Storefront,
                    request.MaxResolution,
                    status,
                    lifetime,
                    cancellationToken);
            }
            catch (Exception cacheException) when (cacheException is not OperationCanceledException)
            {
                request.Logger.LogDebug(cacheException, "Persistent Apple animated artwork failure state write failed.");
            }
            return null;
        }
    }

    private static async Task<string?> ExtractMotionVariantAsync(
        IHttpClientFactory httpClientFactory,
        string? masterUrl,
        int maxResolution,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(masterUrl))
        {
            return null;
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            var masterText = await client.GetStringAsync(masterUrl, cancellationToken);
            var master = AppleHlsManifestParser.ParseMaster(masterText, new Uri(masterUrl));
            var best = SelectVariantByResolution(master, maxResolution);
            return best?.Uri;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static AppleHlsVariantEntry? SelectVariantByResolution(AppleHlsMasterManifest master, int maxResolution)
    {
        if (master.Variants.Count == 0)
        {
            return null;
        }

        var ordered = master.Variants
            .OrderByDescending(v => v.AverageBandwidth > 0 ? v.AverageBandwidth : v.Bandwidth)
            .ToList();

        if (maxResolution <= 0)
        {
            return ordered.FirstOrDefault();
        }

        return ordered.FirstOrDefault(variant =>
            TryParseResolutionHeight(variant.Resolution, out var height) && height <= maxResolution);
    }

    private static bool TryParseResolutionHeight(string resolution, out int height)
    {
        height = 0;
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return false;
        }

        var parts = resolution.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        return int.TryParse(parts[1], out height);
    }

    private static async Task<string?> RunFfmpegAnimatedArtworkAsync(
        string inputUrl,
        string outputPath,
        string format,
        long maxSizeBytes,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(FfmpegExecutable))
        {
            return null;
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var ladder = GetAnimatedArtworkEncodeLadder(format);
        string? bestPath = null;
        var bestLength = long.MaxValue;
        try
        {
            for (var index = 0; index < ladder.Count; index++)
            {
                var candidatePath = Path.Join(
                    outputDirectory ?? Path.GetTempPath(),
                    $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(outputPath)}");
                var length = await RunSingleAnimatedArtworkEncodeAsync(
                    inputUrl,
                    candidatePath,
                    format,
                    ladder[index],
                    logger,
                    cancellationToken);
                if (length <= 0)
                {
                    DeleteFileIfExists(candidatePath);
                    continue;
                }

                if (length < bestLength)
                {
                    DeleteFileIfExists(bestPath);
                    bestPath = candidatePath;
                    bestLength = length;
                }
                else
                {
                    DeleteFileIfExists(candidatePath);
                }

                if (maxSizeBytes <= 0 || length <= maxSizeBytes)
                {
                    break;
                }

                if (index == ladder.Count - 1)
                {
                    logger.LogInformation(
                        "Animated artwork {Output} is {ActualBytes} bytes after the smallest encode step and still exceeds the {BudgetBytes} byte budget.",
                        LogSanitizer.OneLine(Path.GetFileName(outputPath)),
                        bestLength,
                        maxSizeBytes);
                }
            }

            if (bestPath == null)
            {
                return null;
            }

            if (File.Exists(outputPath))
            {
                return outputPath;
            }

            File.Move(bestPath, outputPath);
            bestPath = null;
            return outputPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "ffmpeg animated artwork conversion failed.");
            return null;
        }
        finally
        {
            DeleteFileIfExists(bestPath);
        }
    }

    private static void DeleteFileIfExists(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task<long> RunSingleAnimatedArtworkEncodeAsync(
        string inputUrl,
        string temporaryPath,
        string format,
        AnimatedArtworkEncodeRung rung,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputUrl);
        AddAnimatedArtworkFfmpegArguments(startInfo, format, rung);
        startInfo.ArgumentList.Add(temporaryPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            logger.LogDebug(
                "ffmpeg animated artwork encode failed for {Format} at width {MaxWidth}: {Error}",
                format,
                rung.MaxWidth,
                LogSanitizer.OneLine(stderr.Trim()));
            return 0;
        }

        return File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
    }

    private static void AddAnimatedArtworkFfmpegArguments(
        ProcessStartInfo startInfo,
        string format,
        AnimatedArtworkEncodeRung rung)
    {
        switch (format)
        {
            case AnimatedArtworkWebp:
                startInfo.ArgumentList.Add("-an");
                startInfo.ArgumentList.Add("-vf");
                startInfo.ArgumentList.Add($"fps={rung.Fps},{BuildAnimatedArtworkScaleFilter(rung.MaxWidth)}");
                startInfo.ArgumentList.Add("-loop");
                startInfo.ArgumentList.Add("0");
                startInfo.ArgumentList.Add("-c:v");
                startInfo.ArgumentList.Add("libwebp");
                startInfo.ArgumentList.Add("-quality");
                startInfo.ArgumentList.Add(rung.Quality.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("-compression_level");
                startInfo.ArgumentList.Add("4");
                break;

            case AnimatedArtworkGif:
                startInfo.ArgumentList.Add("-an");
                startInfo.ArgumentList.Add("-vf");
                startInfo.ArgumentList.Add(
                    $"fps={rung.Fps},{BuildAnimatedArtworkScaleFilter(rung.MaxWidth)},split[a][b];"
                    + "[a]palettegen=stats_mode=diff[p];"
                    + $"[b][p]paletteuse=dither={rung.GifDither}:diff_mode=rectangle");
                startInfo.ArgumentList.Add("-loop");
                startInfo.ArgumentList.Add("0");
                break;

            default:
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("copy");
                break;
        }
    }

    private static string BuildAnimatedArtworkScaleFilter(int maxWidth)
        => maxWidth <= 0
            ? "scale=iw:-2:flags=lanczos"
            : $"scale='min(iw,{maxWidth.ToString(CultureInfo.InvariantCulture)})':-2:flags=lanczos";

    private sealed record AnimatedArtworkEncodeRung(int MaxWidth, int Quality, int Fps, string GifDither = "none");

    private static IReadOnlyList<AnimatedArtworkEncodeRung> GetAnimatedArtworkEncodeLadder(string format)
        => format switch
        {
            AnimatedArtworkWebp =>
            [
                new AnimatedArtworkEncodeRung(0, 90, 15),
                new AnimatedArtworkEncodeRung(1000, 85, 15),
                new AnimatedArtworkEncodeRung(800, 80, 15),
                new AnimatedArtworkEncodeRung(640, 75, 15),
                new AnimatedArtworkEncodeRung(512, 70, 12),
                new AnimatedArtworkEncodeRung(400, 65, 10)
            ],
            AnimatedArtworkGif =>
            [
                new AnimatedArtworkEncodeRung(0, 0, 12, "sierra2_4a"),
                new AnimatedArtworkEncodeRung(800, 0, 12, "bayer:bayer_scale=5"),
                new AnimatedArtworkEncodeRung(640, 0, 12, "bayer:bayer_scale=5"),
                new AnimatedArtworkEncodeRung(512, 0, 10, "bayer:bayer_scale=5"),
                new AnimatedArtworkEncodeRung(400, 0, 10, "none"),
                new AnimatedArtworkEncodeRung(320, 0, 8, "none")
            ],
            _ => [new AnimatedArtworkEncodeRung(0, 0, 0)]
        };

    private static string? ResolveExecutablePath(IEnumerable<string> candidates)
        => DownloadFileUtilities.ResolveExecutablePath(candidates, "DEEZSPOTAG_FFMPEG_PATH");

    private static string BuildAnimatedArtworkBaseName(string? baseFileName, string? artist, string? album)
    {
        if (!string.IsNullOrWhiteSpace(baseFileName))
        {
            return SanitizeFileNameSegment(baseFileName.Trim());
        }

        var safeArtist = SanitizeFileNameSegment(string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist.Trim());
        var safeAlbum = SanitizeFileNameSegment(string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album.Trim());
        return $"{safeArtist} - {safeAlbum}";
    }

    private static string SanitizeFileNameSegment(string value)
    {
        return DownloadFileUtilities.SanitizeFilename(value, UnknownValue);
    }

    private sealed record AnimatedArtworkUrls(string? SquareUrl, string? TallUrl, bool NoArtwork = false);
}
