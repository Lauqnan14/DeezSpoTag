using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DeezSpoTag.Services.Download.Shared.Advanced;

/// <summary>
/// PHASE 4: Advanced concurrency manager
/// Manages download concurrency with adaptive algorithms like deezspotag
/// </summary>
public class AdvancedConcurrencyManager : IDisposable
{
    private readonly ILogger<AdvancedConcurrencyManager> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<string, ConcurrencyMetrics> _metrics = new();
    private readonly Timer _adjustmentTimer;
    private bool _disposed;

    public AdvancedConcurrencyManager(ILogger<AdvancedConcurrencyManager> logger)
    {
        _logger = logger;
        _adjustmentTimer = new Timer(AdjustConcurrency, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// PHASE 4: Get or create semaphore for specific operation type
    /// </summary>
    public SemaphoreSlim GetSemaphore(string operationType, int initialConcurrency = 10)
    {
        return _semaphores.GetOrAdd(operationType, key =>
        {
            _metrics[key] = new ConcurrencyMetrics { OperationType = key, CurrentConcurrency = initialConcurrency };
            return new SemaphoreSlim(initialConcurrency, initialConcurrency);
        });
    }

    /// <summary>
    /// PHASE 4: Execute operation with concurrency control
    /// </summary>
    public async Task<T> ExecuteWithConcurrencyAsync<T>(
        string operationType,
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var semaphore = GetSemaphore(operationType);
        var metrics = _metrics.GetOrAdd(operationType, key => new ConcurrencyMetrics { OperationType = key });

        await semaphore.WaitAsync(cancellationToken);
        var startTime = DateTime.UtcNow;

        try
        {
            metrics.RecordStart();
            var result = await operation();
            metrics.RecordSuccess(DateTime.UtcNow - startTime);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metrics.RecordFailure(DateTime.UtcNow - startTime);
            throw new InvalidOperationException($"Operation {operationType} failed", ex);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// PHASE 4: Adjust concurrency based on performance metrics
    /// </summary>
    private void AdjustConcurrency(object? state)
    {
        try
        {
            foreach (var kvp in _metrics)
            {
                var operationType = kvp.Key;
                var metrics = kvp.Value;

                if (metrics.TotalOperations < 10) continue; // Need enough data

                var newConcurrency = CalculateOptimalConcurrency(metrics);
                if (newConcurrency != metrics.CurrentConcurrency)
                {
                    AdjustSemaphore(operationType, newConcurrency);
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Adjusted concurrency for {OperationType}: {OldConcurrency} -> {NewConcurrency} (Success rate: {SuccessRate}%, Avg time: {AvgTime}ms)",
                            operationType, metrics.CurrentConcurrency, newConcurrency,
                            metrics.SuccessRate * 100, metrics.AverageResponseTime.TotalMilliseconds);                    }

                    metrics.CurrentConcurrency = newConcurrency;
                }

                // Reset metrics for next period
                metrics.Reset();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to adjust concurrency");
        }
    }

    /// <summary>
    /// PHASE 4: Calculate optimal concurrency using deezspotag-style algorithm
    /// </summary>
    private static int CalculateOptimalConcurrency(ConcurrencyMetrics metrics)
    {
        var current = metrics.CurrentConcurrency;
        var successRate = metrics.SuccessRate;
        var avgResponseTime = metrics.AverageResponseTime.TotalMilliseconds;

        // DeezSpoTag-style adaptive algorithm
        if (successRate < 0.8) // Less than 80% success rate
        {
            return Math.Max(1, current - 2); // Reduce aggressively
        }
        else if (successRate < 0.9) // Less than 90% success rate
        {
            return Math.Max(1, current - 1); // Reduce moderately
        }
        else if (successRate > 0.95 && avgResponseTime < 2000) // High success, fast response
        {
            return Math.Min(20, current + 1); // Increase gradually, max 20 like deezspotag
        }
        else if (avgResponseTime > 5000) // Slow responses
        {
            return Math.Max(1, current - 1); // Reduce to improve response time
        }

        return current; // No change needed
    }

    /// <summary>
    /// PHASE 4: Adjust semaphore capacity
    /// </summary>
    private void AdjustSemaphore(string operationType, int newConcurrency)
    {
        if (_semaphores.TryGetValue(operationType, out var semaphore))
        {
            var difference = newConcurrency - _metrics[operationType].CurrentConcurrency;

            if (difference > 0)
            {
                // Increase capacity
                semaphore.Release(difference);
            }
            else if (difference < 0)
            {
                // Decrease capacity by waiting for slots
                for (int i = 0; i < Math.Abs(difference); i++)
                {
                    _ = Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        // Don't release - this effectively reduces capacity
                    });
                }
            }
        }
    }

    /// <summary>
    /// PHASE 4: Get current concurrency metrics
    /// </summary>
    public Dictionary<string, ConcurrencyMetrics> GetMetrics()
    {
        return new Dictionary<string, ConcurrencyMetrics>(_metrics);
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

        _adjustmentTimer.Dispose();
        foreach (var semaphore in _semaphores.Values)
        {
            semaphore.Dispose();
        }
    }
}

/// <summary>
/// PHASE 4: Concurrency metrics for adaptive management
/// </summary>
public class ConcurrencyMetrics
{
    private readonly object _lock = new object();
    private long _totalOperations;
    private long _successfulOperations;
    private long _activeOperations;
    private TimeSpan _totalResponseTime;

    public string OperationType { get; set; } = "";
    public int CurrentConcurrency { get; set; }
    public long TotalOperations => _totalOperations;
    public long SuccessfulOperations => _successfulOperations;
    public long ActiveOperations => _activeOperations;
    public double SuccessRate => _totalOperations > 0 ? (double)_successfulOperations / _totalOperations : 1.0;
    public TimeSpan AverageResponseTime => _totalOperations > 0 ? TimeSpan.FromTicks(_totalResponseTime.Ticks / _totalOperations) : TimeSpan.Zero;

    internal void RecordStart()
    {
        lock (_lock)
        {
            _activeOperations++;
        }
    }

    internal void RecordSuccess(TimeSpan responseTime)
    {
        lock (_lock)
        {
            _totalOperations++;
            _successfulOperations++;
            _activeOperations--;
            _totalResponseTime = _totalResponseTime.Add(responseTime);
        }
    }

    internal void RecordFailure(TimeSpan responseTime)
    {
        lock (_lock)
        {
            _totalOperations++;
            _activeOperations--;
            _totalResponseTime = _totalResponseTime.Add(responseTime);
        }
    }

    internal void Reset()
    {
        lock (_lock)
        {
            _totalOperations = 0;
            _successfulOperations = 0;
            _totalResponseTime = TimeSpan.Zero;
            // Keep _activeOperations and CurrentConcurrency
        }
    }
}
