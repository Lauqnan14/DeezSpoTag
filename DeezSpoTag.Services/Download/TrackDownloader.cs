using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Exceptions;
using DeezSpoTag.Services.Crypto;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Apple;
using DeezerModels = DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Linq;
using System.Globalization;
using DeezSpoTag.Core.Enums;
using DeezSpoTag.Core.Utils;
using DeezSpoTagModels = DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download;

/// <summary>
/// Track downloader (ported from deezspotag download method)
/// </summary>
public class TrackDownloader
{
    private const string ArtistKey = "artist";
    private const string ArtistType = "artist";
    private const string UnknownArtist = "Unknown Artist";
    private const string DeezerSource = "deezer";
    private const string FetchingStatus = "fetching";
    private const string SkippedStatus = "skipped";
    private const string CompletedStatus = "completed";
    private const string FailedStatus = "failed";
    private const string NoLyricsStatus = "no-lyrics";
    private readonly ILogger<TrackDownloader> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EnhancedPathTemplateProcessor _pathProcessor;
    private readonly AudioTagger _audioTagger;
    private readonly ImageDownloader _imageDownloader;
    private readonly BitrateSelector _bitrateSelector;
    private readonly DecryptionStreamProcessor _streamProcessor;
    private readonly Utils.TrackEnrichmentService _trackEnrichmentService;
    private readonly SearchFallbackService _searchFallbackService;
    private readonly AuthenticatedDeezerService _authenticatedDeezerService;
    private readonly Utils.LyricsService _lyricsService;
    private readonly ISpotifyIdResolver _spotifyIdResolver;
    private readonly ISpotifyArtworkResolver _spotifyArtworkResolver;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly IDownloadTagSettingsResolver _tagSettingsResolver;
    private readonly IPostDownloadTaskScheduler _postDownloadTaskScheduler;
    private readonly IDeezSpoTagListener _deezspotagListener;
    private readonly IServiceProvider _serviceProvider;

    // File extensions mapping (ported from deezspotag extensions)
    private static readonly Dictionary<int, string> Extensions = new()
    {
        { 9, ".flac" },   // FLAC
        { 3, ".mp3" },    // MP3_320
        { 1, ".mp3" },    // MP3_128
        { 8, ".mp3" },    // DEFAULT
        { 13, ".mp4" },   // MP4_RA3
        { 14, ".mp4" },   // MP4_RA2
        { 15, ".mp4" },   // MP4_RA1
        { 0, ".mp3" }     // LOCAL
    };

    private static readonly HashSet<string> KnownAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac",
        ".mp3",
        ".m4a",
        ".mp4",
        ".m4b",
        ".aac",
        ".wav",
        ".aif",
        ".aiff",
        ".ogg",
        ".opus",
        ".alac",
        ".wma",
        ".ape"
    };

    private readonly string _tempDir;

    // Static dictionary to track active downloads and prevent duplicates
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _activeDownloads = new();

    private sealed class CoverResolutionRequest
    {
        public required Track Track { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required IReadOnlyList<string> FallbackOrder { get; init; }
        public required Album CoverAlbum { get; init; }
        public string? AlbumConstraint { get; init; }
        public bool HasDeezerCover { get; init; }
        public string? AppleTrackId { get; init; }
        public string? DeezerTrackId { get; init; }
        public DeezerClient? DeezerClient { get; init; }
    }

    private sealed class CoverResolutionState
    {
        public bool AllowAppleCover { get; init; }
        public bool AllowSpotifyCover { get; init; }
        public int AppleArtworkSize { get; init; }
        public string? ResolvedCoverUrl { get; set; }
        public string? SpotifyId { get; set; }
        public string? AppleCoverUrl { get; set; }
        public bool UseDeezerCover { get; set; }
        public bool CoverIsApple { get; set; }
    }

    private sealed class ArtistResolutionRequest
    {
        public required Track Track { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required IReadOnlyList<string> ArtistFallbackOrder { get; init; }
        public bool HasDeezerArtist { get; init; }
        public string? AppleTrackId { get; init; }
        public string? DeezerTrackId { get; init; }
        public string? SpotifyId { get; init; }
        public DeezerClient? DeezerClient { get; init; }
    }

    private sealed class ArtistResolutionState
    {
        public bool AllowAppleArtist { get; init; }
        public bool AllowSpotifyArtist { get; init; }
        public int AppleArtworkSize { get; init; }
        public string? ResolvedArtistUrl { get; set; }
        public bool ArtistIsApple { get; set; }
        public bool UseDeezerArtist { get; set; }
        public string? AppleArtistUrl { get; set; }
        public string? SpotifyArtistUrl { get; set; }
    }

    private sealed class EmbeddedCoverPathRequest
    {
        public required Track Track { get; init; }
        public Playlist? Playlist { get; init; }
        public required Album CoverAlbum { get; init; }
        public required string EmbeddedUrl { get; init; }
        public bool CoverIsApple { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public int EmbeddedSize { get; init; }
        public string? ResolvedCoverUrl { get; init; }
    }

    private sealed class ArtworkDownloadSetRequest
    {
        public required string ArtworkType { get; init; }
        public bool ShouldSave { get; init; }
        public string? OutputDirectory { get; init; }
        public string? Filename { get; init; }
        public IReadOnlyList<DeezSpoTagModels.ArtworkUrl>? Urls { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public int AppleArtworkSize { get; init; }
        public required Func<string, string?> OverwritePolicyResolver { get; init; }
    }

    private sealed class SingleArtworkDownloadRequest
    {
        public required string ArtworkType { get; init; }
        public required string OutputDirectory { get; init; }
        public required string Filename { get; init; }
        public required DeezSpoTagModels.ArtworkUrl ImageUrl { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public int AppleArtworkSize { get; init; }
        public string? OverwritePolicyOverride { get; init; }
    }

    private sealed class DeferredPostDownloadRequest
    {
        public required string QueueUuid { get; init; }
        public bool ShouldFetchArtwork { get; init; }
        public bool ShouldFetchLyrics { get; init; }
        public required Track Track { get; init; }
        public required DeezSpoTagModels.TrackDownloadResult Result { get; init; }
        public required PathGenerationResult PathResult { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required string ExpectedOutputPath { get; init; }
    }

    private sealed class AlbumArtworkPopulationRequest
    {
        public required DeezSpoTagModels.TrackDownloadResult Result { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required DeezSpoTagModels.PathGenerationResult PathResult { get; init; }
        public required Album CoverAlbum { get; init; }
        public Playlist? Playlist { get; init; }
        public string? ResolvedCoverUrl { get; init; }
        public bool CoverIsApple { get; init; }
        public string? AlbumMd5 { get; init; }
    }

    private sealed class TrackDownloadExecutionContext
    {
        public required Track Track { get; init; }
        public Album? Album { get; init; }
        public Playlist? Playlist { get; init; }
        public required DownloadObject DownloadObject { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required IDownloadListener? Listener { get; init; }
        public required TagSettings TagSettings { get; init; }
        public required DeezSpoTagModels.PathGenerationResult PathResult { get; init; }
        public required DeezSpoTagModels.TrackDownloadResult Result { get; init; }
        public required string Extension { get; init; }
        public required string WritePath { get; set; }
        public required int SelectedFormat { get; init; }
        public bool EnableDeferredSidecarTasks { get; init; } = true;
        public required CancellationToken CancellationToken { get; init; }
    }

    public sealed record TrackDownloadRequest(
        Track Track,
        Album? Album,
        Playlist? Playlist,
        DownloadObject DownloadObject,
        DeezSpoTagSettings Settings,
        IDownloadListener? Listener,
        bool EnableDeferredSidecarTasks = true,
        bool AllowInEngineBitrateFallback = true,
        CancellationToken CancellationToken = default);

    public TrackDownloader(
        ILogger<TrackDownloader> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        _pathProcessor = serviceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
        _audioTagger = serviceProvider.GetRequiredService<AudioTagger>();
        _imageDownloader = serviceProvider.GetRequiredService<ImageDownloader>();
        _bitrateSelector = serviceProvider.GetRequiredService<BitrateSelector>();
        _streamProcessor = serviceProvider.GetRequiredService<DecryptionStreamProcessor>();
        _trackEnrichmentService = serviceProvider.GetRequiredService<Utils.TrackEnrichmentService>();
        _searchFallbackService = serviceProvider.GetRequiredService<SearchFallbackService>();
        _authenticatedDeezerService = serviceProvider.GetRequiredService<AuthenticatedDeezerService>();
        _lyricsService = serviceProvider.GetRequiredService<Utils.LyricsService>();
        _spotifyIdResolver = serviceProvider.GetRequiredService<ISpotifyIdResolver>();
        _spotifyArtworkResolver = serviceProvider.GetRequiredService<ISpotifyArtworkResolver>();
        _appleCatalogService = serviceProvider.GetRequiredService<AppleMusicCatalogService>();
        _tagSettingsResolver = serviceProvider.GetRequiredService<IDownloadTagSettingsResolver>();
        _postDownloadTaskScheduler = serviceProvider.GetRequiredService<IPostDownloadTaskScheduler>();
        _deezspotagListener = serviceProvider.GetRequiredService<IDeezSpoTagListener>();

        _tempDir = Path.Join(Path.GetTempPath(), "deezspotag-imgs");
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>
    /// Download a single track (port of download method from deezspotag downloader)
    /// </summary>
    public async Task<DeezSpoTagModels.TrackDownloadResult> DownloadTrackAsync(TrackDownloadRequest request)
    {
        var track = request.Track;
        var album = request.Album;
        var playlist = request.Playlist;
        var downloadObject = request.DownloadObject;
        var settings = request.Settings;
        var listener = request.Listener;
        var allowInEngineBitrateFallback = request.AllowInEngineBitrateFallback;
        var cancellationToken = request.CancellationToken;

        var result = new DeezSpoTagModels.TrackDownloadResult
        {
            ItemData = new Dictionary<string, object>
            {
                { "id", track.Id ?? "0" },
                { "title", track.Title ?? "Unknown Title" },
                { ArtistKey, track.MainArtist?.Name ?? UnknownArtist }
            }
        };

        try
        {
            await EnsureTrackReadyForDownloadAsync(track, downloadObject, cancellationToken);
            var selectedFormat = await SelectAndApplyBitrateAsync(
                track,
                album,
                downloadObject,
                settings,
                listener,
                allowInEngineBitrateFallback);

            var resolvedDownloadTagSource = await ResolveAndApplyDownloadProfileAsync(
                downloadObject.DestinationFolderId,
                settings,
                cancellationToken);
            await ApplyProfileMetadataOverrideAsync(track, settings, resolvedDownloadTagSource, cancellationToken);
            track.ApplySettings(settings);
            var tagSettings = settings.Tags ?? new TagSettings();

            // Generate file paths
            var pathResult = _pathProcessor.GeneratePaths(track, downloadObject.Type, settings);
            var extension = Extensions.GetValueOrDefault(track.Bitrate, ".mp3");
            var writePath = Path.Join(pathResult.FilePath, $"{pathResult.Filename}{extension}");
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Downloading track to: {WritePath}", writePath);            }

            // Create directory
            Directory.CreateDirectory(pathResult.FilePath);

            // CRITICAL FIX: Prevent duplicate downloads of the same file
            var downloadKey = writePath.ToLowerInvariant();
            var semaphore = _activeDownloads.GetOrAdd(downloadKey, _ => new SemaphoreSlim(1, 1));

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var context = new TrackDownloadExecutionContext
                {
                    Track = track,
                    Album = album,
                    Playlist = playlist,
                    DownloadObject = downloadObject,
                    Settings = settings,
                    Listener = listener,
                    TagSettings = tagSettings,
                    PathResult = pathResult,
                    Result = result,
                    Extension = extension,
                    WritePath = writePath,
                    SelectedFormat = selectedFormat,
                    EnableDeferredSidecarTasks = request.EnableDeferredSidecarTasks,
                    CancellationToken = cancellationToken
                };

                return await ExecuteTrackDownloadAsync(context);

            } // End of semaphore try block
            finally
            {
                ReleaseActiveDownloadSemaphore(downloadKey, semaphore);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadException)
        {
            downloadObject.CompleteTrackProgress(listener);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            downloadObject.CompleteTrackProgress(listener);
            _logger.LogError(ex, "Error downloading track: {TrackId}", track.Id);
            throw new DownloadException($"Download failed: {ex.Message}", ex, "downloadError");
        }
    }

    private async Task EnsureTrackReadyForDownloadAsync(
        Track track,
        DownloadObject downloadObject,
        CancellationToken cancellationToken)
    {
        if (downloadObject.IsCancellationRequested(cancellationToken))
        {
            throw new OperationCanceledException("Download cancelled");
        }

        if (string.IsNullOrEmpty(track.Id) || track.Id == "0")
        {
            throw new DownloadException(
                "Spotify to Deezer mapping did not resolve to a valid Deezer track.",
                "mapping_miss");
        }

        await EnrichTrackDataAsync(track, cancellationToken);

        if (downloadObject.IsCancellationRequested(cancellationToken))
        {
            throw new OperationCanceledException("Download cancelled");
        }

        if (string.IsNullOrEmpty(track.MD5))
        {
            throw new DownloadException("Track is not encoded", "notEncoded");
        }
    }

    private async Task<int> SelectAndApplyBitrateAsync(
        Track track,
        Album? album,
        DownloadObject downloadObject,
        DeezSpoTagSettings settings,
        IDownloadListener? listener,
        bool allowInEngineBitrateFallback)
    {
        int selectedFormat;
        try
        {
            selectedFormat = await _bitrateSelector.GetPreferredBitrateAsync(
                track,
                downloadObject.Bitrate,
                settings.FallbackBitrate && allowInEngineBitrateFallback,
                settings.FeelingLucky,
                downloadObject.Uuid,
                listener);
        }
        catch (BitrateException ex)
        {
            var mappedErrorId = NormalizeBitrateErrorCode(ex.ErrorCode);
            throw new DownloadException(
                BuildBitrateFailureMessage(mappedErrorId, ex.Message),
                mappedErrorId,
                track);
        }

        track.Bitrate = selectedFormat;
        downloadObject.Bitrate = selectedFormat;
        if (album != null)
        {
            album.Bitrate = selectedFormat;
        }

        return selectedFormat;
    }

    private async Task<DeezSpoTagModels.TrackDownloadResult> ExecuteTrackDownloadAsync(TrackDownloadExecutionContext context)
    {
        var shouldDownload = DownloadUtils.CheckShouldDownload(
            context.PathResult.Filename,
            context.PathResult.FilePath,
            context.Extension,
            context.WritePath,
            EnumConverter.StringToOverwriteOption(context.Settings.OverwriteFile),
            context.Track);

        if (!shouldDownload)
        {
            return await HandleExistingTrackFileAsync(context);
        }

        ApplyKeepBothOverwriteMode(context);
        context.Result.ItemData = BuildTrackItemData(context.Track);
        EnsureExtrasPath(context);

        try
        {
            await GenerateCoverUrlsAsync(
                context.Track,
                context.Album,
                context.Playlist,
                context.Settings,
                context.Result,
                context.PathResult,
                context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Deferred Deezer artwork preparation failed for track {TrackId}; continuing with audio download.",
                context.Track.Id);
        }

        if (context.EnableDeferredSidecarTasks)
        {
            try
            {
                await QueueDeferredPostDownloadTasksAsync(
                    context.DownloadObject,
                    context.Track,
                    context.Result,
                    context.PathResult,
                    context.Settings,
                    context.WritePath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Deferred Deezer sidecar scheduling failed for track {TrackId}; continuing with audio download.",
                    context.Track.Id);
            }
        }

        var downloadUrl = await ResolveRequiredDownloadUrlAsync(context.Track, context.SelectedFormat);
        await StreamTrackToFileAsync(
            downloadUrl,
            context.WritePath,
            context.Track,
            context.DownloadObject,
            context.Settings,
            context.Listener,
            context.CancellationToken);

        EnsureTrackOutputExists(context.WritePath);
        await TagTrackIfNeededAsync(context);
        FinalizeTrackDownloadResult(context);
        context.Listener?.OnDownloadInfo(context.DownloadObject, "Track downloaded successfully", "downloaded");
        return context.Result;
    }

    private async Task<DeezSpoTagModels.TrackDownloadResult> HandleExistingTrackFileAsync(TrackDownloadExecutionContext context)
    {
        if (context.Settings.OverwriteFile == "t" || context.Settings.OverwriteFile == "y")
        {
            await TryTagTrackAsync(
                context,
                isExistingFile: true);
        }

        context.Result.Path = context.WritePath;
        context.Result.Filename = context.WritePath.Substring(context.PathResult.ExtrasPath.Length + 1);
        context.Result.GeneratedPathResult = context.PathResult;
        context.Result.ItemData = BuildTrackItemData(context.Track);

        context.DownloadObject.Files.Add(new DownloadFile
        {
            Filename = context.Result.Filename,
            Path = context.Result.Path,
            Data = context.Result.ItemData
        });

        context.DownloadObject.CompleteTrackProgress(context.Listener);
        context.DownloadObject.Downloaded++;
        context.Listener?.OnDownloadInfo(context.DownloadObject, "File already exists", "alreadyDownloaded");
        return context.Result;
    }

    private static Dictionary<string, object> BuildTrackItemData(Track track)
    {
        return new Dictionary<string, object>
        {
            { "id", track.Id ?? "0" },
            { "title", track.Title ?? "Unknown Title" },
            { ArtistKey, track.MainArtist?.Name ?? UnknownArtist }
        };
    }

    private static void ApplyKeepBothOverwriteMode(TrackDownloadExecutionContext context)
    {
        if (context.Settings.OverwriteFile != "b")
        {
            return;
        }

        var uniqueFilename = DownloadUtils.GenerateUniqueFilename(
            context.PathResult.FilePath,
            context.PathResult.Filename,
            context.Extension);
        context.WritePath = Path.Join(context.PathResult.FilePath, uniqueFilename + context.Extension);
    }

    private static void EnsureExtrasPath(TrackDownloadExecutionContext context)
    {
        if (!string.IsNullOrEmpty(context.PathResult.ExtrasPath) && string.IsNullOrEmpty(context.DownloadObject.ExtrasPath))
        {
            context.DownloadObject.ExtrasPath = context.PathResult.ExtrasPath;
        }
    }

    private async Task<string> ResolveRequiredDownloadUrlAsync(Track track, int selectedFormat)
    {
        var downloadUrl = await GetTrackDownloadUrlAsync(track, selectedFormat);
        if (!string.IsNullOrEmpty(downloadUrl))
        {
            return downloadUrl;
        }

        throw new DownloadException(
            "Mapped track exists, but a playable media URL could not be resolved.",
            BitrateSelector.ErrorMappedButQualityUnavailable,
            track);
    }

    private static void EnsureTrackOutputExists(string outputPath)
    {
        if (!System.IO.File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new DownloadException("File was not written to disk or is empty", "downloadError");
        }
    }

    private async Task TagTrackIfNeededAsync(TrackDownloadExecutionContext context)
    {
        if (context.Track.IsLocal)
        {
            return;
        }

        await TryTagTrackAsync(
            context,
            isExistingFile: false);
    }

    private async Task TryTagTrackAsync(
        TrackDownloadExecutionContext context,
        bool isExistingFile)
    {
        context.Listener?.OnDownloadInfo(context.DownloadObject, "Tagging track", "tagging");
        try
        {
            await EnsureLyricsForTaggingAsync(
                context.Track,
                context.Settings,
                context.TagSettings,
                context.CancellationToken);
            await _audioTagger.TagTrackAsync(context.Extension, context.WritePath, context.Track, context.TagSettings);
            context.Listener?.OnDownloadInfo(context.DownloadObject, "Track tagged", "tagged");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (isExistingFile)
            {
                _logger.LogWarning(ex, "Tagging existing Deezer file failed for {Path}; keeping audio file.", context.WritePath);
                context.Listener?.OnDownloadInfo(context.DownloadObject, "Track tag update failed; keeping audio file", "tagWarning");
                return;
            }

            _logger.LogWarning(ex, "Tagging Deezer download failed for {Path}; keeping audio file.", context.WritePath);
            context.Listener?.OnDownloadInfo(context.DownloadObject, "Track tagging failed; keeping audio file", "tagWarning");
            throw new DownloadException(
                $"Download tagging failed for {context.WritePath}",
                "taggingFailed",
                context.Track);
        }
    }

    private static void FinalizeTrackDownloadResult(TrackDownloadExecutionContext context)
    {
        context.Result.Path = context.WritePath;
        context.Result.Filename = context.WritePath.Substring(context.PathResult.ExtrasPath.Length + 1);
        context.Result.GeneratedPathResult = context.PathResult;
        context.Result.Searched = context.Track.Searched;

        context.DownloadObject.Downloaded++;
        context.DownloadObject.Files.Add(new DownloadFile
        {
            Filename = context.Result.Filename,
            Path = context.Result.Path,
            Data = context.Result.ItemData,
            Searched = context.Result.Searched,
            AlbumUrls = context.Result.AlbumURLs?.Select(u => new ImageUrl { Url = u.Url, Extension = u.Ext }).ToList() ?? new List<ImageUrl>(),
            ArtistUrls = context.Result.ArtistURLs?.Select(u => new ImageUrl { Url = u.Url, Extension = u.Ext }).ToList() ?? new List<ImageUrl>(),
            AlbumPath = context.Result.AlbumPath ?? string.Empty,
            ArtistPath = context.Result.ArtistPath ?? string.Empty,
            AlbumFilename = context.Result.AlbumFilename ?? string.Empty,
            ArtistFilename = context.Result.ArtistFilename ?? string.Empty
        });
    }

    private static void ReleaseActiveDownloadSemaphore(string downloadKey, SemaphoreSlim semaphore)
    {
        semaphore.Release();

        // Clean up semaphore only when this key is no longer contended.
        if (semaphore.CurrentCount != 1)
        {
            return;
        }

        if (_activeDownloads.TryRemove(downloadKey, out var removed))
        {
            removed.Dispose();
        }
    }

    private async Task<string?> ResolveAndApplyDownloadProfileAsync(
        long? destinationFolderId,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DownloadEngineSettingsHelper.ResolveAndApplyProfileAsync(
                _tagSettingsResolver,
                settings,
                destinationFolderId,
                _logger,
                cancellationToken,
                new DownloadEngineSettingsHelper.ProfileResolutionOptions(
                    CurrentEngine: DeezerSource,
                    RequireProfile: destinationFolderId.HasValue));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to apply download profile for folder {FolderId}", destinationFolderId);
            throw new InvalidOperationException(
                $"Failed to apply download profile for folder {destinationFolderId}.",
                ex);
        }
    }

    private async Task ApplyProfileMetadataOverrideAsync(
        Track track,
        DeezSpoTagSettings settings,
        string? resolvedDownloadTagSource,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resolvedDownloadTagSource))
        {
            return;
        }

        await EngineAudioPostDownloadHelper.ApplyProfileMetadataOverrideAsync(
            track,
            new TrackDownloaderMetadataPayload
            {
                DeezerId = track.Id ?? string.Empty,
                Title = track.Title ?? string.Empty,
                Artist = track.MainArtist?.Name ?? string.Empty,
                Album = track.Album?.Title ?? string.Empty,
                Isrc = track.ISRC ?? string.Empty
            },
            settings,
            _serviceProvider,
            DeezerSource,
            resolvedDownloadTagSource,
            _logger,
            cancellationToken);
    }

    private sealed class TrackDownloaderMetadataPayload : EngineQueueItemBase
    {
    }

    private static string NormalizeBitrateErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return BitrateSelector.ErrorMappedButQualityUnavailable;
        }

        return errorCode switch
        {
            "WrongLicense" => BitrateSelector.ErrorMappedButLicenseBlocked,
            "WrongGeolocation" => BitrateSelector.ErrorMappedButGeoBlocked,
            "PreferredBitrateNotFound" => BitrateSelector.ErrorMappedButQualityUnavailable,
            "TrackNot360" => BitrateSelector.ErrorMappedButQualityUnavailable360,
            _ => errorCode
        };
    }

    private static string BuildBitrateFailureMessage(string errorCode, string fallbackMessage)
    {
        return errorCode switch
        {
            BitrateSelector.ErrorMappedButLicenseBlocked =>
                "Mapped track exists, but the current Deezer account cannot stream the requested quality.",
            BitrateSelector.ErrorMappedButGeoBlocked =>
                "Mapped track exists, but it is blocked in the current Deezer region.",
            BitrateSelector.ErrorTransientTokenFailure =>
                "Mapped track exists, but track-token validation failed. Retry may succeed.",
            BitrateSelector.ErrorTransientNetworkFailure =>
                "Mapped track exists, but availability checks failed due to transient network/auth issues.",
            BitrateSelector.ErrorMappedButQualityUnavailable360 =>
                "Mapped track exists, but 360/Atmos quality is unavailable for this track.",
            BitrateSelector.ErrorMappedButQualityUnavailable =>
                "Mapped track exists, but the requested quality is unavailable on Deezer media endpoints.",
            _ => string.IsNullOrWhiteSpace(fallbackMessage)
                ? "Mapped track exists, but bitrate selection failed."
                : fallbackMessage
        };
    }


    /// <summary>
    /// Enrich track with additional data
    /// </summary>
    private async Task EnrichTrackDataAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(track.Id) || track.Id == "0")
        {
            return;
        }

        var deezerClient = await TryGetAuthenticatedClientForEnrichmentAsync(track.Id);
        if (deezerClient != null)
        {
            await TryApplySharedEnrichmentAsync(track, cancellationToken);
            await TryApplyPublicTrackEnrichmentAsync(track, deezerClient);
            await TryApplyPublicAlbumEnrichmentAsync(track, deezerClient);
        }

        if (string.IsNullOrWhiteSpace(track.MD5) || string.IsNullOrWhiteSpace(track.MediaVersion))
        {
            await TryApplySearchFallbackEnrichmentAsync(track, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(track.MD5))
        {
            _logger.LogWarning("Track MD5 not available for track: {TrackId}", track.Id);
        }
    }

    private async Task<DeezerClient?> TryGetAuthenticatedClientForEnrichmentAsync(string trackId)
    {
        try
        {
            var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
            if (deezerClient == null)
            {
                _logger.LogWarning("Cannot enrich track {TrackId} - Deezer client not authenticated", trackId);
            }

            return deezerClient;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GW enrichment failed for track {TrackId}", trackId);
            return null;
        }
    }

    private async Task TryApplySharedEnrichmentAsync(Track track, CancellationToken cancellationToken)
    {
        try
        {
            await _trackEnrichmentService.EnrichCoreTrackAsync(track, new Dictionary<string, object>(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Shared enrichment engine failed for track {TrackId}", track.Id);            }
        }
    }

    private async Task TryApplyPublicTrackEnrichmentAsync(Track track, DeezerClient deezerClient)
    {
        try
        {
            var apiTrack = await deezerClient.GetTrackAsync(track.Id);
            if (apiTrack == null)
            {
                return;
            }

            MergePublicTrackMetadata(track, apiTrack);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Public API track enrichment failed for {TrackId}", track.Id);            }
        }
    }

    private async Task TryApplyPublicAlbumEnrichmentAsync(Track track, DeezerClient deezerClient)
    {
        if (track.Album == null || string.IsNullOrWhiteSpace(track.Album.Id))
        {
            return;
        }

        try
        {
            var apiAlbum = await deezerClient.GetAlbumAsync(track.Album.Id);
            if (apiAlbum == null)
            {
                return;
            }

            MergePublicAlbumMetadata(track.Album, apiAlbum);
            ApplyAlbumCoverIfMissing(track.Album, apiAlbum.Md5Image);
            MergeAlbumGenres(track.Album, apiAlbum);
            TryApplyAlbumReleaseDate(track.Album, apiAlbum.ReleaseDate);
            MergeAlbumMainArtist(track.Album, apiAlbum.Artist);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Public API album enrichment failed for {AlbumId}", track.Album.Id);            }
        }
    }

    private async Task TryApplySearchFallbackEnrichmentAsync(Track track, CancellationToken cancellationToken)
    {
        try
        {
            var fallback = await _searchFallbackService.SearchForAlternativeTrackAsync(track, cancellationToken);
            if (fallback == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(track.MD5))
            {
                track.MD5 = fallback.MD5;
            }

            if (string.IsNullOrWhiteSpace(track.MediaVersion))
            {
                track.MediaVersion = fallback.MediaVersion;
            }

            if (string.IsNullOrWhiteSpace(track.TrackToken))
            {
                track.TrackToken = fallback.TrackToken;
            }

            if (track.FileSizes == null || track.FileSizes.Count == 0)
            {
                track.FileSizes = fallback.FileSizes;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Search fallback enrichment failed for track {TrackId}", track.Id);            }
        }
    }


    /// <summary>
    /// Generate cover URLs and paths
    /// </summary>
    private async Task GenerateCoverUrlsAsync(
        Track track,
        Album? album,
        Playlist? playlist,
        DeezSpoTagSettings settings,
        DeezSpoTagModels.TrackDownloadResult result,
        DeezSpoTagModels.PathGenerationResult pathResult,
        CancellationToken cancellationToken)
    {
        var coverAlbum = album ?? track.Album ?? new Album("Unknown Album");
        var fallbackOrder = ResolveArtworkFallbackOrder(settings);
        var artistFallbackOrder = ResolveArtistArtworkFallbackOrder(settings);
        var albumConstraint = ArtworkFallbackHelper.ResolveAlbumConstraintForArtwork(coverAlbum.Title);
        var hasDeezerCover = coverAlbum.Pic != null && !string.IsNullOrEmpty(coverAlbum.Pic.Md5);
        var hasDeezerArtist = coverAlbum.MainArtist?.Pic != null && !string.IsNullOrEmpty(coverAlbum.MainArtist.Pic.Md5);
        var appleTrackId = ArtworkFallbackHelper.TryExtractAppleTrackId(track);
        var deezerTrackId = ArtworkFallbackHelper.TryExtractDeezerTrackId(track);

        var deezerClient = await TryGetDeezerArtworkClientAsync(fallbackOrder, artistFallbackOrder);
        var coverResolution = await ResolveCoverArtworkAsync(
            new CoverResolutionRequest
            {
                Track = track,
                Settings = settings,
                FallbackOrder = fallbackOrder,
                CoverAlbum = coverAlbum,
                AlbumConstraint = albumConstraint,
                HasDeezerCover = hasDeezerCover,
                AppleTrackId = appleTrackId,
                DeezerTrackId = deezerTrackId,
                DeezerClient = deezerClient
            },
            cancellationToken);

        var (resolvedCoverUrl, coverIsApple, useDeezerCover, spotifyId) = coverResolution;
        var artistResolution = await ResolveArtistArtworkAsync(
            new ArtistResolutionRequest
            {
                Track = track,
                Settings = settings,
                ArtistFallbackOrder = artistFallbackOrder,
                HasDeezerArtist = hasDeezerArtist,
                AppleTrackId = appleTrackId,
                DeezerTrackId = deezerTrackId,
                SpotifyId = spotifyId,
                DeezerClient = deezerClient
            },
            cancellationToken);

        var (resolvedArtistUrl, artistIsApple, useDeezerArtist) = artistResolution;

        if (!TryResolveAlbumMd5(coverAlbum, resolvedCoverUrl, useDeezerCover, out var albumMd5))
        {
            return;
        }

        var embeddedSize = settings.EmbedMaxQualityCover ? settings.LocalArtworkSize : settings.EmbeddedArtworkSize;
        var embeddedFormat = ResolveEmbeddedFormat(settings);
        var embeddedUrl = !string.IsNullOrWhiteSpace(resolvedCoverUrl)
            ? resolvedCoverUrl
            : GeneratePictureUrl(albumMd5!, embeddedSize, embeddedFormat);
        var embeddedPath = BuildEmbeddedCoverPath(
            new EmbeddedCoverPathRequest
            {
                Track = track,
                Playlist = playlist,
                CoverAlbum = coverAlbum,
                EmbeddedUrl = embeddedUrl,
                CoverIsApple = coverIsApple,
                Settings = settings,
                EmbeddedSize = embeddedSize,
                ResolvedCoverUrl = resolvedCoverUrl
            });

        await TryDownloadEmbeddedCoverAsync(embeddedPath, embeddedUrl, coverIsApple, settings, embeddedSize, cancellationToken);
        if (System.IO.File.Exists(embeddedPath))
        {
            coverAlbum.EmbeddedCoverPath = embeddedPath;
        }

        PopulateAlbumArtworkResult(
            new AlbumArtworkPopulationRequest
            {
                Result = result,
                Settings = settings,
                PathResult = pathResult,
                CoverAlbum = coverAlbum,
                Playlist = playlist,
                ResolvedCoverUrl = resolvedCoverUrl,
                CoverIsApple = coverIsApple,
                AlbumMd5 = albumMd5
            });
        PopulateArtistArtworkResult(result, settings, pathResult, coverAlbum, resolvedArtistUrl, artistIsApple, useDeezerArtist);
    }

    private async Task<DeezerClient?> TryGetDeezerArtworkClientAsync(
        IReadOnlyList<string> fallbackOrder,
        IReadOnlyList<string> artistFallbackOrder)
    {
        var needsDeezerClient = fallbackOrder.Contains(DeezerSource, StringComparer.OrdinalIgnoreCase)
            || artistFallbackOrder.Contains(DeezerSource, StringComparer.OrdinalIgnoreCase);
        if (!needsDeezerClient)
        {
            return null;
        }

        try
        {
            return await _authenticatedDeezerService.GetAuthenticatedClientAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Unable to acquire Deezer client for artwork fallback.");
            return null;
        }
    }

    private async Task<(string? resolvedCoverUrl, bool coverIsApple, bool useDeezerCover, string? spotifyId)> ResolveCoverArtworkAsync(
        CoverResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var state = new CoverResolutionState
        {
            AllowAppleCover = AllowsJpegArtwork(request.Settings),
            AllowSpotifyCover = AllowsJpegArtwork(request.Settings),
            AppleArtworkSize = AppleQueueHelpers.GetAppleArtworkSize(request.Settings)
        };

        foreach (var source in request.FallbackOrder)
        {
            var shouldStop = source switch
            {
                "apple" => await TryHandleAppleCoverSourceAsync(request, state, cancellationToken),
                DeezerSource => await TryHandleDeezerCoverSourceAsync(request, state, cancellationToken),
                "spotify" => await TryHandleSpotifyCoverSourceAsync(request, state, cancellationToken),
                _ => false
            };

            if (shouldStop)
            {
                break;
            }
        }

        return (state.ResolvedCoverUrl, state.CoverIsApple, state.UseDeezerCover, state.SpotifyId);
    }

    private async Task<bool> TryHandleAppleCoverSourceAsync(
        CoverResolutionRequest request,
        CoverResolutionState state,
        CancellationToken cancellationToken)
    {
        if (!state.AllowAppleCover)
        {
            return false;
        }

        state.AppleCoverUrl = await ResolveAppleCoverUrlAsync(
            request.Track,
            request.Settings,
            request.AppleTrackId,
            request.AlbumConstraint,
            state.AppleArtworkSize,
            state.AppleCoverUrl,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(state.AppleCoverUrl))
        {
            return false;
        }

        state.ResolvedCoverUrl = state.AppleCoverUrl;
        state.CoverIsApple = true;
        return true;
    }

    private async Task<bool> TryHandleDeezerCoverSourceAsync(
        CoverResolutionRequest request,
        CoverResolutionState state,
        CancellationToken cancellationToken)
    {
        var deezerCoverUrl = await ResolveDeezerCoverUrlAsync(
            request.Track,
            request.Settings,
            request.CoverAlbum,
            request.AlbumConstraint,
            request.DeezerTrackId,
            request.DeezerClient,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(deezerCoverUrl))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Deezer album art selected: {Url}", deezerCoverUrl);            }
            state.ResolvedCoverUrl = deezerCoverUrl;
            return true;
        }

        if (!request.HasDeezerCover)
        {
            return false;
        }

        state.UseDeezerCover = true;
        return true;
    }

    private async Task<bool> TryHandleSpotifyCoverSourceAsync(
        CoverResolutionRequest request,
        CoverResolutionState state,
        CancellationToken cancellationToken)
    {
        if (!state.AllowSpotifyCover || string.IsNullOrWhiteSpace(request.Track.ISRC))
        {
            return false;
        }

        state.SpotifyId ??= await _spotifyIdResolver.ResolveTrackIdAsync(
            request.Track.Title ?? string.Empty,
            request.Track.MainArtist?.Name ?? string.Empty,
            request.AlbumConstraint,
            request.Track.ISRC,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(state.SpotifyId))
        {
            return false;
        }

        var spotifyCoverUrl = await _spotifyArtworkResolver.ResolveAlbumCoverUrlAsync(state.SpotifyId, cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifyCoverUrl))
        {
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Spotify album art selected: {Url}", spotifyCoverUrl);        }
        state.ResolvedCoverUrl = spotifyCoverUrl;
        return true;
    }

    private async Task<string?> ResolveAppleCoverUrlAsync(
        Track track,
        DeezSpoTagSettings settings,
        string? appleTrackId,
        string? albumConstraint,
        int appleArtworkSize,
        string? currentValue,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(currentValue))
        {
            return currentValue;
        }

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
        var appleCoverUrl = await AppleQueueHelpers.ResolveAppleCoverFromCatalogAsync(
            _appleCatalogService,
            new AppleQueueHelpers.AppleCatalogCoverLookup
            {
                AppleId = appleTrackId,
                Title = track.Title,
                Artist = track.MainArtist?.Name,
                Album = albumConstraint,
                Storefront = storefront,
                Size = appleArtworkSize,
                Logger = _logger
            },
            cancellationToken);

        if (string.IsNullOrWhiteSpace(appleCoverUrl))
        {
            appleCoverUrl = await AppleQueueHelpers.ResolveAppleCoverAsync(
                _httpClientFactory,
                track.Title,
                track.MainArtist?.Name,
                albumConstraint,
                appleArtworkSize,
                _logger,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(appleCoverUrl) && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Apple album art selected: {Url}", appleCoverUrl);
        }

        return appleCoverUrl;
    }

    private async Task<string?> ResolveDeezerCoverUrlAsync(
        Track track,
        DeezSpoTagSettings settings,
        Album coverAlbum,
        string? albumConstraint,
        string? deezerTrackId,
        DeezerClient? deezerClient,
        CancellationToken cancellationToken)
    {
        var artworkTrackId = deezerTrackId;
        if (deezerClient != null && ArtworkFallbackHelper.IsCompilationLikeAlbum(coverAlbum))
        {
            var preferredTrackId = await TryResolvePreferredArtworkTrackIdAsync(
                deezerClient,
                track,
                albumConstraint,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(preferredTrackId))
            {
                artworkTrackId = preferredTrackId;
            }
        }

        var deezerCoverUrl = await ArtworkFallbackHelper.TryResolveDeezerCoverAsync(
            deezerClient,
            artworkTrackId,
            settings.LocalArtworkSize,
            _logger,
            cancellationToken,
            albumConstraint);

        if (!string.IsNullOrWhiteSpace(deezerCoverUrl)
            || string.IsNullOrWhiteSpace(artworkTrackId)
            || string.Equals(artworkTrackId, deezerTrackId, StringComparison.Ordinal))
        {
            return deezerCoverUrl;
        }

        return await ArtworkFallbackHelper.TryResolveDeezerCoverAsync(
            deezerClient,
            deezerTrackId,
            settings.LocalArtworkSize,
            _logger,
            cancellationToken,
            albumConstraint);
    }

    private async Task<(string? resolvedArtistUrl, bool artistIsApple, bool useDeezerArtist)> ResolveArtistArtworkAsync(
        ArtistResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var state = new ArtistResolutionState
        {
            AllowAppleArtist = AllowsJpegArtistArtwork(request.Settings),
            AllowSpotifyArtist = AllowsJpegArtistArtwork(request.Settings),
            AppleArtworkSize = AppleQueueHelpers.GetAppleArtworkSize(request.Settings)
        };

        foreach (var source in request.ArtistFallbackOrder)
        {
            var shouldStop = source switch
            {
                "apple" => await TryHandleAppleArtistSourceAsync(request, state, cancellationToken),
                DeezerSource => await TryHandleDeezerArtistSourceAsync(request, state, cancellationToken),
                "spotify" => await TryHandleSpotifyArtistSourceAsync(request, state, cancellationToken),
                _ => false
            };

            if (shouldStop)
            {
                break;
            }
        }

        return (state.ResolvedArtistUrl, state.ArtistIsApple, state.UseDeezerArtist);
    }

    private async Task<bool> TryHandleAppleArtistSourceAsync(
        ArtistResolutionRequest request,
        ArtistResolutionState state,
        CancellationToken cancellationToken)
    {
        if (!state.AllowAppleArtist || string.IsNullOrWhiteSpace(request.Track.MainArtist?.Name))
        {
            return false;
        }

        state.AppleArtistUrl = await ResolveAppleArtistUrlAsync(
            request.Track,
            request.Settings,
            request.AppleTrackId,
            state.AppleArtworkSize,
            state.AppleArtistUrl,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(state.AppleArtistUrl))
        {
            return false;
        }

        state.ResolvedArtistUrl = state.AppleArtistUrl;
        state.ArtistIsApple = true;
        return true;
    }

    private async Task<bool> TryHandleDeezerArtistSourceAsync(
        ArtistResolutionRequest request,
        ArtistResolutionState state,
        CancellationToken cancellationToken)
    {
        var deezerArtistUrl = await ArtworkFallbackHelper.TryResolveDeezerArtistImageAsync(
            request.DeezerClient,
            request.DeezerTrackId,
            request.Settings.LocalArtworkSize,
            _logger,
            cancellationToken,
            request.Track.MainArtist?.Name);
        if (!string.IsNullOrWhiteSpace(deezerArtistUrl))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Deezer artist art selected: {Url}", deezerArtistUrl);            }
            state.ResolvedArtistUrl = deezerArtistUrl;
            return true;
        }

        if (!request.HasDeezerArtist)
        {
            return false;
        }

        state.UseDeezerArtist = true;
        return true;
    }

    private async Task<bool> TryHandleSpotifyArtistSourceAsync(
        ArtistResolutionRequest request,
        ArtistResolutionState state,
        CancellationToken cancellationToken)
    {
        if (!state.AllowSpotifyArtist)
        {
            return false;
        }

        state.SpotifyArtistUrl = await ResolveSpotifyArtistUrlAsync(
            request.Track,
            request.SpotifyId,
            state.SpotifyArtistUrl,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(state.SpotifyArtistUrl))
        {
            return false;
        }

        state.ResolvedArtistUrl = state.SpotifyArtistUrl;
        return true;
    }

    private async Task<string?> ResolveAppleArtistUrlAsync(
        Track track,
        DeezSpoTagSettings settings,
        string? appleTrackId,
        int appleArtworkSize,
        string? currentValue,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(currentValue))
        {
            return currentValue;
        }

        var artistName = track.MainArtist?.Name;
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var artistStorefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
        try
        {
            var appleArtistUrl = string.Empty;
            if (!string.IsNullOrWhiteSpace(appleTrackId))
            {
                appleArtistUrl = await AppleQueueHelpers.ResolveAppleArtistImageFromSongAsync(
                    _appleCatalogService,
                    appleTrackId,
                    artistStorefront,
                    appleArtworkSize,
                    _logger,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(appleArtistUrl))
            {
                appleArtistUrl = await AppleQueueHelpers.ResolveAppleArtistImageAsync(
                    _appleCatalogService,
                    artistName,
                    artistStorefront,
                    appleArtworkSize,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(appleArtistUrl))
            {
                appleArtistUrl = await ArtworkFallbackHelper.TryResolveAppleArtistImageAsync(
                    _appleCatalogService,
                    _httpClientFactory,
                    settings,
                    appleTrackId,
                    artistName,
                    _logger,
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(appleArtistUrl) && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Apple artist art selected: {Url}", appleArtistUrl);
            }

            return appleArtistUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple artist lookup failed for {ArtistName}", artistName);            }
            return null;
        }
    }

    private async Task<string?> ResolveSpotifyArtistUrlAsync(
        Track track,
        string? spotifyId,
        string? currentValue,
        CancellationToken cancellationToken)
    {
        var spotifyArtistUrl = currentValue;
        if (string.IsNullOrWhiteSpace(spotifyArtistUrl) && !string.IsNullOrWhiteSpace(spotifyId))
        {
            spotifyArtistUrl = await _spotifyArtworkResolver.ResolveArtistImageUrlAsync(spotifyId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(spotifyArtistUrl))
        {
            spotifyArtistUrl = await _spotifyArtworkResolver.ResolveArtistImageByNameAsync(
                track.MainArtist?.Name,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(spotifyArtistUrl) && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Spotify artist art selected by name: {Url}", spotifyArtistUrl);
            }
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Spotify artist art selected: {Url}", spotifyArtistUrl);            }
        }

        return spotifyArtistUrl;
    }

    private bool TryResolveAlbumMd5(Album coverAlbum, string? resolvedCoverUrl, bool useDeezerCover, out string? albumMd5)
    {
        albumMd5 = null;
        if (!string.IsNullOrWhiteSpace(resolvedCoverUrl))
        {
            return true;
        }

        if (coverAlbum.Pic == null || string.IsNullOrEmpty(coverAlbum.Pic.Md5) || !useDeezerCover)
        {
            _logger.LogDebug("Skipping artwork generation - no album picture or empty MD5");
            return false;
        }

        albumMd5 = coverAlbum.Pic.Md5;
        return true;
    }

    private string BuildEmbeddedCoverPath(EmbeddedCoverPathRequest request)
    {
        string ext;
        if (request.CoverIsApple)
        {
            var appleExtension = AppleQueueHelpers.GetAppleArtworkExtension(
                request.ResolvedCoverUrl ?? string.Empty,
                AppleQueueHelpers.GetAppleArtworkFormat(request.Settings));
            ext = $".{appleExtension}";
        }
        else
        {
            ext = request.EmbeddedUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        }

        string coverKey;
        if (request.Playlist != null)
        {
            coverKey = $"pl{request.Playlist.Id}";
        }
        else if (!string.IsNullOrWhiteSpace(request.CoverAlbum.Id))
        {
            coverKey = $"alb{request.CoverAlbum.Id}";
        }
        else
        {
            coverKey = $"trk{request.Track.Id}";
        }

        return Path.Join(_tempDir, $"{coverKey}_{request.EmbeddedSize}{ext}");
    }

    private async Task TryDownloadEmbeddedCoverAsync(
        string embeddedPath,
        string embeddedUrl,
        bool coverIsApple,
        DeezSpoTagSettings settings,
        int embeddedSize,
        CancellationToken cancellationToken)
    {
        if (System.IO.File.Exists(embeddedPath))
        {
            return;
        }

        try
        {
            if (coverIsApple)
            {
                await AppleQueueHelpers.DownloadAppleArtworkAsync(
                    _imageDownloader,
                    new AppleQueueHelpers.AppleArtworkDownloadRequest
                    {
                        RawUrl = embeddedUrl,
                        OutputPath = embeddedPath,
                        Settings = settings,
                        Size = embeddedSize,
                        Overwrite = settings.OverwriteFile,
                        PreferMaxQuality = true,
                        Logger = _logger
                    },
                    cancellationToken);
            }
            else
            {
                await _imageDownloader.DownloadImageAsync(
                    embeddedUrl,
                    embeddedPath,
                    preferMaxQuality: true,
                    cancellationToken: cancellationToken);
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Downloaded embedded cover: {Url} to {Path}", embeddedUrl, embeddedPath);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download embedded cover: {Url}", embeddedUrl);
        }
    }

    private void PopulateAlbumArtworkResult(AlbumArtworkPopulationRequest request)
    {
        if (!request.Settings.SaveArtwork)
        {
            return;
        }

        var formats = ParseArtworkFormats(request.Settings.LocalArtworkFormat);
        foreach (var format in formats)
        {
            if (!string.IsNullOrWhiteSpace(request.ResolvedCoverUrl))
            {
                request.Result.AlbumURLs ??= new List<DeezSpoTagModels.ArtworkUrl>();
                if (request.CoverIsApple || format == "jpg")
                {
                    request.Result.AlbumURLs.Add(new DeezSpoTagModels.ArtworkUrl { Url = request.ResolvedCoverUrl, Ext = format });
                }

                continue;
            }

            var formatExt = format == "jpg" ? $"jpg-{request.Settings.JpegImageQuality}" : format;
            var url = GeneratePictureUrl(request.AlbumMd5!, request.Settings.LocalArtworkSize, formatExt);
            request.Result.AlbumURLs ??= new List<DeezSpoTagModels.ArtworkUrl>();
            request.Result.AlbumURLs.Add(new DeezSpoTagModels.ArtworkUrl { Url = url, Ext = format });
        }

        request.Result.AlbumPath = request.PathResult.CoverPath;
        request.Result.AlbumFilename = _pathProcessor.GenerateAlbumName(
            request.Settings.CoverImageTemplate,
            request.CoverAlbum,
            request.Settings,
            request.Playlist);
    }

    private void PopulateArtistArtworkResult(
        DeezSpoTagModels.TrackDownloadResult result,
        DeezSpoTagSettings settings,
        DeezSpoTagModels.PathGenerationResult pathResult,
        Album coverAlbum,
        string? resolvedArtistUrl,
        bool artistIsApple,
        bool useDeezerArtist)
    {
        if (!settings.SaveArtworkArtist)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(resolvedArtistUrl))
        {
            var formats = ParseArtworkFormats(settings.LocalArtworkFormat);
            foreach (var format in formats)
            {
                result.ArtistURLs ??= new List<DeezSpoTagModels.ArtworkUrl>();
                if (artistIsApple || format == "jpg")
                {
                    result.ArtistURLs.Add(new DeezSpoTagModels.ArtworkUrl { Url = resolvedArtistUrl, Ext = format });
                }
            }

            result.ArtistPath = pathResult.ArtistPath ?? pathResult.CoverPath ?? pathResult.FilePath;
            result.ArtistFilename = _pathProcessor.GenerateArtistName(settings.ArtistImageTemplate, coverAlbum.MainArtist, settings, coverAlbum.RootArtist);
            return;
        }

        if (coverAlbum.MainArtist?.Pic == null || string.IsNullOrEmpty(coverAlbum.MainArtist.Pic.Md5) || !useDeezerArtist)
        {
            return;
        }

        var artistPicMd5 = coverAlbum.MainArtist.Pic.Md5;
        if (IsKnownPlaceholderArtistImage(artistPicMd5))
        {
            return;
        }

        foreach (var format in ParseArtworkFormats(settings.LocalArtworkFormat).Where(static format => format == "jpg"))
        {
            var formatExt = $"jpg-{settings.JpegImageQuality}";
            var url = GeneratePictureUrl(artistPicMd5, settings.LocalArtworkSize, formatExt, ArtistType);
            if (!string.IsNullOrEmpty(url))
            {
                result.ArtistURLs ??= new List<DeezSpoTagModels.ArtworkUrl>();
                result.ArtistURLs.Add(new DeezSpoTagModels.ArtworkUrl { Url = url, Ext = format });
            }
        }

        result.ArtistPath = pathResult.ArtistPath ?? pathResult.CoverPath ?? pathResult.FilePath;
        result.ArtistFilename = _pathProcessor.GenerateArtistName(settings.ArtistImageTemplate, coverAlbum.MainArtist, settings, coverAlbum.RootArtist);
    }

    private static List<string> ParseArtworkFormats(string? localArtworkFormat)
    {
        return (localArtworkFormat ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(format => format is "jpg" or "png")
            .ToList();
    }

    private static bool IsKnownPlaceholderArtistImage(string? artistPicMd5)
    {
        return string.IsNullOrWhiteSpace(artistPicMd5)
            || string.Equals(artistPicMd5, "d41d8cd98f00b204e9800998ecf8427e", StringComparison.OrdinalIgnoreCase)
            || string.Equals(artistPicMd5, "522c7b1de6d02790c348da447d3fd2b7", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Generate picture URL from MD5 hash for covers
    /// </summary>
    private string GeneratePictureUrl(string md5, int size, string format)
    {
        return GeneratePictureUrl(md5, size, format, "cover");
    }

    /// <summary>
    /// Generate picture URL from MD5 hash with type
    /// </summary>
    private string GeneratePictureUrl(string md5, int size, string format, string type)
    {
        // CRITICAL FIX: Handle empty MD5 gracefully - return empty string instead of throwing
        if (string.IsNullOrEmpty(md5))
        {
            _logger.LogDebug("Empty MD5 provided for picture URL generation");
            return "";
        }

        var url = $"https://e-cdns-images.dzcdn.net/images/{type}/{md5}/{size}x{size}";

        // Handle format exactly like deezspotag Picture.getURL
        if (format.StartsWith("jpg"))
        {
            var quality = 80;
            if (format.Contains('-'))
            {
                var qualityStr = format.Substring(4); // Remove "jpg-" prefix
                if (int.TryParse(qualityStr, out var parsedQuality))
                {
                    quality = parsedQuality;
                }
            }
            return $"{url}-000000-{quality}-0-0.jpg";
        }

        if (format == "png")
        {
            return $"{url}-none-100-0-0.png";
        }

        return $"{url}.jpg";
    }

    private static string ResolveEmbeddedFormat(DeezSpoTagSettings settings)
    {
        var formats = (settings.LocalArtworkFormat ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(format => format.ToLowerInvariant())
            .ToList();

        if (formats.Contains("png"))
        {
            return "png";
        }

        return $"jpg-{settings.JpegImageQuality}";
    }

    private static IReadOnlyList<string> ResolveArtworkFallbackOrder(DeezSpoTagSettings settings)
    {
        return ArtworkFallbackHelper.ResolveOrder(settings);
    }

    private static List<string> ResolveArtistArtworkFallbackOrder(DeezSpoTagSettings settings)
    {
        var configuredOrder = ArtworkFallbackHelper.ResolveArtistOrder(settings)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .ToList();

        configuredOrder.RemoveAll(source => string.Equals(source, DeezerSource, StringComparison.OrdinalIgnoreCase));
        configuredOrder.Insert(0, DeezerSource);

        return configuredOrder;
    }

    private async Task<string?> TryResolvePreferredArtworkTrackIdAsync(
        DeezerClient deezerClient,
        Track track,
        string? albumConstraint,
        CancellationToken cancellationToken)
    {
        return await ArtworkFallbackHelper.TryResolvePreferredArtworkTrackIdAsync(
            deezerClient,
            track,
            albumConstraint,
            _logger,
            cancellationToken);
    }

    private static bool AllowsJpegArtwork(DeezSpoTagSettings settings)
    {
        var formats = (settings.LocalArtworkFormat ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(format => format.ToLowerInvariant());
        return formats.Contains("jpg");
    }

    private static bool AllowsJpegArtistArtwork(DeezSpoTagSettings settings)
    {
        return AllowsJpegArtwork(settings);
    }

    private static bool ShouldOverwriteArtistArtwork(string? imageUrl)
    {
        return !string.IsNullOrWhiteSpace(imageUrl)
            && imageUrl.Contains("dzcdn.net/images/artist/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAppleArtworkUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && url.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get track download URL using media API (fallback method)
    /// </summary>
    private async Task<string?> GetTrackDownloadUrlAsync(Track track, int format)
    {
        try
        {
            if (string.IsNullOrEmpty(track.TrackToken))
            {
                _logger.LogError("Track {TrackId} has no track token", track.Id);
                return null;
            }

            var formatString = ResolveDownloadFormatName(format);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Getting download URL for track {TrackId} with format {Format} ({FormatString})",
                    track.Id, format, formatString);            }

            var deezerClient = await RequireAuthenticatedClientAsync();
            var mediaResult = await ResolveMediaResultWithTokenRefreshAsync(track, deezerClient, formatString);
            var downloadUrl = mediaResult.Url;
            if (string.IsNullOrEmpty(downloadUrl) && mediaResult.ErrorCode == 2002)
            {
                _logger.LogWarning("Track {TrackId} is geolocked for format {Format}", track.Id, formatString);
                return null;
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                return TryBuildCryptedFallbackUrl(track, format, formatString);
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Got download URL for track {TrackId}: {Url}", track.Id, downloadUrl);            }
            return downloadUrl;
        }
        catch (DownloadException)
        {
            throw; // Re-throw download exceptions as-is
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get download URL for track {TrackId}", track.Id);
            throw new DownloadException($"Failed to get download URL: {ex.Message}", "downloadUrlError");
        }
    }

    private static string ResolveDownloadFormatName(int format)
    {
        return format switch
        {
            9 => "FLAC",
            3 => "MP3_320",
            1 => "MP3_128",
            8 => "MP3_128",
            13 => "MP4_RA3",
            14 => "MP4_RA2",
            15 => "MP4_RA1",
            _ => "MP3_128"
        };
    }

    private async Task<DeezerClient> RequireAuthenticatedClientAsync()
    {
        var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
        if (deezerClient == null)
        {
            throw new DownloadException("Deezer client not authenticated", "notAuthenticated");
        }

        return deezerClient;
    }

    private async Task<DeezSpoTag.Integrations.Deezer.DeezerMediaResult> ResolveMediaResultWithTokenRefreshAsync(
        Track track,
        DeezerClient deezerClient,
        string formatString)
    {
        var mediaResult = await deezerClient.GetTrackUrlWithStatusAsync(track.TrackToken, formatString);
        if (!string.IsNullOrEmpty(mediaResult.Url) || mediaResult.ErrorCode != 2001)
        {
            return mediaResult;
        }

        var refreshed = await RefreshTrackTokenAsync(track, deezerClient);
        if (!refreshed)
        {
            return mediaResult;
        }

        return await deezerClient.GetTrackUrlWithStatusAsync(track.TrackToken, formatString);
    }

    private string? TryBuildCryptedFallbackUrl(Track track, int format, string formatString)
    {
        if (!string.IsNullOrEmpty(track.MD5) && !string.IsNullOrEmpty(track.MediaVersion))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Falling back to crypted URL for track {TrackId} format {Format}", track.Id, formatString);
            }

            return CryptoService.GenerateCryptedStreamUrl(track.Id, track.MD5, track.MediaVersion, format.ToString());
        }

        _logger.LogWarning("No download URL returned for track {TrackId} with format {Format}", track.Id, formatString);
        return null;
    }

    private async Task<bool> RefreshTrackTokenAsync(Track track, DeezerClient deezerClient)
    {
        return await TrackTokenRefreshHelper.RefreshTrackTokenAsync(
            track,
            deezerClient,
            _logger,
            includeFileSizes: true);
    }

    /// <summary>
    /// Stream track to file with decryption (used by queue engine processors)
    /// </summary>
    public async Task StreamTrackToFileAsync(
        string downloadUrl,
        string writePath,
        Track track,
        DownloadObject downloadObject,
        DeezSpoTagSettings settings,
        IDownloadListener? listener,
        CancellationToken cancellationToken = default)
    {
        await _streamProcessor.StreamTrackAsync(
            writePath,
            track,
            downloadUrl,
            downloadObject,
            listener,
            new DecryptionStreamProcessor.StreamTrackRetryPolicy(
                settings.MaxRetries,
                settings.RetryDelaySeconds,
                settings.RetryDelayIncrease),
            cancellationToken: cancellationToken);
    }

    private async Task QueueDeferredPostDownloadTasksAsync(
        DownloadObject downloadObject,
        Track track,
        DeezSpoTagModels.TrackDownloadResult result,
        PathGenerationResult pathResult,
        DeezSpoTagSettings settings,
        string expectedOutputPath)
    {
        var shouldFetchArtwork = settings.SaveArtwork || settings.SaveArtworkArtist || settings.SaveAnimatedArtwork;
        var shouldFetchLyrics = ShouldSaveLyrics(settings);
        if (!shouldFetchArtwork && !shouldFetchLyrics)
        {
            return;
        }

        var queueUuid = string.IsNullOrWhiteSpace(downloadObject.Uuid) ? "unknown" : downloadObject.Uuid;
        SendPrefetchStatus(
            queueUuid,
            shouldFetchArtwork ? FetchingStatus : SkippedStatus,
            shouldFetchLyrics ? FetchingStatus : SkippedStatus);

        var request = new DeferredPostDownloadRequest
        {
            QueueUuid = queueUuid,
            ShouldFetchArtwork = shouldFetchArtwork,
            ShouldFetchLyrics = shouldFetchLyrics,
            Track = track,
            Result = result,
            PathResult = pathResult,
            Settings = settings,
            ExpectedOutputPath = expectedOutputPath
        };

        await _postDownloadTaskScheduler.EnqueueAsync(
            queueUuid,
            DeezerSource,
            (_, token) => RunDeferredPostDownloadTasksAsync(request, token),
            CancellationToken.None);
    }

    private async Task RunDeferredPostDownloadTasksAsync(
        DeferredPostDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var initialArtworkStatus = request.ShouldFetchArtwork ? FetchingStatus : SkippedStatus;
        var initialLyricsStatus = request.ShouldFetchLyrics ? FetchingStatus : SkippedStatus;

        Task<string> artworkTask = Task.FromResult(initialArtworkStatus);
        if (request.ShouldFetchArtwork)
        {
            artworkTask = Task.Run(async () =>
            {
                var artworkStatus = await DownloadArtworkAsync(request.Track, request.Result, request.Settings, cancellationToken)
                    ? CompletedStatus
                    : FailedStatus;
                SendPrefetchStatus(request.QueueUuid, artworkStatus, initialLyricsStatus);
                return artworkStatus;
            }, cancellationToken);
        }

        Task<(string status, string lyricsType)> lyricsTask = Task.FromResult((initialLyricsStatus, string.Empty));
        if (request.ShouldFetchLyrics && !string.IsNullOrWhiteSpace(request.ExpectedOutputPath))
        {
            lyricsTask = Task.Run(async () =>
            {
                var lyricsSaveResultInfo = await SaveLyricsAfterDownloadAsync(
                    request.Track,
                    request.PathResult,
                    request.ExpectedOutputPath,
                    request.Settings,
                    request.QueueUuid,
                    initialArtworkStatus,
                    cancellationToken);
                SendPrefetchStatus(request.QueueUuid, initialArtworkStatus, lyricsSaveResultInfo.status, lyricsSaveResultInfo.lyricsType);
                return lyricsSaveResultInfo;
            }, cancellationToken);
        }

        await Task.WhenAll(artworkTask, lyricsTask);
    }

    private void SendPrefetchStatus(string queueUuid, string? artworkStatus, string? lyricsStatus, string? lyricsType = null)
    {
        Queue.QueuePrefetchStatusHelper.Send(
            _deezspotagListener,
            queueUuid,
            artworkStatus,
            lyricsStatus,
            lyricsType);
    }

    private async Task<(string status, string lyricsType)> SaveLyricsAfterDownloadAsync(
        Track track,
        PathGenerationResult pathResult,
        string outputPath,
        DeezSpoTagSettings settings,
        string queueUuid,
        string artworkStatus,
        CancellationToken cancellationToken)
    {
        var lyricsType = string.Empty;
        try
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Attempting deferred lyrics save for track {TrackId} with settings SaveLyrics={SaveLyrics}, SyncedLyrics={SyncedLyrics}",
                    track.Id,
                    settings.SaveLyrics,
                    settings.SyncedLyrics);            }

            var outputIoPath = DownloadPathResolver.ResolveIoPath(outputPath);
            var filePath = Path.GetDirectoryName(outputIoPath)
                           ?? DownloadPathResolver.ResolveIoPath(pathResult.FilePath);
            var filename = ResolveAudioFilenameStem(outputIoPath);
            Directory.CreateDirectory(filePath);
            var extrasPath = DownloadPathResolver.ResolveIoPath(pathResult.ExtrasPath);
            var coverPath = DownloadPathResolver.ResolveIoPath(pathResult.CoverPath ?? string.Empty);
            var artistPath = DownloadPathResolver.ResolveIoPath(pathResult.ArtistPath ?? string.Empty);
            var paths = (filePath, filename, extrasPath, coverPath, artistPath);

            var lyrics = await _lyricsService.ResolveLyricsAsync(track, settings, cancellationToken);
            lyricsType = LyricsPrefetchTypeHelper.ResolveFromLyrics(lyrics);
            if (!string.IsNullOrWhiteSpace(lyricsType))
            {
                SendPrefetchStatus(queueUuid, artworkStatus, FetchingStatus, lyricsType);
            }
            if (lyrics != null && string.IsNullOrEmpty(lyrics.ErrorMessage))
            {
                track.Lyrics ??= new Lyrics(track.LyricsId ?? "0");
                track.Lyrics.Unsync = lyrics.UnsyncedLyrics ?? "";
                if (lyrics.IsSynced())
                {
                    track.Lyrics.Sync = lyrics.GenerateLrcContent(track.Title, track.MainArtist?.Name, track.Album?.Title);
                }

                await _lyricsService.SaveLyricsAsync(lyrics, track, paths, settings, cancellationToken);
            }
            else
            {
                await _lyricsService.SaveLyricsAsync(track, paths, settings, cancellationToken);
            }

            var savedLyricsType = LyricsPrefetchTypeHelper.ResolveSavedLyricsType(filePath, filename);
            if (!string.IsNullOrWhiteSpace(savedLyricsType))
            {
                lyricsType = savedLyricsType;
            }

            var status = string.IsNullOrWhiteSpace(lyricsType) ? NoLyricsStatus : CompletedStatus;
            return (status, lyricsType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Deferred Deezer lyrics save failed for {Path}", outputPath);
            return (FailedStatus, lyricsType);
        }
    }

    private static bool ShouldSaveLyrics(DeezSpoTagSettings settings)
    {
        return LyricsSettingsPolicy.CanFetchLyrics(settings);
    }

    private async Task EnsureLyricsForTaggingAsync(
        Track track,
        DeezSpoTagSettings settings,
        TagSettings tagSettings,
        CancellationToken cancellationToken)
    {
        if (!LyricsSettingsPolicy.CanFetchLyrics(settings))
        {
            return;
        }

        if (ShouldSkipLyricsTagHydration(track, tagSettings))
        {
            return;
        }

        try
        {
            var lyricsSettings = BuildLyricsResolveSettings(settings, tagSettings);
            var lyrics = await _lyricsService.ResolveLyricsAsync(track, lyricsSettings, cancellationToken);
            if (lyrics == null || !string.IsNullOrWhiteSpace(lyrics.ErrorMessage))
            {
                return;
            }

            ApplyLyricsForTagging(track, tagSettings, lyrics);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed pre-tag lyrics hydration for track {TrackId}", track.Id);            }
        }
    }

    private static bool ShouldSkipLyricsTagHydration(
        Track track,
        TagSettings tagSettings)
    {
        if (!tagSettings.Lyrics && !tagSettings.SyncedLyrics)
        {
            return true;
        }

        return HasRequiredLyricsAlready(track, tagSettings);
    }

    private static bool HasRequiredLyricsAlready(Track track, TagSettings tagSettings)
    {
        var hasUnsynced = !string.IsNullOrWhiteSpace(track.Lyrics?.Unsync);
        var hasSynced = !string.IsNullOrWhiteSpace(track.Lyrics?.Sync) || (track.Lyrics?.SyncID3?.Count ?? 0) > 0;
        return (!tagSettings.Lyrics || hasUnsynced) && (!tagSettings.SyncedLyrics || hasSynced);
    }

    private static void ApplyLyricsForTagging(Track track, TagSettings tagSettings, LyricsBase lyrics)
    {
        track.Lyrics ??= new Lyrics(track.LyricsId ?? "0");

        if (tagSettings.Lyrics && !string.IsNullOrWhiteSpace(lyrics.UnsyncedLyrics))
        {
            track.Lyrics.Unsync = lyrics.UnsyncedLyrics;
        }

        if (!tagSettings.SyncedLyrics || !lyrics.IsSynced())
        {
            return;
        }

        track.Lyrics.Sync = lyrics.GenerateLrcContent(track.Title, track.MainArtist?.Name, track.Album?.Title);
        var syncedLines = lyrics.SyncedLyrics?
            .Where(line => line != null && line.IsValid())
            .Select(line => new SyncLyric
            {
                Timestamp = Math.Max(0, line!.Milliseconds),
                Text = line.Text ?? string.Empty
            })
            .ToList();

        if (syncedLines?.Count > 0)
        {
            track.Lyrics.SyncID3 = syncedLines;
        }
    }

    private static DeezSpoTagSettings BuildLyricsResolveSettings(DeezSpoTagSettings settings, TagSettings tagSettings)
    {
        return LyricsResolveSettingsBuilder.Build(settings, tagSettings);
    }

    private static string ResolveAudioFilenameStem(string pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
        {
            return string.Empty;
        }

        var trimmed = pathOrName.Trim();
        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || !KnownAudioExtensions.Contains(extension))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(stem) ? fileName : stem;
    }

    /// <summary>
    /// Download album and artist artwork (exact port from deezspotag afterDownloadSingle)
    /// </summary>
    private async Task<bool> DownloadArtworkAsync(Track track, DeezSpoTagModels.TrackDownloadResult result, DeezSpoTagSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var appleArtworkSize = AppleQueueHelpers.GetAppleArtworkSize(settings);
            await DownloadArtworkSetAsync(
                new ArtworkDownloadSetRequest
                {
                    ArtworkType = "album",
                    ShouldSave = settings.SaveArtwork,
                    OutputDirectory = result.AlbumPath,
                    Filename = result.AlbumFilename,
                    Urls = result.AlbumURLs,
                    Settings = settings,
                    AppleArtworkSize = appleArtworkSize,
                    OverwritePolicyResolver = static imageUrl => ShouldOverwriteArtistArtwork(imageUrl) ? "y" : null
                },
                cancellationToken);

            await SaveAnimatedArtworkAsync(track, settings, result.AlbumPath, result.AlbumFilename, cancellationToken);

            await DownloadArtworkSetAsync(
                new ArtworkDownloadSetRequest
                {
                    ArtworkType = ArtistType,
                    ShouldSave = settings.SaveArtworkArtist,
                    OutputDirectory = result.ArtistPath,
                    Filename = result.ArtistFilename,
                    Urls = result.ArtistURLs,
                    Settings = settings,
                    AppleArtworkSize = appleArtworkSize,
                    OverwritePolicyResolver = static _ => null
                },
                cancellationToken);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error downloading artwork");
            return false;
        }
    }

    private async Task SaveAnimatedArtworkAsync(
        Track track,
        DeezSpoTagSettings settings,
        string? outputDir,
        string? baseFileName,
        CancellationToken cancellationToken)
    {
        if (!settings.SaveAnimatedArtwork || string.IsNullOrEmpty(outputDir))
        {
            return;
        }

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
        var appleId = ArtworkFallbackHelper.TryExtractAppleTrackId(track);
        await AppleQueueHelpers.SaveAnimatedArtworkAsync(
            _appleCatalogService,
            _httpClientFactory,
            new AppleQueueHelpers.AnimatedArtworkSaveRequest
            {
                AppleId = appleId,
                Title = track.Title,
                Artist = track.MainArtist?.Name,
                Album = track.Album?.Title,
                BaseFileName = baseFileName,
                Storefront = storefront,
                MaxResolution = settings.Video.AppleMusicVideoMaxResolution,
                OutputDir = outputDir,
                Logger = _logger
            },
            cancellationToken);
    }

    private async Task DownloadArtworkSetAsync(
        ArtworkDownloadSetRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ShouldSave
            || string.IsNullOrWhiteSpace(request.OutputDirectory)
            || string.IsNullOrWhiteSpace(request.Filename)
            || request.Urls is not { Count: > 0 })
        {
            return;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Downloading {ArtworkType} artwork to {OutputPath}", request.ArtworkType, request.OutputDirectory);        }
        Directory.CreateDirectory(request.OutputDirectory);

        var downloadTasks = request.Urls.Select(imageUrl => DownloadSingleArtworkAsync(
            new SingleArtworkDownloadRequest
            {
                ArtworkType = request.ArtworkType,
                OutputDirectory = request.OutputDirectory,
                Filename = request.Filename,
                ImageUrl = imageUrl,
                Settings = request.Settings,
                AppleArtworkSize = request.AppleArtworkSize,
                OverwritePolicyOverride = request.OverwritePolicyResolver(imageUrl.Url)
            },
            cancellationToken));

        await Task.WhenAll(downloadTasks);
    }

    private async Task DownloadSingleArtworkAsync(
        SingleArtworkDownloadRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var outputPath = Path.Join(request.OutputDirectory, $"{request.Filename}.{request.ImageUrl.Ext}");
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Downloading {ArtworkType} image: {Url} to {OutputPath}", request.ArtworkType, request.ImageUrl.Url, outputPath);            }

            string? downloadedPath;
            if (IsAppleArtworkUrl(request.ImageUrl.Url))
            {
                downloadedPath = await AppleQueueHelpers.DownloadAppleArtworkAsync(
                    _imageDownloader,
                    new AppleQueueHelpers.AppleArtworkDownloadRequest
                    {
                        RawUrl = request.ImageUrl.Url,
                        OutputPath = outputPath,
                        Settings = request.Settings,
                        Size = request.AppleArtworkSize,
                        Overwrite = request.Settings.OverwriteFile,
                        PreferMaxQuality = true,
                        Logger = _logger
                    },
                    cancellationToken);
            }
            else
            {
                downloadedPath = await _imageDownloader.DownloadImageAsync(
                    request.ImageUrl.Url,
                    outputPath,
                    request.OverwritePolicyOverride ?? request.Settings.OverwriteFile,
                    true,
                    cancellationToken);
            }

            if (downloadedPath != null)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully downloaded {ArtworkType} artwork: {Path}", request.ArtworkType, downloadedPath);                }
            }
            else
            {
                _logger.LogWarning("Failed to download {ArtworkType} artwork: {Url}", request.ArtworkType, request.ImageUrl.Url);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error downloading {ArtworkType} artwork: {Url}", request.ArtworkType, request.ImageUrl.Url);
        }
    }

    private static void MergePublicTrackMetadata(Track track, DeezSpoTag.Core.Models.Deezer.ApiTrack apiTrack)
    {
        MergePublicTrackCoreMetadata(track, apiTrack);
        MergePublicTrackFlags(track, apiTrack);
        MergePublicTrackExtraMetadata(track, apiTrack);
    }

    private static void MergePublicTrackCoreMetadata(Track track, DeezSpoTag.Core.Models.Deezer.ApiTrack apiTrack)
    {
        if (string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(apiTrack.Title))
        {
            track.Title = apiTrack.Title;
        }

        if (track.Duration <= 0 && apiTrack.Duration > 0)
        {
            track.Duration = apiTrack.Duration;
        }

        if (track.TrackNumber <= 0 && apiTrack.TrackPosition > 0)
        {
            track.TrackNumber = apiTrack.TrackPosition;
        }

        if (track.DiscNumber <= 0 && apiTrack.DiskNumber > 0)
        {
            track.DiscNumber = apiTrack.DiskNumber;
        }
    }

    private static void MergePublicTrackFlags(Track track, DeezSpoTag.Core.Models.Deezer.ApiTrack apiTrack)
    {
        if (!track.Explicit && (apiTrack.ExplicitLyrics || apiTrack.ExplicitContentLyrics == 1))
        {
            track.Explicit = true;
        }

        if (track.Bpm <= 0 && apiTrack.Bpm > 0)
        {
            track.Bpm = apiTrack.Bpm;
        }

        if (track.Rank <= 0 && apiTrack.Rank > 0)
        {
            track.Rank = apiTrack.Rank;
        }

        if (Math.Abs(track.Gain) <= double.Epsilon && Math.Abs(apiTrack.Gain) > double.Epsilon)
        {
            track.Gain = apiTrack.Gain;
        }
    }

    private static void MergePublicTrackExtraMetadata(Track track, DeezSpoTag.Core.Models.Deezer.ApiTrack apiTrack)
    {
        if (string.IsNullOrWhiteSpace(track.ISRC) && !string.IsNullOrWhiteSpace(apiTrack.Isrc))
        {
            track.ISRC = apiTrack.Isrc;
        }

        if (string.IsNullOrWhiteSpace(track.Copyright) && !string.IsNullOrWhiteSpace(apiTrack.Copyright))
        {
            track.Copyright = apiTrack.Copyright;
        }

        if (string.IsNullOrWhiteSpace(track.PhysicalReleaseDate) && !string.IsNullOrWhiteSpace(apiTrack.PhysicalReleaseDate))
        {
            track.PhysicalReleaseDate = apiTrack.PhysicalReleaseDate;
        }

        if (string.IsNullOrWhiteSpace(track.LyricsId) && !string.IsNullOrWhiteSpace(apiTrack.LyricsId))
        {
            track.LyricsId = apiTrack.LyricsId;
        }
    }

    private static void MergePublicAlbumMetadata(Album album, DeezSpoTag.Core.Models.Deezer.ApiAlbum apiAlbum)
    {
        album.Title = string.IsNullOrWhiteSpace(album.Title) ? apiAlbum.Title : album.Title;
        if (string.IsNullOrWhiteSpace(album.Label) && !string.IsNullOrWhiteSpace(apiAlbum.Label))
        {
            album.Label = apiAlbum.Label;
        }

        if (string.IsNullOrWhiteSpace(album.Barcode) && !string.IsNullOrWhiteSpace(apiAlbum.Upc))
        {
            album.Barcode = apiAlbum.Upc;
        }

        if (string.IsNullOrWhiteSpace(album.RecordType) && !string.IsNullOrWhiteSpace(apiAlbum.RecordType))
        {
            album.RecordType = apiAlbum.RecordType;
        }

        if (album.TrackTotal <= 0 && apiAlbum.NbTracks is > 0)
        {
            album.TrackTotal = apiAlbum.NbTracks.Value;
        }

        if ((!album.DiscTotal.HasValue || album.DiscTotal.Value <= 0) && apiAlbum.NbDisk is > 0)
        {
            album.DiscTotal = apiAlbum.NbDisk.Value;
        }

        if (string.IsNullOrWhiteSpace(album.Copyright) && !string.IsNullOrWhiteSpace(apiAlbum.Copyright))
        {
            album.Copyright = apiAlbum.Copyright;
        }
    }

    private static void ApplyAlbumCoverIfMissing(Album album, string? md5Image)
    {
        if (string.IsNullOrWhiteSpace(md5Image)
            || (album.Pic != null && !string.IsNullOrWhiteSpace(album.Pic.Md5)))
        {
            return;
        }

        album.Pic = new Picture(md5Image, "cover");
        album.Md5Image = md5Image;
    }

    private static void MergeAlbumGenres(Album album, DeezSpoTag.Core.Models.Deezer.ApiAlbum apiAlbum)
    {
        if (apiAlbum.Genres?.Data is not { Count: > 0 })
        {
            return;
        }

        var incomingGenres = apiAlbum.Genres.Data
            .Select(genre => genre.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (incomingGenres.Count == 0)
        {
            return;
        }

        if (album.Genre is not { Count: > 0 })
        {
            album.Genre = incomingGenres;
            return;
        }

        foreach (var genre in incomingGenres.Where(genre => !album.Genre.Contains(genre, StringComparer.OrdinalIgnoreCase)))
        {
            album.Genre.Add(genre);
        }
    }

    private static void TryApplyAlbumReleaseDate(Album album, string? releaseDateText)
    {
        if (string.IsNullOrWhiteSpace(releaseDateText)
            || album.Date == null
            || !DateTime.TryParse(releaseDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            return;
        }

        album.Date.Day = releaseDate.Day.ToString("D2");
        album.Date.Month = releaseDate.Month.ToString("D2");
        album.Date.Year = releaseDate.Year.ToString();
        album.Date.FixDayMonth();
    }

    private static void MergeAlbumMainArtist(Album album, DeezSpoTag.Core.Models.Deezer.ApiArtist? apiArtist)
    {
        if (apiArtist == null || string.IsNullOrWhiteSpace(apiArtist.Name))
        {
            return;
        }

        if (album.MainArtist == null || string.IsNullOrWhiteSpace(album.MainArtist.Name))
        {
            album.MainArtist = new Artist(apiArtist.Id, apiArtist.Name);
        }

        if (!string.IsNullOrWhiteSpace(apiArtist.Md5Image)
            && string.IsNullOrWhiteSpace(album.MainArtist.Pic?.Md5))
        {
            album.MainArtist.Pic = new Picture(apiArtist.Md5Image, ArtistType);
        }

        if (!album.Artist.TryGetValue("Main", out var mainArtists))
        {
            mainArtists = new List<string>();
            album.Artist["Main"] = mainArtists;
        }

        if (mainArtists.Count == 0)
        {
            mainArtists.Add(album.MainArtist.Name);
        }

        if (album.Artists is not { Count: > 0 })
        {
            album.Artists = new List<string>(mainArtists);
            return;
        }

        if (!album.Artists.Contains(album.MainArtist.Name, StringComparer.OrdinalIgnoreCase))
        {
            album.Artists.Add(album.MainArtist.Name);
        }
    }
}
