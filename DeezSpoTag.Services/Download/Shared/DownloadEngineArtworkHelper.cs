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

namespace DeezSpoTag.Services.Download.Shared;

public static class DownloadEngineArtworkHelper
{
    private const string MzStaticHost = "mzstatic.com";

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
        ILogger Logger,
        IReadOnlyList<string>? CoverUrls = null);

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
        var coverUrls = await ResolveStandardAudioCoverUrlsAsync(request, cancellationToken);
        return coverUrls.Count > 0 ? coverUrls[0] : null;
    }

    public static async Task<IReadOnlyList<string>> ResolveStandardAudioCoverUrlsAsync(
        StandardAudioCoverResolveRequest request,
        CancellationToken cancellationToken)
    {
        var fallbackOrder = ArtworkFallbackHelper.ResolveOrder(request.Settings);
        var coverUrls = new List<string>();

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
                var normalizedCoverUrl = coverUrl.Trim();
                if (!coverUrls.Contains(normalizedCoverUrl, StringComparer.OrdinalIgnoreCase))
                {
                    coverUrls.Add(normalizedCoverUrl);
                }
            }
        }

        // Payload-provided cover URL is a final fallback so download-time embedding can still proceed
        // even when provider lookups fail.
        if (!string.IsNullOrWhiteSpace(request.PayloadCover))
        {
            var normalizedPayloadCover = request.PayloadCover.Trim();
            if (!coverUrls.Contains(normalizedPayloadCover, StringComparer.OrdinalIgnoreCase))
            {
                coverUrls.Add(normalizedPayloadCover);
            }
        }

        return coverUrls;
    }

    public static async Task TagAudioWithResolvedCoverAsync(
        AudioTagWithCoverRequest request,
        CancellationToken cancellationToken)
    {
        var coverUrls = NormalizeCoverUrls(request);
        await TryEmbedCoverAsync(request, coverUrls, cancellationToken);

        try
        {
            await request.AudioTagger.TagTrackAsync(request.OutputPath, request.Track, request.Settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Logger.LogWarning(ex, "{Engine} tagging failed for {Path}", request.EmbedPrefix, request.OutputPath);
            throw new InvalidOperationException(
                $"{request.EmbedPrefix} tagging failed for '{request.OutputPath}'.",
                ex);
        }
    }

    private static List<string> NormalizeCoverUrls(AudioTagWithCoverRequest request)
    {
        var coverUrls = request.CoverUrls?
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        if (coverUrls.Count == 0 && !string.IsNullOrWhiteSpace(request.CoverUrl))
        {
            coverUrls.Add(request.CoverUrl.Trim());
        }

        return coverUrls;
    }

    private static async Task TryEmbedCoverAsync(
        AudioTagWithCoverRequest request,
        List<string> coverUrls,
        CancellationToken cancellationToken)
    {
        if (request.Settings.Tags?.Cover != true
            || coverUrls.Count == 0
            || request.Track.Album == null)
        {
            return;
        }

        var embedSize = request.Settings.EmbedMaxQualityCover
            ? request.Settings.LocalArtworkSize
            : request.Settings.EmbeddedArtworkSize;
        foreach (var coverUrl in coverUrls)
        {
            var downloadedCover = await DownloadEmbeddedCoverAsync(request, coverUrl, embedSize, cancellationToken);
            if (!string.IsNullOrWhiteSpace(downloadedCover))
            {
                request.Track.Album.EmbeddedCoverPath = downloadedCover;
                return;
            }
        }
    }

    private static async Task<string?> DownloadEmbeddedCoverAsync(
        AudioTagWithCoverRequest request,
        string coverUrl,
        int embedSize,
        CancellationToken cancellationToken)
    {
        var isAppleCover = coverUrl.Contains(MzStaticHost, StringComparison.OrdinalIgnoreCase);
        var embedPath = Path.Join(Path.GetTempPath(), $"{request.EmbedPrefix}-embed-{Guid.NewGuid():N}{ResolveEmbeddedCoverExtension(request, coverUrl, isAppleCover)}");
        if (isAppleCover)
        {
            return await AppleQueueHelpers.DownloadAppleArtworkAsync(
                request.ImageDownloader,
                new AppleQueueHelpers.AppleArtworkDownloadRequest
                {
                    RawUrl = coverUrl,
                    OutputPath = embedPath,
                    Settings = request.Settings,
                    Size = embedSize,
                    Overwrite = request.Settings.OverwriteFile,
                    PreferMaxQuality = true,
                    Logger = request.Logger
                },
                cancellationToken);
        }

        return await request.ImageDownloader.DownloadImageAsync(
            coverUrl,
            embedPath,
            request.Settings.OverwriteFile,
            true,
            cancellationToken);
    }

    private static string ResolveEmbeddedCoverExtension(AudioTagWithCoverRequest request, string coverUrl, bool isAppleCover)
    {
        if (isAppleCover)
        {
            return $".{AppleQueueHelpers.GetAppleArtworkExtension(coverUrl, AppleQueueHelpers.GetAppleArtworkFormat(request.Settings))}";
        }

        return coverUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
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

    public static async Task<bool> SaveArtistArtworkAsync(
        SaveArtistArtworkRequest request,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.ArtistPath);
        var anySaved = false;
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
                anySaved |= !string.IsNullOrWhiteSpace(downloaded) && File.Exists(downloaded);
            }

            return anySaved;
        }

        if (request.SingleJpegForNonApple)
        {
            var artistFilePath = Path.Join(request.ArtistPath, $"{artistName}.jpg");
            var downloaded = await request.ImageDownloader.DownloadImageAsync(
                request.ArtistImageUrl,
                artistFilePath,
                request.Settings.OverwriteFile,
                request.PreferMaxQualityCover,
                cancellationToken);
            return !string.IsNullOrWhiteSpace(downloaded) && File.Exists(downloaded);
        }

        var formats = (request.Settings.LocalArtworkFormat ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var format in formats)
        {
            var ext = format.Equals("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
            var targetPath = Path.Join(request.ArtistPath, $"{artistName}.{ext}");
            var downloaded = await request.ImageDownloader.DownloadImageAsync(
                request.ArtistImageUrl,
                targetPath,
                request.Settings.OverwriteFile,
                request.PreferMaxQualityCover,
                cancellationToken);
            anySaved |= !string.IsNullOrWhiteSpace(downloaded) && File.Exists(downloaded);
        }

        return anySaved;
    }
}
