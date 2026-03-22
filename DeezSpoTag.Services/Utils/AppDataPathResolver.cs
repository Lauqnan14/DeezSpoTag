namespace DeezSpoTag.Services.Utils;

public static class AppDataPathResolver
{
    public const string ConfigDirEnvVar = "DEEZSPOTAG_CONFIG_DIR";
    public const string DataDirEnvVar = "DEEZSPOTAG_DATA_DIR";
    private const string WorkersProjectDirectoryName = "DeezSpoTag.Workers";
    private static readonly string[] CanonicalWorkersDataCandidates =
    [
        Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), WorkersProjectDirectoryName, "bin", "Debug", "net8.0", "Data")),
        Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", WorkersProjectDirectoryName, "bin", "Debug", "net8.0", "Data"))
    ];
    private static readonly string[] LegacyWorkersDataCandidates =
    [
        Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), WorkersProjectDirectoryName, "Data")),
        Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", WorkersProjectDirectoryName, "Data"))
    ];

    public static string GetDefaultWorkersDataDir()
    {
        var existingCanonicalCandidate = Array.Find(CanonicalWorkersDataCandidates, Directory.Exists);
        if (!string.IsNullOrWhiteSpace(existingCanonicalCandidate))
        {
            return existingCanonicalCandidate;
        }

        foreach (var legacyCandidate in LegacyWorkersDataCandidates.Where(Directory.Exists))
        {
            var migrateTarget = CanonicalWorkersDataCandidates[0];
            TryMigrateLegacyWorkersData(legacyCandidate, migrateTarget);
            if (Directory.Exists(migrateTarget))
            {
                return migrateTarget;
            }
        }

        var existingLegacyCandidate = Array.Find(LegacyWorkersDataCandidates, Directory.Exists);
        if (!string.IsNullOrWhiteSpace(existingLegacyCandidate))
        {
            return existingLegacyCandidate;
        }

        return CanonicalWorkersDataCandidates[0];
    }

    public static bool IsLegacyWorkersDataDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = NormalizeConfiguredDataRoot(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return LegacyWorkersDataCandidates.Any(candidate =>
            string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
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
