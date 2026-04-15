using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GwTrack = DeezSpoTag.Core.Models.Deezer.GwTrack;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryRecommendationService
{
    public sealed class LibraryRecommendationCollaborators
    {
        public DeezerTrackRecommendationService DeezerRecommendations { get; init; } = null!;
        public LibraryRepository Repository { get; init; } = null!;
        public ShazamRecognitionService ShazamRecognitionService { get; init; } = null!;
        public ShazamDiscoveryService ShazamDiscoveryService { get; init; } = null!;
        public DeezerClient DeezerClient { get; init; } = null!;
        public DeezerGatewayService DeezerGatewayService { get; init; } = null!;
        public SongLinkResolver SongLinkResolver { get; init; } = null!;
    }

    public const string RecommendationSource = "recommendations";
    public const string RecommendationSourceId = "daily-rotation";
    private const string StatusMatched = "matched";
    private const string StatusError = "error";
    private const string StatusNoMatch = "no_match";
    private const string UnknownTitle = "Unknown";
    private const string UnknownArtist = "Unknown Artist";
    private const string UnknownAlbum = "Unknown Album";
    private const string DailyPoolCacheSource = "recommendations-daily-pool";
    private const string DailyPoolSnapshotVersion = "v1";

    private const int MaxDailyRecommendations = 50;
    private const int RecommendationPoolMultiplier = 3;
    private const int RecommendationPoolLimit = MaxDailyRecommendations * RecommendationPoolMultiplier;
    private const int ShazamRelatedPerSeed = 10;
    private const double ShazamDeezerMinTitleSimilarity = 0.62d;
    private const double ShazamDeezerMinArtistSimilarity = 0.52d;
    private const int ShazamDeezerMaxDurationDeltaSeconds = 24;
    private const int ShazamInlineRefreshBudget = 12;
    private const int ShazamBackgroundBatchSize = 120;
    private static readonly TimeSpan ShazamCacheTtl = TimeSpan.FromDays(14);
    private static readonly HashSet<string> RejectedDerivativeTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover",
        "parody",
        "instrumental",
        "karaoke",
        "tribute"
    };

    private readonly DeezerTrackRecommendationService _deezerRecommendations;
    private readonly LibraryRepository _repository;
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly ShazamDiscoveryService _shazamDiscoveryService;
    private readonly DeezerClient _deezerClient;
    private readonly DeezerGatewayService _deezerGatewayService;
    private readonly SongLinkResolver _songLinkResolver;
    private readonly string _recommendationArtworkRootPath;
    private readonly ILogger<LibraryRecommendationService> _logger;
    private readonly ConcurrentDictionary<string, RecommendationDetailDto> _dailyPoolCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _backgroundScans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RecommendationTrackDto> _deezerRecommendationMetadataCache = new(StringComparer.Ordinal);

    public LibraryRecommendationService(
        LibraryRecommendationCollaborators collaborators,
        IWebHostEnvironment webHostEnvironment,
        ILogger<LibraryRecommendationService> logger)
    {
        _deezerRecommendations = collaborators.DeezerRecommendations;
        _repository = collaborators.Repository;
        _shazamRecognitionService = collaborators.ShazamRecognitionService;
        _shazamDiscoveryService = collaborators.ShazamDiscoveryService;
        _deezerClient = collaborators.DeezerClient;
        _deezerGatewayService = collaborators.DeezerGatewayService;
        _songLinkResolver = collaborators.SongLinkResolver;
        _recommendationArtworkRootPath = string.IsNullOrWhiteSpace(webHostEnvironment.WebRootPath)
            ? string.Empty
            : Path.Combine(webHostEnvironment.WebRootPath, "images", "recommendations");
        _logger = logger;
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
        IReadOnlyList<RecommendationTrackDto> Tracks);

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
        var mode = NormalizeFolderMode(folder.DesiredQuality);
        return mode == DownloadContentTypes.Atmos
            || mode == DownloadContentTypes.Video
            || mode == DownloadContentTypes.Podcast
            // Legacy destination-mode folders can persist as numeric "0".
            || mode == "0";
    }

    private static string NormalizeFolderMode(string? desiredQuality)
    {
        var normalized = (desiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains("atmos", StringComparison.Ordinal))
        {
            return DownloadContentTypes.Atmos;
        }

        if (normalized.Contains("video", StringComparison.Ordinal))
        {
            return DownloadContentTypes.Video;
        }

        if (normalized.Contains("podcast", StringComparison.Ordinal))
        {
            return DownloadContentTypes.Podcast;
        }

        return normalized;
    }

    private static string? ResolveRecommendationArtworkUrl(
        string stationId,
        Dictionary<string, string> artworkAssignments)
    {
        if (string.IsNullOrWhiteSpace(stationId) || artworkAssignments.Count == 0)
        {
            return null;
        }

        return artworkAssignments.TryGetValue(stationId, out var imageUrl)
            ? imageUrl
            : null;
    }

    private async Task<IReadOnlyDictionary<string, PlaylistWatchlistDto>> GetRecommendationWatchlistMapAsync(
        CancellationToken cancellationToken)
    {
        var items = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        return items
            .Where(item => string.Equals(item.Source, RecommendationSource, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.SourceId, item => item, StringComparer.OrdinalIgnoreCase);
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

        var sixHourBucket = Math.Clamp(nowLocal.Hour / 6, 0, 3);
        var orderedCandidates = windowCandidates
            .OrderBy(candidate => ComputeStableHash($"{nowLocal:yyyyMMdd}|b{sixHourBucket}|{candidate.Url}"))
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
        var watchlistMap = await GetRecommendationWatchlistMapAsync(cancellationToken);
        var stations = new List<RecommendationStationDto>(folders.Count);
        foreach (var folder in folders.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var stationId = BuildStationId(libraryId, folder.Id);
            var detail = await GetRecommendationsAsync(
                libraryId,
                stationId: stationId,
                folderId: folder.Id,
                limit: MaxDailyRecommendations,
                cancellationToken: cancellationToken);

            if (detail is not null)
            {
                stations.Add(detail.Station with
                {
                    TrackCount = Math.Min(MaxDailyRecommendations, detail.Tracks.Count)
                });
                continue;
            }

            watchlistMap.TryGetValue(stationId, out var watchlistEntry);
            var imageUrl = !string.IsNullOrWhiteSpace(watchlistEntry?.ImageUrl)
                ? watchlistEntry.ImageUrl
                : ResolveRecommendationArtworkUrl(stationId, artworkAssignments);
            stations.Add(new RecommendationStationDto(
                stationId,
                $"Recommendations - {folder.DisplayName}",
                BuildDailyRecommendationDescription(folder.DisplayName, nowLocal.DayOfWeek),
                RecommendationSourceId,
                folder.DisplayName,
                0,
                imageUrl));
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
        var watchlistMap = await GetRecommendationWatchlistMapAsync(cancellationToken);
        watchlistMap.TryGetValue(scope.StationId, out var watchlistEntry);
        var stationImageUrl = !string.IsNullOrWhiteSpace(watchlistEntry?.ImageUrl)
            ? watchlistEntry.ImageUrl
            : ResolveRecommendationArtworkUrl(scope.StationId, artworkAssignments);

        var cappedLimit = Math.Clamp(limit, 1, MaxDailyRecommendations);
        var dayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        PruneOldCache(dayUtc);

        var cacheKey = BuildDailyCacheKey(scope.ScopeKey, dayUtc);
        var basePool = await GetOrBuildDailyPoolAsync(
            cacheKey,
            scope,
            dayUtc,
            stationImageUrl,
            cancellationToken);
        if (basePool == null)
        {
            return null;
        }

        var ignoredTrackIds = await _repository.GetPlaylistWatchIgnoredTrackIdsAsync(
            RecommendationSource,
            scope.StationId,
            cancellationToken);

        var basePoolTrackCount = basePool.Tracks.Count;
        var eligible = basePool.Tracks
            .Where(track => !ignoredTrackIds.Contains(track.Id))
            .ToList();
        var eligibleTrackCount = eligible.Count;
        IReadOnlyList<RecommendationTrackDto> enriched;
        try
        {
            enriched = await EnrichRecommendationMetadataAsync(eligible, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to enrich recommendation metadata for station {StationId}. Returning base recommendation tracks.",
                scope.StationId);
            enriched = eligible;
        }
        enriched = await ExcludeTracksAlreadyInLibraryAsync(
            scope.LibraryId,
            scope.FolderId,
            enriched,
            cancellationToken);
        var nonLibraryTrackCount = enriched.Count;
        var cappedTracks = BuildDiversifiedTrackSelection(
            enriched,
            cappedLimit,
            dayUtc);
        if (cappedTracks.Count < cappedLimit && eligibleTrackCount > cappedTracks.Count)
        {
            cappedTracks = TopUpRecommendationSelection(
                cappedTracks,
                eligible,
                cappedLimit,
                dayUtc);
        }

        if (cappedTracks.Count < cappedLimit && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Recommendation result underfilled for station {StationId}: requested={Requested}, returned={Returned}, basePool={BasePool}, afterIgnore={AfterIgnore}, afterLibraryExclusion={AfterLibraryExclusion}.",
                scope.StationId,
                cappedLimit,
                cappedTracks.Count,
                basePoolTrackCount,
                eligibleTrackCount,
                nonLibraryTrackCount);
        }

        var imageUrl = stationImageUrl
            ?? cappedTracks
                .Select(track => track.Album?.CoverMedium)
                .FirstOrDefault(cover => !string.IsNullOrWhiteSpace(cover))
            ?? basePool.Station.ImageUrl;

        return new RecommendationDetailDto(
            basePool.Station with
            {
                TrackCount = cappedTracks.Count,
                ImageUrl = imageUrl
            },
            cappedTracks,
            basePool.GeneratedAtUtc);
    }

    private async Task<RecommendationDetailDto?> GetOrBuildDailyPoolAsync(
        string cacheKey,
        RecommendationScope scope,
        DateOnly dayUtc,
        string? stationImageUrl,
        CancellationToken cancellationToken)
    {
        if (_dailyPoolCache.TryGetValue(cacheKey, out var cachedPool))
        {
            return cachedPool;
        }

        var basePool = await TryLoadPersistedDailyPoolAsync(scope, dayUtc, stationImageUrl, cancellationToken);
        if (basePool == null)
        {
            basePool = await BuildDailyPoolAsync(scope, dayUtc, stationImageUrl, cancellationToken);
            if (basePool == null)
            {
                return null;
            }

            await PersistDailyPoolAsync(scope, dayUtc, basePool, cancellationToken);
        }

        _dailyPoolCache[cacheKey] = basePool;
        return basePool;
    }

    private async Task<IReadOnlyList<RecommendationTrackDto>> ExcludeTracksAlreadyInLibraryAsync(
        long libraryId,
        long? folderId,
        IReadOnlyList<RecommendationTrackDto> tracks,
        CancellationToken cancellationToken)
    {
        if (libraryId <= 0 || tracks.Count == 0 || !_repository.IsConfigured)
        {
            return tracks;
        }

        var inputs = tracks
            .Select(track => new LibraryRepository.LibraryExistenceInput(
                NormalizeText(track.Isrc, string.Empty),
                NormalizeText(track.Title, string.Empty),
                NormalizeText(track.Artist?.Name, string.Empty),
                track.Duration > 0 ? track.Duration * 1000 : null))
            .ToList();

        var exists = await _repository.ExistsInLibraryAsync(
            libraryId,
            folderId,
            inputs,
            cancellationToken);

        if (exists.Count == 0)
        {
            return tracks;
        }

        var filtered = new List<RecommendationTrackDto>(tracks.Count);
        for (var index = 0; index < tracks.Count; index++)
        {
            if (index < exists.Count && exists[index])
            {
                continue;
            }

            filtered.Add(tracks[index] with { TrackPosition = filtered.Count + 1 });
        }

        return filtered;
    }

    public async Task RefreshDailyRecommendationsAsync(CancellationToken cancellationToken = default)
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
        var dayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        var artworkAssignments = BuildRecommendationArtworkAssignments(allRecommendationFolders, nowLocal);
        var watchlistMap = await GetRecommendationWatchlistMapAsync(cancellationToken);

        PruneOldCache(dayUtc);

        foreach (var folder in allRecommendationFolders
                     .Where(folder => folder.LibraryId.HasValue && folder.LibraryId.Value > 0)
                     .OrderBy(folder => folder.LibraryId!.Value)
                     .ThenBy(folder => folder.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scope = BuildScope(folder.LibraryId!.Value, folder);
            watchlistMap.TryGetValue(scope.StationId, out var watchlistEntry);
            var stationImageUrl = !string.IsNullOrWhiteSpace(watchlistEntry?.ImageUrl)
                ? watchlistEntry.ImageUrl
                : ResolveRecommendationArtworkUrl(scope.StationId, artworkAssignments);

            try
            {
                await RefreshShazamScopeAsync(scope, cancellationToken);

                var cacheKey = BuildDailyCacheKey(scope.ScopeKey, dayUtc);
                _dailyPoolCache.TryRemove(cacheKey, out _);

                var dailyPool = await BuildDailyPoolAsync(scope, dayUtc, stationImageUrl, cancellationToken);
                if (dailyPool is not null)
                {
                    await PersistDailyPoolAsync(scope, dayUtc, dailyPool, cancellationToken);
                    _dailyPoolCache[cacheKey] = dailyPool;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to refresh daily recommendations for library {LibraryId}, folder {FolderId}.",
                    scope.LibraryId,
                    scope.FolderId);
            }
        }
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

    private async Task<RecommendationDetailDto?> BuildDailyPoolAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        string? stationImageUrl,
        CancellationToken cancellationToken)
    {
        var libraryDeezerIds = await _repository.GetLibraryDeezerTrackSourceIdsAsync(
            scope.LibraryId,
            scope.FolderId,
            cancellationToken);
        var libraryIdSet = new HashSet<string>(
            libraryDeezerIds
                .Select(NormalizeId)
                .Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.Ordinal);

        var deezerTracks = new List<RecommendationTrackDto>();
        if (libraryIdSet.Count > 0)
        {
            try
            {
                var deezerDetail = await _deezerRecommendations.GetDailyRecommendationsAsync(
                    scope.LibraryId,
                    RecommendationPoolLimit,
                    scope.FolderId,
                    cancellationToken);
                deezerTracks = (deezerDetail?.Tracks ?? Array.Empty<RecommendationTrackDto>())
                    .Where(track => !libraryIdSet.Contains(NormalizeId(track.Id)))
                    .Select(track => NormalizeRecommendationTrack(track))
                    .Where(track => !string.IsNullOrWhiteSpace(track.Id))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to load Deezer recommendations for library {LibraryId}, folder {FolderId}.",
                    scope.LibraryId,
                    scope.FolderId);
            }
        }

        List<RecommendationTrackDto> shazamTracks;
        try
        {
            shazamTracks = await BuildShazamRecommendationsAsync(
                scope,
                dayUtc,
                libraryIdSet,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to build Shazam recommendations for library {LibraryId}, folder {FolderId}.",
                scope.LibraryId,
                scope.FolderId);
            shazamTracks = new List<RecommendationTrackDto>();
        }

        var merged = MergeRotating(
            OrderDeterministically(deezerTracks, dayUtc, "deezer"),
            OrderDeterministically(shazamTracks, dayUtc, "shazam"),
            RecommendationPoolLimit,
            dayUtc);
        var station = new RecommendationStationDto(
            scope.StationId,
            $"Recommendations - {scope.FolderName}",
            BuildDailyRecommendationDescription(scope.FolderName, DateTime.UtcNow.DayOfWeek),
            RecommendationSourceId,
            scope.FolderName,
            Math.Min(MaxDailyRecommendations, merged.Count),
            stationImageUrl);

        return new RecommendationDetailDto(
            station,
            merged,
            DateTimeOffset.UtcNow);
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

            var station = new RecommendationStationDto(
                scope.StationId,
                $"Recommendations - {scope.FolderName}",
                BuildDailyRecommendationDescription(scope.FolderName, DateTime.UtcNow.DayOfWeek),
                RecommendationSourceId,
                scope.FolderName,
                Math.Min(MaxDailyRecommendations, normalizedTracks.Count),
                stationImageUrl);

            return new RecommendationDetailDto(
                station,
                normalizedTracks,
                payload.GeneratedAtUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Failed to load persisted recommendation daily pool for scope {ScopeKey}.",
                    scope.ScopeKey);
            }
            return null;
        }
    }

    private async Task PersistDailyPoolAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        RecommendationDetailDto detail,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = new PersistedDailyPoolDto(
                detail.GeneratedAtUtc,
                detail.Tracks
                    .Select(NormalizeRecommendationTrack)
                    .Where(track => !string.IsNullOrWhiteSpace(track.Id))
                    .Select((track, index) => track with { TrackPosition = index + 1 })
                    .ToList());

            await _repository.UpsertPlaylistTrackCandidateCacheAsync(
                DailyPoolCacheSource,
                scope.ScopeKey,
                BuildDailyPoolSnapshotId(dayUtc),
                JsonSerializer.Serialize(payload),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Failed to persist recommendation daily pool for scope {ScopeKey}.",
                    scope.ScopeKey);
            }
        }
    }

    private static string BuildDailyPoolSnapshotId(DateOnly dayUtc)
        => $"{DailyPoolSnapshotVersion}:{dayUtc:yyyyMMdd}";

    private static string NormalizeDailyPoolSnapshotId(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private async Task<List<RecommendationTrackDto>> BuildShazamRecommendationsAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        HashSet<string> libraryIdSet,
        CancellationToken cancellationToken)
    {
        if (!IsShazamRecommendationAvailable())
        {
            return new List<RecommendationTrackDto>();
        }

        var orderedSeeds = await GetOrderedShazamSeedTrackIdsAsync(scope, dayUtc, cancellationToken);
        if (orderedSeeds.Count == 0)
        {
            return new List<RecommendationTrackDto>();
        }

        await RefreshStaleShazamSeedsAsync(scope, orderedSeeds, cancellationToken);

        var cacheByTrackId = await _repository.GetShazamTrackCacheByTrackIdForLibraryAsync(
            scope.LibraryId,
            scope.FolderId,
            cancellationToken);
        return BuildRecommendationsFromShazamCache(orderedSeeds, cacheByTrackId, libraryIdSet, cancellationToken);
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

    private async Task<List<long>> GetOrderedShazamSeedTrackIdsAsync(
        RecommendationScope scope,
        DateOnly dayUtc,
        CancellationToken cancellationToken)
    {
        var localTrackIds = await _repository.GetTrackIdsForLibraryScopeAsync(
            scope.LibraryId,
            scope.FolderId,
            cancellationToken);
        if (localTrackIds.Count == 0)
        {
            return new List<long>();
        }

        return localTrackIds
            .OrderBy(trackId => ComputeDailyScore(trackId.ToString(), dayUtc))
            .ToList();
    }

    private async Task RefreshStaleShazamSeedsAsync(
        RecommendationScope scope,
        IReadOnlyList<long> orderedSeeds,
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
        var inlineTargets = orderedSeeds
            .Where(staleSet.Contains)
            .Take(ShazamInlineRefreshBudget)
            .ToList();

        if (inlineTargets.Count > 0)
        {
            await RefreshShazamCacheForTrackBatchAsync(inlineTargets, cancellationToken);
        }

        if (staleTrackIds.Count > inlineTargets.Count)
        {
            StartBackgroundShazamRefresh(scope, null);
        }
    }

    private static List<RecommendationTrackDto> BuildRecommendationsFromShazamCache(
        IReadOnlyList<long> orderedSeeds,
        IReadOnlyDictionary<long, ShazamTrackCacheDto> cacheByTrackId,
        HashSet<string> libraryIdSet,
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
                TryAddShazamRelatedRecommendation(relatedTrack, libraryIdSet, seen, results);
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

    private async Task RefreshShazamScopeAsync(
        RecommendationScope scope,
        CancellationToken cancellationToken)
    {
        if (!_shazamRecognitionService.IsAvailable)
        {
            return;
        }

        var staleBeforeUtc = DateTimeOffset.UtcNow - ShazamCacheTtl;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await _repository.GetTrackIdsNeedingShazamRefreshAsync(
                scope.LibraryId,
                staleBeforeUtc,
                scope.FolderId,
                ShazamBackgroundBatchSize,
                cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            await RefreshShazamCacheForTrackBatchAsync(batch, cancellationToken);
        }
    }

    private static void TryAddShazamRelatedRecommendation(
        RecommendationTrackDto track,
        HashSet<string> libraryIdSet,
        HashSet<string> seen,
        List<RecommendationTrackDto> results)
    {
        var normalized = NormalizeRecommendationTrack(track);
        var deezerId = NormalizeId(normalized.Id);
        if (string.IsNullOrWhiteSpace(deezerId)
            || libraryIdSet.Contains(deezerId)
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

        _ = Task.Run(async () =>
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background Shazam library scan failed for scope {ScopeKey}.", scope.ScopeKey);
            }
            finally
            {
                _backgroundScans.TryRemove(scope.ScopeKey, out _);
            }
        });

        return true;
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
        var relatedCards = await TryFetchRelatedShazamTracksAsync(trackId, recognizedTrack, scannedAtUtc, cancellationToken);
        if (relatedCards is null)
        {
            return;
        }

        var relatedRecommendations = await BuildRelatedShazamRecommendationsAsync(
            relatedCards,
            deezerResolveCache,
            cancellationToken);
        await PersistMatchedShazamCacheAsync(trackId, recognizedTrack, relatedRecommendations, scannedAtUtc, cancellationToken);
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Shazam recognition failed for library track {TrackId}.", trackId);
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
                    "Shazam recognition failed."),
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Shazam related-track fetch failed for track {TrackId} ({ShazamTrackId}).",
                    trackId,
                    recognizedTrack.ShazamTrackId);
            }
            await PersistMatchedShazamCacheAsync(
                trackId,
                recognizedTrack,
                Array.Empty<RecommendationTrackDto>(),
                scannedAtUtc,
                cancellationToken);
            return null;
        }
    }

    private async Task<List<RecommendationTrackDto>> BuildRelatedShazamRecommendationsAsync(
        IReadOnlyList<ShazamTrackCard> relatedCards,
        IDictionary<string, string> deezerResolveCache,
        CancellationToken cancellationToken)
    {
        var relatedRecommendations = new List<RecommendationTrackDto>();
        var seenDeezerIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var card in relatedCards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deezerId = NormalizeId(await ResolveDeezerIdAsync(card, deezerResolveCache, cancellationToken));
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
        CancellationToken cancellationToken)
    {
        await _repository.UpsertTrackShazamCacheAsync(
            new LibraryRepository.TrackShazamCacheUpsertInput(
                trackId,
                StatusMatched,
                recognizedTrack.ShazamTrackId,
                NormalizeText(recognizedTrack.Recognition.Title, string.Empty),
                GetRecognitionArtist(recognizedTrack.Recognition),
                NormalizeText(recognizedTrack.Recognition.Isrc, string.Empty),
                relatedRecommendations,
                scannedAtUtc,
                null),
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
            resolved = NormalizeId(await ResolveDeezerIdFromShazamLinkAsync(deezerLink, card, cancellationToken));
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                break;
            }
        }

        cache[cacheKey] = resolved;
        return resolved;
    }

    private async Task<string> ResolveDeezerIdFromShazamLinkAsync(
        string deezerLink,
        ShazamTrackCard sourceCard,
        CancellationToken cancellationToken)
    {
        var directId = TryExtractDeezerTrackId(deezerLink);
        if (!string.IsNullOrWhiteSpace(directId))
        {
            return directId;
        }

        var deezerQuery = TryBuildDeezerSearchQueryFromLink(deezerLink);
        if (string.IsNullOrWhiteSpace(deezerQuery))
        {
            return string.Empty;
        }

        return await ResolveDeezerIdFromDeezerQueryAsync(deezerQuery, sourceCard, cancellationToken);
    }

    private async Task<string> ResolveDeezerIdFromDeezerQueryAsync(
        string deezerQuery,
        ShazamTrackCard sourceCard,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deezerQuery))
        {
            return string.Empty;
        }

        try
        {
            var result = await _deezerClient.SearchTrackAsync(deezerQuery, new ApiOptions { Limit = 25 })
                .WaitAsync(cancellationToken);
            return SelectBestDeezerSearchCandidateId(result, sourceCard);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer query resolve failed for Shazam Deezer query '{Query}'.", deezerQuery);
            }
            return string.Empty;
        }
    }

    private string SelectBestDeezerSearchCandidateId(
        DeezerSearchResult? result,
        ShazamTrackCard sourceCard)
    {
        if (result?.Data == null || result.Data.Length == 0)
        {
            return string.Empty;
        }

        var firstId = string.Empty;
        foreach (var item in result.Data)
        {
            if (!TryParseDeezerSearchCandidate(item, out var deezerId, out var candidate))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(firstId))
            {
                firstId = deezerId;
            }

            if (candidate != null && ValidateDeezerTrackCandidate(deezerId, candidate, sourceCard))
            {
                return deezerId;
            }
        }

        return firstId;
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

    private bool ValidateDeezerTrackCandidate(
        string deezerId,
        ApiTrack? candidate,
        ShazamTrackCard sourceCard)
    {
        if (candidate is null)
        {
            return false;
        }

        if (HasIsrcMismatch(sourceCard.Isrc, candidate.Isrc))
        {
            return false;
        }

        var sourceTitle = NormalizeMatchText(sourceCard.Title);
        var candidateTitle = NormalizeMatchText(BuildCandidateTitle(candidate));
        var titleScore = ComputeTokenSimilarity(sourceTitle, candidateTitle);
        if (!string.IsNullOrWhiteSpace(sourceTitle) && titleScore < ShazamDeezerMinTitleSimilarity)
        {
            return false;
        }

        var sourceArtist = NormalizeMatchText(sourceCard.Artist);
        var candidateArtist = NormalizeMatchText(candidate.Artist?.Name);
        var artistScore = ComputeTokenSimilarity(sourceArtist, candidateArtist);
        if (!string.IsNullOrWhiteSpace(sourceArtist) && artistScore < ShazamDeezerMinArtistSimilarity)
        {
            return false;
        }

        if (HasDurationMismatch(sourceCard.DurationMs, candidate.Duration, titleScore))
        {
            return false;
        }

        if (HasDerivativeVersionMismatch(sourceCard, candidate))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Rejected derivative mismatch for Shazam recommendation candidate {DeezerId}. Source='{SourceTitle}' Candidate='{CandidateTitle}'",
                    deezerId,
                    sourceCard.Title,
                    BuildCandidateTitle(candidate));
            }
            return false;
        }

        return true;
    }

    private static bool HasIsrcMismatch(string? sourceIsrc, string? candidateIsrc)
    {
        var source = NormalizeText(sourceIsrc, string.Empty);
        var candidate = NormalizeText(candidateIsrc, string.Empty);
        return !string.IsNullOrWhiteSpace(source)
            && !string.IsNullOrWhiteSpace(candidate)
            && !string.Equals(source, candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDurationMismatch(int? sourceDurationMs, int candidateDurationSeconds, double titleScore)
    {
        if (sourceDurationMs is not > 0 || candidateDurationSeconds <= 0)
        {
            return false;
        }

        var sourceSeconds = (int)Math.Round(sourceDurationMs.Value / 1000d);
        var durationDiff = Math.Abs(sourceSeconds - candidateDurationSeconds);
        return durationDiff > ShazamDeezerMaxDurationDeltaSeconds && titleScore < 0.90d;
    }

    private static bool HasDerivativeVersionMismatch(ShazamTrackCard sourceCard, ApiTrack candidate)
    {
        var sourceTerms = ExtractDerivativeTerms(string.Join(' ',
            sourceCard.Title,
            sourceCard.Artist,
            sourceCard.Album ?? string.Empty));
        var candidateTerms = ExtractDerivativeTerms(string.Join(' ',
            BuildCandidateTitle(candidate),
            candidate.Artist?.Name ?? string.Empty,
            candidate.Album?.Title ?? string.Empty));

        if (candidateTerms.Count == 0)
        {
            return false;
        }

        if (sourceTerms.Count == 0)
        {
            return true;
        }

        return !candidateTerms.IsSubsetOf(sourceTerms);
    }

    private static HashSet<string> ExtractDerivativeTerms(string input)
    {
        var normalized = NormalizeMatchText(input);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var terms = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => RejectedDerivativeTerms.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return terms;
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
            foreach (var deezerUrl in deezerUrls)
            {
                var normalized = NormalizeText(deezerUrl, string.Empty);
                if (string.IsNullOrWhiteSpace(normalized)
                    || !IsDeezerLinkCandidate(normalized)
                    || !seen.Add(normalized))
                {
                    continue;
                }

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

        var normalized = value.Trim();
        return long.TryParse(normalized, out _)
               || normalized.StartsWith("deezer-query://", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("deezer://", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("deezer:track:", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("deezer.com/track/", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("deezer.com/play", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryBuildDeezerSearchQueryFromLink(string? deezerLink)
    {
        var queryValue = TryGetQueryParameterValue(deezerLink, "query");
        if (string.IsNullOrWhiteSpace(queryValue))
        {
            return string.Empty;
        }

        var decoded = DecodeQueryValue(queryValue);
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return string.Empty;
        }

        var trackTerm = ExtractDeezerQueryTerm(decoded, "track:");
        var artistTerm = ExtractDeezerQueryTerm(decoded, "artist:");
        if (!string.IsNullOrWhiteSpace(trackTerm) && !string.IsNullOrWhiteSpace(artistTerm))
        {
            return $"track:\"{trackTerm}\" artist:\"{artistTerm}\"";
        }

        if (!string.IsNullOrWhiteSpace(trackTerm))
        {
            return $"track:\"{trackTerm}\"";
        }

        return NormalizeText(decoded, string.Empty);
    }

    private static string TryGetQueryParameterValue(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(parameterName))
        {
            return string.Empty;
        }

        var trimmedValue = value.Trim();
        if (Uri.TryCreate(trimmedValue, UriKind.Absolute, out var uri))
        {
            return TryGetQueryValueFromAbsoluteUri(uri, parameterName);
        }

        return TryGetQueryValueFromRawText(trimmedValue, parameterName);
    }

    private static string TryGetQueryValueFromAbsoluteUri(Uri uri, string parameterName)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (string.Equals(parts[0], parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return parts.Length == 2 ? parts[1] : string.Empty;
            }
        }

        return string.Empty;
    }

    private static string TryGetQueryValueFromRawText(string value, string parameterName)
    {
        var marker = $"{parameterName}=";
        var markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var start = markerIndex + marker.Length;
        var end = value.IndexOf('&', start);
        return end >= 0 ? value[start..end] : value[start..];
    }

    private static string DecodeQueryValue(string value)
    {
        var decoded = value.Replace('+', ' ');
        for (var i = 0; i < 2; i++)
        {
            string unescaped;
            try
            {
                unescaped = Uri.UnescapeDataString(decoded);
            }
            catch
            {
                break;
            }

            if (string.Equals(decoded, unescaped, StringComparison.Ordinal))
            {
                break;
            }

            decoded = unescaped;
        }

        return decoded.Trim();
    }

    private static string ExtractDeezerQueryTerm(string value, string marker)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(marker))
        {
            return string.Empty;
        }

        var markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var start = markerIndex + marker.Length;
        var end = value.Length;
        var nextMarkers = new[] { " track:", " artist:", " album:", " genre:", " label:", " isrc:" };
        foreach (var nextMarker in nextMarkers)
        {
            var index = value.IndexOf(nextMarker, start, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && index < end)
            {
                end = index;
            }
        }

        if (start >= end)
        {
            return string.Empty;
        }

        var normalized = NormalizeText(value[start..end], string.Empty);
        normalized = normalized.Trim('{', '}', '[', ']', '(', ')', '"', '\'');
        return NormalizeText(normalized, string.Empty);
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

        if (string.Equals(uri.Scheme, "deezer", StringComparison.OrdinalIgnoreCase)
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

        var deezerLink = await TryResolveAndPersistSongLinkSourcesAsync(
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
            "deezer",
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

    private async Task<(string DeezerId, string DeezerUrl)> TryResolveAndPersistSongLinkSourcesAsync(
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
            var linked = await _songLinkResolver.ResolveByUrlAsync(preferredUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(appleUrl))
            {
                await TryPersistPlatformSourceLinkAsync(
                    trackId,
                    "apple",
                    linked?.AppleMusicUrl,
                    TryExtractAppleTrackId,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(spotifyUrl))
            {
                await TryPersistPlatformSourceLinkAsync(
                    trackId,
                    "spotify",
                    linked?.SpotifyUrl,
                    TryExtractSpotifyTrackId,
                    cancellationToken);
            }

            return (
                NormalizeId(linked?.DeezerId),
                NormalizeOptionalText(linked?.DeezerUrl) ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "SongLink source-link persistence failed for library track {TrackId}.", trackId);
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
        catch (Exception ex)
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
        catch (Exception ex)
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
        IReadOnlyList<RecommendationTrackDto> fallbackCandidates,
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
            AddTopUpTracks(fallbackCandidates, output, seen, cappedLimit, dayUtc);
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
        IReadOnlyList<RecommendationTrackDto> fallbackCandidates,
        List<RecommendationTrackDto> output,
        HashSet<string> seen,
        int limit,
        DateOnly dayUtc)
    {
        var remaining = fallbackCandidates
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

        var fallbackArtist = NormalizeText(track.Artist.Name, UnknownArtist);
        var fallbackTitle = NormalizeText(track.Title, UnknownTitle);
        return $"fallback:{fallbackArtist.ToLowerInvariant()}|{fallbackTitle.ToLowerInvariant()}";
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
        if (unresolvedSet.Count > 0)
        {
            await EnrichRecommendationsFromFallbackAsync(normalized, pendingByTrackId, unresolvedSet, cancellationToken);
        }

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
                foreach (var gatewayTrack in gatewayTracks)
                {
                    var deezerMetadata = MapGatewayTrack(gatewayTrack);
                    var deezerId = NormalizeId(deezerMetadata.Id);
                    if (string.IsNullOrWhiteSpace(deezerId) || !unresolvedTrackIds.Remove(deezerId))
                    {
                        continue;
                    }

                    _deezerRecommendationMetadataCache[deezerId] = deezerMetadata;
                    ApplyRecommendationMetadata(normalized, pendingByTrackId, deezerId, deezerMetadata);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Recommendation metadata batch enrichment failed for {Count} tracks.", batch.Count);
                }
            }
        }
    }

    private async Task EnrichRecommendationsFromFallbackAsync(
        RecommendationTrackDto[] normalized,
        Dictionary<string, List<int>> pendingByTrackId,
        HashSet<string> unresolvedTrackIds,
        CancellationToken cancellationToken)
    {
        foreach (var unresolvedId in unresolvedTrackIds.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fallbackMetadata = await TryGetFallbackRecommendationMetadataAsync(unresolvedId);
            if (fallbackMetadata == null)
            {
                continue;
            }

            var deezerId = NormalizeId(fallbackMetadata.Id);
            if (string.IsNullOrWhiteSpace(deezerId) || !unresolvedTrackIds.Remove(deezerId))
            {
                continue;
            }

            _deezerRecommendationMetadataCache[deezerId] = fallbackMetadata;
            ApplyRecommendationMetadata(normalized, pendingByTrackId, deezerId, fallbackMetadata);
        }
    }

    private async Task<RecommendationTrackDto?> TryGetFallbackRecommendationMetadataAsync(
        string unresolvedId)
    {
        try
        {
            var track = await _deezerClient.GetTrackWithFallbackAsync(unresolvedId);
            return MapGatewayTrack(track);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DeezerGatewayException ex) when (IsMissingSongData(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Recommendation metadata fallback confirmed missing for Deezer track {TrackId}. Keeping base recommendation payload.",
                    unresolvedId);
            }
            return null;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Recommendation metadata fallback enrichment failed for Deezer track {TrackId}.", unresolvedId);
            }
            return null;
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
        var mergedArtistName = IsMissingOrUnknown(current.Artist?.Name, UnknownArtist)
            ? NormalizeText(deezerMetadata.Artist?.Name, UnknownArtist)
            : NormalizeText(current.Artist?.Name, UnknownArtist);
        var mergedAlbumId = !string.IsNullOrWhiteSpace(current.Album?.Id)
            ? NormalizeReferenceId(current.Album.Id)
            : NormalizeReferenceId(deezerMetadata.Album?.Id);
        var mergedAlbumTitle = IsMissingOrUnknown(current.Album?.Title, UnknownAlbum)
            ? NormalizeText(deezerMetadata.Album?.Title, UnknownAlbum)
            : NormalizeText(current.Album?.Title, UnknownAlbum);
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
            || IsMissingOrUnknown(track.Artist?.Name, UnknownArtist)
            || IsMissingOrUnknown(track.Album?.Title, UnknownAlbum)
            || string.IsNullOrWhiteSpace(track.Album?.CoverMedium);
    }

    private static bool IsMissingOrUnknown(string? value, string unknownLabel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return string.Equals(value.Trim(), unknownLabel, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMissingSongData(DeezerGatewayException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("No song data", StringComparison.OrdinalIgnoreCase)
               || message.Contains("DATA_ERROR", StringComparison.OrdinalIgnoreCase);
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

    private static string NormalizeText(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = string.Join(
            " ",
            value
                .Trim()
                .Replace('\u2013', '-')
                .Replace('\u2014', '-')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
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
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && string.Equals(parts[0], "i", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeId(Uri.UnescapeDataString(parts[1]));
            }
        }

        var lastSegment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return NormalizeId(lastSegment);
    }
}
