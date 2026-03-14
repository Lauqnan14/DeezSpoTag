using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;

namespace DeezSpoTag.Services.Download.Utils;

public static class ArtworkFallbackHelper
{
    private const string AppleProvider = "apple";
    private const string DeezerProvider = "deezer";
    private const string SpotifyProvider = "spotify";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex AppleTrackUrlRegex = new(
        @"music\.apple\.com\/[^\/]+\/(?:song|album)\/[^\/]+\/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex AppleQueryIdRegex = new(
        @"(?:[?&]i=)(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);
    private static readonly string[] DefaultArtworkOrder = [AppleProvider, DeezerProvider, SpotifyProvider];
    private static readonly string[] CompilationAlbumMarkers =
    {
        "greatest hits",
        "best of",
        "compilation",
        "essentials",
        "anthology",
        "collection",
        "top hits",
        "playlist",
        "mix",
        "songs of",
        "ultimate hits"
    };

    public static IReadOnlyList<string> ResolveOrder(DeezSpoTagSettings settings)
    {
        return ResolveOrderInternal(settings.ArtworkFallbackEnabled, settings.ArtworkFallbackOrder);
    }

    public static IReadOnlyList<string> ResolveArtistOrder(DeezSpoTagSettings settings)
    {
        var artistOrder = string.IsNullOrWhiteSpace(settings.ArtistArtworkFallbackOrder)
            ? settings.ArtworkFallbackOrder
            : settings.ArtistArtworkFallbackOrder;
        return ResolveOrderInternal(settings.ArtistArtworkFallbackEnabled, artistOrder);
    }

    public sealed record AppleCoverLookupRequest(
        DeezSpoTagSettings Settings,
        string? AppleId,
        string? Title,
        string? Artist,
        string? Album);

    public static string? ResolveAlbumConstraintForArtwork(string? albumTitle)
    {
        return IsCompilationLikeAlbumTitle(albumTitle) ? null : albumTitle;
    }

    public static bool ShouldRejectCompilationArtworkCandidate(string? requestedAlbumTitle, string? candidateAlbumTitle)
    {
        return !string.IsNullOrWhiteSpace(requestedAlbumTitle)
            && !IsCompilationLikeAlbumTitle(requestedAlbumTitle)
            && IsCompilationLikeAlbumTitle(candidateAlbumTitle);
    }

    public static bool ShouldRejectAlbumArtworkCandidate(string? requestedAlbumTitle, string? candidateAlbumTitle)
    {
        if (string.IsNullOrWhiteSpace(requestedAlbumTitle) || string.IsNullOrWhiteSpace(candidateAlbumTitle))
        {
            return false;
        }

        if (ShouldRejectCompilationArtworkCandidate(requestedAlbumTitle, candidateAlbumTitle))
        {
            return true;
        }

        var requested = NormalizeAlbumIdentity(requestedAlbumTitle);
        var candidate = NormalizeAlbumIdentity(candidateAlbumTitle);
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (string.Equals(requested, candidate, StringComparison.Ordinal))
        {
            return false;
        }

        if (requested.Contains(candidate, StringComparison.Ordinal) || candidate.Contains(requested, StringComparison.Ordinal))
        {
            return false;
        }

        var requestedTokens = requested
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsAlbumIdentityToken)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var candidateTokens = candidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsAlbumIdentityToken)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requestedTokens.Length == 0 || candidateTokens.Length == 0)
        {
            return false;
        }

        var overlap = requestedTokens.Intersect(candidateTokens, StringComparer.Ordinal).Count();
        if (overlap == 0)
        {
            return true;
        }

        var minTokenCount = Math.Min(requestedTokens.Length, candidateTokens.Length);
        return overlap < minTokenCount;
    }

    public static async Task<string?> TryResolvePreferredArtworkTrackIdAsync(
        DeezerClient deezerClient,
        Track track,
        string? albumConstraint,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(track.MainArtist?.Name) || string.IsNullOrWhiteSpace(track.Title))
            {
                return null;
            }

            var durationMs = track.Duration > 0 ? track.Duration * 1000 : (int?)null;
            var resolvedTrackId = await deezerClient.GetTrackIdFromMetadataAsync(
                track.MainArtist.Name,
                track.Title,
                albumConstraint ?? string.Empty,
                durationMs);

            if (string.IsNullOrWhiteSpace(resolvedTrackId) || string.Equals(resolvedTrackId, "0", StringComparison.Ordinal))
            {
                return null;
            }

            var currentDeezerTrackId = TryExtractDeezerTrackId(track);
            if (string.Equals(resolvedTrackId, currentDeezerTrackId, StringComparison.Ordinal))
            {
                return null;
            }

            logger.LogInformation(
                "Album-art resolver switched away from compilation-like album for track {TrackId}; using Deezer track {ResolvedTrackId}",
                track.Id,
                resolvedTrackId);
            return resolvedTrackId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resolve preferred non-compilation artwork track for {TrackId}", track.Id);
            return null;
        }
    }

    public static bool ShouldUseDeezerPayloadCoverFallback(string? deezerTrackId, string? requestedAlbumTitle)
    {
        if (string.IsNullOrWhiteSpace(deezerTrackId))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(requestedAlbumTitle)
            || IsCompilationLikeAlbumTitle(requestedAlbumTitle);
    }

    public static bool IsCompilationLikeAlbum(Album? album)
    {
        if (album == null)
        {
            return false;
        }

        var recordType = album.RecordType?.Trim();
        if (string.Equals(recordType, "compile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(recordType, "compilation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(album.MainArtist?.Name)
            && string.Equals(album.MainArtist.Name.Trim(), "Various Artists", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsCompilationLikeAlbumTitle(album.Title);
    }

    private static List<string> ResolveOrderInternal(bool enabled, string? orderSetting)
    {
        return ProviderOrderResolver.Resolve(
            enabled,
            orderSetting,
            DefaultArtworkOrder,
            NormalizeArtworkProviderToken);
    }

    private static string NormalizeArtworkProviderToken(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "itunes" => AppleProvider,
            "applemusic" => AppleProvider,
            "apple-music" => AppleProvider,
            "apple_music" => AppleProvider,
            "apple music" => AppleProvider,
            _ => normalized
        };
    }

    public static async Task<string?> TryResolveAppleCoverAsync(
        AppleMusicCatalogService? appleCatalog,
        IHttpClientFactory? httpClientFactory,
        AppleCoverLookupRequest request,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var settings = request.Settings;
        var appleId = request.AppleId;
        var title = request.Title;
        var artist = request.Artist;
        var album = ResolveAlbumConstraintForArtwork(request.Album);
        var appleArtworkSize = AppleQueueHelpers.GetAppleArtworkSize(settings);
        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
            ? "us"
            : settings.AppleMusic!.Storefront;

        if (appleCatalog != null)
        {
            var catalogCover = await AppleQueueHelpers.ResolveAppleCoverFromCatalogAsync(
                appleCatalog,
                new AppleQueueHelpers.AppleCatalogCoverLookup
                {
                    AppleId = appleId,
                    Title = title,
                    Artist = artist,
                    Album = album,
                    Storefront = storefront,
                    Size = appleArtworkSize,
                    Logger = logger
                },
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(catalogCover))
            {
                return catalogCover;
            }
        }

        if (httpClientFactory == null)
        {
            return null;
        }

        return await AppleQueueHelpers.ResolveAppleCoverAsync(
            httpClientFactory,
            title,
            artist,
            album,
            appleArtworkSize,
            logger,
            cancellationToken);
    }

    public static async Task<string?> TryResolveSpotifyCoverAsync(
        ISpotifyIdResolver? spotifyIdResolver,
        ISpotifyArtworkResolver? spotifyArtworkResolver,
        string? title,
        string? artist,
        string? album,
        string? isrc,
        CancellationToken cancellationToken)
    {
        if (spotifyIdResolver == null || spotifyArtworkResolver == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        album = ResolveAlbumConstraintForArtwork(album);
        var spotifyId = await spotifyIdResolver.ResolveTrackIdAsync(
            title,
            artist,
            album,
            isrc,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return null;
        }

        return await spotifyArtworkResolver.ResolveAlbumCoverUrlAsync(spotifyId, cancellationToken, album);
    }

    public static async Task<string?> TryResolveDeezerCoverAsync(
        DeezerClient? deezerClient,
        string? deezerTrackId,
        int size,
        ILogger logger,
        CancellationToken cancellationToken,
        string? requestedAlbumTitle = null)
    {
        _ = cancellationToken;
        if (deezerClient == null)
        {
            return null;
        }

        var normalizedTrackId = TryExtractNumericId(deezerTrackId);
        if (string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            return null;
        }

        try
        {
            var apiTrack = await deezerClient.GetTrackAsync(normalizedTrackId);
            if (ShouldRejectAlbumArtworkCandidate(requestedAlbumTitle, apiTrack.Album?.Title)
                || (!string.IsNullOrWhiteSpace(requestedAlbumTitle)
                    && !IsCompilationLikeAlbumTitle(requestedAlbumTitle)
                    && IsCompilationLikeApiAlbum(apiTrack.Album)))
            {
                logger.LogDebug(
                    "Rejected Deezer artwork for track {TrackId}: resolved album '{ResolvedAlbum}' did not match requested album '{RequestedAlbum}'.",
                    normalizedTrackId,
                    apiTrack.Album?.Title,
                    requestedAlbumTitle);
                return null;
            }

            var imageSize = size > 0 ? size : 1200;
            return BuildDeezerCoverImageUrl(
                apiTrack.Album?.Md5Image ?? apiTrack.Md5Image,
                imageSize,
                apiTrack.Album?.CoverXl,
                apiTrack.Album?.CoverBig,
                apiTrack.Album?.CoverMedium,
                apiTrack.Album?.CoverSmall,
                apiTrack.Album?.Cover);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Deezer cover lookup failed for track {TrackId}", normalizedTrackId);
            return null;
        }
    }

    public static async Task<string?> TryResolveAppleArtistImageAsync(
        AppleMusicCatalogService? appleCatalog,
        IHttpClientFactory? httpClientFactory,
        DeezSpoTagSettings settings,
        string? appleTrackId,
        string? artist,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        if (appleCatalog != null && !string.IsNullOrWhiteSpace(appleTrackId))
        {
            var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
            var trackLinkedArtworkSize = AppleQueueHelpers.GetAppleArtworkSize(settings);
            var fromSong = await AppleQueueHelpers.ResolveAppleArtistImageFromSongAsync(
                appleCatalog,
                appleTrackId,
                storefront,
                trackLinkedArtworkSize,
                logger,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(fromSong))
            {
                return fromSong;
            }
        }

        if (httpClientFactory == null)
        {
            return null;
        }

        var appleArtworkSize = AppleQueueHelpers.GetAppleArtworkSize(settings);
        return await AppleQueueHelpers.ResolveItunesArtistImageAsync(
            httpClientFactory,
            artist,
            appleArtworkSize,
            logger,
            cancellationToken);
    }

    public static string? TryExtractDeezerTrackId(Track? track)
    {
        if (track == null)
        {
            return null;
        }

        return TrackIdNormalization.TryResolveDeezerTrackId(track, out var deezerTrackId)
            ? deezerTrackId
            : null;
    }

    public static async Task<string?> TryResolveDeezerArtistImageAsync(
        DeezerClient? deezerClient,
        string? deezerTrackId,
        int size,
        ILogger logger,
        CancellationToken cancellationToken,
        string? artistName = null)
    {
        _ = cancellationToken;
        if (deezerClient == null)
        {
            return null;
        }

        var imageSize = size > 0 ? size : 1200;
        var normalizedTrackId = TryExtractNumericId(deezerTrackId);
        if (!string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            try
            {
                var apiTrack = await deezerClient.GetTrackAsync(normalizedTrackId);
                var fromTrack = BuildDeezerArtistImageUrl(
                    apiTrack.Artist?.Md5Image ?? apiTrack.Album?.Artist?.Md5Image,
                    imageSize,
                    apiTrack.Artist?.PictureXl,
                    apiTrack.Artist?.PictureBig,
                    apiTrack.Artist?.PictureMedium,
                    apiTrack.Artist?.PictureSmall,
                    apiTrack.Artist?.Picture,
                    apiTrack.Album?.Artist?.PictureXl,
                    apiTrack.Album?.Artist?.PictureBig,
                    apiTrack.Album?.Artist?.PictureMedium,
                    apiTrack.Album?.Artist?.PictureSmall,
                    apiTrack.Album?.Artist?.Picture);
                if (!string.IsNullOrWhiteSpace(fromTrack))
                {
                    return fromTrack;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Deezer artist image lookup failed for track {TrackId}", normalizedTrackId);
            }
        }

        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        try
        {
            var search = await deezerClient.SearchArtistAsync(
                artistName,
                new ApiOptions
                {
                    Limit = 8
                });
            var artistId = SelectBestDeezerArtistId(search.Data, artistName);
            var normalizedArtistId = TryExtractNumericId(artistId);
            if (string.IsNullOrWhiteSpace(normalizedArtistId))
            {
                return null;
            }

            var apiArtist = await deezerClient.GetArtistAsync(normalizedArtistId);
            return BuildDeezerArtistImageUrl(
                apiArtist.Md5Image,
                imageSize,
                apiArtist.PictureXl,
                apiArtist.PictureBig,
                apiArtist.PictureMedium,
                apiArtist.PictureSmall,
                apiArtist.Picture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Deezer artist image lookup failed for artist {ArtistName}", artistName);
            return null;
        }
    }

    private static string? BuildDeezerArtistImageUrl(
        string? md5,
        int size,
        params string?[] candidates)
    {
        if (HasUsableDeezerMd5(md5))
        {
            return $"https://e-cdns-images.dzcdn.net/images/artist/{md5}/{size}x{size}-000000-80-0-0.jpg";
        }

        return candidates.FirstOrDefault(candidate => IsAllowedDeezerImageUrl(candidate));
    }

    private static string? BuildDeezerCoverImageUrl(
        string? md5,
        int size,
        params string?[] candidates)
    {
        if (HasUsableDeezerMd5(md5))
        {
            return $"https://e-cdns-images.dzcdn.net/images/cover/{md5}/{size}x{size}-000000-80-0-0.jpg";
        }

        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
    }

    private static string? SelectBestDeezerArtistId(object[]? data, string? artistName)
    {
        if (data == null || data.Length == 0)
        {
            return null;
        }

        var target = NormalizeLookupToken(artistName);
        string? firstId = null;

        foreach (var item in data)
        {
            if (item is not JsonElement element || element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = TryGetJsonString(element, "id");
            if (firstId == null && !string.IsNullOrWhiteSpace(id))
            {
                firstId = id;
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            var name = TryGetJsonString(element, "name");
            if (!string.IsNullOrWhiteSpace(name)
                && string.Equals(NormalizeLookupToken(name), target, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return firstId;
    }

    private static string? TryGetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number when property.TryGetInt64(out var numeric) => numeric.ToString(),
            _ => null
        };
    }

    private static string NormalizeLookupToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }

    private static bool IsCompilationLikeAlbumTitle(string? value)
    {
        var normalized = NormalizeDescriptorToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return CompilationAlbumMarkers.Any(marker => ContainsWholeMarker(normalized, marker));
    }

    private static bool IsCompilationLikeApiAlbum(ApiAlbum? album)
    {
        if (album == null)
        {
            return false;
        }

        var recordType = album.RecordType?.Trim();
        if (string.Equals(recordType, "compile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(recordType, "compilation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(album.Artist?.Name)
            && string.Equals(album.Artist.Name.Trim(), "Various Artists", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(album.RootArtist?.Name)
            && string.Equals(album.RootArtist.Name.Trim(), "Various Artists", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsCompilationLikeAlbumTitle(album.Title);
    }

    private static bool ContainsWholeMarker(string text, string marker)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(marker))
        {
            return false;
        }

        var paddedText = $" {text} ";
        var paddedMarker = $" {marker} ";
        return paddedText.Contains(paddedMarker, StringComparison.Ordinal);
    }

    private static string NormalizeDescriptorToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant()
            .Replace("–", " ", StringComparison.Ordinal)
            .Replace("—", " ", StringComparison.Ordinal);

        var chars = normalized
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();
        normalized = new string(chars);
        normalized = string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized;
    }

    private static string NormalizeAlbumIdentity(string? value)
    {
        var normalized = NormalizeAlbumReleaseLabel(value);
        normalized = NormalizeDescriptorToken(normalized);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = Regex.Replace(normalized, @"\b(deluxe|expanded|edition|version|remaster(ed)?|bonus|clean|explicit|mono|stereo)\b", " ", RegexOptions.IgnoreCase, RegexTimeout);
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
        return normalized;
    }

    private static string NormalizeAlbumReleaseLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        normalized = Regex.Replace(normalized, @"\s*[\(\[]\s*(single|ep|e\.p\.|lp)\s*[\)\]]\s*$", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);
        normalized = Regex.Replace(normalized, @"\s*[-–—:]\s*(single|ep|e\.p\.|lp)\s*$", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
        return normalized;
    }

    private static bool IsAlbumIdentityToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return token.Length > 2 || token.All(char.IsDigit);
    }

    public static string? TryExtractAppleTrackId(Track? track)
    {
        if (track == null)
        {
            return null;
        }

        var sourceAppleId = TryExtractAppleTrackIdFromSource(track);
        if (!string.IsNullOrWhiteSpace(sourceAppleId))
        {
            return sourceAppleId;
        }

        var urlAppleId = TryExtractAppleTrackIdFromUrls(track.Urls);
        if (!string.IsNullOrWhiteSpace(urlAppleId))
        {
            return urlAppleId;
        }

        return TryExtractAppleTrackId(track.DownloadURL, allowRawNumeric: false);
    }

    public static string? TryExtractAppleTrackId(string? value, bool allowRawNumeric = true)
        => TryExtractAppleTrackIdFromValue(value, allowRawNumeric);

    private static string? TryExtractAppleTrackIdFromSource(Track track)
    {
        if (string.Equals(track.Source, AppleProvider, StringComparison.OrdinalIgnoreCase)
            && TryNormalizeAppleId(track.SourceId, out var sourceAppleId))
        {
            return sourceAppleId;
        }

        return null;
    }

    private static string? TryExtractAppleTrackIdFromUrls(Dictionary<string, string>? urls)
    {
        if (urls is not { Count: > 0 })
        {
            return null;
        }

        var idKeys = new[] { "apple_track_id", "apple_id", "appleid", AppleProvider };
        var idValue = TryExtractAppleTrackIdFromKeys(urls, idKeys, static key => !key.Equals(AppleProvider, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(idValue))
        {
            return idValue;
        }

        var urlKeys = new[] { "apple_url", "source_url" };
        return TryExtractAppleTrackIdFromKeys(urls, urlKeys, static _ => false);
    }

    private static string? TryExtractAppleTrackIdFromKeys(
        Dictionary<string, string> urls,
        IEnumerable<string> keys,
        Func<string, bool> allowRawNumeric)
    {
        foreach (var key in keys)
        {
            if (!urls.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var extracted = TryExtractAppleTrackId(value, allowRawNumeric(key));
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return extracted;
            }
        }

        return null;
    }

    private static string? TryExtractAppleTrackIdFromValue(string? value, bool allowRawNumeric = true)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        var rawAppleId = TryExtractRawAppleId(candidate, allowRawNumeric);
        if (!string.IsNullOrWhiteSpace(rawAppleId))
        {
            return rawAppleId;
        }

        var uriAppleId = TryExtractAppleTrackIdFromUri(candidate);
        if (!string.IsNullOrWhiteSpace(uriAppleId))
        {
            return uriAppleId;
        }

        var regexAppleId = TryExtractAppleTrackIdFromRegex(candidate);
        if (!string.IsNullOrWhiteSpace(regexAppleId))
        {
            return regexAppleId;
        }

        if (candidate.StartsWith("id", StringComparison.OrdinalIgnoreCase)
            && TryNormalizeAppleId(candidate[2..], out var prefixedId))
        {
            return prefixedId;
        }

        return allowRawNumeric && TryNormalizeAppleId(candidate, out var normalized) ? normalized : null;
    }

    private static string? TryExtractRawAppleId(string candidate, bool allowRawNumeric)
    {
        if (allowRawNumeric && TryNormalizeAppleId(candidate, out var appleId))
        {
            return appleId;
        }

        return null;
    }

    private static string? TryExtractAppleTrackIdFromUri(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsAppleMusicHost(uri))
        {
            return null;
        }

        var queryAppleId = TryExtractAppleTrackIdFromQuery(uri);
        if (!string.IsNullOrWhiteSpace(queryAppleId))
        {
            return queryAppleId;
        }

        return TryExtractAppleTrackIdFromPath(uri);
    }

    private static string? TryExtractAppleTrackIdFromPath(Uri uri)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i];
            if (segment.StartsWith("id", StringComparison.OrdinalIgnoreCase)
                && TryNormalizeAppleId(segment[2..], out var idSegment))
            {
                return idSegment;
            }

            if (TryNormalizeAppleId(segment, out var directSegment))
            {
                return directSegment;
            }
        }

        return null;
    }

    private static string? TryExtractAppleTrackIdFromRegex(string candidate)
    {
        var queryMatch = AppleQueryIdRegex.Match(candidate);
        if (queryMatch.Success && TryNormalizeAppleId(queryMatch.Groups["id"].Value, out var queryId))
        {
            return queryId;
        }

        var urlMatch = AppleTrackUrlRegex.Match(candidate);
        if (urlMatch.Success && TryNormalizeAppleId(urlMatch.Groups["id"].Value, out var urlId))
        {
            return urlId;
        }

        return null;
    }

    private static bool IsAppleMusicHost(Uri uri)
    {
        var host = uri.Host;
        return host.Equals("music.apple.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".music.apple.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeAppleId(string? value, out string? appleId)
    {
        appleId = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (!long.TryParse(candidate, out var numeric) || numeric <= 0)
        {
            return false;
        }

        appleId = numeric.ToString();
        return true;
    }

    private static string? TryExtractAppleTrackIdFromQuery(Uri uri)
    {
        if (uri == null || string.IsNullOrWhiteSpace(uri.Query))
        {
            return null;
        }

        var query = uri.Query.StartsWith('?') ? uri.Query[1..] : uri.Query;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var splitIndex = part.IndexOf('=');
            if (splitIndex <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part[..splitIndex]);
            if (!key.Equals("i", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var queryValue = Uri.UnescapeDataString(part[(splitIndex + 1)..]);
            if (TryNormalizeAppleId(queryValue, out var appleId))
            {
                return appleId;
            }
        }

        return null;
    }

    private static string? TryExtractNumericId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (long.TryParse(trimmed, out var numeric) && numeric > 0)
        {
            return numeric.ToString();
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (long.TryParse(segments[i], out numeric) && numeric > 0)
            {
                return numeric.ToString();
            }
        }

        return null;
    }

    private static bool HasUsableDeezerMd5(string? md5)
        => DeezerImageUrlValidator.HasUsableDeezerMd5(md5);

    private static bool IsAllowedDeezerImageUrl(string? url)
        => DeezerImageUrlValidator.IsAllowedDeezerImageUrl(url);
}
