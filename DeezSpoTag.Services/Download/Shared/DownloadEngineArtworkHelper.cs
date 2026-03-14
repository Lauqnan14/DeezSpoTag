using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;

namespace DeezSpoTag.Services.Download.Shared;

public static class DownloadEngineArtworkHelper
{
    public sealed record StandardAudioCoverResolveRequest(
        DeezSpoTagSettings Settings,
        AppleMusicCatalogService? AppleCatalog,
        IHttpClientFactory? HttpClientFactory,
        ISpotifyArtworkResolver? SpotifyArtworkResolver,
        ISpotifyIdResolver? SpotifyIdResolver,
        DeezerClient? DeezerClient,
        string? AppleId,
        string? Title,
        string? Artist,
        string? Album,
        string? DeezerId,
        string? PayloadCover,
        string? Isrc,
        ILogger Logger);

    public sealed record AudioTagWithCoverRequest(
        string OutputPath,
        Track Track,
        DeezSpoTagSettings Settings,
        string? CoverUrl,
        string EmbedPrefix,
        ImageDownloader ImageDownloader,
        AudioTagger AudioTagger,
        ILogger Logger);

    public sealed record ArtistImageResolveRequest(
        AppleMusicCatalogService? AppleCatalog,
        IHttpClientFactory? HttpClientFactory,
        DeezSpoTagSettings Settings,
        DeezerClient? DeezerClient,
        ISpotifyArtworkResolver? SpotifyArtworkResolver,
        string? AppleId,
        string? DeezerId,
        string? SpotifyId,
        string? Artist,
        ILogger Logger);

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

    public static async Task<string?> ResolveStandardAudioCoverUrlAsync(
        StandardAudioCoverResolveRequest request,
        CancellationToken cancellationToken)
    {
        var fallbackOrder = ArtworkFallbackHelper.ResolveOrder(request.Settings);
        string? coverUrl = null;

        foreach (var fallback in fallbackOrder)
        {
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
                        request.Album);
                    break;

                case "spotify":
                    coverUrl = await ArtworkFallbackHelper.TryResolveSpotifyCoverAsync(
                        request.SpotifyIdResolver,
                        request.SpotifyArtworkResolver,
                        request.Title,
                        request.Artist,
                        request.Album,
                        request.Isrc,
                        cancellationToken);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(coverUrl))
            {
                break;
            }
        }

        return coverUrl;
    }

    public static async Task TagAudioWithResolvedCoverAsync(
        AudioTagWithCoverRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Settings.Tags?.Cover == true
            && !string.IsNullOrWhiteSpace(request.CoverUrl)
            && request.Track.Album != null)
        {
            var embedSize = request.Settings.EmbedMaxQualityCover
                ? request.Settings.LocalArtworkSize
                : request.Settings.EmbeddedArtworkSize;
            var isAppleCover = request.CoverUrl.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase);
            string embedExt;
            if (isAppleCover)
            {
                embedExt = $".{AppleQueueHelpers.GetAppleArtworkExtension(request.CoverUrl, AppleQueueHelpers.GetAppleArtworkFormat(request.Settings))}";
            }
            else
            {
                embedExt = request.CoverUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            }
            var embedPath = Path.Join(Path.GetTempPath(), $"{request.EmbedPrefix}-embed-{Guid.NewGuid():N}{embedExt}");
            var downloadedCover = isAppleCover
                ? await AppleQueueHelpers.DownloadAppleArtworkAsync(
                    request.ImageDownloader,
                    new AppleQueueHelpers.AppleArtworkDownloadRequest
                    {
                        RawUrl = request.CoverUrl,
                        OutputPath = embedPath,
                        Settings = request.Settings,
                        Size = embedSize,
                        Overwrite = request.Settings.OverwriteFile,
                        PreferMaxQuality = true,
                        Logger = request.Logger
                    },
                    cancellationToken)
                : await request.ImageDownloader.DownloadImageAsync(
                    request.CoverUrl,
                    embedPath,
                    request.Settings.OverwriteFile,
                    true,
                    cancellationToken);

            if (!string.IsNullOrWhiteSpace(downloadedCover))
            {
                request.Track.Album.EmbeddedCoverPath = downloadedCover;
            }
        }

        try
        {
            await request.AudioTagger.TagTrackAsync(request.OutputPath, request.Track, request.Settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Logger.LogWarning(ex, "{Engine} tagging failed for {Path}", request.EmbedPrefix, request.OutputPath);
        }
    }

    public static async Task<string?> ResolveArtistImageUrlAsync(
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        var fallbackOrder = ArtworkFallbackHelper.ResolveArtistOrder(request.Settings);
        foreach (var source in fallbackOrder)
        {
            var imageUrl = await TryResolveArtistImageBySourceAsync(source, request, cancellationToken);
            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                return imageUrl;
            }
        }

        return null;
    }

    private static Task<string?> TryResolveArtistImageBySourceAsync(
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
            return ArtworkFallbackHelper.TryResolveDeezerArtistImageAsync(
                request.DeezerClient,
                request.DeezerId,
                request.Settings.LocalArtworkSize,
                request.Logger,
                cancellationToken,
                request.Artist);
        }

        if (string.Equals(source, "spotify", StringComparison.OrdinalIgnoreCase))
        {
            return TryResolveSpotifyArtistImageAsync(request, cancellationToken);
        }

        return Task.FromResult<string?>(null);
    }

    private static Task<string?> TryResolveAppleArtistImageAsync(
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Artist))
        {
            return Task.FromResult<string?>(null);
        }

        return ArtworkFallbackHelper.TryResolveAppleArtistImageAsync(
            request.AppleCatalog,
            request.HttpClientFactory,
            request.Settings,
            request.AppleId,
            request.Artist,
            request.Logger,
            cancellationToken);
    }

    private static async Task<string?> TryResolveSpotifyArtistImageAsync(
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SpotifyArtworkResolver == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            var byId = await request.SpotifyArtworkResolver.ResolveArtistImageUrlAsync(request.SpotifyId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(byId))
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Artist))
        {
            return await request.SpotifyArtworkResolver.ResolveArtistImageByNameAsync(request.Artist, cancellationToken);
        }

        return null;
    }

    public static async Task SaveArtistArtworkAsync(
        SaveArtistArtworkRequest request,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.ArtistPath);
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
                await AppleQueueHelpers.DownloadAppleArtworkAsync(
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
            }

            return;
        }

        if (request.SingleJpegForNonApple)
        {
            var artistFilePath = Path.Join(request.ArtistPath, $"{artistName}.jpg");
            await request.ImageDownloader.DownloadImageAsync(
                request.ArtistImageUrl,
                artistFilePath,
                request.Settings.OverwriteFile,
                request.PreferMaxQualityCover,
                cancellationToken);
            return;
        }

        var formats = (request.Settings.LocalArtworkFormat ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var format in formats)
        {
            var ext = format.Equals("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
            var targetPath = Path.Join(request.ArtistPath, $"{artistName}.{ext}");
            await request.ImageDownloader.DownloadImageAsync(
                request.ArtistImageUrl,
                targetPath,
                request.Settings.OverwriteFile,
                request.PreferMaxQualityCover,
                cancellationToken);
        }
    }
}
