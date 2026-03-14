using DeezSpoTag.Services.Download;

namespace DeezSpoTag.Web.Services;

public sealed class ActivityLogWriter : IActivityLogWriter
{
    private readonly LibraryConfigStore _configStore;

    public ActivityLogWriter(LibraryConfigStore configStore)
    {
        _configStore = configStore;
    }

    public void Info(string message)
    {
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "info", message));
    }

    public void Warn(string message)
    {
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "warn", message));
    }

    public void Error(string message)
    {
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "error", message));
    }
}
