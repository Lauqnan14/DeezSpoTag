using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeezSpoTag.Services.Download.Shared.Advanced;

/// <summary>
/// Performance optimization service for deezspotag downloads
/// Implements adaptive performance tuning and monitoring
/// </summary>
public class PerformanceOptimizationService : IDisposable
{
    private readonly ILogger<PerformanceOptimizationService> _logger;
    private readonly ConcurrentDictionary<string, PerformanceMetric> _metrics = new();
    private readonly ConcurrentQueue<DownloadPerformanceData> _performanceHistory = new();
    private readonly Timer _optimizationTimer;
    private readonly object _optimizationLock = new();
    private bool _disposed;

    private PerformanceProfile _currentProfile = PerformanceProfile.Balanced;
    private DateTime _lastOptimization = DateTime.MinValue;
    private readonly TimeSpan _optimizationInterval = TimeSpan.FromMinutes(2);
    private const int MaxHistorySize = 1000;

    public PerformanceOptimizationService(ILogger<PerformanceOptimizationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Start optimization timer
        _optimizationTimer = new Timer(OptimizePerformance, null, _optimizationInterval, _optimizationInterval);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Performance optimization service initialized with {Profile} profile", _currentProfile);        }
    }

    /// <summary>
    /// Record download performance data
    /// </summary>
    public void RecordDownloadPerformance(DownloadPerformanceData data)
    {
        try
        {
            _performanceHistory.Enqueue(data);

            // Maintain history size
            while (_performanceHistory.Count > MaxHistorySize)
            {
                _performanceHistory.TryDequeue(out _);
            }

            // Update metrics
            UpdateMetrics(data);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Recorded performance data: {DownloadId} - {Duration}ms, Success: {Success}",
                    data.DownloadId, data.Duration.TotalMilliseconds, data.Success);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error recording download performance data");
        }
    }

    /// <summary>
    /// Get current performance metrics
    /// </summary>
    public PerformanceMetrics GetCurrentMetrics()
    {
        try
        {
            var recentData = GetRecentPerformanceData(TimeSpan.FromMinutes(10));

            if (recentData.Count == 0)
            {
                return new PerformanceMetrics
                {
                    ErrorRate = 0,
                    AverageResponseTime = TimeSpan.Zero,
                    TotalRequests = 0,
                    FailedRequests = 0
                };
            }

            var totalRequests = recentData.Count;
            var failedRequests = recentData.Count(d => !d.Success);
            var errorRate = (double)failedRequests / totalRequests;
            var averageResponseTime = TimeSpan.FromMilliseconds(
                recentData.Average(d => d.Duration.TotalMilliseconds));

            return new PerformanceMetrics
            {
                ErrorRate = errorRate,
                AverageResponseTime = averageResponseTime,
                TotalRequests = totalRequests,
                FailedRequests = failedRequests
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error calculating current metrics");
            return new PerformanceMetrics();
        }
    }

    /// <summary>
    /// Get optimized settings based on current performance
    /// </summary>
    public OptimizedSettings GetOptimizedSettings(DeezSpoTagSettings baseSettings)
    {
        try
        {
            var metrics = GetCurrentMetrics();
            var profile = DetermineOptimalProfile(metrics);

            return ApplyPerformanceProfile(baseSettings, profile, metrics);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting optimized settings");
            return new OptimizedSettings
            {
                Settings = baseSettings,
                Profile = _currentProfile,
                Reason = "Error occurred during optimization"
            };
        }
    }

    /// <summary>
    /// Get performance recommendations
    /// </summary>
    public List<PerformanceRecommendation> GetRecommendations()
    {
        try
        {
            var recommendations = new List<PerformanceRecommendation>();
            var metrics = GetCurrentMetrics();

            // High error rate recommendations
            if (metrics.ErrorRate > 0.1)
            {
                recommendations.Add(new PerformanceRecommendation
                {
                    Type = RecommendationType.ReduceConcurrency,
                    Priority = RecommendationPriority.High,
                    Description = $"High error rate detected ({metrics.ErrorRate:P1}). Consider reducing concurrent downloads.",
                    Impact = "Improved stability and success rate"
                });
            }

            // High response time recommendations
            if (metrics.AverageResponseTime > TimeSpan.FromSeconds(10))
            {
                recommendations.Add(new PerformanceRecommendation
                {
                    Type = RecommendationType.ReduceConcurrency,
                    Priority = RecommendationPriority.Medium,
                    Description = $"High response times detected ({metrics.AverageResponseTime.TotalSeconds:F1}s). Consider reducing load.",
                    Impact = "Faster individual downloads"
                });
            }

            // Low utilization recommendations
            if (metrics.ErrorRate < 0.02 && metrics.AverageResponseTime < TimeSpan.FromSeconds(3))
            {
                recommendations.Add(new PerformanceRecommendation
                {
                    Type = RecommendationType.IncreaseConcurrency,
                    Priority = RecommendationPriority.Low,
                    Description = "Performance is good. Consider increasing concurrency for faster overall throughput.",
                    Impact = "Higher overall download speed"
                });
            }

            // Network optimization recommendations
            var networkMetrics = AnalyzeNetworkPerformance();
            if (networkMetrics.HasSlowNetwork)
            {
                recommendations.Add(new PerformanceRecommendation
                {
                    Type = RecommendationType.OptimizeNetwork,
                    Priority = RecommendationPriority.Medium,
                    Description = "Slow network detected. Consider enabling compression and reducing quality for faster downloads.",
                    Impact = "Better performance on slow connections"
                });
            }

            return recommendations;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error generating performance recommendations");
            return new List<PerformanceRecommendation>();
        }
    }

    /// <summary>
    /// Get performance statistics
    /// </summary>
    public PerformanceStatistics GetStatistics(TimeSpan? period = null)
    {
        try
        {
            var timeframe = period ?? TimeSpan.FromHours(1);
            var data = GetRecentPerformanceData(timeframe);

            if (data.Count == 0)
            {
                return new PerformanceStatistics
                {
                    Period = timeframe,
                    TotalDownloads = 0
                };
            }

            var successful = data.Where(d => d.Success).ToList();
            var failed = data.Where(d => !d.Success).ToList();

            return new PerformanceStatistics
            {
                Period = timeframe,
                TotalDownloads = data.Count,
                SuccessfulDownloads = successful.Count,
                FailedDownloads = failed.Count,
                SuccessRate = (double)successful.Count / data.Count,
                AverageDownloadTime = successful.Count > 0 ?
                    TimeSpan.FromMilliseconds(successful.Average(d => d.Duration.TotalMilliseconds)) :
                    TimeSpan.Zero,
                MedianDownloadTime = successful.Count > 0 ?
                    CalculateMedian(successful.Select(d => d.Duration)) :
                    TimeSpan.Zero,
                FastestDownload = successful.Count > 0 ? successful.Min(d => d.Duration) : TimeSpan.Zero,
                SlowestDownload = successful.Count > 0 ? successful.Max(d => d.Duration) : TimeSpan.Zero,
                TotalDataTransferred = data.Sum(d => d.BytesTransferred),
                AverageSpeed = CalculateAverageSpeed(successful),
                ErrorsByType = failed.GroupBy(d => d.ErrorType ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.Count())
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error calculating performance statistics");
            return new PerformanceStatistics { Period = period ?? TimeSpan.FromHours(1) };
        }
    }

    /// <summary>
    /// Periodic performance optimization
    /// </summary>
    private void OptimizePerformance(object? state)
    {
        try
        {
            lock (_optimizationLock)
            {
                if (DateTime.UtcNow - _lastOptimization < _optimizationInterval)
                {
                    return;
                }
                _lastOptimization = DateTime.UtcNow;
            }

            var metrics = GetCurrentMetrics();
            var newProfile = DetermineOptimalProfile(metrics);

            if (newProfile != _currentProfile)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Performance profile changed from {OldProfile} to {NewProfile} based on metrics: ErrorRate={ErrorRate:P1}, AvgResponseTime={ResponseTime}ms",
                        _currentProfile, newProfile, metrics.ErrorRate, metrics.AverageResponseTime.TotalMilliseconds);                }

                _currentProfile = newProfile;
            }

            // Clean up old metrics
            CleanupOldMetrics();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during performance optimization");
        }
    }

    /// <summary>
    /// Update performance metrics
    /// </summary>
    private void UpdateMetrics(DownloadPerformanceData data)
    {
        var key = $"{data.DownloadType}_{DateTime.UtcNow:yyyyMMddHH}";

        _metrics.AddOrUpdate(key,
            new PerformanceMetric
            {
                TotalRequests = 1,
                SuccessfulRequests = data.Success ? 1 : 0,
                TotalResponseTime = data.Duration,
                LastUpdated = DateTime.UtcNow
            },
            (k, existing) => new PerformanceMetric
            {
                TotalRequests = existing.TotalRequests + 1,
                SuccessfulRequests = existing.SuccessfulRequests + (data.Success ? 1 : 0),
                TotalResponseTime = existing.TotalResponseTime + data.Duration,
                LastUpdated = DateTime.UtcNow
            });
    }

    /// <summary>
    /// Get recent performance data
    /// </summary>
    private List<DownloadPerformanceData> GetRecentPerformanceData(TimeSpan timeframe)
    {
        var cutoff = DateTime.UtcNow - timeframe;
        return _performanceHistory.Where(d => d.Timestamp >= cutoff).ToList();
    }

    /// <summary>
    /// Determine optimal performance profile
    /// </summary>
    private static PerformanceProfile DetermineOptimalProfile(PerformanceMetrics metrics)
    {
        // High error rate or slow response times - use conservative profile
        if (metrics.ErrorRate > 0.15 || metrics.AverageResponseTime > TimeSpan.FromSeconds(15))
        {
            return PerformanceProfile.Conservative;
        }

        // Good performance - use aggressive profile
        if (metrics.ErrorRate < 0.02 && metrics.AverageResponseTime < TimeSpan.FromSeconds(3))
        {
            return PerformanceProfile.Aggressive;
        }

        // Default to balanced
        return PerformanceProfile.Balanced;
    }

    /// <summary>
    /// Apply performance profile to settings
    /// </summary>
    private static OptimizedSettings ApplyPerformanceProfile(DeezSpoTagSettings baseSettings, PerformanceProfile profile, PerformanceMetrics metrics)
    {
        var optimizedSettings = new DeezSpoTagSettings
        {
            // Copy all base settings
            DownloadLocation = baseSettings.DownloadLocation,
            TracknameTemplate = baseSettings.TracknameTemplate,
            AlbumTracknameTemplate = baseSettings.AlbumTracknameTemplate,
            PlaylistTracknameTemplate = baseSettings.PlaylistTracknameTemplate,
            CreatePlaylistFolder = baseSettings.CreatePlaylistFolder,
            PlaylistNameTemplate = baseSettings.PlaylistNameTemplate,
            CreateArtistFolder = baseSettings.CreateArtistFolder,
            ArtistNameTemplate = baseSettings.ArtistNameTemplate,
            CreateAlbumFolder = baseSettings.CreateAlbumFolder,
            AlbumNameTemplate = baseSettings.AlbumNameTemplate,
            CreateCDFolder = baseSettings.CreateCDFolder,
            CreateStructurePlaylist = baseSettings.CreateStructurePlaylist,
            CreateSingleFolder = baseSettings.CreateSingleFolder,
            PadTracks = baseSettings.PadTracks,
            PadSingleDigit = baseSettings.PadSingleDigit,
            PaddingSize = baseSettings.PaddingSize,
            IllegalCharacterReplacer = baseSettings.IllegalCharacterReplacer,
            MaxBitrate = baseSettings.MaxBitrate,
            FeelingLucky = baseSettings.FeelingLucky,
            FallbackBitrate = baseSettings.FallbackBitrate,
            FallbackSearch = baseSettings.FallbackSearch,
            FallbackISRC = baseSettings.FallbackISRC,
            LogErrors = baseSettings.LogErrors,
            LogSearched = baseSettings.LogSearched,
            OverwriteFile = baseSettings.OverwriteFile,
            CreateM3U8File = baseSettings.CreateM3U8File,
            PlaylistFilenameTemplate = baseSettings.PlaylistFilenameTemplate,
            SyncedLyrics = baseSettings.SyncedLyrics,
            EmbeddedArtworkSize = baseSettings.EmbeddedArtworkSize,
            LocalArtworkSize = baseSettings.LocalArtworkSize,
            AppleArtworkSize = baseSettings.AppleArtworkSize,
            AppleArtworkSizeText = baseSettings.AppleArtworkSizeText,
            LocalArtworkFormat = baseSettings.LocalArtworkFormat,
            SaveArtwork = baseSettings.SaveArtwork,
            CoverImageTemplate = baseSettings.CoverImageTemplate,
            SaveArtworkArtist = baseSettings.SaveArtworkArtist,
            ArtistImageTemplate = baseSettings.ArtistImageTemplate,
            JpegImageQuality = baseSettings.JpegImageQuality,
            DateFormat = baseSettings.DateFormat,
            AlbumVariousArtists = baseSettings.AlbumVariousArtists,
            RemoveAlbumVersion = baseSettings.RemoveAlbumVersion,
            RemoveDuplicateArtists = baseSettings.RemoveDuplicateArtists,
            FeaturedToTitle = baseSettings.FeaturedToTitle,
            TitleCasing = baseSettings.TitleCasing,
            ArtistCasing = baseSettings.ArtistCasing,
            ExecuteCommand = baseSettings.ExecuteCommand,
            Tags = baseSettings.Tags
        };

        string reason;

        // Apply profile-specific optimizations
        switch (profile)
        {
            case PerformanceProfile.Conservative:
                optimizedSettings.MaxConcurrentDownloads = Math.Max(1, baseSettings.MaxConcurrentDownloads / 2);
                optimizedSettings.FallbackBitrate = true;
                optimizedSettings.FallbackSearch = true;
                reason = $"Conservative profile applied due to high error rate ({metrics.ErrorRate:P1}) or slow response times";
                break;

            case PerformanceProfile.Aggressive:
                optimizedSettings.MaxConcurrentDownloads = Math.Min(20, baseSettings.MaxConcurrentDownloads * 2);
                optimizedSettings.FallbackBitrate = false;
                reason = $"Aggressive profile applied due to good performance (error rate: {metrics.ErrorRate:P1})";
                break;

            default:
                optimizedSettings.MaxConcurrentDownloads = baseSettings.MaxConcurrentDownloads;
                reason = "Balanced profile maintained";
                break;
        }

        return new OptimizedSettings
        {
            Settings = optimizedSettings,
            Profile = profile,
            Reason = reason,
            Metrics = metrics
        };
    }

    /// <summary>
    /// Analyze network performance
    /// </summary>
    private NetworkMetrics AnalyzeNetworkPerformance()
    {
        var recentData = GetRecentPerformanceData(TimeSpan.FromMinutes(5));

        if (recentData.Count == 0)
        {
            return new NetworkMetrics();
        }

        var averageSpeed = CalculateAverageSpeed(recentData.Where(d => d.Success));
        var hasSlowNetwork = averageSpeed < 100 * 1024; // Less than 100 KB/s

        return new NetworkMetrics
        {
            AverageSpeed = averageSpeed,
            HasSlowNetwork = hasSlowNetwork,
            SampleSize = recentData.Count
        };
    }

    /// <summary>
    /// Calculate average download speed
    /// </summary>
    private static double CalculateAverageSpeed(IEnumerable<DownloadPerformanceData> data)
    {
        var validData = data.Where(d => d.Duration.TotalSeconds > 0 && d.BytesTransferred > 0).ToList();

        if (validData.Count == 0)
        {
            return 0;
        }

        return validData.Average(d => d.BytesTransferred / d.Duration.TotalSeconds);
    }

    /// <summary>
    /// Calculate median duration
    /// </summary>
    private static TimeSpan CalculateMedian(IEnumerable<TimeSpan> durations)
    {
        var sorted = durations.OrderBy(d => d.TotalMilliseconds).ToList();
        var count = sorted.Count;

        if (count == 0) return TimeSpan.Zero;
        if (count == 1) return sorted[0];

        if (count % 2 == 0)
        {
            var mid1 = sorted[count / 2 - 1];
            var mid2 = sorted[count / 2];
            return TimeSpan.FromMilliseconds((mid1.TotalMilliseconds + mid2.TotalMilliseconds) / 2);
        }
        else
        {
            return sorted[count / 2];
        }
    }

    /// <summary>
    /// Clean up old metrics
    /// </summary>
    private void CleanupOldMetrics()
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var keysToRemove = _metrics.Where(kvp => kvp.Value.LastUpdated < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _metrics.TryRemove(key, out _);
        }

        if (keysToRemove.Count > 0 && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Cleaned up {Count} old performance metrics", keysToRemove.Count);
        }
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!disposing)
        {
            return;
        }

        _optimizationTimer.Dispose();
    }
}

/// <summary>
/// Download performance data
/// </summary>
public class DownloadPerformanceData
{
    public string DownloadId { get; set; } = "";
    public string DownloadType { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public long BytesTransferred { get; set; }
    public bool Success { get; set; }
    public string? ErrorType { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Performance metric
/// </summary>
public class PerformanceMetric
{
    public int TotalRequests { get; set; }
    public int SuccessfulRequests { get; set; }
    public TimeSpan TotalResponseTime { get; set; }
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Optimized settings result
/// </summary>
public class OptimizedSettings
{
    public DeezSpoTagSettings Settings { get; set; } = new();
    public PerformanceProfile Profile { get; set; }
    public string Reason { get; set; } = "";
    public PerformanceMetrics? Metrics { get; set; }
}

/// <summary>
/// Performance metrics
/// </summary>
public class PerformanceMetrics
{
    public double ErrorRate { get; set; }
    public TimeSpan AverageResponseTime { get; set; }
    public int TotalRequests { get; set; }
    public int FailedRequests { get; set; }
}

/// <summary>
/// Performance recommendation
/// </summary>
public class PerformanceRecommendation
{
    public RecommendationType Type { get; set; }
    public RecommendationPriority Priority { get; set; }
    public string Description { get; set; } = "";
    public string Impact { get; set; } = "";
}

/// <summary>
/// Performance statistics
/// </summary>
public class PerformanceStatistics
{
    public TimeSpan Period { get; set; }
    public int TotalDownloads { get; set; }
    public int SuccessfulDownloads { get; set; }
    public int FailedDownloads { get; set; }
    public double SuccessRate { get; set; }
    public TimeSpan AverageDownloadTime { get; set; }
    public TimeSpan MedianDownloadTime { get; set; }
    public TimeSpan FastestDownload { get; set; }
    public TimeSpan SlowestDownload { get; set; }
    public long TotalDataTransferred { get; set; }
    public double AverageSpeed { get; set; }
    public Dictionary<string, int> ErrorsByType { get; set; } = new();
}

/// <summary>
/// Network performance metrics
/// </summary>
public class NetworkMetrics
{
    public double AverageSpeed { get; set; }
    public bool HasSlowNetwork { get; set; }
    public int SampleSize { get; set; }
}

/// <summary>
/// Performance profile enumeration
/// </summary>
public enum PerformanceProfile
{
    Conservative,
    Balanced,
    Aggressive
}

/// <summary>
/// Recommendation type enumeration
/// </summary>
public enum RecommendationType
{
    ReduceConcurrency,
    IncreaseConcurrency,
    OptimizeNetwork,
    ChangeQuality,
    EnableFallback
}

/// <summary>
/// Recommendation priority enumeration
/// </summary>
public enum RecommendationPriority
{
    Low,
    Medium,
    High,
    Critical
}
