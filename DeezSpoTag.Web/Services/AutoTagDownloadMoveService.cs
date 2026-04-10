using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Conversion;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TagLib;
using IOFile = System.IO.File;

namespace DeezSpoTag.Web.Services;

public sealed class AutoTagMoveSummary
{
    public int MovedCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> DestinationRoots { get; set; } = new();
    public bool RecoveryCleanup { get; set; }
    public string? Error { get; set; }

    public void AddDestinationRoot(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        var normalized = DownloadPathResolver.NormalizeDisplayPath(rootPath);
        if (DestinationRoots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        DestinationRoots.Add(normalized);
    }

    public AutoTagMoveSummary Clone()
    {
        return new AutoTagMoveSummary
        {
            MovedCount = MovedCount,
            SkippedCount = SkippedCount,
            FailedCount = FailedCount,
            DestinationRoots = DestinationRoots.ToList(),
            RecoveryCleanup = RecoveryCleanup,
            Error = Error
        };
    }
}

public sealed class AutoTagDownloadMoveService
{
    private sealed record ResidualMoveContext(
        string RootPath,
        AutoTagOrganizerOptions Options,
        string OverwritePolicy,
        string? DestinationRootOverride,
        ConversionPlan ConversionPlan,
        IReadOnlyCollection<string> TaggedFiles,
        IReadOnlyCollection<string> FailedFiles);

    private sealed record PayloadSourceMaps(
        Dictionary<long, HashSet<string>> FilesByDestination,
        Dictionary<long, HashSet<string>> RootsByDestination,
        Dictionary<long, Dictionary<string, string?>> FileBucketsByDestination,
        Dictionary<long, Dictionary<string, string?>> RootBucketsByDestination,
        Dictionary<long, Dictionary<string, HashSet<string>>> FileOwnersByDestination,
        Dictionary<long, Dictionary<string, HashSet<string>>> RootOwnersByDestination);

    private sealed record DestinationMoveContext(
        string RootPath,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings Settings,
        string DestinationRoot,
        long DestinationKey,
        ConversionPlan ConversionPlan,
        Dictionary<string, Dictionary<string, string>> TransitionsByQueue,
        CancellationToken CancellationToken);

    private sealed record MoveFileContext(
        string SourceIo,
        string DestinationIo,
        string DestinationPath,
        string? DestinationDir);

    private sealed record ResidualPaths(
        string RootIo,
        string SuccessIo,
        string? FailedIo);

    private sealed record ResidualOptions(
        AutoTagOrganizerOptions OrganizerOptions,
        string OverwritePolicy,
        IReadOnlyList<string> PreferredExtensions,
        ConversionPlan ConversionPlan);

    private sealed record ResidualClassification(
        IReadOnlySet<string> TaggedSet,
        IReadOnlySet<string> FailedSet,
        bool ExplicitResultSets);

    private sealed record ResidualRuntime(
        ResidualPaths Paths,
        ResidualOptions Options,
        ResidualClassification Classification,
        IReadOnlyList<string> Files);

    private sealed record ResidualBuckets(
        Dictionary<string, ResidualBucket> AudioBuckets,
        HashSet<string> TaggedAudioKeys,
        Dictionary<string, ResidualBucket> SidecarBucketsByKey,
        Dictionary<string, ResidualBucket> FolderBucketsByDirectory);

    private const string FilePathProperty = "filePath";
    private const string FilesProperty = "files";
    private static readonly HashSet<string> DirectoryArtworkSidecarNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover",
        "folder",
        "front",
        "album",
        "albumart",
        "artwork"
    };

    private static readonly HashSet<string> DirectoryArtworkSidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    };

    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly LibraryRepository _libraryRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly QuickTagService _quickTagService;
    private readonly ILogger<AutoTagDownloadMoveService> _logger;

    public AutoTagDownloadMoveService(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        LibraryRepository libraryRepository,
        IServiceScopeFactory scopeFactory,
        QuickTagService quickTagService,
        ILogger<AutoTagDownloadMoveService> logger)
    {
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _libraryRepository = libraryRepository;
        _scopeFactory = scopeFactory;
        _quickTagService = quickTagService;
        _logger = logger;
    }

    public Task MoveForRootAsync(string rootPath, CancellationToken cancellationToken)
    {
        return MoveForRootAsync(rootPath, new AutoTagOrganizerOptions(), cancellationToken);
    }

    public async Task MoveForRootAsync(string rootPath, AutoTagOrganizerOptions options, CancellationToken cancellationToken)
    {
        _ = await MoveForRootWithSummaryAsync(
            rootPath,
            options,
            Array.Empty<string>(),
            Array.Empty<string>(),
            cancellationToken);
    }

    public async Task MoveForRootAsync(
        string rootPath,
        AutoTagOrganizerOptions options,
        IReadOnlyCollection<string> taggedFiles,
        IReadOnlyCollection<string> failedFiles,
        CancellationToken cancellationToken)
    {
        _ = await MoveForRootWithSummaryAsync(
            rootPath,
            options,
            taggedFiles,
            failedFiles,
            cancellationToken);
    }

    public Task<AutoTagMoveSummary> MoveForRootWithSummaryAsync(string rootPath, CancellationToken cancellationToken)
    {
        return MoveForRootWithSummaryAsync(rootPath, new AutoTagOrganizerOptions(), Array.Empty<string>(), Array.Empty<string>(), cancellationToken);
    }

    public Task<AutoTagMoveSummary> MoveForRootWithSummaryAsync(
        string rootPath,
        AutoTagOrganizerOptions options,
        CancellationToken cancellationToken)
    {
        return MoveForRootWithSummaryAsync(rootPath, options, Array.Empty<string>(), Array.Empty<string>(), cancellationToken);
    }

    public async Task<AutoTagMoveSummary> MoveForRootWithSummaryAsync(
        string rootPath,
        AutoTagOrganizerOptions options,
        IReadOnlyCollection<string> taggedFiles,
        IReadOnlyCollection<string> failedFiles,
        CancellationToken cancellationToken)
    {
        var summary = new AutoTagMoveSummary();
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return summary;
        }

        var normalizedRootPath = ResolveExistingDirectoryPath(rootPath);
        var settings = _settingsService.LoadSettings();
        var items = await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var foldersById = await LoadFoldersByIdAsync(cancellationToken);
        await MoveRemainingContentByDestinationAsync(items, normalizedRootPath, settings, foldersById, summary, cancellationToken);
        var residualDestinationFolderId = ResolveResidualDestinationFolderId(items, normalizedRootPath);
        var residualDestination = await ResolveDestinationRootAsync(residualDestinationFolderId, cancellationToken);
        summary.AddDestinationRoot(residualDestination);
        var residualConversion = BuildConversionPlan(
            settings,
            residualDestinationFolderId.HasValue && foldersById.TryGetValue(residualDestinationFolderId.Value, out var residualFolder)
                ? residualFolder
                : null);
        var overwritePolicy = string.IsNullOrWhiteSpace(settings.OverwriteFile) ? "y" : settings.OverwriteFile;
        var residualTransitions = await MoveResidualFilesAsync(
            new ResidualMoveContext(
                normalizedRootPath,
                options,
                overwritePolicy,
                residualDestination,
                residualConversion,
                taggedFiles,
                failedFiles),
            summary,
            cancellationToken);
        await PersistFinalDestinationsByPayloadLookupAsync(items, residualTransitions, cancellationToken);

        if (!options.DryRun && options.RemoveEmptyFolders)
        {
            DeleteEmptyDirectories(normalizedRootPath, BuildProtectedQualityBucketDirectories(normalizedRootPath));
        }

        return summary;
    }

    private static bool IsUnderRoot(string rootPath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var rootIo = DownloadPathResolver.ResolveIoPath(rootPath);
        var candidateIo = DownloadPathResolver.ResolveIoPath(candidatePath);
        if (string.IsNullOrWhiteSpace(rootIo) || string.IsNullOrWhiteSpace(candidateIo))
        {
            return false;
        }

        var rootFull = NormalizeRootPath(rootIo);
        var candidateFull = NormalizeCandidatePath(candidateIo, rootIo);
        return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRootPath(string rootPath)
    {
        var normalized = DownloadPathResolver.IsSmbPath(rootPath) ? rootPath : Path.GetFullPath(rootPath);
        var separator = normalized.Contains('\\') ? "\\" : "/";
        return normalized.EndsWith(separator, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + separator;
    }

    private static string NormalizeCandidatePath(string candidatePath, string rootPath)
    {
        if (DownloadPathResolver.IsSmbPath(candidatePath) || DownloadPathResolver.IsSmbPath(rootPath))
        {
            return candidatePath;
        }

        return Path.GetFullPath(candidatePath);
    }

    private async Task MoveRemainingContentByDestinationAsync(
        IReadOnlyList<DownloadQueueItem> items,
        string rootPath,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IReadOnlyDictionary<long, FolderDto> foldersById,
        AutoTagMoveSummary summary,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        var filesByDestination = new Dictionary<long, HashSet<string>>();
        var rootsByDestination = new Dictionary<long, HashSet<string>>();
        var fileBucketsByDestination = new Dictionary<long, Dictionary<string, string?>>();
        var rootBucketsByDestination = new Dictionary<long, Dictionary<string, string?>>();
        var fileOwnersByDestination = new Dictionary<long, Dictionary<string, HashSet<string>>>();
        var rootOwnersByDestination = new Dictionary<long, Dictionary<string, HashSet<string>>>();
        var payloadSourceMaps = new PayloadSourceMaps(
            filesByDestination,
            rootsByDestination,
            fileBucketsByDestination,
            rootBucketsByDestination,
            fileOwnersByDestination,
            rootOwnersByDestination);
        var transitionsByQueue = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        PopulatePayloadSourceMaps(items, rootPath, payloadSourceMaps);

        if (rootsByDestination.Count == 0 && filesByDestination.Count == 0)
        {
            return;
        }

        var destinationKeys = CollectDestinationKeys(filesByDestination, rootsByDestination);

        foreach (var destinationKey in destinationKeys)
        {
            var destinationRoot = await ResolveDestinationRootAsync(destinationKey == 0 ? null : destinationKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(destinationRoot))
            {
                continue;
            }
            summary.AddDestinationRoot(destinationRoot);
            var conversionPlan = BuildConversionPlan(
                settings,
                destinationKey > 0 && foldersById.TryGetValue(destinationKey, out var destinationFolder)
                    ? destinationFolder
                    : null);

            var destinationContext = new DestinationMoveContext(
                rootPath,
                settings,
                destinationRoot,
                destinationKey,
                conversionPlan,
                transitionsByQueue,
                cancellationToken);
            await MoveDestinationFilesAsync(destinationContext, payloadSourceMaps, summary);
            await MoveDestinationRootsAsync(destinationContext, payloadSourceMaps, summary);
        }

        await PersistFinalDestinationsAsync(items, transitionsByQueue, cancellationToken);
    }

    private static void PopulatePayloadSourceMaps(
        IReadOnlyList<DownloadQueueItem> items,
        string rootPath,
        PayloadSourceMaps payloadSourceMaps)
    {
        foreach (var item in items)
        {
            if (!IsCompletedStatus(item.Status) || string.IsNullOrWhiteSpace(item.PayloadJson))
            {
                continue;
            }

            AddGenericPayloadSources(payloadSourceMaps, item, rootPath);
        }
    }

    private static HashSet<long> CollectDestinationKeys(
        IReadOnlyDictionary<long, HashSet<string>> filesByDestination,
        IReadOnlyDictionary<long, HashSet<string>> rootsByDestination)
    {
        var destinationKeys = new HashSet<long>();
        foreach (var key in filesByDestination.Keys)
        {
            destinationKeys.Add(key);
        }

        foreach (var key in rootsByDestination.Keys)
        {
            destinationKeys.Add(key);
        }

        return destinationKeys;
    }

    private async Task MoveDestinationFilesAsync(
        DestinationMoveContext context,
        PayloadSourceMaps maps,
        AutoTagMoveSummary summary)
    {
        if (!maps.FilesByDestination.TryGetValue(context.DestinationKey, out var fileSet) || fileSet.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Auto-move grouping: destination {Destination} with {FileCount} explicit files",
            context.DestinationRoot,
            fileSet.Count);

        foreach (var filePath in fileSet)
        {
            await ProcessDestinationFileAsync(filePath, context, maps, summary);
        }
    }

    private async Task MoveDestinationRootsAsync(
        DestinationMoveContext context,
        PayloadSourceMaps maps,
        AutoTagMoveSummary summary)
    {
        if (!maps.RootsByDestination.TryGetValue(context.DestinationKey, out var rootSet) || rootSet.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Auto-move grouping: destination {Destination} with {RootCount} fallback roots",
            context.DestinationRoot,
            rootSet.Count);

        foreach (var root in rootSet)
        {
            await ProcessDestinationRootAsync(root, context, maps, summary);
        }
    }

    private async Task ProcessDestinationFileAsync(
        string filePath,
        DestinationMoveContext context,
        PayloadSourceMaps maps,
        AutoTagMoveSummary summary)
    {
        var sourceDisplay = DownloadPathResolver.NormalizeDisplayPath(DownloadPathResolver.ResolveIoPath(filePath) ?? filePath);
        try
        {
            var qualityBucket = TryResolveBucket(maps.FileBucketsByDestination, context.DestinationKey, filePath);
            var movedPath = MoveFileUnderRoot(
                context.RootPath,
                filePath,
                context.DestinationRoot,
                context.Settings,
                qualityBucket);
            if (string.IsNullOrWhiteSpace(movedPath))
            {
                summary.SkippedCount++;
                return;
            }

            var finalPath = await ApplyDestinationConversionWithFallbackAsync(
                movedPath,
                context,
                summary,
                isRootItem: false);

            var owners = ResolvePathOwners(maps.FileOwnersByDestination, context.DestinationKey, filePath);
            RememberTransitionsForOwners(context.TransitionsByQueue, owners, filePath, finalPath);
            if (DidPathChange(sourceDisplay, movedPath))
            {
                summary.MovedCount++;
                return;
            }

            summary.SkippedCount++;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            summary.FailedCount++;
            _logger.LogWarning(ex, "Auto-move destination file failed for {Path}", filePath);
        }
    }

    private async Task ProcessDestinationRootAsync(
        string root,
        DestinationMoveContext context,
        PayloadSourceMaps maps,
        AutoTagMoveSummary summary)
    {
        var candidateCount = TryCountFiles(root);
        try
        {
            var qualityBucket = TryResolveBucket(maps.RootBucketsByDestination, context.DestinationKey, root);
            var movedPaths = MoveDirectoryTreeUnderRoot(
                context.RootPath,
                root,
                context.DestinationRoot,
                context.Settings,
                qualityBucket);
            if (movedPaths.Count == 0)
            {
                summary.SkippedCount += candidateCount;
                return;
            }

            summary.MovedCount += movedPaths.Count;
            if (candidateCount > movedPaths.Count)
            {
                summary.SkippedCount += candidateCount - movedPaths.Count;
            }

            var owners = ResolvePathOwners(maps.RootOwnersByDestination, context.DestinationKey, root);
            await RememberDestinationRootTransitionsAsync(movedPaths, owners, context, summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            summary.FailedCount += Math.Max(1, candidateCount);
            _logger.LogWarning(ex, "Auto-move destination root failed for {Root}", root);
        }
    }

    private async Task RememberDestinationRootTransitionsAsync(
        IReadOnlyDictionary<string, string> movedPaths,
        IReadOnlyCollection<string> owners,
        DestinationMoveContext context,
        AutoTagMoveSummary summary)
    {
        foreach (var transition in movedPaths)
        {
            var finalPath = await ApplyDestinationConversionWithFallbackAsync(
                transition.Value,
                context,
                summary,
                isRootItem: true);
            RememberTransitionsForOwners(context.TransitionsByQueue, owners, transition.Key, finalPath);
        }
    }

    private async Task<string> ApplyDestinationConversionWithFallbackAsync(
        string movedPath,
        DestinationMoveContext context,
        AutoTagMoveSummary summary,
        bool isRootItem)
    {
        try
        {
            return await ApplyDestinationConversionIfNeededAsync(
                movedPath,
                context.ConversionPlan,
                context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            summary.FailedCount++;
            if (isRootItem)
            {
                _logger.LogWarning(ex, "Auto-move conversion failed for destination root item {Path}", movedPath);
            }
            else
            {
                _logger.LogWarning(ex, "Auto-move conversion failed for destination file {Path}", movedPath);
            }

            return movedPath;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> MoveResidualFilesAsync(
        ResidualMoveContext context,
        AutoTagMoveSummary summary,
        CancellationToken cancellationToken)
    {
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var runtime = await BuildResidualRuntimeAsync(context, cancellationToken);
        if (runtime is null)
        {
            return moved;
        }

        summary.AddDestinationRoot(runtime.Paths.SuccessIo);
        summary.AddDestinationRoot(runtime.Paths.FailedIo);

        var buckets = BuildResidualBuckets(runtime);
        foreach (var file in runtime.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movedBefore = moved.Count;
            try
            {
                if (IsAudioExtension(file))
                {
                    await ProcessResidualAudioFileAsync(file, runtime, buckets, moved, cancellationToken);
                }
                else
                {
                    ProcessResidualSidecarFile(file, runtime, buckets, moved);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                summary.FailedCount++;
                _logger.LogWarning(ex, "Auto-move residual processing failed for {Path}", file);
                continue;
            }

            if (moved.Count > movedBefore)
            {
                summary.MovedCount += moved.Count - movedBefore;
            }
            else
            {
                summary.SkippedCount++;
            }
        }

        if (!runtime.Options.OrganizerOptions.DryRun && runtime.Options.OrganizerOptions.RemoveEmptyFolders)
        {
            DeleteEmptyDirectories(runtime.Paths.RootIo, BuildProtectedQualityBucketDirectories(runtime.Paths.RootIo));
        }

        return moved;
    }

    private static bool DidPathChange(string sourcePath, string destinationPath)
    {
        return !string.Equals(
            DownloadPathResolver.NormalizeDisplayPath(sourcePath),
            DownloadPathResolver.NormalizeDisplayPath(destinationPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static int TryCountFiles(string rootPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return 0;
            }

            return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    private async Task<ResidualRuntime?> BuildResidualRuntimeAsync(
        ResidualMoveContext context,
        CancellationToken cancellationToken)
    {
        var resolvedSuccessRoot = await ResolveResidualSuccessRootAsync(context, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedSuccessRoot)
            || !TryResolveResidualPaths(context.RootPath, resolvedSuccessRoot, context.Options.MoveUntaggedPath, out var paths))
        {
            return null;
        }

        if (string.Equals(paths.RootIo, paths.SuccessIo, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(paths.FailedIo))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(
                paths.RootIo,
                "*",
                context.Options.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .ToList();
        if (files.Count == 0)
        {
            if (!context.Options.DryRun && context.Options.RemoveEmptyFolders)
            {
                DeleteEmptyDirectories(paths.RootIo, BuildProtectedQualityBucketDirectories(paths.RootIo));
            }

            return null;
        }

        var taggedSet = BuildNormalizedPathSet(context.TaggedFiles);
        var failedSet = BuildNormalizedPathSet(context.FailedFiles);
        var preferredExtensions = context.Options.PreferredExtensions
            .Select(value => value.Trim().TrimStart('.'))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        return new ResidualRuntime(
            paths,
            new ResidualOptions(
                context.Options,
                context.OverwritePolicy,
                preferredExtensions,
                context.ConversionPlan),
            new ResidualClassification(
                taggedSet,
                failedSet,
                taggedSet.Count > 0 || failedSet.Count > 0),
            files);
    }

    private async Task<string?> ResolveResidualSuccessRootAsync(
        ResidualMoveContext context,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(context.Options.MoveTaggedPath))
        {
            return context.Options.MoveTaggedPath;
        }

        if (!string.IsNullOrWhiteSpace(context.DestinationRootOverride))
        {
            return context.DestinationRootOverride;
        }

        return await ResolveDestinationRootAsync(null, cancellationToken);
    }

    private static bool TryResolveResidualPaths(
        string rootPath,
        string successRoot,
        string? failedRoot,
        out ResidualPaths paths)
    {
        paths = default!;

        var rootIo = DownloadPathResolver.ResolveIoPath(rootPath);
        var successIo = DownloadPathResolver.ResolveIoPath(successRoot);
        if (string.IsNullOrWhiteSpace(rootIo)
            || string.IsNullOrWhiteSpace(successIo)
            || !Directory.Exists(rootIo)
            || DownloadPathResolver.IsSmbPath(rootIo)
            || DownloadPathResolver.IsSmbPath(successIo))
        {
            return false;
        }

        var failedIo = string.IsNullOrWhiteSpace(failedRoot)
            ? null
            : DownloadPathResolver.ResolveIoPath(failedRoot);
        paths = new ResidualPaths(rootIo, successIo, failedIo);
        return true;
    }

    private static ResidualBuckets BuildResidualBuckets(ResidualRuntime runtime)
    {
        var audioBuckets = new Dictionary<string, ResidualBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in runtime.Files)
        {
            if (!IsAudioExtension(file))
            {
                continue;
            }

            var bucket = GetAudioBucket(
                file,
                runtime.Options.OrganizerOptions,
                runtime.Classification.ExplicitResultSets,
                runtime.Classification.TaggedSet,
                runtime.Classification.FailedSet);
            audioBuckets[file] = bucket;
        }

        var taggedAudioKeys = audioBuckets
            .Where(pair => pair.Value == ResidualBucket.Tagged)
            .Select(pair => BuildSidecarKey(pair.Key))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (runtime.Classification.ExplicitResultSets)
        {
            foreach (var file in runtime.Classification.TaggedSet)
            {
                if (!IsAudioExtension(file) || !IsUnderRoot(runtime.Paths.RootIo, file))
                {
                    continue;
                }

                var key = BuildSidecarKey(file);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    taggedAudioKeys.Add(key);
                }
            }
        }

        return new ResidualBuckets(
            audioBuckets,
            taggedAudioKeys,
            BuildSidecarBuckets(audioBuckets, runtime),
            BuildFolderBuckets(audioBuckets, runtime));
    }

    private async Task ProcessResidualAudioFileAsync(
        string file,
        ResidualRuntime runtime,
        ResidualBuckets buckets,
        Dictionary<string, string> moved,
        CancellationToken cancellationToken)
    {
        var bucket = buckets.AudioBuckets.TryGetValue(file, out var resolved)
            ? resolved
            : GetAudioBucket(
                file,
                runtime.Options.OrganizerOptions,
                runtime.Classification.ExplicitResultSets,
                runtime.Classification.TaggedSet,
                runtime.Classification.FailedSet);
        if (bucket == ResidualBucket.Skip)
        {
            return;
        }

        if (bucket == ResidualBucket.Failed && string.IsNullOrWhiteSpace(runtime.Paths.FailedIo))
        {
            if (IsTaggedDuplicateArtifact(file, buckets.TaggedAudioKeys))
            {
                // Drop failed duplicate artifacts (for example stale *.wav alongside tagged *.flac).
                TryDeleteDuplicateSource(file);
                return;
            }

            // No failed target configured: do not strand files in staging.
            bucket = ResidualBucket.Tagged;
        }

        var target = bucket == ResidualBucket.Failed ? runtime.Paths.FailedIo : runtime.Paths.SuccessIo;
        if (string.IsNullOrWhiteSpace(target)
            || string.Equals(target, runtime.Paths.RootIo, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var movedPath = MoveResidualFile(
            file,
            runtime.Paths.RootIo,
            target,
            runtime.Options.OrganizerOptions,
            runtime.Options.OverwritePolicy,
            runtime.Options.PreferredExtensions);
        if (string.IsNullOrWhiteSpace(movedPath))
        {
            return;
        }

        var finalPath = await ApplyDestinationConversionIfNeededAsync(
            movedPath,
            runtime.Options.ConversionPlan,
            cancellationToken);
        moved[DownloadPathResolver.NormalizeDisplayPath(file)] = finalPath;
    }

    private static bool IsTaggedDuplicateArtifact(string file, HashSet<string> taggedAudioKeys)
    {
        var sidecarKey = BuildSidecarKey(file);
        return !string.IsNullOrWhiteSpace(sidecarKey) && taggedAudioKeys.Contains(sidecarKey);
    }

    private static void ProcessResidualSidecarFile(
        string file,
        ResidualRuntime runtime,
        ResidualBuckets buckets,
        Dictionary<string, string> moved)
    {
        var sidecarBucket = ResolveSidecarBucket(
            file,
            buckets.SidecarBucketsByKey,
            buckets.FolderBucketsByDirectory,
            runtime.Paths.RootIo);
        if (sidecarBucket == ResidualBucket.Skip)
        {
            return;
        }

        if (sidecarBucket == ResidualBucket.Failed && string.IsNullOrWhiteSpace(runtime.Paths.FailedIo))
        {
            // Mirror audio routing behavior: without a failed target, sidecars must go to success.
            sidecarBucket = ResidualBucket.Tagged;
        }

        var sidecarTarget = sidecarBucket == ResidualBucket.Failed ? runtime.Paths.FailedIo : runtime.Paths.SuccessIo;
        if (string.IsNullOrWhiteSpace(sidecarTarget)
            || string.Equals(sidecarTarget, runtime.Paths.RootIo, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var movedSidecar = MoveResidualFile(
            file,
            runtime.Paths.RootIo,
            sidecarTarget,
            runtime.Options.OrganizerOptions,
            runtime.Options.OverwritePolicy,
            runtime.Options.PreferredExtensions);
        if (!string.IsNullOrWhiteSpace(movedSidecar))
        {
            moved[DownloadPathResolver.NormalizeDisplayPath(file)] = movedSidecar;
        }
    }

    private enum ResidualBucket
    {
        Tagged,
        Failed,
        Skip
    }

    private sealed record ConversionPlan(
        bool Enabled,
        string? Format,
        string? Bitrate,
        bool KeepOriginal,
        bool SkipIfSourceMatches,
        string ExtraArgs,
        bool WarnLossyToLossless,
        bool SkipLossyToLossless);

    private static ConversionPlan BuildConversionPlan(
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        FolderDto? folder)
    {
        var enabled = settings.ConvertAfterDownload;
        var format = NormalizeOptional(settings.ConvertFormat) ?? NormalizeOptional(settings.ConvertTo);
        var bitrate = NormalizeOptional(settings.Bitrate);

        if (folder is { ConvertEnabled: true })
        {
            enabled = true;
            format = NormalizeOptional(folder.ConvertFormat) ?? format;
            bitrate = NormalizeOptional(folder.ConvertBitrate) ?? bitrate;
        }

        enabled = enabled && !string.IsNullOrWhiteSpace(format);

        return new ConversionPlan(
            Enabled: enabled,
            Format: format,
            Bitrate: bitrate,
            KeepOriginal: settings.ConvertKeepOriginal,
            SkipIfSourceMatches: settings.ConvertSkipIfSourceMatches,
            ExtraArgs: settings.ConvertExtraArgs ?? string.Empty,
            WarnLossyToLossless: settings.ConvertWarnLossyToLossless,
            SkipLossyToLossless: settings.ConvertSkipLossyToLossless);
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private async Task<string> ApplyDestinationConversionIfNeededAsync(
        string movedPath,
        ConversionPlan conversionPlan,
        CancellationToken cancellationToken)
    {
        if (!conversionPlan.Enabled || string.IsNullOrWhiteSpace(movedPath))
        {
            return movedPath;
        }

        var sourceIo = DownloadPathResolver.ResolveIoPath(movedPath);
        if (string.IsNullOrWhiteSpace(sourceIo) || !IOFile.Exists(sourceIo) || !IsAudioExtension(sourceIo))
        {
            return movedPath;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var conversionService = scope.ServiceProvider.GetRequiredService<FfmpegConversionService>();
            var conversion = await conversionService.ConvertIfNeededAsync(
                sourceIo,
                conversionPlan.Format,
                conversionPlan.Bitrate,
                new ConversionOptions(
                    KeepOriginal: true,
                    SkipIfSourceMatches: conversionPlan.SkipIfSourceMatches,
                    ExtraArgs: conversionPlan.ExtraArgs,
                    WarnLossyToLossless: conversionPlan.WarnLossyToLossless,
                    SkipLossyToLossless: conversionPlan.SkipLossyToLossless),
                cancellationToken);

            if (!TryGetConvertedOutputPath(conversion, movedPath, out var convertedIo))
            {
                return movedPath;
            }
            var cloneResult = _quickTagService.CloneAllTags(
                sourceIo,
                convertedIo,
                enforceLibraryPathCheck: false);
            if (!cloneResult.Success)
            {
                _logger.LogWarning(
                    "Converted tag clone warning for {Source} -> {Output}: {Reason}",
                    sourceIo,
                    convertedIo,
                    cloneResult.Error ?? "unknown");
            }

            TryDeleteOriginalAfterConversion(sourceIo, convertedIo, conversionPlan.KeepOriginal);

            var convertedDisplay = DownloadPathResolver.NormalizeDisplayPath(convertedIo);
            _logger.LogInformation("Destination conversion completed: {Input} -> {Output}", movedPath, convertedDisplay);
            return convertedDisplay;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Destination conversion failed for {Path}", movedPath);
            return movedPath;
        }
    }

    private bool TryGetConvertedOutputPath(ConversionResult conversion, string movedPath, out string convertedIo)
    {
        if (conversion.WasConverted && !string.IsNullOrWhiteSpace(conversion.OutputPath))
        {
            convertedIo = conversion.OutputPath;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(conversion.Error))
        {
            _logger.LogInformation(
                "Destination conversion skipped for {Path}: {Reason}",
                movedPath,
                conversion.Error);
        }

        convertedIo = string.Empty;
        return false;
    }

    private void TryDeleteOriginalAfterConversion(string sourceIo, string convertedIo, bool keepOriginal)
    {
        if (keepOriginal)
        {
            return;
        }

        try
        {
            if (!string.Equals(sourceIo, convertedIo, StringComparison.OrdinalIgnoreCase) && IOFile.Exists(sourceIo))
            {
                IOFile.Delete(sourceIo);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to delete source after destination conversion: {Path}", sourceIo);
        }
    }

    private static HashSet<string> BuildNormalizedPathSet(IReadOnlyCollection<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                result.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                result.Add(path);
            }
        }

        return result;
    }

    private static ResidualBucket GetAudioBucket(
        string file,
        AutoTagOrganizerOptions options,
        bool explicitResultSets,
        IReadOnlySet<string> taggedSet,
        IReadOnlySet<string> failedSet)
    {
        if (explicitResultSets)
        {
            if (taggedSet.Contains(file))
            {
                return ResidualBucket.Tagged;
            }

            if (failedSet.Contains(file))
            {
                return ResidualBucket.Failed;
            }
        }

        if (!options.OnlyMoveWhenTagged)
        {
            return ResidualBucket.Tagged;
        }

        return AutoTagTaggedDateProbe.HasTaggedDate(file) ? ResidualBucket.Tagged : ResidualBucket.Failed;
    }

    private static Dictionary<string, ResidualBucket> BuildSidecarBuckets(
        IReadOnlyDictionary<string, ResidualBucket> audioBuckets,
        ResidualRuntime runtime)
    {
        var sidecarBuckets = new Dictionary<string, ResidualBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in audioBuckets)
        {
            ApplyAudioBucketToSidecarBuckets(pair, sidecarBuckets);
        }

        if (runtime.Classification.ExplicitResultSets)
        {
            ApplyExplicitSidecarBuckets(runtime, sidecarBuckets);
        }

        return sidecarBuckets;
    }

    private static Dictionary<string, ResidualBucket> BuildFolderBuckets(
        IReadOnlyDictionary<string, ResidualBucket> audioBuckets,
        ResidualRuntime runtime)
    {
        var folderBuckets = new Dictionary<string, ResidualBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in audioBuckets)
        {
            ApplyAudioBucketToFolderBuckets(pair, folderBuckets);
        }

        if (runtime.Classification.ExplicitResultSets)
        {
            ApplyExplicitFolderBuckets(runtime, folderBuckets);
        }

        return folderBuckets;
    }

    private static void ApplyAudioBucketToSidecarBuckets(
        KeyValuePair<string, ResidualBucket> pair,
        Dictionary<string, ResidualBucket> sidecarBuckets)
    {
        var key = BuildSidecarKey(pair.Key);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (pair.Value == ResidualBucket.Tagged)
        {
            sidecarBuckets[key] = ResidualBucket.Tagged;
            return;
        }

        if (pair.Value == ResidualBucket.Failed && !sidecarBuckets.ContainsKey(key))
        {
            sidecarBuckets[key] = ResidualBucket.Failed;
        }
    }

    private static void ApplyExplicitSidecarBuckets(
        ResidualRuntime runtime,
        Dictionary<string, ResidualBucket> sidecarBuckets)
    {
        foreach (var file in runtime.Classification.TaggedSet)
        {
            if (!IsEligibleExplicitAudioFile(file, runtime.Paths.RootIo))
            {
                continue;
            }

            var key = BuildSidecarKey(file);
            if (!string.IsNullOrWhiteSpace(key))
            {
                sidecarBuckets[key] = ResidualBucket.Tagged;
            }
        }

        foreach (var file in runtime.Classification.FailedSet)
        {
            if (!IsEligibleExplicitAudioFile(file, runtime.Paths.RootIo))
            {
                continue;
            }

            var key = BuildSidecarKey(file);
            if (string.IsNullOrWhiteSpace(key) || sidecarBuckets.ContainsKey(key))
            {
                continue;
            }

            sidecarBuckets[key] = ResidualBucket.Failed;
        }
    }

    private static void ApplyAudioBucketToFolderBuckets(
        KeyValuePair<string, ResidualBucket> pair,
        Dictionary<string, ResidualBucket> folderBuckets)
    {
        var directory = Path.GetDirectoryName(pair.Key);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        if (pair.Value == ResidualBucket.Tagged)
        {
            folderBuckets[directory] = ResidualBucket.Tagged;
            return;
        }

        if (pair.Value == ResidualBucket.Failed && !folderBuckets.ContainsKey(directory))
        {
            folderBuckets[directory] = ResidualBucket.Failed;
        }
    }

    private static void ApplyExplicitFolderBuckets(
        ResidualRuntime runtime,
        Dictionary<string, ResidualBucket> folderBuckets)
    {
        foreach (var file in runtime.Classification.TaggedSet)
        {
            if (!TryGetExplicitAudioDirectory(file, runtime.Paths.RootIo, out var directory))
            {
                continue;
            }

            folderBuckets[directory] = ResidualBucket.Tagged;
        }

        foreach (var file in runtime.Classification.FailedSet)
        {
            if (!TryGetExplicitAudioDirectory(file, runtime.Paths.RootIo, out var directory)
                || folderBuckets.ContainsKey(directory))
            {
                continue;
            }

            folderBuckets[directory] = ResidualBucket.Failed;
        }
    }

    private static bool IsEligibleExplicitAudioFile(string file, string rootIo)
    {
        return IsAudioExtension(file) && IsUnderRoot(rootIo, file);
    }

    private static bool TryGetExplicitAudioDirectory(string file, string rootIo, out string directory)
    {
        directory = string.Empty;
        if (!IsEligibleExplicitAudioFile(file, rootIo))
        {
            return false;
        }

        var candidate = Path.GetDirectoryName(file);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        directory = candidate;
        return true;
    }

    private static ResidualBucket ResolveSidecarBucket(
        string file,
        IReadOnlyDictionary<string, ResidualBucket> sidecarBuckets,
        IReadOnlyDictionary<string, ResidualBucket> folderBuckets,
        string rootIo)
    {
        var key = BuildSidecarKey(file);
        if (string.IsNullOrWhiteSpace(key))
        {
            return ResidualBucket.Skip;
        }

        if (sidecarBuckets.TryGetValue(key, out var bucket))
        {
            return bucket;
        }

        if (!IsDirectorySharedSidecar(file))
        {
            return ResolveArtistArtworkBucket(file, folderBuckets, rootIo);
        }

        var directory = Path.GetDirectoryName(file);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return ResidualBucket.Skip;
        }

        if (folderBuckets.TryGetValue(directory, out var folderBucket))
        {
            return folderBucket;
        }

        return ResolveArtistArtworkBucket(file, folderBuckets, rootIo);
    }

    private static ResidualBucket ResolveArtistArtworkBucket(
        string file,
        IReadOnlyDictionary<string, ResidualBucket> folderBuckets,
        string rootIo)
    {
        if (!IsArtistArtworkFile(file, rootIo))
        {
            return ResidualBucket.Skip;
        }

        var artistDirectory = Path.GetDirectoryName(file);
        if (string.IsNullOrWhiteSpace(artistDirectory))
        {
            return ResidualBucket.Skip;
        }

        if (folderBuckets.TryGetValue(artistDirectory, out var directBucket))
        {
            return directBucket;
        }

        var hasTaggedDescendant = false;
        var hasFailedDescendant = false;
        var prefix = artistDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (var pair in folderBuckets)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pair.Value == ResidualBucket.Tagged)
            {
                hasTaggedDescendant = true;
                break;
            }

            if (pair.Value == ResidualBucket.Failed)
            {
                hasFailedDescendant = true;
            }
        }

        if (hasTaggedDescendant)
        {
            return ResidualBucket.Tagged;
        }

        if (hasFailedDescendant)
        {
            return ResidualBucket.Failed;
        }

        if (folderBuckets.Values.Any(bucket => bucket == ResidualBucket.Tagged))
        {
            return ResidualBucket.Tagged;
        }

        if (folderBuckets.Values.Any(bucket => bucket == ResidualBucket.Failed))
        {
            return ResidualBucket.Failed;
        }

        // Do not strand artist artwork in staging when there is no local bucket evidence left.
        return ResidualBucket.Tagged;
    }

    private static bool IsDirectorySharedSidecar(string file)
    {
        var extension = Path.GetExtension(file);
        if (!DirectoryArtworkSidecarExtensions.Contains(extension))
        {
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(file);
        return DirectoryArtworkSidecarNames.Contains(baseName);
    }

    private static bool IsArtistArtworkFile(string file, string rootIo)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(rootIo))
        {
            return false;
        }

        var extension = Path.GetExtension(file);
        if (!DirectoryArtworkSidecarExtensions.Contains(extension))
        {
            return false;
        }

        var artistDirectory = Path.GetDirectoryName(file);
        if (string.IsNullOrWhiteSpace(artistDirectory) || !IsUnderRoot(rootIo, artistDirectory))
        {
            return false;
        }

        if (!TryGetRelativePathUnderRoot(rootIo, file, out var relativePath))
        {
            return false;
        }

        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            // Ignore root-level images; artist artwork should be inside an artist folder.
            return false;
        }

        return true;
    }

    private static bool TryResolveArtistArtworkConflict(
        string sourcePath,
        string destinationPath,
        string rootIo,
        string destinationRoot,
        out (bool IsHandled, string? PathResult, string DestinationPath) result)
    {
        result = (false, null, destinationPath);
        if (!IOFile.Exists(destinationPath) || !IsArtistArtworkFile(sourcePath, rootIo))
        {
            return false;
        }

        var sourceHash = TryComputeSha256(sourcePath);
        var destinationHash = TryComputeSha256(destinationPath);
        if (string.IsNullOrWhiteSpace(sourceHash) || string.IsNullOrWhiteSpace(destinationHash))
        {
            return false;
        }

        if (string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteDuplicateSource(sourcePath);
            result = (true, DownloadPathResolver.NormalizeDisplayPath(destinationPath), destinationPath);
            return true;
        }

        result = (false, null, GetUniqueDestinationPath(destinationRoot, destinationPath));
        return true;
    }

    private static string? TryComputeSha256(string filePath)
    {
        try
        {
            using var stream = IOFile.OpenRead(filePath);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string? MoveResidualFile(
        string sourcePath,
        string rootIo,
        string destinationRoot,
        AutoTagOrganizerOptions options,
        string overwritePolicy,
        IReadOnlyList<string> preferredExtensions)
    {
        if (!TryResolveResidualDestination(sourcePath, rootIo, destinationRoot, options, out var destinationPath, out var destinationDir))
        {
            return null;
        }

        if (IOFile.Exists(destinationPath))
        {
            var existingResult = HandleExistingResidualDestination(
                sourcePath,
                destinationPath,
                rootIo,
                destinationDir ?? destinationRoot,
                overwritePolicy,
                preferredExtensions);
            if (existingResult.IsHandled)
            {
                return existingResult.PathResult;
            }

            destinationPath = existingResult.DestinationPath;
        }

        MoveFileWithFallback(sourcePath, destinationPath);

        return DownloadPathResolver.NormalizeDisplayPath(destinationPath);
    }

    private static bool TryResolveResidualDestination(
        string sourcePath,
        string rootIo,
        string destinationRoot,
        AutoTagOrganizerOptions options,
        out string destinationPath,
        out string? destinationDir)
    {
        destinationPath = string.Empty;
        destinationDir = null;
        if (!IOFile.Exists(sourcePath)
            || !IsUnderRoot(rootIo, sourcePath)
            || !TryGetRelativePathUnderRoot(rootIo, sourcePath, out var relative)
            || IsUnderRoot(destinationRoot, sourcePath)
            || options.DryRun)
        {
            return false;
        }

        relative = StripLeadingKnownQualityBucket(relative);
        if (string.IsNullOrWhiteSpace(relative))
        {
            return false;
        }

        destinationPath = Path.Join(destinationRoot, relative);
        destinationDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        return true;
    }

    private static (bool IsHandled, string? PathResult, string DestinationPath) HandleExistingResidualDestination(
        string sourcePath,
        string destinationPath,
        string rootIo,
        string destinationRoot,
        string overwritePolicy,
        IReadOnlyList<string> preferredExtensions)
    {
        if (TryResolveArtistArtworkConflict(sourcePath, destinationPath, rootIo, destinationRoot, out var artistResult))
        {
            return artistResult;
        }

        if (!string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase)
            && AudioCollisionDedupe.IsDuplicate(destinationPath, sourcePath))
        {
            if (AudioCollisionDedupe.ShouldPreferIncoming(destinationPath, sourcePath))
            {
                if (!ReplaceDuplicateDestination(sourcePath, destinationPath))
                {
                    TryDeleteDuplicateSource(sourcePath);
                }
            }
            else
            {
                TryDeleteDuplicateSource(sourcePath);
            }

            return (true, DownloadPathResolver.NormalizeDisplayPath(destinationPath), destinationPath);
        }

        if (preferredExtensions.Count > 0
            && PreferredExtensionComparer.ShouldSkipForPreferredExtension(sourcePath, destinationPath, preferredExtensions))
        {
            return (true, null, destinationPath);
        }

        if (overwritePolicy == "b")
        {
            return (false, null, GetUniqueDestinationPath(destinationRoot, destinationPath));
        }

        if (overwritePolicy == "y" || overwritePolicy == "t")
        {
            return (false, null, destinationPath);
        }

        try
        {
            IOFile.Delete(sourcePath);
            return (true, DownloadPathResolver.NormalizeDisplayPath(destinationPath), destinationPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (true, null, destinationPath);
        }
    }

    private static void MoveFileWithFallback(string sourcePath, string destinationPath)
    {
        try
        {
            IOFile.Move(sourcePath, destinationPath, overwrite: true);
        }
        catch (IOException)
        {
            IOFile.Copy(sourcePath, destinationPath, overwrite: true);
            IOFile.Delete(sourcePath);
        }
    }

    private static bool IsAudioExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4b", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".opus", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aac", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<long, FolderDto>> LoadFoldersByIdAsync(CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return new Dictionary<long, FolderDto>();
        }

        var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
        return folders.ToDictionary(folder => folder.Id, folder => folder);
    }

    private static long? ResolveResidualDestinationFolderId(
        IReadOnlyList<DownloadQueueItem> items,
        string rootPath)
    {
        var destinationIds = new HashSet<long>();
        foreach (var item in items)
        {
            if (!IsCompletedStatus(item.Status))
            {
                continue;
            }

            if (!PayloadMentionsRoot(item.PayloadJson, rootPath))
            {
                continue;
            }

            var resolvedDestinationId = item.DestinationFolderId ?? TryReadDestinationFolderId(item.PayloadJson);
            if (resolvedDestinationId.HasValue && resolvedDestinationId.Value > 0)
            {
                destinationIds.Add(resolvedDestinationId.Value);
            }
        }

        return destinationIds.Count == 1 ? destinationIds.First() : null;
    }

    private static long? TryReadDestinationFolderId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return ReadInt64Property(document.RootElement, "destinationFolderId");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }

    private static bool PayloadMentionsRoot(string? payloadJson, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var normalizedRoot = rootPath.Replace('\\', '/').TrimEnd('/');
        var normalizedPayload = payloadJson.Replace('\\', '/');
        return normalizedPayload.Contains(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExistingDirectoryPath(string rootPath)
    {
        var rootIo = DownloadPathResolver.ResolveIoPath(rootPath);
        if (string.IsNullOrWhiteSpace(rootIo))
        {
            return rootPath;
        }

        if (DownloadPathResolver.IsSmbPath(rootIo))
        {
            return rootIo;
        }

        if (Directory.Exists(rootIo))
        {
            return Path.GetFullPath(rootIo);
        }

        try
        {
            var fullPath = Path.GetFullPath(rootIo);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return rootIo;
            }

            var segments = fullPath[root.Length..]
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var segment in segments)
            {
                var baseDir = string.IsNullOrWhiteSpace(current) ? root : current;
                if (!Directory.Exists(baseDir))
                {
                    return rootIo;
                }

                var next = Directory.EnumerateDirectories(baseDir)
                    .FirstOrDefault(dir =>
                        string.Equals(Path.GetFileName(dir), segment, StringComparison.OrdinalIgnoreCase));
                current = next ?? Path.Join(baseDir, segment);
            }

            return Directory.Exists(current) ? current : rootIo;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return rootIo;
        }
    }

    private static bool TryGetRelativePathUnderRoot(string rootPath, string candidatePath, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var rootIo = DownloadPathResolver.ResolveIoPath(rootPath);
        var candidateIo = DownloadPathResolver.ResolveIoPath(candidatePath);
        if (string.IsNullOrWhiteSpace(rootIo) || string.IsNullOrWhiteSpace(candidateIo))
        {
            return false;
        }

        try
        {
            if (!DownloadPathResolver.IsSmbPath(rootIo))
            {
                rootIo = Path.GetFullPath(rootIo);
            }

            if (!DownloadPathResolver.IsSmbPath(candidateIo))
            {
                candidateIo = Path.GetFullPath(candidateIo);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return false;
        }

        var normalizedRoot = rootIo.Replace('\\', '/').TrimEnd('/') + "/";
        var normalizedCandidate = candidateIo.Replace('\\', '/');
        if (normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = normalizedCandidate[normalizedRoot.Length..].TrimStart('/');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return false;
            }

            relative = trimmed.Replace('/', Path.DirectorySeparatorChar);
            return true;
        }

        var fallback = Path.GetRelativePath(rootIo, candidateIo);
        if (fallback.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        relative = fallback;
        return true;
    }

    private static string BuildSidecarKey(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var baseName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(baseName))
        {
            return string.Empty;
        }

        return $"{dir}|{baseName}";
    }

    private static void DeleteEmptyDirectories(string rootPath, IReadOnlySet<string>? protectedDirectories = null)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (IsProtectedDirectory(dir, protectedDirectories))
            {
                continue;
            }

            if (Directory.EnumerateFileSystemEntries(dir).Any())
            {
                continue;
            }

            Directory.Delete(dir, false);
        }
    }

    private static bool IsProtectedDirectory(string path, IReadOnlySet<string>? protectedDirectories)
    {
        if (protectedDirectories is null || protectedDirectories.Count == 0)
        {
            return false;
        }

        return protectedDirectories.Contains(Path.GetFullPath(path));
    }

    private static bool IsQualityBucketDirectoryName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value, "Atmos", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Stereo", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> BuildProtectedQualityBucketDirectories(string rootPath)
    {
        var protectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return protectedDirectories;
        }

        var rootIo = DownloadPathResolver.ResolveIoPath(rootPath);
        if (string.IsNullOrWhiteSpace(rootIo)
            || DownloadPathResolver.IsSmbPath(rootIo)
            || !Directory.Exists(rootIo))
        {
            return protectedDirectories;
        }

        foreach (var bucket in new[] { "Atmos", "Stereo" })
        {
            var bucketPath = Path.Join(rootIo, bucket);
            if (Directory.Exists(bucketPath))
            {
                protectedDirectories.Add(Path.GetFullPath(bucketPath));
            }
        }

        return protectedDirectories;
    }

    private static void AddGenericPayloadSources(
        PayloadSourceMaps maps,
        DownloadQueueItem item,
        string rootPath)
    {
        if (string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectPayloadPaths(rootPath, root, files, roots);

            if (files.Count == 0 && roots.Count == 0)
            {
                return;
            }

            var qualityBucket = NormalizeQualityBucket(ReadStringProperty(root, "qualityBucket"));
            var destinationKey = ResolveDestinationKey(item, root);
            AddPathsToPayloadMaps(maps, item.QueueUuid, destinationKey, qualityBucket, files, roots);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Ignore malformed payloads; move pass should stay best-effort.
        }
    }

    private static long ResolveDestinationKey(DownloadQueueItem item, JsonElement payloadRoot)
    {
        // Queue metadata is authoritative. Payload destination is fallback for legacy rows.
        return item.DestinationFolderId
            ?? ReadInt64Property(payloadRoot, "destinationFolderId")
            ?? 0;
    }

    private static void CollectPayloadPaths(
        string rootPath,
        JsonElement root,
        HashSet<string> files,
        HashSet<string> roots)
    {
        AddFileFromProperty(files, rootPath, root, FilePathProperty);
        AddRootFromProperty(roots, rootPath, root, FilePathProperty);
        AddRootFromProperty(roots, rootPath, root, "albumPath");
        AddRootFromProperty(roots, rootPath, root, "artistPath");
        AddRootFromProperty(roots, rootPath, root, "extrasPath");

        if (TryGetPropertyIgnoreCase(root, FilesProperty, out var filesElement)
            && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileElement in filesElement.EnumerateArray())
            {
                if (fileElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                AddFileFromProperty(files, rootPath, fileElement, "path");
                AddRootFromProperty(roots, rootPath, fileElement, "path");
                AddRootFromProperty(roots, rootPath, fileElement, "albumPath");
                AddRootFromProperty(roots, rootPath, fileElement, "artistPath");
            }
        }

        // Payloads always maintain final destination/source maps after completion.
        // Use the map keys as an additional source list so move pass stays robust
        // even when legacy payloads miss filePath/files arrays.
        CollectFinalDestinationSourcePaths(rootPath, root, files, roots);
    }

    private static void CollectFinalDestinationSourcePaths(
        string rootPath,
        JsonElement root,
        HashSet<string> files,
        HashSet<string> roots)
    {
        if (!TryGetPropertyIgnoreCase(root, "finalDestinations", out var finalDestinationsElement)
            || finalDestinationsElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var rawPath in finalDestinationsElement.EnumerateObject().Select(pathEntry => pathEntry.Name))
        {
            AddFileFromRawPath(files, rootPath, rawPath);
            AddRootFromRawPath(roots, rootPath, rawPath);
        }
    }

    private static void AddPathsToPayloadMaps(
        PayloadSourceMaps maps,
        string? queueUuid,
        long destinationKey,
        string? qualityBucket,
        HashSet<string> files,
        HashSet<string> roots)
    {
        AddPathsByDestination(
            maps.FilesByDestination,
            maps.FileBucketsByDestination,
            maps.FileOwnersByDestination,
            destinationKey,
            qualityBucket,
            queueUuid,
            files);

        AddPathsByDestination(
            maps.RootsByDestination,
            maps.RootBucketsByDestination,
            maps.RootOwnersByDestination,
            destinationKey,
            qualityBucket,
            queueUuid,
            roots);
    }

    private static void AddPathsByDestination(
        Dictionary<long, HashSet<string>> pathsByDestination,
        Dictionary<long, Dictionary<string, string?>> bucketsByDestination,
        Dictionary<long, Dictionary<string, HashSet<string>>> ownersByDestination,
        long destinationKey,
        string? qualityBucket,
        string? queueUuid,
        IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (!pathsByDestination.TryGetValue(destinationKey, out var existingPaths))
        {
            existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pathsByDestination[destinationKey] = existingPaths;
        }

        foreach (var path in paths)
        {
            existingPaths.Add(path);
            RememberPathBucket(bucketsByDestination, destinationKey, path, qualityBucket);
            RememberPathOwner(ownersByDestination, destinationKey, path, queueUuid);
        }
    }

    private static void AddFileFromProperty(
        HashSet<string> files,
        string rootPath,
        JsonElement source,
        string propertyName)
    {
        AddFileFromRawPath(files, rootPath, ReadStringProperty(source, propertyName));
    }

    private static void AddFileFromRawPath(
        HashSet<string> files,
        string rootPath,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!IsUnderRoot(rootPath, value))
        {
            return;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(value);
        if (string.IsNullOrWhiteSpace(ioPath))
        {
            return;
        }

        var fileName = Path.GetFileName(ioPath);
        if (!Path.HasExtension(fileName) && !IOFile.Exists(ioPath))
        {
            return;
        }

        files.Add(value);
    }

    private static void AddRootFromProperty(
        HashSet<string> roots,
        string rootPath,
        JsonElement source,
        string propertyName)
    {
        AddRootFromRawPath(roots, rootPath, ReadStringProperty(source, propertyName));
    }

    private static void AddRootFromRawPath(
        HashSet<string> roots,
        string rootPath,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!IsUnderRoot(rootPath, value))
        {
            return;
        }

        var candidate = value;
        var ioPath = DownloadPathResolver.ResolveIoPath(value);
        if (!string.IsNullOrWhiteSpace(ioPath) && !Directory.Exists(ioPath))
        {
            var fileName = Path.GetFileName(ioPath);
            if (Path.HasExtension(fileName))
            {
                var parent = Path.GetDirectoryName(value);
                if (!string.IsNullOrWhiteSpace(parent) && IsUnderRoot(rootPath, parent))
                {
                    candidate = parent;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            roots.Add(candidate);
        }
    }

    private static string? MoveFileUnderRoot(
        string stagingRoot,
        string sourcePath,
        string destinationRoot,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string? qualityBucket)
    {
        var moveContext = TryResolveMoveFileContext(
            stagingRoot,
            sourcePath,
            destinationRoot,
            qualityBucket);
        if (moveContext is null)
        {
            return null;
        }

        var sourceIo = moveContext.SourceIo;
        var destinationIo = moveContext.DestinationIo;
        var destinationPath = moveContext.DestinationPath;
        var destinationDir = moveContext.DestinationDir;

        var overwritePolicy = string.IsNullOrWhiteSpace(settings.OverwriteFile) ? "y" : settings.OverwriteFile;
        if (string.Equals(sourceIo, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return DownloadPathResolver.NormalizeDisplayPath(sourceIo);
        }

        var existingResolution = ResolveExistingDestinationPath(
            sourceIo,
            destinationPath,
            stagingRoot,
            destinationDir ?? destinationIo,
            overwritePolicy);
        if (existingResolution.IsHandled)
        {
            return existingResolution.PathResult;
        }
        destinationPath = existingResolution.DestinationPath;

        MoveFileWithFallback(sourceIo, destinationPath);

        return DownloadPathResolver.NormalizeDisplayPath(destinationPath);
    }

    private static MoveFileContext? TryResolveMoveFileContext(
        string stagingRoot,
        string sourcePath,
        string destinationRoot,
        string? qualityBucket)
    {
        var stagingIo = DownloadPathResolver.ResolveIoPath(stagingRoot);
        var destinationIo = DownloadPathResolver.ResolveIoPath(destinationRoot) ?? string.Empty;
        var sourceIo = DownloadPathResolver.ResolveIoPath(sourcePath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stagingIo)
            || string.IsNullOrWhiteSpace(destinationIo)
            || string.IsNullOrWhiteSpace(sourceIo))
        {
            return null;
        }

        if (!IOFile.Exists(sourceIo) && TryGetRelativePathUnderRoot(stagingIo, sourceIo, out var sourceRelative))
        {
            var fallbackPath = Path.Join(stagingIo, sourceRelative);
            if (IOFile.Exists(fallbackPath))
            {
                sourceIo = fallbackPath;
            }
        }

        if (!IOFile.Exists(sourceIo)
            || DownloadPathResolver.IsSmbPath(stagingIo)
            || DownloadPathResolver.IsSmbPath(destinationIo)
            || !IsUnderRoot(stagingIo, sourceIo))
        {
            return null;
        }

        if (IsUnderRoot(destinationIo, sourceIo))
        {
            return new MoveFileContext(
                sourceIo,
                destinationIo,
                sourceIo,
                Path.GetDirectoryName(sourceIo));
        }

        if (!TryGetRelativePathUnderRoot(stagingIo, sourceIo, out var relative))
        {
            return null;
        }

        relative = StripLeadingQualityBucket(relative, qualityBucket);
        relative = StripLeadingKnownQualityBucket(relative);
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        var destinationPath = Path.Join(destinationIo, relative);
        var destinationDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        return new MoveFileContext(
            sourceIo,
            destinationIo,
            destinationPath,
            destinationDir);
    }

    private static (bool IsHandled, string? PathResult, string DestinationPath) ResolveExistingDestinationPath(
        string sourcePath,
        string destinationPath,
        string stagingRoot,
        string destinationRoot,
        string overwritePolicy)
    {
        if (!IOFile.Exists(destinationPath))
        {
            return (false, null, destinationPath);
        }

        if (TryResolveArtistArtworkConflict(sourcePath, destinationPath, stagingRoot, destinationRoot, out var artistResult))
        {
            return artistResult;
        }

        if (!string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase)
            && AudioCollisionDedupe.IsDuplicate(destinationPath, sourcePath))
        {
            if (AudioCollisionDedupe.ShouldPreferIncoming(destinationPath, sourcePath))
            {
                if (!ReplaceDuplicateDestination(sourcePath, destinationPath))
                {
                    TryDeleteDuplicateSource(sourcePath);
                }
            }
            else
            {
                TryDeleteDuplicateSource(sourcePath);
            }

            return (true, DownloadPathResolver.NormalizeDisplayPath(destinationPath), destinationPath);
        }

        if (overwritePolicy == "b")
        {
            return (false, null, GetUniqueDestinationPath(destinationRoot, destinationPath));
        }

        if (overwritePolicy == "y" || overwritePolicy == "t")
        {
            return (false, null, destinationPath);
        }

        IOFile.Delete(sourcePath);
        return (true, DownloadPathResolver.NormalizeDisplayPath(destinationPath), destinationPath);
    }

    private static string? ReadStringProperty(JsonElement source, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static long? ReadInt64Property(JsonElement source, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement source, string propertyName, out JsonElement value)
    {
        if (source.ValueKind == JsonValueKind.Object)
        {
            var property = source.EnumerateObject().FirstOrDefault(property =>
                string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
            if (property.Name != null)
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static Dictionary<string, string> MoveDirectoryTreeUnderRoot(
        string stagingRoot,
        string moveRoot,
        string destinationRoot,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string? qualityBucket)
    {
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stagingIo = DownloadPathResolver.ResolveIoPath(stagingRoot);
        var destinationIo = DownloadPathResolver.ResolveIoPath(destinationRoot);
        var moveRootIo = DownloadPathResolver.ResolveIoPath(moveRoot);
        if (string.IsNullOrWhiteSpace(stagingIo) || string.IsNullOrWhiteSpace(destinationIo) || string.IsNullOrWhiteSpace(moveRootIo))
        {
            return moved;
        }

        if (!Directory.Exists(moveRootIo))
        {
            return moved;
        }

        if (DownloadPathResolver.IsSmbPath(stagingIo) || DownloadPathResolver.IsSmbPath(destinationIo))
        {
            return moved;
        }

        if (string.Equals(stagingIo, destinationIo, StringComparison.OrdinalIgnoreCase))
        {
            return moved;
        }

        foreach (var file in Directory.EnumerateFiles(moveRootIo, "*", SearchOption.AllDirectories))
        {
            var sourceDisplay = DownloadPathResolver.NormalizeDisplayPath(file);
            var movedPath = MoveFileUnderRoot(stagingIo, file, destinationIo, settings, qualityBucket);
            if (!string.IsNullOrWhiteSpace(movedPath))
            {
                moved[sourceDisplay] = movedPath;
            }
        }

        if (Directory.Exists(moveRootIo))
        {
            var protectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (TryShouldPreserveMoveRoot(stagingIo, moveRootIo))
            {
                protectedDirectories.Add(Path.GetFullPath(moveRootIo));
            }

            DeleteEmptyDirectories(moveRootIo, protectedDirectories);
            if (!Directory.EnumerateFileSystemEntries(moveRootIo).Any()
                && !IsProtectedDirectory(moveRootIo, protectedDirectories))
            {
                Directory.Delete(moveRootIo, false);
            }
        }

        return moved;
    }

    private static string GetUniqueDestinationPath(string destinationDir, string destinationPath)
    {
        var filename = Path.GetFileNameWithoutExtension(destinationPath);
        var extension = Path.GetExtension(destinationPath);
        var uniqueFilename = DownloadUtils.GenerateUniqueFilename(destinationDir, filename, extension);
        return Path.Join(destinationDir, uniqueFilename + extension);
    }

    private static void TryDeleteDuplicateSource(string sourcePath)
    {
        try
        {
            if (IOFile.Exists(sourcePath))
            {
                IOFile.Delete(sourcePath);
            }
        }
        catch (IOException ex)
        {
            _ = ex.HResult;
        }
        catch (UnauthorizedAccessException ex)
        {
            _ = ex.HResult;
        }
        catch (ArgumentException ex)
        {
            _ = ex.HResult;
        }
        catch (NotSupportedException ex)
        {
            _ = ex.HResult;
        }
    }

    private static bool ReplaceDuplicateDestination(string sourcePath, string destinationPath)
    {
        try
        {
            IOFile.Move(sourcePath, destinationPath, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            try
            {
                IOFile.Copy(sourcePath, destinationPath, overwrite: true);
                IOFile.Delete(sourcePath);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<string?> ResolveDestinationRootAsync(long? destinationFolderId, CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return null;
        }

        var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
        if (destinationFolderId.HasValue)
        {
            var explicitFolder = folders.FirstOrDefault(folder => folder.Id == destinationFolderId.Value);
            if (explicitFolder != null && explicitFolder.Enabled)
            {
                return explicitFolder.RootPath;
            }
        }

        return null;
    }

    private static bool IsCompletedStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeQualityBucket(string? qualityBucket)
    {
        var normalized = qualityBucket?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "atmos" => "atmos",
            "stereo" => "stereo",
            _ => null
        };
    }

    private static void RememberPathBucket(
        Dictionary<long, Dictionary<string, string?>> bucketsByDestination,
        long destinationKey,
        string path,
        string? qualityBucket)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!bucketsByDestination.TryGetValue(destinationKey, out var byPath))
        {
            byPath = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            bucketsByDestination[destinationKey] = byPath;
        }

        if (!byPath.TryGetValue(path, out var existing) || string.IsNullOrWhiteSpace(existing))
        {
            byPath[path] = qualityBucket;
        }
    }

    private static string? TryResolveBucket(
        Dictionary<long, Dictionary<string, string?>> bucketsByDestination,
        long destinationKey,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!bucketsByDestination.TryGetValue(destinationKey, out var byPath))
        {
            return null;
        }

        return byPath.TryGetValue(path, out var qualityBucket)
            ? qualityBucket
            : null;
    }

    private static void RememberPathOwner(
        Dictionary<long, Dictionary<string, HashSet<string>>> ownersByDestination,
        long destinationKey,
        string path,
        string? queueUuid)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        if (!ownersByDestination.TryGetValue(destinationKey, out var byPath))
        {
            byPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            ownersByDestination[destinationKey] = byPath;
        }

        if (!byPath.TryGetValue(path, out var owners))
        {
            owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            byPath[path] = owners;
        }

        owners.Add(queueUuid);
    }

    private static IReadOnlyCollection<string> ResolvePathOwners(
        Dictionary<long, Dictionary<string, HashSet<string>>> ownersByDestination,
        long destinationKey,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !ownersByDestination.TryGetValue(destinationKey, out var byPath)
            || !byPath.TryGetValue(path, out var owners))
        {
            return Array.Empty<string>();
        }

        return owners;
    }

    private static void RememberTransitionsForOwners(
        Dictionary<string, Dictionary<string, string>> transitionsByQueue,
        IReadOnlyCollection<string> owners,
        string sourcePath,
        string destinationPath)
    {
        if (owners.Count == 0)
        {
            return;
        }

        foreach (var queueUuid in owners)
        {
            if (!transitionsByQueue.TryGetValue(queueUuid, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                transitionsByQueue[queueUuid] = map;
            }

            FinalDestinationTracker.RecordPathTransition(map, sourcePath, destinationPath);
        }
    }

    private async Task PersistFinalDestinationsAsync(
        IReadOnlyList<DownloadQueueItem> items,
        Dictionary<string, Dictionary<string, string>> transitionsByQueue,
        CancellationToken cancellationToken)
    {
        if (transitionsByQueue.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.QueueUuid)
                || string.IsNullOrWhiteSpace(item.PayloadJson)
                || !transitionsByQueue.TryGetValue(item.QueueUuid, out var transitions)
                || transitions.Count == 0)
            {
                continue;
            }

            if (!TryApplyFinalDestinationTransitions(item.PayloadJson, transitions, out var payloadJson, out var finalDestinationsJson))
            {
                continue;
            }

            await _queueRepository.UpdateFinalDestinationsAsync(
                item.QueueUuid,
                finalDestinationsJson,
                payloadJson,
                cancellationToken);
        }
    }

    private async Task PersistFinalDestinationsByPayloadLookupAsync(
        IReadOnlyList<DownloadQueueItem> items,
        IReadOnlyDictionary<string, string> transitions,
        CancellationToken cancellationToken)
    {
        if (transitions.Count == 0)
        {
            return;
        }

        var transitionsByQueue = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.QueueUuid)
                || !IsCompletedStatus(item.Status)
                || string.IsNullOrWhiteSpace(item.PayloadJson))
            {
                continue;
            }

            var trackedPaths = ReadTrackedPayloadPaths(item.PayloadJson);
            if (trackedPaths.Count == 0)
            {
                continue;
            }

            var trackedNames = trackedPaths
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var transition in transitions)
            {
                if (!IsTrackedTransitionMatch(trackedPaths, trackedNames, transition))
                {
                    continue;
                }

                var perQueue = GetOrCreateQueueTransitions(transitionsByQueue, item.QueueUuid);
                FinalDestinationTracker.RecordPathTransition(perQueue, transition.Key, transition.Value);
            }
        }

        await PersistFinalDestinationsAsync(items, transitionsByQueue, cancellationToken);
    }

    private static bool IsTrackedTransitionMatch(
        HashSet<string> trackedPaths,
        HashSet<string> trackedNames,
        KeyValuePair<string, string> transition)
    {
        var sourcePath = DownloadPathResolver.NormalizeDisplayPath(transition.Key);
        var destinationPath = DownloadPathResolver.NormalizeDisplayPath(transition.Value);
        var sourceName = Path.GetFileName(sourcePath);
        var destinationName = Path.GetFileName(destinationPath);
        return trackedPaths.Contains(sourcePath)
            || trackedPaths.Contains(destinationPath)
            || (!string.IsNullOrWhiteSpace(sourceName) && trackedNames.Contains(sourceName))
            || (!string.IsNullOrWhiteSpace(destinationName) && trackedNames.Contains(destinationName));
    }

    private static Dictionary<string, string> GetOrCreateQueueTransitions(
        Dictionary<string, Dictionary<string, string>> transitionsByQueue,
        string queueUuid)
    {
        if (transitionsByQueue.TryGetValue(queueUuid, out var existing))
        {
            return existing;
        }

        var created = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        transitionsByQueue[queueUuid] = created;
        return created;
    }

    private static HashSet<string> ReadTrackedPayloadPaths(string payloadJson)
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return tracked;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return tracked;
            }

            var filePath = ReadStringProperty(root, FilePathProperty);
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                tracked.Add(DownloadPathResolver.NormalizeDisplayPath(filePath));
            }

            if (!TryGetPropertyIgnoreCase(root, FilesProperty, out var filesElement)
                || filesElement.ValueKind != JsonValueKind.Array)
            {
                return tracked;
            }

            foreach (var fileElement in filesElement.EnumerateArray())
            {
                if (fileElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var path = ReadStringProperty(fileElement, "path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                tracked.Add(DownloadPathResolver.NormalizeDisplayPath(path));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // ignore malformed payload
        }

        return tracked;
    }

    private static bool TryApplyFinalDestinationTransitions(
        string payloadJson,
        IReadOnlyDictionary<string, string> transitions,
        out string updatedPayloadJson,
        out string? finalDestinationsJson)
    {
        updatedPayloadJson = payloadJson;
        finalDestinationsJson = null;
        if (string.IsNullOrWhiteSpace(payloadJson) || transitions.Count == 0)
        {
            return false;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(payloadJson) as JsonObject;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return false;
        }

        if (root == null)
        {
            return false;
        }

        var finalMap = ReadFinalDestinations(root);
        SeedFinalDestinationsFromPayload(root, finalMap);

        foreach (var transition in transitions)
        {
            FinalDestinationTracker.RecordPathTransition(finalMap, transition.Key, transition.Value);
        }

        var changed = false;
        if (TryRewriteStringProperty(root, FilePathProperty, transitions))
        {
            changed = true;
        }

        if (root[FilesProperty] is JsonArray files)
        {
            foreach (var node in files.OfType<JsonObject>())
            {
                changed |= TryRewriteStringProperty(node, "path", transitions);
            }
        }

        finalDestinationsJson = FinalDestinationTracker.Serialize(finalMap);
        root["finalDestinations"] = BuildFinalDestinationsNode(finalMap);
        updatedPayloadJson = root.ToJsonString();
        return changed || !string.IsNullOrWhiteSpace(finalDestinationsJson);
    }

    private static Dictionary<string, string> ReadFinalDestinations(JsonObject root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root["finalDestinations"] is not JsonObject existing)
        {
            return map;
        }

        foreach (var property in existing)
        {
            var key = property.Key;
            var value = TryReadJsonString(property.Value);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            map[key] = value;
        }

        return map;
    }

    private static void SeedFinalDestinationsFromPayload(JsonObject root, Dictionary<string, string> finalMap)
    {
        if (root.TryGetPropertyValue(FilePathProperty, out var filePathNode))
        {
            var filePath = TryReadJsonString(filePathNode);
            FinalDestinationTracker.RecordPathTransition(finalMap, filePath, filePath);
        }

        if (root[FilesProperty] is not JsonArray files)
        {
            return;
        }

        foreach (var node in files.OfType<JsonObject>())
        {
            if (!node.TryGetPropertyValue("path", out var pathNode))
            {
                continue;
            }

            var path = TryReadJsonString(pathNode);
            FinalDestinationTracker.RecordPathTransition(finalMap, path, path);
        }
    }

    private static JsonObject BuildFinalDestinationsNode(IReadOnlyDictionary<string, string> map)
    {
        var node = new JsonObject();
        foreach (var pair in map)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            node[pair.Key] = pair.Value;
        }

        return node;
    }

    private static bool TryRewriteStringProperty(
        JsonObject obj,
        string propertyName,
        IReadOnlyDictionary<string, string> transitions)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return false;
        }

        var current = TryReadJsonString(node);
        if (string.IsNullOrWhiteSpace(current))
        {
            return false;
        }

        var rewritten = RewritePath(current, transitions);
        if (string.IsNullOrWhiteSpace(rewritten)
            || string.Equals(current, rewritten, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        obj[propertyName] = rewritten;
        return true;
    }

    private static string? RewritePath(string? path, IReadOnlyDictionary<string, string> transitions)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var normalized = DownloadPathResolver.NormalizeDisplayPath(path);
        if (transitions.TryGetValue(normalized, out var mapped))
        {
            return DownloadPathResolver.NormalizeDisplayPath(mapped);
        }

        foreach (var pair in transitions)
        {
            var source = DownloadPathResolver.NormalizeDisplayPath(pair.Key);
            if (string.Equals(source, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return DownloadPathResolver.NormalizeDisplayPath(pair.Value);
            }
        }

        return normalized;
    }

    private static string? TryReadJsonString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue valueNode && valueNode.TryGetValue<string>(out var value))
        {
            return value;
        }

        return node.ToJsonString().Trim('"');
    }

    private static string StripLeadingQualityBucket(string relativePath, string? qualityBucket)
    {
        var normalizedBucket = NormalizeQualityBucket(qualityBucket);
        if (string.IsNullOrWhiteSpace(normalizedBucket))
        {
            return relativePath;
        }

        var expectedSegment = string.Equals(normalizedBucket, "atmos", StringComparison.OrdinalIgnoreCase)
            ? "Atmos"
            : "Stereo";
        var parts = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return relativePath;
        }

        if (!string.Equals(parts[0], expectedSegment, StringComparison.OrdinalIgnoreCase))
        {
            var bucketIndex = Array.FindIndex(
                parts,
                part => string.Equals(part, expectedSegment, StringComparison.OrdinalIgnoreCase));
            if (bucketIndex <= 0)
            {
                return relativePath;
            }

            if (bucketIndex >= parts.Length - 1)
            {
                return string.Empty;
            }

            return Path.Join(parts.Skip(bucketIndex + 1).ToArray());
        }

        if (parts.Length == 1)
        {
            return string.Empty;
        }

        return Path.Join(parts.Skip(1).ToArray());
    }

    private static string StripLeadingKnownQualityBucket(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return relativePath;
        }

        var parts = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            return relativePath;
        }

        if (!IsQualityBucketDirectoryName(parts[0]))
        {
            return relativePath;
        }

        return Path.Join(parts.Skip(1).ToArray());
    }

    private static bool TryShouldPreserveMoveRoot(string stagingIo, string moveRootIo)
    {
        var normalizedStaging = Path.GetFullPath(stagingIo);
        var normalizedMoveRoot = Path.GetFullPath(moveRootIo);
        var parent = Path.GetDirectoryName(normalizedMoveRoot);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        if (!string.Equals(Path.GetFullPath(parent), normalizedStaging, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsQualityBucketDirectoryName(Path.GetFileName(normalizedMoveRoot));
    }
}
