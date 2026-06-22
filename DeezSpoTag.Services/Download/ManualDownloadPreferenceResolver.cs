using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Download;

public static class ManualDownloadPreferenceResolver
{
    public static string ResolvePreferredEngine(DeezSpoTagSettings settings)
    {
        if (settings.DownloadEngineOrder?.Enabled == true)
        {
            return "auto";
        }

        var normalized = DownloadSourceCatalog.NormalizeEngineOrAuto(settings.Service) ?? DownloadSourceCatalog.Auto;
        return normalized switch
        {
            "auto" or "amazon" or "apple" or "deezer" or "qobuz" or "tidal" => normalized,
            _ => DownloadSourceCatalog.Auto
        };
    }
}
