
using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Settings;

public interface ISettingsService
{
    DeezSpoTagSettings LoadSettings();
    void SaveSettings(DeezSpoTagSettings settings);
}
