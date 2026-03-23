using System.Text.Json;
using System.Linq;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CoreAlbum = DeezSpoTag.Core.Models.Album;
using CorePlaylist = DeezSpoTag.Core.Models.Playlist;

namespace DeezSpoTag.Services.Download.Deezer;

public sealed class DeezerQueueService
{
    private const string DeezerEngine = "deezer";
    private const string QueueErrorEvent = "queueError";
    private const string AlreadyInQueueEvent = "alreadyInQueue";
    private const string UpdateQueueEvent = "updateQueue";
    private const string UnknownValue = "Unknown";
    private const string QueuedStatus = "queued";
    private const string RunningStatus = "running";
    private const string PausedStatus = "paused";
    private const string FailedStatus = "failed";
    private const string CanceledStatus = "canceled";
    private const string CancelledStatus = "cancelled";
    private const string InQueueStatus = "inQueue";
    private const string EpisodeType = "episode";
    private const string TitleKey = "title";
    private const string ArtistKey = "artist";
    private const string CoverKey = "cover";
    private readonly ILogger<DeezerQueueService> _logger;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly Integrations.Deezer.DeezerClient _deezerClient;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly IActivityLogWriter _activityLog;
    private readonly IDeezSpoTagListener _listener;
    private DeezSpoTagSettings _settings = new();

    public DeezerQueueService(
        ILogger<DeezerQueueService> logger,
        DeezSpoTagSettingsService settingsService,
        Integrations.Deezer.DeezerClient deezerClient,
        DownloadQueueRepository queueRepository,
        IServiceProvider serviceProvider,
        IActivityLogWriter activityLog,
        IDeezSpoTagListener listener)
    {
        _logger = logger;
        _settingsService = settingsService;
        _deezerClient = deezerClient;
        _queueRepository = queueRepository;
        _serviceProvider = serviceProvider;
        _activityLog = activityLog;
        _listener = listener;
    }

    public async Task<List<Dictionary<string, object>>> AddToQueueAsync(
        string[] urls,
        int bitrate,
        bool retry = false,
        long? destinationFolderId = null)
    {
        var deezerClient = _deezerClient;
        _settings = _settingsService.LoadSettings();
        var multiQuality = _settings.MultiQuality;
        var useMultiQuality = multiQuality?.Enabled == true && multiQuality.SecondaryEnabled;
        _logger.LogInformation(
            "Multi-quality check: enabled={Enabled} secondaryEnabled={SecondaryEnabled} useMultiQuality={UseMultiQuality}",
            multiQuality?.Enabled,
            multiQuality?.SecondaryEnabled,
            useMultiQuality);
        var multiQualityContext = CreateMultiQualityContext(multiQuality, destinationFolderId, useMultiQuality);

        _logger.LogInformation(
            "NOTIFICATION FLOW: Starting AddToQueueAsync for {UrlCount} URLs - Login Status: {LoggedIn}, User: {UserName}",
            urls.Length,
            deezerClient.LoggedIn,
            deezerClient.CurrentUser?.Name ?? "None");

        EnsureQueuePrerequisites(deezerClient, urls);

        using var scope = _serviceProvider.CreateScope();
        var dependencies = ResolveAddToQueueDependencies(scope.ServiceProvider);
        var queueItems = await BuildQueueCandidatesAsync(urls, bitrate, dependencies, multiQualityContext);

        if (queueItems.Count == 0)
        {
            _logger.LogWarning("No valid download objects generated from {UrlCount} URLs", urls.Length);
            return new List<Dictionary<string, object>>();
        }

        var slimmedObjects = await EnqueueCandidatesAsync(queueItems, retry, dependencies.LibraryRepository);

        if (slimmedObjects.Count > 0)
        {
            if (slimmedObjects.Count == 1)
            {
                _listener.Send("addedToQueue", slimmedObjects[0]);
            }
            else
            {
                _listener.Send("addedToQueue", slimmedObjects);
            }
            _logger.LogInformation("Successfully added {Count} items to queue and notified listeners", slimmedObjects.Count);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(100);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error ensuring queue processor is running");
                }
            });
        }

        return slimmedObjects;
    }

    private sealed record MultiQualityContext(
        bool UseMultiQuality,
        MultiQualityDownloadSettings? Settings,
        long? PrimaryDestinationFolderId);

    private sealed record QueueCandidate(DeezerQueueItem Item, bool IsSecondary);

    private sealed record AddToQueueDependencies(
        Objects.DownloadObjectGenerator Generator,
        LibraryRepository? LibraryRepository,
        ISpotifyIdResolver? SpotifyIdResolver,
        ISpotifyArtworkResolver? SpotifyArtworkResolver);

    private static MultiQualityContext CreateMultiQualityContext(
        MultiQualityDownloadSettings? settings,
        long? destinationFolderId,
        bool useMultiQuality)
    {
        var primaryDestinationFolderId = useMultiQuality
            ? settings!.PrimaryDestinationFolderId ?? destinationFolderId
            : destinationFolderId;
        return new MultiQualityContext(useMultiQuality, settings, primaryDestinationFolderId);
    }

    private void EnsureQueuePrerequisites(Integrations.Deezer.DeezerClient deezerClient, IReadOnlyCollection<string> urls)
    {
        if (!deezerClient.LoggedIn)
        {
            _logger.LogError("Cannot add tracks to queue - user is not logged in to Deezer");
            foreach (var url in urls)
            {
                _listener.Send(QueueErrorEvent, new
                {
                    link = url,
                    error = "Must be logged in to Deezer before adding tracks to download queue",
                    errid = "NotLoggedIn"
                });
            }

            throw new InvalidOperationException("Must be logged in to Deezer before adding tracks to download queue");
        }

        if (!DownloadQueueRepository.IsConfigured)
        {
            _logger.LogError("Queue DB connection string not configured; cannot enqueue downloads");
            throw new InvalidOperationException("Queue database is not configured");
        }
    }

    private static AddToQueueDependencies ResolveAddToQueueDependencies(IServiceProvider provider)
    {
        return new AddToQueueDependencies(
            provider.GetRequiredService<Objects.DownloadObjectGenerator>(),
            provider.GetService<LibraryRepository>(),
            provider.GetService<ISpotifyIdResolver>(),
            provider.GetService<ISpotifyArtworkResolver>());
    }

    private async Task<List<QueueCandidate>> BuildQueueCandidatesAsync(
        IEnumerable<string> urls,
        int bitrate,
        AddToQueueDependencies dependencies,
        MultiQualityContext multiQualityContext)
    {
        var queueItems = new List<QueueCandidate>();
        foreach (var url in urls)
        {
            _logger.LogInformation("Processing {Url} for queue", url);
            try
            {
                await BuildQueueCandidatesForUrlAsync(queueItems, url, bitrate, dependencies, multiQualityContext);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to generate download object for {Url}", url);
                _listener.Send(QueueErrorEvent, new
                {
                    link = url,
                    error = ex.Message,
                    errid = ex.GetType().Name
                });
            }
        }

        return queueItems;
    }

    private async Task BuildQueueCandidatesForUrlAsync(
        List<QueueCandidate> queueItems,
        string url,
        int bitrate,
        AddToQueueDependencies dependencies,
        MultiQualityContext multiQualityContext)
    {
        var generatedObjects = await dependencies.Generator.GenerateDownloadObjectFromUrlAsync(url, bitrate);
        if (generatedObjects.Count == 0)
        {
            throw new InvalidOperationException("No download objects generated for URL");
        }

        for (var generatedIndex = 0; generatedIndex < generatedObjects.Count; generatedIndex++)
        {
            var deezspotagDownloadObject = await ConvertToDeezSpoTagDownloadObjectAsync(
                generatedObjects[generatedIndex],
                dependencies.SpotifyIdResolver,
                dependencies.SpotifyArtworkResolver,
                CancellationToken.None);
            var baseItems = BuildQueueItemsFromDownloadObject(
                deezspotagDownloadObject,
                url,
                bitrate,
                multiQualityContext.PrimaryDestinationFolderId);

            foreach (var baseItem in baseItems)
            {
                if (!await TryAddPrimaryQueueCandidateAsync(queueItems, baseItem, url, dependencies.LibraryRepository))
                {
                    continue;
                }

                if (!multiQualityContext.UseMultiQuality)
                {
                    continue;
                }

                await TryAddSecondaryQueueCandidateAsync(
                    queueItems,
                    baseItem,
                    url,
                    dependencies.LibraryRepository,
                    multiQualityContext.Settings);
            }
        }
    }

    private async Task<bool> TryAddPrimaryQueueCandidateAsync(
        List<QueueCandidate> queueItems,
        DeezerQueueItem baseItem,
        string url,
        LibraryRepository? libraryRepository)
    {
        if (libraryRepository == null || !libraryRepository.IsConfigured)
        {
            _listener.Send(QueueErrorEvent, new
            {
                link = url,
                error = "Library folders are not configured.",
                errid = "DestinationFolderRequired"
            });
            return false;
        }

        var destCheck = await DownloadDestinationGuard.ValidateAsync(
            baseItem.DestinationFolderId,
            _settings.DownloadLocation,
            libraryRepository,
            CancellationToken.None,
            baseItem.ContentType);
        if (!destCheck.Ok)
        {
            NotifyDestinationFolderRequired(url, destCheck.Error);
            return false;
        }

        queueItems.Add(new QueueCandidate(baseItem, false));
        return true;
    }

    private async Task TryAddSecondaryQueueCandidateAsync(
        List<QueueCandidate> queueItems,
        DeezerQueueItem baseItem,
        string url,
        LibraryRepository? libraryRepository,
        MultiQualityDownloadSettings? multiQualitySettings)
    {
        if (libraryRepository == null || !libraryRepository.IsConfigured || multiQualitySettings == null)
        {
            return;
        }

        var secondaryBitrate = ResolveSecondaryBitrate(
            multiQualitySettings.SecondaryQuality,
            baseItem.Bitrate);
        if (secondaryBitrate <= 0 || secondaryBitrate == baseItem.Bitrate)
        {
            _logger.LogWarning(
                "Multi-quality skipped for {Title}: secondaryBitrate={SecondaryBitrate} primaryBitrate={PrimaryBitrate} configuredSecondaryQuality={ConfiguredSecondaryQuality}",
                baseItem.Title,
                secondaryBitrate,
                baseItem.Bitrate,
                multiQualitySettings.SecondaryQuality);
            return;
        }

        var secondaryItem = CloneQueueItem(baseItem);
        secondaryItem.Id = Guid.NewGuid().ToString("N");
        secondaryItem.Bitrate = secondaryBitrate;
        secondaryItem.Quality = secondaryBitrate.ToString();
        secondaryItem.DestinationFolderId = multiQualitySettings.SecondaryDestinationFolderId ?? baseItem.DestinationFolderId;

        var secondaryCheck = await DownloadDestinationGuard.ValidateAsync(
            secondaryItem.DestinationFolderId,
            _settings.DownloadLocation,
            libraryRepository,
            CancellationToken.None,
            secondaryItem.ContentType);
        if (!secondaryCheck.Ok)
        {
            NotifyDestinationFolderRequired(url, secondaryCheck.Error);
            return;
        }

        queueItems.Add(new QueueCandidate(secondaryItem, true));
    }

    private async Task<List<Dictionary<string, object>>> EnqueueCandidatesAsync(
        IEnumerable<QueueCandidate> candidates,
        bool retry,
        LibraryRepository? libraryRepository)
    {
        var slimmedObjects = new List<Dictionary<string, object>>();
        foreach (var candidate in candidates)
        {
            var payload = candidate.Item;
            if (retry)
            {
                payload.Id = Guid.NewGuid().ToString("N");
            }

            var isEpisode = IsEpisodePayload(payload);
            var dedupeData = GetQueueDeduplicationData(payload);
            var shouldSkip = await ShouldSkipQueueCandidateAsync(
                payload,
                candidate.IsSecondary,
                retry,
                isEpisode,
                dedupeData,
                libraryRepository);
            if (shouldSkip)
            {
                continue;
            }

            var queueItem = CreateDownloadQueueItem(payload, dedupeData);
            await _queueRepository.EnqueueAsync(queueItem, skipDuplicateCheck: isEpisode);
            slimmedObjects.Add(payload.ToQueuePayload());
        }

        return slimmedObjects;
    }

    private async Task<bool> ShouldSkipQueueCandidateAsync(
        DeezerQueueItem payload,
        bool isSecondary,
        bool retry,
        bool isEpisode,
        QueueDeduplicationData dedupeData,
        LibraryRepository? libraryRepository)
    {
        if (await ShouldSkipForLibraryDuplicateAsync(payload, isSecondary, retry, isEpisode, libraryRepository))
        {
            return true;
        }

        if (await ShouldSkipForExistingTrackIdAsync(payload, isSecondary, retry, isEpisode))
        {
            return true;
        }

        if (await ShouldSkipForMetadataDuplicateAsync(payload, retry, isEpisode, dedupeData))
        {
            return true;
        }

        if (await ShouldSkipForEpisodeDuplicateAsync(payload, retry, isEpisode))
        {
            return true;
        }

        return await ShouldSkipForExistingMetadataAsync(payload);
    }

    private async Task<bool> ShouldSkipForLibraryDuplicateAsync(
        DeezerQueueItem payload,
        bool isSecondary,
        bool retry,
        bool isEpisode,
        LibraryRepository? libraryRepository)
    {
        if (isEpisode || isSecondary || retry || libraryRepository == null || !libraryRepository.IsConfigured)
        {
            return false;
        }

        var isLibraryDuplicate = await IsLibraryDuplicateAsync(payload, libraryRepository);
        if (!isLibraryDuplicate)
        {
            return false;
        }

        NotifyAlreadyInQueue(payload);
        return true;
    }

    private async Task<bool> ShouldSkipForExistingTrackIdAsync(
        DeezerQueueItem payload,
        bool isSecondary,
        bool retry,
        bool isEpisode)
    {
        if (isEpisode || isSecondary || retry || string.IsNullOrWhiteSpace(payload.DeezerId))
        {
            return false;
        }

        var existingTrack = await _queueRepository.GetByDeezerTrackIdAsync(DeezerEngine, payload.DeezerId);
        if (existingTrack == null)
        {
            return false;
        }

        var existingStatus = existingTrack.Status ?? string.Empty;
        if (existingStatus is QueuedStatus or RunningStatus or PausedStatus || IsCompletedStatus(existingStatus))
        {
            NotifyAlreadyInQueue(payload);
            return true;
        }

        if (!IsRetryableStatus(existingStatus))
        {
            return false;
        }

        await _queueRepository.RequeueAsync(existingTrack.QueueUuid);
        _activityLog.Info($"Track-id duplicate triggered retry (engine=deezer): {existingTrack.QueueUuid}");
        NotifyRequeued(existingTrack.QueueUuid);
        return true;
    }

    private async Task<bool> ShouldSkipForMetadataDuplicateAsync(
        DeezerQueueItem payload,
        bool retry,
        bool isEpisode,
        QueueDeduplicationData dedupeData)
    {
        if (isEpisode || retry)
        {
            return false;
        }

        var hasDuplicate = await _queueRepository.ExistsDuplicateAsync(
            new DuplicateLookupRequest
            {
                Isrc = dedupeData.Isrc,
                DeezerTrackId = dedupeData.DeezerTrackId,
                DeezerAlbumId = dedupeData.DeezerAlbumId,
                DeezerArtistId = dedupeData.DeezerArtistId,
                SpotifyTrackId = dedupeData.SpotifyTrackId,
                SpotifyAlbumId = dedupeData.SpotifyAlbumId,
                SpotifyArtistId = dedupeData.SpotifyArtistId,
                AppleTrackId = dedupeData.AppleTrackId,
                AppleAlbumId = dedupeData.AppleAlbumId,
                AppleArtistId = dedupeData.AppleArtistId,
                ArtistName = payload.Artist ?? UnknownValue,
                TrackTitle = payload.Title ?? UnknownValue,
                DurationMs = dedupeData.DurationMs,
                DestinationFolderId = payload.DestinationFolderId,
                ContentType = payload.ContentType,
                RedownloadCooldownMinutes = _settings.RedownloadCooldownMinutes
            });
        if (!hasDuplicate)
        {
            return false;
        }

        var existingDuplicate = await _queueRepository.GetByMetadataAsync(
            new MetadataLookupRequest
            {
                ArtistName = payload.Artist ?? UnknownValue,
                TrackTitle = payload.Title ?? UnknownValue,
                ContentType = payload.ContentType
            });
        if (existingDuplicate != null && IsRetryableStatus(existingDuplicate.Status))
        {
            await _queueRepository.RequeueAsync(existingDuplicate.QueueUuid);
            _activityLog.Info($"Duplicate triggered retry (engine=deezer): {existingDuplicate.QueueUuid}");
            NotifyRequeued(existingDuplicate.QueueUuid);
            return true;
        }

        NotifyAlreadyInQueue(payload);
        return true;
    }

    private async Task<bool> ShouldSkipForEpisodeDuplicateAsync(
        DeezerQueueItem payload,
        bool retry,
        bool isEpisode)
    {
        if (!isEpisode || retry)
        {
            return false;
        }

        var existingEpisode = await _queueRepository.GetByDeezerTrackIdAsync(DeezerEngine, payload.DeezerId);
        if (existingEpisode == null)
        {
            return false;
        }

        var existingStatus = existingEpisode.Status ?? string.Empty;
        if (existingStatus is QueuedStatus or RunningStatus or PausedStatus || IsCompletedStatus(existingStatus))
        {
            NotifyAlreadyInQueue(payload);
            return true;
        }

        if (!IsRetryableStatus(existingStatus))
        {
            return false;
        }

        await _queueRepository.RequeueAsync(existingEpisode.QueueUuid);
        _activityLog.Info($"Episode retry queued (engine=deezer): {existingEpisode.QueueUuid}");
        NotifyRequeued(existingEpisode.QueueUuid);
        return true;
    }

    private async Task<bool> ShouldSkipForExistingMetadataAsync(DeezerQueueItem payload)
    {
        var existingByMetadata = await _queueRepository.GetByMetadataAsync(
            new MetadataLookupRequest
            {
                ArtistName = payload.Artist ?? UnknownValue,
                TrackTitle = payload.Title ?? UnknownValue,
                ContentType = payload.ContentType
            });
        if (existingByMetadata is null)
        {
            return false;
        }

        var existingStatus = existingByMetadata.Status ?? string.Empty;
        if (existingStatus is QueuedStatus or RunningStatus or PausedStatus)
        {
            NotifyAlreadyInQueue(payload);
            return true;
        }

        if (!IsSameProfile(existingByMetadata.PayloadJson, payload))
        {
            return true;
        }

        if (!IsCompletedStatus(existingStatus))
        {
            return false;
        }

        NotifyAlreadyInQueue(payload);
        return true;
    }

    private static bool IsRetryableStatus(string? status)
    {
        var normalizedStatus = status ?? string.Empty;
        return normalizedStatus is FailedStatus or CanceledStatus or CancelledStatus;
    }

    private static DownloadQueueItem CreateDownloadQueueItem(DeezerQueueItem payload, QueueDeduplicationData dedupeData)
    {
        var duration = payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : (int?)null;
        var now = DateTimeOffset.UtcNow;
        return new DownloadQueueItem(
            Id: 0,
            QueueUuid: payload.Id,
            Engine: DeezerEngine,
            ArtistName: payload.Artist ?? UnknownValue,
            TrackTitle: payload.Title ?? UnknownValue,
            Isrc: dedupeData.Isrc,
            DeezerTrackId: dedupeData.DeezerTrackId,
            DeezerAlbumId: dedupeData.DeezerAlbumId,
            DeezerArtistId: dedupeData.DeezerArtistId,
            SpotifyTrackId: dedupeData.SpotifyTrackId,
            SpotifyAlbumId: dedupeData.SpotifyAlbumId,
            SpotifyArtistId: dedupeData.SpotifyArtistId,
            AppleTrackId: dedupeData.AppleTrackId,
            AppleAlbumId: dedupeData.AppleAlbumId,
            AppleArtistId: dedupeData.AppleArtistId,
            DurationMs: duration,
            DestinationFolderId: payload.DestinationFolderId,
            QualityRank: MapBitrateToQualityRank(payload.Bitrate),
            QueueOrder: null,
            ContentType: payload.ContentType,
            Status: QueuedStatus,
            PayloadJson: JsonSerializer.Serialize(payload),
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: now,
            UpdatedAt: now);
    }

    private void NotifyDestinationFolderRequired(string url, string? error)
    {
        _listener.Send(QueueErrorEvent, new
        {
            link = url,
            error = error ?? "Destination folder is required.",
            errid = "DestinationFolderRequired"
        });
    }

    private void NotifyRequeued(string queueUuid)
    {
        _listener.Send(UpdateQueueEvent, new
        {
            uuid = queueUuid,
            status = InQueueStatus,
            progress = 0,
            downloaded = 0,
            failed = 0,
            error = default(string)
        });
    }

    private void NotifyAlreadyInQueue(DeezerQueueItem payload)
    {
        _listener.Send(AlreadyInQueueEvent, payload.ToQueuePayload());
    }

    private static List<DeezerQueueItem> BuildQueueItemsFromDownloadObject(
        DeezSpoTagDownloadObject downloadObject,
        string sourceUrl,
        int bitrate,
        long? destinationFolderId)
    {
        var items = new List<DeezerQueueItem>();
        var isEpisodeObject = string.Equals(downloadObject.Type, EpisodeType, StringComparison.OrdinalIgnoreCase);
        var quality = isEpisodeObject ? DownloadContentTypes.Podcast : bitrate.ToString();
        var autoSources = isEpisodeObject
            ? DownloadSourceOrder.ResolveEngineQualitySources(DeezerEngine, DownloadContentTypes.Podcast, strict: true)
            : DownloadSourceOrder.ResolveEngineQualitySources(DeezerEngine, quality, strict: false);
        var autoIndex = autoSources.Count > 0 ? 0 : -1;

        if (downloadObject is DeezSpoTagSingle single)
        {
            var track = single.Single.TrackAPI;
            var source = ResolveSourceUrlForCollection(track, single.Type, sourceUrl);
            var normalizedCollectionType = NormalizeCollectionType(single.Type, sourceUrl);
            var itemBitrate = string.Equals(normalizedCollectionType, EpisodeType, StringComparison.OrdinalIgnoreCase) ? 0 : bitrate;
            var item = BuildQueueItemFromTrack(
                track,
                new QueueItemBuildContext(
                    normalizedCollectionType,
                    string.Empty,
                    single.Artist,
                    single.Cover,
                    source,
                    quality,
                    autoSources,
                    autoIndex,
                    itemBitrate,
                    destinationFolderId));
            items.Add(item);
            return items;
        }

        if (downloadObject is DeezSpoTagCollection collection)
        {
            var normalizedCollectionType = NormalizeCollectionType(collection.Type, sourceUrl);
            foreach (var track in collection.Collection.Tracks)
            {
                var source = ResolveSourceUrlForCollection(track, normalizedCollectionType, sourceUrl);
                var itemBitrate = string.Equals(normalizedCollectionType, EpisodeType, StringComparison.OrdinalIgnoreCase) ? 0 : bitrate;
                var item = BuildQueueItemFromTrack(
                    track,
                    new QueueItemBuildContext(
                        normalizedCollectionType,
                        collection.Title,
                        collection.Artist,
                        collection.Cover,
                        source,
                        quality,
                        autoSources,
                        autoIndex,
                        itemBitrate,
                        destinationFolderId));
                items.Add(item);
            }
        }

        return items;
    }

    private sealed record QueueItemBuildContext(
        string CollectionType,
        string CollectionName,
        string ArtistFallback,
        string CoverFallback,
        string SourceUrl,
        string Quality,
        List<string> AutoSources,
        int AutoIndex,
        int Bitrate,
        long? DestinationFolderId);

    private static DeezerQueueItem BuildQueueItemFromTrack(
        Dictionary<string, object> track,
        QueueItemBuildContext context)
    {
        var title = GetTrackField(track, TitleKey) ?? string.Empty;
        var artist = GetNestedTrackField(track, ArtistKey, "name") ?? context.ArtistFallback;
        var albumTitle = GetNestedTrackField(track, "album", TitleKey) ?? string.Empty;
        var durationSeconds = 0;
        if (track.TryGetValue("duration", out var durationValue) && durationValue != null && TryParseInt(durationValue, out var parsedDuration))
        {
            durationSeconds = parsedDuration;
        }

        var normalizedCollectionType = string.IsNullOrWhiteSpace(context.CollectionType) ? "track" : context.CollectionType;
        var effectiveQuality = string.Equals(normalizedCollectionType, EpisodeType, StringComparison.OrdinalIgnoreCase)
            ? DownloadContentTypes.Podcast
            : context.Quality;
        var effectiveBitrate = string.Equals(normalizedCollectionType, EpisodeType, StringComparison.OrdinalIgnoreCase)
            ? 0
            : context.Bitrate;

        var item = new DeezerQueueItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Engine = DeezerEngine,
            QueueOrigin = "tracklist",
            SourceService = DeezerEngine,
            SourceUrl = context.SourceUrl,
            CollectionName = context.CollectionName ?? string.Empty,
            CollectionType = normalizedCollectionType,
            Title = string.IsNullOrWhiteSpace(title) ? context.CollectionName ?? string.Empty : title,
            Artist = artist ?? string.Empty,
            Album = albumTitle,
            AlbumArtist = artist ?? string.Empty,
            Isrc = GetTrackField(track, "isrc") ?? string.Empty,
            DeezerId = GetTrackField(track, "id") ?? string.Empty,
            DeezerAlbumId = GetNestedTrackId(track, "album", "id") ?? string.Empty,
            DeezerArtistId = GetNestedTrackId(track, ArtistKey, "id") ?? GetTrackField(track, "artist_id") ?? string.Empty,
            ContentType = ResolveContentType(normalizedCollectionType, context.SourceUrl, effectiveQuality),
            SpotifyId = GetTrackField(track, "spotify_id") ?? GetTrackField(track, "spotifyId") ?? string.Empty,
            AppleId = GetTrackField(track, "apple_id") ?? GetTrackField(track, "appleId") ?? string.Empty,
            Cover = string.IsNullOrWhiteSpace(context.CoverFallback)
                ? (GetNestedTrackField(track, "album", CoverKey) ?? string.Empty)
                : context.CoverFallback,
            AutoSources = context.AutoSources,
            AutoIndex = context.AutoIndex,
            Quality = effectiveQuality,
            Bitrate = effectiveBitrate,
            DurationSeconds = durationSeconds,
            Position = TryGetPosition(track),
            DestinationFolderId = context.DestinationFolderId,
            Size = 1
        };

        return item;
    }

    private static string ResolveSourceUrlForCollection(
        Dictionary<string, object> track,
        string? collectionType,
        string sourceUrl)
    {
        var normalizedCollectionType = NormalizeCollectionType(collectionType, sourceUrl);
        if (!string.Equals(normalizedCollectionType, EpisodeType, StringComparison.OrdinalIgnoreCase))
        {
            return sourceUrl;
        }

        // Keep episode queue URLs canonical so the processor always resolves a fresh
        // direct stream from GW at execution time (avoids stale/expired direct links).
        var episodeId = GetTrackField(track, "id") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(episodeId))
        {
            return $"https://www.deezer.com/episode/{episodeId}";
        }

        return sourceUrl;
    }

    private static string NormalizeCollectionType(string? collectionType, string? sourceUrl)
    {
        var normalizedCollectionType = string.IsNullOrWhiteSpace(collectionType) ? "track" : collectionType.Trim();
        if (!string.Equals(normalizedCollectionType, EpisodeType, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedCollectionType;
        }

        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            if (sourceUrl.Contains("/episode/", StringComparison.OrdinalIgnoreCase)
                || sourceUrl.Contains("/show/", StringComparison.OrdinalIgnoreCase))
            {
                return EpisodeType;
            }

            if (sourceUrl.Contains("/track/", StringComparison.OrdinalIgnoreCase)
                || sourceUrl.Contains("/album/", StringComparison.OrdinalIgnoreCase)
                || sourceUrl.Contains("/playlist/", StringComparison.OrdinalIgnoreCase)
                || sourceUrl.Contains("/artist/", StringComparison.OrdinalIgnoreCase))
            {
                return "track";
            }
        }

        return normalizedCollectionType;
    }

    private static string ResolveContentType(string? collectionType, string? sourceUrl, string? quality)
    {
        if (!string.IsNullOrWhiteSpace(collectionType)
            && string.Equals(collectionType, EpisodeType, StringComparison.OrdinalIgnoreCase))
        {
            return DownloadContentTypes.Podcast;
        }

        if (!string.IsNullOrWhiteSpace(sourceUrl)
            && sourceUrl.Contains("/episode/", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadContentTypes.Podcast;
        }

        if (!string.IsNullOrWhiteSpace(quality)
            && quality.Contains("atmos", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadContentTypes.Atmos;
        }

        return DownloadContentTypes.Stereo;
    }

    private static int ResolveSecondaryBitrate(string? configuredQuality, int primaryBitrate)
    {
        if (string.IsNullOrWhiteSpace(configuredQuality))
        {
            return ResolveFallbackSecondaryBitrate(primaryBitrate);
        }

        if (!int.TryParse(configuredQuality, out var parsed) || parsed <= 0)
        {
            return ResolveFallbackSecondaryBitrate(primaryBitrate);
        }

        return parsed == primaryBitrate ? ResolveFallbackSecondaryBitrate(primaryBitrate) : parsed;
    }

    private static int ResolveFallbackSecondaryBitrate(int primaryBitrate)
    {
        return primaryBitrate switch
        {
            DownloadSourceOrder.DeezerFlac => DownloadSourceOrder.DeezerMp3High,
            DownloadSourceOrder.DeezerMp3High => DownloadSourceOrder.DeezerFlac,
            DownloadSourceOrder.DeezerMp3Low => DownloadSourceOrder.DeezerMp3High,
            _ => 0
        };
    }

    private static DeezerQueueItem CloneQueueItem(DeezerQueueItem source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<DeezerQueueItem>(json) ?? source;
    }

    private static bool IsSameProfile(string? payloadJson, DeezerQueueItem candidate)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var existingBitrate = root.TryGetProperty("bitrate", out var bitrateEl) && bitrateEl.TryGetInt32(out var bitrate)
                ? bitrate
                : candidate.Bitrate;
            var existingDestination = root.TryGetProperty("destinationFolderId", out var destinationEl)
                                      && destinationEl.TryGetInt64(out var dest)
                ? dest
                : candidate.DestinationFolderId;

            return existingBitrate == candidate.Bitrate
                && existingDestination == candidate.DestinationFolderId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return true;
        }
    }

    private static bool IsCompletedStatus(string status)
    {
        return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("complete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEpisodePayload(DeezerQueueItem payload)
    {
        if (string.Equals(payload.CollectionType, EpisodeType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(payload.SourceUrl)
               && payload.SourceUrl.Contains("/episode/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsLibraryDuplicateAsync(
        DeezerQueueItem payload,
        LibraryRepository libraryRepository)
    {
        var sourceIds = new List<(string Source, string Id)>();
        if (!string.IsNullOrWhiteSpace(payload.DeezerId))
        {
            sourceIds.Add((DeezerEngine, payload.DeezerId));
        }

        if (!string.IsNullOrWhiteSpace(payload.SpotifyId))
        {
            sourceIds.Add(("spotify", payload.SpotifyId));
        }

        if (!string.IsNullOrWhiteSpace(payload.AppleId))
        {
            sourceIds.Add(("apple", payload.AppleId));
        }

        foreach (var (source, id) in sourceIds)
        {
            if (await libraryRepository.ExistsTrackSourceAsync(source, id))
            {
                return true;
            }
        }

        var durationMs = payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : (int?)null;
        var existence = await libraryRepository.ExistsInLibraryAsync(
            new[]
            {
                new LibraryRepository.LibraryExistenceInput(
                    payload.Isrc,
                    payload.Title,
                    payload.Artist,
                    durationMs)
            });
        if (existence.Count > 0 && existence[0])
        {
            return true;
        }

        return false;
    }

    private static string? GetTrackField(Dictionary<string, object> track, string key)
    {
        if (!track.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string str => str,
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),
            _ => value.ToString()
        };
    }

    private static string? GetNestedTrackField(Dictionary<string, object> track, string parentKey, string childKey)
    {
        return GetNestedTrackValue(track, parentKey, childKey);
    }

    private static int TryGetPosition(Dictionary<string, object> track)
    {
        if (track.TryGetValue("position", out var positionValue) && positionValue != null && TryParseInt(positionValue, out var position))
        {
            return position;
        }

        if (track.TryGetValue("track_position", out var trackPositionValue) && trackPositionValue != null && TryParseInt(trackPositionValue, out var trackPosition))
        {
            return trackPosition;
        }

        return 0;
    }

    private sealed record QueueDeduplicationData(
        string? Isrc,
        int? DurationMs,
        string? DeezerTrackId,
        string? DeezerAlbumId,
        string? DeezerArtistId,
        string? SpotifyTrackId,
        string? SpotifyAlbumId,
        string? SpotifyArtistId,
        string? AppleTrackId,
        string? AppleAlbumId,
        string? AppleArtistId);

    private static QueueDeduplicationData GetQueueDeduplicationData(DeezerQueueItem payload)
    {
        var durationMs = payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : (int?)null;
        return new QueueDeduplicationData(
            payload.Isrc,
            durationMs,
            payload.DeezerId,
            null, // DeezerAlbumId excluded: shared across all album tracks, causes false duplicate matches
            null, // DeezerArtistId excluded: shared across all artist tracks, causes false duplicate matches
            payload.SpotifyId,
            null,
            null,
            payload.AppleId,
            null,
            null);
    }

    private static string? GetNestedTrackId(Dictionary<string, object> track, string parentKey, string idKey)
    {
        return GetNestedTrackValue(track, parentKey, idKey);
    }

    private static string? GetNestedTrackValue(Dictionary<string, object> track, string parentKey, string childKey)
    {
        if (!track.TryGetValue(parentKey, out var value) || value is null)
        {
            return null;
        }

        if (value is Dictionary<string, object> dict)
        {
            return GetTrackField(dict, childKey);
        }

        if (value is JsonElement element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(childKey, out var child))
        {
            return child.ValueKind == JsonValueKind.String
                ? child.GetString()
                : child.ToString();
        }

        return null;
    }

    private static bool TryParseInt(object value, out int result)
    {
        result = 0;
        switch (value)
        {
            case int intValue:
                result = intValue;
                return true;
            case long longValue:
                if (longValue is < int.MinValue or > int.MaxValue) return false;
                result = (int)longValue;
                return true;
            case string strValue:
                return int.TryParse(strValue, out result);
            case JsonElement element when element.ValueKind == JsonValueKind.Number:
                return element.TryGetInt32(out result);
            case JsonElement element when element.ValueKind == JsonValueKind.String:
                return int.TryParse(element.GetString(), out result);
            default:
                return int.TryParse(value.ToString(), out result);
        }
    }

    private async Task<DeezSpoTagDownloadObject> ConvertToDeezSpoTagDownloadObjectAsync(
        DownloadObject downloadObject,
        ISpotifyIdResolver? spotifyIdResolver,
        ISpotifyArtworkResolver? spotifyArtworkResolver,
        CancellationToken cancellationToken)
    {
        if (downloadObject is SingleDownloadObject single)
        {
            return await ConvertSingleDownloadObjectAsync(
                single,
                spotifyIdResolver,
                spotifyArtworkResolver,
                cancellationToken);
        }

        if (downloadObject is CollectionDownloadObject collection)
        {
            return await ConvertCollectionDownloadObjectAsync(
                collection,
                spotifyIdResolver,
                spotifyArtworkResolver,
                cancellationToken);
        }

        throw new InvalidOperationException($"Unsupported download object type: {downloadObject.GetType()}");
    }

    private async Task<DeezSpoTagSingle> ConvertSingleDownloadObjectAsync(
        SingleDownloadObject single,
        ISpotifyIdResolver? spotifyIdResolver,
        ISpotifyArtworkResolver? spotifyArtworkResolver,
        CancellationToken cancellationToken)
    {
        var track = single.Track ?? new Track();
        var album = track.Album;
        var cover = await ResolveCoverWithFallbackAsync(
            track,
            album,
            spotifyIdResolver,
            spotifyArtworkResolver,
            cancellationToken);
        var downloadType = string.IsNullOrWhiteSpace(single.Type) ? "track" : single.Type;

        return new DeezSpoTagSingle
        {
            Id = track.Id ?? "",
            Type = downloadType,
            Bitrate = single.Bitrate,
            UUID = single.Uuid,
            Title = track.Title ?? single.Title,
            Artist = track.MainArtist?.Name ?? "",
            Cover = cover,
            Explicit = track.Explicit,
            Size = 1,
            ExtrasPath = single.ExtrasPath ?? "",
            Single = new SingleData
            {
                TrackAPI = CreateTrackApiDict(track, album, single.Playlist),
                AlbumAPI = album != null ? CreateAlbumApiDict(album) : null
            }
        };
    }

    private async Task<DeezSpoTagCollection> ConvertCollectionDownloadObjectAsync(
        CollectionDownloadObject collection,
        ISpotifyIdResolver? spotifyIdResolver,
        ISpotifyArtworkResolver? spotifyArtworkResolver,
        CancellationToken cancellationToken)
    {
        var tracks = collection.Tracks ?? new List<Track>();
        var album = collection.Album;
        var playlist = collection.Playlist;
        var isPlaylist = collection.Type == "playlist";
        var cover = isPlaylist ? GetCoverUrl(playlist?.Pic, "playlist", 75) : string.Empty;
        var coverTrack = tracks.FirstOrDefault();
        if (!isPlaylist)
        {
            cover = await ResolveCoverWithFallbackAsync(
                coverTrack,
                coverTrack?.Album ?? album,
                spotifyIdResolver,
                spotifyArtworkResolver,
                cancellationToken);
        }

        var isExplicit = isPlaylist ? tracks.Any(t => t.Explicit) : (album?.Explicit ?? false);
        var id = album?.Id ?? playlist?.Id ?? "";
        return new DeezSpoTagCollection
        {
            Id = id,
            Type = collection.Type,
            Bitrate = collection.Bitrate,
            UUID = collection.Uuid,
            Title = collection.Title,
            Artist = album?.MainArtist?.Name ?? playlist?.Owner?.Name ?? "",
            Cover = cover,
            Explicit = isExplicit,
            Size = tracks.Count,
            ExtrasPath = collection.ExtrasPath ?? "",
            Collection = new CollectionData
            {
                Tracks = tracks.Select((t, index) => CreateTrackApiDict(t, t.Album, playlist, index)).ToList(),
                AlbumAPI = album != null ? CreateAlbumApiDict(album) : null,
                PlaylistAPI = playlist != null ? CreatePlaylistApiDict(playlist) : null
            }
        };
    }

    private sealed record CoverFallbackRequest(
        Track? Track,
        CoreAlbum? Album,
        string FallbackCover,
        bool AllowsJpegCover,
        string? AppleId,
        string? AppleTitle,
        string? AppleArtist,
        string? AppleAlbum,
        IHttpClientFactory? HttpClientFactory,
        AppleMusicCatalogService? AppleCatalog);

    private async Task<string> ResolveCoverWithFallbackAsync(
        Track? track,
        CoreAlbum? album,
        ISpotifyIdResolver? spotifyIdResolver,
        ISpotifyArtworkResolver? spotifyArtworkResolver,
        CancellationToken cancellationToken)
    {
        var request = new CoverFallbackRequest(
            track,
            album,
            GetCoverUrl(album?.Pic, CoverKey, 75),
            AllowsJpegArtwork(),
            ArtworkFallbackHelper.TryExtractAppleTrackId(track),
            track?.Title,
            track?.MainArtist?.Name,
            track?.Album?.Title ?? album?.Title,
            _serviceProvider.GetService<IHttpClientFactory>(),
            _serviceProvider.GetService<AppleMusicCatalogService>());
        var fallbackOrder = ArtworkFallbackHelper.ResolveOrder(_settings);

        foreach (var fallback in fallbackOrder)
        {
            var resolvedCover = await TryResolveCoverForFallbackAsync(
                fallback,
                request,
                spotifyIdResolver,
                spotifyArtworkResolver,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedCover))
            {
                return resolvedCover;
            }
        }

        return request.FallbackCover;
    }

    private async Task<string?> TryResolveCoverForFallbackAsync(
        string fallback,
        CoverFallbackRequest request,
        ISpotifyIdResolver? spotifyIdResolver,
        ISpotifyArtworkResolver? spotifyArtworkResolver,
        CancellationToken cancellationToken)
    {
        if (fallback == DeezerEngine)
        {
            return string.IsNullOrWhiteSpace(request.FallbackCover) ? null : request.FallbackCover;
        }

        if (fallback == "apple")
        {
            return await TryResolveAppleCoverAsync(request, cancellationToken);
        }

        if (fallback == "spotify")
        {
            return await TryResolveSpotifyCoverByFallbackAsync(
                request,
                spotifyIdResolver,
                spotifyArtworkResolver,
                cancellationToken);
        }

        return null;
    }

    private async Task<string?> TryResolveAppleCoverAsync(CoverFallbackRequest request, CancellationToken cancellationToken)
    {
        if (!request.AllowsJpegCover)
        {
            return null;
        }

        var appleCover = await ArtworkFallbackHelper.TryResolveAppleCoverAsync(
            request.AppleCatalog,
            request.HttpClientFactory,
            new ArtworkFallbackHelper.AppleCoverLookupRequest(
                _settings,
                request.AppleId,
                request.AppleTitle,
                request.AppleArtist,
                request.AppleAlbum),
            _logger,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(appleCover))
        {
            return null;
        }

        var dims = AppleQueueHelpers.GetAppleArtworkDimensions(_settings);
        return AppleQueueHelpers.BuildAppleArtworkUrl(
            appleCover,
            dims.SizeText,
            75,
            75,
            AppleQueueHelpers.GetAppleArtworkFormat(_settings));
    }

    private async Task<string?> TryResolveSpotifyCoverByFallbackAsync(
        CoverFallbackRequest request,
        ISpotifyIdResolver? spotifyIdResolver,
        ISpotifyArtworkResolver? spotifyArtworkResolver,
        CancellationToken cancellationToken)
    {
        if (!request.AllowsJpegCover)
        {
            return null;
        }

        return await ResolveSpotifyCoverAsync(
            request.Track,
            request.Album,
            spotifyIdResolver,
            spotifyArtworkResolver,
            cancellationToken);
    }

    private async Task<string?> ResolveSpotifyCoverAsync(
        Track? track,
        CoreAlbum? album,
        ISpotifyIdResolver? spotifyIdResolver,
        ISpotifyArtworkResolver? spotifyArtworkResolver,
        CancellationToken cancellationToken)
    {
        if (track == null || spotifyIdResolver == null || spotifyArtworkResolver == null)
        {
            return null;
        }

        var spotifyId = await spotifyIdResolver.ResolveTrackIdAsync(
            track.Title ?? string.Empty,
            track.MainArtist?.Name ?? string.Empty,
            album?.Title,
            track.ISRC,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return null;
        }

        var spotifyCover = await spotifyArtworkResolver.ResolveAlbumCoverUrlAsync(spotifyId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(spotifyCover))
        {
            _logger.LogInformation("Spotify cover selected for queue item: {Url}", spotifyCover);
        }

        return spotifyCover;
    }

    private static int? MapBitrateToQualityRank(int bitrate)
    {
        return bitrate switch
        {
            9 => 3,
            3 => 2,
            1 => 1,
            _ => null
        };
    }

    private static Dictionary<string, object> CreateTrackApiDict(Track track, CoreAlbum? album, CorePlaylist? playlist, int? fallbackIndex = null)
    {
        var position = track.Position ?? (fallbackIndex.HasValue ? fallbackIndex.Value + 1 : track.TrackNumber);
        var trackDict = new Dictionary<string, object>
        {
            ["id"] = track.Id ?? "",
            [TitleKey] = track.Title ?? "",
            [ArtistKey] = new Dictionary<string, object>
            {
                ["id"] = track.MainArtist?.Id ?? "0",
                ["name"] = track.MainArtist?.Name ?? UnknownValue
            },
            ["duration"] = track.Duration,
            ["explicit_lyrics"] = track.Explicit,
            ["track_position"] = track.TrackNumber,
            ["disk_number"] = track.DiscNumber,
            ["position"] = position
        };

        if (!string.IsNullOrEmpty(track.ISRC))
        {
            trackDict["isrc"] = track.ISRC;
        }

        if (track.Bpm > 0)
        {
            trackDict["bpm"] = track.Bpm;
        }

        if (!string.IsNullOrEmpty(track.ReplayGain))
        {
            trackDict["gain"] = track.ReplayGain;
        }

        if (!string.IsNullOrEmpty(track.DownloadURL))
        {
            trackDict["direct_stream_url"] = track.DownloadURL;
        }

        if (track.Contributors is { Count: > 0 })
        {
            trackDict["contributors"] = track.Contributors;
        }

        if (album != null)
        {
            trackDict["album"] = new Dictionary<string, object>
            {
                ["id"] = album.Id,
                [TitleKey] = album.Title ?? "",
                [CoverKey] = GetCoverUrl(album.Pic, CoverKey, 75),
                ["md5_image"] = album.Pic?.Md5 ?? ""
            };
        }

        if (playlist != null)
        {
            trackDict["playlist_id"] = playlist.Id;
        }

        if (track.Lyrics != null)
        {
            if (!string.IsNullOrEmpty(track.Lyrics.Sync))
            {
                trackDict["lyrics_sync"] = true;
            }

            if (!string.IsNullOrEmpty(track.Lyrics.Unsync))
            {
                trackDict["lyrics_unsync"] = true;
            }
        }

        return trackDict;
    }

    private static Dictionary<string, object> CreateAlbumApiDict(CoreAlbum album)
    {
        var artistPic = album.MainArtist?.Pic;
        return new Dictionary<string, object>
        {
            ["id"] = album.Id,
            [TitleKey] = album.Title ?? "",
            [ArtistKey] = new Dictionary<string, object>
            {
                ["id"] = album.MainArtist?.Id ?? "0",
                ["name"] = album.MainArtist?.Name ?? UnknownValue,
                ["picture_small"] = GetCoverUrl(artistPic, ArtistKey, 56),
                ["picture_medium"] = GetCoverUrl(artistPic, ArtistKey, 250),
                ["picture_big"] = GetCoverUrl(artistPic, ArtistKey, 500),
                ["picture_xl"] = GetCoverUrl(artistPic, ArtistKey, 1000)
            },
            ["cover_small"] = GetCoverUrl(album.Pic, CoverKey, 56),
            ["md5_image"] = album.Pic?.Md5 ?? "",
            ["release_date"] = album.ReleaseDate?.ToString("yyyy-MM-dd") ?? "",
            ["nb_tracks"] = album.TrackTotal,
            ["explicit_lyrics"] = album.Explicit == true,
            ["genre"] = album.Genre.FirstOrDefault() ?? "",
            ["label"] = album.Label ?? "",
            ["upc"] = album.Barcode ?? ""
        };
    }

    private static Dictionary<string, object> CreatePlaylistApiDict(CorePlaylist playlist)
    {
        return new Dictionary<string, object>
        {
            ["id"] = playlist.Id,
            [TitleKey] = playlist.Title ?? "",
            ["creator"] = new Dictionary<string, object>
            {
                ["id"] = playlist.Owner?.Id ?? "0",
                ["name"] = playlist.Owner?.Name ?? UnknownValue
            },
            ["picture_small"] = GetCoverUrl(playlist.Pic, "playlist", 56),
            ["md5_image"] = playlist.Pic?.Md5 ?? "",
            ["creation_date"] = playlist.Date.ToString(),
            ["nb_tracks"] = playlist.TrackTotal,
            ["public"] = playlist.IsPublic,
            ["collaborative"] = playlist.IsCollaborative
        };
    }

    private static string GetCoverUrl(Picture? pic, string type, int size)
    {
        if (pic == null || string.IsNullOrEmpty(pic.Md5))
        {
            return "";
        }

        if (pic.Type == "talk")
        {
            return pic.GetURL(size);
        }

        if (pic.Type == type)
        {
            return pic.GetURL(size);
        }

        var temp = new Picture(pic.Md5, type);
        return temp.GetURL(size);
    }

    private bool AllowsJpegArtwork()
    {
        var formats = (_settings.LocalArtworkFormat ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(format => format.ToLowerInvariant());
        return formats.Contains("jpg");
    }
}
