using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DeezSpoTag.Services.Download.Shared;

/// <summary>
/// Async queue processor that mimics the behavior of async.queue from Node.js
/// Ported from: async.queue functionality used in deezspotag downloader.ts
/// </summary>
/// <typeparam name="T">Type of data to process</typeparam>
public class AsyncQueueProcessor<T> : IDisposable
{
    private readonly Func<T, Task> _worker;
    private readonly int _concurrency;
    private readonly ILogger? _logger;
    private readonly ConcurrentQueue<QueueItem<T>> _queue = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly List<Task> _activeTasks = new();
    private readonly object _lock = new();
    private volatile bool _isDisposed = false;
    private volatile bool _isDraining = false;
    private TaskCompletionSource? _drainCompletionSource;

    public AsyncQueueProcessor(Func<T, Task> worker, int concurrency, ILogger? logger = null)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _concurrency = Math.Max(1, concurrency);
        _logger = logger;
        _semaphore = new SemaphoreSlim(_concurrency, _concurrency);

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger?.LogDebug("Created AsyncQueueProcessor with concurrency: {Concurrency}", _concurrency);        }
    }

    /// <summary>
    /// Add an item to the queue for processing
    /// </summary>
    public void Push(T data, Action? callback = null)
    {
        if (_isDisposed)
        {
            _logger?.LogWarning("Attempted to push to disposed queue");
            callback?.Invoke();
            return;
        }

        var item = new QueueItem<T>(data, callback);
        _queue.Enqueue(item);

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger?.LogDebug("Pushed item to queue, current length: {QueueLength}", _queue.Count);        }

        // Start processing if not already at capacity
        _ = Task.Run(ProcessQueueAsync);
    }

    /// <summary>
    /// Wait for all items in the queue to be processed
    /// Mimics async.queue drain functionality
    /// </summary>
    public async Task DrainAsync()
    {
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger?.LogDebug("Starting queue drain, current queue length: {QueueLength}", _queue.Count);        }

        _isDraining = true;

        // If queue is empty and no active tasks, return immediately
        if (_queue.IsEmpty && _activeTasks.Count == 0)
        {
            _logger?.LogDebug("Queue already empty, drain complete");
            return;
        }

        // Create completion source for drain
        lock (_lock)
        {
            _drainCompletionSource = new TaskCompletionSource();
        }

        // Wait for drain to complete
        await _drainCompletionSource.Task;

        _logger?.LogDebug("Queue drain completed");
    }

    /// <summary>
    /// Get the current length of the queue
    /// </summary>
    public int Length => _queue.Count;

    /// <summary>
    /// Get the number of currently running tasks
    /// </summary>
    public int Running => _activeTasks.Count;

    /// <summary>
    /// Check if the queue is idle (no items and no running tasks)
    /// </summary>
    public bool IsIdle => _queue.IsEmpty && _activeTasks.Count == 0;

    /// <summary>
    /// Process items from the queue with concurrency control
    /// </summary>
    private async Task ProcessQueueAsync()
    {
        if (ShouldSkipProcessing())
            return;

        if (!await TryAcquireSlotAsync())
        {
            return;
        }

        try
        {
            await ScheduleQueuedItemsAsync();
        }
        catch (OperationCanceledException ex)
        {
            _logger?.LogDebug(ex, "Queue processing cancelled");
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

    private bool ShouldSkipProcessing()
    {
        return _isDisposed || _cancellationTokenSource.Token.IsCancellationRequested;
    }

    private async Task<bool> TryAcquireSlotAsync()
    {
        return await _semaphore.WaitAsync(0, _cancellationTokenSource.Token);
    }

    private Task ScheduleQueuedItemsAsync()
    {
        while (!ShouldSkipProcessing())
        {
            if (!_queue.TryDequeue(out var item))
            {
                break;
            }

            var processingTask = ProcessItemAsync(item);
            TrackActiveTask(processingTask);
            if (HasReachedConcurrencyLimit())
            {
                break;
            }
        }

        return Task.CompletedTask;
    }

    private void TrackActiveTask(Task processingTask)
    {
        lock (_lock)
        {
            _activeTasks.Add(processingTask);
        }

        _ = processingTask.ContinueWith(HandleProcessingTaskCompletion, TaskScheduler.Default);
    }

    private void HandleProcessingTaskCompletion(Task completedTask)
    {
        lock (_lock)
        {
            _activeTasks.Remove(completedTask);
        }

        SignalDrainCompletionIfNeeded();
        ScheduleAdditionalProcessingIfNeeded();
    }

    private void SignalDrainCompletionIfNeeded()
    {
        if (!_isDraining || !_queue.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            if (!_isDraining || !_queue.IsEmpty || _activeTasks.Count != 0)
            {
                return;
            }

            _drainCompletionSource?.SetResult();
            _drainCompletionSource = null;
            _isDraining = false;
        }
    }

    private void ScheduleAdditionalProcessingIfNeeded()
    {
        if (!_queue.IsEmpty)
        {
            _ = Task.Run(ProcessQueueAsync);
        }
    }

    private bool HasReachedConcurrencyLimit()
    {
        lock (_lock)
        {
            return _activeTasks.Count >= _concurrency;
        }
    }

    /// <summary>
    /// Process a single item with error handling
    /// </summary>
    private async Task ProcessItemAsync(QueueItem<T> item)
    {
        _logger?.LogDebug("Processing queue item");
        var succeeded = await QueueItemExecutionHelper.ExecuteAsync(item, _worker, _logger);
        if (succeeded)
        {
            _logger?.LogDebug("Successfully processed queue item");
        }
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

        _logger?.LogDebug("Disposing AsyncQueueProcessor");
        _isDisposed = true;
        if (!disposing)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        lock (_lock)
        {
            _drainCompletionSource?.SetCanceled();
            _drainCompletionSource = null;
        }

        _semaphore.Dispose();
        _cancellationTokenSource.Dispose();
        _logger?.LogDebug("AsyncQueueProcessor disposed");
    }
}

/// <summary>
/// Internal queue item wrapper
/// </summary>
/// <typeparam name="T">Type of data</typeparam>
internal class QueueItem<T>
{
    public T Data { get; }
    public Action? Callback { get; }

    public QueueItem(T data, Action? callback = null)
    {
        Data = data;
        Callback = callback;
    }
}
