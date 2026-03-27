namespace DeezSpoTag.Services.Utils;

public static class AppDataPathResolver
{
    public const string ConfigDirEnvVar = "DEEZSPOTAG_CONFIG_DIR";
    public const string DataDirEnvVar = "DEEZSPOTAG_DATA_DIR";
    private const string WorkersProjectDirectoryName = "DeezSpoTag.Workers";
    private const string WebProjectDirectoryName = "DeezSpoTag.Web";
    private const string StableWorkersDataSuffix = "Data";
    private const string DebugWorkersDataSuffix = "bin/Debug/net8.0/Data";
    private static readonly string WorkspaceRoot = ResolveWorkspaceRoot();
    private static readonly string[] CanonicalWorkersDataCandidates =
    [
        Path.GetFullPath(Path.Join(WorkspaceRoot, WorkersProjectDirectoryName, StableWorkersDataSuffix))
    ];
    private static readonly string[] LegacyWorkersDataCandidates =
    [
        Path.GetFullPath(Path.Join(WorkspaceRoot, WorkersProjectDirectoryName, DebugWorkersDataSuffix))
    ];
    private static readonly string[] MisplacedWorkersDataCandidates =
    [
        Path.GetFullPath(Path.Join(WorkspaceRoot, WebProjectDirectoryName, WorkersProjectDirectoryName, StableWorkersDataSuffix)),
        Path.GetFullPath(Path.Join(WorkspaceRoot, WebProjectDirectoryName, WorkersProjectDirectoryName, DebugWorkersDataSuffix)),
        Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), WorkersProjectDirectoryName, StableWorkersDataSuffix)),
        Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), WorkersProjectDirectoryName, DebugWorkersDataSuffix))
    ];

    public static string GetDefaultWorkersDataDir()
    {
        var configuredConfigDir = NormalizeConfiguredDataRoot(Environment.GetEnvironmentVariable(ConfigDirEnvVar));
        if (!string.IsNullOrWhiteSpace(configuredConfigDir))
        {
            EnsureWritableDirectoryOrThrow(configuredConfigDir, ConfigDirEnvVar);
            return configuredConfigDir;
        }

        var configuredDataDir = NormalizeConfiguredDataRoot(Environment.GetEnvironmentVariable(DataDirEnvVar));
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            EnsureWritableDirectoryOrThrow(configuredDataDir, DataDirEnvVar);
            return configuredDataDir;
        }

        var canonicalPrimary = CanonicalWorkersDataCandidates[0];
        EnsureWritableDirectoryOrThrow(canonicalPrimary, "default workers data root");

        foreach (var misplacedCandidate in MisplacedWorkersDataCandidates.Where(Directory.Exists))
        {
            if (string.Equals(misplacedCandidate, canonicalPrimary, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryMigrateLegacyWorkersData(misplacedCandidate, canonicalPrimary);
        }

        var existingCanonicalCandidate = Array.Find(
            CanonicalWorkersDataCandidates,
            candidate => Directory.Exists(candidate) && !string.Equals(candidate, canonicalPrimary, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(existingCanonicalCandidate))
        {
            TryMigrateLegacyWorkersData(existingCanonicalCandidate, canonicalPrimary);
            return canonicalPrimary;
        }

        foreach (var legacyCandidate in LegacyWorkersDataCandidates.Where(Directory.Exists))
        {
            var migrateTarget = canonicalPrimary;
            TryMigrateLegacyWorkersData(legacyCandidate, migrateTarget);
            if (Directory.Exists(migrateTarget))
            {
                return migrateTarget;
            }
        }

        var existingLegacyCandidate = Array.Find(LegacyWorkersDataCandidates, Directory.Exists);
        if (!string.IsNullOrWhiteSpace(existingLegacyCandidate))
        {
            var migrateTarget = canonicalPrimary;
            TryMigrateLegacyWorkersData(existingLegacyCandidate, migrateTarget);
            return migrateTarget;
        }

        return canonicalPrimary;
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
                   string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
               || MisplacedWorkersDataCandidates.Any(candidate =>
            string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryMigrateLegacyWorkersData(string sourcePath, string targetPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? targetPath);
            CopyDirectoryRecursive(sourcePath, targetPath);
        }
        catch
        {
            // Best effort migration; fallback selection continues if migration fails.
        }
    }

    private static void EnsureWritableDirectoryOrThrow(string path, string source)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(
                $"Data root '{path}' from '{source}' is not writable. Set DEEZSPOTAG_DATA_DIR/DEEZSPOTAG_CONFIG_DIR to a writable path.");
        }
        catch (IOException)
        {
            throw new IOException(
                $"Data root '{path}' from '{source}' is not writable or cannot be created. Set DEEZSPOTAG_DATA_DIR/DEEZSPOTAG_CONFIG_DIR to a writable path.");
        }
    }

    private static string ResolveWorkspaceRoot()
    {
        var fromCwd = TryResolveWorkspaceRootFrom(Directory.GetCurrentDirectory());
        if (!string.IsNullOrWhiteSpace(fromCwd))
        {
            return fromCwd;
        }

        var fromAppBase = TryResolveWorkspaceRootFrom(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(fromAppBase))
        {
            return fromAppBase;
        }

        return Path.GetFullPath(Directory.GetCurrentDirectory());
    }

    private static string? TryResolveWorkspaceRootFrom(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        var current = new DirectoryInfo(Path.GetFullPath(startPath));
        for (var depth = 0; depth < 12 && current != null; depth++)
        {
            if (Directory.Exists(Path.Join(current.FullName, WorkersProjectDirectoryName))
                && Directory.Exists(Path.Join(current.FullName, WebProjectDirectoryName)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
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
            if (!File.Exists(destinationPath))
            {
                File.Copy(filePath, destinationPath);
            }
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
