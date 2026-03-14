using System.Collections.Concurrent;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class DownloadCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _userCanceled = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _userPaused = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string queueUuid, CancellationTokenSource cancellationTokenSource)
    {
        _active[queueUuid] = cancellationTokenSource;
    }

    public bool Cancel(string queueUuid)
    {
        if (_active.TryGetValue(queueUuid, out var cancellationTokenSource))
        {
            cancellationTokenSource.Cancel();
            return true;
        }

        return false;
    }

    public void MarkUserCanceled(string queueUuid)
    {
        if (!string.IsNullOrWhiteSpace(queueUuid))
        {
            _userCanceled[queueUuid] = true;
        }
    }

    public bool WasUserCanceled(string queueUuid)
    {
        return !string.IsNullOrWhiteSpace(queueUuid) && _userCanceled.ContainsKey(queueUuid);
    }

    public void ClearUserCanceled(string queueUuid)
    {
        if (!string.IsNullOrWhiteSpace(queueUuid))
        {
            _userCanceled.TryRemove(queueUuid, out _);
        }
    }

    public void MarkUserPaused(string queueUuid)
    {
        if (!string.IsNullOrWhiteSpace(queueUuid))
        {
            _userPaused[queueUuid] = true;
        }
    }

    public bool WasUserPaused(string queueUuid)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return false;
        }

        if (_userPaused.TryRemove(queueUuid, out _))
        {
            return true;
        }

        return false;
    }

    public void Remove(string queueUuid)
    {
        _active.TryRemove(queueUuid, out _);
        _userPaused.TryRemove(queueUuid, out _);
    }
}
