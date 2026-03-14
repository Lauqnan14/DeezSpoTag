namespace DeezSpoTag.Services.Authentication;

internal static class DeezSpoTagConfigPathResolver
{
    private const string DeezSpoTagFolderName = "deezspotag";

    public static string GetConfigFolder()
    {
        var deezspotagDataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (!string.IsNullOrEmpty(deezspotagDataDir))
        {
            return deezspotagDataDir.TrimEnd('/') + "/";
        }

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdgConfigHome))
        {
            var configPath = Path.Join(xdgConfigHome, DeezSpoTagFolderName);
            if (Directory.Exists(Path.GetDirectoryName(configPath)) || CanCreateDirectory(configPath))
            {
                return configPath;
            }
        }

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
        {
            var configPath = Path.Join(appData, DeezSpoTagFolderName);
            if (Directory.Exists(Path.GetDirectoryName(configPath)) || CanCreateDirectory(configPath))
            {
                return configPath;
            }
        }

        if (Environment.OSVersion.Platform == PlatformID.Unix)
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var macOsPath = Path.Join(homeDir, "Library", "Application Support", DeezSpoTagFolderName);
            if (Directory.Exists(Path.Join(homeDir, "Library", "Application Support")) || CanCreateDirectory(macOsPath))
            {
                return macOsPath;
            }
        }

        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Join(homeDirectory, ".config", DeezSpoTagFolderName);
    }

    private static bool CanCreateDirectory(string path)
    {
        try
        {
            var parentDir = Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(parentDir)
                && (Directory.Exists(parentDir) || Directory.Exists(Path.GetDirectoryName(parentDir)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
