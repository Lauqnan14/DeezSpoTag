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

        var service = (settings.Service ?? string.Empty).Trim().ToLowerInvariant();
        return service is "auto" or "amazon" or "apple" or "deezer" or "qobuz" or "tidal"
            ? service
            : "auto";
    }

    public static string ResolvePreferredQuality(
        DeezSpoTagSettings settings,
        string preferredEngine,
        int requestedBitrate)
    {
        return preferredEngine switch
        {
            "deezer" => DownloadSourceOrder.ResolveDeezerBitrate(settings, requestedBitrate).ToString(),
            "qobuz" => string.IsNullOrWhiteSpace(settings.QobuzQuality) ? string.Empty : settings.QobuzQuality,
            "tidal" => string.IsNullOrWhiteSpace(settings.TidalQuality) ? string.Empty : settings.TidalQuality,
            "apple" => settings.AppleMusic?.PreferredAudioProfile ?? string.Empty,
            _ => string.Empty
        };
    }
}
