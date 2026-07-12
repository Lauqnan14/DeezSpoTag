using Microsoft.Data.Sqlite;

namespace DeezSpoTag.Services.Utils;

public static class AppDataPathResolver
{
    private sealed record DatabaseEvidence(
        bool IsValid,
        HashSet<string> Tables,
        long RowCount,
        long FileLength);

    public const string ConfigDirEnvVar = "DEEZSPOTAG_CONFIG_DIR";
    public const string DataDirEnvVar = "DEEZSPOTAG_DATA_DIR";
    private const string WorkersProjectDirectoryName = "DeezSpoTag.Workers";
    private const string WebProjectDirectoryName = "DeezSpoTag.Web";
    private const string StableWorkersDataSuffix = "Data";
    private static readonly string[] DebugWorkersDataSuffixes =
    [
        "bin/Debug/net10.0/Data",
        "bin/Debug/net8.0/Data"
    ];
    private static readonly string WorkspaceRoot = ResolveWorkspaceRoot();
    private static readonly string[] CanonicalWorkersDataCandidates =
    [
        Path.GetFullPath(Path.Join(WorkspaceRoot, WorkersProjectDirectoryName, StableWorkersDataSuffix))
    ];
    private static readonly string[] LegacyWorkersDataCandidates =
    [
        .. DebugWorkersDataSuffixes.Select(suffix =>
            Path.GetFullPath(Path.Join(WorkspaceRoot, WorkersProjectDirectoryName, suffix)))
    ];
    private static readonly string[] MisplacedWorkersDataCandidates =
    [
        Path.GetFullPath(Path.Join(WorkspaceRoot, WebProjectDirectoryName, WorkersProjectDirectoryName, StableWorkersDataSuffix)),
        .. DebugWorkersDataSuffixes.Select(suffix =>
            Path.GetFullPath(Path.Join(WorkspaceRoot, WebProjectDirectoryName, WorkersProjectDirectoryName, suffix))),
        Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), WorkersProjectDirectoryName, StableWorkersDataSuffix)),
        .. DebugWorkersDataSuffixes.Select(suffix =>
            Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), WorkersProjectDirectoryName, suffix)))
    ];

    public static string GetDefaultWorkersDataDir()
    {
        var configuredUnifiedRoot = ResolveConfiguredUnifiedDataRootOrThrow();
        if (!string.IsNullOrWhiteSpace(configuredUnifiedRoot))
        {
            EnsureWritableDirectoryOrThrow(configuredUnifiedRoot, $"{DataDirEnvVar}/{ConfigDirEnvVar}");
            return configuredUnifiedRoot;
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
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
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

            var destinationPath = Path.Join(targetPath, fileName);
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

            CopyDirectoryRecursive(directoryPath, Path.Join(targetPath, directoryName));
        }
    }

    public static string ResolveDataRootOrDefault(string defaultDataRoot)
    {
        var configuredUnifiedRoot = ResolveConfiguredUnifiedDataRootOrThrow();
        if (!string.IsNullOrWhiteSpace(configuredUnifiedRoot))
        {
            return configuredUnifiedRoot;
        }

        return Path.GetFullPath(defaultDataRoot);
    }

    public static string EnsureConfiguredDataAndConfigRoots(string defaultDataRoot)
    {
        var configuredUnifiedRoot = ResolveConfiguredUnifiedDataRootOrThrow();
        var effectiveRoot = string.IsNullOrWhiteSpace(configuredUnifiedRoot)
            ? Path.GetFullPath(defaultDataRoot)
            : configuredUnifiedRoot;
        Environment.SetEnvironmentVariable(ConfigDirEnvVar, effectiveRoot);
        Environment.SetEnvironmentVariable(DataDirEnvVar, effectiveRoot);
        return effectiveRoot;
    }

    private static string? ResolveConfiguredUnifiedDataRootOrThrow()
    {
        var configuredConfigDir = NormalizeConfiguredDataRoot(Environment.GetEnvironmentVariable(ConfigDirEnvVar));
        var configuredDataDir = NormalizeConfiguredDataRoot(Environment.GetEnvironmentVariable(DataDirEnvVar));

        if (!string.IsNullOrWhiteSpace(configuredConfigDir)
            && !string.IsNullOrWhiteSpace(configuredDataDir)
            && !string.Equals(configuredConfigDir, configuredDataDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configured data roots diverge: {ConfigDirEnvVar}='{configuredConfigDir}' and {DataDirEnvVar}='{configuredDataDir}'. " +
                "Set both to the same path.");
        }

        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return configuredDataDir;
        }

        return configuredConfigDir;
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
            ReconcileDatabaseLayoutConflict(normalizedRoot, scope, fileName, legacyPath, scopedPath);
        }

        if (File.Exists(legacyPath))
        {
            MoveDatabaseBundle(legacyPath, scopedPath);
        }

        return scopedPath;
    }

    private static void ReconcileDatabaseLayoutConflict(
        string dataRoot,
        string scope,
        string fileName,
        string legacyPath,
        string scopedPath)
    {
        var legacy = InspectDatabase(legacyPath, includeRowCounts: false);
        var scoped = InspectDatabase(scopedPath, includeRowCounts: false);
        if (!legacy.IsValid && !scoped.IsValid)
        {
            throw new InvalidOperationException(
                $"Database layout conflict for '{fileName}': neither '{legacyPath}' nor '{scopedPath}' is a valid SQLite database. Both files were preserved.");
        }

        var useLegacy = ShouldUseLegacyDatabase(scope, legacyPath, scopedPath, legacy, scoped);
        var backupDirectory = CreateMigrationBackupDirectory(dataRoot, scope);
        if (useLegacy)
        {
            ArchiveDatabaseBundle(scopedPath, backupDirectory, $"scoped-{fileName}");
            MoveDatabaseBundle(legacyPath, scopedPath);
            Console.WriteLine(
                $"Database migration: promoted legacy '{legacyPath}' to '{scopedPath}'. Previous scoped database archived in '{backupDirectory}'.");
            return;
        }

        ArchiveDatabaseBundle(legacyPath, backupDirectory, $"legacy-{fileName}");
        Console.WriteLine(
            $"Database migration: retained scoped '{scopedPath}'. Legacy database archived in '{backupDirectory}'.");
    }

    private static bool ShouldUseLegacyDatabase(
        string scope,
        string legacyPath,
        string scopedPath,
        DatabaseEvidence legacy,
        DatabaseEvidence scoped)
    {
        if (legacy.IsValid != scoped.IsValid)
        {
            return legacy.IsValid;
        }

        var legacyWithRows = InspectDatabase(legacyPath, includeRowCounts: true);
        var scopedWithRows = InspectDatabase(scopedPath, includeRowCounts: true);
        var anchorTables = ResolveDatabaseAnchorTables(scope);
        var legacyHasAnchors = anchorTables.Count > 0 && anchorTables.All(legacy.Tables.Contains);
        var scopedHasAnchors = anchorTables.Count > 0 && anchorTables.All(scoped.Tables.Contains);
        if (legacyHasAnchors != scopedHasAnchors)
        {
            return legacyHasAnchors;
        }
        if (legacyHasAnchors
            && scopedHasAnchors
            && legacyWithRows.RowCount != scopedWithRows.RowCount)
        {
            return legacyWithRows.RowCount > scopedWithRows.RowCount;
        }

        if (legacy.Tables.IsProperSupersetOf(scoped.Tables))
        {
            return true;
        }
        if (scoped.Tables.IsProperSupersetOf(legacy.Tables))
        {
            return false;
        }

        if (legacyWithRows.RowCount != scopedWithRows.RowCount)
        {
            return legacyWithRows.RowCount > scopedWithRows.RowCount;
        }

        if (legacyWithRows.FileLength != scopedWithRows.FileLength)
        {
            return legacyWithRows.FileLength > scopedWithRows.FileLength;
        }

        return false;
    }

    private static IReadOnlyList<string> ResolveDatabaseAnchorTables(string scope)
    {
        return scope.Trim().ToLowerInvariant() switch
        {
            "library" => new[] { "track", "audio_file", "folder" },
            "queue" => new[] { "download_task" },
            "identity" => new[] { "AspNetUsers" },
            _ => Array.Empty<string>()
        };
    }

    private static DatabaseEvidence InspectDatabase(string path, bool includeRowCounts)
    {
        var fileLength = new FileInfo(path).Length;
        if (fileLength == 0)
        {
            return new DatabaseEvidence(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, 0);
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA quick_check;";
                if (!string.Equals(integrity.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return new DatabaseEvidence(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, fileLength);
                }
            }

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
                using var reader = schema.ExecuteReader();
                while (reader.Read())
                {
                    var table = reader.GetString(0).Trim();
                    if (!string.IsNullOrWhiteSpace(table))
                    {
                        tables.Add(table);
                    }
                }
            }

            long rowCount = 0;
            if (includeRowCounts)
            {
                foreach (var table in tables)
                {
                    using var count = connection.CreateCommand();
                    count.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\";";
                    rowCount = checked(rowCount + Convert.ToInt64(count.ExecuteScalar()));
                }
            }

            return new DatabaseEvidence(true, tables, rowCount, fileLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DatabaseEvidence(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, fileLength);
        }
    }

    private static string CreateMigrationBackupDirectory(string dataRoot, string scope)
    {
        var backupDirectory = Path.Join(
            dataRoot,
            "db",
            "migration-backups",
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{scope}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDirectory);
        return backupDirectory;
    }

    private static void ArchiveDatabaseBundle(string sourcePath, string backupDirectory, string backupFileName)
    {
        var destinationPath = Path.Join(backupDirectory, backupFileName);
        MoveDatabaseBundle(sourcePath, destinationPath);
    }

    private static void MoveDatabaseBundle(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Move(sourcePath, destinationPath);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sourceSidecar = sourcePath + suffix;
            if (File.Exists(sourceSidecar))
            {
                File.Move(sourceSidecar, destinationPath + suffix);
            }
        }
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
