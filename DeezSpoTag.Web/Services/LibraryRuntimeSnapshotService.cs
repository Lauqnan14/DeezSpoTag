using DeezSpoTag.Services.Library;
using System.Globalization;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryRuntimeSnapshotService
{
    private const int DefaultScanStatusActiveMs = 5000;
    private const int DefaultScanStatusIdleMs = 15000;
    private const int DefaultAnalysisMs = 15000;
    private const int DefaultMinArtistRefreshMs = 10000;

    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly LibraryScanRunner _scanRunner;
    private readonly LibraryStatsSnapshotService _libraryStatsSnapshotService;
    private readonly ILogger<LibraryRuntimeSnapshotService> _logger;
    private readonly object _statsCacheLock = new();
    private readonly Dictionary<string, CachedStatsSnapshot> _activeScanStatsCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan ActiveScanStatsTtl = TimeSpan.FromSeconds(5);

    private sealed record CachedStatsSnapshot(DateTimeOffset ExpiresAtUtc, object Payload);
    private sealed record ScanStatusSnapshot(bool Running, object Payload);

    public LibraryRuntimeSnapshotService(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        LibraryScanRunner scanRunner,
        LibraryStatsSnapshotService libraryStatsSnapshotService,
        ILogger<LibraryRuntimeSnapshotService> logger)
    {
        _repository = repository;
        _configStore = configStore;
        _scanRunner = scanRunner;
        _libraryStatsSnapshotService = libraryStatsSnapshotService;
        _logger = logger;
    }

    public sealed record LibraryRefreshPolicyDto(
        int ScanStatusActiveMs,
        int ScanStatusIdleMs,
        int AnalysisMs,
        int MinArtistRefreshMs);

    public sealed record LibraryRuntimeSnapshotDto(
        object ScanStatus,
        object Stats,
        LibraryRefreshPolicyDto RefreshPolicy);

    public async Task<LibraryRuntimeSnapshotDto> BuildSnapshotAsync(long? folderId, CancellationToken cancellationToken)
    {
        var scanStatus = BuildScanStatus();
        var stats = await BuildStatsPayloadAsync(folderId, scanStatus.Running, cancellationToken);
        var refreshPolicy = BuildRefreshPolicy();
        return new LibraryRuntimeSnapshotDto(scanStatus.Payload, stats, refreshPolicy);
    }

    private ScanStatusSnapshot BuildScanStatus()
    {
        var lastScan = _configStore.GetLastScanInfo();
        var scanStatus = _scanRunner.GetStatus();

        return new ScanStatusSnapshot(scanStatus.IsRunning, new
        {
            lastRunUtc = lastScan.LastRunUtc,
            lastCounts = new
            {
                artists = lastScan.ArtistCount,
                albums = lastScan.AlbumCount,
                tracks = lastScan.TrackCount
            },
            running = scanStatus.IsRunning,
            progress = new
            {
                processedFiles = scanStatus.ProcessedFiles,
                totalFiles = scanStatus.TotalFiles,
                errorCount = scanStatus.ErrorCount,
                currentFile = scanStatus.CurrentFile,
                artistsDetected = scanStatus.ArtistsDetected,
                albumsDetected = scanStatus.AlbumsDetected,
                tracksDetected = scanStatus.TracksDetected
            },
            dbConfigured = _repository.IsConfigured
        });
    }

    private async Task<object> BuildStatsPayloadAsync(long? folderId, bool running, CancellationToken cancellationToken)
    {
        if (!running)
        {
            return await _libraryStatsSnapshotService.BuildStatsPayloadAsync(folderId, cancellationToken);
        }

        var cacheKey = folderId?.ToString(CultureInfo.InvariantCulture) ?? "all";
        var now = DateTimeOffset.UtcNow;
        lock (_statsCacheLock)
        {
            if (_activeScanStatsCache.TryGetValue(cacheKey, out var cached)
                && cached.ExpiresAtUtc >= now)
            {
                _logger.LogDebug("Library runtime stats cache hit for key {CacheKey} during active scan.", cacheKey);
                return cached.Payload;
            }
        }

        _logger.LogDebug("Library runtime stats cache miss for key {CacheKey} during active scan.", cacheKey);
        var payload = await _libraryStatsSnapshotService.BuildStatsPayloadAsync(folderId, cancellationToken);
        lock (_statsCacheLock)
        {
            _activeScanStatsCache[cacheKey] = new CachedStatsSnapshot(now.Add(ActiveScanStatsTtl), payload);
        }

        return payload;
    }

    private static LibraryRefreshPolicyDto BuildRefreshPolicy()
    {
        return new LibraryRefreshPolicyDto(
            ScanStatusActiveMs: ReadInterval("DEEZSPOTAG_LIBRARY_SCAN_STATUS_ACTIVE_MS", DefaultScanStatusActiveMs, min: 1000, max: 120000),
            ScanStatusIdleMs: ReadInterval("DEEZSPOTAG_LIBRARY_SCAN_STATUS_IDLE_MS", DefaultScanStatusIdleMs, min: 2000, max: 300000),
            AnalysisMs: ReadInterval("DEEZSPOTAG_LIBRARY_ANALYSIS_STATUS_MS", DefaultAnalysisMs, min: 5000, max: 300000),
            MinArtistRefreshMs: ReadInterval("DEEZSPOTAG_LIBRARY_MIN_ARTIST_REFRESH_MS", DefaultMinArtistRefreshMs, min: 1000, max: 120000));
    }

    private static int ReadInterval(string name, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(raw, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, min, max);
    }
}
