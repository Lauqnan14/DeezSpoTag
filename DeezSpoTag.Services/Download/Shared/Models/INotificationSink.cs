namespace DeezSpoTag.Services.Download.Shared.Models;

public interface INotificationSink
{
    void Raise(
        string kind,
        string title,
        string body,
        string severity = "Info",
        string? dedupeKey = null,
        string? entityType = null,
        string? entityId = null,
        string? link = null);

    void Resolve(
        string dedupeKey,
        bool manuallyResolved,
        string? recoveryTitle = null,
        string? recoveryBody = null);
}

public sealed class NullNotificationSink : INotificationSink
{
    public static readonly NullNotificationSink Instance = new();

    public void Raise(
        string kind,
        string title,
        string body,
        string severity = "Info",
        string? dedupeKey = null,
        string? entityType = null,
        string? entityId = null,
        string? link = null)
    {
    }

    public void Resolve(
        string dedupeKey,
        bool manuallyResolved,
        string? recoveryTitle = null,
        string? recoveryBody = null)
    {
    }
}
