using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

public static class DeezerAccountCapabilityService
{
    public static void UpdateMaxBitrateForUser(
        DeezSpoTag.Core.Models.Deezer.DeezerUser currentUser,
        DeezSpoTagSettingsService settingsService,
        ILogger logger)
    {
        var bestBitrate = ResolveBestBitrate(currentUser);
        var settings = settingsService.LoadSettings();
        if (!settings.AutoMaxBitrate)
        {
            logger.LogInformation("Auto max bitrate disabled; keeping user-selected setting");
            return;
        }

        if (settings.MaxBitrate == bestBitrate)
        {
            return;
        }

        settings.MaxBitrate = bestBitrate;
        settingsService.SaveSettings(settings);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Updated max bitrate to {Bitrate} based on account capabilities", bestBitrate);
        }
    }

    private static int ResolveBestBitrate(DeezSpoTag.Core.Models.Deezer.DeezerUser currentUser)
    {
        if (currentUser.CanStreamLossless == true)
        {
            return 9;
        }

        if (currentUser.CanStreamHq == true)
        {
            return 3;
        }

        return 1;
    }
}
