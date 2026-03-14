using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DeezSpoTag.Services.Download.Shared;

/// <summary>
/// Exact port of Node.js async.queue functionality used in deezspotag downloader.ts
/// Ported from: async.queue from Node.js async library
/// </summary>
/// <typeparam name="T">Type of data to process</typeparam>
public class DeezSpoTagAsyncQueue<T> : IDisposable
{
    private readonly Func<T, Task> _worker;
    private readonly ILogger? _logger;
    private readonly ConcurrentQueue<QueueItem<T>> _queue = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private volatile int _running = 0;
    private volatile bool _isDisposed = false;
    private TaskCompletionSource? _drainCompletionSource;
    private readonly object _drainLock = new();

    public DeezSpoTagAsyncQueue(Func<T, Task> worker, int concurrency, ILogger? logger = null)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _logger = logger;
        var normalizedConcurrency = Math.Max(1, concurrency);
        _semaphore = new SemaphoreSlim(normalizedConcurrency, normalizedConcurrency);
    }

    /// <summary>
    /// Add an item to the queue for processing - exact port of async.queue.push
    /// </summary>
    public void Push(T data, Action? callback = null)
    {
        if (_isDisposed)
        {
            callback?.Invoke();
            return;
        }

        var item = new QueueItem<T>(data, callback);
        _queue.Enqueue(item);
        
        // Start processing if not already at capacity
        _ = Task.Run(ProcessQueueAsync);
    }

    /// <summary>
    /// Wait for all items in the queue to be processed - exact port of async.queue.drain
    /// </summary>
    public async Task DrainAsync()
    {
        // If queue is empty and no running tasks, return immediately
        if (_queue.IsEmpty && _running == 0)
        {
            return;
        }

        TaskCompletionSource drainTcs;
        lock (_drainLock)
        {
            if (_drainCompletionSource != null)
            {
                // Already draining, wait for existing drain
                drainTcs = _drainCompletionSource;
            }
            else
            {
                _drainCompletionSource = new TaskCompletionSource();
                drainTcs = _drainCompletionSource;
            }
        }

        await drainTcs.Task;
    }

    /// <summary>
    /// Get the current length of the queue
    /// </summary>
    public int Length => _queue.Count;

    /// <summary>
    /// Get the number of currently running tasks
    /// </summary>
    public int Running => _running;

    /// <summary>
    /// Check if the queue is idle (no items and no running tasks)
    /// </summary>
    public bool IsIdle => _queue.IsEmpty && _running == 0;

    /// <summary>
    /// Process items from the queue with concurrency control
    /// </summary>
    private async Task ProcessQueueAsync()
    {
        if (_isDisposed || _cancellationTokenSource.Token.IsCancellationRequested)
            return;

        // Try to acquire a semaphore slot
        if (!await _semaphore.WaitAsync(0, _cancellationTokenSource.Token))
        {
            // No available slots
            return;
        }

        try
        {
            // Try to get an item from the queue
            if (!_queue.TryDequeue(out var item))
            {
                // No items to process
                return;
            }

            // Increment running counter
            Interlocked.Increment(ref _running);

            try
            {
                // Process the item
                await ProcessItemAsync(item);
            }
            finally
            {
                // Decrement running counter
                var currentRunning = Interlocked.Decrement(ref _running);
                
                // Check if we should signal drain completion
                if (_queue.IsEmpty && currentRunning == 0)
                {
                    lock (_drainLock)
                    {
                        _drainCompletionSource?.SetResult();
                        _drainCompletionSource = null;
                    }
                }
            }

            // Continue processing if there are more items
            if (!_queue.IsEmpty)
            {
                _ = Task.Run(ProcessQueueAsync);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelling
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Error in queue processing");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Process a single item with error handling
    /// </summary>
    private async Task ProcessItemAsync(QueueItem<T> item)
    {
        await QueueItemExecutionHelper.ExecuteAsync(item, _worker, _logger);
    }

    /// <summary>
    /// Dispose the queue processor and cancel all pending operations
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (!disposing)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        lock (_drainLock)
        {
            _drainCompletionSource?.SetCanceled();
            _drainCompletionSource = null;
        }

        _semaphore.Dispose();
        _cancellationTokenSource.Dispose();
    }
}

/// <summary>
