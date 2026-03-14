using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeezSpoTag.Services.Download.Shared.Performance;

/// <summary>
/// PHASE 4: Performance optimization service
/// Monitors and optimizes deezspotag download performance
/// </summary>
public class PerformanceOptimizationService : IDisposable
{
    private readonly ILogger<PerformanceOptimizationService> _logger;
    private readonly ConcurrentDictionary<string, PerformanceMetrics> _metrics = new();
    private readonly Timer _metricsTimer;
    private bool _disposed;

    public PerformanceOptimizationService(ILogger<PerformanceOptimizationService> logger)
    {
        _logger = logger;
        _metricsTimer = new Timer(LogMetrics, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// PHASE 4: Track operation performance
    /// </summary>
    public IDisposable TrackOperation(string operationName)
    {
        return new PerformanceTracker(operationName, this);
    }

    /// <summary>
    /// PHASE 4: Record operation completion
    /// </summary>
    internal void RecordOperation(string operationName, TimeSpan duration, bool success)
    {
        _metrics.AddOrUpdate(operationName, 
            new PerformanceMetrics { OperationName = operationName },
            (key, existing) => existing)
            .RecordOperation(duration, success);
    }

    /// <summary>
    /// PHASE 4: Get performance metrics
    /// </summary>
    public Dictionary<string, PerformanceMetrics> GetMetrics()
    {
        return new Dictionary<string, PerformanceMetrics>(_metrics);
    }

    /// <summary>
    /// PHASE 4: Optimize concurrency based on performance
    /// </summary>
    public static int OptimizeConcurrency(int currentConcurrency, double averageResponseTime, double errorRate)
    {
        // DeezSpoTag-style adaptive concurrency
        if (errorRate > 0.1) // More than 10% errors
        {
            return Math.Max(1, currentConcurrency - 1);
        }
        else if (averageResponseTime < 1000 && errorRate < 0.05) // Fast responses, low errors
        {
            return Math.Min(20, currentConcurrency + 1); // Max 20 concurrent like deezspotag
        }
        
        return currentConcurrency;
    }

    /// <summary>
    /// PHASE 4: Check if system is under stress
    /// </summary>
    public bool IsSystemUnderStress()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var memoryUsage = process.WorkingSet64;
            
            // Simple heuristics for system stress
            var memoryThreshold = 1024 * 1024 * 1024; // 1GB
            
            return memoryUsage > memoryThreshold;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to check system stress");
            return false;
        }
    }

    private void LogMetrics(object? state)
    {
        try
        {
            foreach (var metric in _metrics.Values.Where(static metric => metric.TotalOperations > 0))
            {
                _logger.LogInformation("Performance metrics for {Operation}: {TotalOps} ops, {AvgTime}ms avg, {SuccessRate}% success",
                    metric.OperationName,
                    metric.TotalOperations,
                    metric.AverageResponseTime.TotalMilliseconds,
                    metric.SuccessRate * 100);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to log performance metrics");
        }
    }

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

        _metricsTimer.Dispose();
    }
}

/// <summary>
/// PHASE 4: Performance tracker for individual operations
/// </summary>
internal class PerformanceTracker : IDisposable
{
    private readonly string _operationName;
    private readonly PerformanceOptimizationService _service;
    private readonly Stopwatch _stopwatch;
    private bool _disposed;

    public PerformanceTracker(string operationName, PerformanceOptimizationService service)
    {
        _operationName = operationName;
        _service = service;
        _stopwatch = Stopwatch.StartNew();
    }

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

        _stopwatch.Stop();
        _service.RecordOperation(_operationName, _stopwatch.Elapsed, true);
    }

    public void RecordFailure()
    {
        if (!_disposed)
        {
            _stopwatch.Stop();
            _service.RecordOperation(_operationName, _stopwatch.Elapsed, false);
            _disposed = true;
        }
    }
}

/// <summary>
/// PHASE 4: Performance metrics for operations
/// </summary>
public class PerformanceMetrics
{
    private readonly object _lock = new object();
    private long _totalOperations;
    private long _successfulOperations;
    private TimeSpan _totalTime;

    public string OperationName { get; set; } = "";
    public long TotalOperations => _totalOperations;
    public long SuccessfulOperations => _successfulOperations;
    public double SuccessRate => _totalOperations > 0 ? (double)_successfulOperations / _totalOperations : 0;
    public TimeSpan AverageResponseTime => _totalOperations > 0 ? TimeSpan.FromTicks(_totalTime.Ticks / _totalOperations) : TimeSpan.Zero;

    internal void RecordOperation(TimeSpan duration, bool success)
    {
        lock (_lock)
        {
            _totalOperations++;
            _totalTime = _totalTime.Add(duration);
            
            if (success)
            {
                _successfulOperations++;
            }
        }
    }
}
