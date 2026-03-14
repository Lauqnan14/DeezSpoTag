namespace DeezSpoTag.Services.Utils;

public static class AppDataPathResolver
{
    public const string ConfigDirEnvVar = "DEEZSPOTAG_CONFIG_DIR";
    public const string DataDirEnvVar = "DEEZSPOTAG_DATA_DIR";

    public static string GetDefaultWorkersDataDir()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), "DeezSpoTag.Workers", "bin", "Debug", "net8.0", "Data")),
            Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", "DeezSpoTag.Workers", "bin", "Debug", "net8.0", "Data")),
            Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "Data"))
        };

        var existingCandidate = Array.Find(candidates, Directory.Exists);
        if (!string.IsNullOrWhiteSpace(existingCandidate))
        {
            return existingCandidate;
        }

        return candidates[^1];
    }

    public static string ResolveDataRootOrDefault(string defaultDataRoot)
    {
        var configuredConfigDir = NormalizeConfiguredDataRoot(Environment.GetEnvironmentVariable(ConfigDirEnvVar));
        if (!string.IsNullOrWhiteSpace(configuredConfigDir))
        {
            return configuredConfigDir;
        }

        var configuredDataDir = NormalizeConfiguredDataRoot(Environment.GetEnvironmentVariable(DataDirEnvVar));
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return configuredDataDir;
        }

        return Path.GetFullPath(defaultDataRoot);
    }

    public static string EnsureConfiguredDataAndConfigRoots(string defaultDataRoot)
    {
        var resolvedConfigRoot = NormalizeConfiguredDataRoot(Environment.GetEnvironmentVariable(ConfigDirEnvVar));
        var resolvedDataRoot = NormalizeConfiguredDataRoot(Environment.GetEnvironmentVariable(DataDirEnvVar));
        var fallbackRoot = Path.GetFullPath(defaultDataRoot);

        var effectiveConfigRoot = string.IsNullOrWhiteSpace(resolvedConfigRoot) ? fallbackRoot : resolvedConfigRoot;
        var effectiveDataRoot = string.IsNullOrWhiteSpace(resolvedDataRoot) ? fallbackRoot : resolvedDataRoot;

        Environment.SetEnvironmentVariable(ConfigDirEnvVar, effectiveConfigRoot);
        Environment.SetEnvironmentVariable(DataDirEnvVar, effectiveDataRoot);
        return effectiveDataRoot;
    }

    public static string ResolveDbPathStrict(string dataRoot, string scope, string fileName)
    {
        var normalizedRoot = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(normalizedRoot);

        var scopedDirectory = Path.GetFullPath(Path.Join(normalizedRoot, "db", scope));
        Directory.CreateDirectory(scopedDirectory);

        var scopedPath = Path.GetFullPath(Path.Join(scopedDirectory, fileName));
        var legacyPath = Path.GetFullPath(Path.Join(normalizedRoot, fileName));

        if (File.Exists(legacyPath) && File.Exists(scopedPath))
        {
            var legacyInfo = new FileInfo(legacyPath);
            var scopedInfo = new FileInfo(scopedPath);
            if (legacyInfo.Length == 0 && scopedInfo.Length > 0)
            {
                File.Delete(legacyPath);
                return scopedPath;
            }

            throw new InvalidOperationException(
                $"Database layout conflict for '{fileName}': both '{legacyPath}' and '{scopedPath}' exist. Remove the legacy file.");
        }

        if (File.Exists(legacyPath))
        {
            File.Move(legacyPath, scopedPath);
        }

        return scopedPath;
    }

    public static string? NormalizeConfiguredDataRoot(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var normalized = Path.GetFullPath(configuredPath.Trim());
        while (string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized)),
            "deezspotag",
            StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(normalized))?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            normalized = parent;
        }

        return normalized;
    }
}
