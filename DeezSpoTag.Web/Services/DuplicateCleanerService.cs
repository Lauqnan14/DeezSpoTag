using DeezSpoTag.Services.Library;
using System.Diagnostics;

namespace DeezSpoTag.Web.Services;

public sealed class DuplicateCleanResult
{
    public int FilesScanned { get; set; }
    public int DuplicatesFound { get; set; }
    public int Deleted { get; set; }
    public long SpaceFreedBytes { get; set; }
}

public sealed record DuplicateCleanerRunSummary(
    string Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    long? DurationMs,
    bool UseDuplicatesFolder,
    int FolderCount,
    int FilesScanned,
    int DuplicatesFound,
    int Deleted,
    long SpaceFreedBytes,
    string? ErrorMessage)
{
    public static DuplicateCleanerRunSummary Idle()
        => new("idle", DateTimeOffset.MinValue, null, null, true, 0, 0, 0, 0, 0, null);
}

public class DuplicateCleanerService
{
    private readonly record struct RunSummaryContext(
        DateTimeOffset StartedUtc,
        bool UseDuplicatesFolder,
        int FolderCount);

    public const string DuplicatesFolderName = "%duplicates%";
    private const string LegacyDuplicatesFolderName = "dups";
    private readonly object _lastRunLock = new();
    private DuplicateCleanerRunSummary _lastRun = DuplicateCleanerRunSummary.Idle();

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".aiff", ".aif", ".alac", ".m4a", ".m4b", ".aac", ".mp3", ".wma", ".ogg", ".opus"
    };

    private static readonly Dictionary<string, int> QualityRanks = new(StringComparer.OrdinalIgnoreCase)
    {
        [".flac"] = 1,
        [".wav"] = 1,
        [".aiff"] = 1,
        [".aif"] = 1,
        [".alac"] = 1,
        [".opus"] = 2,
        [".ogg"] = 2,
        [".m4a"] = 3,
        [".m4b"] = 3,
        [".aac"] = 3,
        [".mp3"] = 4,
        [".wma"] = 4
    };

    public DuplicateCleanerRunSummary GetLastRunSummary()
    {
        lock (_lastRunLock)
        {
            return _lastRun;
        }
    }

    public async Task<DuplicateCleanResult> ScanAsync(
        IReadOnlyList<FolderDto> folders,
        bool useDupsFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        // Duplicate cleaner is always non-destructive: duplicates are quarantined in %duplicates%.
        _ = useDupsFolder;
        const bool moveToDuplicatesFolder = true;
        var runContext = new RunSummaryContext(
            StartedUtc: DateTimeOffset.UtcNow,
            UseDuplicatesFolder: moveToDuplicatesFolder,
            FolderCount: folders.Count);

        var stopwatch = Stopwatch.StartNew();
        SetLastRun(new DuplicateCleanerRunSummary(
            Status: "running",
            StartedUtc: runContext.StartedUtc,
            FinishedUtc: null,
            DurationMs: null,
            UseDuplicatesFolder: runContext.UseDuplicatesFolder,
            FolderCount: runContext.FolderCount,
            FilesScanned: 0,
            DuplicatesFound: 0,
            Deleted: 0,
            SpaceFreedBytes: 0,
            ErrorMessage: null));

        var result = new DuplicateCleanResult();
        try
        {
            await ScanFoldersAsync(folders, moveToDuplicatesFolder, result, cancellationToken);
            stopwatch.Stop();
            var finishedUtc = DateTimeOffset.UtcNow;
            SetLastRun(BuildRunSummary(
                status: "completed",
                runContext: runContext,
                finishedUtc: finishedUtc,
                durationMs: stopwatch.ElapsedMilliseconds,
                result: result,
                errorMessage: null));

            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var finishedUtc = DateTimeOffset.UtcNow;
            SetLastRun(BuildRunSummary(
                status: "canceled",
                runContext: runContext,
                finishedUtc: finishedUtc,
                durationMs: stopwatch.ElapsedMilliseconds,
                result: result,
                errorMessage: null));
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var finishedUtc = DateTimeOffset.UtcNow;
            SetLastRun(BuildRunSummary(
                status: "error",
                runContext: runContext,
                finishedUtc: finishedUtc,
                durationMs: stopwatch.ElapsedMilliseconds,
                result: result,
                errorMessage: ex.Message));
            throw;
        }
    }

    private void SetLastRun(DuplicateCleanerRunSummary summary)
    {
        lock (_lastRunLock)
        {
            _lastRun = summary;
        }
    }

    private static async Task ScanFoldersAsync(
        IReadOnlyList<FolderDto> folders,
        bool moveToDuplicatesFolder,
        DuplicateCleanResult result,
        CancellationToken cancellationToken)
    {
        var roots = folders
            .Select(folder => folder.RootPath?.Trim())
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Cast<string>()
            .Where(Directory.Exists);

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScanFolderAsync(root, moveToDuplicatesFolder, result, cancellationToken);
        }
    }

    private static async Task ScanFolderAsync(
        string root,
        bool moveToDuplicatesFolder,
        DuplicateCleanResult result,
        CancellationToken cancellationToken)
    {
        var duplicatesRoot = Path.Join(root, DuplicatesFolderName);
        var legacyDuplicatesRoot = Path.Join(root, LegacyDuplicatesFolderName);
        Directory.CreateDirectory(duplicatesRoot);

        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => !IsInDuplicatesFolder(path, duplicatesRoot))
            .Where(path => !IsInDuplicatesFolder(path, legacyDuplicatesRoot))
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path)));

        var groups = files
            .Select(path => new FileInfo(path))
            .GroupBy(info =>
            {
                var directory = info.DirectoryName ?? string.Empty;
                var name = Path.GetFileNameWithoutExtension(info.Name);
                return $"{directory}|{name}";
            }, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var items = group.ToList();
            await ProcessDuplicateGroupAsync(items, root, duplicatesRoot, moveToDuplicatesFolder, result, cancellationToken);
        }
    }

    private static async Task ProcessDuplicateGroupAsync(
        IReadOnlyList<FileInfo> items,
        string root,
        string duplicatesRoot,
        bool moveToDuplicatesFolder,
        DuplicateCleanResult result,
        CancellationToken cancellationToken)
    {
        result.FilesScanned += items.Count;
        if (items.Count <= 1)
        {
            return;
        }

        result.DuplicatesFound += items.Count - 1;
        var best = PickBest(items);
        foreach (var file in items)
        {
            if (ReferenceEquals(file, best))
            {
                continue;
            }

            if (moveToDuplicatesFolder)
            {
                var relativePath = Path.GetRelativePath(root, file.FullName);
                var destinationPath = Path.Join(duplicatesRoot, relativePath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                destinationPath = EnsureUniquePath(destinationPath);
                await MoveFileAsync(file.FullName, destinationPath, cancellationToken);
            }

            result.Deleted += 1;
            result.SpaceFreedBytes += file.Length;
        }
    }

    private static DuplicateCleanerRunSummary BuildRunSummary(
        string status,
        RunSummaryContext runContext,
        DateTimeOffset finishedUtc,
        long durationMs,
        DuplicateCleanResult result,
        string? errorMessage)
    {
        return new DuplicateCleanerRunSummary(
            Status: status,
            StartedUtc: runContext.StartedUtc,
            FinishedUtc: finishedUtc,
            DurationMs: durationMs,
            UseDuplicatesFolder: runContext.UseDuplicatesFolder,
            FolderCount: runContext.FolderCount,
            FilesScanned: result.FilesScanned,
            DuplicatesFound: result.DuplicatesFound,
            Deleted: result.Deleted,
            SpaceFreedBytes: result.SpaceFreedBytes,
            ErrorMessage: errorMessage);
    }

    private static bool IsInDuplicatesFolder(string filePath, string duplicatesRoot)
    {
        if (string.IsNullOrWhiteSpace(duplicatesRoot))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(duplicatesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(filePath);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static FileInfo PickBest(IReadOnlyList<FileInfo> items)
    {
        return items
            .OrderBy(info => QualityRanks.TryGetValue(info.Extension, out var rank) ? rank : int.MaxValue)
            .ThenByDescending(info => info.Length)
            .First();
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var filename = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var counter = 1;
        string candidate;
        do
        {
            candidate = Path.Join(directory, $"{filename} ({counter}){extension}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }

    private static Task MoveFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(source, destination);
        return Task.CompletedTask;
    }

}
