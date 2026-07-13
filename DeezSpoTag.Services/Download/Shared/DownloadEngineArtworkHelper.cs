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
    private const string AppleProvider = "apple";
    private const string DeezerProvider = "deezer";
    private const string SpotifyProvider = "spotify";

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

        if (string.Equals(source, "lastfm", StringComparison.OrdinalIgnoreCase))
        {
            return TryResolveLastFmArtistImageAsync(request, cancellationToken);
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

        return string.IsNullOrWhiteSpace(request.Artist)
            ? null
            : await request.SpotifyArtworkResolver.ResolveArtistImageByNameAsync(request.Artist, cancellationToken);
    }

    private static async Task<string?> TryResolveLastFmArtistImageAsync(
        ArtistImageResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LastFmArtistImageResolver == null || string.IsNullOrWhiteSpace(request.Artist))
        {
            return null;
        }

        return await request.LastFmArtistImageResolver.ResolveArtistImageByNameAsync(request.Artist, cancellationToken);
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
