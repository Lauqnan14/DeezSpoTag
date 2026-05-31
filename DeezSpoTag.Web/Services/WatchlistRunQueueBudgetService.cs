namespace DeezSpoTag.Web.Services;

public sealed class WatchlistRunQueueBudgetService
{
    private readonly object _gate = new();
    private long _generation;
    private long _activeGeneration;
    private int _remaining = int.MaxValue;

    public long BeginRun(int queueBudget)
    {
        lock (_gate)
        {
            _generation++;
            _activeGeneration = _generation;
            _remaining = Math.Max(0, queueBudget);
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
            _remaining = int.MaxValue;
        }
    }

    public int GetRemaining()
    {
        lock (_gate)
        {
            return _activeGeneration == 0 ? int.MaxValue : _remaining;
        }
    }

    public int Consume(int queuedCount)
    {
        if (queuedCount <= 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_activeGeneration == 0)
            {
                return queuedCount;
            }

            var consumed = Math.Min(_remaining, queuedCount);
            _remaining -= consumed;
            return consumed;
        }
    }
}
