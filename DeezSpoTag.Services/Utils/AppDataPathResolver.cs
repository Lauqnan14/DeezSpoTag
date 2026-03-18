namespace DeezSpoTag.Services.Utils;

public static class AppDataPathResolver
{
    public const string ConfigDirEnvVar = "DEEZSPOTAG_CONFIG_DIR";
    public const string DataDirEnvVar = "DEEZSPOTAG_DATA_DIR";
    private const string WorkersProjectDirectoryName = "DeezSpoTag.Workers";

    public static string GetDefaultWorkersDataDir()
    {
        var stableCandidates = new[]
        {
            Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), WorkersProjectDirectoryName, "Data")),
            Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", WorkersProjectDirectoryName, "Data"))
        };
        var legacyCandidates = new[]
        {
            Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), WorkersProjectDirectoryName, "bin", "Debug", "net8.0", "Data")),
            Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", WorkersProjectDirectoryName, "bin", "Debug", "net8.0", "Data"))
        };

        var existingStableCandidate = Array.Find(stableCandidates, Directory.Exists);
        var existingLegacyCandidate = Array.Find(legacyCandidates, Directory.Exists);

        if (!string.IsNullOrWhiteSpace(existingStableCandidate)
            && !HasSpotifyAuthState(existingStableCandidate)
            && !string.IsNullOrWhiteSpace(existingLegacyCandidate)
            && HasSpotifyAuthState(existingLegacyCandidate))
        {
            return existingLegacyCandidate;
        }

        if (!string.IsNullOrWhiteSpace(existingStableCandidate))
        {
            return existingStableCandidate;
        }

        foreach (var legacyCandidate in legacyCandidates.Where(Directory.Exists))
        {
            var migrateTarget = stableCandidates[0];
            TryMigrateLegacyWorkersData(legacyCandidate, migrateTarget);
            if (Directory.Exists(migrateTarget))
            {
                return migrateTarget;
            }
        }

        if (!string.IsNullOrWhiteSpace(existingLegacyCandidate))
        {
            return existingLegacyCandidate;
        }

        return stableCandidates[0];
    }

    private static bool HasSpotifyAuthState(string dataRoot)
    {
        var platformSpotifyPath = Path.Join(dataRoot, "autotag", "spotify.json");
        if (File.Exists(platformSpotifyPath))
        {
            return true;
        }

        var usersRoot = Path.Join(dataRoot, "spotify", "users");
        if (!Directory.Exists(usersRoot))
        {
            return false;
        }

        return Directory.EnumerateFiles(usersRoot, "spotify-auth.json", SearchOption.AllDirectories).Any();
    }

    private static void TryMigrateLegacyWorkersData(string sourcePath, string targetPath)
    {
        try
        {
            if (Directory.Exists(targetPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? targetPath);
            CopyDirectoryRecursive(sourcePath, targetPath);
        }
        catch
        {
            // Best effort migration; fallback selection continues if migration fails.
        }
    }

    private static void CopyDirectoryRecursive(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(targetPath);

        foreach (var filePath in Directory.GetFiles(sourcePath))
        {
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var destinationPath = Path.Combine(targetPath, fileName);
            File.Copy(filePath, destinationPath, overwrite: true);
        }

        foreach (var directoryPath in Directory.GetDirectories(sourcePath))
        {
            var directoryName = Path.GetFileName(directoryPath);
            if (string.IsNullOrWhiteSpace(directoryName))
            {
                continue;
            }

            CopyDirectoryRecursive(directoryPath, Path.Combine(targetPath, directoryName));
        }
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
