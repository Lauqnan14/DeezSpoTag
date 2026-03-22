using System.Diagnostics;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Library;
using TagLib;
using IOFile = System.IO.File;

namespace DeezSpoTag.Web.Services;

public sealed class DuplicateCleanResult
{
    public int FilesScanned { get; set; }
    public int DuplicatesFound { get; set; }
    public int Deleted { get; set; }
    public long SpaceFreedBytes { get; set; }
    public string DuplicatesFolderName { get; set; } = DuplicateCleanerService.DuplicatesFolderName;
    public bool UsedShazamForIdentity { get; set; }
}

public sealed record DuplicateCleanerOptions
{
    public bool UseDuplicatesFolder { get; set; } = true;
    public string DuplicatesFolderName { get; set; } = DuplicateCleanerService.DuplicatesFolderName;
    public bool UseShazamForIdentity { get; set; }
}

public sealed record DuplicateCleanerRunSummary(
    string Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    long? DurationMs,
    bool UseDuplicatesFolder,
    string DuplicatesFolderName,
    bool UseShazamForIdentity,
    int FolderCount,
    int FilesScanned,
    int DuplicatesFound,
    int Deleted,
    long SpaceFreedBytes,
    string? ErrorMessage)
{
    public static DuplicateCleanerRunSummary Idle()
        => new("idle", DateTimeOffset.MinValue, null, null, true, DuplicateCleanerService.DuplicatesFolderName, false, 0, 0, 0, 0, 0, null);
}

public class DuplicateCleanerService
{
    private readonly record struct RunSummaryContext(
        DateTimeOffset StartedUtc,
        bool UseDuplicatesFolder,
        string DuplicatesFolderName,
        bool UseShazamForIdentity,
        int FolderCount);

    private sealed record DuplicateCandidate(
        string FullPath,
        string RelativePath,
        string BaseName,
        string Extension,
        long FileSize,
        int QualityRank,
        string Isrc,
        string Title,
        string Album,
        IReadOnlyList<string> Artists,
        int? TrackNumber,
        int? DiscNumber,
        int? DurationMs,
        string ShazamTrackId,
        string ShazamIsrc,
        string ShazamTitle,
        IReadOnlyList<string> ShazamArtists);

    private sealed class DisjointSet
    {
        private readonly int[] _parents;
        private readonly byte[] _ranks;

        public DisjointSet(int count)
        {
            _parents = new int[count];
            _ranks = new byte[count];
            for (var i = 0; i < count; i++)
            {
                _parents[i] = i;
            }
        }

        public int Find(int value)
        {
            if (_parents[value] != value)
            {
                _parents[value] = Find(_parents[value]);
            }

            return _parents[value];
        }

        public void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot)
            {
                return;
            }

            if (_ranks[leftRoot] < _ranks[rightRoot])
            {
                _parents[leftRoot] = rightRoot;
                return;
            }

            if (_ranks[leftRoot] > _ranks[rightRoot])
            {
                _parents[rightRoot] = leftRoot;
                return;
            }

            _parents[rightRoot] = leftRoot;
            _ranks[leftRoot]++;
        }
    }

    public const string DuplicatesFolderName = "%duplicates%";
    private const string LegacyDuplicatesFolderName = "dups";
    private const int DurationToleranceMs = 2000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled, RegexTimeout);
    private readonly object _lastRunLock = new();
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly ILogger<DuplicateCleanerService> _logger;
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

    public DuplicateCleanerService(
        ShazamRecognitionService shazamRecognitionService,
        ILogger<DuplicateCleanerService> logger)
    {
        _shazamRecognitionService = shazamRecognitionService;
        _logger = logger;
    }

    public DuplicateCleanerRunSummary GetLastRunSummary()
    {
        lock (_lastRunLock)
        {
            return _lastRun;
        }
    }

    public Task<DuplicateCleanResult> ScanAsync(
        IReadOnlyList<FolderDto> folders,
        bool useDupsFolder,
        CancellationToken cancellationToken = default)
    {
        return ScanAsync(
            folders,
            new DuplicateCleanerOptions
            {
                UseDuplicatesFolder = useDupsFolder,
                DuplicatesFolderName = DuplicatesFolderName,
                UseShazamForIdentity = false
            },
            cancellationToken);
    }

    public async Task<DuplicateCleanResult> ScanAsync(
        IReadOnlyList<FolderDto> folders,
        DuplicateCleanerOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var resolvedOptions = NormalizeOptions(options);
        var effectiveUseShazam = resolvedOptions.UseShazamForIdentity && _shazamRecognitionService.IsAvailable;
        var runContext = new RunSummaryContext(
            StartedUtc: DateTimeOffset.UtcNow,
            UseDuplicatesFolder: resolvedOptions.UseDuplicatesFolder,
            DuplicatesFolderName: resolvedOptions.DuplicatesFolderName,
            UseShazamForIdentity: effectiveUseShazam,
            FolderCount: folders.Count);

        var stopwatch = Stopwatch.StartNew();
        SetLastRun(new DuplicateCleanerRunSummary(
            Status: "running",
            StartedUtc: runContext.StartedUtc,
            FinishedUtc: null,
            DurationMs: null,
            UseDuplicatesFolder: runContext.UseDuplicatesFolder,
            DuplicatesFolderName: runContext.DuplicatesFolderName,
            UseShazamForIdentity: runContext.UseShazamForIdentity,
            FolderCount: runContext.FolderCount,
            FilesScanned: 0,
            DuplicatesFound: 0,
            Deleted: 0,
            SpaceFreedBytes: 0,
            ErrorMessage: null));

        var result = new DuplicateCleanResult
        {
            DuplicatesFolderName = runContext.DuplicatesFolderName,
            UsedShazamForIdentity = runContext.UseShazamForIdentity
        };

        try
        {
            await ScanFoldersAsync(folders, resolvedOptions with { UseShazamForIdentity = effectiveUseShazam }, result, cancellationToken);
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

    private static DuplicateCleanerOptions NormalizeOptions(DuplicateCleanerOptions? options)
    {
        var resolved = options ?? new DuplicateCleanerOptions();
        return new DuplicateCleanerOptions
        {
            UseDuplicatesFolder = resolved.UseDuplicatesFolder,
            UseShazamForIdentity = resolved.UseShazamForIdentity,
            DuplicatesFolderName = NormalizeDuplicatesFolderName(resolved.DuplicatesFolderName)
        };
    }

    private void SetLastRun(DuplicateCleanerRunSummary summary)
    {
        lock (_lastRunLock)
        {
            _lastRun = summary;
        }
    }

    private async Task ScanFoldersAsync(
        IReadOnlyList<FolderDto> folders,
        DuplicateCleanerOptions options,
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
            await ScanFolderAsync(root, options, result, cancellationToken);
        }
    }

    private async Task ScanFolderAsync(
        string root,
        DuplicateCleanerOptions options,
        DuplicateCleanResult result,
        CancellationToken cancellationToken)
    {
        var excludedRoots = BuildExcludedRoots(root, options.DuplicatesFolderName);
        if (options.UseDuplicatesFolder)
        {
            Directory.CreateDirectory(Path.Join(root, options.DuplicatesFolderName));
        }

        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => !IsInExcludedFolder(path, excludedRoots))
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
            .ToList();

        var candidates = new List<DuplicateCandidate>(files.Count);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = BuildCandidate(path, root, options.UseShazamForIdentity, cancellationToken);
            candidates.Add(candidate);
        }

        result.FilesScanned += candidates.Count;
        if (candidates.Count <= 1)
        {
            return;
        }

        var candidateGroups = BuildCandidateComponents(candidates);
        foreach (var component in candidateGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (component.Count <= 1)
            {
                continue;
            }

            foreach (var cluster in BuildValidatedClusters(component, candidates))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessDuplicateClusterAsync(cluster, candidates, root, options, result, cancellationToken);
            }
        }
    }

    private DuplicateCandidate BuildCandidate(
        string path,
        string root,
        bool useShazamForIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fileInfo = new FileInfo(path);
        var relativePath = Path.GetRelativePath(root, path);
        var baseName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var isrc = string.Empty;
        var title = string.Empty;
        var album = string.Empty;
        var artists = Array.Empty<string>();
        int? trackNumber = null;
        int? discNumber = null;
        int? durationMs = null;

        try
        {
            using var tagFile = TagLib.File.Create(path);
            var tag = tagFile.Tag;
            isrc = Normalize(tag.ISRC);
            title = Normalize(tag.Title);
            album = Normalize(tag.Album);
            artists = ResolveArtists(tag)
                .Select(Normalize)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
            trackNumber = tag.Track > 0 ? (int)tag.Track : null;
            discNumber = tag.Disc > 0 ? (int)tag.Disc : null;
            durationMs = tagFile.Properties.Duration.TotalMilliseconds > 0
                ? (int)Math.Round(tagFile.Properties.Duration.TotalMilliseconds)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Duplicate cleaner failed to read metadata for {Path}.", path);
        }

        var shazamTrackId = string.Empty;
        var shazamIsrc = string.Empty;
        var shazamTitle = string.Empty;
        var shazamArtists = Array.Empty<string>();
        if (useShazamForIdentity)
        {
            try
            {
                var recognition = _shazamRecognitionService.Recognize(path, cancellationToken);
                if (recognition != null)
                {
                    shazamTrackId = Normalize(recognition.TrackId);
                    shazamIsrc = Normalize(recognition.Isrc);
                    shazamTitle = Normalize(recognition.Title);
                    shazamArtists = recognition.Artists
                        .Select(Normalize)
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(static value => value, StringComparer.Ordinal)
                        .ToArray();
                    if (shazamArtists.Length == 0 && !string.IsNullOrWhiteSpace(recognition.Artist))
                    {
                        shazamArtists = new[] { Normalize(recognition.Artist) };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Duplicate cleaner Shazam recognition failed for {Path}.", path);
            }
        }

        return new DuplicateCandidate(
            FullPath: path,
            RelativePath: relativePath,
            BaseName: Normalize(baseName),
            Extension: extension,
            FileSize: fileInfo.Exists ? fileInfo.Length : 0,
            QualityRank: ResolveQualityRank(extension),
            Isrc: isrc,
            Title: title,
            Album: album,
            Artists: artists,
            TrackNumber: trackNumber,
            DiscNumber: discNumber,
            DurationMs: durationMs,
            ShazamTrackId: shazamTrackId,
            ShazamIsrc: shazamIsrc,
            ShazamTitle: shazamTitle,
            ShazamArtists: shazamArtists);
    }

    private static IReadOnlyList<List<int>> BuildCandidateComponents(IReadOnlyList<DuplicateCandidate> candidates)
    {
        var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < candidates.Count; index++)
        {
            foreach (var key in BuildGroupingKeys(candidates[index]))
            {
                if (!byKey.TryGetValue(key, out var values))
                {
                    values = new List<int>();
                    byKey[key] = values;
                }

                values.Add(index);
            }
        }

        var set = new DisjointSet(candidates.Count);
        foreach (var group in byKey.Values)
        {
            if (group.Count <= 1)
            {
                continue;
            }

            var first = group[0];
            for (var i = 1; i < group.Count; i++)
            {
                set.Union(first, group[i]);
            }
        }

        var components = new Dictionary<int, List<int>>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var root = set.Find(index);
            if (!components.TryGetValue(root, out var values))
            {
                values = new List<int>();
                components[root] = values;
            }

            values.Add(index);
        }

        return components.Values.ToList();
    }

    private static IEnumerable<List<int>> BuildValidatedClusters(
        IReadOnlyList<int> component,
        IReadOnlyList<DuplicateCandidate> candidates)
    {
        if (component.Count <= 1)
        {
            yield break;
        }

        var adjacency = new Dictionary<int, List<int>>();
        foreach (var index in component)
        {
            adjacency[index] = new List<int>();
        }

        for (var i = 0; i < component.Count; i++)
        {
            var leftIndex = component[i];
            for (var j = i + 1; j < component.Count; j++)
            {
                var rightIndex = component[j];
                if (!AreDuplicates(candidates[leftIndex], candidates[rightIndex]))
                {
                    continue;
                }

                adjacency[leftIndex].Add(rightIndex);
                adjacency[rightIndex].Add(leftIndex);
            }
        }

        var visited = new HashSet<int>();
        foreach (var index in component)
        {
            if (!visited.Add(index) || adjacency[index].Count == 0)
            {
                continue;
            }

            var cluster = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(index);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                cluster.Add(current);
                foreach (var next in adjacency[current])
                {
                    if (visited.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            if (cluster.Count > 1)
            {
                yield return cluster;
            }
        }
    }

    private static IEnumerable<string> BuildGroupingKeys(DuplicateCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.ShazamTrackId))
        {
            yield return $"shazam-track:{candidate.ShazamTrackId}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.ShazamIsrc))
        {
            yield return $"shazam-isrc:{candidate.ShazamIsrc}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.Isrc))
        {
            yield return $"isrc:{candidate.Isrc}";
        }

        var identityTitle = !string.IsNullOrWhiteSpace(candidate.ShazamTitle) ? candidate.ShazamTitle : candidate.Title;
        var identityArtists = candidate.ShazamArtists.Count > 0 ? candidate.ShazamArtists : candidate.Artists;
        if (!string.IsNullOrWhiteSpace(identityTitle) && identityArtists.Count > 0 && candidate.DurationMs.HasValue)
        {
            yield return $"meta:{string.Join("|", identityArtists)}|{identityTitle}|{BucketDuration(candidate.DurationMs.Value)}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.Album) && candidate.TrackNumber.HasValue)
        {
            yield return $"album-track:{candidate.Album}|{candidate.TrackNumber.Value}|{candidate.DiscNumber.GetValueOrDefault()}";
        }

        yield return $"basename:{Path.GetDirectoryName(candidate.RelativePath)?.ToLowerInvariant() ?? string.Empty}|{candidate.BaseName}";
    }

    private static bool AreDuplicates(DuplicateCandidate left, DuplicateCandidate right)
    {
        if (!string.IsNullOrWhiteSpace(left.ShazamTrackId)
            && string.Equals(left.ShazamTrackId, right.ShazamTrackId, StringComparison.Ordinal)
            && DurationMatches(left.DurationMs, right.DurationMs))
        {
            return true;
        }

        var leftIsrc = !string.IsNullOrWhiteSpace(left.ShazamIsrc) ? left.ShazamIsrc : left.Isrc;
        var rightIsrc = !string.IsNullOrWhiteSpace(right.ShazamIsrc) ? right.ShazamIsrc : right.Isrc;
        if (!string.IsNullOrWhiteSpace(leftIsrc)
            && string.Equals(leftIsrc, rightIsrc, StringComparison.Ordinal)
            && DurationMatches(left.DurationMs, right.DurationMs))
        {
            return true;
        }

        if (AudioCollisionDedupe.IsDuplicate(left.FullPath, right.FullPath))
        {
            return true;
        }

        var leftTitle = !string.IsNullOrWhiteSpace(left.ShazamTitle) ? left.ShazamTitle : left.Title;
        var rightTitle = !string.IsNullOrWhiteSpace(right.ShazamTitle) ? right.ShazamTitle : right.Title;
        var leftArtists = left.ShazamArtists.Count > 0 ? left.ShazamArtists : left.Artists;
        var rightArtists = right.ShazamArtists.Count > 0 ? right.ShazamArtists : right.Artists;
        return !string.IsNullOrWhiteSpace(leftTitle)
            && string.Equals(leftTitle, rightTitle, StringComparison.Ordinal)
            && DurationMatches(left.DurationMs, right.DurationMs)
            && HaveSharedArtist(leftArtists, rightArtists);
    }

    private static async Task ProcessDuplicateClusterAsync(
        IReadOnlyList<int> cluster,
        IReadOnlyList<DuplicateCandidate> candidates,
        string root,
        DuplicateCleanerOptions options,
        DuplicateCleanResult result,
        CancellationToken cancellationToken)
    {
        result.DuplicatesFound += cluster.Count - 1;
        var bestIndex = PickBest(cluster, candidates);

        foreach (var index in cluster)
        {
            if (index == bestIndex)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var file = candidates[index];
            if (options.UseDuplicatesFolder)
            {
                var destinationRoot = Path.Join(root, options.DuplicatesFolderName);
                var destinationPath = Path.Join(destinationRoot, file.RelativePath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                destinationPath = EnsureUniquePath(destinationPath);
                await MoveFileAsync(file.FullPath, destinationPath, cancellationToken);
            }
            else
            {
                IOFile.Delete(file.FullPath);
            }

            result.Deleted += 1;
            result.SpaceFreedBytes += file.FileSize;
        }
    }

    private static int PickBest(IReadOnlyList<int> cluster, IReadOnlyList<DuplicateCandidate> candidates)
    {
        return cluster
            .OrderBy(index => candidates[index].QualityRank)
            .ThenByDescending(index => candidates[index].FileSize)
            .ThenByDescending(index => ComputeMetadataScore(candidates[index]))
            .First();
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
            DuplicatesFolderName: runContext.DuplicatesFolderName,
            UseShazamForIdentity: runContext.UseShazamForIdentity,
            FolderCount: runContext.FolderCount,
            FilesScanned: result.FilesScanned,
            DuplicatesFound: result.DuplicatesFound,
            Deleted: result.Deleted,
            SpaceFreedBytes: result.SpaceFreedBytes,
            ErrorMessage: errorMessage);
    }

    private static IReadOnlyList<string> BuildExcludedRoots(string root, string duplicatesFolderName)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeDirectory(Path.Join(root, duplicatesFolderName)),
            NormalizeDirectory(Path.Join(root, DuplicatesFolderName)),
            NormalizeDirectory(Path.Join(root, LegacyDuplicatesFolderName))
        };
        return results.ToList();
    }

    private static bool IsInExcludedFolder(string filePath, IReadOnlyList<string> excludedRoots)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        return excludedRoots.Any(root => normalizedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDirectory(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private static int ResolveQualityRank(string extension)
        => QualityRanks.TryGetValue(extension, out var rank) ? rank : int.MaxValue;

    private static int ComputeMetadataScore(DuplicateCandidate candidate)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(candidate.Isrc) || !string.IsNullOrWhiteSpace(candidate.ShazamIsrc))
        {
            score += 5;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Title) || !string.IsNullOrWhiteSpace(candidate.ShazamTitle))
        {
            score += 3;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Album))
        {
            score += 2;
        }

        if (candidate.Artists.Count > 0 || candidate.ShazamArtists.Count > 0)
        {
            score += 3;
        }

        if (!string.IsNullOrWhiteSpace(candidate.ShazamTrackId))
        {
            score += 4;
        }

        if (candidate.TrackNumber.HasValue)
        {
            score += 1;
        }

        if (candidate.DiscNumber.HasValue)
        {
            score += 1;
        }

        if (candidate.DurationMs.HasValue)
        {
            score += 1;
        }

        return score;
    }

    private static bool HaveSharedArtist(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return false;
        }

        return left.Intersect(right, StringComparer.Ordinal).Any();
    }

    private static bool DurationMatches(int? leftMs, int? rightMs)
    {
        if (!leftMs.HasValue || !rightMs.HasValue)
        {
            return true;
        }

        return Math.Abs(leftMs.Value - rightMs.Value) <= DurationToleranceMs;
    }

    private static int BucketDuration(int durationMs)
    {
        return (int)Math.Round(durationMs / 2000d, MidpointRounding.AwayFromZero);
    }

    private static IEnumerable<string> ResolveArtists(Tag tag)
    {
        var albumArtists = tag.AlbumArtists ?? Array.Empty<string>();
        if (albumArtists.Any(static value => !string.IsNullOrWhiteSpace(value)))
        {
            return albumArtists;
        }

        return tag.Performers ?? Array.Empty<string>();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return WhitespaceRegex.Replace(value.Trim(), " ").ToLowerInvariant();
    }

    private static string NormalizeDuplicatesFolderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DuplicatesFolderName;
        }

        var fileName = Path.GetFileName(value.Trim());
        if (string.IsNullOrWhiteSpace(fileName) || string.Equals(fileName, ".", StringComparison.Ordinal))
        {
            return DuplicatesFolderName;
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(ch => invalidCharacters.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? DuplicatesFolderName : sanitized;
    }

    private static string EnsureUniquePath(string path)
    {
        if (!IOFile.Exists(path))
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
        } while (IOFile.Exists(candidate));

        return candidate;
    }

    private static Task MoveFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IOFile.Move(source, destination);
        return Task.CompletedTask;
    }
}
