using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

internal static class ConfiguredDownloadRootResolver
{
    public static bool TryResolve(
        DeezSpoTagSettingsService settingsService,
        string locationLabel,
        string missingConfigurationMessage,
        out string downloadRootPath,
        out string error)
    {
        downloadRootPath = string.Empty;
        error = string.Empty;

        string configuredPath;
        try
        {
            configuredPath = settingsService.LoadSettings().DownloadLocation?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = $"{locationLabel} could not be loaded ({ex.Message}).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            error = missingConfigurationMessage;
            return false;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(configuredPath);
        if (string.IsNullOrWhiteSpace(ioPath))
        {
            error = $"{locationLabel} '{configuredPath}' resolved to an empty path.";
            return false;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(ioPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = $"{locationLabel} '{configuredPath}' is invalid ({ex.Message}).";
            return false;
        }

        if (!Directory.Exists(normalizedPath))
        {
            error = $"{locationLabel} '{normalizedPath}' is not accessible.";
            return false;
        }

        downloadRootPath = normalizedPath;
        return true;
    }
}
