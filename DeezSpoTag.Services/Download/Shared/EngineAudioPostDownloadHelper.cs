using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;

namespace DeezSpoTag.Services.Download.Shared;

public static partial class EngineAudioPostDownloadHelper
{
    private const string PlaylistType = "playlist";
    private const string AlbumType = "album";
    private const string TrackType = "track";
    private const string DeezerSource = "deezer";
    private const string SpotifySource = "spotify";
    private const string AppleSource = "apple";
    private const string QobuzSource = "qobuz";
    private const string TidalSource = "tidal";
    private const string AmazonSource = "amazon";
    private const string MzStaticHost = "mzstatic.com";
    private const string UnknownArtist = "Unknown Artist";
    private const string CompletedStatus = "completed";
    private const string DownloadedStatus = "downloaded";
    private const string FailedStatus = "failed";
    private const string FetchingStatus = "fetching";
    private const string SkippedStatus = "skipped";
    private const string NoLyricsStatus = "no-lyrics";
    private const string CompletedStatusName = "completed";
    private const string CancelledStatus = "cancelled";
    private const string PausedStatus = "paused";
    private const string CanceledStatus = "canceled";
    private const string UpdateQueueEvent = "updateQueue";
    private const string DeezerTrackIdKey = "deezer_track_id";
    private const int WatchlistUnavailableRecheckDays = 7;
    private static readonly TimeSpan PrefetchCancelDrainTimeout = TimeSpan.FromSeconds(15);
    private static readonly Regex LrcTimestampRegex = LrcTimestampPatternRegex();

    [GeneratedRegex(
        @"^\[(?<m>\d{1,3}):(?<s>\d{2})(?:\.(?<f>\d{1,3}))?\]",
        RegexOptions.CultureInvariant,
        250)]
    private static partial Regex LrcTimestampPatternRegex();
    [GeneratedRegex(
        @"\s*\(\s*Dolby\s+Atmos\s+Version\s*\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        250)]
    private static partial Regex TidalAtmosAlbumFolderSuffixRegex();
    private static readonly HashSet<string> KnownAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac",
        ".mp3",
        ".m4a",
        ".mp4",
        ".aac",
        ".alac",
        ".wav",
        ".aif",
        ".aiff",
        ".ogg",
        ".oga",
        ".opus",
        ".wma",
        ".mka",
        ".webm"
    };

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
        DownloadQueueRepository QueueRepository,
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

    private sealed record PrefetchRequirements(
        bool ShouldFetchPrimaryArtwork,
        bool ShouldFetchAnimatedArtwork,
        bool ShouldFetchArtistArtwork,
        bool ShouldFetchLyrics,
        bool ShouldRequireEmbeddedArtwork)
    {
        public bool ShouldFetchArtwork => ShouldFetchPrimaryArtwork || ShouldFetchAnimatedArtwork || ShouldFetchArtistArtwork;
        public bool ShouldQueueWork => ShouldFetchArtwork || ShouldFetchLyrics;
    }

    private sealed record PrefetchArtworkResult(bool Success, string? FailureReason = null);

    private sealed record PrefetchCompletionResult(
        bool ShouldValidateArtwork,
        bool ArtworkReady,
        string? ArtworkFailureReason);

    private sealed class PrefetchRunState
    {
        public PrefetchRunState(PrefetchRequirements requirements)
        {
            ArtworkStatus = requirements.ShouldFetchArtwork ? FetchingStatus : SkippedStatus;
            LyricsStatus = requirements.ShouldFetchLyrics ? FetchingStatus : SkippedStatus;
            ArtworkResult = new PrefetchArtworkResult(!requirements.ShouldFetchArtwork);
        }

        public string ArtworkStatus { get; set; }

        public string LyricsStatus { get; set; }

        public string LyricsType { get; set; } = string.Empty;

        public PrefetchArtworkResult ArtworkResult { get; set; }
    }

    private sealed class PrefetchGateState
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource<PrefetchCompletionResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PrefetchExecutionContext(
        PrefetchRequest Request,
        PrefetchPathContext Paths,
        PrefetchRequirements Requirements,
        PrefetchGateState GateState);

    private sealed record PrefetchRuntimeServices(
        ImageDownloader ImageDownloader,
        EnhancedPathTemplateProcessor PathProcessor,
        ISpotifyArtworkResolver? SpotifyArtworkResolver,
        ILastFmArtistImageResolver? LastFmArtistImageResolver,
        ISpotifyIdResolver? SpotifyIdResolver,
        ITrackIdentityResolver? TrackIdentityResolver,
        IHttpClientFactory? HttpClientFactory,
        AppleMusicCatalogService? AppleCatalog,
        DeezerClient? DeezerApiClient);

    private sealed record AppleArtworkIdentity(
        string? AppleId,
        string? AlbumId,
        string? AlbumName,
        string? ArtistName,
        string? Isrc,
        int? DurationMs);

    private static readonly ConcurrentDictionary<string, PrefetchGateState> PrefetchGates =
        new(StringComparer.OrdinalIgnoreCase);

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
        return BuildTrackContextFromTrack(track, payload, settings, pathProcessor, downloadTypeResolver);
    }

    public static EngineTrackContext BuildTrackContextFromTrack(
        Track track,
        EngineQueueItemBase payload,
        DeezSpoTagSettings settings,
        EnhancedPathTemplateProcessor pathProcessor,
        Func<EngineQueueItemBase, string>? downloadTypeResolver = null)
    {
        if (!string.IsNullOrWhiteSpace(payload.CollectionName)
            && string.Equals(payload.CollectionType, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            track.Playlist = new Playlist("0", payload.CollectionName);
        }

        track.ApplySettings(settings);

        var downloadType = ResolveDownloadType(payload, downloadTypeResolver);
        var pathResult = GeneratePathResult(track, payload, settings, pathProcessor, downloadType);
        var filenameStem = ResolveFilenameStem(pathResult.Filename);
        if (string.IsNullOrWhiteSpace(filenameStem))
        {
            filenameStem = pathResult.Filename;
        }

        var outputDir = DownloadPathResolver.ResolveIoPath(pathResult.FilePath);
        return new EngineTrackContext(track, pathResult, outputDir, $"literal:{filenameStem}");
    }

    private static PathGenerationResult GeneratePathResult(
        Track track,
        EngineQueueItemBase payload,
        DeezSpoTagSettings settings,
        EnhancedPathTemplateProcessor pathProcessor,
        string downloadType)
    {
        var originalAlbumTitle = track.Album?.Title;
        var folderAlbumTitle = ResolveAlbumFolderTitle(payload, originalAlbumTitle);
        var shouldRestoreAlbumTitle = track.Album != null
            && !string.Equals(originalAlbumTitle, folderAlbumTitle, StringComparison.Ordinal);
        if (shouldRestoreAlbumTitle)
        {
            track.Album!.Title = folderAlbumTitle;
        }

        try
        {
            return pathProcessor.GeneratePaths(track, downloadType, settings);
        }
        finally
        {
            if (shouldRestoreAlbumTitle)
            {
                track.Album!.Title = originalAlbumTitle ?? string.Empty;
            }
        }
    }

    private static string ResolveAlbumFolderTitle(EngineQueueItemBase payload, string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle) || !IsTidalAtmosPayload(payload))
        {
            return albumTitle ?? string.Empty;
        }

        var cleaned = TidalAtmosAlbumFolderSuffixRegex().Replace(albumTitle, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? albumTitle.Trim() : cleaned;
    }

    private static bool IsTidalAtmosPayload(EngineQueueItemBase payload)
    {
        var isTidal = string.Equals(payload.Engine, TidalSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(payload.SourceService, TidalSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(payload.ResolvedEngine, TidalSource, StringComparison.OrdinalIgnoreCase);
        if (!isTidal)
        {
            return false;
        }

        return ContainsAtmos(payload.Quality)
            || ContainsAtmos(payload.QualityBucket)
            || ContainsAtmos(payload.ContentType)
            || payload.FallbackPlan.Any(static step =>
                ContainsAtmos(step.Quality)
                || ContainsAtmos(step.ResolutionStrategy));
    }

    private static bool ContainsAtmos(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains("atmos", StringComparison.OrdinalIgnoreCase);

    public static async Task<string?> ResolveProfileDownloadTagSourceAsync(
        IDownloadTagSettingsResolver tagSettingsResolver,
        long? destinationFolderId,
        DeezSpoTagSettings settings,
        string engineName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await tagSettingsResolver.ResolveProfileAsync(destinationFolderId, cancellationToken);
            if (profile == null)
            {
                return null;
            }

            return DownloadTagSourceHelper.ResolveDownloadTagSource(
                profile.DownloadTagSource,
                engineName,
                settings.Service);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Failed to resolve profile download tag source for folder {FolderId}", destinationFolderId);
            }

            return null;
        }
    }

    public sealed record ProfileMetadataOverrideRequest(
        Track Track,
        EngineQueueItemBase Payload,
        DeezSpoTagSettings Settings,
        IServiceProvider ServiceProvider,
        string EngineName,
        string? ResolvedDownloadTagSource,
        ILogger Logger,
        CancellationToken CancellationToken);

    public static async Task<bool> ApplyProfileMetadataOverrideAsync(ProfileMetadataOverrideRequest request)
    {
        var source = DownloadTagSourceHelper.NormalizeResolvedDownloadTagSource(request.ResolvedDownloadTagSource);
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        using var scope = request.ServiceProvider.CreateScope();
        var registry = scope.ServiceProvider.GetService<IMetadataResolverRegistry>();
        if (registry == null)
        {
            request.Logger.LogWarning(
                "{Engine} profile metadata registry unavailable; skipping profile source {Source} for track {TrackId}",
                request.EngineName,
                source,
                request.Track.Id);
            return false;
        }

        var resolver = registry.GetResolver(source);
        if (resolver == null)
        {
            request.Logger.LogWarning(
                "{Engine} profile metadata source {Source} is not registered; skipping metadata override for track {TrackId}",
                request.EngineName,
                source,
                request.Track.Id);
            return false;
        }

        var originalTrack = TrackMetadataSnapshot.Capture(request.Track);
        var originalPayload = PayloadSourceIdentitySnapshot.Capture(request.Payload);
        ApplyResolvedTagSourceIdentity(request.Track, request.Payload, source);
        await TryHydrateResolvedTagSourceIdentityAsync(
            request.Track,
            request.Payload,
            source,
            scope.ServiceProvider,
            request.Logger,
            request.CancellationToken);
        ApplyResolvedTagSourceIdentity(request.Track, request.Payload, source);
        try
        {
            await resolver.ResolveTrackAsync(request.Track, request.Settings, request.CancellationToken);
            if (!IsResolvedMetadataCompatible(originalTrack, request.Payload, request.Track))
            {
                originalTrack.Restore(request.Track);
                originalPayload.Restore(request.Payload);
                request.Logger.LogWarning(
                    "{Engine} profile metadata source {Source} rejected for track {TrackId}: resolved identity does not match requested queue identity.",
                    request.EngineName,
                    source,
                    request.Track.Id);
                return false;
            }

            PreserveRequestedAlbumTextStyle(originalTrack, request.Payload, request.Track, request.EngineName, source, request.Logger);
            ApplyResolvedTagSourceIdentity(request.Track, request.Payload, source);
            request.Track.ApplySettings(request.Settings);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            originalTrack.Restore(request.Track);
            originalPayload.Restore(request.Payload);
            request.Logger.LogWarning(
                ex,
                "{Engine} profile metadata resolver failed for source {Source} and track {TrackId}",
                request.EngineName,
                source,
                request.Track.Id);
            return false;
        }
    }

    private static async Task TryHydrateResolvedTagSourceIdentityAsync(
        Track track,
        EngineQueueItemBase payload,
        string source,
        IServiceProvider scopedProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(source, DeezerSource, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var existingSourceId = ResolveTagSourceId(payload, source, track);
        if (!string.IsNullOrWhiteSpace(existingSourceId))
        {
            return;
        }

        var deezerClient = scopedProvider.GetService<DeezerClient>();
        if (deezerClient == null)
        {
            return;
        }

        try
        {
            var resolvedDeezerId = await ResolveDeezerIdForTagSourceAsync(
                track,
                payload,
                deezerClient,
                logger,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(resolvedDeezerId))
            {
                return;
            }

            payload.DeezerId = resolvedDeezerId;
            track.Urls[DeezerTrackIdKey] = resolvedDeezerId;
            track.Urls[DeezerSource] = $"https://www.deezer.com/track/{resolvedDeezerId}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    ex,
                    "Failed to hydrate Deezer identity for metadata override on track {TrackId}",
                    track.Id);
            }
        }
    }

    private static async Task<string?> ResolveDeezerIdForTagSourceAsync(
        Track track,
        EngineQueueItemBase payload,
        DeezerClient deezerClient,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var knownDeezerId = ResolveKnownDeezerId(track, payload);
        if (!string.IsNullOrWhiteSpace(knownDeezerId))
        {
            return knownDeezerId;
        }

        return await ResolveDeezerIdFromLookupAsync(track, payload, deezerClient, logger, cancellationToken);
    }

    private static string? ResolveKnownDeezerId(Track track, EngineQueueItemBase payload)
    {
        return NormalizeDeezerId(FirstNonEmpty(
            payload.DeezerId,
            track.Urls.GetValueOrDefault(DeezerTrackIdKey),
            track.Urls.GetValueOrDefault("deezer_id"),
            ExtractSourceTrackId(track.Urls.GetValueOrDefault("deezer"), DeezerSource),
            ExtractSourceTrackId(payload.SourceUrl, DeezerSource, allowRawId: false),
            ExtractSourceTrackId(payload.Url, DeezerSource, allowRawId: false),
            ExtractSourceTrackId(track.DownloadURL, DeezerSource, allowRawId: false)));
    }

    private static async Task<string?> ResolveDeezerIdFromLookupAsync(
        Track track,
        EngineQueueItemBase payload,
        DeezerClient deezerClient,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var isrc = FirstNonEmpty(track.ISRC, payload.Isrc);
        var fromIsrc = await TryResolveDeezerIdByIsrcAsync(isrc, deezerClient, logger, cancellationToken);
        if (!string.IsNullOrWhiteSpace(fromIsrc))
        {
            return fromIsrc;
        }

        var artist = FirstNonEmpty(track.MainArtist?.Name, track.ArtistString, payload.Artist);
        var title = FirstNonEmpty(track.Title, payload.Title);
        return await TryResolveDeezerIdByMetadataAsync(track, payload, artist, title, deezerClient, logger, cancellationToken);
    }

    private static async Task<string?> TryResolveDeezerIdByIsrcAsync(
        string? isrc,
        DeezerClient deezerClient,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        try
        {
            var byIsrc = await deezerClient.GetTrackByIsrcAsync(isrc);
            return NormalizeDeezerId(byIsrc?.Id);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Failed to resolve Deezer track id by ISRC {Isrc}.", isrc);
            }
        }

        return null;
    }

    private static async Task<string?> TryResolveDeezerIdByMetadataAsync(
        Track track,
        EngineQueueItemBase payload,
        string? artist,
        string? title,
        DeezerClient deezerClient,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var durationMs = ResolveDurationMilliseconds(track, payload);
        var album = FirstNonEmpty(track.Album?.Title, payload.Album) ?? string.Empty;

        try
        {
            var byMetadata = await deezerClient.GetTrackIdFromMetadataAsync(
                artist,
                title,
                album,
                durationMs);
            return NormalizeDeezerId(byMetadata);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Failed to resolve Deezer track id by metadata for {Artist} - {Title}.", artist, title);
            }
        }

        return null;
    }

    private static int? ResolveDurationMilliseconds(Track track, EngineQueueItemBase payload)
    {
        if (track.Duration > 0)
        {
            return track.Duration * 1000;
        }

        return payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : null;
    }

    private static string? NormalizeDeezerId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (long.TryParse(candidate, out var numeric) && numeric > 0)
        {
            return numeric.ToString();
        }

        if (candidate.StartsWith("deezer:track:", StringComparison.OrdinalIgnoreCase))
        {
            var rawId = candidate["deezer:track:".Length..].Trim();
            return long.TryParse(rawId, out numeric) && numeric > 0 ? numeric.ToString() : null;
        }

        var fromUrl = ExtractSourceTrackId(candidate, DeezerSource, allowRawId: false);
        if (long.TryParse(fromUrl, out numeric) && numeric > 0)
        {
            return numeric.ToString();
        }

        return null;
    }

    private static void ApplyResolvedTagSourceIdentity(Track track, EngineQueueItemBase payload, string source)
    {
        track.Source = source;
        var sourceId = ResolveTagSourceId(payload, source, track);
        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            track.SourceId = sourceId;
            ApplyResolvedPayloadSourceId(payload, source, sourceId);
        }
    }

    private static void ApplyResolvedPayloadSourceId(EngineQueueItemBase payload, string source, string sourceId)
    {
        switch (source)
        {
            case DeezerSource:
                payload.DeezerId = sourceId;
                return;
            case SpotifySource:
                payload.SpotifyId = sourceId;
                return;
            case AppleSource:
                payload.AppleId = sourceId;
                return;
        }

        var property = payload.GetType().GetProperty(
            $"{source}Id",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property?.CanWrite == true && property.PropertyType == typeof(string))
        {
            property.SetValue(payload, sourceId);
        }
    }

    private static string? ResolveTagSourceId(EngineQueueItemBase payload, string source, Track track)
    {
        return source switch
        {
            DeezerSource => FirstNonEmpty(
                NormalizeDeezerId(payload.DeezerId),
                NormalizeDeezerId(track.Urls.GetValueOrDefault(DeezerTrackIdKey)),
                NormalizeDeezerId(track.Urls.GetValueOrDefault("deezer_id")),
                NormalizeDeezerId(ExtractSourceTrackId(track.Urls.GetValueOrDefault(DeezerSource), DeezerSource))),
            SpotifySource => FirstNonEmpty(
                payload.SpotifyId,
                track.Urls.GetValueOrDefault("spotify_track_id"),
                track.Urls.GetValueOrDefault("spotify_id"),
                ExtractSourceTrackId(track.Urls.GetValueOrDefault(SpotifySource), SpotifySource)),
            AppleSource => FirstNonEmpty(
                payload.AppleId,
                track.Urls.GetValueOrDefault("apple_track_id"),
                track.Urls.GetValueOrDefault("apple_id"),
                ExtractSourceTrackId(track.Urls.GetValueOrDefault(AppleSource), AppleSource)),
            QobuzSource => FirstNonEmpty(
                ReadStringProperty(payload, "QobuzId"),
                ReadStringProperty(payload, "QobuzTrackId"),
                track.Urls.GetValueOrDefault("qobuz_track_id"),
                track.Urls.GetValueOrDefault("qobuz_id"),
                ExtractSourceTrackId(track.Urls.GetValueOrDefault(QobuzSource), QobuzSource)),
            TidalSource => FirstNonEmpty(
                ReadStringProperty(payload, "TidalId"),
                ReadStringProperty(payload, "TidalTrackId"),
                track.Urls.GetValueOrDefault("tidal_track_id"),
                track.Urls.GetValueOrDefault("tidal_id"),
                ExtractSourceTrackId(track.Urls.GetValueOrDefault(TidalSource), TidalSource)),
            AmazonSource => FirstNonEmpty(
                ReadStringProperty(payload, "AmazonId"),
                ReadStringProperty(payload, "AmazonTrackId"),
                track.Urls.GetValueOrDefault("amazon_track_id"),
                track.Urls.GetValueOrDefault("amazon_id"),
                ExtractSourceTrackId(track.Urls.GetValueOrDefault(AmazonSource), AmazonSource)),
            _ => FirstNonEmpty(
                ReadStringProperty(payload, $"{source}Id"),
                ReadStringProperty(payload, $"{source}TrackId"),
                track.Urls.GetValueOrDefault($"{source}_track_id"),
                track.Urls.GetValueOrDefault($"{source}_id"),
                ExtractSourceTrackId(track.Urls.GetValueOrDefault(source), source))
        };
    }

    private static bool IsResolvedMetadataCompatible(
        TrackMetadataSnapshot original,
        EngineQueueItemBase payload,
        Track resolved)
    {
        var expectedTitle = FirstNonEmpty(payload.Title, original.Title);
        var resolvedTitle = FirstNonEmpty(resolved.Title, original.Title);
        if (!string.IsNullOrWhiteSpace(expectedTitle)
            && !string.IsNullOrWhiteSpace(resolvedTitle)
            && !TrackTitleMatcher.TitlesMatch(expectedTitle, resolvedTitle))
        {
            return false;
        }

        var expectedArtist = FirstNonEmpty(payload.Artist, original.Artist);
        var resolvedArtist = FirstNonEmpty(resolved.MainArtist?.Name, resolved.ArtistString, resolved.ArtistsString);
        if (!string.IsNullOrWhiteSpace(expectedArtist)
            && !string.IsNullOrWhiteSpace(resolvedArtist)
            && !TrackTitleMatcher.ArtistsMatch(expectedArtist, resolvedArtist))
        {
            return false;
        }

        var expectedIsrc = FirstNonEmpty(payload.Isrc, original.Isrc);
        if (!string.IsNullOrWhiteSpace(expectedIsrc)
            && !string.IsNullOrWhiteSpace(resolved.ISRC)
            && !string.Equals(expectedIsrc.Trim(), resolved.ISRC.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedDurationMs = ResolveExpectedDurationMilliseconds(original, payload);
        if (expectedDurationMs > 0 && resolved.Duration > 0)
        {
            var resolvedDurationMs = resolved.Duration * 1000;
            if (Math.Abs(expectedDurationMs - resolvedDurationMs) > 10_000)
            {
                return false;
            }
        }

        return true;
    }

    private static void PreserveRequestedAlbumTextStyle(
        TrackMetadataSnapshot original,
        EngineQueueItemBase payload,
        Track resolved,
        string engineName,
        string source,
        ILogger logger)
    {
        var requestedAlbum = FirstNonEmpty(payload.Album, original.AlbumTitle);
        var resolvedAlbum = resolved.Album?.Title;
        if (string.IsNullOrWhiteSpace(requestedAlbum)
            || string.IsNullOrWhiteSpace(resolvedAlbum)
            || string.Equals(requestedAlbum.Trim(), resolvedAlbum.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        if (!IsLatinScriptText(requestedAlbum) || !HasNonLatinLetters(resolvedAlbum))
        {
            return;
        }

        resolved.Album ??= new Album("0", requestedAlbum);
        resolved.Album.Title = requestedAlbum.Trim();
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "{Engine} profile metadata source {Source} kept requested album title '{RequestedAlbum}' instead of localized album title '{ResolvedAlbum}'.",
                engineName,
                source,
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(requestedAlbum),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(resolvedAlbum));
        }
    }

    private static bool IsLatinScriptText(string value)
    {
        var hasLetter = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (!RuneIsLetter(rune))
            {
                continue;
            }

            hasLetter = true;
            if (!IsLatinLetter(rune.Value))
            {
                return false;
            }
        }

        return hasLetter;
    }

    private static bool HasNonLatinLetters(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (RuneIsLetter(rune) && !IsLatinLetter(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RuneIsLetter(Rune rune)
        => Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter;

    private static bool IsLatinLetter(int codePoint)
        => codePoint is >= 0x0041 and <= 0x007A
           or >= 0x00C0 and <= 0x024F
           or >= 0x1E00 and <= 0x1EFF;

    private static double ResolveExpectedDurationMilliseconds(TrackMetadataSnapshot original, EngineQueueItemBase payload)
    {
        if (original.DurationSeconds > 0)
        {
            return (double)original.DurationSeconds * 1000;
        }

        return payload.DurationSeconds > 0 ? (double)payload.DurationSeconds * 1000 : 0;
    }

    private static string? ReadStringProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return property?.GetValue(instance) as string;
    }

    private static void WriteStringProperty(object instance, string propertyName, string? value)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property?.CanWrite == true && property.PropertyType == typeof(string))
        {
            property.SetValue(instance, value ?? string.Empty);
        }
    }

    private static string? ExtractSourceTrackId(string? value, string source, bool allowRawId = true)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (allowRawId && !LooksLikeUrlOrUri(candidate))
        {
            return candidate;
        }

        var sourcePrefix = $"{source}:track:";
        if (candidate.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rawId = candidate[sourcePrefix.Length..].Trim();
            return string.IsNullOrWhiteSpace(rawId) ? null : rawId;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !IsTrustedSourceHost(uri.Host, source))
        {
            return null;
        }

        if (string.Equals(source, SpotifySource, StringComparison.OrdinalIgnoreCase))
        {
            return ExtractSegmentAfter(uri, "track");
        }

        if (string.Equals(source, AppleSource, StringComparison.OrdinalIgnoreCase))
        {
            var appleTrackId = ExtractQueryValue(uri.Query, "i");
            if (!string.IsNullOrWhiteSpace(appleTrackId))
            {
                return appleTrackId;
            }
        }

        return ExtractTrailingId(uri.GetLeftPart(UriPartial.Path));
    }

    private static bool LooksLikeUrlOrUri(string value)
    {
        return value.Contains("://", StringComparison.Ordinal)
            || value.Contains('.', StringComparison.Ordinal)
            || value.Contains(':', StringComparison.Ordinal);
    }

    private static bool IsTrustedSourceHost(string host, string source)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        return source.ToLowerInvariant() switch
        {
            DeezerSource => HostMatches(normalizedHost, "deezer.com"),
            SpotifySource => HostMatches(normalizedHost, "spotify.com"),
            AppleSource => HostMatches(normalizedHost, "music.apple.com") || HostMatches(normalizedHost, "itunes.apple.com"),
            QobuzSource => HostMatches(normalizedHost, "qobuz.com"),
            TidalSource => HostMatches(normalizedHost, "tidal.com"),
            AmazonSource => HostMatches(normalizedHost, "amazon.com") || HostMatches(normalizedHost, "music.amazon.com"),
            _ => false
        };
    }

    private static bool HostMatches(string host, string expectedRoot)
    {
        return string.Equals(host, expectedRoot, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{expectedRoot}", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractSegmentAfter(Uri uri, string segmentName)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], segmentName, StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return null;
    }

    private static string? ExtractQueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmed = query.TrimStart('?');
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var name = separator >= 0 ? pair[..separator] : pair;
            if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            return string.IsNullOrWhiteSpace(value) ? null : Uri.UnescapeDataString(value);
        }

        return null;
    }

    private static string? ExtractTrailingId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimEnd('/');
        var queryIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            trimmed = trimmed[..queryIndex].TrimEnd('/');
        }

        var slashIndex = trimmed.LastIndexOf('/');
        return slashIndex >= 0 && slashIndex < trimmed.Length - 1
            ? trimmed[(slashIndex + 1)..]
            : trimmed;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private sealed record TrackMetadataSnapshot(
        string? Title,
        string? Artist,
        string? ArtistString,
        string? ArtistsString,
        string? AlbumTitle,
        string? Isrc,
        int DurationSeconds,
        string? Source,
        string? SourceId,
        IReadOnlyDictionary<string, string> Urls)
    {
        public static TrackMetadataSnapshot Capture(Track track)
        {
            return new TrackMetadataSnapshot(
                track.Title,
                FirstNonEmpty(track.MainArtist?.Name, track.ArtistString, track.ArtistsString),
                track.ArtistString,
                track.ArtistsString,
                track.Album?.Title,
                track.ISRC,
                track.Duration,
                track.Source,
                track.SourceId,
                new Dictionary<string, string>(track.Urls, StringComparer.OrdinalIgnoreCase));
        }

        public void Restore(Track track)
        {
            track.Title = Title ?? string.Empty;
            track.ISRC = Isrc ?? string.Empty;
            track.Duration = DurationSeconds;
            track.ArtistString = ArtistString ?? string.Empty;
            track.ArtistsString = ArtistsString ?? string.Empty;
            track.Source = Source ?? string.Empty;
            track.SourceId = SourceId ?? string.Empty;
            track.Urls = new Dictionary<string, string>(Urls, StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(Artist))
            {
                track.MainArtist = new Artist("0", Artist);
                track.Artists = new List<string> { Artist };
                track.Artist["Main"] = new List<string> { Artist };
            }

            if (track.Album != null)
            {
                track.Album.Title = AlbumTitle ?? string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(AlbumTitle))
            {
                track.Album = new Album("0", AlbumTitle);
            }
        }
    }

    private sealed record PayloadSourceIdentitySnapshot(IReadOnlyDictionary<string, string?> Values)
    {
        private static readonly string[] SourceIdentityProperties =
        [
            "DeezerId",
            "SpotifyId",
            "AppleId",
            "QobuzId",
            "QobuzTrackId",
            "TidalId",
            "TidalTrackId",
            "AmazonId",
            "AmazonTrackId"
        ];

        public static PayloadSourceIdentitySnapshot Capture(EngineQueueItemBase payload)
        {
            return new PayloadSourceIdentitySnapshot(SourceIdentityProperties
                .ToDictionary(
                    static propertyName => propertyName,
                    propertyName => ReadStringProperty(payload, propertyName),
                    StringComparer.OrdinalIgnoreCase));
        }

        public void Restore(EngineQueueItemBase payload)
        {
            foreach (var entry in Values)
            {
                WriteStringProperty(payload, entry.Key, entry.Value);
            }
        }
    }

    private static string ResolveFilenameStem(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return string.Empty;
        }

        var normalized = filename.Trim();
        var extension = Path.GetExtension(normalized);
        if (string.IsNullOrWhiteSpace(extension) || !KnownAudioExtensions.Contains(extension))
        {
            return normalized;
        }

        var stem = Path.GetFileNameWithoutExtension(normalized);
        return string.IsNullOrWhiteSpace(stem) ? normalized : stem;
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
            SourceId = NormalizeSourceId(sourceId),
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
        if (!string.IsNullOrWhiteSpace(payload.SpotifyId))
        {
            track.Urls["spotify_track_id"] = payload.SpotifyId;
            track.Urls[SpotifySource] = $"https://open.spotify.com/track/{payload.SpotifyId}";
        }

        if (!string.IsNullOrWhiteSpace(payload.DeezerId))
        {
            track.Urls[DeezerTrackIdKey] = payload.DeezerId;
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

    private static string? NormalizeSourceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
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
        SynchronizeTrackWithPayloadForTagging(request.Context.Track, request.Payload);

        var audioTagger = request.Scope.GetRequiredService<AudioTagger>();
        var pathProcessor = request.Scope.GetRequiredService<EnhancedPathTemplateProcessor>();
        if (ShouldAllowPlaylistCover(request.Payload, request.Settings))
        {
            ApplyPrefetchedEmbeddedCoverPath(request, pathProcessor);
        }

        EnsureLyricsForTagging(request);

        await DownloadEngineArtworkHelper.TagAudioWithResolvedCoverAsync(
            new DownloadEngineArtworkHelper.AudioTagWithCoverRequest(
                request.OutputPath,
                request.Context.Track,
                request.Settings,
                request.Engine,
                audioTagger,
                request.Logger),
            cancellationToken);

        UpdateAudioPayloadFiles(request.Payload, request.Context.PathResult, request.OutputPath);
        return request.OutputPath;
    }

    public static void SynchronizeTrackWithPayloadForTagging(Track track, EngineQueueItemBase payload)
    {
        if (track == null || payload == null)
        {
            return;
        }

        SynchronizeTrackCoreFields(track, payload);
        SynchronizeTrackSequenceFields(track, payload);
        SynchronizeTrackAlbumAndArtistFields(track, payload);
        PopulateTrackUrls(track, payload);

        if (string.IsNullOrWhiteSpace(track.DownloadURL))
        {
            track.DownloadURL = !string.IsNullOrWhiteSpace(payload.Url)
                ? payload.Url!
                : payload.SourceUrl ?? string.Empty;
        }

        var normalizedSource = DownloadTagSourceHelper.NormalizeResolvedDownloadTagSource(track.Source);
        if (!string.IsNullOrWhiteSpace(normalizedSource))
        {
            ApplyResolvedTagSourceIdentity(track, payload, normalizedSource);
        }
    }

    private static void SynchronizeTrackCoreFields(Track track, EngineQueueItemBase payload)
    {
        if (string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(payload.Title))
        {
            track.Title = payload.Title.Trim();
        }

        if (string.IsNullOrWhiteSpace(track.ISRC) && !string.IsNullOrWhiteSpace(payload.Isrc))
        {
            track.ISRC = payload.Isrc.Trim();
        }

        if (track.Duration <= 0 && payload.DurationSeconds > 0)
        {
            track.Duration = payload.DurationSeconds;
        }
    }

    private static void SynchronizeTrackSequenceFields(Track track, EngineQueueItemBase payload)
    {
        if (track.TrackNumber <= 0)
        {
            if (payload.TrackNumber > 0)
            {
                track.TrackNumber = payload.TrackNumber;
            }
            else if (payload.SpotifyTrackNumber > 0)
            {
                track.TrackNumber = payload.SpotifyTrackNumber;
            }
            else if (payload.Position > 0)
            {
                track.TrackNumber = payload.Position;
            }
        }

        if (track.DiscNumber <= 0)
        {
            if (payload.DiscNumber > 0)
            {
                track.DiscNumber = payload.DiscNumber;
            }
            else if (payload.SpotifyDiscNumber > 0)
            {
                track.DiscNumber = payload.SpotifyDiscNumber;
            }
        }
    }

    private static void SynchronizeTrackAlbumAndArtistFields(Track track, EngineQueueItemBase payload)
    {
        if (track.Album != null)
        {
            if (string.IsNullOrWhiteSpace(track.Album.Title) && !string.IsNullOrWhiteSpace(payload.Album))
            {
                track.Album.Title = payload.Album.Trim();
            }

            if (track.Album.TrackTotal <= 0 && payload.TrackTotal > 0)
            {
                track.Album.TrackTotal = payload.TrackTotal;
            }

            if (track.Album.DiscTotal <= 0 && payload.DiscTotal > 0)
            {
                track.Album.DiscTotal = payload.DiscTotal;
            }
        }

        if ((track.Artists?.Count ?? 0) == 0 && !string.IsNullOrWhiteSpace(payload.Artist))
        {
            var artistName = payload.Artist.Trim();
            track.Artists = new List<string> { artistName };
            track.Artist["Main"] = new List<string> { artistName };
            track.MainArtist ??= new Artist("0", artistName);
        }
    }

    private static void ApplyPrefetchedEmbeddedCoverPath(
        PostDownloadSettingsRequest request,
        EnhancedPathTemplateProcessor pathProcessor)
    {
        if (request.Context.Track.Album == null || request.Settings.Tags?.Cover != true)
        {
            return;
        }

        var coverPath = ResolvePrefetchedAlbumCoverPath(request, pathProcessor);
        if (string.IsNullOrWhiteSpace(coverPath))
        {
            return;
        }

        request.Context.Track.Album.EmbeddedCoverPath = coverPath;
    }

    private static string? ResolvePrefetchedAlbumCoverPath(
        PostDownloadSettingsRequest request,
        EnhancedPathTemplateProcessor pathProcessor)
    {
        var coverPath = DownloadPathResolver.ResolveIoPath(request.Context.PathResult.CoverPath ?? request.Context.PathResult.FilePath);
        if (string.IsNullOrWhiteSpace(coverPath))
        {
            return null;
        }

        var coverName = pathProcessor.GenerateAlbumName(
            request.Settings.CoverImageTemplate,
            request.Context.Track.Album,
            request.Settings,
            request.Context.Track.Playlist);

        foreach (var format in AppleQueueHelpers.GetArtworkOutputFormats(request.Settings))
        {
            var candidate = Path.Join(coverPath, $"{coverName}.{format}");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void EnsureLyricsForTagging(PostDownloadSettingsRequest request)
    {
        if (!LyricsSettingsPolicy.CanFetchLyrics(request.Settings))
        {
            return;
        }

        var tagSettings = request.Settings.Tags;
        if (tagSettings == null || (!tagSettings.Lyrics && !tagSettings.SyncedLyrics))
        {
            return;
        }

        var track = request.Context.Track;
        track.Lyrics ??= new Lyrics(track.LyricsId ?? "0");

        try
        {
            HydrateLyricsFromSidecars(track, request.OutputPath, tagSettings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (request.Logger.IsEnabled(LogLevel.Debug))
            {
                request.Logger.LogDebug(ex, "{Engine} failed lyrics hydration for {Path}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(request.Engine), DeezSpoTag.Core.Security.LogSanitizer.OneLine(request.OutputPath));
            }
        }
    }

    private static void HydrateLyricsFromSidecars(
        Track track,
        string outputPath,
        TagSettings tagSettings)
    {
        var ioOutputPath = DownloadPathResolver.ResolveIoPath(outputPath);
        if (string.IsNullOrWhiteSpace(ioOutputPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(ioOutputPath);
        var baseName = Path.GetFileNameWithoutExtension(ioOutputPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName))
        {
            return;
        }

        var lrcPath = Path.Join(directory, $"{baseName}.lrc");
        var ttmlPath = Path.Join(directory, $"{baseName}.ttml");
        var txtPath = Path.Join(directory, $"{baseName}.txt");

        if (tagSettings.SyncedLyrics && !HasSyncedLyrics(track))
        {
            var syncedLines = ResolveSyncedLinesFromSidecars(lrcPath, ttmlPath);
            if (syncedLines.Count > 0)
            {
                track.Lyrics!.Sync = string.Join(Environment.NewLine, syncedLines);
                track.Lyrics.SyncID3 = syncedLines
                    .Select(ToSyncLyricOrNull)
                    .Where(line => line != null)
                    .Select(line => line!)
                    .ToList();
            }
        }

        if (tagSettings.Lyrics && string.IsNullOrWhiteSpace(track.Lyrics!.Unsync))
        {
            var unsyncedText = ResolveUnsyncedTextFromSidecars(txtPath, lrcPath, ttmlPath);
            if (!string.IsNullOrWhiteSpace(unsyncedText))
            {
                track.Lyrics.Unsync = unsyncedText;
            }
            else if (HasSyncedLyrics(track))
            {
                track.Lyrics.Unsync = ConvertSyncedLyricsToUnsynced(track.Lyrics.SyncID3);
            }
        }
    }

    private static List<string> ResolveSyncedLinesFromSidecars(string lrcPath, string ttmlPath)
    {
        if (File.Exists(lrcPath))
        {
            return NormalizeLrcLines(File.ReadAllLines(lrcPath));
        }

        if (!File.Exists(ttmlPath))
        {
            return new List<string>();
        }

        return new List<string>();
    }

    private static string ResolveUnsyncedTextFromSidecars(string txtPath, string lrcPath, string ttmlPath)
    {
        if (File.Exists(txtPath))
        {
            return (File.ReadAllText(txtPath) ?? string.Empty).Trim();
        }

        var syncedLines = ResolveSyncedLinesFromSidecars(lrcPath, ttmlPath);
        if (syncedLines.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            syncedLines
                .Select(static line => TryExtractLrcText(line, out var text) ? text : string.Empty)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static List<string> NormalizeLrcLines(IEnumerable<string> lines)
    {
        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .Where(line => LrcTimestampRegex.IsMatch(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static SyncLyric? ToSyncLyricOrNull(string line)
    {
        if (!TryExtractLrcText(line, out var text) || !TryParseLrcTimestamp(line, out var timestampMs))
        {
            return null;
        }

        return new SyncLyric
        {
            Timestamp = timestampMs,
            Text = text
        };
    }

    private static bool TryExtractLrcText(string line, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = LrcTimestampRegex.Match(line.Trim());
        if (!match.Success)
        {
            return false;
        }

        text = line[(match.Index + match.Length)..].Trim();
        return true;
    }

    private static bool TryParseLrcTimestamp(string line, out int milliseconds)
    {
        milliseconds = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = LrcTimestampRegex.Match(line.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["m"].Value, out var minutes))
        {
            return false;
        }

        if (!int.TryParse(match.Groups["s"].Value, out var seconds))
        {
            return false;
        }

        var fractionRaw = match.Groups["f"].Success ? match.Groups["f"].Value : "0";
        if (!int.TryParse(fractionRaw, out var fraction))
        {
            return false;
        }

        var ms = fractionRaw.Length switch
        {
            1 => fraction * 100,
            2 => fraction * 10,
            _ => fraction
        };
        milliseconds = Math.Max(0, (minutes * 60 * 1000) + (seconds * 1000) + ms);
        return true;
    }

    private static string ConvertSyncedLyricsToUnsynced(IEnumerable<SyncLyric>? syncLyrics)
    {
        if (syncLyrics == null)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            syncLyrics
                .Select(static line => line.Text?.Trim() ?? string.Empty)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static bool HasSyncedLyrics(Track track)
    {
        return !string.IsNullOrWhiteSpace(track.Lyrics?.Sync)
            || (track.Lyrics?.SyncID3?.Count ?? 0) > 0;
    }

    public static async Task QueueParallelPostDownloadPrefetchAsync(
        PrefetchRequest request,
        CancellationToken cancellationToken = default)
    {
        var requirements = BuildPrefetchRequirements(request);
        if (!requirements.ShouldQueueWork)
        {
            ClearPrefetchState(request.QueueUuid);
            return;
        }

        var prefetchPaths = BuildPrefetchPathContext(request.QueueUuid, request.Context, request.ExpectedOutputPath);
        if (PrefetchGates.ContainsKey(prefetchPaths.QueueUuid))
        {
            return;
        }

        var gateState = new PrefetchGateState();
        if (!PrefetchGates.TryAdd(prefetchPaths.QueueUuid, gateState))
        {
            gateState.Cancellation.Dispose();
            return;
        }

        var initialArtworkStatus = requirements.ShouldFetchArtwork ? FetchingStatus : SkippedStatus;
        var initialLyricsStatus = requirements.ShouldFetchLyrics ? FetchingStatus : SkippedStatus;
        try
        {
            await request.QueueRepository.UpdatePrefetchProgressAsync(
                prefetchPaths.QueueUuid,
                initialArtworkStatus,
                initialLyricsStatus,
                string.Empty,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Logger.LogDebug(
                ex,
                "{Engine} failed to persist initial prefetch progress for {QueueUuid}",
                request.Engine,
                prefetchPaths.QueueUuid);
        }
        QueuePrefetchStatusHelper.Send(
            request.Listener,
            prefetchPaths.QueueUuid,
            initialArtworkStatus,
            initialLyricsStatus);

        var execution = new PrefetchExecutionContext(request, prefetchPaths, requirements, gateState);
        try
        {
            await request.TaskScheduler.EnqueueAsync(
                prefetchPaths.QueueUuid,
                request.Engine,
                async (provider, token) =>
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, gateState.Cancellation.Token);
                    await RunPrefetchWorkAsync(provider, execution, linkedCts.Token);
                },
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Logger.LogWarning(
                ex,
                "{Engine} prefetch scheduling failed for {QueueUuid}",
                request.Engine,
                prefetchPaths.QueueUuid);
            try
            {
                await request.QueueRepository.UpdatePrefetchProgressAsync(
                    prefetchPaths.QueueUuid,
                    requirements.ShouldFetchArtwork ? FailedStatus : SkippedStatus,
                    requirements.ShouldFetchLyrics ? FailedStatus : SkippedStatus,
                    string.Empty,
                    CancellationToken.None);
            }
            catch (Exception persistEx) when (persistEx is not OperationCanceledException)
            {
                request.Logger.LogDebug(
                    persistEx,
                    "{Engine} failed to persist failed prefetch scheduling state for {QueueUuid}",
                    request.Engine,
                    prefetchPaths.QueueUuid);
            }
            QueuePrefetchStatusHelper.Send(
                request.Listener,
                prefetchPaths.QueueUuid,
                requirements.ShouldFetchArtwork ? FailedStatus : SkippedStatus,
                requirements.ShouldFetchLyrics ? FailedStatus : SkippedStatus);
            gateState.Completion.TrySetResult(new PrefetchCompletionResult(
                requirements.ShouldFetchArtwork,
                false,
                "Artwork prefetch could not be scheduled."));
            return;
        }
    }

    private static async Task RunPrefetchWorkAsync(
        IServiceProvider provider,
        PrefetchExecutionContext execution,
        CancellationToken token)
    {
        var completionResult = BuildDefaultPrefetchCompletionResult(execution.Requirements);
        var runState = new PrefetchRunState(execution.Requirements);

        try
        {
            var runtime = ResolvePrefetchRuntimeServices(provider);
            await PersistPrefetchProgressAsync(execution, runState, token);
            var settings = execution.Request.Settings;
            var appleArtworkSize = AppleQueueHelpers.GetAppleArtworkSize(settings);
            var preferMaxQualityCover = settings.EmbedMaxQualityCover;
            var appleIdentity = await ResolveAppleArtworkIdentityAsync(execution, runtime, token);
            if (!string.IsNullOrWhiteSpace(appleIdentity?.AppleId)
                && !string.Equals(execution.Request.Payload.AppleId, appleIdentity.AppleId, StringComparison.Ordinal))
            {
                execution.Request.Payload.AppleId = appleIdentity.AppleId;
                await execution.Request.QueueRepository.UpdatePayloadAsync(
                    execution.Paths.QueueUuid,
                    System.Text.Json.JsonSerializer.Serialize(execution.Request.Payload),
                    token);
            }
            var coverUrls = await DownloadEngineArtworkHelper.ResolveStandardAudioCoverUrlsAsync(
                new DownloadEngineArtworkHelper.StandardAudioCoverResolveRequest(
                    settings,
                    runtime.AppleCatalog,
                    runtime.HttpClientFactory,
                    runtime.SpotifyArtworkResolver,
                    runtime.SpotifyIdResolver,
                    runtime.DeezerApiClient,
                    appleIdentity?.AppleId ?? execution.Request.AppleCoverLookupIdOverride ?? execution.Request.Payload.AppleId,
                    execution.Request.Payload.Title,
                    execution.Request.Payload.Artist,
                    execution.Request.Payload.Album,
                    execution.Request.Payload.CollectionType,
                    execution.Request.Payload.DeezerId,
                    execution.Request.Payload.Cover,
                    execution.Request.Payload.Isrc,
                    execution.Request.Logger)
                    {
                        TrackIdentityResolver = runtime.TrackIdentityResolver
                    },
                token);
            var artworkTask = BuildArtworkPrefetchTask(
                execution,
                runtime,
                coverUrls,
                appleIdentity,
                appleArtworkSize,
                preferMaxQualityCover,
                runState,
                token);
            var lyricsTask = BuildLyricsPrefetchTask(execution, runState, token);
            await Task.WhenAll(artworkTask, lyricsTask);
            completionResult = BuildPrefetchCompletionResult(execution.Requirements, runState);
            await PersistPrefetchPayloadStateAsync(provider, execution, runState, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            completionResult = new PrefetchCompletionResult(
                execution.Requirements.ShouldRequireEmbeddedArtwork,
                false,
                "Artwork prefetch canceled.");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            completionResult = new PrefetchCompletionResult(
                execution.Requirements.ShouldRequireEmbeddedArtwork,
                false,
                ex.Message);
            execution.Request.Logger.LogWarning(
                ex,
                "{Engine} prefetch worker failed for {QueueUuid}",
                execution.Request.Engine,
                execution.Paths.QueueUuid);
        }
        finally
        {
            execution.GateState.Completion.TrySetResult(completionResult);
        }
    }

    private static PrefetchCompletionResult BuildDefaultPrefetchCompletionResult(PrefetchRequirements requirements)
    {
        return new PrefetchCompletionResult(
            requirements.ShouldRequireEmbeddedArtwork,
            !requirements.ShouldFetchArtwork,
            requirements.ShouldFetchArtwork ? "Artwork prefetch did not complete." : null);
    }

    private static PrefetchCompletionResult BuildPrefetchCompletionResult(
        PrefetchRequirements requirements,
        PrefetchRunState runState)
    {
        return new PrefetchCompletionResult(
            requirements.ShouldRequireEmbeddedArtwork,
            runState.ArtworkResult.Success,
            runState.ArtworkResult.FailureReason);
    }

    private static Task BuildArtworkPrefetchTask(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        IReadOnlyList<string> coverUrls,
        AppleArtworkIdentity? appleIdentity,
        int appleArtworkSize,
        bool preferMaxQualityCover,
        PrefetchRunState runState,
        CancellationToken token)
    {
        if (!execution.Requirements.ShouldFetchArtwork)
        {
            return Task.CompletedTask;
        }

        return RunArtworkPrefetchTaskAsync(
            execution,
            runtime,
            coverUrls,
            appleIdentity,
            appleArtworkSize,
            preferMaxQualityCover,
            runState,
            token);
    }

    private static Task BuildLyricsPrefetchTask(
        PrefetchExecutionContext execution,
        PrefetchRunState runState,
        CancellationToken token)
    {
        if (!execution.Requirements.ShouldFetchLyrics)
        {
            return Task.CompletedTask;
        }

        return RunLyricsPrefetchTaskAsync(execution, runState, token);
    }

    private static async Task RunArtworkPrefetchTaskAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        IReadOnlyList<string> coverUrls,
        AppleArtworkIdentity? appleIdentity,
        int appleArtworkSize,
        bool preferMaxQualityCover,
        PrefetchRunState runState,
        CancellationToken token)
    {
        try
        {
            runState.ArtworkResult = await RunArtworkPrefetchAsync(
                execution,
                runtime,
                coverUrls,
                appleIdentity,
                appleArtworkSize,
                preferMaxQualityCover,
                token);
            runState.ArtworkStatus = runState.ArtworkResult.Success ? CompletedStatus : FailedStatus;
            if (!runState.ArtworkResult.Success && !string.IsNullOrWhiteSpace(runState.ArtworkResult.FailureReason))
            {
                execution.Request.Logger.LogWarning(
                    "{Engine} artwork prefetch incomplete for {Path}: {Reason}",
                    execution.Request.Engine,
                    execution.Request.ExpectedOutputPath,
                    runState.ArtworkResult.FailureReason);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            runState.ArtworkResult = new PrefetchArtworkResult(false, ex.Message);
            runState.ArtworkStatus = FailedStatus;
            execution.Request.Logger.LogWarning(
                ex,
                "{Engine} artwork prefetch failed for {Path}",
                execution.Request.Engine,
                execution.Request.ExpectedOutputPath);
        }
        finally
        {
            await PersistPrefetchProgressAsync(execution, runState, CancellationToken.None);
            QueuePrefetchStatusHelper.Send(
                execution.Request.Listener,
                execution.Paths.QueueUuid,
                runState.ArtworkStatus,
                runState.LyricsStatus);
        }
    }

    private static async Task PersistPrefetchProgressAsync(
        PrefetchExecutionContext execution,
        PrefetchRunState runState,
        CancellationToken cancellationToken)
    {
        try
        {
            await execution.Request.QueueRepository.UpdatePrefetchProgressAsync(
                execution.Paths.QueueUuid,
                runState.ArtworkStatus,
                runState.LyricsStatus,
                runState.LyricsType,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (execution.Request.Logger.IsEnabled(LogLevel.Debug))
            {
                execution.Request.Logger.LogDebug(
                    ex,
                    "{Engine} failed to persist prefetch progress for {QueueUuid}",
                    execution.Request.Engine,
                    execution.Paths.QueueUuid);
            }
        }
    }

    private static async Task RunLyricsPrefetchTaskAsync(
        PrefetchExecutionContext execution,
        PrefetchRunState runState,
        CancellationToken token)
    {
        try
        {
            runState.LyricsType = await RunLyricsPrefetchAsync(execution, runState, token);
            runState.LyricsStatus = string.IsNullOrWhiteSpace(runState.LyricsType) ? NoLyricsStatus : CompletedStatus;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            runState.LyricsStatus = FailedStatus;
            execution.Request.Logger.LogWarning(
                ex,
                "{Engine} lyrics download failed for {Path}",
                execution.Request.Engine,
                execution.Request.ExpectedOutputPath);
        }
        finally
        {
            QueuePrefetchStatusHelper.Send(
                execution.Request.Listener,
                execution.Paths.QueueUuid,
                runState.ArtworkStatus,
                runState.LyricsStatus,
                runState.LyricsType);
        }
    }

    private static async Task PersistPrefetchPayloadStateAsync(
        IServiceProvider provider,
        PrefetchExecutionContext execution,
        PrefetchRunState runState,
        CancellationToken token)
    {
        try
        {
            var queueRepository = provider.GetService<DownloadQueueRepository>();
            if (queueRepository == null)
            {
                return;
            }

            var outputPath = ResolveCurrentOutputPath(execution);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            var result = QueuePayloadFileHelper.BuildAudioFiles(execution.Request.Context.PathResult, outputPath);
            execution.Request.Payload.Files = result.Files;
            execution.Request.Payload.LyricsStatus = result.LyricsStatus;
            var filesJson = System.Text.Json.JsonSerializer.Serialize(result.Files);
            await queueRepository.UpdatePrefetchStateAsync(
                execution.Paths.QueueUuid,
                filesJson,
                result.LyricsStatus,
                runState.ArtworkStatus,
                runState.ArtworkResult.FailureReason,
                token);

            execution.Request.Listener.Send(UpdateQueueEvent, new
            {
                uuid = execution.Paths.QueueUuid,
                files = result.Files,
                lyricsStatus = result.LyricsStatus,
                lyrics_status = result.LyricsStatus
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (execution.Request.Logger.IsEnabled(LogLevel.Debug))
            {
                execution.Request.Logger.LogDebug(
                    ex,
                    "{Engine} failed to persist prefetch payload state for {QueueUuid}",
                    execution.Request.Engine,
                    execution.Paths.QueueUuid);
            }
        }
    }

    private static string ResolveCurrentOutputPath(PrefetchExecutionContext execution)
    {
        if (!string.IsNullOrWhiteSpace(execution.Request.Payload.FilePath))
        {
            return DownloadPathResolver.NormalizeDisplayPath(execution.Request.Payload.FilePath);
        }

        var filePath = execution.Request.Payload.Files
            .Select(file => file.TryGetValue("path", out var value) ? value?.ToString() : null)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return DownloadPathResolver.NormalizeDisplayPath(filePath);
        }

        return DownloadPathResolver.NormalizeDisplayPath(execution.Request.ExpectedOutputPath);
    }

    private static PrefetchRuntimeServices ResolvePrefetchRuntimeServices(IServiceProvider provider)
    {
        var imageDownloader = provider.GetRequiredService<ImageDownloader>();
        var pathProcessor = provider.GetRequiredService<EnhancedPathTemplateProcessor>();
        var spotifyArtworkResolver = provider.GetService<ISpotifyArtworkResolver>();
        var lastFmArtistImageResolver = provider.GetService<ILastFmArtistImageResolver>();
        var spotifyIdResolver = provider.GetService<ISpotifyIdResolver>();
        var trackIdentityResolver = provider.GetService<ITrackIdentityResolver>();
        var httpClientFactory = provider.GetService<IHttpClientFactory>();
        var appleCatalog = provider.GetService<AppleMusicCatalogService>();
        var deezerClient = provider.GetService<DeezerClient>();
        return new PrefetchRuntimeServices(
            imageDownloader,
            pathProcessor,
            spotifyArtworkResolver,
            lastFmArtistImageResolver,
            spotifyIdResolver,
            trackIdentityResolver,
            httpClientFactory,
            appleCatalog,
            deezerClient);
    }

    private static async Task<PrefetchArtworkResult> RunArtworkPrefetchAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        IReadOnlyList<string> coverUrls,
        AppleArtworkIdentity? appleIdentity,
        int appleArtworkSize,
        bool preferMaxQualityCover,
        CancellationToken token)
    {
        if (execution.Requirements.ShouldFetchPrimaryArtwork)
        {
            var primaryResult = await TrySavePrimaryArtworkAsync(
                execution,
                runtime,
                coverUrls,
                appleArtworkSize,
                preferMaxQualityCover,
                token);
            if (!primaryResult.Success)
            {
                return primaryResult;
            }
        }

        if (execution.Requirements.ShouldFetchAnimatedArtwork && runtime.AppleCatalog != null && runtime.HttpClientFactory != null)
        {
            await LogMissingAnimatedArtworkAsync(execution, runtime, appleIdentity, token);
        }

        if (execution.Requirements.ShouldFetchArtistArtwork)
        {
            var artistResult = await TrySaveArtistArtworkAsync(execution, runtime, appleArtworkSize, preferMaxQualityCover, token);
            if (!artistResult.Success)
            {
                return artistResult;
            }
        }

        return new PrefetchArtworkResult(true);
    }

    private static async Task<AppleArtworkIdentity?> ResolveAppleArtworkIdentityAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        CancellationToken token)
    {
        if (runtime.TrackIdentityResolver == null)
        {
            return null;
        }

        var settings = execution.Request.Settings;
        var payload = execution.Request.Payload;
        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
            ? "us"
            : settings.AppleMusic.Storefront;
        var identity = await runtime.TrackIdentityResolver.ResolveAsync(
            new TrackIdentityResolutionRequest(
                SourcePlatform: payload.SourceService,
                SourceUrl: FirstNonEmpty(payload.SourceUrl, payload.Url, payload.ResolvedSourceUrl),
                Title: payload.Title,
                Artist: payload.Artist,
                Album: payload.Album,
                Isrc: payload.Isrc,
                DurationMs: payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : null,
                SpotifyId: payload.SpotifyId,
                DeezerId: payload.DeezerId,
                AppleId: execution.Request.AnimatedArtworkAppleIdOverride
                    ?? execution.Request.AppleCoverLookupIdOverride
                    ?? payload.AppleId,
                QobuzId: payload.QobuzId,
                TidalId: payload.TidalId,
                AmazonId: payload.AmazonId,
                TargetPlatforms: new[] { AppleSource },
                Storefront: storefront,
                Language: "en-US",
                MediaUserToken: settings.AppleMusic?.MediaUserToken),
            token);

        if (string.IsNullOrWhiteSpace(identity.AppleId))
        {
            return null;
        }

        return new AppleArtworkIdentity(
            identity.AppleId,
            identity.AppleAlbumId,
            identity.AppleAlbumName,
            identity.AppleArtistName,
            identity.AppleIsrc,
            identity.AppleDurationMs);
    }

    private static async Task<PrefetchArtworkResult> TrySavePrimaryArtworkAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        IReadOnlyList<string> coverUrls,
        int appleArtworkSize,
        bool preferMaxQualityCover,
        CancellationToken token)
    {
        if (coverUrls.Count == 0)
        {
            return new PrefetchArtworkResult(false, "Album artwork URL could not be resolved.");
        }

        foreach (var coverUrl in coverUrls)
        {
            var isAppleCover = coverUrl.Contains(MzStaticHost, StringComparison.OrdinalIgnoreCase);
            var primarySaved = await SavePrimaryArtworkAsync(
                execution,
                runtime,
                coverUrl,
                isAppleCover,
                appleArtworkSize,
                preferMaxQualityCover,
                token);
            if (primarySaved)
            {
                return new PrefetchArtworkResult(true);
            }
        }

        return new PrefetchArtworkResult(false, "Album artwork download failed.");
    }

    private static async Task LogMissingAnimatedArtworkAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        AppleArtworkIdentity? appleIdentity,
        CancellationToken token)
    {
        var animatedSaved = await SaveAnimatedArtworkAsync(execution, runtime, appleIdentity, token);
        if (!animatedSaved && execution.Request.Logger.IsEnabled(LogLevel.Debug))
        {
            execution.Request.Logger.LogDebug(
                "{Engine} animated artwork not available for {QueueUuid}",
                execution.Request.Engine,
                execution.Paths.QueueUuid);
        }
    }

    private static async Task<PrefetchArtworkResult> TrySaveArtistArtworkAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        int appleArtworkSize,
        bool preferMaxQualityCover,
        CancellationToken token)
    {
        var artistSaved = await SaveArtistArtworkAsync(execution, runtime, appleArtworkSize, preferMaxQualityCover, token);
        return artistSaved
            ? new PrefetchArtworkResult(true)
            : new PrefetchArtworkResult(false, "Artist artwork download failed.");
    }

    private static async Task<bool> SavePrimaryArtworkAsync(
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
        var anySaved = false;
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
                var downloaded = await AppleQueueHelpers.DownloadAppleArtworkAsync(
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
                anySaved |= !string.IsNullOrWhiteSpace(downloaded) && File.Exists(downloaded);
            }
            return anySaved;
        }

        var formats = (settings.LocalArtworkFormat ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var format in formats)
        {
            var ext = format.Equals("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
            var targetPath = Path.Join(execution.Paths.CoverPath, $"{coverName}.{ext}");
            var downloaded = await runtime.ImageDownloader.DownloadImageAsync(
                coverUrl,
                targetPath,
                settings.OverwriteFile,
                preferMaxQualityCover,
                token);
            anySaved |= !string.IsNullOrWhiteSpace(downloaded) && File.Exists(downloaded);
        }

        return anySaved;
    }

    private static async Task<bool> SaveAnimatedArtworkAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        AppleArtworkIdentity? appleIdentity,
        CancellationToken token)
    {
        var resolvedAppleId = appleIdentity?.AppleId;
        if (string.IsNullOrWhiteSpace(resolvedAppleId))
        {
            return false;
        }

        var settings = execution.Request.Settings;
        var coverName = runtime.PathProcessor.GenerateAlbumName(
            settings.CoverImageTemplate,
            execution.Request.Context.Track.Album,
            settings,
            execution.Request.Context.Track.Playlist);
        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
        var savedAnimated = await AppleQueueHelpers.SaveAnimatedArtworkAsync(
            runtime.AppleCatalog!,
            runtime.HttpClientFactory!,
            new AppleQueueHelpers.AnimatedArtworkSaveRequest
            {
                AppleId = resolvedAppleId,
                Artist = appleIdentity?.ArtistName ?? execution.Request.Payload.Artist,
                Album = appleIdentity?.AlbumName ?? execution.Request.Payload.Album,
                BaseFileName = coverName,
                Storefront = storefront,
                MaxResolution = settings.Video.AppleMusicVideoMaxResolution,
                OutputDir = execution.Paths.CoverPath,
                Logger = execution.Request.Logger,
                CollectionType = string.IsNullOrWhiteSpace(appleIdentity?.AlbumId) ? null : AlbumType,
                CollectionId = appleIdentity?.AlbumId,
                OutputFormats = AppleQueueHelpers.ResolveAnimatedArtworkFormats(settings)
            },
            token);
        if (savedAnimated)
        {
            execution.Request.ActivityLog.Info($"Animated artwork saved: {execution.Paths.CoverPath}");
        }

        return savedAnimated;
    }

    private static async Task<bool> SaveArtistArtworkAsync(
        PrefetchExecutionContext execution,
        PrefetchRuntimeServices runtime,
        int appleArtworkSize,
        bool preferMaxQualityCover,
        CancellationToken token)
    {
        var settings = execution.Request.Settings;
        if (!settings.SaveArtworkArtist)
        {
            return true;
        }

        var artistImageUrl = await DownloadEngineArtworkHelper.ResolveArtistImageUrlAsync(
            new DownloadEngineArtworkHelper.ArtistImageResolveRequest(
                runtime.AppleCatalog,
                runtime.HttpClientFactory,
                settings,
                runtime.DeezerApiClient,
                runtime.SpotifyArtworkResolver,
                runtime.LastFmArtistImageResolver,
                execution.Request.Payload.AppleId,
                execution.Request.Payload.DeezerId,
                execution.Request.Payload.SpotifyId,
                execution.Request.Payload.Artist,
                NullLogger.Instance),
            token);
        if (string.IsNullOrWhiteSpace(artistImageUrl))
        {
            return false;
        }

        return await DownloadEngineArtworkHelper.SaveArtistArtworkAsync(
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
        PrefetchRunState runState,
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
            runState.LyricsType = lyricsType;
            await PersistPrefetchProgressAsync(execution, runState, token);
            QueuePrefetchStatusHelper.Send(
                execution.Request.Listener,
                execution.Paths.QueueUuid,
                runState.ArtworkStatus,
                FetchingStatus,
                lyricsType);
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
        var expectedOutputName = Path.GetFileName(expectedOutputPath);
        var expectedBaseName = ResolveFilenameStem(expectedOutputName);
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

    public static async Task<string?> EnsureArtworkPrefetchCompletedAsync(
        string queueUuid,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid)
            || !PrefetchGates.TryGetValue(queueUuid, out var gateState))
        {
            return null;
        }

        PrefetchCompletionResult result;
        try
        {
            result = await gateState.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            TryRemovePrefetchState(queueUuid, gateState);
        }

        if (!result.ShouldValidateArtwork || result.ArtworkReady)
        {
            return null;
        }

        if (HasEmbeddedArtwork(outputPath))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(result.ArtworkFailureReason)
            ? "Artwork prefetch did not complete successfully."
            : result.ArtworkFailureReason;
    }

    private static bool HasEmbeddedArtwork(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)
            || !File.Exists(outputPath))
        {
            return false;
        }

        try
        {
            using var file = TagLib.File.Create(outputPath);
            return file.Tag.Pictures?.Any(pic => pic?.Data != null && pic.Data.Count > 0) == true;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    public static void ClearPrefetchState(string queueUuid)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        if (PrefetchGates.TryRemove(queueUuid, out var gateState))
        {
            gateState.Cancellation.Cancel();
            gateState.Cancellation.Dispose();
        }
    }

    public static async Task CancelPrefetchAndWaitAsync(
        string queueUuid,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid)
            || !PrefetchGates.TryGetValue(queueUuid, out var gateState))
        {
            return;
        }

        await gateState.Cancellation.CancelAsync();
        try
        {
            await gateState.Completion.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Terminal queue cleanup still runs after this. The prefetch worker is canceled
            // above, and cleanup removes any sidecars already written before cancellation.
        }
        finally
        {
            TryRemovePrefetchState(queueUuid, gateState);
        }
    }

    public static async Task UpdateWatchlistTrackStatusAsync(
        EngineQueueItemBase payload,
        string status,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken,
        string? queueUuid = null)
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

        if (string.Equals(status, "unavailable", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(payload.WatchlistUnavailableSettingsFingerprint))
        {
            await libraryRepository.MarkPlaylistWatchTrackUnavailableAsync(
                payload.WatchlistSource,
                payload.WatchlistPlaylistId,
                payload.WatchlistTrackId,
                payload.Isrc,
                string.IsNullOrWhiteSpace(payload.ErrorMessage)
                    ? "Track unavailable from enabled sources."
                    : payload.ErrorMessage,
                payload.WatchlistUnavailableSettingsFingerprint,
                DateTimeOffset.UtcNow.AddDays(WatchlistUnavailableRecheckDays),
                cancellationToken);
        }
        else
        {
            var watchlistStatus = string.Equals(status, CompletedStatus, StringComparison.OrdinalIgnoreCase)
                ? DownloadedStatus
                : status;

            await libraryRepository.UpdatePlaylistWatchTrackStatusAsync(
                payload.WatchlistSource,
                payload.WatchlistPlaylistId,
                payload.WatchlistTrackId,
                watchlistStatus,
                cancellationToken);
        }

        var resolvedQueueUuid = ResolveQueueUuid(queueUuid, payload);
        if (string.Equals(status, CompletedStatus, StringComparison.OrdinalIgnoreCase))
        {
            await MarkSharedWatchDownloadClaimsDownloadedAsync(
                libraryRepository,
                resolvedQueueUuid,
                payload,
                cancellationToken);
            return;
        }

        if (IsFailedOrCanceledWatchStatus(status))
        {
            await UpdateSharedWatchDownloadClaimsStatusAsync(
                libraryRepository,
                resolvedQueueUuid,
                payload,
                status,
                cancellationToken);
            await libraryRepository.UpdatePlaylistWatchDownloadClaimStatusAsync(
                resolvedQueueUuid,
                payload.WatchlistSource,
                payload.WatchlistPlaylistId,
                payload.WatchlistTrackId,
                status,
                cancellationToken);
        }
        else if (string.Equals(status, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateSharedWatchDownloadClaimsStatusAsync(
                libraryRepository,
                resolvedQueueUuid,
                payload,
                status,
                cancellationToken);
            await libraryRepository.UpdatePlaylistWatchDownloadClaimStatusAsync(
                resolvedQueueUuid,
                payload.WatchlistSource,
                payload.WatchlistPlaylistId,
                payload.WatchlistTrackId,
                status,
                cancellationToken);
        }
    }

    private static async Task MarkSharedWatchDownloadClaimsDownloadedAsync(
        LibraryRepository libraryRepository,
        string queueUuid,
        EngineQueueItemBase payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        var claims = await libraryRepository.GetPlaylistWatchDownloadClaimsAsync(
            queueUuid,
            status: "pending",
            cancellationToken);
        foreach (var claim in claims)
        {
            if (IsOriginalWatchClaim(payload, claim))
            {
                continue;
            }

            await libraryRepository.UpdatePlaylistWatchTrackStatusAsync(
                claim.Source,
                claim.SourceId,
                claim.TrackSourceId,
                DownloadedStatus,
                cancellationToken);
        }
    }

    private static async Task UpdateSharedWatchDownloadClaimsStatusAsync(
        LibraryRepository libraryRepository,
        string queueUuid,
        EngineQueueItemBase payload,
        string status,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        var claims = await libraryRepository.GetPlaylistWatchDownloadClaimsAsync(
            queueUuid,
            status: "pending",
            cancellationToken);
        foreach (var claim in claims)
        {
            if (IsOriginalWatchClaim(payload, claim))
            {
                continue;
            }

            if (string.Equals(status, "unavailable", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(payload.WatchlistUnavailableSettingsFingerprint))
            {
                await libraryRepository.MarkPlaylistWatchTrackUnavailableAsync(
                    claim.Source,
                    claim.SourceId,
                    claim.TrackSourceId,
                    payload.Isrc,
                    string.IsNullOrWhiteSpace(payload.ErrorMessage)
                        ? "Track unavailable from enabled sources."
                        : payload.ErrorMessage,
                    payload.WatchlistUnavailableSettingsFingerprint,
                    DateTimeOffset.UtcNow.AddDays(WatchlistUnavailableRecheckDays),
                    cancellationToken);
            }
            else
            {
                await libraryRepository.UpdatePlaylistWatchTrackStatusAsync(
                    claim.Source,
                    claim.SourceId,
                    claim.TrackSourceId,
                    status,
                    cancellationToken);
            }
        }
    }

    private static bool IsFailedOrCanceledWatchStatus(string status)
    {
        return string.Equals(status, FailedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, CancelledStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, CanceledStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOriginalWatchClaim(EngineQueueItemBase payload, PlaylistWatchDownloadClaimDto claim)
    {
        return string.Equals(payload.WatchlistSource, claim.Source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(payload.WatchlistPlaylistId, claim.SourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(payload.WatchlistTrackId, claim.TrackSourceId, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveQueueUuid(string? queueUuid, EngineQueueItemBase payload)
    {
        return !string.IsNullOrWhiteSpace(queueUuid)
            ? queueUuid.Trim()
            : payload.Id ?? string.Empty;
    }

    private static bool CanProcessAtmosPayload(string? engineName)
    {
        return string.Equals(engineName, "apple", StringComparison.OrdinalIgnoreCase)
            || string.Equals(engineName, "tidal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(engineName, "amazon", StringComparison.OrdinalIgnoreCase);
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

        if (DownloadEngineSettingsHelper.IsAtmosOnlyPayload(payload.ContentType, payload.Quality)
            && !CanProcessAtmosPayload(context.EngineName))
        {
            const string message = "Atmos payload must be processed by Apple Music, Tidal, or Amazon Music.";
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
            cancellationToken,
            new DownloadEngineSettingsHelper.ProfileResolutionOptions(CurrentEngine: context.EngineName));
        await context.FolderConversionSettingsOverlay.ApplyAsync(context.Settings, payload.DestinationFolderId, cancellationToken);
        DownloadEngineSettingsHelper.ApplyQualityBucketToSettings(context.Settings, payload.QualityBucket);
        await QueueHelperUtils.SendRunningStartedAsync(
            context.QueueRepository,
            context.Listener,
            queueItem.QueueUuid,
            payload.Downloaded,
            payload.Failed,
            cancellationToken);
        return payload;
    }

    public static async Task HandleCancellationAsync<TPayload>(
        string queueUuid,
        TPayload? payload,
        CancellationHandlingContext context,
        CancellationToken cancellationToken = default)
        where TPayload : EngineQueueItemBase
    {
        await CancelPrefetchAndWaitAsync(queueUuid, PrefetchCancelDrainTimeout, cancellationToken);
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

        if (IsProviderUnavailableFailure(exception))
        {
            await CancelPrefetchAndWaitAsync(queueUuid, PrefetchCancelDrainTimeout, CancellationToken.None);
            var waitingMessage = BuildProviderWaitingMessage(context.EngineName);
            await context.QueueRepository.MarkProviderWaitingAsync(
                queueUuid,
                context.EngineName,
                waitingMessage,
                CancellationToken.None);
            context.ActivityLog.Warn($"Download waiting (engine={context.EngineName}): {queueUuid} {waitingMessage}");
            context.Listener.Send(UpdateQueueEvent, new
            {
                uuid = queueUuid,
                status = "waiting",
                progress = 0,
                downloaded = 0,
                failed = 0,
                error = waitingMessage
            });
            return;
        }

        await CancelPrefetchAndWaitAsync(queueUuid, PrefetchCancelDrainTimeout, CancellationToken.None);
        var failureMessage = !string.IsNullOrWhiteSpace(payload?.ResolutionError)
            ? payload.ResolutionError
            : exception.Message;
        if (!IsFinalDestinationDedupeBlock(failureMessage))
        {
            await context.QueueRepository.UpdatePrefetchStateAsync(
                queueUuid,
                "[]",
                string.Empty,
                FailedStatus,
                "Audio download failed before prefetched assets could be finalized.",
                CancellationToken.None);
        }
        await context.QueueRepository.UpdateStatusAsync(queueUuid, FailedStatus, failureMessage, cancellationToken: CancellationToken.None);
        if (payload != null)
        {
            payload.ErrorMessage = failureMessage;
            var watchlistStatus = IsTrackUnavailableFailure(failureMessage)
                ? "unavailable"
                : FailedStatus;
            await UpdateWatchlistTrackStatusAsync(payload, watchlistStatus, context.ServiceProvider, CancellationToken.None);
        }

        context.ActivityLog.Error($"Download failed (engine={context.EngineName}): {queueUuid} {failureMessage}");
        if (!IsRateLimitedFailure(exception) && !IsFinalDestinationDedupeBlock(failureMessage))
        {
            context.RetryScheduler.ScheduleRetry(queueUuid, context.EngineName, failureMessage);
        }
    }

    public static bool IsFinalDestinationDedupeBlock(string? message)
        => message?.StartsWith("Skipped before download: final destination already contains", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsRateLimitedFailure(Exception exception)
        => exception.Message.Contains("429", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("rate-limit", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("throttl", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrackUnavailableFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.Trim().ToLowerInvariant();
        if (normalized.Contains("credentials", StringComparison.Ordinal)
            || normalized.Contains("login required", StringComparison.Ordinal)
            || normalized.Contains("unauthorized", StringComparison.Ordinal)
            || normalized.Contains("forbidden", StringComparison.Ordinal)
            || normalized.Contains("rate limit", StringComparison.Ordinal)
            || normalized.Contains("too many requests", StringComparison.Ordinal)
            || normalized.Contains("timed out", StringComparison.Ordinal)
            || normalized.Contains("timeout", StringComparison.Ordinal)
            || normalized.Contains("provider", StringComparison.Ordinal)
                && normalized.Contains("unavailable", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("track not found", StringComparison.Ordinal)
            || normalized.Contains("track not available", StringComparison.Ordinal)
            || normalized.Contains("track unavailable", StringComparison.Ordinal)
            || normalized.Contains("track id unavailable", StringComparison.Ordinal)
            || normalized.Contains("identity unavailable", StringComparison.Ordinal)
            || normalized.Contains("download url not available", StringComparison.Ordinal)
            || normalized.Contains("download url unavailable", StringComparison.Ordinal)
            || normalized.Contains("not available from enabled", StringComparison.Ordinal)
            || normalized.Contains("not found for isrc or metadata", StringComparison.Ordinal)
            || normalized.Contains("could not resolve this track", StringComparison.Ordinal)
            || normalized.Contains("could not be resolved for this download", StringComparison.Ordinal);
    }

    private static bool IsProviderUnavailableFailure(Exception exception)
    {
        var message = exception.Message;
        return message.Contains("No Tidal download provider is currently available", StringComparison.OrdinalIgnoreCase)
               || message.Contains("No Qobuz public download provider is currently available", StringComparison.OrdinalIgnoreCase)
               || message.Contains("No enabled Amazon public download API provider", StringComparison.OrdinalIgnoreCase)
               || message.Contains("provider is cooling down", StringComparison.OrdinalIgnoreCase)
               || message.Contains("no public download provider", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildProviderWaitingMessage(string engineName)
        => $"{engineName} public download provider is unavailable. Waiting for provider health to recover.";

    private static PrefetchRequirements BuildPrefetchRequirements(PrefetchRequest request)
    {
        var allowPlaylistCover = ShouldAllowPlaylistCover(request.Payload, request.Settings);
        return new PrefetchRequirements(
            allowPlaylistCover && (request.Settings.SaveArtwork || request.Settings.Tags?.Cover == true),
            allowPlaylistCover && request.Settings.SaveAnimatedArtwork,
            request.Settings.SaveArtworkArtist,
            ShouldSaveLyrics(request.Settings),
            allowPlaylistCover && request.Settings.Tags?.Cover == true);
    }

    private static bool ShouldAllowPlaylistCover(EngineQueueItemBase payload, DeezSpoTagSettings settings)
    {
        var isPlaylist = string.Equals(payload.CollectionType, PlaylistType, StringComparison.OrdinalIgnoreCase);
        return !isPlaylist || settings.DlAlbumcoverForPlaylist;
    }

    private static void TryRemovePrefetchState(string queueUuid, PrefetchGateState expected)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        if (PrefetchGates.TryGetValue(queueUuid, out var current)
            && ReferenceEquals(current, expected))
        {
            PrefetchGates.TryRemove(queueUuid, out _);
            expected.Cancellation.Dispose();
        }
    }
}
