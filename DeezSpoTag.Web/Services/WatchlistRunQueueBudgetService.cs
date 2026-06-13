namespace DeezSpoTag.Web.Services;

public sealed class WatchlistRunQueueBudgetService
{
    private readonly object _gate = new();
    private readonly AsyncLocal<long> _executionGeneration = new();
    private long _generation;
    private long _activeGeneration;
    private int _remaining;

    public long BeginRun(int queueBudget)
    {
        lock (_gate)
        {
            _generation++;
            _activeGeneration = _generation;
            _executionGeneration.Value = _activeGeneration;
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
            _executionGeneration.Value = 0;
            _remaining = 0;
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

    public int Consume(int queuedCount)
    {
        if (queuedCount <= 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_activeGeneration == 0 || _executionGeneration.Value != _activeGeneration)
            {
                return 0;
            }

            var consumed = Math.Min(_remaining, queuedCount);
            _remaining -= consumed;
            return consumed;
        }
    }
}
