using DeezSpoTag.Services.Runtime;

namespace DeezSpoTag.Web.Services;

public sealed class StartupStateService
{
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly object _stateLock = new();
    private readonly List<StartupCheckpoint> _checkpoints = [];
    private string _phase = "starting";

    public StartupStateService()
        : this(TimeProvider.System)
    {
    }

    public StartupStateService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _startedAtUtc = _timeProvider.GetUtcNow();
    }

    public void Checkpoint(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_stateLock)
        {
            _phase = name;
            _checkpoints.Add(new StartupCheckpoint(
                name,
                _timeProvider.GetUtcNow(),
                _timeProvider.GetUtcNow() - _startedAtUtc));
        }
    }

    public StartupRuntimeSnapshot GetSnapshot(BackgroundWorkCoordinator coordinator)
    {
        lock (_stateLock)
        {
            return new StartupRuntimeSnapshot(
                _phase,
                _timeProvider.GetUtcNow() - _startedAtUtc,
                coordinator.GetSnapshot(),
                _checkpoints.ToArray());
        }
    }
}

public sealed record StartupCheckpoint(
    string Name,
    DateTimeOffset TimestampUtc,
    TimeSpan Elapsed);

public sealed record StartupRuntimeSnapshot(
    string Phase,
    TimeSpan Elapsed,
    BackgroundWorkSnapshot BackgroundWork,
    IReadOnlyList<StartupCheckpoint> Checkpoints);
