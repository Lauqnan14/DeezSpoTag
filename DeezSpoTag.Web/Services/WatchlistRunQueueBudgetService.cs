namespace DeezSpoTag.Web.Services;

public enum WatchlistQueueBlockReason
{
    None,
    PreviousWatchlistRunActive
}

public sealed class WatchlistRunQueueBudgetService
{
    private readonly object _gate = new();
    private readonly AsyncLocal<long> _executionGeneration = new();
    private long _generation;
    private long _activeGeneration;
    private int _limit;
    private int _remaining;
    private WatchlistQueueBlockReason _blockReason;

    public long BeginRun(
        int queueBudget,
        WatchlistQueueBlockReason blockReason = WatchlistQueueBlockReason.None)
    {
        lock (_gate)
        {
            _generation++;
            _activeGeneration = _generation;
            _executionGeneration.Value = _activeGeneration;
            _limit = Math.Max(0, queueBudget);
            _remaining = _limit;
            _blockReason = blockReason;
            return _activeGeneration;
        }
    }

    public long BeginRunIfInactive(
        int queueBudget,
        WatchlistQueueBlockReason blockReason = WatchlistQueueBlockReason.None)
    {
        lock (_gate)
        {
            if (_activeGeneration != 0)
            {
                return 0;
            }

            _generation++;
            _activeGeneration = _generation;
            _executionGeneration.Value = _activeGeneration;
            _limit = Math.Max(0, queueBudget);
            _remaining = _limit;
            _blockReason = blockReason;
            return _activeGeneration;
        }
    }

    public void EndRun(long token)
    {
        lock (_gate)
        {
            if (_activeGeneration != token)
            {
                return;
            }

            _activeGeneration = 0;
            _executionGeneration.Value = 0;
            _limit = 0;
            _remaining = 0;
            _blockReason = WatchlistQueueBlockReason.None;
        }
    }

    public int GetRemaining()
    {
        lock (_gate)
        {
            return _activeGeneration == 0 || _executionGeneration.Value != _activeGeneration
                ? 0
                : _remaining;
        }
    }

    public WatchlistQueueBlockReason GetBlockReason()
    {
        lock (_gate)
        {
            return _activeGeneration == 0 || _executionGeneration.Value != _activeGeneration
                ? WatchlistQueueBlockReason.None
                : _blockReason;
        }
    }

    public bool TryReserve(int queueItemCount)
    {
        if (queueItemCount <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_activeGeneration == 0
                || _executionGeneration.Value != _activeGeneration
                || _remaining < queueItemCount)
            {
                return false;
            }

            _remaining -= queueItemCount;
            return true;
        }
    }

    public void Release(int queueItemCount)
    {
        if (queueItemCount <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_activeGeneration == 0 || _executionGeneration.Value != _activeGeneration)
            {
                return;
            }

            _remaining = Math.Min(_limit, _remaining + queueItemCount);
        }
    }
}
