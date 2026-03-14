using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;

namespace DeezSpoTag.Services.Download.Shared;

public static class EngineAudioPostDownloadHelper
{
    private const string PlaylistType = "playlist";
    private const string AlbumType = "album";
    private const string TrackType = "track";
    private const string DeezerSource = "deezer";
    private const string AppleSource = "apple";
    private const string SpotifySource = "spotify";
    private const string MzStaticHost = "mzstatic.com";
    private const string UnknownArtist = "Unknown Artist";
    private const string CompletedStatus = "completed";
    private const string FailedStatus = "failed";
    private const string FetchingStatus = "fetching";
    private const string SkippedStatus = "skipped";
    private const string NoLyricsStatus = "no-lyrics";
    private const string RunningStatus = "running";
    private const string CompletedStatusName = "completed";
    private const string CancelledStatus = "cancelled";
    private const string PausedStatus = "paused";
    private const string CanceledStatus = "canceled";
    private const string UpdateQueueEvent = "updateQueue";

    public sealed record EngineTrackContext(
        Track Track,
        PathGenerationResult PathResult,
        string OutputDir,
        string FilenameFormat);

    public sealed record PrefetchPathContext(
        string QueueUuid,
        string FileDir,
        string CoverPath,
        string ArtistPath,
        string ExtrasPath,
        string ExpectedBaseName);

    public sealed record PostDownloadSettingsRequest(
        EngineTrackContext Context,
        EngineQueueItemBase Payload,
        string OutputPath,
        DeezSpoTagSettings Settings,
        IServiceProvider Scope,
        string Engine,
        ILogger Logger,
        string? AppleCoverLookupIdOverride = null,
        string? AnimatedArtworkAppleIdOverride = null);

    public sealed record PrefetchRequest(
        string QueueUuid,
        EngineTrackContext Context,
        EngineQueueItemBase Payload,
        DeezSpoTagSettings Settings,
        string ExpectedOutputPath,
        IPostDownloadTaskScheduler TaskScheduler,
        LyricsService LyricsService,
        IDeezSpoTagListener Listener,
        IActivityLogWriter ActivityLog,
        ILogger Logger,
        string Engine,
        string? AppleCoverLookupIdOverride = null,
        string? AnimatedArtworkAppleIdOverride = null);

    public sealed record InitializeQueueItemContext<TPayload>(
        DownloadQueueRepository QueueRepository,
        DownloadRetryScheduler RetryScheduler,
        IActivityLogWriter ActivityLog,
        IDownloadTagSettingsResolver TagSettingsResolver,
        IFolderConversionSettingsOverlay FolderConversionSettingsOverlay,
        IDeezSpoTagListener Listener,
        Func<string, string, TPayload, CancellationToken, Task<bool>> TryAdvanceAsync,
        Func<TPayload, Dictionary<string, object>> QueuePayloadFactory,
        DeezSpoTagSettings Settings,
        string EngineName,
        ILogger Logger)
        where TPayload : EngineQueueItemBase;

    public sealed record CancellationHandlingContext(
        DownloadQueueRepository QueueRepository,
        DownloadCancellationRegistry CancellationRegistry,
        IDeezSpoTagListener Listener,
        DownloadRetryScheduler RetryScheduler,
        string EngineName,
        IServiceProvider ServiceProvider);

    public sealed record FailureHandlingContext<TPayload>(
        DownloadQueueRepository QueueRepository,
        IActivityLogWriter ActivityLog,
        IDeezSpoTagListener Listener,
        DownloadRetryScheduler RetryScheduler,
        IServiceProvider ServiceProvider,
        Func<string, string, TPayload, CancellationToken, Task<bool>> TryAdvanceAsync,
        Func<TPayload, Dictionary<string, object>> QueuePayloadFactory,
        string EngineName,
        ILogger Logger)
        where TPayload : EngineQueueItemBase;

    private sealed record PrefetchExecutionContext(
        PrefetchRequest Request,
        PrefetchPathContext Paths,
        bool ShouldFetchArtwork,
        bool ShouldFetchLyrics);

    private sealed record PrefetchRuntimeServices(
        ImageDownloader ImageDownloader,
        EnhancedPathTemplateProcessor PathProcessor,
        ISpotifyArtworkResolver? SpotifyArtworkResolver,
        ISpotifyIdResolver? SpotifyIdResolver,
        IHttpClientFactory? HttpClientFactory,
        AppleMusicCatalogService? AppleCatalog,
        DeezerClient? DeezerClient);

    public static EngineTrackContext BuildTrackContext(
        EngineQueueItemBase payload,
        DeezSpoTagSettings settings,
        EnhancedPathTemplateProcessor pathProcessor,
        string source,
        string? sourceId)
        => BuildTrackContext(payload, settings, pathProcessor, source, sourceId, null, null);

    public static EngineTrackContext BuildTrackContext(
        EngineQueueItemBase payload,
        DeezSpoTagSettings settings,
        EnhancedPathTemplateProcessor pathProcessor,
        string source,
        string? sourceId,
        Func<EngineQueueItemBase, string>? downloadTypeResolver,
        Action<Track, EngineQueueItemBase>? configureTrack)
    {
        var track = CreateTrackFromPayload(payload, settings, source, sourceId, out var artistName);
        PopulateTrackUrls(track, payload);
        PopulateTrackMetadata(track, payload, artistName);

        configureTrack?.Invoke(track, payload);

        if (!string.IsNullOrWhiteSpace(payload.CollectionName)
            && string.Equals(payload.CollectionType, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            track.Playlist = new Playlist("0", payload.CollectionName);
        }

        track.ApplySettings(settings);

        var downloadType = ResolveDownloadType(payload, downloadTypeResolver);
        var pathResult = pathProcessor.GeneratePaths(track, downloadType, settings);
        var filenameStem = Path.GetFileNameWithoutExtension(pathResult.Filename);
        if (string.IsNullOrWhiteSpace(filenameStem))
        {
            filenameStem = pathResult.Filename;
        }

        var outputDir = DownloadPathResolver.ResolveIoPath(pathResult.FilePath);
        return new EngineTrackContext(track, pathResult, outputDir, $"literal:{filenameStem}");
    }

    private static Track CreateTrackFromPayload(
        EngineQueueItemBase payload,
        DeezSpoTagSettings settings,
        string source,
        string? sourceId,
        out string artistName)
    {
        artistName = string.IsNullOrWhiteSpace(payload.Artist) ? UnknownArtist : payload.Artist;
        var albumArtistName = string.IsNullOrWhiteSpace(payload.AlbumArtist) ? artistName : payload.AlbumArtist;
        var albumTitle = string.IsNullOrWhiteSpace(payload.Album) ? "Unknown Album" : payload.Album;
        var mainArtist = new Artist("0", artistName);
        var albumArtist = new Artist("0", albumArtistName);
        var parsedDate = CustomDate.FromString(payload.ReleaseDate);
        var album = new Album("0", albumTitle)
        {
            MainArtist = albumArtist,
            RootArtist = albumArtist,
            TrackTotal = ResolveTrackTotal(payload),
            DiscTotal = ResolveDiscTotal(payload),
            Date = parsedDate,
            DateString = parsedDate.Format(settings.DateFormat),
            Genre = payload.Genres.ToList(),
            Label = payload.Label,
            Barcode = payload.Barcode
        };

        var track = new Track
        {
            Id = string.IsNullOrWhiteSpace(payload.Isrc) ? payload.Id : payload.Isrc,
            Title = payload.Title ?? string.Empty,
            MainArtist = mainArtist,
            Album = album,
            TrackNumber = ResolveTrackNumber(payload),
            DiscNumber = ResolveDiscNumber(payload),
            Position = payload.Position,
            ISRC = payload.Isrc ?? string.Empty,
            Date = parsedDate,
            DateString = parsedDate.Format(settings.DateFormat),
            Danceability = payload.Danceability,
            Energy = payload.Energy,
            Valence = payload.Valence,
            Acousticness = payload.Acousticness,
            Instrumentalness = payload.Instrumentalness,
            Speechiness = payload.Speechiness,
            Loudness = payload.Loudness,
            Tempo = payload.Tempo,
            TimeSignature = payload.TimeSignature,
            Liveness = payload.Liveness,
            Source = source,
            SourceId = !string.IsNullOrWhiteSpace(sourceId) ? sourceId : payload.SpotifyId,
            DownloadURL = !string.IsNullOrWhiteSpace(payload.Url) ? payload.Url : payload.SourceUrl
        };

        if (payload.Tempo is > 0)
        {
            track.Bpm = payload.Tempo.Value;
        }

        if (!string.IsNullOrWhiteSpace(payload.MusicKey))
        {
            track.Key = payload.MusicKey;
        }

        return track;
    }

    private static void PopulateTrackUrls(Track track, EngineQueueItemBase payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.DeezerId))
        {
            track.Urls["deezer_track_id"] = payload.DeezerId;
            track.Urls[DeezerSource] = $"https://www.deezer.com/track/{payload.DeezerId}";
        }

        if (!string.IsNullOrWhiteSpace(payload.AppleId))
        {
            track.Urls["apple_track_id"] = payload.AppleId;
            track.Urls["apple_id"] = payload.AppleId;
            track.Urls[AppleSource] = $"https://music.apple.com/us/song/{payload.AppleId}?i={payload.AppleId}";
        }

        if (!string.IsNullOrWhiteSpace(payload.SourceUrl))
        {
            track.Urls["source_url"] = payload.SourceUrl;
        }
    }

    private static void PopulateTrackMetadata(Track track, EngineQueueItemBase payload, string artistName)
    {
        track.Artists = new List<string> { artistName };
        track.Artist["Main"] = new List<string> { artistName };
        track.Copyright = payload.Copyright ?? string.Empty;
        track.Explicit = payload.Explicit ?? false;
        if (!string.IsNullOrWhiteSpace(payload.Composer))
        {
            track.Contributors["composer"] = new List<string> { payload.Composer };
        }
    }

    private static string ResolveDownloadType(
        EngineQueueItemBase payload,
        Func<EngineQueueItemBase, string>? downloadTypeResolver)
        => downloadTypeResolver?.Invoke(payload)
            ?? payload.CollectionType?.ToLowerInvariant() switch
            {
                PlaylistType => PlaylistType,
                AlbumType => AlbumType,
                _ => TrackType
            };

    private static int ResolveTrackTotal(EngineQueueItemBase payload)
    {
        if (payload.TrackTotal > 0)
        {
            return payload.TrackTotal;
        }

        return payload.SpotifyTotalTracks > 0 ? payload.SpotifyTotalTracks : 0;
    }

    private static int ResolveDiscTotal(EngineQueueItemBase payload)
    {
        if (payload.DiscTotal > 0)
        {
            return payload.DiscTotal;
        }

        if (payload.DiscNumber > 0)
        {
            return payload.DiscNumber;
        }

        return payload.SpotifyDiscNumber > 0 ? payload.SpotifyDiscNumber : 1;
    }

    private static int ResolveTrackNumber(EngineQueueItemBase payload)
    {
        if (payload.TrackNumber > 0)
        {
            return payload.TrackNumber;
        }

        return payload.SpotifyTrackNumber > 0 ? payload.SpotifyTrackNumber : payload.Position;
    }

    private static int ResolveDiscNumber(EngineQueueItemBase payload)
    {
        if (payload.DiscNumber > 0)
        {
            return payload.DiscNumber;
        }

        return payload.SpotifyDiscNumber > 0 ? payload.SpotifyDiscNumber : 1;
    }

    public static async Task<string> ApplyPostDownloadSettingsAsync(
        PostDownloadSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var imageDownloader = request.Scope.GetRequiredService<ImageDownloader>();
        var audioTagger = request.Scope.GetRequiredService<AudioTagger>();
        var spotifyArtworkResolver = request.Scope.GetService<ISpotifyArtworkResolver>();
        var spotifyIdResolver = request.Scope.GetService<ISpotifyIdResolver>();
        var httpClientFactory = request.Scope.GetService<IHttpClientFactory>();
        var appleCatalog = request.Scope.GetService<AppleMusicCatalogService>();
        var deezerClient = request.Scope.GetService<DeezerClient>();

        var coverUrl = await DownloadEngineArtworkHelper.ResolveStandardAudioCoverUrlAsync(
            new DownloadEngineArtworkHelper.StandardAudioCoverResolveRequest(
                request.Settings,
                appleCatalog,
                httpClientFactory,
                spotifyArtworkResolver,
                spotifyIdResolver,
                deezerClient,
                request.AppleCoverLookupIdOverride ?? request.Payload.AppleId,
                request.Payload.Title,
                request.Payload.Artist,
                request.Payload.Album,
                request.Payload.DeezerId,
                request.Payload.Cover,
                request.Payload.Isrc,
                request.Logger),
            cancellationToken);

        await DownloadEngineArtworkHelper.TagAudioWithResolvedCoverAsync(
            new DownloadEngineArtworkHelper.AudioTagWithCoverRequest(
                request.OutputPath,
                request.Context.Track,
                request.Settings,
                coverUrl,
                request.Engine,
                imageDownloader,
                audioTagger,
                request.Logger),
            cancellationToken);

        UpdateAudioPayloadFiles(request.Payload, request.Context.PathResult, request.OutputPath);
        return request.OutputPath;
    }

    public static async Task QueueParallelPostDownloadPrefetchAsync(
        PrefetchRequest request,
        CancellationToken cancellationToken = default)
    {
        var shouldFetchArtwork = request.Settings.SaveArtwork || request.Settings.SaveArtworkArtist || request.Settings.SaveAnimatedArtwork;
        var shouldFetchLyrics = ShouldSaveLyrics(request.Settings);
        if (!shouldFetchArtwork && !shouldFetchLyrics)
        {
            return;
        }

        var prefetchPaths = BuildPrefetchPathContext(request.QueueUuid, request.Context, request.ExpectedOutputPath);

        QueuePrefetchStatusHelper.Send(
            request.Listener,
            prefetchPaths.QueueUuid,
            shouldFetchArtwork ? FetchingStatus : SkippedStatus,
            shouldFetchLyrics ? FetchingStatus : SkippedStatus);

        var execution = new PrefetchExecutionContext(request, prefetchPaths, shouldFetchArtwork, shouldFetchLyrics);
        await request.TaskScheduler.EnqueueAsync(
            prefetchPaths.QueueUuid,
            request.Engine,
            (provider, token) => RunPrefetchWorkAsync(provider, execution, token),
            CancellationToken.None);
    }

    private static async Task RunPrefetchWorkAsync(
        IServiceProvider provider,
        PrefetchExecutionContext execution,
        CancellationToken token)
    {
        var runtime = ResolvePrefetchRuntimeServices(provider);
        var settings = execution.Request.Settings;
        var fallbackOrder = ArtworkFallbackHelper.ResolveOrder(settings);
        var appleArtworkSize = AppleQueueHelpers.GetAppleArtworkSize(settings);
        var preferMaxQualityCover = settings.EmbedMaxQualityCover;
        var artworkStatus = execution.ShouldFetchArtwork ? FetchingStatus : SkippedStatus;
        var lyricsStatus = execution.ShouldFetchLyrics ? FetchingStatus : SkippedStatus;
        var lyricsType = string.Empty;
        var coverUrl = await ResolvePrefetchCoverUrlAsync(execution, runtime, fallbackOrder, token);
        var isAppleCover = !string.IsNullOrWhiteSpace(coverUrl)
            && coverUrl.Contains(MzStaticHost, StringComparison.OrdinalIgnoreCase);

        Task artworkTask = Task.CompletedTask;
        if (execution.ShouldFetchArtwork)
        {
            artworkTask = Task.Run(async () =>
            {
                try
                {
                    await RunArtworkPrefetchAsync(
                        execution,
                        runtime,
                        coverUrl,
                        isAppleCover,
                        appleArtworkSize,
                        preferMaxQualityCover,
                        token);
                    artworkStatus = CompletedStatus;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    artworkStatus = FailedStatus;
                    execution.Request.Logger.LogWarning(
                        ex,
                        "{Engine} artwork prefetch failed for {Path}",
                        execution.Request.Engine,
                        execution.Request.ExpectedOutputPath);
                }
                finally
                {
                    QueuePrefetchStatusHelper.Send(execution.Request.Listener, execution.Paths.QueueUuid, artworkStatus, lyricsStatus);
                }
            }, token);
        }

        Task lyricsTask = Task.CompletedTask;
        if (execution.ShouldFetchLyrics)
        {
            lyricsTask = Task.Run(async () =>
            {
                try
                {
                    lyricsType = await RunLyricsPrefetchAsync(execution, token);
                    lyricsStatus = string.IsNullOrWhiteSpace(lyricsType) ? NoLyricsStatus : CompletedStatus;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lyricsStatus = FailedStatus;
                    execution.Request.Logger.LogWarning(
                        ex,
                        "{Engine} lyrics download failed for {Path}",
                        execution.Request.Engine,
                        execution.Request.ExpectedOutputPath);
                }
                finally
                {
                    QueuePrefetchStatusHelper.Send(execution.Request.Listener, execution.Paths.QueueUuid, artworkStatus, lyricsStatus, lyricsType);
                }
            }, token);
        }

        await Task.WhenAll(artworkTask, lyricsTask);
    }

    private static PrefetchRuntimeServices ResolvePrefetchRuntimeServices(IServiceProvider provider)
    {
        var imageDownloader = provider.GetRequiredService<ImageDownloader>();
        var pathProcessor = provider.GetRequiredService<EnhancedPathTemplateProcessor>();
        var spotifyArtworkResolver = provider.GetService<ISpotifyArtworkResolver>();
        var spotifyIdResolver = provider.GetService<ISpotifyIdResolver>();
        var httpClientFactory = provider.GetService<IHttpClientFactory>();
        var appleCatalog = provider.GetService<AppleMusicCatalogService>();
        var deezerClient = provider.GetService<DeezerClient>();
        return new PrefetchRuntimeServices(
            imageDownloader,
            pathProcessor,
            spotifyArtworkResolver,
            spotifyIdResolver,
            httpClientFactory,
            appleCatalog,
            deezerClient);
    }

    private static async Task<string?> ResolvePrefetchCoverUrlAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        IReadOnlyList<string> fallbackOrder,
        CancellationToken token)
    {
        foreach (var fallback in fallbackOrder)
        {
            var coverUrl = fallback switch
            {
                AppleSource => await ArtworkFallbackHelper.TryResolveAppleCoverAsync(
                    runtime.AppleCatalog,
                    runtime.HttpClientFactory,
                    new ArtworkFallbackHelper.AppleCoverLookupRequest(
                        execution.Request.Settings,
                        execution.Request.AppleCoverLookupIdOverride ?? execution.Request.Payload.AppleId,
                        execution.Request.Payload.Title,
                        execution.Request.Payload.Artist,
                        execution.Request.Payload.Album),
                    execution.Request.Logger,
                    token),
                DeezerSource => await ArtworkFallbackHelper.TryResolveDeezerCoverAsync(
                    runtime.DeezerClient,
                    execution.Request.Payload.DeezerId,
                    execution.Request.Settings.LocalArtworkSize,
                    NullLogger.Instance,
                    token,
                    execution.Request.Payload.Album),
                SpotifySource => await ArtworkFallbackHelper.TryResolveSpotifyCoverAsync(
                    runtime.SpotifyIdResolver,
                    runtime.SpotifyArtworkResolver,
                    execution.Request.Payload.Title,
                    execution.Request.Payload.Artist,
                    execution.Request.Payload.Album,
                    execution.Request.Payload.Isrc,
                    token),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(coverUrl))
            {
                return coverUrl;
            }
        }

        return null;
    }

    private static async Task RunArtworkPrefetchAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        string? coverUrl,
        bool isAppleCover,
        int appleArtworkSize,
        bool preferMaxQualityCover,
        CancellationToken token)
    {
        var settings = execution.Request.Settings;
        if (settings.SaveArtwork && !string.IsNullOrWhiteSpace(coverUrl))
        {
            await SavePrimaryArtworkAsync(execution, runtime, coverUrl, isAppleCover, appleArtworkSize, preferMaxQualityCover, token);
        }

        if (settings.SaveAnimatedArtwork && runtime.AppleCatalog != null && runtime.HttpClientFactory != null)
        {
            await SaveAnimatedArtworkAsync(execution, runtime, token);
        }

        await SaveArtistArtworkAsync(execution, runtime, appleArtworkSize, preferMaxQualityCover, token);
    }

    private static async Task SavePrimaryArtworkAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        string coverUrl,
        bool isAppleCover,
        int appleArtworkSize,
        bool preferMaxQualityCover,
        CancellationToken token)
    {
        var settings = execution.Request.Settings;
        Directory.CreateDirectory(execution.Paths.CoverPath);
        var coverName = runtime.PathProcessor.GenerateAlbumName(
            settings.CoverImageTemplate,
            execution.Request.Context.Track.Album,
            settings,
            execution.Request.Context.Track.Playlist);
        if (isAppleCover)
        {
            foreach (var format in AppleQueueHelpers.GetArtworkOutputFormats(settings))
            {
                var targetPath = Path.Join(execution.Paths.CoverPath, $"{coverName}.{format}");
                await AppleQueueHelpers.DownloadAppleArtworkAsync(
                    runtime.ImageDownloader,
                    new AppleQueueHelpers.AppleArtworkDownloadRequest
                    {
                        RawUrl = coverUrl,
                        OutputPath = targetPath,
                        Settings = settings,
                        Size = appleArtworkSize,
                        Overwrite = settings.OverwriteFile,
                        PreferMaxQuality = preferMaxQualityCover,
                        Logger = execution.Request.Logger
                    },
                    token);
            }
            return;
        }

        var formats = (settings.LocalArtworkFormat ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var format in formats)
        {
            var ext = format.Equals("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
            var targetPath = Path.Join(execution.Paths.CoverPath, $"{coverName}.{ext}");
            await runtime.ImageDownloader.DownloadImageAsync(
                coverUrl,
                targetPath,
                settings.OverwriteFile,
                preferMaxQualityCover,
                token);
        }
    }

    private static async Task SaveAnimatedArtworkAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        CancellationToken token)
    {
        var settings = execution.Request.Settings;
        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
        var savedAnimated = await AppleQueueHelpers.SaveAnimatedArtworkAsync(
            runtime.AppleCatalog!,
            runtime.HttpClientFactory!,
            new AppleQueueHelpers.AnimatedArtworkSaveRequest
            {
                AppleId = execution.Request.AnimatedArtworkAppleIdOverride ?? execution.Request.Payload.AppleId,
                Title = execution.Request.Payload.Title,
                Artist = execution.Request.Payload.Artist,
                Album = execution.Request.Payload.Album,
                Storefront = storefront,
                MaxResolution = settings.Video.AppleMusicVideoMaxResolution,
                OutputDir = execution.Paths.CoverPath,
                Logger = execution.Request.Logger
            },
            token);
        if (savedAnimated)
        {
            execution.Request.ActivityLog.Info($"Animated artwork saved: {execution.Paths.CoverPath}");
        }
    }

    private static async Task SaveArtistArtworkAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        int appleArtworkSize,
        bool preferMaxQualityCover,
        CancellationToken token)
    {
        var settings = execution.Request.Settings;
        if (!settings.SaveArtworkArtist)
        {
            return;
        }

        var artistImageUrl = await DownloadEngineArtworkHelper.ResolveArtistImageUrlAsync(
            new DownloadEngineArtworkHelper.ArtistImageResolveRequest(
                runtime.AppleCatalog,
                runtime.HttpClientFactory,
                settings,
                runtime.DeezerClient,
                runtime.SpotifyArtworkResolver,
                execution.Request.Payload.AppleId,
                execution.Request.Payload.DeezerId,
                execution.Request.Payload.SpotifyId,
                execution.Request.Payload.Artist,
                NullLogger.Instance),
            token);
        if (string.IsNullOrWhiteSpace(artistImageUrl))
        {
            return;
        }

        await DownloadEngineArtworkHelper.SaveArtistArtworkAsync(
            new DownloadEngineArtworkHelper.SaveArtistArtworkRequest(
                runtime.ImageDownloader,
                runtime.PathProcessor,
                execution.Paths.ArtistPath,
                artistImageUrl,
                settings,
                execution.Request.Context.Track,
                appleArtworkSize,
                preferMaxQualityCover,
                execution.Request.Logger),
            token);
    }

    private static async Task<string> RunLyricsPrefetchAsync(
        PrefetchExecutionContext execution,
        CancellationToken token)
    {
        Directory.CreateDirectory(execution.Paths.FileDir);
        var paths = (
            FilePath: execution.Paths.FileDir,
            Filename: execution.Paths.ExpectedBaseName,
            ExtrasPath: execution.Paths.ExtrasPath,
            CoverPath: execution.Paths.CoverPath,
            ArtistPath: execution.Paths.ArtistPath
        );
        var lyrics = await execution.Request.LyricsService.ResolveLyricsAsync(execution.Request.Context.Track, execution.Request.Settings, token);
        var lyricsType = LyricsPrefetchTypeHelper.ResolveFromLyrics(lyrics);
        if (!string.IsNullOrWhiteSpace(lyricsType))
        {
            QueuePrefetchStatusHelper.Send(execution.Request.Listener, execution.Paths.QueueUuid, FetchingStatus, FetchingStatus, lyricsType);
        }
        if (lyrics != null && lyrics.IsLoaded())
        {
            await execution.Request.LyricsService.SaveLyricsAsync(lyrics, execution.Request.Context.Track, paths, execution.Request.Settings, token);
            var savedLyricsType = LyricsPrefetchTypeHelper.ResolveSavedLyricsType(execution.Paths.FileDir, execution.Paths.ExpectedBaseName);
            if (!string.IsNullOrWhiteSpace(savedLyricsType))
            {
                lyricsType = savedLyricsType;
            }
        }

        return lyricsType;
    }

    public static PrefetchPathContext BuildPrefetchPathContext(
        string queueUuid,
        EngineTrackContext context,
        string expectedOutputPath)
    {
        var normalizedQueueUuid = string.IsNullOrWhiteSpace(queueUuid) ? "unknown" : queueUuid;
        var fileDir = DownloadPathResolver.ResolveIoPath(context.PathResult.FilePath);
        var coverPath = DownloadPathResolver.ResolveIoPath(context.PathResult.CoverPath ?? context.PathResult.FilePath);
        var artistPath = DownloadPathResolver.ResolveIoPath(context.PathResult.ArtistPath ?? context.PathResult.CoverPath ?? context.PathResult.FilePath);
        var extrasPath = DownloadPathResolver.ResolveIoPath(context.PathResult.ExtrasPath);
        var expectedBaseName = Path.GetFileNameWithoutExtension(expectedOutputPath);
        if (string.IsNullOrWhiteSpace(expectedBaseName))
        {
            expectedBaseName = context.PathResult.Filename;
        }

        return new PrefetchPathContext(
            normalizedQueueUuid,
            fileDir,
            coverPath,
            artistPath,
            extrasPath,
            expectedBaseName);
    }

    public static void UpdateAudioPayloadFiles(EngineQueueItemBase payload, PathGenerationResult pathResult, string outputPath)
    {
        var result = QueuePayloadFileHelper.BuildAudioFiles(pathResult, outputPath);
        payload.Files = result.Files;
        payload.LyricsStatus = result.LyricsStatus;
    }

    public static bool ShouldSaveLyrics(DeezSpoTagSettings settings) => LyricsSettingsPolicy.CanFetchLyrics(settings);

    public static async Task UpdateWatchlistTrackStatusAsync(
        EngineQueueItemBase payload,
        string status,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.WatchlistSource)
            || string.IsNullOrWhiteSpace(payload.WatchlistPlaylistId)
            || string.IsNullOrWhiteSpace(payload.WatchlistTrackId))
        {
            return;
        }

        using var scope = serviceProvider.CreateScope();
        var libraryRepository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!libraryRepository.IsConfigured)
        {
            return;
        }

        await libraryRepository.UpdatePlaylistWatchTrackStatusAsync(
            payload.WatchlistSource,
            payload.WatchlistPlaylistId,
            payload.WatchlistTrackId,
            status,
            cancellationToken);
    }

    public static async Task<TPayload?> InitializeQueueItemAsync<TPayload>(
        DownloadQueueItem queueItem,
        string? payloadJson,
        Func<string, TPayload?> deserialize,
        InitializeQueueItemContext<TPayload> context,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        var payload = deserialize(payloadJson ?? string.Empty);
        if (payload == null)
        {
            await context.QueueRepository.UpdateStatusAsync(queueItem.QueueUuid, FailedStatus, "Invalid payload", cancellationToken: cancellationToken);
            context.RetryScheduler.ScheduleRetry(queueItem.QueueUuid, context.EngineName, "invalid payload");
            return null;
        }

        if (DownloadEngineSettingsHelper.IsAtmosOnlyPayload(payload.ContentType, payload.Quality))
        {
            const string message = "Atmos payload must be processed by Apple engine.";
            context.ActivityLog.Warn($"Atmos guard blocked non-Apple processing: {queueItem.QueueUuid} engine={context.EngineName}");
            var advanced = await context.TryAdvanceAsync(
                queueItem.QueueUuid,
                queueItem.Engine,
                payload,
                cancellationToken);
            if (advanced)
            {
                context.ActivityLog.Info($"Fallback advanced: {queueItem.QueueUuid} -> {payload.Engine} (auto_index={payload.AutoIndex})");
                if (!payload.FallbackQueuedExternally)
                {
                    context.Listener.SendAddedToQueue(context.QueuePayloadFactory(payload));
                }
                return null;
            }

            await context.QueueRepository.UpdateStatusAsync(queueItem.QueueUuid, FailedStatus, message, cancellationToken: cancellationToken);
            context.RetryScheduler.ScheduleRetry(queueItem.QueueUuid, context.EngineName, message);
            return null;
        }

        await DownloadEngineSettingsHelper.ResolveAndApplyProfileAsync(
            context.TagSettingsResolver,
            context.Settings,
            payload.DestinationFolderId,
            context.Logger,
            cancellationToken);
        await context.FolderConversionSettingsOverlay.ApplyAsync(context.Settings, payload.DestinationFolderId, cancellationToken);
        DownloadEngineSettingsHelper.ApplyQualityBucketToSettings(context.Settings, payload.QualityBucket);
        context.Listener.SendStartDownload(queueItem.QueueUuid);
        context.Listener.Send(UpdateQueueEvent, new
        {
            uuid = queueItem.QueueUuid,
            progress = payload.Progress,
            downloaded = payload.Downloaded,
            failed = payload.Failed
        });

        await context.QueueRepository.UpdateStatusAsync(queueItem.QueueUuid, RunningStatus, progress: payload.Progress, cancellationToken: cancellationToken);
        return payload;
    }

    public static async Task HandleCancellationAsync<TPayload>(
        string queueUuid,
        TPayload? payload,
        CancellationHandlingContext context,
        CancellationToken cancellationToken = default)
        where TPayload : EngineQueueItemBase
    {
        var current = await context.QueueRepository.GetByUuidAsync(queueUuid, cancellationToken);
        var status = current?.Status ?? CancelledStatus;
        if (status is CompletedStatusName or FailedStatus)
        {
            return;
        }

        if (context.CancellationRegistry.WasUserPaused(queueUuid))
        {
            await context.QueueRepository.UpdateStatusAsync(queueUuid, PausedStatus, cancellationToken: cancellationToken);
            context.Listener.Send(UpdateQueueEvent, new { uuid = queueUuid, status = PausedStatus });
            return;
        }

        if (context.CancellationRegistry.WasUserCanceled(queueUuid))
        {
            await context.QueueRepository.UpdateStatusAsync(queueUuid, CanceledStatus, cancellationToken: cancellationToken);
            context.Listener.Send(UpdateQueueEvent, new { uuid = queueUuid, status = CanceledStatus });
            return;
        }

        await context.QueueRepository.UpdateStatusAsync(queueUuid, CancelledStatus, "Cancelled", cancellationToken: cancellationToken);
        if (payload != null)
        {
            await UpdateWatchlistTrackStatusAsync(payload, CancelledStatus, context.ServiceProvider, cancellationToken);
        }

        context.RetryScheduler.ScheduleRetry(queueUuid, context.EngineName, CancelledStatus);
    }

    public static async Task HandleFailureAsync<TPayload>(
        Exception exception,
        string queueUuid,
        TPayload? payload,
        FailureHandlingContext<TPayload> context,
        CancellationToken stoppingToken)
        where TPayload : EngineQueueItemBase
    {
        context.Logger.LogError(exception, "{Engine} download failed for {QueueUuid}", context.EngineName, queueUuid);
        if (payload != null && !stoppingToken.IsCancellationRequested)
        {
            var quality = string.IsNullOrWhiteSpace(payload.Quality) ? "unknown" : payload.Quality;
            context.ActivityLog.Warn($"Download failed (engine={context.EngineName} quality={quality}): {queueUuid} {exception.Message}");
            var advanced = await context.TryAdvanceAsync(
                queueUuid,
                payload.Engine,
                payload,
                stoppingToken);
            if (advanced)
            {
                context.ActivityLog.Info($"Fallback advanced: {queueUuid} -> {payload.Engine} (auto_index={payload.AutoIndex})");
                if (!payload.FallbackQueuedExternally)
                {
                    context.Listener.SendAddedToQueue(context.QueuePayloadFactory(payload));
                }
                return;
            }
        }

        await context.QueueRepository.UpdateStatusAsync(queueUuid, FailedStatus, exception.Message, cancellationToken: CancellationToken.None);
        if (payload != null)
        {
            await UpdateWatchlistTrackStatusAsync(payload, FailedStatus, context.ServiceProvider, CancellationToken.None);
        }

        context.ActivityLog.Error($"Download failed (engine={context.EngineName}): {queueUuid} {exception.Message}");
        context.RetryScheduler.ScheduleRetry(queueUuid, context.EngineName, exception.Message);
    }
}
