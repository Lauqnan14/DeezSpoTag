using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Linq;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;
using SixLabors.ImageSharp;

namespace DeezSpoTag.Services.Download.Shared;

public static class DownloadEngineArtworkHelper
{
    private const string AppleProvider = "apple";
    private const string DeezerProvider = "deezer";
    private const string SpotifyProvider = "spotify";
    private const int ArtistArtworkCacheLimit = 2048;
    private static readonly TimeSpan ArtistArtworkHitTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan ArtistArtworkMissTtl = TimeSpan.FromMinutes(30);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Stamp, ArtistArtworkResolution? Resolution)> ArtistArtworkCache = new(StringComparer.OrdinalIgnoreCase);

    public sealed record StandardAudioCoverResolveRequest(
        DeezSpoTagSettings Settings,
        AppleMusicCatalogService? AppleCatalog,
        IHttpClientFactory? HttpClientFactory,
        ISpotifyArtworkResolver? SpotifyArtworkResolver,
        DeezerClient? DeezerClient,
        string? AppleId,
        string? Title,
        string? Artist,
        string? Album,
        string? CollectionType,
        string? DeezerId,
        string? PayloadCover,
        string? Isrc,
        ILogger Logger)
    {
        public string? SpotifyId { get; init; }
    }

    public sealed record AudioTagWithCoverRequest(
        string OutputPath,
        Track Track,
        DeezSpoTagSettings Settings,
        string EmbedPrefix,
        AudioTagger AudioTagger,
        ILogger Logger);

    public sealed record ArtistImageResolveRequest(
        AppleMusicCatalogService? AppleCatalog,
        IHttpClientFactory? HttpClientFactory,
        DeezSpoTagSettings Settings,
        DeezerClient? DeezerClient,
        ISpotifyArtworkResolver? SpotifyArtworkResolver,
        ILastFmArtistImageResolver? LastFmArtistImageResolver,
        string? AppleId,
        string? DeezerId,
        string? SpotifyId,
        string? Artist,
        ILogger Logger)
    {
        public string? AppleArtistId { get; init; }
        public string? DeezerArtistId { get; init; }
        public string? SpotifyArtistId { get; init; }
    }

    public sealed record ArtistArtworkResolution(
        string Provider,
        string Url,
        string? ProviderArtistId,
        string ResolutionMethod);

    public sealed record SaveArtistArtworkRequest(
        ImageDownloader ImageDownloader,
        EnhancedPathTemplateProcessor PathProcessor,
        string ArtistPath,
        string ArtistImageUrl,
        DeezSpoTagSettings Settings,
        Track Track,
        int AppleArtworkSize,
        bool PreferMaxQualityCover,
        ILogger Logger,
        bool SingleJpegForNonApple = false);

    public sealed record ArtworkCandidate(string Provider, string Url);

    public static async Task<IReadOnlyList<string>> ResolveStandardAudioCoverUrlsAsync(
        StandardAudioCoverResolveRequest request,
        CancellationToken cancellationToken)
    {
        var fallbackOrder = ArtworkFallbackHelper.ResolveOrder(request.Settings);
        var coverUrls = new List<string>();
        var payloadCandidate = TryCreatePayloadCoverCandidate(request.PayloadCover);
        var rejectCompilationAlbumCandidate = ShouldRejectCompilationArtworkForRequest(request);

        // The queued source cover belongs to the selected release. Other providers
        // are fallbacks and must not replace it with another edition's artwork.
        if (payloadCandidate != null
            && (string.IsNullOrWhiteSpace(payloadCandidate.Provider)
                || fallbackOrder.Contains(payloadCandidate.Provider, StringComparer.OrdinalIgnoreCase)))
        {
            AddCoverUrl(coverUrls, payloadCandidate.Url);
        }

        foreach (var fallback in fallbackOrder)
        {
            string? coverUrl = null;
            switch (fallback)
            {
                case "apple":
                    coverUrl = await ArtworkFallbackHelper.TryResolveAppleCoverAsync(
                        request.AppleCatalog,
                        request.HttpClientFactory,
                        new ArtworkFallbackHelper.AppleCoverLookupRequest(
                            request.Settings,
                            request.AppleId,
                            request.Title,
                            request.Artist,
                            request.Album),
                        request.Logger,
                        cancellationToken);
                    break;

                case "deezer":
                    coverUrl = await ArtworkFallbackHelper.TryResolveDeezerCoverAsync(
                        request.DeezerClient,
                        request.DeezerId,
                        request.Settings.LocalArtworkSize,
                        NullLogger.Instance,
                        cancellationToken,
                        request.Album,
                        rejectCompilationAlbumCandidate);
                    break;

                case "spotify":
                    coverUrl = await TryResolveSpotifyCoverAsync(
                        request,
                        rejectCompilationAlbumCandidate,
                        cancellationToken);
                    break;
            }

            AddCoverUrl(coverUrls, coverUrl);
        }

        return coverUrls;
    }

    private static ArtworkCandidate? TryCreatePayloadCoverCandidate(string? payloadCover)
    {
        if (string.IsNullOrWhiteSpace(payloadCover))
        {
            return null;
        }

        var normalizedUrl = payloadCover.Trim();
        var provider = TryIdentifyArtworkProvider(normalizedUrl);
        return string.IsNullOrWhiteSpace(provider)
            ? new ArtworkCandidate(string.Empty, normalizedUrl)
            : new ArtworkCandidate(provider, normalizedUrl);
    }

    private static async Task<string?> TryResolveSpotifyCoverAsync(
        StandardAudioCoverResolveRequest request,
        bool rejectCompilationAlbumCandidate,
        CancellationToken cancellationToken)
    {
        if (request.SpotifyArtworkResolver == null)
        {
            return null;
        }

        var spotifyId = request.SpotifyId;
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return null;
        }

        var album = ArtworkFallbackHelper.ResolveAlbumConstraintForArtwork(request.Album);
        return await request.SpotifyArtworkResolver.ResolveAlbumCoverUrlAsync(
            spotifyId,
            cancellationToken,
            album,
            rejectCompilationAlbumCandidate);
    }

    private static string? TryIdentifyArtworkProvider(string coverUrl)
    {
        if (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host;
        if (host.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase)
            || host.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase))
        {
            return AppleProvider;
        }

        if (host.Contains("dzcdn.net", StringComparison.OrdinalIgnoreCase)
            || host.Contains("deezer.com", StringComparison.OrdinalIgnoreCase))
        {
            return DeezerProvider;
        }

        if (host.Contains("scdn.co", StringComparison.OrdinalIgnoreCase)
            || host.Contains("spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            return SpotifyProvider;
        }

        return null;
    }

    private static void AddCoverUrl(List<string> coverUrls, string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return;
        }

        var normalizedCoverUrl = coverUrl.Trim();
        if (!coverUrls.Contains(normalizedCoverUrl, StringComparer.OrdinalIgnoreCase))
        {
            coverUrls.Add(normalizedCoverUrl);
        }
    }

    private static bool ShouldRejectCompilationArtworkForRequest(StandardAudioCoverResolveRequest request)
    {
        if (string.Equals(request.CollectionType, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ArtworkFallbackHelper.IsCompilationLikeAlbumTitle(request.Album))
        {
            return false;
        }

        return true;
    }

    public static async Task TagAudioWithResolvedCoverAsync(
        AudioTagWithCoverRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await request.AudioTagger.TagTrackAsync(request.OutputPath, request.Track, request.Settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Logger.LogWarning(ex, "{Engine} tagging failed for {Path}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(request.EmbedPrefix), DeezSpoTag.Core.Security.LogSanitizer.OneLine(request.OutputPath));
            throw new InvalidOperationException(
                $"{request.EmbedPrefix} tagging failed for '{request.OutputPath}'.",
                ex);
        }
    }

    public static async Task<ArtistArtworkResolution?> ResolveArtistArtworkAsync(
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        var fallbackOrder = ArtworkFallbackHelper.ResolveArtistOrder(request.Settings);
        var cacheKey = BuildArtistArtworkCacheKey(request, fallbackOrder);
        if (TryGetCachedArtistArtwork(cacheKey, out var cached))
        {
            return cached;
        }

        for (var index = 0; index < fallbackOrder.Count; index++)
        {
            var source = fallbackOrder[index];
            var isLastProvider = index == fallbackOrder.Count - 1;
            var resolution = await TryResolveArtistImageBySourceAsync(
                source,
                request,
                allowAlbumArtworkFallback: isLastProvider,
                cancellationToken);
            if (resolution != null)
            {
                request.Logger.LogDebug(
                    "Artist artwork resolved from {Provider} using {ResolutionMethod} for {Artist}",
                    resolution.Provider,
                    resolution.ResolutionMethod,
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(request.Artist));
                StoreArtistArtwork(cacheKey, resolution);
                return resolution;
            }
        }

        StoreArtistArtwork(cacheKey, null);
        return null;
    }

    private static string? BuildArtistArtworkCacheKey(
        ArtistImageResolveRequest request,
        IEnumerable<string> fallbackOrder)
    {
        if (string.IsNullOrWhiteSpace(request.Artist))
        {
            return null;
        }

        return string.Join(
            '|',
            request.Artist.Trim().ToLowerInvariant(),
            request.AppleArtistId ?? request.AppleId ?? string.Empty,
            request.DeezerArtistId ?? request.DeezerId ?? string.Empty,
            request.SpotifyArtistId ?? request.SpotifyId ?? string.Empty,
            string.Join(',', fallbackOrder));
    }

    private static bool TryGetCachedArtistArtwork(string? cacheKey, out ArtistArtworkResolution? resolution)
    {
        resolution = null;
        if (cacheKey == null || !ArtistArtworkCache.TryGetValue(cacheKey, out var entry))
        {
            return false;
        }

        var ttl = entry.Resolution == null ? ArtistArtworkMissTtl : ArtistArtworkHitTtl;
        if (DateTimeOffset.UtcNow - entry.Stamp > ttl)
        {
            ArtistArtworkCache.TryRemove(cacheKey, out _);
            return false;
        }

        resolution = entry.Resolution;
        return true;
    }

    private static void StoreArtistArtwork(string? cacheKey, ArtistArtworkResolution? resolution)
    {
        if (cacheKey == null)
        {
            return;
        }

        ArtistArtworkCache[cacheKey] = (DateTimeOffset.UtcNow, resolution);
        if (ArtistArtworkCache.Count <= ArtistArtworkCacheLimit)
        {
            return;
        }

        foreach (var stale in ArtistArtworkCache
            .OrderBy(pair => pair.Value.Stamp)
            .Take(ArtistArtworkCache.Count - ArtistArtworkCacheLimit)
            .Select(pair => pair.Key)
            .ToList())
        {
            ArtistArtworkCache.TryRemove(stale, out _);
        }
    }

    /// <summary>
    /// Resolves an artist portrait from a single provider. Album or song artwork is only an
    /// acceptable answer for the last provider in the configured order; every earlier provider
    /// must report a miss so the chain keeps moving.
    /// </summary>
    private static async Task<ArtistArtworkResolution?> TryResolveArtistImageBySourceAsync(
        string source,
        ArtistImageResolveRequest request,
        bool allowAlbumArtworkFallback,
        CancellationToken cancellationToken)
    {
        var portrait = await TryResolveArtistPortraitBySourceAsync(source, request, cancellationToken);
        if (portrait != null || !allowAlbumArtworkFallback)
        {
            return portrait;
        }

        return await TryResolveArtistImageFromAlbumArtworkAsync(source, request, cancellationToken);
    }

    private static async Task<ArtistArtworkResolution?> TryResolveArtistImageFromAlbumArtworkAsync(
        string source,
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = source?.Trim().ToLowerInvariant();
        var url = normalized switch
        {
            "apple" => await ResolveAppleAlbumArtworkForArtistAsync(request, cancellationToken),
            "deezer" => await ArtworkFallbackHelper.TryResolveDeezerCoverAsync(
                request.DeezerClient,
                request.DeezerId,
                ArtworkSizePolicy.ResolveRequestSize(request.Settings.LocalArtworkSize, "deezer"),
                request.Logger,
                cancellationToken),
            "spotify" => request.SpotifyArtworkResolver == null
                ? null
                : await request.SpotifyArtworkResolver.ResolveAlbumCoverUrlAsync(request.SpotifyId, cancellationToken),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var provider = normalized switch
        {
            "apple" => AppleProvider,
            "deezer" => DeezerProvider,
            _ => SpotifyProvider
        };
        var providerArtistId = normalized switch
        {
            "apple" => request.AppleArtistId,
            "deezer" => request.DeezerArtistId,
            _ => request.SpotifyArtistId
        };

        request.Logger.LogDebug(
            "Artist portrait unavailable from every provider; using {Provider} album artwork for {Artist}",
            provider,
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(request.Artist));
        return new ArtistArtworkResolution(provider, url!, providerArtistId, "album-artwork-fallback");
    }

    private static async Task<string?> ResolveAppleAlbumArtworkForArtistAsync(
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AppleCatalog == null || string.IsNullOrWhiteSpace(request.AppleId))
        {
            return null;
        }

        var storefront = string.IsNullOrWhiteSpace(request.Settings.AppleMusic?.Storefront)
            ? "us"
            : request.Settings.AppleMusic!.Storefront;
        return await AppleQueueHelpers.ResolveAppleArtistImageFromSongAsync(
            request.AppleCatalog,
            request.AppleId,
            storefront,
            AppleQueueHelpers.GetAppleArtworkSize(request.Settings),
            request.Logger,
            cancellationToken,
            allowAlbumArtwork: true);
    }

    private static Task<ArtistArtworkResolution?> TryResolveArtistPortraitBySourceAsync(
        string source,
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (string.Equals(source, "apple", StringComparison.OrdinalIgnoreCase))
        {
            return TryResolveAppleArtistImageAsync(request, cancellationToken);
        }

        if (string.Equals(source, "deezer", StringComparison.OrdinalIgnoreCase))
        {
            return TryResolveDeezerArtistImageAsync(request, cancellationToken);
        }

        if (string.Equals(source, "spotify", StringComparison.OrdinalIgnoreCase))
        {
            return TryResolveSpotifyArtistImageAsync(request, cancellationToken);
        }

        if (string.Equals(source, "lastfm", StringComparison.OrdinalIgnoreCase))
        {
            return TryResolveLastFmArtistImageAsync(request, cancellationToken);
        }

        return Task.FromResult<ArtistArtworkResolution?>(null);
    }

    private static async Task<ArtistArtworkResolution?> TryResolveDeezerArtistImageAsync(
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.DeezerArtistId))
        {
            var byArtistId = await ArtworkFallbackHelper.TryResolveDeezerArtistImageByArtistIdAsync(
                request.DeezerClient,
                request.DeezerArtistId,
                request.Settings.LocalArtworkSize,
                request.Logger,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(byArtistId))
            {
                return new ArtistArtworkResolution(DeezerProvider, byArtistId, request.DeezerArtistId, "artist-id");
            }
        }

        var imageUrl = await ArtworkFallbackHelper.TryResolveDeezerArtistImageAsync(
            request.DeezerClient,
            request.DeezerId,
            request.Settings.LocalArtworkSize,
            request.Logger,
            cancellationToken,
            request.Artist);
        return string.IsNullOrWhiteSpace(imageUrl)
            ? null
            : new ArtistArtworkResolution(
                DeezerProvider,
                imageUrl,
                request.DeezerArtistId,
                string.IsNullOrWhiteSpace(request.DeezerId) ? "exact-name" : "track-relationship");
    }

    private static async Task<ArtistArtworkResolution?> TryResolveAppleArtistImageAsync(
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AppleCatalog != null && !string.IsNullOrWhiteSpace(request.AppleArtistId))
        {
            var storefront = string.IsNullOrWhiteSpace(request.Settings.AppleMusic?.Storefront)
                ? "us"
                : request.Settings.AppleMusic!.Storefront;
            var byArtistId = await AppleQueueHelpers.ResolveAppleArtistImageByIdAsync(
                request.AppleCatalog,
                request.AppleArtistId,
                storefront,
                AppleQueueHelpers.GetAppleArtworkSize(request.Settings),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(byArtistId))
            {
                return new ArtistArtworkResolution(AppleProvider, byArtistId, request.AppleArtistId, "artist-id");
            }
        }

        if (string.IsNullOrWhiteSpace(request.Artist))
        {
            return null;
        }

        var imageUrl = await ArtworkFallbackHelper.TryResolveAppleArtistImageAsync(
            request.AppleCatalog,
            request.HttpClientFactory,
            request.Settings,
            request.AppleId,
            request.Artist,
            request.Logger,
            cancellationToken);
        return string.IsNullOrWhiteSpace(imageUrl)
            ? null
            : new ArtistArtworkResolution(
                AppleProvider,
                imageUrl,
                request.AppleArtistId,
                string.IsNullOrWhiteSpace(request.AppleId) ? "exact-name" : "track-relationship");
    }

    private static async Task<ArtistArtworkResolution?> TryResolveSpotifyArtistImageAsync(
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SpotifyArtworkResolver == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.SpotifyArtistId))
        {
            var byArtistId = await request.SpotifyArtworkResolver.ResolveArtistImageByArtistIdAsync(
                request.SpotifyArtistId,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(byArtistId))
            {
                return new ArtistArtworkResolution(SpotifyProvider, byArtistId, request.SpotifyArtistId, "artist-id");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            var byId = await request.SpotifyArtworkResolver.ResolveArtistImageUrlAsync(request.SpotifyId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(byId))
            {
                return new ArtistArtworkResolution(SpotifyProvider, byId, request.SpotifyArtistId, "track-relationship");
            }
        }

        if (string.IsNullOrWhiteSpace(request.Artist))
        {
            return null;
        }

        var byName = await request.SpotifyArtworkResolver.ResolveArtistImageByNameAsync(request.Artist, cancellationToken);
        return string.IsNullOrWhiteSpace(byName)
            ? null
            : new ArtistArtworkResolution(SpotifyProvider, byName, request.SpotifyArtistId, "exact-name");
    }

    private static async Task<ArtistArtworkResolution?> TryResolveLastFmArtistImageAsync(
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LastFmArtistImageResolver == null || string.IsNullOrWhiteSpace(request.Artist))
        {
            return null;
        }

        var imageUrl = await request.LastFmArtistImageResolver.ResolveArtistImageByNameAsync(request.Artist, cancellationToken);
        return string.IsNullOrWhiteSpace(imageUrl)
            ? null
            : new ArtistArtworkResolution("lastfm", imageUrl, null, "exact-name");
    }

    public sealed record ArtistArtworkSaveResult(
        IReadOnlyList<string> Paths,
        int? Width,
        int? Height,
        bool ExistingArtworkRetained);

    private sealed record ValidatedArtistArtwork(string Path, int Width, int Height, bool ExistingArtworkRetained);

    public static async Task<ArtistArtworkSaveResult> SaveArtistArtworkAsync(
        SaveArtistArtworkRequest request,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.ArtistPath);
        var savedArtwork = new List<ValidatedArtistArtwork>();
        var artistName = request.PathProcessor.GenerateArtistName(
            request.Settings.ArtistImageTemplate,
            request.Track.MainArtist,
            request.Settings,
            request.Track.Album?.RootArtist);

        if (string.IsNullOrWhiteSpace(artistName))
        {
            artistName = "artist";
        }

        if (request.ArtistImageUrl.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var format in AppleQueueHelpers.GetArtworkOutputFormats(request.Settings))
            {
                var targetPath = Path.Join(request.ArtistPath, $"{artistName}.{format}");
                var protectedExisting = IsProtectedExistingArtwork(targetPath, request.Settings.OverwriteFile);
                var downloaded = await AppleQueueHelpers.DownloadAppleArtworkAsync(
                    request.ImageDownloader,
                    new AppleQueueHelpers.AppleArtworkDownloadRequest
                    {
                        RawUrl = request.ArtistImageUrl,
                        OutputPath = targetPath,
                        Settings = request.Settings,
                        Size = request.AppleArtworkSize,
                        Overwrite = request.Settings.OverwriteFile,
                        PreferMaxQuality = request.PreferMaxQualityCover,
                        Logger = request.Logger
                    },
                    cancellationToken);
                var validated = await ValidateArtistArtworkAsync(downloaded, protectedExisting, request.Logger, cancellationToken);
                if (validated != null)
                {
                    savedArtwork.Add(validated);
                }
            }

            return BuildArtistArtworkSaveResult(savedArtwork);
        }

        if (request.SingleJpegForNonApple)
        {
            var artistFilePath = Path.Join(request.ArtistPath, $"{artistName}.jpg");
            var protectedExisting = IsProtectedExistingArtwork(artistFilePath, request.Settings.OverwriteFile);
            var downloaded = await request.ImageDownloader.DownloadImageAsync(
                request.ArtistImageUrl,
                artistFilePath,
                request.Settings.OverwriteFile,
                request.PreferMaxQualityCover,
                cancellationToken);
            var validated = await ValidateArtistArtworkAsync(downloaded, protectedExisting, request.Logger, cancellationToken);
            if (validated != null)
            {
                savedArtwork.Add(validated);
            }

            return BuildArtistArtworkSaveResult(savedArtwork);
        }

        var formats = (request.Settings.LocalArtworkFormat ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var format in formats)
        {
            var ext = format.Equals("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
            var targetPath = Path.Join(request.ArtistPath, $"{artistName}.{ext}");
            var protectedExisting = IsProtectedExistingArtwork(targetPath, request.Settings.OverwriteFile);
            var downloaded = await request.ImageDownloader.DownloadImageAsync(
                request.ArtistImageUrl,
                targetPath,
                request.Settings.OverwriteFile,
                request.PreferMaxQualityCover,
                cancellationToken);
            var validated = await ValidateArtistArtworkAsync(downloaded, protectedExisting, request.Logger, cancellationToken);
            if (validated != null)
            {
                savedArtwork.Add(validated);
            }
        }

        return BuildArtistArtworkSaveResult(savedArtwork);
    }

    private static ArtistArtworkSaveResult BuildArtistArtworkSaveResult(IReadOnlyList<ValidatedArtistArtwork> savedArtwork)
    {
        var first = savedArtwork.FirstOrDefault();
        return new ArtistArtworkSaveResult(
            savedArtwork.Select(item => item.Path).ToArray(),
            first?.Width,
            first?.Height,
            savedArtwork.Any(item => item.ExistingArtworkRetained));
    }

    private static async Task<ValidatedArtistArtwork?> ValidateArtistArtworkAsync(
        string? downloadedPath,
        bool protectedExisting,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadedPath) || !File.Exists(downloadedPath))
        {
            return null;
        }

        try
        {
            using var image = await Image.LoadAsync(downloadedPath, cancellationToken);
            if (IsSquareArtistArtworkDimensions(image.Width, image.Height))
            {
                if (protectedExisting && logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "Retained existing artist artwork because overwrite protection is enabled: {Path}",
                        DeezSpoTag.Core.Security.LogSanitizer.OneLine(downloadedPath));
                }
                return new ValidatedArtistArtwork(downloadedPath, image.Width, image.Height, protectedExisting);
            }

            logger.LogWarning(
                "Rejected non-square artist artwork {Path} ({Width}x{Height}). ExistingProtected={ExistingProtected}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(downloadedPath),
                image.Width,
                image.Height,
                protectedExisting);
            if (!protectedExisting)
            {
                File.Delete(downloadedPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Rejected unreadable artist artwork {Path}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(downloadedPath));
            if (!protectedExisting)
            {
                File.Delete(downloadedPath);
            }
        }

        return null;
    }

    private static bool IsProtectedExistingArtwork(string path, string? overwrite)
        => File.Exists(path) && overwrite is not ("y" or "t");

    public static bool IsSquareArtistArtworkDimensions(int width, int height)
    {
        var longer = Math.Max(width, height);
        var shorter = Math.Min(width, height);
        return shorter > 0 && longer / (double)shorter <= 1.01d;
    }

    public static bool ShouldRefreshExistingArtistArtwork(
        string? currentProvider,
        string? preferredProvider,
        string? overwrite)
    {
        if (!string.IsNullOrWhiteSpace(currentProvider)
            && string.Equals(currentProvider, preferredProvider, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return overwrite is "y" or "t";
    }

    public static async Task<(int Width, int Height)?> ReadSquareArtistArtworkDimensionsAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var image = await Image.LoadAsync(path, cancellationToken);
            return IsSquareArtistArtworkDimensions(image.Width, image.Height)
                ? (image.Width, image.Height)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
