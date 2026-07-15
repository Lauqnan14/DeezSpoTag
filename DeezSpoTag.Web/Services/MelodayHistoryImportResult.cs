namespace DeezSpoTag.Web.Services;

public sealed record MelodayHistoryImportResult(
    string Service,
    bool Configured,
    bool Available,
    int RemoteLibraries,
    int Fetched,
    int Imported,
    int Resolved,
    int Ambiguous,
    int Unresolved,
    string? Error)
{
    public string Status => !Configured
        ? "not-configured"
        : !Available
            ? "unavailable"
            : !string.IsNullOrWhiteSpace(Error) || Ambiguous > 0 || Unresolved > 0
                ? "degraded"
                : "complete";

    public static MelodayHistoryImportResult NotConfigured(string service)
        => new(service, false, false, 0, 0, 0, 0, 0, 0, null);

    public static MelodayHistoryImportResult Unavailable(string service, string error)
        => new(service, true, false, 0, 0, 0, 0, 0, 0, error);
}
