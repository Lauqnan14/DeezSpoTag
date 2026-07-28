using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Matching;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using GwTrack = DeezSpoTag.Core.Models.Deezer.GwTrack;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryRecommendationService
{
    private const string FolderContentMusic = "music";
    private const string FolderContentAtmos = "atmos";
    private const string FolderContentVideo = "video";
    private const string FolderContentPodcast = "podcast";
    public sealed class LibraryRecommendationCollaborators
    {
        public LibraryRepository Repository { get; init; } = null!;
        public ShazamRecognitionService ShazamRecognitionService { get; init; } = null!;
        public ShazamDiscoveryService ShazamDiscoveryService { get; init; } = null!;
        public DeezerClient DeezerClient { get; init; } = null!;
        public DeezerGatewayService DeezerGatewayService { get; init; } = null!;
        public ITrackIdentityResolver TrackIdentityResolver { get; init; } = null!;
        public DownloadDedupeService DedupeService { get; init; } = null!;
    }

    public const string RecommendationSource = "recommendations";
    public const string RecommendationSourceId = "daily-rotation";
    private const string DeezerSource = "deezer";
    private const string StatusMatched = "matched";
    private const string StatusMatchedNoRelated = "matched_no_related";
    private const string StatusMatchedNoDeezerResolution = "matched_no_deezer_resolution";
    private const string StatusError = "error";
    private const string StatusNoMatch = "no_match";
    private const string UnknownTitle = "Unknown";
    private const string UnknownArtist = "Unknown Artist";
    private const string UnknownAlbum = "Unknown Album";
    private const string DailyPoolCacheSource = "recommendations-daily-pool";
    private const string ExposureHistoryCacheSource = "recommendations-exposure-history";
    private const string DailyPoolSnapshotVersion = "v1";
    private const string ExposureHistorySnapshotVersion = "v1";
    private const string EmptyPoolReason = "empty_pool";
    private const string GenerationQueuedReason = "generation_queued";
    private const string BackgroundGenerationFailedReason = "background_generation_failed";
    private const string PersistFailedReason = "persist_failed";
    private const string PersistTimedOutReason = "persist_timed_out";
    private const string GenerationReasonOnDemand = "on-demand";
    private const string GenerationReasonManualRebuild = "manual-rebuild";
    private const int PersistedFailureReasonMaxLength = 240;
    private const int DeezerMetadataCacheLimit = 2048;

    private const int MaxDailyRecommendations = 50;
    private const int RecommendationPoolMultiplier = 3;
    private const int RecommendationPoolLimit = MaxDailyRecommendations * RecommendationPoolMultiplier;
    private const int DailySeedProbeLimit = 32;
    private const int DeezerSearchLimit = 12;
    private const int MaxArtistOccurrences = 2;
    private const int MaxAlbumOccurrences = 2;
    private const int ShazamRelatedPerSeed = 10;
    private const int ShazamSimilarLookupLimit = 20;
    private const double ShazamDeezerMinTitleSimilarity = 0.62d;
    private const double ShazamDeezerMinArtistSimilarity = 0.52d;
    private const int ShazamSelectedSeedLimit = 12;
    private const int ShazamBackgroundBatchSize = 120;
    private const int RecommendationExposureRetentionDays = 14;
    private static readonly TimeSpan ShazamCacheTtl = TimeSpan.FromDays(14);
    private static readonly TimeSpan RecommendationGenerationLease = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TrackMixRequestTimeout = TimeSpan.FromSeconds(8);

    private readonly LibraryRepository _repository;
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly ShazamDiscoveryService _shazamDiscoveryService;
    private readonly DeezerClient _deezerClient;
    private readonly DeezerGatewayService _deezerGatewayService;
    private readonly ITrackIdentityResolver _trackIdentityResolver;
    private readonly DownloadDedupeService _dedupeService;
    private readonly string _recommendationArtworkRootPath;
    private readonly ILogger<LibraryRecommendationService> _logger;
    private readonly CancellationToken _backgroundCancellationToken;
    private readonly ConcurrentDictionary<string, RecommendationDetailDto> _dailyPoolCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _backgroundScans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RecommendationTrackDto> _deezerRecommendationMetadataCache = new(StringComparer.Ordinal);

    public LibraryRecommendationService(
        LibraryRecommendationCollaborators collaborators,
        IWebHostEnvironment webHostEnvironment,
        ILogger<LibraryRecommendationService> logger,
        IHostApplicationLifetime? hostApplicationLifetime = null)
    {
        _repository = collaborators.Repository;
        _shazamRecognitionService = collaborators.ShazamRecognitionService;
        _shazamDiscoveryService = collaborators.ShazamDiscoveryService;
        _deezerClient = collaborators.DeezerClient;
        _deezerGatewayService = collaborators.DeezerGatewayService;
        _trackIdentityResolver = collaborators.TrackIdentityResolver;
        _dedupeService = collaborators.DedupeService;
        _recommendationArtworkRootPath = string.IsNullOrWhiteSpace(webHostEnvironment.WebRootPath)
            ? string.Empty
            : Path.Join(webHostEnvironment.WebRootPath, "images", "recommendations");
        _logger = logger;
        _backgroundCancellationToken = hostApplicationLifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    private sealed record RecommendationScope(
        long LibraryId,
        long FolderId,
        string FolderName,
        string StationId,
        string ScopeKey);
    private sealed record RecommendationArtworkCandidate(string Url, string DayKey);
    private sealed record PersistedDailyPoolDto(
        DateTimeOffset GeneratedAtUtc,
        IReadOnlyList<RecommendationTrackDto> Tracks,
        string? StationImageUrl = null);
    private sealed record RecommendationExposureHistoryDto(
        IReadOnlyList<RecommendationExposureEntryDto> Entries);
    private sealed record RecommendationExposureEntryDto(string TrackId, string Day);
    private sealed record RecommendationBuildResult(RecommendationDetailDto? Detail, IReadOnlyList<string> ReasonCodes);
    private sealed record ShazamRecommendationBuildResult(List<RecommendationTrackDto> Tracks, string EmptyReasonCode)
    {
        public static ShazamRecommendationBuildResult Empty(string reasonCode)
            => new(new List<RecommendationTrackDto>(), reasonCode);
    }

    private sealed record ResolvedRecommendationSeed(LibraryRecommendationSeedTrackDto LocalTrack, string DeezerTrackId);
    private sealed class RecommendationAccumulator
    {
        public RecommendationAccumulator(int limit)
        {
            Limit = limit;
            DestinationTracks = new List<RecommendationTrackDto>(limit);
            OverflowTracks = new List<RecommendationTrackDto>(limit);
            SeenRecommendationIds = new HashSet<string>(StringComparer.Ordinal);
            ArtistCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            AlbumCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        public int Limit { get; }
        public List<RecommendationTrackDto> DestinationTracks { get; }
        public List<RecommendationTrackDto> OverflowTracks { get; }
        public HashSet<string> SeenRecommendationIds { get; }
        public Dictionary<string, int> ArtistCounts { get; }
        public Dictionary<string, int> AlbumCounts { get; }
        public int FailedSeedLoads { get; set; }
    }
    private sealed record PersistDailyPoolResult(bool Success, string? ReasonCode = null)
    {
        public static PersistDailyPoolResult Ok { get; } = new(true);
        public static PersistDailyPoolResult Failed(string reasonCode) => new(false, reasonCode);
    }

    private async Task<IReadOnlyList<FolderDto>> GetRecommendationEligibleFoldersAsync(CancellationToken cancellationToken)
    {
        var folders = await _repository.GetFoldersAsync(cancellationToken);
        return folders
            .Where(folder => folder.Enabled
                             && folder.LibraryId.HasValue
                             && folder.LibraryId.Value > 0
                             && !IsExcludedFromRecommendations(folder))
            .ToList();
    }

    private static List<FolderDto> FilterScopedFolders(
        IReadOnlyList<FolderDto> folders,
        long libraryId,
        long? folderId)
    {
        var filtered = folders
            .Where(folder => folder.LibraryId.HasValue && folder.LibraryId.Value == libraryId)
            .ToList();

        if (folderId.HasValue)
        {
            filtered = filtered
                .Where(folder => folder.Id == folderId.Value)
                .ToList();
        }

        return filtered;
    }

    private async Task<IReadOnlyList<FolderDto>> GetScopedFoldersAsync(
        long libraryId,
        long? folderId,
        CancellationToken cancellationToken)
    {
        var folders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        return FilterScopedFolders(folders, libraryId, folderId);
    }

    private static bool IsExcludedFromRecommendations(FolderDto folder)
    {
        var mode = ResolveFolderContentType(folder);
        return mode == FolderContentAtmos
            || mode == FolderContentVideo
            || mode == FolderContentPodcast;
    }

    private static string ResolveFolderContentType(FolderDto folder)
    {
        var normalized = (folder.DesiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return FolderContentMusic;
        }

        if (normalized.Contains("atmos", StringComparison.Ordinal))
        {
            return FolderContentAtmos;
        }

        if (normalized.Contains("video", StringComparison.Ordinal))
        {
            return FolderContentVideo;
        }

        if (normalized.Contains("podcast", StringComparison.Ordinal))
        {
            return FolderContentPodcast;
        }

        // Legacy numeric quality rank for Atmos.
        if (normalized == "5")
        {
            return FolderContentAtmos;
        }

        // Legacy destination mode "0" historically mapped to mixed content.
        // Default it to music unless the folder naming strongly indicates video/podcast.
        if (normalized == "0")
        {
            var folderDescriptor = $"{folder.DisplayName} {folder.RootPath}".ToLowerInvariant();
            if (folderDescriptor.Contains("video", StringComparison.Ordinal))
            {
                return FolderContentVideo;
            }

            if (folderDescriptor.Contains("podcast", StringComparison.Ordinal))
            {
                return FolderContentPodcast;
            }

            return FolderContentMusic;
        }

        return FolderContentMusic;
    }

    private static string? ResolveRecommendationArtworkUrl(
        string stationId,
        IReadOnlyDictionary<string, string> artworkAssignments)
    {
        if (string.IsNullOrWhiteSpace(stationId) || artworkAssignments.Count == 0)
        {
            return null;
        }

        return artworkAssignments.TryGetValue(stationId, out var imageUrl)
            ? imageUrl
            : null;
    }

    private Dictionary<string, string> BuildRecommendationArtworkAssignments(
        IReadOnlyList<FolderDto> folders,
        DateTimeOffset nowLocal)
    {
        if (folders.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var windowCandidates = GetRecommendationArtworkCandidatesForWindow(nowLocal);
        if (windowCandidates.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var orderedCandidates = windowCandidates
            .OrderBy(candidate => ComputeStableHash($"{nowLocal:yyyyMMdd}|{candidate.Url}"))
            .ThenBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orderedCandidates.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var stationIds = folders
            .Where(folder => folder.LibraryId.HasValue && folder.LibraryId.Value > 0)
            .Select(folder => BuildStationId(folder.LibraryId!.Value, folder.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var assignments = new Dictionary<string, string>(stationIds.Count, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < stationIds.Count; index++)
        {
            assignments[stationIds[index]] = orderedCandidates[index % orderedCandidates.Count].Url;
        }

        return assignments;
    }

    private IReadOnlyList<RecommendationArtworkCandidate> GetRecommendationArtworkCandidatesForWindow(DateTimeOffset nowLocal)
    {
        var candidates = GetRecommendationArtworkCandidates();
        if (candidates.Count == 0)
        {
            return Array.Empty<RecommendationArtworkCandidate>();
        }

        var dayKey = nowLocal.DayOfWeek.ToString().ToLowerInvariant();
        var dayCandidates = candidates
            .Where(candidate => string.Equals(candidate.DayKey, dayKey, StringComparison.Ordinal))
            .ToList();

        return dayCandidates.Count > 0
            ? dayCandidates
            : candidates.ToList();
    }

    private IReadOnlyList<RecommendationArtworkCandidate> GetRecommendationArtworkCandidates()
    {
        if (string.IsNullOrWhiteSpace(_recommendationArtworkRootPath)
            || !Directory.Exists(_recommendationArtworkRootPath))
        {
            return Array.Empty<RecommendationArtworkCandidate>();
        }

        var results = new List<RecommendationArtworkCandidate>();
        foreach (var filePath in Directory.EnumerateFiles(_recommendationArtworkRootPath, "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(filePath);
            if (!IsSupportedRecommendationArtworkExtension(extension))
            {
                continue;
            }

            var dayKey = NormalizeRecommendationArtworkDay(Path.GetFileNameWithoutExtension(filePath));
            if (string.IsNullOrWhiteSpace(dayKey))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(_recommendationArtworkRootPath, filePath);
            var urlPath = relativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            results.Add(new RecommendationArtworkCandidate($"/images/recommendations/{urlPath}", dayKey));
        }

        return results;
    }

    private static bool IsSupportedRecommendationArtworkExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeRecommendationArtworkDay(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "monday" => "monday",
            "tuesday" => "tuesday",
            "tueday" => "tuesday",
            "wednesday" => "wednesday",
            "thursday" => "thursday",
            "friday" => "friday",
            "saturday" => "saturday",
            "sunday" => "sunday",
            _ => null
        };
    }

    private static RecommendationScope? ResolveScope(
        long libraryId,
        IReadOnlyList<FolderDto> folders,
        string? stationId,
        long? folderId)
    {
        if (folders.Count == 0)
        {
            return null;
        }

        if (TryParseStationId(stationId, out var stationLibraryId, out var stationFolderId))
        {
            if (stationLibraryId != libraryId)
            {
                return null;
            }

            var stationFolder = folders.FirstOrDefault(folder => folder.Id == stationFolderId);
            if (stationFolder is null)
            {
                return null;
            }

            return BuildScope(libraryId, stationFolder);
        }

        if (folderId.HasValue)
        {
            var selectedFolder = folders.FirstOrDefault(folder => folder.Id == folderId.Value);
            if (selectedFolder is null)
            {
                return null;
            }

            return BuildScope(libraryId, selectedFolder);
        }

        var firstFolder = folders
            .OrderBy(folder => folder.DisplayName, StringComparer.OrdinalIgnoreCase)
            .First();
        return BuildScope(libraryId, firstFolder);
    }

    private static RecommendationScope BuildScope(long libraryId, FolderDto folder)
    {
        var stationId = BuildStationId(libraryId, folder.Id);
        return new RecommendationScope(
            libraryId,
            folder.Id,
            folder.DisplayName,
            stationId,
            stationId);
    }

    private static string BuildStationId(long libraryId, long folderId)
        => $"daily-rotation:l{libraryId}:f{folderId}";

    private static bool TryParseStationId(string? stationId, out long libraryId, out long folderId)
    {
        libraryId = 0;
        folderId = 0;

        if (string.IsNullOrWhiteSpace(stationId))
        {
            return false;
        }

        var value = stationId.Trim();
        if (!value.StartsWith("daily-rotation:l", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        var libPart = parts[1];
        var folderPart = parts[2];
        if (!libPart.StartsWith("l", StringComparison.OrdinalIgnoreCase)
            || !folderPart.StartsWith("f", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(libPart[1..], out libraryId)
            && long.TryParse(folderPart[1..], out folderId);
    }

    public async Task<IReadOnlyList<RecommendationStationDto>> GetStationsAsync(
        long libraryId,
        long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || !_repository.IsConfigured)
        {
            return Array.Empty<RecommendationStationDto>();
        }

        var allRecommendationFolders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        var folders = FilterScopedFolders(allRecommendationFolders, libraryId, folderId);
        if (folders.Count == 0)
        {
            return Array.Empty<RecommendationStationDto>();
        }

        var nowLocal = DateTimeOffset.Now;
        var artworkAssignments = BuildRecommendationArtworkAssignments(allRecommendationFolders, nowLocal);
        var stations = new List<RecommendationStationDto>(folders.Count);
        foreach (var scope in folders
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(folder => BuildScope(libraryId, folder)))
        {
            var stationId = scope.StationId;

            var imageUrl = ResolveRecommendationArtworkUrl(stationId, artworkAssignments);
            var dayLocal = DateOnly.FromDateTime(nowLocal.DateTime);
            var cacheKey = BuildDailyCacheKey(scope.ScopeKey, dayLocal);
            var persistedPool = await GetDailyPoolAsync(
                cacheKey,
                scope,
                dayLocal,
                imageUrl,
                cancellationToken);
            if (persistedPool is not null)
            {
                stations.Add(persistedPool.Station with
                {
                    TrackCount = Math.Min(MaxDailyRecommendations, persistedPool.Tracks.Count),
                    Status = persistedPool.Tracks.Count > 0 ? "ready" : "empty",
                    ReasonCodes = Array.Empty<string>(),
                    Message = null
                });
                continue;
            }

            var missingDetail = await CreateMissingDailyPoolResponseAsync(
                scope,
                imageUrl,
                dayLocal,
                allRecommendationFolders,
                artworkAssignments,
                cancellationToken);
            stations.Add(missingDetail.Station);
        }

        return stations;
    }

    public async Task<RecommendationDetailDto?> GetRecommendationsAsync(
        long libraryId,
        string? stationId = null,
        long? folderId = null,
        int limit = MaxDailyRecommendations,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || !_repository.IsConfigured)
        {
            return null;
        }

        var allRecommendationFolders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        var folders = FilterScopedFolders(allRecommendationFolders, libraryId, folderId);
        var scope = ResolveScope(libraryId, folders, stationId, folderId);
        if (scope is null)
        {
            return null;
        }

        var nowLocal = DateTimeOffset.Now;
        var artworkAssignments = BuildRecommendationArtworkAssignments(allRecommendationFolders, nowLocal);
        var stationImageUrl = ResolveRecommendationArtworkUrl(scope.StationId, artworkAssignments);

        var cappedLimit = Math.Clamp(limit, 1, MaxDailyRecommendations);
        var dayLocal = DateOnly.FromDateTime(nowLocal.DateTime);
        PruneOldCache(dayLocal);

        var cacheKey = BuildDailyCacheKey(scope.ScopeKey, dayLocal);
        var basePool = await GetDailyPoolAsync(
            cacheKey,
            scope,
            dayLocal,
            stationImageUrl,
            cancellationToken);
        if (basePool == null)
        {
            return await CreateMissingDailyPoolResponseAsync(
                scope,
                stationImageUrl,
                dayLocal,
                allRecommendationFolders,
                artworkAssignments,
                cancellationToken);
        }

        var ignoredTrackIds = await _repository.GetPlaylistWatchIgnoredTrackIdsAsync(
            RecommendationSource,
            scope.StationId,
            cancellationToken);
        var rejectedTrackIds = await _repository.GetRecommendationRejectedTrackIdsAsync(
            scope.LibraryId,
            scope.FolderId,
            scope.StationId,
            cancellationToken);
        var excludedTrackIds = BuildNormalizedRecommendationIdSet(ignoredTrackIds);
        excludedTrackIds.UnionWith(BuildNormalizedRecommendationIdSet(rejectedTrackIds));

        var visibleTracks = BuildVisibleDailySelection(
            basePool.Tracks,
            excludedTrackIds,
            cappedLimit,
            dayLocal);
        IReadOnlyList<RecommendationTrackDto> enriched;
        try
        {
            enriched = await EnrichRecommendationMetadataAsync(visibleTracks, cancellationToken);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to enrich recommendation metadata for station {StationId}. Returning base recommendation tracks.",
                scope.StationId);
            enriched = visibleTracks;
        }

        if (enriched.Count < cappedLimit && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Recommendation result underfilled for station {StationId}: requested={Requested}, returned={Returned}, dailySelection={DailySelection}, ignored={Ignored}.",
                scope.StationId,
                cappedLimit,
                enriched.Count,
                Math.Min(cappedLimit, basePool.Tracks.Count),
                excludedTrackIds.Count);
        }

        var imageUrl = stationImageUrl
            ?? enriched
                .Select(track => track.Album?.CoverMedium)
                .FirstOrDefault(cover => !string.IsNullOrWhiteSpace(cover))
            ?? basePool.Station.ImageUrl;

        return new RecommendationDetailDto(
            basePool.Station with
            {
                TrackCount = enriched.Count,
                ImageUrl = imageUrl,
                Status = enriched.Count > 0 ? "ready" : "empty",
                ReasonCodes = enriched.Count > 0 ? Array.Empty<string>() : ["all_candidates_rejected_or_ignored"],
                Message = enriched.Count > 0 ? null : "No recommendation tracks are available after user rejections and ignores."
            },
            enriched,
            basePool.GeneratedAtUtc,
            enriched.Count > 0 ? "ready" : "empty",
            enriched.Count > 0 ? Array.Empty<string>() : ["all_candidates_rejected_or_ignored"],
            enriched.Count > 0 ? null : "No recommendation tracks are available after user rejections and ignores.");
    }

    private async Task<RecommendationDetailDto> CreateMissingDailyPoolResponseAsync(
        RecommendationScope scope,
        string? stationImageUrl,
        DateOnly dayLocal,
        IReadOnlyList<FolderDto> allRecommendationFolders,
        IReadOnlyDictionary<string, string> artworkAssignments,
        CancellationToken cancellationToken)
    {
        var state = await _repository.GetRecommendationGenerationStateAsync(
            scope.LibraryId,
            scope.FolderId,
            dayLocal,
            cancellationToken);
        var stateReasons = ResolveGenerationStateReasonCodes(state);
        if (stateReasons.Length > 0)
        {
            return CreateUnavailableRecommendationDetail(scope, stationImageUrl, dayLocal, stateReasons);
        }

        var libraryTrackIds = await _repository.GetTrackIdsForLibraryScopeAsync(
            scope.LibraryId,
            scope.FolderId,
            cancellationToken);
        if (libraryTrackIds.Count == 0)
        {
            return CreateUnavailableRecommendationDetail(scope, stationImageUrl, dayLocal, ["no_library_tracks"]);
        }

        await QueueDailyPoolGenerationAsync(
            scope,
            dayLocal,
            allRecommendationFolders,
            artworkAssignments,
            string.Equals(state?.Status, "completed", StringComparison.OrdinalIgnoreCase),
            cancellationToken);
        return CreateUnavailableRecommendationDetail(scope, stationImageUrl, dayLocal, [GenerationQueuedReason]);
    }

    private async Task QueueDailyPoolGenerationAsync(
        RecommendationScope scope,
        DateOnly dayLocal,
        IReadOnlyList<FolderDto> allRecommendationFolders,
        IReadOnlyDictionary<string, string> artworkAssignments,
        bool forceReset,
        CancellationToken cancellationToken)
    {
        await _repository.RequestRecommendationGenerationAsync(
            BuildGenerationStateKey(scope, dayLocal),
            GenerationReasonOnDemand,
            forceReset,
            cancellationToken);
        if (_shazamRecognitionService.IsAvailable)
        {
            StartBackgroundShazamRefresh(scope, explicitTrackIds: null);
        }

        StartBackgroundDailyPoolGeneration(
            scope,
            dayLocal,
            allRecommendationFolders,
            artworkAssignments,
            GenerationReasonOnDemand);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Recommendation generation queued for scope {ScopeKey} ({DayLocal}) from on-demand request.",
                scope.ScopeKey,
                dayLocal);
        }
    }

    private async Task<RecommendationDetailDto?> GetDailyPoolAsync(
        string cacheKey,
        RecommendationScope scope,
        DateOnly dayLocal,
        string? stationImageUrl,
        CancellationToken cancellationToken)
    {
        if (_dailyPoolCache.TryGetValue(cacheKey, out var cachedPool))
        {
            return cachedPool;
        }

        var basePool = await TryLoadPersistedDailyPoolAsync(scope, dayLocal, stationImageUrl, cancellationToken);
        if (basePool != null)
        {
            _dailyPoolCache[cacheKey] = basePool;
        }

        return basePool;
    }

    private static RecommendationDetailDto CreateUnavailableRecommendationDetail(
        RecommendationScope scope,
        string? stationImageUrl,
        DateOnly dayLocal,
        string[] reasonCodes)
    {
        var reasons = reasonCodes.Length > 0
            ? reasonCodes
            : [EmptyPoolReason];
        var message = BuildRecommendationUnavailableMessage(reasons);
        var isGenerating = reasonCodes.Contains(GenerationQueuedReason, StringComparer.Ordinal);
        var station = new RecommendationStationDto(
            scope.StationId,
            $"Recommendations - {scope.FolderName}",
            BuildDailyRecommendationDescription(scope.FolderName, dayLocal.DayOfWeek),
            RecommendationSourceId,
            scope.FolderName,
            0,
            stationImageUrl,
            isGenerating ? "generating" : "unavailable",
            reasons,
            message);

        return new RecommendationDetailDto(
            station,
            Array.Empty<RecommendationTrackDto>(),
            DateTimeOffset.UtcNow,
            isGenerating ? "generating" : "unavailable",
            reasons,
            message);
    }

    private static string BuildRecommendationUnavailableMessage(IReadOnlyList<string> reasonCodes)
    {
        if (reasonCodes.Contains(GenerationQueuedReason, StringComparer.Ordinal))
        {
            return "Recommendation generation is running in the background. Refresh this tracklist shortly.";
        }

        if (reasonCodes.Contains(PersistFailedReason, StringComparer.Ordinal)
            || reasonCodes.Contains(PersistTimedOutReason, StringComparer.Ordinal))
        {
            return "Recommendation generation completed but failed to save. Try regenerating the station.";
        }

        if (reasonCodes.Contains(BackgroundGenerationFailedReason, StringComparer.Ordinal))
        {
            return "Recommendation generation failed in the background. Try regenerating the station.";
        }

        if (reasonCodes.Contains("no_seed_tracks", StringComparer.Ordinal)
            || reasonCodes.Contains("no_library_tracks", StringComparer.Ordinal))
        {
            return "No library tracks are available to seed recommendations.";
        }

        if (reasonCodes.Contains("deezer_seed_resolution_failed", StringComparer.Ordinal))
        {
            return "No local tracks could be identified on Deezer for recommendation seeding.";
        }

        if (reasonCodes.Contains("dedupe_removed_all", StringComparer.Ordinal))
        {
            return "All generated recommendation tracks already exist in the library or queue.";
        }

        if (reasonCodes.Contains("all_candidates_rejected_or_ignored", StringComparer.Ordinal))
        {
            return "No recommendation tracks are available after user rejections and ignores.";
        }

        return "No recommendations are available for this station today.";
    }

    private static RecommendationGenerationStateKey BuildGenerationStateKey(
        RecommendationScope scope,
        DateOnly dayLocal)
        => new(scope.LibraryId, scope.FolderId, scope.StationId, dayLocal);

    private static string[] ResolveGenerationStateReasonCodes(RecommendationGenerationStateDto? state)
    {
        if (state is null)
        {
            return Array.Empty<string>();
        }

        if (string.Equals(state.Status, "pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            return [GenerationQueuedReason];
        }

        if (string.Equals(state.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return [string.IsNullOrWhiteSpace(state.ReasonCode) ? BackgroundGenerationFailedReason : state.ReasonCode];
        }

        return Array.Empty<string>();
    }

    private void StartBackgroundDailyPoolGeneration(
        RecommendationScope requestedScope,
        DateOnly dayLocal,
        IReadOnlyList<FolderDto> allRecommendationFolders,
        IReadOnlyDictionary<string, string> artworkAssignments,
        string reasonCode)
    {
        _ = RunBackgroundDailyPoolGenerationAsync(
            dayLocal,
            allRecommendationFolders,
            artworkAssignments,
            requestedScope.ScopeKey,
            reasonCode);
    }

    private async Task RunBackgroundDailyPoolGenerationAsync(
        DateOnly dayLocal,
        IReadOnlyList<FolderDto> allRecommendationFolders,
        IReadOnlyDictionary<string, string> artworkAssignments,
        string requestedScopeKey,
        string reasonCode)
    {
        try
        {
            foreach (var folder in OrderDailyPoolFolders(allRecommendationFolders, requestedScopeKey))
            {
                _backgroundCancellationToken.ThrowIfCancellationRequested();
                var scope = BuildScope(folder.LibraryId!.Value, folder);
                var stationImageUrl = ResolveRecommendationArtworkUrl(scope.StationId, artworkAssignments);
                await RunDailyRecommendationGenerationAsync(
                    scope,
                    dayLocal,
                    stationImageUrl,
                    reasonCode,
                    forceReset: false,
                    _backgroundCancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Background recommendation generation failed before all scopes could be processed.");
        }
    }

    private static IOrderedEnumerable<FolderDto> OrderDailyPoolFolders(
        IReadOnlyList<FolderDto> allRecommendationFolders,
        string requestedScopeKey)
        => allRecommendationFolders
            .Where(folder => folder.LibraryId.HasValue && folder.LibraryId.Value > 0)
            .OrderBy(folder => string.Equals(BuildScope(folder.LibraryId!.Value, folder).ScopeKey, requestedScopeKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(folder => folder.LibraryId!.Value)
            .ThenBy(folder => folder.DisplayName, StringComparer.OrdinalIgnoreCase);

    public async Task RefreshDailyRecommendationsAsync(
        string reasonCode = "scheduled",
        CancellationToken cancellationToken = default)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        var allRecommendationFolders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        if (allRecommendationFolders.Count == 0)
        {
            return;
        }

        var nowLocal = DateTimeOffset.Now;
        var dayLocal = DateOnly.FromDateTime(nowLocal.DateTime);
        var artworkAssignments = BuildRecommendationArtworkAssignments(allRecommendationFolders, nowLocal);

        PruneOldCache(dayLocal);

        foreach (var folder in OrderRefreshRecommendationFolders(allRecommendationFolders))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshDailyRecommendationFolderAsync(folder, dayLocal, artworkAssignments, reasonCode, cancellationToken);
        }
    }

    private static IOrderedEnumerable<FolderDto> OrderRefreshRecommendationFolders(IReadOnlyList<FolderDto> folders)
        => folders
            .Where(folder => folder.LibraryId.HasValue && folder.LibraryId.Value > 0)
            .OrderBy(folder => folder.LibraryId!.Value)
            .ThenBy(folder => folder.DisplayName, StringComparer.OrdinalIgnoreCase);

    private async Task RefreshDailyRecommendationFolderAsync(
        FolderDto folder,
        DateOnly dayLocal,
        IReadOnlyDictionary<string, string> artworkAssignments,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var scope = BuildScope(folder.LibraryId!.Value, folder);
        try
        {
            await RefreshDailyRecommendationScopeAsync(scope, dayLocal, artworkAssignments, reasonCode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Daily recommendation refresh timed out for library {LibraryId}, folder {FolderId}.",
                scope.LibraryId,
                scope.FolderId);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to refresh daily recommendations for library {LibraryId}, folder {FolderId}.",
                scope.LibraryId,
                scope.FolderId);
        }
    }

    private async Task RefreshDailyRecommendationScopeAsync(
        RecommendationScope scope,
        DateOnly dayLocal,
        IReadOnlyDictionary<string, string> artworkAssignments,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var stationImageUrl = ResolveRecommendationArtworkUrl(scope.StationId, artworkAssignments);
        var cacheKey = BuildDailyCacheKey(scope.ScopeKey, dayLocal);
        var existingPool = await TryLoadPersistedDailyPoolAsync(scope, dayLocal, stationImageUrl, cancellationToken);
        if (existingPool is not null)
        {
            _dailyPoolCache[cacheKey] = existingPool;
            await _repository.CompleteRecommendationGenerationAsync(
                BuildGenerationStateKey(scope, dayLocal),
                cancellationToken);
            return;
        }

        await RunDailyRecommendationGenerationAsync(
            scope,
            dayLocal,
            stationImageUrl,
            reasonCode,
            forceReset: false,
            cancellationToken);
    }

    private async Task<bool> RunDailyRecommendationGenerationAsync(
        RecommendationScope scope,
        DateOnly dayLocal,
        string? stationImageUrl,
        string reasonCode,
        bool forceReset,
        CancellationToken cancellationToken)
    {
        var stateKey = BuildGenerationStateKey(scope, dayLocal);
        if (forceReset)
        {
            await _repository.RequestRecommendationGenerationAsync(
                stateKey,
                reasonCode,
                forceReset: true,
                cancellationToken);
        }

        var runningExpiresBeforeUtc = DateTimeOffset.UtcNow - RecommendationGenerationLease;
        if (!await _repository.TryStartRecommendationGenerationAsync(
                stateKey,
                reasonCode,
                runningExpiresBeforeUtc,
                cancellationToken))
        {
            return false;
        }

        var cacheKey = BuildDailyCacheKey(scope.ScopeKey, dayLocal);
        var existingPool = await TryLoadPersistedDailyPoolAsync(scope, dayLocal, stationImageUrl, cancellationToken);
        if (existingPool is not null && !forceReset)
        {
            _dailyPoolCache[cacheKey] = existingPool;
            await _repository.CompleteRecommendationGenerationAsync(stateKey, cancellationToken);
            return true;
        }

        try
        {
            if (forceReset)
            {
                _dailyPoolCache.TryRemove(cacheKey, out _);
                await _repository.DeletePlaylistTrackCandidateCacheAsync(
                    DailyPoolCacheSource,
                    scope.ScopeKey,
                    cancellationToken);
            }

            var dailyPool = await BuildDailyPoolAsync(scope, dayLocal, stationImageUrl, cancellationToken);
            if (dailyPool.Detail is null)
            {
                await FailRecommendationGenerationAsync(stateKey, dailyPool.ReasonCodes, cancellationToken);
                return false;
            }

            var persistResult = await PersistDailyPoolAsync(scope, dayLocal, dailyPool.Detail, cancellationToken);
            if (!persistResult.Success)
            {
                await FailRecommendationGenerationAsync(
                    stateKey,
                    [persistResult.ReasonCode ?? PersistFailedReason],
                    cancellationToken);
                return false;
            }

            _dailyPoolCache[cacheKey] = dailyPool.Detail;
            await _repository.CompleteRecommendationGenerationAsync(stateKey, cancellationToken);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Recommendation pool generated for scope {ScopeKey} ({DayLocal}).",
                    scope.ScopeKey,
                    dayLocal);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            await _repository.FailRecommendationGenerationAsync(
                stateKey,
                BackgroundGenerationFailedReason,
                ex.Message,
                CancellationToken.None);
            _logger.LogWarning(
                ex,
                "Recommendation generation timed out for scope {ScopeKey}.",
                scope.ScopeKey);
            return false;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            await _repository.FailRecommendationGenerationAsync(
                stateKey,
                BackgroundGenerationFailedReason,
                ex.Message,
                CancellationToken.None);
            _logger.LogWarning(
                ex,
                "Recommendation generation failed for scope {ScopeKey}.",
                scope.ScopeKey);
            return false;
        }
    }

    private async Task FailRecommendationGenerationAsync(
        RecommendationGenerationStateKey stateKey,
        IReadOnlyList<string> reasonCodes,
        CancellationToken cancellationToken)
    {
        var reasons = reasonCodes.Count > 0
            ? reasonCodes
            : [EmptyPoolReason];
        await _repository.FailRecommendationGenerationAsync(
            stateKey,
            reasons[0],
            string.Join(", ", reasons),
            cancellationToken);
    }

    public async Task<bool> TriggerFullLibraryShazamScanAsync(
        long libraryId,
        long? folderId,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || !_repository.IsConfigured || !_shazamRecognitionService.IsAvailable)
        {
            return false;
        }

        var folders = await GetScopedFoldersAsync(libraryId, folderId, cancellationToken);
        if (folders.Count == 0)
        {
            return false;
        }

        var scope = ResolveScope(libraryId, folders, null, folderId);
        if (scope is null)
        {
            return false;
        }

        if (force)
        {
            var allTrackIds = await _repository.GetTrackIdsForLibraryScopeAsync(libraryId, scope.FolderId, cancellationToken);
            if (allTrackIds.Count == 0)
            {
                return false;
            }

            return StartBackgroundShazamRefresh(scope, allTrackIds);
        }

        return StartBackgroundShazamRefresh(scope, null);
    }

    public async Task<LibraryShazamScanStatusDto?> GetShazamScanStatusAsync(
        long libraryId,
        long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || !_repository.IsConfigured)
        {
            return null;
        }

        var folders = await GetScopedFoldersAsync(libraryId, folderId, cancellationToken);
        var scope = ResolveScope(libraryId, folders, null, folderId);
        if (scope is null)
        {
            return null;
        }

        var trackIds = await _repository.GetTrackIdsForLibraryScopeAsync(libraryId, scope.FolderId, cancellationToken);
        if (trackIds.Count == 0)
        {
            return new LibraryShazamScanStatusDto(
                libraryId,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                _backgroundScans.ContainsKey(scope.ScopeKey));
        }

        var cacheByTrackId = await _repository.GetShazamTrackCacheByTrackIdForLibraryAsync(
            libraryId,
            scope.FolderId,
            cancellationToken);
        var cachedTracks = 0;
        var matchedTracks = 0;
        var noMatchTracks = 0;
        var errorTracks = 0;
        DateTimeOffset? lastScannedAtUtc = null;

        foreach (var trackId in trackIds)
        {
            if (!cacheByTrackId.TryGetValue(trackId, out var cache))
            {
                continue;
            }

            cachedTracks++;
            if (cache.ScannedAtUtc.HasValue
                && (!lastScannedAtUtc.HasValue || cache.ScannedAtUtc.Value > lastScannedAtUtc.Value))
            {
                lastScannedAtUtc = cache.ScannedAtUtc.Value;
            }

            if (string.Equals(cache.Status, StatusMatched, StringComparison.OrdinalIgnoreCase))
            {
                matchedTracks++;
            }
            else if (string.Equals(cache.Status, "no_match", StringComparison.OrdinalIgnoreCase))
            {
                noMatchTracks++;
            }
            else if (string.Equals(cache.Status, StatusError, StringComparison.OrdinalIgnoreCase))
            {
                errorTracks++;
            }
        }

        var pendingTracks = Math.Max(0, trackIds.Count - cachedTracks);
        return new LibraryShazamScanStatusDto(
            libraryId,
            trackIds.Count,
            cachedTracks,
            matchedTracks,
            noMatchTracks,
            errorTracks,
            pendingTracks,
            lastScannedAtUtc,
            _backgroundScans.ContainsKey(scope.ScopeKey));
    }

    public async Task<RecommendationDetailDto?> RebuildRecommendationsAsync(
        long libraryId,
        string? stationId = null,
        long? folderId = null,
        int limit = MaxDailyRecommendations,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0 || !_repository.IsConfigured)
        {
            return null;
        }

        var allRecommendationFolders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        var folders = FilterScopedFolders(allRecommendationFolders, libraryId, folderId);
        var scope = ResolveScope(libraryId, folders, stationId, folderId);
        if (scope is null)
        {
            return null;
        }

        var nowLocal = DateTimeOffset.Now;
        var dayLocal = DateOnly.FromDateTime(nowLocal.DateTime);
        var artworkAssignments = BuildRecommendationArtworkAssignments(allRecommendationFolders, nowLocal);
        var stationImageUrl = ResolveRecommendationArtworkUrl(scope.StationId, artworkAssignments);

        var cacheKey = BuildDailyCacheKey(scope.ScopeKey, dayLocal);
        _dailyPoolCache.TryRemove(cacheKey, out _);
        var generated = await RunDailyRecommendationGenerationAsync(
            scope,
            dayLocal,
            stationImageUrl,
            GenerationReasonManualRebuild,
            forceReset: true,
            cancellationToken);
        if (!generated)
        {
            var state = await _repository.GetRecommendationGenerationStateAsync(
                scope.LibraryId,
                scope.FolderId,
                dayLocal,
                cancellationToken);
            var reasons = ResolveGenerationStateReasonCodes(state);
            return CreateUnavailableRecommendationDetail(
                scope,
                stationImageUrl,
                dayLocal,
                reasons.Length > 0 ? reasons : [GenerationQueuedReason]);
        }

        return await GetRecommendationsAsync(libraryId, scope.StationId, scope.FolderId, limit, cancellationToken);
    }

    public async Task<RecommendationDetailDto?> RejectRecommendationTrackAsync(
        RecommendationRejectionUpsertInput input,
        int limit = MaxDailyRecommendations,
        CancellationToken cancellationToken = default)
    {
        var normalizedTrackSourceId = NormalizeId(input.TrackSourceId);
        if (input.LibraryId <= 0
            || string.IsNullOrWhiteSpace(input.StationId)
            || string.IsNullOrWhiteSpace(normalizedTrackSourceId)
            || !TryParseStationId(input.StationId, out _, out _))
        {
            return null;
        }

        var allRecommendationFolders = await GetRecommendationEligibleFoldersAsync(cancellationToken);
        var folders = FilterScopedFolders(allRecommendationFolders, input.LibraryId, input.FolderId);
        var scope = ResolveScope(input.LibraryId, folders, input.StationId, input.FolderId);
        if (scope is null)
        {
            return null;
        }

        await _repository.AddRecommendationRejectionAsync(
            new RecommendationRejectionUpsertInput(
                scope.LibraryId,
                scope.FolderId,
                scope.StationId,
                normalizedTrackSourceId,
                input.Isrc,
                input.Title,
                input.Artist),
            cancellationToken);
        return await GetRecommendationsAsync(
            scope.LibraryId,
            scope.StationId,
            scope.FolderId,
            Math.Clamp(limit, 1, MaxDailyRecommendations),
            cancellationToken);
    }

    private async Task<RecommendationBuildResult> BuildDailyPoolAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        string? stationImageUrl,
        CancellationToken cancellationToken)
    {
        var reasonCodes = new List<string>();
        var orderedSeeds = await GetOrderedRecommendationSeedsAsync(
            scope,
            dayUtc,
            cancellationToken);
        if (orderedSeeds.Count == 0)
        {
            reasonCodes.Add("no_seed_tracks");
        }

        var rejectedTrackIds = await _repository.GetRecommendationRejectedTrackIdsAsync(
            scope.LibraryId,
            scope.FolderId,
            scope.StationId,
            cancellationToken);

        List<RecommendationTrackDto> deezerTracks;
        try
        {
            var deezerResult = await BuildDeezerRecommendationsAsync(
                orderedSeeds,
                RecommendationPoolLimit,
                cancellationToken);
            deezerTracks = deezerResult.Tracks
                .Select(track => NormalizeRecommendationTrack(track))
                .Where(track => !string.IsNullOrWhiteSpace(track.Id))
                .Where(track => !rejectedTrackIds.Contains(track.Id))
                .ToList();
            if (deezerResult.ResolvedSeedCount == 0)
            {
                reasonCodes.Add("deezer_seed_resolution_failed");
            }
            if (deezerTracks.Count == 0)
            {
                reasonCodes.Add("deezer_empty");
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            reasonCodes.Add("deezer_failed");
            _logger.LogWarning(
                ex,
                "Failed to build Deezer recommendations for library {LibraryId}, folder {FolderId}.",
                scope.LibraryId,
                scope.FolderId);
            deezerTracks = new List<RecommendationTrackDto>();
        }

        ShazamRecommendationBuildResult shazamResult;
        List<RecommendationTrackDto> shazamTracks;
        try
        {
            shazamResult = await BuildShazamRecommendationsAsync(
                scope,
                orderedSeeds,
                cancellationToken);
            shazamTracks = shazamResult.Tracks;
            shazamTracks = shazamTracks
                .Where(track => !rejectedTrackIds.Contains(NormalizeId(track.Id)))
                .ToList();
            if (shazamTracks.Count == 0)
            {
                reasonCodes.Add(shazamResult.EmptyReasonCode);
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            reasonCodes.Add("shazam_failed");
            _logger.LogWarning(
                ex,
                "Failed to build Shazam recommendations for library {LibraryId}, folder {FolderId}.",
                scope.LibraryId,
                scope.FolderId);
            shazamTracks = new List<RecommendationTrackDto>();
        }

        var merged = MergeRotating(
            OrderDeterministically(deezerTracks, dayUtc, DeezerSource),
            OrderDeterministically(shazamTracks, dayUtc, "shazam"),
            RecommendationPoolLimit,
            dayUtc);
        merged = await FilterRecommendationCandidatesThroughDedupeAsync(scope, merged, cancellationToken);
        merged = FilterDerivativeRecommendationCandidates(merged);
        merged = await FilterRecommendationCandidatesThroughDeezerValidationAsync(merged, cancellationToken);
        merged = await PreferFreshRecommendationCandidatesAsync(scope, merged, dayUtc, cancellationToken);
        if (merged.Count == 0)
        {
            if (deezerTracks.Count > 0 || shazamTracks.Count > 0)
            {
                reasonCodes.Add("dedupe_removed_all");
            }
            reasonCodes.Add(EmptyPoolReason);
            return new RecommendationBuildResult(null, reasonCodes.Distinct(StringComparer.Ordinal).ToList());
        }

        var station = new RecommendationStationDto(
            scope.StationId,
            $"Recommendations - {scope.FolderName}",
            BuildDailyRecommendationDescription(scope.FolderName, dayUtc.DayOfWeek),
            RecommendationSourceId,
            scope.FolderName,
            Math.Min(MaxDailyRecommendations, merged.Count),
            stationImageUrl);

        var detail = new RecommendationDetailDto(
                station,
                merged,
                DateTimeOffset.UtcNow);
        await PersistRecommendationExposureHistoryAsync(
            scope,
            dayUtc,
            detail.Tracks.Take(MaxDailyRecommendations),
            cancellationToken);

        return new RecommendationBuildResult(
            detail,
            reasonCodes.Distinct(StringComparer.Ordinal).ToList());
    }

    private async Task<List<LibraryRecommendationSeedTrackDto>> GetOrderedRecommendationSeedsAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        CancellationToken cancellationToken)
    {
        var localTracks = await _repository.GetRecommendationSeedTracksForLibraryScopeAsync(
            scope.LibraryId,
            scope.FolderId,
            cancellationToken);
        return localTracks
            .Where(HasUsableRecommendationSeedMetadata)
            .OrderBy(track => ComputeDailyScore(track.TrackId.ToString(), dayUtc))
            .ThenBy(track => track.TrackId)
            .ToList();
    }

    private static bool HasUsableRecommendationSeedMetadata(LibraryRecommendationSeedTrackDto track)
    {
        return !string.IsNullOrWhiteSpace(track.DeezerTrackId)
            || (!string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(track.Artist))
            || !string.IsNullOrWhiteSpace(track.Isrc);
    }

    private async Task<(List<RecommendationTrackDto> Tracks, int ResolvedSeedCount)> BuildDeezerRecommendationsAsync(
        IReadOnlyList<LibraryRecommendationSeedTrackDto> orderedSeeds,
        int cappedLimit,
        CancellationToken cancellationToken)
    {
        var resolvedSeeds = await ResolveDeezerRecommendationSeedsAsync(orderedSeeds, cancellationToken);
        if (resolvedSeeds.Count == 0)
        {
            return (new List<RecommendationTrackDto>(), 0);
        }

        var localDeezerIds = resolvedSeeds
            .Select(seed => NormalizeId(seed.DeezerTrackId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var accumulator = new RecommendationAccumulator(cappedLimit);
        foreach (var seed in resolvedSeeds.Take(DailySeedProbeLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var mixTracks = await LoadDeezerTrackMixAsync(seed.DeezerTrackId, cancellationToken);
                AddUniqueRecommendationTracks(mixTracks, localDeezerIds, accumulator);
                if (accumulator.DestinationTracks.Count >= cappedLimit
                    && accumulator.OverflowTracks.Count >= cappedLimit)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                accumulator.FailedSeedLoads++;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        ex,
                        "Deezer recommendation seed load failed for local track {TrackId} / Deezer {DeezerTrackId}.",
                        seed.LocalTrack.TrackId,
                        seed.DeezerTrackId);
                }
            }
        }

        AppendOverflowRecommendationTracks(accumulator, cappedLimit);
        return (accumulator.DestinationTracks, resolvedSeeds.Count);
    }

    private async Task<List<ResolvedRecommendationSeed>> ResolveDeezerRecommendationSeedsAsync(
        IReadOnlyList<LibraryRecommendationSeedTrackDto> orderedSeeds,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ResolvedRecommendationSeed>(Math.Min(orderedSeeds.Count, DailySeedProbeLimit));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in orderedSeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deezerId = await ResolveDeezerSeedTrackIdAsync(seed, cancellationToken);
            deezerId = NormalizeId(deezerId);
            if (string.IsNullOrWhiteSpace(deezerId) || !seen.Add(deezerId))
            {
                continue;
            }

            resolved.Add(new ResolvedRecommendationSeed(seed, deezerId));
            if (resolved.Count >= DailySeedProbeLimit)
            {
                break;
            }
        }

        return resolved;
    }

    private async Task<string> ResolveDeezerSeedTrackIdAsync(
        LibraryRecommendationSeedTrackDto seed,
        CancellationToken cancellationToken)
    {
        var existingId = NormalizeId(seed.DeezerTrackId);
        if (IsNumericIdentifier(existingId))
        {
            return existingId;
        }

        var isrcId = await TryResolveDeezerSeedByIsrcAsync(seed, cancellationToken);
        if (!string.IsNullOrWhiteSpace(isrcId))
        {
            await PersistResolvedDeezerSeedAsync(seed.TrackId, isrcId, cancellationToken);
            return isrcId;
        }

        var metadataId = await TryResolveDeezerSeedByMetadataAsync(seed, cancellationToken);
        if (!string.IsNullOrWhiteSpace(metadataId))
        {
            await PersistResolvedDeezerSeedAsync(seed.TrackId, metadataId, cancellationToken);
            return metadataId;
        }

        return string.Empty;
    }

    private async Task<string> TryResolveDeezerSeedByIsrcAsync(
        LibraryRecommendationSeedTrackDto seed,
        CancellationToken cancellationToken)
    {
        var isrc = NormalizeText(seed.Isrc, string.Empty);
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return string.Empty;
        }

        try
        {
            var track = await _deezerClient.GetTrackByIsrcAsync(isrc).WaitAsync(cancellationToken);
            return IsValidDeezerSeedCandidate(seed, track)
                ? NormalizeId(track.Id?.ToString())
                : string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer recommendation ISRC seed resolve failed for local track {TrackId}.", seed.TrackId);
            }
            return string.Empty;
        }
    }

    private async Task<string> TryResolveDeezerSeedByMetadataAsync(
        LibraryRecommendationSeedTrackDto seed,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seed.Title) || string.IsNullOrWhiteSpace(seed.Artist))
        {
            return string.Empty;
        }

        try
        {
            var query = $"{seed.Artist} {seed.Title}".Trim();
            var result = await _deezerClient.SearchTrackAsync(
                    query,
                    new ApiOptions { Limit = DeezerSearchLimit, Strict = true })
                .WaitAsync(cancellationToken);
            return SelectBestDeezerSeedCandidateId(result, seed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer recommendation metadata seed resolve failed for local track {TrackId}.", seed.TrackId);
            }
            return string.Empty;
        }
    }

    private static string SelectBestDeezerSeedCandidateId(
        DeezerSearchResult? result,
        LibraryRecommendationSeedTrackDto seed)
    {
        if (result?.Data == null || result.Data.Length == 0)
        {
            return string.Empty;
        }

        foreach (var item in result.Data)
        {
            if (!TryParseDeezerSearchCandidate(item, out var deezerId, out var candidate))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(deezerId) && IsValidDeezerSeedCandidate(seed, candidate))
            {
                return deezerId;
            }
        }

        return string.Empty;
    }

    private static bool IsValidDeezerSeedCandidate(LibraryRecommendationSeedTrackDto seed, ApiTrack? candidate)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Id?.ToString()))
        {
            return false;
        }

        if (HasIsrcMismatch(seed.Isrc, candidate.Isrc))
        {
            return false;
        }

        var sourceTitle = NormalizeMatchText(seed.Title);
        var candidateTitle = NormalizeMatchText(BuildCandidateTitle(candidate));
        if (!string.IsNullOrWhiteSpace(sourceTitle)
            && ComputeTokenSimilarity(sourceTitle, candidateTitle) < ShazamDeezerMinTitleSimilarity)
        {
            return false;
        }

        var sourceArtist = NormalizeMatchText(seed.Artist);
        var candidateArtist = NormalizeMatchText(candidate.Artist?.Name);
        if (!string.IsNullOrWhiteSpace(sourceArtist)
            && ComputeTokenSimilarity(sourceArtist, candidateArtist) < ShazamDeezerMinArtistSimilarity)
        {
            return false;
        }

        return !HasSeedDurationMismatch(seed.DurationMs, candidate.Duration);
    }

    private static bool HasSeedDurationMismatch(int? seedDurationMs, int candidateDurationSeconds)
    {
        if (seedDurationMs is not > 0 || candidateDurationSeconds <= 0)
        {
            return false;
        }

        var seedSeconds = (int)Math.Round(seedDurationMs.Value / 1000d);
        return Math.Abs(seedSeconds - candidateDurationSeconds) > 5;
    }

    private async Task PersistResolvedDeezerSeedAsync(
        long trackId,
        string deezerId,
        CancellationToken cancellationToken)
    {
        var normalizedId = NormalizeId(deezerId);
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return;
        }

        await _repository.UpsertTrackSourceLinkAsync(
            trackId,
            DeezerSource,
            normalizedId,
            $"https://www.deezer.com/track/{normalizedId}",
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<RecommendationTrackDto>> LoadDeezerTrackMixAsync(
        string sourceTrackId,
        CancellationToken cancellationToken)
    {
        var response = await _deezerGatewayService
            .GetContextualTrackMixAsync(new[] { sourceTrackId })
            .WaitAsync(TrackMixRequestTimeout, cancellationToken);
        if (response is null)
        {
            return Array.Empty<RecommendationTrackDto>();
        }

        var results = response["results"] as JObject ?? response;
        var data = results["data"] as JArray ?? results["DATA"] as JArray;
        if (data is null || data.Count == 0)
        {
            return Array.Empty<RecommendationTrackDto>();
        }

        var tracks = new List<RecommendationTrackDto>(data.Count);
        for (var i = 0; i < data.Count; i++)
        {
            var mapped = MapDeezerMixTrack(data[i] as JObject, i + 1);
            if (mapped is not null)
            {
                tracks.Add(mapped);
            }
        }

        return tracks;
    }

    private static RecommendationTrackDto? MapDeezerMixTrack(JObject? track, int defaultPosition)
    {
        if (track is null)
        {
            return null;
        }

        var id = GetJObjectString(track, "SNG_ID", "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var title = GetJObjectString(track, "SNG_TITLE", "title");
        var version = GetJObjectString(track, "VERSION", "title_version");
        if (!string.IsNullOrWhiteSpace(version)
            && !title.Contains(version, StringComparison.OrdinalIgnoreCase))
        {
            title = string.IsNullOrWhiteSpace(title) ? version : $"{title} {version}";
        }

        return NormalizeRecommendationTrack(new RecommendationTrackDto(
            id,
            title,
            GetJObjectInt(track, "DURATION", "duration") ?? 0,
            GetJObjectString(track, "ISRC", "isrc"),
            GetJObjectInt(track, "TRACK_NUMBER", "position", "track_position") ?? defaultPosition,
            new RecommendationArtistDto(
                GetJObjectString(track, "ART_ID", "artist_id"),
                FirstNonEmpty(GetJObjectString(track, "ART_NAME", "artist_name"), UnknownArtist) ?? UnknownArtist),
            new RecommendationAlbumDto(
                GetJObjectString(track, "ALB_ID", "album_id"),
                FirstNonEmpty(GetJObjectString(track, "ALB_TITLE", "album_title"), UnknownAlbum) ?? UnknownAlbum,
                FirstNonEmpty(
                    GetJObjectString(track, "ALB_PICTURE_MEDIUM", "album_cover_medium", "cover_medium"),
                    BuildCoverUrl(GetJObjectString(track, "ALB_PICTURE", "album_cover"))) ?? string.Empty)));
    }

    private static string GetJObjectString(JObject source, params string[] keys)
    {
        foreach (var key in keys)
        {
            var token = source[key];
            if (token is null || token.Type == JTokenType.Null)
            {
                continue;
            }

            var value = token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static int? GetJObjectInt(JObject source, params string[] keys)
    {
        foreach (var key in keys)
        {
            var token = source[key];
            if (token is null || token.Type == JTokenType.Null)
            {
                continue;
            }

            if (token.Type == JTokenType.Integer && token.Value<int>() is var number)
            {
                return number;
            }

            if (int.TryParse(token.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static void AddUniqueRecommendationTracks(
        IReadOnlyList<RecommendationTrackDto> sourceTracks,
        HashSet<string> normalizedLibraryIds,
        RecommendationAccumulator accumulator)
    {
        foreach (var track in sourceTracks)
        {
            if (accumulator.DestinationTracks.Count >= accumulator.Limit
                && accumulator.OverflowTracks.Count >= accumulator.Limit)
            {
                break;
            }

            TryAddRecommendationTrack(track, normalizedLibraryIds, accumulator);
        }
    }

    private static void TryAddRecommendationTrack(
        RecommendationTrackDto track,
        HashSet<string> normalizedLibraryIds,
        RecommendationAccumulator accumulator)
    {
        var normalizedTrackId = NormalizeId(track.Id);
        if (string.IsNullOrWhiteSpace(normalizedTrackId)
            || normalizedLibraryIds.Contains(normalizedTrackId)
            || !accumulator.SeenRecommendationIds.Add(normalizedTrackId))
        {
            return;
        }

        if (CanAddWithDiversity(track, accumulator.ArtistCounts, accumulator.AlbumCounts))
        {
            accumulator.DestinationTracks.Add(track with { TrackPosition = accumulator.DestinationTracks.Count + 1 });
            IncrementDiversityCount(GetArtistDiversityKey(track), accumulator.ArtistCounts);
            IncrementDiversityCount(GetAlbumDiversityKey(track), accumulator.AlbumCounts);
            return;
        }

        accumulator.OverflowTracks.Add(track);
    }

    private static void AppendOverflowRecommendationTracks(
        RecommendationAccumulator accumulator,
        int cappedLimit)
    {
        if (accumulator.DestinationTracks.Count >= cappedLimit || accumulator.OverflowTracks.Count == 0)
        {
            return;
        }

        foreach (var overflowTrack in accumulator.OverflowTracks)
        {
            if (accumulator.DestinationTracks.Count >= cappedLimit)
            {
                return;
            }

            accumulator.DestinationTracks.Add(overflowTrack with { TrackPosition = accumulator.DestinationTracks.Count + 1 });
        }
    }

    private static bool CanAddWithDiversity(
        RecommendationTrackDto track,
        Dictionary<string, int> artistCounts,
        Dictionary<string, int> albumCounts)
    {
        var artistKey = GetArtistDiversityKey(track);
        var albumKey = GetAlbumDiversityKey(track);
        return GetDiversityCount(artistKey, artistCounts) < MaxArtistOccurrences
               && GetDiversityCount(albumKey, albumCounts) < MaxAlbumOccurrences;
    }

    private static string GetArtistDiversityKey(RecommendationTrackDto track)
    {
        var artistId = NormalizeId(track.Artist.Id);
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            return $"artist:{artistId}";
        }

        var artistName = NormalizeText(track.Artist.Name, UnknownArtist);
        return !string.IsNullOrWhiteSpace(artistName)
               && !artistName.Equals(UnknownArtist, StringComparison.OrdinalIgnoreCase)
            ? $"artist-name:{artistName.ToLowerInvariant()}"
            : string.Empty;
    }

    private static string GetAlbumDiversityKey(RecommendationTrackDto track)
    {
        var albumId = NormalizeId(track.Album.Id);
        if (!string.IsNullOrWhiteSpace(albumId))
        {
            return $"album:{albumId}";
        }

        var albumTitle = NormalizeText(track.Album.Title, UnknownAlbum);
        return !string.IsNullOrWhiteSpace(albumTitle)
               && !albumTitle.Equals(UnknownAlbum, StringComparison.OrdinalIgnoreCase)
            ? $"album-title:{albumTitle.ToLowerInvariant()}"
            : string.Empty;
    }

    private static int GetDiversityCount(string key, Dictionary<string, int> counts)
        => string.IsNullOrWhiteSpace(key) ? 0 : counts.GetValueOrDefault(key);

    private static void IncrementDiversityCount(string key, Dictionary<string, int> counts)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            counts[key] = GetDiversityCount(key, counts) + 1;
        }
    }

    private async Task<List<RecommendationTrackDto>> FilterRecommendationCandidatesThroughDedupeAsync(
        RecommendationScope scope,
        List<RecommendationTrackDto> candidates,
        CancellationToken cancellationToken)
    {
        var accepted = new List<RecommendationTrackDto>(Math.Min(candidates.Count, RecommendationPoolLimit));
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = await _dedupeService.CheckAsync(
                BuildRecommendationDedupeRequest(scope, candidate),
                cancellationToken);
            if (!decision.Allowed)
            {
                continue;
            }

            accepted.Add(candidate with { TrackPosition = accepted.Count + 1 });
            if (accepted.Count >= RecommendationPoolLimit)
            {
                break;
            }
        }

        return accepted;
    }

    private static DownloadDedupeRequest BuildRecommendationDedupeRequest(
        RecommendationScope scope,
        RecommendationTrackDto track)
    {
        return new DownloadDedupeRequest
        {
            Isrc = track.Isrc,
            DeezerTrackId = track.Id,
            TrackTitle = track.Title,
            TrackArtist = track.Artist.Name,
            Album = track.Album.Title,
            DurationMs = track.Duration > 0 ? track.Duration * 1000 : null,
            DestinationFolderId = scope.FolderId
        };
    }

    private static List<RecommendationTrackDto> FilterDerivativeRecommendationCandidates(
        List<RecommendationTrackDto> candidates)
    {
        var accepted = new List<RecommendationTrackDto>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (IsDerivativeRecommendationCandidate(candidate))
            {
                continue;
            }

            accepted.Add(candidate with { TrackPosition = accepted.Count + 1 });
        }

        return accepted;
    }

    private static bool IsDerivativeRecommendationCandidate(RecommendationTrackDto track)
    {
        var candidate = new ApiTrack
        {
            Id = NormalizeId(track.Id),
            Title = NormalizeText(track.Title, string.Empty),
            TitleShort = NormalizeText(track.Title, string.Empty),
            TitleVersion = string.Empty,
            Isrc = NormalizeText(track.Isrc, string.Empty),
            Duration = Math.Max(0, track.Duration),
            Artist = new ApiArtist { Name = NormalizeText(track.Artist?.Name, string.Empty) },
            Album = new ApiAlbum { Title = NormalizeText(track.Album?.Title, string.Empty) }
        };
        return DeezerCandidateHeuristics.IsDerivativeCandidate(candidate);
    }

    private async Task<List<RecommendationTrackDto>> FilterRecommendationCandidatesThroughDeezerValidationAsync(
        List<RecommendationTrackDto> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var hydrated = await HydrateRecommendationCandidatesAsync(candidates, cancellationToken);
        if (hydrated.Count == 0)
        {
            return new List<RecommendationTrackDto>();
        }

        var accepted = new List<RecommendationTrackDto>(Math.Min(candidates.Count, RecommendationPoolLimit));
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = NormalizeId(candidate.Id);
            if (string.IsNullOrWhiteSpace(id) || !hydrated.TryGetValue(id, out var metadata))
            {
                continue;
            }

            var validation = ValidateRecommendationCandidate(candidate, metadata);
            if (!validation.Accepted || IsDerivativeRecommendationCandidate(metadata))
            {
                continue;
            }

            accepted.Add(MergeRecommendationTrack(candidate, metadata) with { TrackPosition = accepted.Count + 1 });
            if (accepted.Count >= RecommendationPoolLimit)
            {
                break;
            }
        }

        return accepted;
    }

    private async Task<Dictionary<string, RecommendationTrackDto>> HydrateRecommendationCandidatesAsync(
        IReadOnlyList<RecommendationTrackDto> candidates,
        CancellationToken cancellationToken)
    {
        var hydrated = new Dictionary<string, RecommendationTrackDto>(StringComparer.Ordinal);
        var ids = candidates
            .Select(track => NormalizeId(track.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        const int batchSize = 100;
        for (var start = 0; start < ids.Count; start += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = ids.Skip(start).Take(batchSize).ToList();
            try
            {
                var tracks = await _deezerGatewayService.GetTracksAsync(batch);
                foreach (var metadata in tracks.Select(MapGatewayTrack))
                {
                    var id = NormalizeId(metadata.Id);
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        hydrated[id] = metadata;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Recommendation candidate Deezer validation failed for {Count} tracks.", batch.Count);
                }
            }
        }

        return hydrated;
    }

    private static TrackCandidateValidationResult ValidateRecommendationCandidate(
        RecommendationTrackDto source,
        RecommendationTrackDto candidate)
    {
        return TrackCandidateValidator.Validate(
            new TrackMatchSource(
                source.Isrc,
                source.Title,
                source.Artist?.Name,
                source.Album?.Title,
                source.Duration > 0 ? source.Duration * 1000 : null),
            new TrackMatchCandidate(
                candidate.Id,
                candidate.Isrc,
                candidate.Title,
                candidate.Artist?.Name,
                candidate.Album?.Title,
                candidate.Duration > 0 ? candidate.Duration * 1000 : null),
            new TrackCandidateValidationOptions(
                StrictWithoutIsrc: true,
                AllowMissingCandidateArtist: false,
                RequireCandidateDurationWhenSourceHasDuration: true,
                MaxIsrcDurationDifferenceMs: 20_000,
                MaxMetadataDurationDifferenceMs: 8_000));
    }

    private async Task<RecommendationDetailDto?> TryLoadPersistedDailyPoolAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        string? stationImageUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var persisted = await _repository.GetPlaylistTrackCandidateCacheAsync(
                DailyPoolCacheSource,
                scope.ScopeKey,
                cancellationToken);
            if (persisted is null
                || !string.Equals(
                    NormalizeDailyPoolSnapshotId(persisted.SnapshotId),
                    BuildDailyPoolSnapshotId(dayUtc),
                    StringComparison.Ordinal))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(persisted.CandidatesJson))
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<PersistedDailyPoolDto>(persisted.CandidatesJson);
            if (payload is null)
            {
                return null;
            }

            var normalizedTracks = (payload.Tracks ?? Array.Empty<RecommendationTrackDto>())
                .Select(NormalizeRecommendationTrack)
                .Where(track => !string.IsNullOrWhiteSpace(track.Id))
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();
            if (normalizedTracks.Count == 0)
            {
                return null;
            }

            var station = new RecommendationStationDto(
                scope.StationId,
                $"Recommendations - {scope.FolderName}",
                BuildDailyRecommendationDescription(scope.FolderName, dayUtc.DayOfWeek),
                RecommendationSourceId,
                scope.FolderName,
                Math.Min(MaxDailyRecommendations, normalizedTracks.Count),
                string.IsNullOrWhiteSpace(payload.StationImageUrl) ? stationImageUrl : payload.StationImageUrl);

            return new RecommendationDetailDto(
                station,
                normalizedTracks,
                payload.GeneratedAtUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Daily recommendation build timed out for scope {ScopeKey}.",
                scope.ScopeKey);

            return null;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to load persisted recommendation daily pool for scope {ScopeKey}.",
                scope.ScopeKey);
            return null;
        }
    }

    private async Task<PersistDailyPoolResult> PersistDailyPoolAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        RecommendationDetailDto detail,
        CancellationToken cancellationToken)
    {
        if (detail.Tracks.Count == 0)
        {
            return PersistDailyPoolResult.Failed(EmptyPoolReason);
        }

        try
        {
            var payload = new PersistedDailyPoolDto(
                detail.GeneratedAtUtc,
                detail.Tracks
                    .Select(NormalizeRecommendationTrack)
                    .Where(track => !string.IsNullOrWhiteSpace(track.Id))
                    .Select((track, index) => track with { TrackPosition = index + 1 })
                    .ToList(),
                detail.Station.ImageUrl);

            await _repository.UpsertPlaylistTrackCandidateCacheAsync(
                DailyPoolCacheSource,
                scope.ScopeKey,
                BuildDailyPoolSnapshotId(dayUtc),
                JsonSerializer.Serialize(payload),
                schemaVersion: 0,
                identityRevision: null,
                providerReadinessRevision: null,
                isComplete: true,
                cancellationToken);
            return PersistDailyPoolResult.Ok;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Persisting recommendation daily pool timed out for scope {ScopeKey}.",
                scope.ScopeKey);
            return PersistDailyPoolResult.Failed(PersistTimedOutReason);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Failed to persist recommendation daily pool for scope {ScopeKey}.",
                scope.ScopeKey);
            return PersistDailyPoolResult.Failed(PersistFailedReason);
        }
    }

    private static string BuildDailyPoolSnapshotId(DateOnly dayUtc)
        => $"{DailyPoolSnapshotVersion}:{dayUtc:yyyyMMdd}";

    private static string NormalizeDailyPoolSnapshotId(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private async Task<List<RecommendationTrackDto>> PreferFreshRecommendationCandidatesAsync(
        RecommendationScope scope,
        List<RecommendationTrackDto> candidates,
        DateOnly dayUtc,
        CancellationToken cancellationToken)
    {
        if (candidates.Count <= MaxDailyRecommendations)
        {
            return candidates
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();
        }

        var recentIds = await LoadRecentRecommendationExposureIdsAsync(scope, dayUtc, cancellationToken);
        if (recentIds.Count == 0)
        {
            return candidates
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();
        }

        var fresh = new List<RecommendationTrackDto>(candidates.Count);
        var recent = new List<RecommendationTrackDto>();
        foreach (var candidate in candidates)
        {
            var id = NormalizeId(candidate.Id);
            if (!string.IsNullOrWhiteSpace(id) && recentIds.Contains(id))
            {
                recent.Add(candidate);
            }
            else
            {
                fresh.Add(candidate);
            }
        }

        return fresh
            .Concat(recent)
            .Take(RecommendationPoolLimit)
            .Select((track, index) => track with { TrackPosition = index + 1 })
            .ToList();
    }

    private async Task<HashSet<string>> LoadRecentRecommendationExposureIdsAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        CancellationToken cancellationToken)
    {
        var history = await LoadRecommendationExposureHistoryAsync(scope, cancellationToken);
        var oldestAllowedDay = dayUtc.AddDays(-RecommendationExposureRetentionDays);
        return history.Entries
            .Select(entry => new
            {
                TrackId = NormalizeId(entry.TrackId),
                Day = TryParseExposureDay(entry.Day)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.TrackId)
                            && entry.Day.HasValue
                            && entry.Day.Value < dayUtc
                            && entry.Day.Value >= oldestAllowedDay)
            .Select(entry => entry.TrackId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<RecommendationExposureHistoryDto> LoadRecommendationExposureHistoryAsync(
        RecommendationScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            var persisted = await _repository.GetPlaylistTrackCandidateCacheAsync(
                ExposureHistoryCacheSource,
                scope.ScopeKey,
                cancellationToken);
            if (persisted is null || string.IsNullOrWhiteSpace(persisted.CandidatesJson))
            {
                return new RecommendationExposureHistoryDto(Array.Empty<RecommendationExposureEntryDto>());
            }

            var payload = JsonSerializer.Deserialize<RecommendationExposureHistoryDto>(persisted.CandidatesJson);
            return payload ?? new RecommendationExposureHistoryDto(Array.Empty<RecommendationExposureEntryDto>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to load recommendation exposure history for scope {ScopeKey}.", scope.ScopeKey);
            }

            return new RecommendationExposureHistoryDto(Array.Empty<RecommendationExposureEntryDto>());
        }
    }

    private async Task PersistRecommendationExposureHistoryAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        IEnumerable<RecommendationTrackDto> visibleTracks,
        CancellationToken cancellationToken)
    {
        var history = await LoadRecommendationExposureHistoryAsync(scope, cancellationToken);
        var oldestRetainedDay = dayUtc.AddDays(-RecommendationExposureRetentionDays);
        var dayKey = FormatExposureDay(dayUtc);
        var entries = history.Entries
            .Where(entry =>
            {
                var parsedDay = TryParseExposureDay(entry.Day);
                return parsedDay.HasValue
                       && parsedDay.Value >= oldestRetainedDay
                       && parsedDay.Value <= dayUtc
                       && !string.Equals(entry.Day, dayKey, StringComparison.Ordinal);
            })
            .ToList();

        entries.AddRange(visibleTracks
            .Select(track => NormalizeId(track.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Select(id => new RecommendationExposureEntryDto(id, dayKey)));

        try
        {
            await _repository.UpsertPlaylistTrackCandidateCacheAsync(
                ExposureHistoryCacheSource,
                scope.ScopeKey,
                $"{ExposureHistorySnapshotVersion}:{dayKey}",
                JsonSerializer.Serialize(new RecommendationExposureHistoryDto(entries)),
                schemaVersion: 0,
                identityRevision: null,
                providerReadinessRevision: null,
                isComplete: true,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to persist recommendation exposure history for scope {ScopeKey}.", scope.ScopeKey);
            }
        }
    }

    private static DateOnly? TryParseExposureDay(string? value)
    {
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", out var parsed)
            ? parsed
            : null;
    }

    private static string FormatExposureDay(DateOnly day)
        => day.ToString("yyyy-MM-dd");

    private async Task<ShazamRecommendationBuildResult> BuildShazamRecommendationsAsync(
        RecommendationScope scope,
        IReadOnlyList<LibraryRecommendationSeedTrackDto> orderedSeeds,
        CancellationToken cancellationToken)
    {
        if (!IsShazamRecommendationAvailable())
        {
            return ShazamRecommendationBuildResult.Empty("shazam_unavailable");
        }

        var shazamSeedTrackIds = orderedSeeds
            .Select(seed => seed.TrackId)
            .Take(ShazamSelectedSeedLimit)
            .ToList();
        if (shazamSeedTrackIds.Count == 0)
        {
            return ShazamRecommendationBuildResult.Empty("shazam_no_seed_tracks");
        }

        await RefreshStaleShazamSeedsAsync(scope, shazamSeedTrackIds, cancellationToken);

        var cacheByTrackId = await _repository.GetShazamTrackCacheByTrackIdForLibraryAsync(
            scope.LibraryId,
            scope.FolderId,
            cancellationToken);
        var tracks = BuildRecommendationsFromShazamCache(shazamSeedTrackIds, cacheByTrackId, cancellationToken);
        return tracks.Count > 0
            ? new ShazamRecommendationBuildResult(tracks, "shazam_ok")
            : ShazamRecommendationBuildResult.Empty(ResolveEmptyShazamReasonCode(shazamSeedTrackIds, cacheByTrackId));
    }

    private bool IsShazamRecommendationAvailable()
    {
        if (_shazamRecognitionService.IsAvailable)
        {
            return true;
        }

        _logger.LogDebug("Skipping Shazam recommendation scan because recognizer is unavailable.");
        return false;
    }

    private async Task RefreshStaleShazamSeedsAsync(
        RecommendationScope scope,
        IReadOnlyList<long> selectedSeedTrackIds,
        CancellationToken cancellationToken)
    {
        var staleBeforeUtc = DateTimeOffset.UtcNow - ShazamCacheTtl;
        var staleTrackIds = await _repository.GetTrackIdsNeedingShazamRefreshAsync(
            scope.LibraryId,
            staleBeforeUtc,
            scope.FolderId,
            cancellationToken: cancellationToken);
        if (staleTrackIds.Count == 0)
        {
            return;
        }

        var staleSet = new HashSet<long>(staleTrackIds);
        var inlineTargets = selectedSeedTrackIds
            .Where(staleSet.Contains)
            .ToList();

        if (inlineTargets.Count > 0)
        {
            await RefreshShazamCacheForTrackBatchAsync(inlineTargets, cancellationToken);
        }
    }

    private static List<RecommendationTrackDto> BuildRecommendationsFromShazamCache(
        IReadOnlyList<long> orderedSeeds,
        IReadOnlyDictionary<long, ShazamTrackCacheDto> cacheByTrackId,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<RecommendationTrackDto>();
        var maxResults = MaxDailyRecommendations * 2;

        foreach (var trackId in orderedSeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= maxResults)
            {
                break;
            }

            if (!cacheByTrackId.TryGetValue(trackId, out var cache)
                || !ShouldIncludeShazamCacheEntry(cache))
            {
                continue;
            }

            foreach (var relatedTrack in cache.RelatedTracks)
            {
                TryAddShazamRelatedRecommendation(relatedTrack, seen, results);
                if (results.Count >= maxResults)
                {
                    break;
                }
            }
        }

        return results;
    }

    private static bool ShouldIncludeShazamCacheEntry(ShazamTrackCacheDto cache)
    {
        return string.Equals(cache.Status, StatusMatched, StringComparison.OrdinalIgnoreCase)
               && cache.RelatedTracks.Count > 0;
    }

    private static string ResolveEmptyShazamReasonCode(
        IReadOnlyList<long> orderedSeeds,
        IReadOnlyDictionary<long, ShazamTrackCacheDto> cacheByTrackId)
    {
        var hasCachedSeed = false;
        var hasNoDeezerResolution = false;
        var hasNoRelated = false;
        var hasNoMatch = false;
        var hasError = false;

        foreach (var trackId in orderedSeeds)
        {
            if (!cacheByTrackId.TryGetValue(trackId, out var cache))
            {
                continue;
            }

            hasCachedSeed = true;
            if (string.Equals(cache.Status, StatusMatchedNoDeezerResolution, StringComparison.OrdinalIgnoreCase))
            {
                hasNoDeezerResolution = true;
            }
            else if (string.Equals(cache.Status, StatusMatchedNoRelated, StringComparison.OrdinalIgnoreCase))
            {
                hasNoRelated = true;
            }
            else if (string.Equals(cache.Status, StatusNoMatch, StringComparison.OrdinalIgnoreCase))
            {
                hasNoMatch = true;
            }
            else if (string.Equals(cache.Status, StatusError, StringComparison.OrdinalIgnoreCase))
            {
                hasError = true;
            }
        }

        if (hasNoDeezerResolution)
        {
            return "shazam_no_deezer_resolution";
        }

        if (hasNoRelated)
        {
            return "shazam_no_related";
        }

        if (hasNoMatch)
        {
            return "shazam_no_match";
        }

        if (hasError)
        {
            return "shazam_failed";
        }

        return hasCachedSeed ? "shazam_empty" : "shazam_no_cache";
    }

    private static void TryAddShazamRelatedRecommendation(
        RecommendationTrackDto track,
        HashSet<string> seen,
        List<RecommendationTrackDto> results)
    {
        var normalized = NormalizeRecommendationTrack(track);
        var deezerId = NormalizeId(normalized.Id);
        if (string.IsNullOrWhiteSpace(deezerId)
            || !seen.Add(deezerId))
        {
            return;
        }

        results.Add(normalized with { Id = deezerId, TrackPosition = results.Count + 1 });
    }

    private bool StartBackgroundShazamRefresh(RecommendationScope scope, IReadOnlyList<long>? explicitTrackIds)
    {
        if (!_backgroundScans.TryAdd(scope.ScopeKey, 0))
        {
            return false;
        }

        _ = StartBackgroundShazamRefreshCoreAsync(scope, explicitTrackIds);
        return true;
    }

    private async Task StartBackgroundShazamRefreshCoreAsync(
        RecommendationScope scope,
        IReadOnlyList<long>? explicitTrackIds)
    {
        try
        {
            if (explicitTrackIds is { Count: > 0 })
            {
                await RefreshShazamCacheForTrackBatchAsync(explicitTrackIds, CancellationToken.None);
            }
            else
            {
                var staleBeforeUtc = DateTimeOffset.UtcNow - ShazamCacheTtl;
                while (true)
                {
                    var batch = await _repository.GetTrackIdsNeedingShazamRefreshAsync(
                        scope.LibraryId,
                        staleBeforeUtc,
                        scope.FolderId,
                        ShazamBackgroundBatchSize,
                        CancellationToken.None);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    await RefreshShazamCacheForTrackBatchAsync(batch, CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Background Shazam library scan failed for scope {ScopeKey}.", scope.ScopeKey);
        }
        finally
        {
            _backgroundScans.TryRemove(scope.ScopeKey, out _);
        }
    }

    private async Task RefreshShazamCacheForTrackBatchAsync(
        IReadOnlyList<long> trackIds,
        CancellationToken cancellationToken)
    {
        if (trackIds.Count == 0)
        {
            return;
        }

        var deezerResolveCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trackId in trackIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshShazamCacheForTrackAsync(trackId, deezerResolveCache, cancellationToken);
        }
    }

    private async Task RefreshShazamCacheForTrackAsync(
        long trackId,
        IDictionary<string, string> deezerResolveCache,
        CancellationToken cancellationToken)
    {
        var scannedAtUtc = DateTimeOffset.UtcNow;
        var filePath = await _repository.GetTrackPrimaryFilePathAsync(trackId, cancellationToken);
        if (IsMissingPrimaryAudioFile(filePath))
        {
            await PersistShazamCacheFileNotFoundAsync(trackId, scannedAtUtc, cancellationToken);
            return;
        }

        var attempt = await TryRecognizeTrackWithShazamAsync(trackId, filePath!, scannedAtUtc, cancellationToken);
        if (attempt is null)
        {
            return;
        }

        var recognizedTrack = await TryCreateRecognizedTrackAsync(trackId, attempt, scannedAtUtc, cancellationToken);
        if (recognizedTrack is null)
        {
            return;
        }

        await PersistRecognitionSourceLinksAsync(trackId, recognizedTrack.Recognition, cancellationToken);
        var similarCards = await TryFetchSimilarShazamTracksAsync(trackId, recognizedTrack, scannedAtUtc, cancellationToken);
        if (similarCards is null)
        {
            return;
        }

        var relatedRecommendations = await BuildRelatedShazamRecommendationsAsync(
            similarCards,
            trackId,
            deezerResolveCache,
            cancellationToken);
        var status = ResolveShazamSimilarStatus(relatedRecommendations.Count, similarCards.Count);
        var failureReason = status switch
        {
            StatusMatchedNoDeezerResolution => "Shazam similar tracks did not resolve to Deezer tracks.",
            StatusMatchedNoRelated => "Shazam returned no similar tracks.",
            _ => null
        };
        await PersistMatchedShazamCacheAsync(
            trackId,
            recognizedTrack,
            relatedRecommendations,
            scannedAtUtc,
            status,
            failureReason,
            cancellationToken);
    }

    private sealed record RecognizedShazamTrack(ShazamRecognitionInfo Recognition, string ShazamTrackId);

    private static bool IsMissingPrimaryAudioFile(string? filePath)
    {
        return string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath);
    }

    private async Task PersistShazamCacheFileNotFoundAsync(
        long trackId,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        await _repository.UpsertTrackShazamCacheAsync(
            new LibraryRepository.TrackShazamCacheUpsertInput(
                trackId,
                StatusError,
                null,
                null,
                null,
                null,
                Array.Empty<RecommendationTrackDto>(),
                scannedAtUtc,
                "Primary audio file not found."),
            cancellationToken);
    }

    private async Task<ShazamRecognitionAttempt?> TryRecognizeTrackWithShazamAsync(
        long trackId,
        string filePath,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return _shazamRecognitionService.RecognizeWithDetails(filePath, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Shazam recognition timed out for track {TrackId} ({FilePath}).",
                    trackId,
                    filePath);
            }

            await _repository.UpsertTrackShazamCacheAsync(
                new LibraryRepository.TrackShazamCacheUpsertInput(
                    trackId,
                    StatusError,
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<RecommendationTrackDto>(),
                    scannedAtUtc,
                    "Shazam recognition timed out."),
                cancellationToken);
            return null;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            var reason = BuildPersistedFailureReason("Shazam recognition failed", ex);
            _logger.LogWarning(ex, "Shazam recognition failed for library track {TrackId}.", trackId);
            await _repository.UpsertTrackShazamCacheAsync(
                new LibraryRepository.TrackShazamCacheUpsertInput(
                    trackId,
                    StatusError,
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<RecommendationTrackDto>(),
                    scannedAtUtc,
                    reason),
                cancellationToken);
            return null;
        }
    }

    private async Task<RecognizedShazamTrack?> TryCreateRecognizedTrackAsync(
        long trackId,
        ShazamRecognitionAttempt attempt,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!attempt.Matched || attempt.Recognition is null || !attempt.Recognition.HasCoreMetadata)
        {
            await PersistIncompleteShazamAttemptAsync(trackId, attempt, scannedAtUtc, cancellationToken);
            return null;
        }

        var recognition = attempt.Recognition;
        var shazamTrackId = NormalizeId(recognition.TrackId);
        if (string.IsNullOrWhiteSpace(shazamTrackId))
        {
            await _repository.UpsertTrackShazamCacheAsync(
                new LibraryRepository.TrackShazamCacheUpsertInput(
                    trackId,
                    StatusNoMatch,
                    null,
                    NormalizeText(recognition.Title, string.Empty),
                    GetRecognitionArtist(recognition),
                    NormalizeText(recognition.Isrc, string.Empty),
                    Array.Empty<RecommendationTrackDto>(),
                    scannedAtUtc,
                    "Shazam did not return a track id."),
                cancellationToken);
            return null;
        }

        return new RecognizedShazamTrack(recognition, shazamTrackId);
    }

    private async Task PersistIncompleteShazamAttemptAsync(
        long trackId,
        ShazamRecognitionAttempt attempt,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        var status = attempt.Outcome == ShazamRecognitionOutcome.NoMatch ? StatusNoMatch : StatusError;
        await _repository.UpsertTrackShazamCacheAsync(
            new LibraryRepository.TrackShazamCacheUpsertInput(
                trackId,
                status,
                NormalizeId(attempt.Recognition?.TrackId),
                NormalizeText(attempt.Recognition?.Title, string.Empty),
                GetRecognitionArtist(attempt.Recognition),
                NormalizeText(attempt.Recognition?.Isrc, string.Empty),
                Array.Empty<RecommendationTrackDto>(),
                scannedAtUtc,
                string.IsNullOrWhiteSpace(attempt.Error) ? null : attempt.Error.Trim()),
            cancellationToken);
    }

    private async Task<IReadOnlyList<ShazamTrackCard>?> TryFetchRelatedShazamTracksAsync(
        long trackId,
        RecognizedShazamTrack recognizedTrack,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _shazamDiscoveryService.GetRelatedTracksAsync(
                recognizedTrack.ShazamTrackId,
                limit: ShazamRelatedPerSeed,
                offset: 0,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Shazam related-track fetch timed out for track {TrackId} ({ShazamTrackId}).",
                    trackId,
                    recognizedTrack.ShazamTrackId);
            }

            await PersistMatchedShazamCacheAsync(
                trackId,
                recognizedTrack,
                Array.Empty<RecommendationTrackDto>(),
                scannedAtUtc,
                StatusMatchedNoRelated,
                "Shazam related-track fetch timed out.",
                cancellationToken);
            return null;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Shazam related-track fetch failed for track {TrackId} ({ShazamTrackId}). Persisting matched recognition without related tracks.",
                trackId,
                recognizedTrack.ShazamTrackId);
            await PersistMatchedShazamCacheAsync(
                trackId,
                recognizedTrack,
                Array.Empty<RecommendationTrackDto>(),
                scannedAtUtc,
                StatusMatchedNoRelated,
                BuildPersistedFailureReason("Shazam related-track fetch failed", ex),
                cancellationToken);
            return null;
        }
    }

    private async Task<IReadOnlyList<ShazamTrackCard>?> TryFetchSimilarShazamTracksAsync(
        long trackId,
        RecognizedShazamTrack recognizedTrack,
        DateTimeOffset scannedAtUtc,
        CancellationToken cancellationToken)
    {
        var relatedCards = await TryFetchRelatedShazamTracksAsync(trackId, recognizedTrack, scannedAtUtc, cancellationToken);
        if (relatedCards is null)
        {
            return null;
        }

        var searchCards = await TryFetchSearchShazamTracksAsync(trackId, recognizedTrack.Recognition, cancellationToken);
        return MergeShazamSimilarCards(relatedCards, searchCards, recognizedTrack);
    }

    private async Task<IReadOnlyList<ShazamTrackCard>> TryFetchSearchShazamTracksAsync(
        long trackId,
        ShazamRecognitionInfo recognition,
        CancellationToken cancellationToken)
    {
        var query = BuildShazamSearchQuery(recognition);
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ShazamTrackCard>();
        }

        try
        {
            return await _shazamDiscoveryService.SearchTracksAsync(
                query,
                limit: ShazamSimilarLookupLimit,
                offset: 0,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Shazam search fetch failed for track {TrackId} using query '{Query}'. Continuing with related tracks only.",
                trackId,
                query);
            return Array.Empty<ShazamTrackCard>();
        }
    }

    private static List<ShazamTrackCard> MergeShazamSimilarCards(
        IReadOnlyList<ShazamTrackCard> relatedCards,
        IReadOnlyList<ShazamTrackCard> searchCards,
        RecognizedShazamTrack recognizedTrack)
    {
        var output = new List<ShazamTrackCard>(ShazamSimilarLookupLimit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedIdentity = BuildShazamRecognitionIdentity(recognizedTrack);
        AddShazamSimilarCards(relatedCards, output, seen, matchedIdentity);
        AddShazamSimilarCards(searchCards, output, seen, matchedIdentity);
        return output;
    }

    private static string ResolveShazamSimilarStatus(int relatedRecommendationCount, int similarCardCount)
    {
        if (relatedRecommendationCount > 0)
        {
            return StatusMatched;
        }

        return similarCardCount > 0 ? StatusMatchedNoDeezerResolution : StatusMatchedNoRelated;
    }

    private static void AddShazamSimilarCards(
        IReadOnlyList<ShazamTrackCard> source,
        List<ShazamTrackCard> output,
        HashSet<string> seen,
        string? matchedIdentity)
    {
        foreach (var card in source)
        {
            if (output.Count >= ShazamSimilarLookupLimit)
            {
                return;
            }

            var identity = BuildShazamCardIdentity(card);
            if (string.IsNullOrWhiteSpace(identity)
                || string.Equals(identity, matchedIdentity, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(identity))
            {
                continue;
            }

            output.Add(card);
        }
    }

    private static string? BuildShazamCardIdentity(ShazamTrackCard card)
    {
        if (!string.IsNullOrWhiteSpace(card.Id))
        {
            return $"id:{card.Id.Trim()}";
        }

        return BuildShazamTextIdentity(card.Title, card.Artist);
    }

    private static string? BuildShazamRecognitionIdentity(RecognizedShazamTrack recognizedTrack)
    {
        if (!string.IsNullOrWhiteSpace(recognizedTrack.ShazamTrackId))
        {
            return $"id:{recognizedTrack.ShazamTrackId.Trim()}";
        }

        return BuildShazamTextIdentity(
            recognizedTrack.Recognition.Title,
            recognizedTrack.Recognition.Artist);
    }

    private static string? BuildShazamTextIdentity(string? title, string? artist)
    {
        var normalizedTitle = NormalizeText(title, string.Empty).ToLowerInvariant();
        var normalizedArtist = NormalizeText(artist, string.Empty).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedArtist)
            ? null
            : $"ta:{normalizedTitle}|{normalizedArtist}";
    }

    private static string BuildShazamSearchQuery(ShazamRecognitionInfo recognition)
    {
        return string.Join(
            " ",
            new[] { recognition.Title, recognition.Artist }
                .Select(value => NormalizeText(value, string.Empty))
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildPersistedFailureReason(string prefix, Exception exception)
    {
        var message = exception.Message?.Trim();
        var reason = string.IsNullOrWhiteSpace(message)
            ? prefix
            : $"{prefix}: {message}";
        return reason.Length <= PersistedFailureReasonMaxLength
            ? reason
            : reason[..PersistedFailureReasonMaxLength];
    }

    private async Task<List<RecommendationTrackDto>> BuildRelatedShazamRecommendationsAsync(
        IReadOnlyList<ShazamTrackCard> relatedCards,
        long sourceTrackId,
        IDictionary<string, string> deezerResolveCache,
        CancellationToken cancellationToken)
    {
        var relatedRecommendations = new List<RecommendationTrackDto>();
        var seenDeezerIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var card in relatedCards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deezerId = NormalizeId(await ResolveDeezerIdAsync(card, sourceTrackId, deezerResolveCache, cancellationToken));
            if (string.IsNullOrWhiteSpace(deezerId) || !seenDeezerIds.Add(deezerId))
            {
                continue;
            }

            relatedRecommendations.Add(CreateRecommendationFromShazamCard(card, deezerId, relatedRecommendations.Count + 1));
            if (relatedRecommendations.Count >= ShazamRelatedPerSeed)
            {
                break;
            }
        }

        return relatedRecommendations;
    }

    private static RecommendationTrackDto CreateRecommendationFromShazamCard(
        ShazamTrackCard card,
        string deezerId,
        int trackPosition)
    {
        var duration = card.DurationMs.HasValue && card.DurationMs.Value > 0
            ? Math.Max(0, (int)Math.Round(card.DurationMs.Value / 1000d))
            : 0;
        return NormalizeRecommendationTrack(new RecommendationTrackDto(
            deezerId,
            NormalizeText(card.Title, UnknownTitle),
            duration,
            NormalizeText(card.Isrc, string.Empty),
            trackPosition,
            new RecommendationArtistDto(
                NormalizeId(card.ArtistIds.FirstOrDefault() ?? string.Empty),
                NormalizeText(card.Artist, UnknownArtist)),
            new RecommendationAlbumDto(
                NormalizeId(card.AlbumAdamId ?? string.Empty),
                NormalizeText(card.Album, UnknownAlbum),
                NormalizeCoverMedium(card.ArtworkUrl))));
    }

    private async Task PersistMatchedShazamCacheAsync(
        long trackId,
        RecognizedShazamTrack recognizedTrack,
        IReadOnlyList<RecommendationTrackDto> relatedRecommendations,
        DateTimeOffset scannedAtUtc,
        string status,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        await _repository.UpsertTrackShazamCacheAsync(
            new LibraryRepository.TrackShazamCacheUpsertInput(
                trackId,
                status,
                recognizedTrack.ShazamTrackId,
                NormalizeText(recognizedTrack.Recognition.Title, string.Empty),
                GetRecognitionArtist(recognizedTrack.Recognition),
                NormalizeText(recognizedTrack.Recognition.Isrc, string.Empty),
                relatedRecommendations,
                scannedAtUtc,
                failureReason),
            cancellationToken);
    }

    private static string GetRecognitionArtist(ShazamRecognitionInfo? recognition)
    {
        var artist = recognition?.Artists.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? recognition?.Artist;
        return NormalizeText(artist, string.Empty);
    }

    private async Task<string> ResolveDeezerIdAsync(
        ShazamTrackCard card,
        long sourceTrackId,
        IDictionary<string, string> cache,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildResolveCacheKey(card);
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var resolved = string.Empty;
        foreach (var deezerLink in EnumerateShazamDeezerLinks(card))
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolved = NormalizeId(TryExtractDeezerTrackId(deezerLink));
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            resolved = await TryResolveShazamCardThroughCentralResolverAsync(card, sourceTrackId, cancellationToken);
        }

        cache[cacheKey] = resolved;
        return resolved;
    }

    private async Task<string> TryResolveShazamCardThroughCentralResolverAsync(
        ShazamTrackCard card,
        long sourceTrackId,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await _trackIdentityResolver.ResolveAsync(
                new TrackIdentityResolutionRequest(
                    SourcePlatform: "shazam",
                    SourceUrl: card.Url,
                    Title: NormalizeText(card.Title, string.Empty),
                    Artist: NormalizeText(card.Artist, string.Empty),
                    Album: NormalizeText(card.Album, string.Empty),
                    Isrc: NormalizeText(card.Isrc, string.Empty),
                    DurationMs: card.DurationMs,
                    TargetPlatforms: new[] { DeezerSource }),
                cancellationToken);
            return NormalizeId(resolution.DeezerId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Shazam recommendation central Deezer resolve failed for source track {TrackId}.",
                    sourceTrackId);
            }

            return string.Empty;
        }
    }

    private static bool TryParseDeezerSearchCandidate(
        object item,
        out string deezerId,
        out ApiTrack? candidate)
    {
        deezerId = string.Empty;
        candidate = null;

        if (item is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        deezerId = NormalizeId(GetJsonString(element, "id"));
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return false;
        }

        candidate = new ApiTrack
        {
            Id = deezerId,
            Title = NormalizeText(GetJsonString(element, "title"), string.Empty),
            TitleVersion = NormalizeText(GetJsonString(element, "title_version"), string.Empty),
            Isrc = NormalizeText(GetJsonString(element, "isrc"), string.Empty),
            Duration = GetJsonInt(element, "duration") ?? 0,
            Artist = new ApiArtist
            {
                Name = NormalizeText(
                    GetJsonNestedString(element, "artist", "name") ?? GetJsonString(element, "artist"),
                    string.Empty)
            },
            Album = new ApiAlbum
            {
                Title = NormalizeText(GetJsonNestedString(element, "album", "title"), string.Empty)
            }
        };

        return true;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static string? GetJsonNestedString(JsonElement element, string parentProperty, string childProperty)
    {
        if (!element.TryGetProperty(parentProperty, out var parent)
            || parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(childProperty, out var child))
        {
            return null;
        }

        return child.ValueKind switch
        {
            JsonValueKind.String => child.GetString()?.Trim(),
            JsonValueKind.Number => child.ToString(),
            _ => null
        };
    }

    private static int? GetJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static bool HasIsrcMismatch(string? sourceIsrc, string? candidateIsrc)
    {
        var source = NormalizeText(sourceIsrc, string.Empty);
        var candidate = NormalizeText(candidateIsrc, string.Empty);
        return !string.IsNullOrWhiteSpace(source)
            && !string.IsNullOrWhiteSpace(candidate)
            && !string.Equals(source, candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCandidateTitle(ApiTrack candidate)
    {
        var title = NormalizeText(candidate.Title, string.Empty);
        var version = NormalizeText(candidate.TitleVersion, string.Empty);
        if (string.IsNullOrWhiteSpace(version))
        {
            return title;
        }

        return title.Contains(version, StringComparison.OrdinalIgnoreCase)
            ? title
            : $"{title} {version}".Trim();
    }

    private static string NormalizeMatchText(string? value)
    {
        var normalized = NormalizeText(value, string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(normalized.Length);
        var previousWasSpace = false;
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousWasSpace = false;
                continue;
            }

            if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        var compact = string.Join(
            " ",
            builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact;
    }

    private static double ComputeTokenSimilarity(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0d;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1d;
        }

        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
        {
            return 0.92d;
        }

        var leftTokens = left
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var rightTokens = right
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0d;
        }

        var intersectionCount = leftTokens.Count(token => rightTokens.Contains(token));
        var unionCount = leftTokens.Count + rightTokens.Count - intersectionCount;
        return unionCount == 0 ? 0d : (double)intersectionCount / unionCount;
    }

    private static IEnumerable<string> EnumerateShazamDeezerLinks(ShazamTrackCard card)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (card.Tags.TryGetValue("SHAZAM_DEEZER_URL", out var deezerUrls))
        {
            foreach (var normalized in deezerUrls
                .Select(deezerUrl => NormalizeText(deezerUrl, string.Empty))
                .Where(normalized => !string.IsNullOrWhiteSpace(normalized)
                    && IsDeezerLinkCandidate(normalized)
                    && seen.Add(normalized)))
            {
                yield return normalized;
            }
        }

        if (IsDeezerLinkCandidate(card.Url))
        {
            var normalized = NormalizeText(card.Url, string.Empty);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static bool IsDeezerLinkCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(TryExtractDeezerTrackId(value));
    }

    private static string TryExtractDeezerTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (IsNumericIdentifier(trimmed))
        {
            return trimmed;
        }

        if (TryExtractTrackIdFromDeezerTrackPrefix(trimmed, out var prefixedId))
        {
            return prefixedId;
        }

        if (TryExtractTrackIdFromDeezerUri(trimmed, out var uriId))
        {
            return uriId;
        }

        if (TryExtractTrackIdFromTrackMarker(trimmed, out var markerId))
        {
            return markerId;
        }

        return string.Empty;
    }

    private static bool IsNumericIdentifier(string value)
    {
        return long.TryParse(value, out _);
    }

    private static bool TryExtractTrackIdFromDeezerTrackPrefix(string value, out string trackId)
    {
        trackId = string.Empty;
        const string deezerTrackPrefix = "deezer:track:";
        if (!value.StartsWith(deezerTrackPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var raw = value[deezerTrackPrefix.Length..];
        var candidate = new string(raw.TakeWhile(char.IsDigit).ToArray());
        if (!IsNumericIdentifier(candidate))
        {
            return false;
        }

        trackId = candidate;
        return true;
    }

    private static bool TryExtractTrackIdFromDeezerUri(string value, out string trackId)
    {
        trackId = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, DeezerSource, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, "track", StringComparison.OrdinalIgnoreCase))
        {
            var deezerPath = uri.AbsolutePath.Trim('/');
            if (IsNumericIdentifier(deezerPath))
            {
                trackId = deezerPath;
                return true;
            }
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!segments[i].Equals("track", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = segments[i + 1];
            if (IsNumericIdentifier(candidate))
            {
                trackId = candidate;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryExtractTrackIdFromTrackMarker(string value, out string trackId)
    {
        trackId = string.Empty;
        const string deezerTrackMarker = "deezer.com/track/";
        var markerIndex = value.IndexOf(deezerTrackMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var raw = value[(markerIndex + deezerTrackMarker.Length)..];
        var candidate = new string(raw.TakeWhile(char.IsDigit).ToArray());
        if (!IsNumericIdentifier(candidate))
        {
            return false;
        }

        trackId = candidate;
        return true;
    }

    private async Task PersistRecognitionSourceLinksAsync(
        long trackId,
        ShazamRecognitionInfo recognition,
        CancellationToken cancellationToken)
    {
        var spotifyUrl = NormalizeOptionalText(recognition.SpotifyUrl);
        var appleUrl = NormalizeOptionalText(recognition.AppleMusicUrl);
        await TryPersistPlatformSourceLinkAsync(
            trackId,
            "spotify",
            spotifyUrl,
            TryExtractSpotifyTrackId,
            cancellationToken);
        await TryPersistPlatformSourceLinkAsync(
            trackId,
            "apple",
            appleUrl,
            TryExtractAppleTrackId,
            cancellationToken);

        var deezerLink = await TryResolveAndPersistPlatformSourcesAsync(
            trackId,
            spotifyUrl,
            appleUrl,
            cancellationToken);
        var deezerId = deezerLink.DeezerId;
        var deezerUrl = deezerLink.DeezerUrl;

        if (string.IsNullOrWhiteSpace(deezerId))
        {
            deezerId = await TryResolveDeezerIdByIsrcAsync(trackId, recognition.Isrc);
        }

        if (string.IsNullOrWhiteSpace(deezerId))
        {
            deezerId = await TryResolveDeezerIdByMetadataAsync(trackId, recognition);
        }

        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return;
        }

        var resolvedDeezerUrl = string.IsNullOrWhiteSpace(deezerUrl)
            ? $"https://www.deezer.com/track/{deezerId}"
            : deezerUrl;
        await _repository.UpsertTrackSourceLinkAsync(
            trackId,
            DeezerSource,
            deezerId,
            resolvedDeezerUrl,
            cancellationToken: cancellationToken);
    }

    private async Task TryPersistPlatformSourceLinkAsync(
        long trackId,
        string source,
        string? platformUrl,
        Func<string?, string?> sourceIdExtractor,
        CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeOptionalText(platformUrl);
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            return;
        }

        var sourceId = sourceIdExtractor(normalizedUrl);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return;
        }

        await _repository.UpsertTrackSourceLinkAsync(
            trackId,
            source,
            sourceId,
            normalizedUrl,
            cancellationToken: cancellationToken);
    }

    private async Task<(string DeezerId, string DeezerUrl)> TryResolveAndPersistPlatformSourcesAsync(
        long trackId,
        string? spotifyUrl,
        string? appleUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyUrl) && string.IsNullOrWhiteSpace(appleUrl))
        {
            return (string.Empty, string.Empty);
        }

        var preferredUrl = !string.IsNullOrWhiteSpace(spotifyUrl) ? spotifyUrl : appleUrl!;
        try
        {
            var linked = await _trackIdentityResolver.ResolveAsync(
                new TrackIdentityResolutionRequest(
                    SourcePlatform: !string.IsNullOrWhiteSpace(spotifyUrl) ? "spotify" : "apple",
                    SourceUrl: preferredUrl,
                    Title: null,
                    Artist: null,
                    Album: null,
                    Isrc: null,
                    DurationMs: null,
                    TargetPlatforms: new[] { "deezer", "spotify", "apple" }),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(appleUrl))
            {
                await TryPersistPlatformSourceLinkAsync(
                    trackId,
                    "apple",
                    linked.AppleUrl,
                    TryExtractAppleTrackId,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(spotifyUrl))
            {
                await TryPersistPlatformSourceLinkAsync(
                    trackId,
                    "spotify",
                    linked.SpotifyUrl,
                    TryExtractSpotifyTrackId,
                    cancellationToken);
            }

            return (
                NormalizeId(linked.DeezerId),
                NormalizeOptionalText(linked.DeezerUrl) ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "platform source-link persistence failed for library track {TrackId}.", trackId);
            }
            return (string.Empty, string.Empty);
        }
    }

    private async Task<string> TryResolveDeezerIdByIsrcAsync(
        long trackId,
        string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return string.Empty;
        }

        try
        {
            var deezerTrack = await _deezerClient.GetTrackByIsrcAsync(isrc.Trim());
            return NormalizeId(deezerTrack?.Id?.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer ISRC source-link persistence failed for library track {TrackId}.", trackId);
            }
            return string.Empty;
        }
    }

    private async Task<string> TryResolveDeezerIdByMetadataAsync(
        long trackId,
        ShazamRecognitionInfo recognition)
    {
        if (string.IsNullOrWhiteSpace(recognition.Artist) || string.IsNullOrWhiteSpace(recognition.Title))
        {
            return string.Empty;
        }

        try
        {
            return NormalizeId(await _deezerClient.GetTrackIdFromMetadataAsync(
                recognition.Artist.Trim(),
                recognition.Title.Trim(),
                recognition.Album?.Trim() ?? string.Empty,
                recognition.DurationMs.HasValue && recognition.DurationMs.Value > 0
                    ? (int?)recognition.DurationMs.Value
                    : null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer metadata source-link persistence failed for library track {TrackId}.", trackId);
            }
            return string.Empty;
        }
    }

    private static List<RecommendationTrackDto> MergeRotating(
        List<RecommendationTrackDto> deezerTracks,
        List<RecommendationTrackDto> shazamTracks,
        int limit,
        DateOnly dayUtc)
    {
        var cappedLimit = Math.Clamp(limit, 1, RecommendationPoolLimit);
        var merged = new List<RecommendationTrackDto>(cappedLimit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deezerIndex = 0;
        var shazamIndex = 0;
        var useShazam = dayUtc.DayNumber % 2 == 0;

        while (merged.Count < cappedLimit && (deezerIndex < deezerTracks.Count || shazamIndex < shazamTracks.Count))
        {
            var added = false;

            if (useShazam)
            {
                added = TryAddTrack(shazamTracks, ref shazamIndex, merged, seen);
                if (!added)
                {
                    added = TryAddTrack(deezerTracks, ref deezerIndex, merged, seen);
                }
            }
            else
            {
                added = TryAddTrack(deezerTracks, ref deezerIndex, merged, seen);
                if (!added)
                {
                    added = TryAddTrack(shazamTracks, ref shazamIndex, merged, seen);
                }
            }

            if (!added)
            {
                break;
            }

            useShazam = !useShazam;
        }

        DrainTracksUntilLimit(deezerTracks, ref deezerIndex, merged, seen, cappedLimit);
        DrainTracksUntilLimit(shazamTracks, ref shazamIndex, merged, seen, cappedLimit);

        for (var index = 0; index < merged.Count; index++)
        {
            merged[index] = merged[index] with { TrackPosition = index + 1 };
        }

        return merged;
    }

    private sealed record RecommendationLane(string Key, Queue<RecommendationTrackDto> Tracks);

    private static List<RecommendationTrackDto> TopUpRecommendationSelection(
        List<RecommendationTrackDto> primarySelection,
        IReadOnlyList<RecommendationTrackDto> topUpCandidates,
        int limit,
        DateOnly dayUtc)
    {
        var cappedLimit = Math.Clamp(limit, 1, MaxDailyRecommendations);
        if (primarySelection.Count >= cappedLimit)
        {
            return primarySelection
                .Take(cappedLimit)
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();
        }

        var output = new List<RecommendationTrackDto>(cappedLimit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddUniqueTracks(primarySelection, output, seen, cappedLimit);
        if (output.Count < cappedLimit)
        {
            AddTopUpTracks(topUpCandidates, output, seen, cappedLimit, dayUtc);
        }

        return output
            .Select((track, index) => track with { TrackPosition = index + 1 })
            .ToList();
    }

    private static void AddUniqueTracks(
        IEnumerable<RecommendationTrackDto> tracks,
        List<RecommendationTrackDto> output,
        HashSet<string> seen,
        int limit)
    {
        foreach (var track in tracks)
        {
            if (!TryNormalizeTrackId(track.Id, out var normalizedId)
                || !seen.Add(normalizedId))
            {
                continue;
            }

            output.Add(track with { Id = normalizedId });
            if (output.Count >= limit)
            {
                return;
            }
        }
    }

    private static void AddTopUpTracks(
        IReadOnlyList<RecommendationTrackDto> topUpCandidates,
        List<RecommendationTrackDto> output,
        HashSet<string> seen,
        int limit,
        DateOnly dayUtc)
    {
        var remaining = topUpCandidates
            .Where(track => TryNormalizeTrackId(track.Id, out var normalizedId) && !seen.Contains(normalizedId))
            .ToList();
        var topUpTracks = BuildDiversifiedTrackSelection(
            remaining,
            limit - output.Count,
            dayUtc);
        AddUniqueTracks(topUpTracks, output, seen, limit);
    }

    private static bool TryNormalizeTrackId(string? id, out string normalizedId)
    {
        normalizedId = NormalizeId(id);
        return !string.IsNullOrWhiteSpace(normalizedId);
    }

    private static List<RecommendationTrackDto> BuildDiversifiedTrackSelection(
        IReadOnlyList<RecommendationTrackDto> tracks,
        int limit,
        DateOnly dayUtc)
    {
        var cappedLimit = Math.Clamp(limit, 1, MaxDailyRecommendations);
        if (tracks.Count == 0)
        {
            return new List<RecommendationTrackDto>();
        }

        if (tracks.Count <= cappedLimit)
        {
            return tracks
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();
        }

        var lanes = tracks
            .GroupBy(BuildRecommendationLaneKey, StringComparer.Ordinal)
            .Select(group => new RecommendationLane(
                group.Key,
                new Queue<RecommendationTrackDto>(group)))
            .OrderBy(lane => ComputeStableHash($"{dayUtc:yyyyMMdd}:{lane.Key}"))
            .ThenBy(lane => lane.Key, StringComparer.Ordinal)
            .ToList();

        if (lanes.Count == 0)
        {
            return tracks
                .Take(cappedLimit)
                .Select((track, index) => track with { TrackPosition = index + 1 })
                .ToList();
        }

        var selected = new List<RecommendationTrackDto>(cappedLimit);
        while (selected.Count < cappedLimit && lanes.Count > 0)
        {
            for (var index = 0; index < lanes.Count && selected.Count < cappedLimit; index++)
            {
                var lane = lanes[index];
                if (lane.Tracks.Count == 0)
                {
                    continue;
                }

                selected.Add(lane.Tracks.Dequeue());
            }

            lanes.RemoveAll(lane => lane.Tracks.Count == 0);
        }

        return selected
            .Select((track, index) => track with { TrackPosition = index + 1 })
            .ToList();
    }

    private static List<RecommendationTrackDto> BuildVisibleDailySelection(
        IReadOnlyList<RecommendationTrackDto> tracks,
        HashSet<string> excludedTrackIds,
        int limit,
        DateOnly dayUtc)
    {
        var eligibleTracks = excludedTrackIds.Count == 0
            ? tracks
            : tracks
                .Where(track => !excludedTrackIds.Contains(NormalizeId(track.Id)))
                .ToList();
        var dailySelection = BuildDiversifiedTrackSelection(eligibleTracks, limit, dayUtc);
        return TopUpRecommendationSelection(
            dailySelection,
            eligibleTracks,
            limit,
            dayUtc);
    }

    private static HashSet<string> BuildNormalizedRecommendationIdSet(IEnumerable<string> ids)
    {
        return ids
            .Select(NormalizeId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildRecommendationLaneKey(RecommendationTrackDto track)
    {
        var artistId = NormalizeReferenceId(track.Artist.Id);
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            return $"artist:{artistId}";
        }

        var artistName = NormalizeText(track.Artist.Name, UnknownArtist);
        if (!string.IsNullOrWhiteSpace(artistName)
            && !string.Equals(artistName, UnknownArtist, StringComparison.OrdinalIgnoreCase))
        {
            return $"artist-name:{artistName.ToLowerInvariant()}";
        }

        var albumId = NormalizeReferenceId(track.Album.Id);
        if (!string.IsNullOrWhiteSpace(albumId))
        {
            return $"album:{albumId}";
        }

        var normalizedTrackId = NormalizeTrackId(track.Id);
        if (!string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            return $"track:{normalizedTrackId}";
        }

        var textArtist = NormalizeText(track.Artist.Name, UnknownArtist);
        var textTitle = NormalizeText(track.Title, UnknownTitle);
        return $"text:{textArtist.ToLowerInvariant()}|{textTitle.ToLowerInvariant()}";
    }

    private static void DrainTracksUntilLimit(
        List<RecommendationTrackDto> tracks,
        ref int index,
        List<RecommendationTrackDto> output,
        HashSet<string> seen,
        int limit)
    {
        while (output.Count < limit)
        {
            if (!TryAddTrack(tracks, ref index, output, seen))
            {
                break;
            }
        }
    }

    private static bool TryAddTrack(
        List<RecommendationTrackDto> tracks,
        ref int index,
        List<RecommendationTrackDto> output,
        HashSet<string> seen)
    {
        while (index < tracks.Count)
        {
            var track = tracks[index++];
            var id = NormalizeId(track.Id);
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                continue;
            }

            output.Add(track with { Id = id });
            return true;
        }

        return false;
    }

    private static List<RecommendationTrackDto> OrderDeterministically(
        IEnumerable<RecommendationTrackDto> tracks,
        DateOnly dayUtc,
        string sourceTag)
    {
        return tracks
            .OrderBy(track => ComputeDailyScore($"{sourceTag}:{NormalizeId(track.Id)}", dayUtc))
            .ThenBy(track => NormalizeId(track.Id), StringComparer.Ordinal)
            .ToList();
    }

    private static RecommendationTrackDto NormalizeRecommendationTrack(RecommendationTrackDto track)
    {
        return new RecommendationTrackDto(
            NormalizeTrackId(track.Id),
            NormalizeText(track.Title, UnknownTitle),
            Math.Max(0, track.Duration),
            NormalizeText(track.Isrc, string.Empty),
            track.TrackPosition > 0 ? track.TrackPosition : 1,
            new RecommendationArtistDto(
                NormalizeReferenceId(track.Artist?.Id),
                NormalizeText(track.Artist?.Name, UnknownArtist)),
            new RecommendationAlbumDto(
                NormalizeReferenceId(track.Album?.Id),
                NormalizeText(track.Album?.Title, UnknownAlbum),
                NormalizeCoverMedium(track.Album?.CoverMedium)));
    }

    private async Task<IReadOnlyList<RecommendationTrackDto>> EnrichRecommendationMetadataAsync(
        List<RecommendationTrackDto> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return tracks;
        }

        var normalized = tracks
            .Select(NormalizeRecommendationTrack)
            .ToArray();
        var pendingIndexes = GetRecommendationMetadataPendingIndexes(normalized);
        if (pendingIndexes.Count == 0)
        {
            return normalized;
        }

        var pendingByTrackId = BuildPendingTrackMap(normalized, pendingIndexes);
        if (pendingByTrackId.Count == 0)
        {
            return normalized;
        }

        var unresolvedSet = ApplyCachedRecommendationMetadata(normalized, pendingByTrackId);
        if (unresolvedSet.Count == 0)
        {
            return normalized;
        }

        await EnrichRecommendationsFromGatewayAsync(normalized, pendingByTrackId, unresolvedSet, cancellationToken);
        return normalized;
    }

    private static List<int> GetRecommendationMetadataPendingIndexes(RecommendationTrackDto[] normalized)
    {
        var pendingIndexes = new List<int>(normalized.Length);
        for (var index = 0; index < normalized.Length; index++)
        {
            if (NeedsRecommendationMetadataEnrichment(normalized[index]))
            {
                pendingIndexes.Add(index);
            }
        }

        return pendingIndexes;
    }

    private static Dictionary<string, List<int>> BuildPendingTrackMap(
        RecommendationTrackDto[] normalized,
        IReadOnlyList<int> pendingIndexes)
    {
        var pendingByTrackId = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var index in pendingIndexes)
        {
            var deezerId = NormalizeId(normalized[index].Id);
            if (string.IsNullOrWhiteSpace(deezerId))
            {
                continue;
            }

            if (!pendingByTrackId.TryGetValue(deezerId, out var indexes))
            {
                indexes = new List<int>();
                pendingByTrackId[deezerId] = indexes;
            }

            indexes.Add(index);
        }

        return pendingByTrackId;
    }

    private HashSet<string> ApplyCachedRecommendationMetadata(
        RecommendationTrackDto[] normalized,
        Dictionary<string, List<int>> pendingByTrackId)
    {
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deezerId in pendingByTrackId.Keys)
        {
            if (_deezerRecommendationMetadataCache.TryGetValue(deezerId, out var cached))
            {
                ApplyRecommendationMetadata(normalized, pendingByTrackId, deezerId, cached);
                continue;
            }

            unresolved.Add(deezerId);
        }

        return unresolved;
    }

    private async Task EnrichRecommendationsFromGatewayAsync(
        RecommendationTrackDto[] normalized,
        Dictionary<string, List<int>> pendingByTrackId,
        HashSet<string> unresolvedTrackIds,
        CancellationToken cancellationToken)
    {
        const int batchSize = 100;
        var unresolvedIds = unresolvedTrackIds.ToList();
        for (var start = 0; start < unresolvedIds.Count; start += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = unresolvedIds
                .Skip(start)
                .Take(batchSize)
                .ToList();
            try
            {
                var gatewayTracks = await _deezerGatewayService.GetTracksAsync(batch);
                foreach (var deezerMetadata in gatewayTracks.Select(MapGatewayTrack))
                {
                    var deezerId = NormalizeId(deezerMetadata.Id);
                    if (string.IsNullOrWhiteSpace(deezerId) || !unresolvedTrackIds.Remove(deezerId))
                    {
                        continue;
                    }

                    CacheDeezerRecommendationMetadata(deezerId, deezerMetadata);
                    ApplyRecommendationMetadata(normalized, pendingByTrackId, deezerId, deezerMetadata);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Recommendation metadata batch enrichment failed for {Count} tracks.", batch.Count);
                }
            }
        }
    }

    private static void ApplyRecommendationMetadata(
        RecommendationTrackDto[] normalized,
        Dictionary<string, List<int>> pendingByTrackId,
        string deezerId,
        RecommendationTrackDto metadata)
    {
        if (!pendingByTrackId.TryGetValue(deezerId, out var indexes))
        {
            return;
        }

        foreach (var index in indexes)
        {
            normalized[index] = MergeRecommendationTrack(normalized[index], metadata);
        }
    }

    private static RecommendationTrackDto MapGatewayTrack(GwTrack track)
    {
        var trackId = track?.SngId > 0 ? track.SngId.ToString() : string.Empty;
        var artistId = track?.ArtId > 0
            ? track.ArtId.ToString()
            : string.Empty;
        var albumId = NormalizeId(track?.AlbId);
        var title = track?.SngTitle;
        var version = NormalizeText(track?.Version, string.Empty);
        if (!string.IsNullOrWhiteSpace(version))
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                title = version;
            }
            else if (!title.Contains(version, StringComparison.OrdinalIgnoreCase))
            {
                title = $"{title} {version}";
            }
        }

        var albumTitle = track?.AlbTitle;
        var artistName = track?.ArtName;
        var duration = Math.Max(0, track?.Duration ?? 0);
        var position = 1;
        if (track?.TrackNumber > 0)
        {
            position = track.TrackNumber;
        }
        else if (track?.Position > 0)
        {
            position = track.Position;
        }
        var cover = FirstNonEmpty(
            BuildCoverUrl(track?.AlbPicture));

        return new RecommendationTrackDto(
            NormalizeTrackId(trackId),
            NormalizeText(title, UnknownTitle),
            duration,
            NormalizeText(track?.Isrc, string.Empty),
            position,
            new RecommendationArtistDto(
                NormalizeReferenceId(artistId),
                NormalizeText(artistName, UnknownArtist)),
            new RecommendationAlbumDto(
                NormalizeReferenceId(albumId),
                NormalizeText(albumTitle, UnknownAlbum),
                NormalizeText(cover, string.Empty)));
    }

    private static RecommendationTrackDto MergeRecommendationTrack(
        RecommendationTrackDto current,
        RecommendationTrackDto deezerMetadata)
    {
        var mergedArtistId = !string.IsNullOrWhiteSpace(current.Artist?.Id)
            ? NormalizeReferenceId(current.Artist.Id)
            : NormalizeReferenceId(deezerMetadata.Artist?.Id);
        var mergedArtistName = IsMissingOrUnknown(current.Artist!.Name, UnknownArtist)
            ? NormalizeText(deezerMetadata.Artist?.Name, UnknownArtist)
            : NormalizeText(current.Artist.Name, UnknownArtist);
        var mergedAlbumId = !string.IsNullOrWhiteSpace(current.Album?.Id)
            ? NormalizeReferenceId(current.Album.Id)
            : NormalizeReferenceId(deezerMetadata.Album?.Id);
        var mergedAlbumTitle = IsMissingOrUnknown(current.Album!.Title, UnknownAlbum)
            ? NormalizeText(deezerMetadata.Album?.Title, UnknownAlbum)
            : NormalizeText(current.Album.Title, UnknownAlbum);
        var currentCover = NormalizeCoverMedium(current.Album?.CoverMedium);
        var deezerCover = NormalizeCoverMedium(deezerMetadata.Album?.CoverMedium);
        var mergedCover = !string.IsNullOrWhiteSpace(currentCover)
            ? currentCover
            : deezerCover;

        return new RecommendationTrackDto(
            NormalizeTrackId(current.Id),
            NormalizeText(current.Title, UnknownTitle),
            current.Duration > 0 ? current.Duration : Math.Max(0, deezerMetadata.Duration),
            !string.IsNullOrWhiteSpace(current.Isrc)
                ? NormalizeText(current.Isrc, string.Empty)
                : NormalizeText(deezerMetadata.Isrc, string.Empty),
            current.TrackPosition > 0 ? current.TrackPosition : 1,
            new RecommendationArtistDto(mergedArtistId, mergedArtistName),
            new RecommendationAlbumDto(mergedAlbumId, mergedAlbumTitle, mergedCover));
    }

    private static bool NeedsRecommendationMetadataEnrichment(RecommendationTrackDto track)
    {
        return track.Duration <= 0
            || IsMissingOrUnknown(track.Artist.Name, UnknownArtist)
            || IsMissingOrUnknown(track.Album.Title, UnknownAlbum)
            || string.IsNullOrWhiteSpace(track.Album.CoverMedium);
    }

    private static bool IsMissingOrUnknown(string? value, string unknownLabel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return string.Equals(value.Trim(), unknownLabel, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCoverMedium(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{trimmed}";
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            return string.Empty;
        }

        return LooksLikeDeezerCoverHash(trimmed)
            ? BuildCoverUrl(trimmed)
            : string.Empty;
    }

    private static bool LooksLikeDeezerCoverHash(string value)
    {
        return value.Length == 32 && value.All(Uri.IsHexDigit);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim();
    }

    private static string BuildCoverUrl(string? md5OrUrl)
    {
        if (string.IsNullOrWhiteSpace(md5OrUrl))
        {
            return string.Empty;
        }

        var normalized = md5OrUrl.Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out _))
        {
            return normalized;
        }

        return $"https://e-cdns-images.dzcdn.net/images/cover/{normalized}/500x500-000000-80-0-0.jpg";
    }

    private static string BuildResolveCacheKey(ShazamTrackCard card)
    {
        var isrc = NormalizeText(card.Isrc, string.Empty);
        if (!string.IsNullOrWhiteSpace(isrc))
        {
            return $"isrc:{isrc}";
        }

        return $"meta:{NormalizeText(card.Artist, string.Empty)}|{NormalizeText(card.Title, string.Empty)}|{NormalizeText(card.Album, string.Empty)}|{card.DurationMs?.ToString() ?? string.Empty}";
    }

    private static ulong ComputeDailyScore(string value, DateOnly dayUtc)
    {
        var input = $"{dayUtc:yyyyMMdd}:{value}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, sizeof(ulong)));
    }

    private static string BuildDailyCacheKey(string scopeKey, DateOnly dayUtc)
        => $"{scopeKey}:{dayUtc:yyyyMMdd}";

    private static ulong ComputeStableHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, sizeof(ulong)));
    }

    private void PruneOldCache(DateOnly currentDayUtc)
    {
        var marker = $":{currentDayUtc:yyyyMMdd}";
        var staleKeys = _dailyPoolCache.Keys
            .Where(key => !key.EndsWith(marker, StringComparison.Ordinal))
            .ToArray();
        foreach (var key in staleKeys)
        {
            _dailyPoolCache.TryRemove(key, out _);
        }
    }

    private void CacheDeezerRecommendationMetadata(string deezerId, RecommendationTrackDto metadata)
    {
        _deezerRecommendationMetadataCache[deezerId] = metadata;
        var excess = _deezerRecommendationMetadataCache.Count - DeezerMetadataCacheLimit;
        if (excess <= 0)
        {
            return;
        }

        foreach (var key in _deezerRecommendationMetadataCache.Keys.Take(excess))
        {
            _deezerRecommendationMetadataCache.TryRemove(key, out _);
        }
    }

    private static string NormalizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return long.TryParse(trimmed, out _) ? trimmed : string.Empty;
    }

    private static string NormalizeTrackId(string? value)
        => NormalizeId(value);

    private static string NormalizeReferenceId(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeText(string? value, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = string.Join(
            " ",
            value
                .Trim()
                .Replace('\u2013', '-')
                .Replace('\u2014', '-')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return string.IsNullOrWhiteSpace(normalized) ? defaultValue : normalized;
    }

    private static string BuildDailyRecommendationDescription(string folderName, DayOfWeek dayOfWeek)
    {
        var normalizedFolderName = NormalizeText(folderName, "Library");
        var dayName = $"{dayOfWeek}'s";
        return $"{dayName} recommendation for {normalizedFolderName}.";
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = NormalizeText(value, string.Empty);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? TryExtractSpotifyTrackId(string? spotifyUrl)
    {
        if (string.IsNullOrWhiteSpace(spotifyUrl))
        {
            return null;
        }

        return SpotifyMetadataService.TryParseSpotifyUrl(spotifyUrl, out var type, out var id)
               && string.Equals(type, "track", StringComparison.OrdinalIgnoreCase)
            ? NormalizeId(id)
            : null;
    }

    private static string? TryExtractAppleTrackId(string? appleUrl)
    {
        if (string.IsNullOrWhiteSpace(appleUrl)
            || !Uri.TryCreate(appleUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?');
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(parts => parts.Length == 2 && string.Equals(parts[0], "i", StringComparison.OrdinalIgnoreCase));
        if (parts is { Length: 2 })
        {
            return NormalizeId(Uri.UnescapeDataString(parts[1]));
        }

        var lastSegment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return NormalizeId(lastSegment);
    }
}
