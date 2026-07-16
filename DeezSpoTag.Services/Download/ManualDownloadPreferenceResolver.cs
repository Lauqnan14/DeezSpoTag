using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Download;

public static class ManualDownloadPreferenceResolver
{
    public static string ResolvePreferredEngine(DeezSpoTagSettings settings)
    {
        var normalized = DownloadSourceCatalog.NormalizeSourcePolicy(settings.Service) ?? DownloadSourceCatalog.Auto;
        return normalized switch
        {
            "auto" or "custom" or "amazon" or "apple" or "deezer" or "qobuz" or "tidal" => normalized,
            _ => DownloadSourceCatalog.Auto
        };
    }
}
