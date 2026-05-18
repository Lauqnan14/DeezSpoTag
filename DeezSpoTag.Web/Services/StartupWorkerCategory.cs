namespace DeezSpoTag.Web.Services;

public enum StartupWorkerCategory
{
    Critical,
    Deferred,
    Manual,
    DisabledOnError
}

public sealed class StartupWorkerRegistry
{
    private readonly IReadOnlyList<StartupWorkerDescriptor> _workers;

    public StartupWorkerRegistry(IEnumerable<StartupWorkerDescriptor> workers)
    {
        _workers = workers
            .GroupBy(worker => worker.ServiceType, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(worker => worker.Category, StringComparer.Ordinal)
            .ThenBy(worker => worker.ServiceName, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<StartupWorkerDescriptor> GetWorkers() => _workers;
}

public sealed record StartupWorkerDescriptor(
    string ServiceType,
    string ServiceName,
    string Category,
    string Description);
