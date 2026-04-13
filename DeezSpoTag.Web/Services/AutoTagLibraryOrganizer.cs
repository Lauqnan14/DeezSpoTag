using System.Linq;
using System.Text.RegularExpressions;
using System.Globalization;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using TagLib;
using IOFile = System.IO.File;

namespace DeezSpoTag.Web.Services;

public class AutoTagLibraryOrganizer
{
    public sealed class AutoTagOrganizerReport
    {
        public int CandidateFiles { get; set; }
        public int PlannedMoves { get; set; }
        public int MovedFolders { get; set; }
        public int MovedFiles { get; set; }
        public int MovedSidecars { get; set; }
        public int MovedLeftovers { get; set; }
        public int ReplacedDuplicates { get; set; }
        public int QuarantinedDuplicates { get; set; }
        public int KeptLowerQualityDuplicates { get; set; }
        public int PreservedExistingArtwork { get; set; }
        public int MergedLyricsSidecars { get; set; }
        public int SkippedUntagged { get; set; }
        public int SkippedCompilationFolders { get; set; }
        public int SkippedVariousArtistsFolders { get; set; }
        public int SkippedIncompleteAlbums { get; set; }
        public int SkippedExistingFolderMerges { get; set; }
        public int SkippedConflicts { get; set; }
        public List<string> Entries { get; } = new();
    }

    private const string MainArtistRole = "Main";
    private const string FeaturedArtistRole = "Featured";
    private const string SinglesAlbumTitle = "Singles";
    private const string DownloadTypeTrack = "track";
    private const string DownloadTypeAlbum = "album";
    private const int PlanProgressLogInterval = 200;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly ILogger<AutoTagLibraryOrganizer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ShazamRecognitionService _shazamRecognitionService;

    private static readonly HashSet<string> VariousArtistsTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "Various Artists", "Various", "VA", "V/A"
    };

    private static readonly Regex LeadingTrackTokenRegex = new(
        @"^\s*(?<num>\d{1,4})\s*[-._)\]]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    public AutoTagLibraryOrganizer(
        ILogger<AutoTagLibraryOrganizer> logger,
        ILoggerFactory loggerFactory,
        DeezSpoTagSettingsService settingsService,
        ShazamRecognitionService shazamRecognitionService)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _settingsService = settingsService;
        _shazamRecognitionService = shazamRecognitionService;
    }

    public Task OrganizeAsync(string rootPath, IReadOnlyCollection<string> filePaths, Action<string>? log = null)
    {
        return OrganizeAsync(rootPath, filePaths, new AutoTagOrganizerOptions(), report: null, log);
    }

    public Task OrganizePathAsync(string rootPath, Action<string>? log = null)
    {
        return OrganizePathAsync(rootPath, new AutoTagOrganizerOptions(), log);
    }

    public Task OrganizePathAsync(string rootPath, AutoTagOrganizerOptions options, Action<string>? log = null)
        => OrganizePathAsync(rootPath, options, report: null, log);

    public Task OrganizePathAsync(string rootPath, AutoTagOrganizerOptions options, AutoTagOrganizerReport? report, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Task.CompletedTask;
        }

        var normalizedRoot = Path.GetFullPath(rootPath);
        var files = EnumerateAudioFiles(normalizedRoot, options.IncludeSubfolders).ToList();
        log?.Invoke($"organizer scan prepared: root={normalizedRoot}, includeSubfolders={options.IncludeSubfolders}, candidateFiles={files.Count}");
        report?.Entries.Clear();
        report ??= options.GenerateReconciliationReport ? new AutoTagOrganizerReport() : null;
        if (report != null)
        {
            report.CandidateFiles += files.Count;
        }

        return OrganizeAsync(normalizedRoot, files, options, report, log);
    }

    private Task OrganizeAsync(string rootPath, IReadOnlyCollection<string> filePaths, AutoTagOrganizerOptions options, AutoTagOrganizerReport? report, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Task.CompletedTask;
        }

        var normalizedRoot = Path.GetFullPath(rootPath);
        var settings = _settingsService.LoadSettings();
        settings.DownloadLocation = normalizedRoot;
        if (settings.Tags == null)
        {
            settings.Tags = new TagSettings();
        }

        ApplySettingsOverrides(settings, options);

        if (!string.IsNullOrWhiteSpace(options.MultiArtistSeparatorOverride))
        {
            settings.Tags.MultiArtistSeparator = options.MultiArtistSeparatorOverride!.Trim();
        }

        var usePrimaryArtistFolders = options.UsePrimaryArtistFoldersOverride
            ?? settings.Tags.SingleAlbumArtist;
        var plan = BuildMovePlan(normalizedRoot, filePaths, options, settings, usePrimaryArtistFolders, report, log);
        log?.Invoke($"organizer plan prepared: {plan.Count} move action(s)");
        if (report != null)
        {
            report.PlannedMoves += plan.Count;
        }

        var artistDirectoryTransitions = ExecuteMovePlan(normalizedRoot, plan, options, report, log);
        MoveResidualArtistSidecarsForTransitions(normalizedRoot, artistDirectoryTransitions, options, report, log);
        MergeNoAudioArtistDirectoriesIntoMatchingDestinations(normalizedRoot, options, report, log);
        MoveExistingNoAudioDirectoriesToQuarantine(normalizedRoot, options, report, log);
        ReconcileOrphanCombinedArtistFolders(normalizedRoot, usePrimaryArtistFolders, options, report, log);
        if (!options.DryRun && options.RemoveEmptyFolders)
        {
            CleanupArtistFolders(normalizedRoot, usePrimaryArtistFolders, report, log);
        }
        return Task.CompletedTask;
    }

    private static void ApplySettingsOverrides(DeezSpoTagSettings settings, AutoTagOrganizerOptions options)
    {
        if (options.TechnicalSettingsOverride != null)
        {
            var technical = options.TechnicalSettingsOverride;
            settings.Tags.SavePlaylistAsCompilation = technical.SavePlaylistAsCompilation;
            settings.Tags.UseNullSeparator = technical.UseNullSeparator;
            settings.Tags.SaveID3v1 = technical.SaveID3v1;
            settings.Tags.MultiArtistSeparator = technical.MultiArtistSeparator;
            settings.Tags.SingleAlbumArtist = technical.SingleAlbumArtist;
            settings.Tags.CoverDescriptionUTF8 = technical.CoverDescriptionUTF8;
            settings.AlbumVariousArtists = technical.AlbumVariousArtists;
            settings.RemoveDuplicateArtists = technical.RemoveDuplicateArtists;
            settings.RemoveAlbumVersion = technical.RemoveAlbumVersion;
            settings.DateFormat = technical.DateFormat;
            settings.FeaturedToTitle = technical.FeaturedToTitle;
            settings.TitleCasing = technical.TitleCasing;
            settings.ArtistCasing = technical.ArtistCasing;
        }

        if (options.CreateArtistFolderOverride.HasValue)
        {
            settings.CreateArtistFolder = options.CreateArtistFolderOverride.Value;
        }

        if (!string.IsNullOrWhiteSpace(options.ArtistNameTemplateOverride))
        {
            settings.ArtistNameTemplate = options.ArtistNameTemplateOverride.Trim();
        }

        if (options.CreateAlbumFolderOverride.HasValue)
        {
            settings.CreateAlbumFolder = options.CreateAlbumFolderOverride.Value;
        }

        if (!string.IsNullOrWhiteSpace(options.AlbumNameTemplateOverride))
        {
            settings.AlbumNameTemplate = options.AlbumNameTemplateOverride.Trim();
        }

        if (options.CreateCDFolderOverride.HasValue)
        {
            settings.CreateCDFolder = options.CreateCDFolderOverride.Value;
        }

        if (options.CreateStructurePlaylistOverride.HasValue)
        {
            settings.CreateStructurePlaylist = options.CreateStructurePlaylistOverride.Value;
        }

        if (options.CreateSingleFolderOverride.HasValue)
        {
            settings.CreateSingleFolder = options.CreateSingleFolderOverride.Value;
        }

        if (options.CreatePlaylistFolderOverride.HasValue)
        {
            settings.CreatePlaylistFolder = options.CreatePlaylistFolderOverride.Value;
        }

        if (!string.IsNullOrWhiteSpace(options.PlaylistNameTemplateOverride))
        {
            settings.PlaylistNameTemplate = options.PlaylistNameTemplateOverride.Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.IllegalCharacterReplacerOverride))
        {
            settings.IllegalCharacterReplacer = options.IllegalCharacterReplacerOverride.Trim();
        }
    }

    private sealed class MovePlanItem
    {
        public required string SourcePath { get; set; }
        public required string SourceDir { get; set; }
        public required string DestinationPath { get; set; }
        public required string DestinationDir { get; set; }
        public bool IsUntagged { get; set; }
    }

    private sealed record MovePlanTargetContext(
        string FullPath,
        string RootPath,
        string SourceDir,
        DeezSpoTagSettings Settings,
        bool UsePrimaryArtistFolders,
        AutoTagOrganizerOptions Options,
        Action<string>? Log);

    private sealed record SourceDirectoryPolicy(
        bool ShouldProcess,
        string? Reason);

    private sealed record MovePlanBuildContext(
        string RootPath,
        AutoTagOrganizerOptions Options,
        DeezSpoTagSettings Settings,
        bool UsePrimaryArtistFolders,
        IReadOnlyDictionary<string, SourceDirectoryPolicy> SourcePolicies,
        AutoTagOrganizerReport? Report,
        Action<string>? Log);

    private sealed record SidecarMoveContext(
        string RootPath,
        string SourceDir,
        string DestinationDir,
        string SourcePath,
        string DestinationPath,
        AutoTagOrganizerOptions Options,
        AutoTagOrganizerReport? Report,
        Action<string>? Log);

    private sealed record ArtistDirectoryMatchCandidate(
        string Path,
        string ArtistName,
        string Key,
        bool HasAudio);

    private static Dictionary<string, SourceDirectoryPolicy> BuildSourceDirectoryPolicies(
        string rootPath,
        IReadOnlyCollection<string> filePaths,
        AutoTagOrganizerOptions options)
    {
        var policies = new Dictionary<string, SourceDirectoryPolicy>(StringComparer.OrdinalIgnoreCase);
        var groups = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .GroupBy(path => Path.GetDirectoryName(Path.GetFullPath(path)) ?? rootPath, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var sourceDir = group.Key;
            if (options.SkipVariousArtistsFolders && IsSourceDirectoryVariousArtists(rootPath, sourceDir))
            {
                policies[sourceDir] = new SourceDirectoryPolicy(false, "various_artists");
                continue;
            }

            if (options.SkipCompilationFolders && IsCompilationDirectory(sourceDir))
            {
                policies[sourceDir] = new SourceDirectoryPolicy(false, "compilation");
                continue;
            }

            if (options.OnlyReorganizeAlbumsWithFullTrackSets && !HasFullTrackSet(group))
            {
                policies[sourceDir] = new SourceDirectoryPolicy(false, "incomplete_album");
                continue;
            }

            policies[sourceDir] = new SourceDirectoryPolicy(true, null);
        }

        return policies;
    }

    private static void RegisterSourceDirectorySkip(
        string? reason,
        string sourceDir,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        switch (reason)
        {
            case "various_artists":
                if (report != null)
                {
                    report.SkippedVariousArtistsFolders++;
                }
                log?.Invoke($"organizer skipped Various Artists folder: {sourceDir}");
                report?.Entries.Add($"skip: various artists folder -> {sourceDir}");
                break;
            case "compilation":
                if (report != null)
                {
                    report.SkippedCompilationFolders++;
                }
                log?.Invoke($"organizer skipped compilation folder: {sourceDir}");
                report?.Entries.Add($"skip: compilation folder -> {sourceDir}");
                break;
            case "incomplete_album":
                if (report != null)
                {
                    report.SkippedIncompleteAlbums++;
                }
                log?.Invoke($"organizer skipped incomplete album folder: {sourceDir}");
                report?.Entries.Add($"skip: incomplete album folder -> {sourceDir}");
                break;
        }
    }

    private List<MovePlanItem> BuildMovePlan(
        string rootPath,
        IReadOnlyCollection<string> filePaths,
        AutoTagOrganizerOptions options,
        DeezSpoTagSettings settings,
        bool usePrimaryArtistFolders,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        var results = new List<MovePlanItem>();
        var sourcePolicies = BuildSourceDirectoryPolicies(rootPath, filePaths, options);
        var totalFiles = filePaths.Count;
        var processedFiles = 0;
        foreach (var filePath in filePaths)
        {
            processedFiles++;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                ReportPlanProgress(log, processedFiles, totalFiles);
                continue;
            }

            var context = new MovePlanBuildContext(
                rootPath,
                options,
                settings,
                usePrimaryArtistFolders,
                sourcePolicies,
                report,
                log);
            if (TryBuildMovePlanItem(context, filePath, out var item))
            {
                results.Add(item);
            }

            ReportPlanProgress(log, processedFiles, totalFiles);
        }

        return results;
    }

    private static void ReportPlanProgress(Action<string>? log, int processedFiles, int totalFiles)
    {
        if (log == null || totalFiles <= 0 || processedFiles <= 0)
        {
            return;
        }

        if (processedFiles == 1
            || processedFiles == totalFiles
            || processedFiles % PlanProgressLogInterval == 0)
        {
            log($"organizer planning progress: {processedFiles}/{totalFiles}");
        }
    }

    private bool TryBuildMovePlanItem(
        MovePlanBuildContext context,
        string filePath,
        out MovePlanItem item)
    {
        item = default!;
        try
        {
            var fullPath = ResolveMovePlanSourcePath(filePath, context.RootPath);
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }

            var sourceDir = Path.GetDirectoryName(fullPath) ?? context.RootPath;
            if (context.SourcePolicies.TryGetValue(sourceDir, out var sourcePolicy) && !sourcePolicy.ShouldProcess)
            {
                RegisterSourceDirectorySkip(sourcePolicy.Reason, sourceDir, context.Report, context.Log);
                return false;
            }

            var target = ResolveMovePlanTarget(
                fullPath,
                context.RootPath,
                context.Options,
                context.Settings,
                context.UsePrimaryArtistFolders,
                context.Log);
            if (target is null)
            {
                return false;
            }

            var normalized = ApplyMovePlanOptions(fullPath, target, context.Options);
            if (normalized is null)
            {
                return false;
            }

            if (target.IsUntagged && context.Report != null)
            {
                context.Report.SkippedUntagged++;
            }

            item = normalized;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag organizer failed for {Path}", filePath);
            context.Log?.Invoke($"organizer failed: {filePath} ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    private static string? ResolveMovePlanSourcePath(string filePath, string rootPath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!IsPathUnderRoot(fullPath, rootPath)
            || !IOFile.Exists(fullPath)
            || IsAnimatedArtworkFile(fullPath))
        {
            return null;
        }

        return fullPath;
    }

    private MovePlanItem? ResolveMovePlanTarget(
        string fullPath,
        string rootPath,
        AutoTagOrganizerOptions options,
        DeezSpoTagSettings settings,
        bool usePrimaryArtistFolders,
        Action<string>? log)
    {
        var sourceDir = Path.GetDirectoryName(fullPath) ?? rootPath;
        var extension = Path.GetExtension(fullPath);
        var context = new MovePlanTargetContext(
            fullPath,
            rootPath,
            sourceDir,
            settings,
            usePrimaryArtistFolders,
            options,
            log);
        try
        {
            using var tagFile = TagLib.File.Create(fullPath);
            if (ShouldRouteToUntaggedDestination(tagFile, extension, options))
            {
                return BuildUntaggedMovePlanItem(
                    context.FullPath,
                    context.RootPath,
                    context.SourceDir,
                    context.Options,
                    context.Log,
                    "organizer skipped untagged file");
            }

            return BuildTaggedMovePlanItem(tagFile, context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AutoTag organizer metadata read failed for {Path}; falling back to inferred identity.", fullPath);
            context.Log?.Invoke($"organizer metadata read failed; using fallback inference: {fullPath} ({ex.Message})");
            return BuildUnreadableMetadataMovePlanItem(context);
        }
    }

    private static bool ShouldRouteToUntaggedDestination(
        TagLib.File tagFile,
        string extension,
        AutoTagOrganizerOptions options)
    {
        return options.OnlyMoveWhenTagged && !AutoTagTaggedDateProbe.HasTaggedDate(tagFile, extension);
    }

    private MovePlanItem? BuildTaggedMovePlanItem(
        TagLib.File tagFile,
        MovePlanTargetContext context)
    {
        var tag = tagFile.Tag;
        if (IsMissingCoreTags(tag))
        {
            var missingTagsResult = TryBuildTrackFromMissingCoreTags(context, out var missingTagTrack, out var missingTagDownloadType);
            if (missingTagsResult == MissingCoreTrackBuildResult.Skip)
            {
                return null;
            }

            if (missingTagsResult == MissingCoreTrackBuildResult.RouteToUntagged)
            {
                return BuildUntaggedMovePlanItem(
                    context.FullPath,
                    context.RootPath,
                    context.SourceDir,
                    context.Options,
                    context.Log,
                    "organizer skipped missing core tags");
            }

            return BuildMovePlanItemFromTrack(missingTagTrack, missingTagDownloadType, context);
        }

        var track = BuildTrackFromTag(
            tag,
            context.FullPath,
            context.UsePrimaryArtistFolders,
            context.Settings.Tags?.MultiArtistSeparator);
        var downloadType = string.IsNullOrWhiteSpace(tag.Album) ? DownloadTypeTrack : DownloadTypeAlbum;
        return BuildMovePlanItemFromTrack(track, downloadType, context);
    }

    private enum MissingCoreTrackBuildResult
    {
        Built,
        RouteToUntagged,
        Skip
    }

    private MissingCoreTrackBuildResult TryBuildTrackFromMissingCoreTags(
        MovePlanTargetContext context,
        out Track track,
        out string downloadType)
    {
        track = null!;
        downloadType = string.Empty;

        if (context.Options.UseShazamForUntaggedFiles)
        {
            var shazamTrack = BuildTrackFromShazamRecognition(
                context.FullPath,
                context.UsePrimaryArtistFolders,
                context.Settings.Tags?.MultiArtistSeparator,
                context.Log);
            if (shazamTrack is null)
            {
                context.Log?.Invoke($"organizer left untagged file in place after Shazam produced no usable match: {context.FullPath}");
                return MissingCoreTrackBuildResult.Skip;
            }

            track = shazamTrack;
            downloadType = IsSingleTrackDownload(track) ? DownloadTypeTrack : DownloadTypeAlbum;
            context.Log?.Invoke($"organizer inferred core tags from Shazam: {context.FullPath}");
            return MissingCoreTrackBuildResult.Built;
        }

        var fallbackTrack = BuildTrackFromPathFallback(
            context.FullPath,
            context.RootPath,
            context.UsePrimaryArtistFolders,
            context.Settings.Tags?.MultiArtistSeparator);
        if (fallbackTrack is null)
        {
            return MissingCoreTrackBuildResult.RouteToUntagged;
        }

        track = fallbackTrack;
        downloadType = IsSingleTrackDownload(track) ? DownloadTypeTrack : DownloadTypeAlbum;
        context.Log?.Invoke($"organizer inferred core tags from path: {context.FullPath}");
        return MissingCoreTrackBuildResult.Built;
    }

    private static bool IsSingleTrackDownload(Track track)
    {
        return string.IsNullOrWhiteSpace(track.Album?.Title)
            || string.Equals(track.Album.Title, SinglesAlbumTitle, StringComparison.OrdinalIgnoreCase);
    }

    private MovePlanItem BuildMovePlanItemFromTrack(
        Track track,
        string downloadType,
        MovePlanTargetContext context)
    {
        track.ApplySettings(context.Settings);
        var pathProcessor = new EnhancedPathTemplateProcessor(_loggerFactory.CreateLogger<EnhancedPathTemplateProcessor>());
        var pathResult = pathProcessor.GeneratePaths(track, downloadType, context.Settings);

        var destinationIoDir = DownloadPathResolver.ResolveIoPath(pathResult.FilePath);
        var destinationDir = string.IsNullOrWhiteSpace(destinationIoDir)
            ? context.RootPath
            : destinationIoDir;
        var extension = ResolveContainerAwareExtension(context.FullPath, context.Log);
        var destinationPath = GetUniquePath(Path.Join(destinationDir, $"{pathResult.Filename}{extension}"), context.FullPath);

        return new MovePlanItem
        {
            SourcePath = context.FullPath,
            SourceDir = context.SourceDir,
            DestinationDir = destinationDir,
            DestinationPath = destinationPath,
            IsUntagged = false
        };
    }

    private static string ResolveContainerAwareExtension(string fullPath, Action<string>? log)
    {
        var currentExtension = Path.GetExtension(fullPath);
        if (string.IsNullOrWhiteSpace(currentExtension))
        {
            return currentExtension;
        }

        var normalized = currentExtension.ToLowerInvariant();
        if (!string.Equals(normalized, ".flac", StringComparison.OrdinalIgnoreCase))
        {
            return currentExtension;
        }

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[12];
            var read = stream.Read(header);
            if (read < 12)
            {
                return currentExtension;
            }

            var looksLikeMp4 = header[4] == (byte)'f'
                && header[5] == (byte)'t'
                && header[6] == (byte)'y'
                && header[7] == (byte)'p';
            if (!looksLikeMp4)
            {
                return currentExtension;
            }

            log?.Invoke($"organizer normalized extension for container mismatch: {fullPath} (.flac -> .m4a)");
            return ".m4a";
        }
        catch (Exception)
        {
            return currentExtension;
        }
    }

    private MovePlanItem? BuildUnreadableMetadataMovePlanItem(MovePlanTargetContext context)
    {
        Track? track = null;
        var usedShazam = false;

        if (context.Options.UseShazamForUntaggedFiles)
        {
            track = BuildTrackFromShazamRecognition(
                context.FullPath,
                context.UsePrimaryArtistFolders,
                context.Settings.Tags?.MultiArtistSeparator,
                context.Log);
            usedShazam = track != null;
        }

        track ??= BuildTrackFromPathFallback(
            context.FullPath,
            context.RootPath,
            context.UsePrimaryArtistFolders,
            context.Settings.Tags?.MultiArtistSeparator);

        if (track == null)
        {
            return BuildUntaggedMovePlanItem(
                context.FullPath,
                context.RootPath,
                context.SourceDir,
                context.Options,
                context.Log,
                "organizer skipped unreadable metadata file");
        }

        var downloadType = IsSingleTrackDownload(track) ? DownloadTypeTrack : DownloadTypeAlbum;
        context.Log?.Invoke(usedShazam
            ? $"organizer inferred core tags from Shazam for unreadable metadata file: {context.FullPath}"
            : $"organizer inferred core tags from path for unreadable metadata file: {context.FullPath}");
        return BuildMovePlanItemFromTrack(track, downloadType, context);
    }

    private static MovePlanItem? BuildUntaggedMovePlanItem(
        string fullPath,
        string rootPath,
        string sourceDir,
        AutoTagOrganizerOptions options,
        Action<string>? log,
        string skipMessage)
    {
        if (string.IsNullOrWhiteSpace(options.MoveUntaggedPath))
        {
            log?.Invoke($"{skipMessage}: {fullPath}");
            return null;
        }

        var destinationIoDir = DownloadPathResolver.ResolveIoPath(options.MoveUntaggedPath);
        var destinationDir = string.IsNullOrWhiteSpace(destinationIoDir) ? rootPath : destinationIoDir;
        var destinationPath = GetUniquePath(Path.Join(destinationDir, Path.GetFileName(fullPath)), fullPath);
        return new MovePlanItem
        {
            SourcePath = fullPath,
            SourceDir = sourceDir,
            DestinationDir = destinationDir,
            DestinationPath = destinationPath,
            IsUntagged = true
        };
    }

    private static MovePlanItem? ApplyMovePlanOptions(
        string fullPath,
        MovePlanItem target,
        AutoTagOrganizerOptions options)
    {
        var sourceDir = target.SourceDir;
        var destinationDir = target.DestinationDir;
        var destinationPath = target.DestinationPath;
        var sourceFileName = Path.GetFileName(fullPath);
        var destinationFileName = Path.GetFileName(destinationPath);
        var requiresDirectoryMove = !string.Equals(sourceDir, destinationDir, StringComparison.OrdinalIgnoreCase);
        var requiresRename = !string.Equals(sourceFileName, destinationFileName, StringComparison.OrdinalIgnoreCase);

        if (!options.MoveMisplacedFiles && requiresDirectoryMove)
        {
            destinationDir = sourceDir;
            destinationPath = Path.Join(sourceDir, options.RenameFilesToTemplate ? destinationFileName : sourceFileName);
            destinationPath = GetUniquePath(destinationPath, fullPath);
            destinationFileName = Path.GetFileName(destinationPath);
            requiresDirectoryMove = false;
            requiresRename = !string.Equals(sourceFileName, destinationFileName, StringComparison.OrdinalIgnoreCase);
        }

        if (!options.RenameFilesToTemplate && requiresRename)
        {
            destinationPath = Path.Join(destinationDir, sourceFileName);
            destinationPath = GetUniquePath(destinationPath, fullPath);
            destinationFileName = Path.GetFileName(destinationPath);
            requiresRename = !string.Equals(sourceFileName, destinationFileName, StringComparison.OrdinalIgnoreCase);
        }

        if ((!options.MoveMisplacedFiles && !options.RenameFilesToTemplate)
            || (!requiresDirectoryMove && !requiresRename))
        {
            return null;
        }

        return new MovePlanItem
        {
            SourcePath = fullPath,
            SourceDir = sourceDir,
            DestinationDir = destinationDir,
            DestinationPath = destinationPath,
            IsUntagged = target.IsUntagged
        };
    }

    private Dictionary<string, Dictionary<string, int>> ExecuteMovePlan(string rootPath, List<MovePlanItem> plan, AutoTagOrganizerOptions options, AutoTagOrganizerReport? report, Action<string>? log)
    {
        var artistDirectoryTransitions = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        if (plan.Count == 0)
        {
            log?.Invoke("organizer no move actions generated");
            report?.Entries.Add("noop: no move actions generated");
            return artistDirectoryTransitions;
        }

        var actionsBySourceDir = plan
            .GroupBy(item => item.SourceDir, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sourceFolderIndex = 0;
        foreach (var group in actionsBySourceDir)
        {
            sourceFolderIndex++;
            try
            {
                ProcessMovePlanSourceGroup(
                    rootPath,
                    group,
                    options,
                    report,
                    log,
                    sourceFolderIndex,
                    actionsBySourceDir.Count,
                    artistDirectoryTransitions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordOrganizerFailure(
                    report,
                    log,
                    "process source folder",
                    group.Key,
                    destinationPath: null,
                    ex);
            }
        }

        return artistDirectoryTransitions;
    }

    private void ProcessMovePlanSourceGroup(
        string rootPath,
        IGrouping<string, MovePlanItem> group,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log,
        int sourceFolderIndex,
        int sourceFolderCount,
        Dictionary<string, Dictionary<string, int>> artistDirectoryTransitions)
    {
        var sourceDir = group.Key;
        var actions = group.ToList();
        var destinationDirs = actions
            .Select(item => item.DestinationDir)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        log?.Invoke($"organizer processing source folder ({sourceFolderIndex}/{sourceFolderCount}): {sourceDir}");

        if (ShouldSkipExistingDestinationMerge(sourceDir, destinationDirs, options, report, log))
        {
            return;
        }

        if (options.MoveMisplacedFiles
            && !options.RenameFilesToTemplate
            && destinationDirs.Count == 1
            && TryMoveFolder(rootPath, sourceDir, destinationDirs[0], options, report, log, artistDirectoryTransitions))
        {
            return;
        }

        foreach (var action in actions)
        {
            MoveSingleFile(rootPath, action, options, report, log, artistDirectoryTransitions);
        }

        var destinationDir = destinationDirs.FirstOrDefault();
        if (options.MoveMisplacedFiles && !string.IsNullOrWhiteSpace(destinationDir))
        {
            MoveRemainingFilesIfAlbumDone(rootPath, sourceDir, destinationDir, log, options, report);
        }
    }

    private static bool ShouldSkipExistingDestinationMerge(
        string sourceDir,
        IReadOnlyList<string> destinationDirs,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        if (!options.MoveMisplacedFiles
            || destinationDirs.Count != 1
            || !Directory.Exists(destinationDirs[0])
            || options.MergeIntoExistingDestinationFolders)
        {
            return false;
        }

        if (report != null)
        {
            report.SkippedExistingFolderMerges++;
        }

        log?.Invoke($"organizer skipped merge into existing destination folder: {sourceDir} -> {destinationDirs[0]}");
        report?.Entries.Add($"skip: existing destination folder merge -> {sourceDir} -> {destinationDirs[0]}");
        return true;
    }

    private bool TryMoveFolder(
        string rootPath,
        string sourceDir,
        string destinationDir,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log,
        Dictionary<string, Dictionary<string, int>> artistDirectoryTransitions)
    {
        if (!options.MoveMisplacedFiles)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(destinationDir))
        {
            return false;
        }

        if (string.Equals(sourceDir, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsPathUnderRoot(sourceDir, rootPath))
        {
            return false;
        }

        if (Directory.Exists(destinationDir))
        {
            return false;
        }

        if (!Directory.Exists(sourceDir))
        {
            return false;
        }

        if (options.DryRun)
        {
            log?.Invoke($"organizer dry-run: would move folder {sourceDir} -> {destinationDir}");
            return true;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationDir) ?? destinationDir);
            Directory.Move(sourceDir, destinationDir);
            if (report != null)
            {
                report.MovedFolders++;
            }

            RegisterArtistDirectoryTransition(artistDirectoryTransitions, rootPath, sourceDir, destinationDir);
            _logger.LogInformation("AutoTag organizer moved folder {SourceDir} -> {DestinationDir}", sourceDir, destinationDir);
            log?.Invoke($"organizer moved folder: {sourceDir} -> {destinationDir}");
            report?.Entries.Add($"move-folder: {sourceDir} -> {destinationDir}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordOrganizerFailure(
                report,
                log,
                "move folder",
                sourceDir,
                destinationDir,
                ex);
            return false;
        }
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSlash = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
    }

    private void MoveSingleFile(
        string rootPath,
        MovePlanItem action,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log,
        Dictionary<string, Dictionary<string, int>> artistDirectoryTransitions)
    {
        if (!IOFile.Exists(action.SourcePath))
        {
            log?.Invoke($"organizer skipped missing source file: {action.SourcePath}");
            report?.Entries.Add($"skip: missing source file -> {action.SourcePath}");
            return;
        }

        if (HandleDuplicateFileCollision(rootPath, action, options, report, log))
        {
            return;
        }

        if (options.PreferredExtensions.Count > 0
            && IOFile.Exists(action.DestinationPath)
            && PreferredExtensionComparer.ShouldSkipForPreferredExtension(action.SourcePath, action.DestinationPath, options.PreferredExtensions))
        {
            log?.Invoke($"organizer skipped (preferred format exists): {action.SourcePath}");
            if (report != null)
            {
                report.SkippedConflicts++;
            }
            report?.Entries.Add($"skip: preferred format exists -> {action.SourcePath}");
            return;
        }

        if (options.DryRun)
        {
            log?.Invoke($"organizer dry-run: would move file {action.SourcePath} -> {action.DestinationPath}");
            return;
        }

        try
        {
            Directory.CreateDirectory(action.DestinationDir);
            MoveFileOverwrite(action.SourcePath, action.DestinationPath);
            if (report != null)
            {
                report.MovedFiles++;
            }

            RegisterArtistDirectoryTransition(artistDirectoryTransitions, rootPath, action.SourceDir, action.DestinationDir);
            _logger.LogInformation("AutoTag organizer moved file {SourcePath} -> {DestinationPath}", action.SourcePath, action.DestinationPath);
            log?.Invoke($"organizer moved file: {action.SourcePath} -> {action.DestinationPath}");
            report?.Entries.Add($"move-file: {action.SourcePath} -> {action.DestinationPath}");
            MoveSidecarFiles(new SidecarMoveContext(
                rootPath,
                action.SourceDir,
                action.DestinationDir,
                action.SourcePath,
                action.DestinationPath,
                options,
                report,
                log));
            CleanupSourceDirectoryIfConfigured(rootPath, action.SourceDir, options, log);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordOrganizerFailure(
                report,
                log,
                "move file",
                action.SourcePath,
                action.DestinationPath,
                ex);
        }
    }

    private void MoveResidualArtistSidecarsForTransitions(
        string rootPath,
        IReadOnlyDictionary<string, Dictionary<string, int>> artistDirectoryTransitions,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        if (!options.MoveMisplacedFiles || artistDirectoryTransitions.Count == 0)
        {
            return;
        }

        foreach (var transition in artistDirectoryTransitions)
        {
            var sourceArtistDir = transition.Key;
            if (!Directory.Exists(sourceArtistDir))
            {
                continue;
            }

            if (EnumerateAudioFiles(sourceArtistDir, includeSubfolders: true).Any())
            {
                continue;
            }

            var destinationArtistDir = SelectPreferredArtistTransitionDestination(rootPath, sourceArtistDir, transition.Value);
            if (string.IsNullOrWhiteSpace(destinationArtistDir))
            {
                continue;
            }

            if (options.DryRun)
            {
                log?.Invoke($"organizer dry-run: would move residual artist sidecars {sourceArtistDir} -> {destinationArtistDir}");
                continue;
            }

            try
            {
                var movedCount = MoveResidualArtistDirectoryContents(sourceArtistDir, destinationArtistDir, report, log);
                if (movedCount > 0 && options.RemoveEmptyFolders)
                {
                    DeleteEmptyDirectoryTree(sourceArtistDir, rootPath, log);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordOrganizerFailure(
                    report,
                    log,
                    "move residual artist sidecars",
                    sourceArtistDir,
                    destinationArtistDir,
                    ex);
            }
        }
    }

    private static int MoveResidualArtistDirectoryContents(
        string sourceArtistDir,
        string destinationArtistDir,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        Directory.CreateDirectory(destinationArtistDir);
        var movedFiles = 0;
        foreach (var sourceFile in Directory.EnumerateFiles(sourceArtistDir, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceArtistDir, sourceFile);
            var destinationFile = Path.Join(destinationArtistDir, relative);
            var destinationDirectory = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            destinationFile = GetUniquePath(destinationFile, sourceFile);
            MoveFileOverwrite(sourceFile, destinationFile);
            movedFiles++;
            if (report != null)
            {
                report.MovedSidecars++;
            }

            log?.Invoke($"organizer moved residual artist sidecar: {sourceFile} -> {destinationFile}");
            report?.Entries.Add($"move-residual-artist-sidecar: {sourceFile} -> {destinationFile}");
        }

        return movedFiles;
    }

    private void MergeNoAudioArtistDirectoriesIntoMatchingDestinations(
        string rootPath,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        if (!options.MoveMisplacedFiles || !Directory.Exists(rootPath))
        {
            return;
        }

        var duplicatesRoot = Path.GetFullPath(Path.Join(
            rootPath,
            string.IsNullOrWhiteSpace(options.DuplicatesFolderName)
                ? DuplicateCleanerService.DuplicatesFolderName
                : options.DuplicatesFolderName.Trim()));
        var artistDirectories = GetTopLevelArtistDirectories(rootPath)
            .Select(path =>
            {
                var normalizedPath = Path.GetFullPath(path);
                var artistName = Path.GetFileName(normalizedPath)?.Trim() ?? string.Empty;
                var hasAudio = EnumerateAudioFiles(normalizedPath, includeSubfolders: true).Any();
                var key = BuildArtistFolderMatchKey(artistName);
                return new ArtistDirectoryMatchCandidate(normalizedPath, artistName, key, hasAudio);
            })
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Key)
                && !IsPathUnderRoot(candidate.Path, duplicatesRoot))
            .ToList();
        if (artistDirectories.Count == 0)
        {
            return;
        }

        var destinationsByKey = artistDirectories
            .Where(candidate => candidate.HasAudio)
            .GroupBy(candidate => candidate.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.Ordinal);
        if (destinationsByKey.Count == 0)
        {
            return;
        }

        foreach (var source in artistDirectories.Where(candidate => !candidate.HasAudio))
        {
            if (!Directory.Exists(source.Path)
                || !Directory.EnumerateFiles(source.Path, "*.*", SearchOption.AllDirectories).Any())
            {
                continue;
            }

            var destination = ResolveMatchingArtistDestination(source, destinationsByKey);
            if (string.IsNullOrWhiteSpace(destination))
            {
                continue;
            }

            if (options.DryRun)
            {
                log?.Invoke($"organizer dry-run: would merge no-audio artist folder {source.Path} -> {destination}");
                continue;
            }

            try
            {
                var movedCount = MoveResidualArtistDirectoryContents(source.Path, destination, report, log);
                if (movedCount <= 0)
                {
                    continue;
                }

                log?.Invoke($"organizer merged no-audio artist folder: {source.Path} -> {destination}");
                report?.Entries.Add($"merge-no-audio-artist-folder: {source.Path} -> {destination}");
                if (options.RemoveEmptyFolders)
                {
                    DeleteEmptyDirectoryTree(source.Path, rootPath, log);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordOrganizerFailure(
                    report,
                    log,
                    "merge no-audio artist folder",
                    source.Path,
                    destination,
                    ex);
            }
        }
    }

    private static string? ResolveMatchingArtistDestination(
        ArtistDirectoryMatchCandidate source,
        IReadOnlyDictionary<string, List<ArtistDirectoryMatchCandidate>> destinationsByKey)
    {
        if (destinationsByKey.TryGetValue(source.Key, out var directMatches))
        {
            var match = directMatches
                .Select(candidate => candidate.Path)
                .FirstOrDefault(path => !string.Equals(path, source.Path, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        var primaryArtist = ExtractFirstMainArtist(source.ArtistName);
        if (string.IsNullOrWhiteSpace(primaryArtist))
        {
            return null;
        }

        var primaryKey = BuildArtistFolderMatchKey(primaryArtist);
        if (string.IsNullOrWhiteSpace(primaryKey)
            || string.Equals(primaryKey, source.Key, StringComparison.Ordinal))
        {
            return null;
        }

        if (!destinationsByKey.TryGetValue(primaryKey, out var primaryMatches))
        {
            return null;
        }

        return primaryMatches
            .Select(candidate => candidate.Path)
            .FirstOrDefault(path => !string.Equals(path, source.Path, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path));
    }

    private static string BuildArtistFolderMatchKey(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return string.Empty;
        }

        return new string(artistName
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static string? SelectPreferredArtistTransitionDestination(
        string rootPath,
        string sourceArtistDir,
        IReadOnlyDictionary<string, int> destinationCounts)
    {
        return destinationCounts
            .Where(pair =>
                pair.Value > 0
                && !string.IsNullOrWhiteSpace(pair.Key)
                && IsPathUnderRoot(pair.Key, rootPath)
                && !string.Equals(pair.Key, sourceArtistDir, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key)
            .FirstOrDefault();
    }

    private static void RegisterArtistDirectoryTransition(
        Dictionary<string, Dictionary<string, int>> artistDirectoryTransitions,
        string rootPath,
        string sourceDir,
        string destinationDir)
    {
        var sourceArtistDir = TryResolveTopLevelArtistDirectory(rootPath, sourceDir);
        var destinationArtistDir = TryResolveTopLevelArtistDirectory(rootPath, destinationDir);
        if (string.IsNullOrWhiteSpace(sourceArtistDir)
            || string.IsNullOrWhiteSpace(destinationArtistDir)
            || string.Equals(sourceArtistDir, destinationArtistDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!artistDirectoryTransitions.TryGetValue(sourceArtistDir, out var destinationCounts))
        {
            destinationCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            artistDirectoryTransitions[sourceArtistDir] = destinationCounts;
        }

        destinationCounts.TryGetValue(destinationArtistDir, out var currentCount);
        destinationCounts[destinationArtistDir] = currentCount + 1;
    }

    private static string? TryResolveTopLevelArtistDirectory(string rootPath, string path)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalizedRoot = Path.GetFullPath(rootPath);
        var normalizedPath = Path.GetFullPath(path);
        if (!IsPathUnderRoot(normalizedPath, normalizedRoot))
        {
            return null;
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }

        var topLevelSegment = relative
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(topLevelSegment))
        {
            return null;
        }

        return Path.Join(normalizedRoot, topLevelSegment);
    }

    private bool HandleDuplicateFileCollision(
        string rootPath,
        MovePlanItem action,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        if (!IOFile.Exists(action.DestinationPath)
            || string.Equals(action.SourcePath, action.DestinationPath, StringComparison.OrdinalIgnoreCase)
            || !AudioCollisionDedupe.IsDuplicate(action.DestinationPath, action.SourcePath))
        {
            return false;
        }

        var preferIncoming = AudioCollisionDedupe.ShouldPreferIncoming(action.DestinationPath, action.SourcePath);
        if (!options.ResolveSameTrackQualityConflicts)
        {
            PreserveDuplicateWithUniqueDestination(
                action,
                report,
                log,
                "organizer kept duplicate due to disabled conflict resolution",
                "keep-both-duplicate");
            return false;
        }

        if (string.Equals(options.DuplicateConflictPolicy, AutoTagOrganizerOptions.DuplicateConflictKeepBoth, StringComparison.OrdinalIgnoreCase))
        {
            PreserveDuplicateWithUniqueDestination(
                action,
                report,
                log,
                "organizer kept both duplicates",
                "keep-both-duplicate");
            return false;
        }

        if (string.Equals(options.DuplicateConflictPolicy, AutoTagOrganizerOptions.DuplicateConflictKeepLower, StringComparison.OrdinalIgnoreCase))
        {
            PreserveDuplicateWithUniqueDestination(
                action,
                report,
                log,
                "organizer preserved lower-quality duplicate",
                "keep-lower-duplicate");
            return false;
        }

        if (options.DryRun)
        {
            LogDuplicateDryRun(action, preferIncoming, log);
            return true;
        }

        if (string.Equals(options.DuplicateConflictPolicy, AutoTagOrganizerOptions.DuplicateConflictMoveToDuplicates, StringComparison.OrdinalIgnoreCase))
        {
            return HandleMoveToDuplicatesConflict(rootPath, action, options, report, log, preferIncoming);
        }

        return HandleKeepBestDuplicateConflict(rootPath, action, options, report, log, preferIncoming);
    }

    private static void PreserveDuplicateWithUniqueDestination(
        MovePlanItem action,
        AutoTagOrganizerReport? report,
        Action<string>? log,
        string logPrefix,
        string reportPrefix)
    {
        action.DestinationPath = GetUniquePath(action.DestinationPath, action.SourcePath);
        log?.Invoke($"{logPrefix}: {action.SourcePath} -> {action.DestinationPath}");
        if (report != null)
        {
            report.KeptLowerQualityDuplicates++;
        }

        report?.Entries.Add($"{reportPrefix}: {action.SourcePath} -> {action.DestinationPath}");
    }

    private static void LogDuplicateDryRun(MovePlanItem action, bool preferIncoming, Action<string>? log)
    {
        if (preferIncoming)
        {
            log?.Invoke($"organizer dry-run: would replace duplicate destination {action.DestinationPath} using {action.SourcePath}");
            return;
        }

        log?.Invoke($"organizer dry-run: would skip duplicate file {action.SourcePath} (already exists at {action.DestinationPath})");
    }

    private bool HandleMoveToDuplicatesConflict(
        string rootPath,
        MovePlanItem action,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log,
        bool preferIncoming)
    {
        var losingPath = preferIncoming ? action.DestinationPath : action.SourcePath;
        var target = QuarantineDuplicateFile(rootPath, options.DuplicatesFolderName, losingPath);
        MoveAssociatedDuplicateSidecarsToQuarantine(rootPath, losingPath, target, options, report, log);
        if (report != null)
        {
            report.QuarantinedDuplicates++;
        }

        report?.Entries.Add($"quarantine-duplicate: {losingPath} -> {target}");

        if (preferIncoming)
        {
            MoveFileOverwrite(action.SourcePath, action.DestinationPath);
            if (report != null)
            {
                report.ReplacedDuplicates++;
            }

            log?.Invoke($"organizer replaced duplicate destination: {action.DestinationPath} using {action.SourcePath}");
        }
        else
        {
            log?.Invoke($"organizer quarantined duplicate source: {action.SourcePath}");
        }

        FinalizeDuplicateCleanup(
            rootPath,
            action,
            options,
            report,
            log,
            moveSourceSidecarsToDestination: preferIncoming);
        return true;
    }

    private bool HandleKeepBestDuplicateConflict(
        string rootPath,
        MovePlanItem action,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log,
        bool preferIncoming)
    {
        if (preferIncoming)
        {
            var quarantinedPath = QuarantineDuplicateFile(rootPath, options.DuplicatesFolderName, action.DestinationPath);
            MoveAssociatedDuplicateSidecarsToQuarantine(rootPath, action.DestinationPath, quarantinedPath, options, report, log);
            if (report != null)
            {
                report.QuarantinedDuplicates++;
            }

            report?.Entries.Add($"quarantine-duplicate: {action.DestinationPath} -> {quarantinedPath}");
            MoveFileOverwrite(action.SourcePath, action.DestinationPath);
            if (report != null)
            {
                report.ReplacedDuplicates++;
            }

            _logger.LogInformation("AutoTag organizer replaced duplicate destination {DestinationPath} using {SourcePath}", action.DestinationPath, action.SourcePath);
            log?.Invoke($"organizer replaced duplicate destination: {action.DestinationPath} using {action.SourcePath}");
            report?.Entries.Add($"replace-duplicate: {action.DestinationPath} <= {action.SourcePath}");
            FinalizeDuplicateCleanup(
                rootPath,
                action,
                options,
                report,
                log,
                moveSourceSidecarsToDestination: true);
            return true;
        }

        var sourceQuarantinedPath = QuarantineDuplicateFile(rootPath, options.DuplicatesFolderName, action.SourcePath);
        MoveAssociatedDuplicateSidecarsToQuarantine(rootPath, action.SourcePath, sourceQuarantinedPath, options, report, log);
        if (report != null)
        {
            report.QuarantinedDuplicates++;
        }

        _logger.LogInformation("AutoTag organizer quarantined duplicate source {SourcePath} (existing {DestinationPath})", action.SourcePath, action.DestinationPath);
        log?.Invoke($"organizer quarantined duplicate source: {action.SourcePath} -> {sourceQuarantinedPath}");
        report?.Entries.Add($"quarantine-duplicate: {action.SourcePath} -> {sourceQuarantinedPath}");
        FinalizeDuplicateCleanup(
            rootPath,
            action,
            options,
            report,
            log,
            moveSourceSidecarsToDestination: false);
        return true;
    }

    private void FinalizeDuplicateCleanup(
        string rootPath,
        MovePlanItem action,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log,
        bool moveSourceSidecarsToDestination)
    {
        if (moveSourceSidecarsToDestination)
        {
            MoveSidecarFiles(new SidecarMoveContext(
                rootPath,
                action.SourceDir,
                action.DestinationDir,
                action.SourcePath,
                action.DestinationPath,
                options,
                report,
                log));
        }

        MoveDuplicateSourceFolderToQuarantineIfNoAudio(rootPath, action.SourceDir, options, report, log);
        CleanupSourceDirectoryIfConfigured(rootPath, action.SourceDir, options, log);
    }

    private void MoveAssociatedDuplicateSidecarsToQuarantine(
        string rootPath,
        string originalFilePath,
        string quarantinedFilePath,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        var sourceDir = Path.GetDirectoryName(originalFilePath);
        var destinationDir = Path.GetDirectoryName(quarantinedFilePath);
        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(destinationDir))
        {
            return;
        }

        MoveSidecarFiles(new SidecarMoveContext(
            rootPath,
            sourceDir,
            destinationDir,
            originalFilePath,
            quarantinedFilePath,
            options,
            report,
            log));
    }

    private static string QuarantineDuplicateFile(string rootPath, string? duplicatesFolderName, string sourcePath)
    {
        var folderName = string.IsNullOrWhiteSpace(duplicatesFolderName)
            ? DuplicateCleanerService.DuplicatesFolderName
            : duplicatesFolderName.Trim();
        var duplicatesRoot = Path.Join(rootPath, folderName);
        Directory.CreateDirectory(duplicatesRoot);
        var target = GetUniquePath(Path.Join(duplicatesRoot, Path.GetFileName(sourcePath)), sourcePath);
        MoveFileOverwrite(sourcePath, target);
        return target;
    }

    private void MoveDuplicateSourceFolderToQuarantineIfNoAudio(
        string rootPath,
        string sourceDir,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        var folderName = string.IsNullOrWhiteSpace(options.DuplicatesFolderName)
            ? DuplicateCleanerService.DuplicatesFolderName
            : options.DuplicatesFolderName.Trim();
        var duplicatesRoot = Path.Join(rootPath, folderName);
        if (IsPathUnderRoot(sourceDir, duplicatesRoot))
        {
            return;
        }

        if (EnumerateAudioFiles(sourceDir, includeSubfolders: true).Any())
        {
            return;
        }

        if (!Directory.EnumerateFileSystemEntries(sourceDir).Any())
        {
            return;
        }

        var sourceFolderName = Path.GetFileName(sourceDir);
        if (string.IsNullOrWhiteSpace(sourceFolderName))
        {
            return;
        }

        var targetDirectory = GetUniqueDirectoryPath(Path.Join(duplicatesRoot, sourceFolderName), sourceDir);
        var movedFiles = options.DryRun
            ? 0
            : Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories).Count();
        if (options.DryRun)
        {
            log?.Invoke($"organizer dry-run: would move duplicate leftovers folder {sourceDir} -> {targetDirectory}");
            return;
        }

        try
        {
            Directory.CreateDirectory(duplicatesRoot);
            Directory.Move(sourceDir, targetDirectory);
            if (report != null)
            {
                report.MovedLeftovers += movedFiles;
            }

            _logger.LogInformation("AutoTag organizer moved duplicate leftovers folder {SourceDir} -> {DestinationDir}", sourceDir, targetDirectory);
            log?.Invoke($"organizer moved duplicate leftovers folder: {sourceDir} -> {targetDirectory}");
            report?.Entries.Add($"move-duplicate-leftovers-folder: {sourceDir} -> {targetDirectory}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordOrganizerFailure(
                report,
                log,
                "move duplicate leftovers folder",
                sourceDir,
                targetDirectory,
                ex);
        }
    }

    private void MoveExistingNoAudioDirectoriesToQuarantine(
        string rootPath,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        var folderName = string.IsNullOrWhiteSpace(options.DuplicatesFolderName)
            ? DuplicateCleanerService.DuplicatesFolderName
            : options.DuplicatesFolderName.Trim();
        var duplicatesRoot = Path.Join(rootPath, folderName);
        var candidates = Directory
            .EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderByDescending(path => path.Length)
            .ToList();

        foreach (var directory in candidates)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            if (IsPathUnderRoot(directory, duplicatesRoot))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0)
            {
                continue;
            }

            if (EnumerateAudioFiles(directory, includeSubfolders: true).Any())
            {
                continue;
            }

            MoveDirectoryToDuplicatesRoot(rootPath, directory, duplicatesRoot, options, report, log, "legacy no-audio leftovers");
        }
    }

    private void MoveDirectoryToDuplicatesRoot(
        string rootPath,
        string sourceDir,
        string duplicatesRoot,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log,
        string reason)
    {
        var sourceFolderName = Path.GetFileName(sourceDir);
        if (string.IsNullOrWhiteSpace(sourceFolderName))
        {
            return;
        }

        var targetDirectory = GetUniqueDirectoryPath(Path.Join(duplicatesRoot, sourceFolderName), sourceDir);
        var movedFiles = options.DryRun
            ? 0
            : Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories).Count();
        if (options.DryRun)
        {
            log?.Invoke($"organizer dry-run: would move {reason} folder {sourceDir} -> {targetDirectory}");
            return;
        }

        try
        {
            Directory.CreateDirectory(duplicatesRoot);
            Directory.Move(sourceDir, targetDirectory);
            if (report != null)
            {
                report.MovedLeftovers += movedFiles;
            }

            _logger.LogInformation("AutoTag organizer moved {Reason} folder {SourceDir} -> {DestinationDir}", reason, sourceDir, targetDirectory);
            log?.Invoke($"organizer moved {reason} folder: {sourceDir} -> {targetDirectory}");
            report?.Entries.Add($"move-{reason.Replace(' ', '-')}-folder: {sourceDir} -> {targetDirectory}");
            if (options.RemoveEmptyFolders)
            {
                DeleteEmptyDirectoryTree(Path.GetDirectoryName(sourceDir) ?? string.Empty, rootPath, log);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordOrganizerFailure(
                report,
                log,
                $"move {reason} folder",
                sourceDir,
                targetDirectory,
                ex);
        }
    }

    private void CleanupSourceDirectoryIfConfigured(
        string rootPath,
        string sourceDir,
        AutoTagOrganizerOptions options,
        Action<string>? log)
    {
        if (!options.DryRun && options.RemoveEmptyFolders)
        {
            DeleteEmptyDirectoryTree(sourceDir, rootPath, log);
        }
    }

    private static Track BuildTrackFromTag(
        Tag tag,
        string fullPath,
        bool usePrimaryArtistFolders,
        string? multiArtistSeparator)
    {
        var title = string.IsNullOrWhiteSpace(tag.Title)
            ? Path.GetFileNameWithoutExtension(fullPath)
            : tag.Title.Trim();
        var albumTitle = string.IsNullOrWhiteSpace(tag.Album) ? "Unknown Album" : tag.Album.Trim();

        var albumArtists = (tag.AlbumArtists ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();
        var performers = (tag.Performers ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();

        var artistCredits = albumArtists.Count > 0 ? albumArtists : performers;
        var expandedArtists = ExpandArtistNames(artistCredits);
        if (expandedArtists.Count == 0)
        {
            expandedArtists.Add("Unknown Artist");
        }

        var primaryArtistName = expandedArtists[0];
        var keepBothArtistName = string.Join(GetArtistSeparator(multiArtistSeparator), expandedArtists);
        var mainArtistName = usePrimaryArtistFolders ? primaryArtistName : keepBothArtistName;
        var mainArtist = new Artist(mainArtistName);

        var track = new Track
        {
            Title = title,
            MainArtist = mainArtist,
            TrackNumber = (int)tag.Track,
            DiscNumber = (int)tag.Disc,
            DiskNumber = (int)tag.Disc,
            Bpm = tag.BeatsPerMinute
        };

        track.Artists = expandedArtists.ToList();
        track.Artist[MainArtistRole] = usePrimaryArtistFolders
            ? new List<string> { primaryArtistName }
            : track.Artists.ToList();
        if (usePrimaryArtistFolders && expandedArtists.Count > 1)
        {
            track.Artist[FeaturedArtistRole] = expandedArtists.Skip(1).ToList();
        }

        var album = new Album(albumTitle)
        {
            MainArtist = mainArtist,
            TrackTotal = (int)tag.TrackCount,
            DiscTotal = tag.DiscCount > 0 ? (int)tag.DiscCount : 1,
            Label = tag.Publisher ?? string.Empty
        };
        album.Artists = track.Artists.ToList();
        album.Artist[MainArtistRole] = usePrimaryArtistFolders
            ? new List<string> { primaryArtistName }
            : album.Artists.ToList();
        if (usePrimaryArtistFolders && expandedArtists.Count > 1)
        {
            album.Artist[FeaturedArtistRole] = expandedArtists.Skip(1).ToList();
        }
        if (tag.Genres?.Length > 0)
        {
            album.Genre = tag.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList();
        }

        if (tag.Year > 0)
        {
            album.Date.Year = tag.Year.ToString();
            album.Date.FixDayMonth();
            track.Date = album.Date;
        }

        track.Album = album;
        track.GenerateMainFeatStrings();

        return track;
    }

    private static Track? BuildTrackFromPathFallback(
        string fullPath,
        string rootPath,
        bool usePrimaryArtistFolders,
        string? multiArtistSeparator)
    {
        var fileStem = Path.GetFileNameWithoutExtension(fullPath)?.Trim();
        if (string.IsNullOrWhiteSpace(fileStem))
        {
            return null;
        }

        var (parsedTrackNumber, parsedTitle) = ExtractTrackNumberAndTitle(fileStem);
        var title = parsedTitle;

        var relativeParts = GetRelativePathParts(fullPath, rootPath, out var directory);
        var fallbackArtist = ResolveFallbackArtist(directory);
        var artistCredit = ResolveArtistCredit(relativeParts, fallbackArtist);
        var albumTitle = ResolveAlbumTitle(relativeParts);

        title = TryRecoverSingleTrackTitle(title, albumTitle);

        var expandedArtists = ExpandArtistNames(new[] { artistCredit });
        if (expandedArtists.Count == 0)
        {
            expandedArtists.Add("Unknown Artist");
        }

        var primaryArtistName = expandedArtists[0];
        var keepBothArtistName = string.Join(GetArtistSeparator(multiArtistSeparator), expandedArtists);
        var mainArtistName = usePrimaryArtistFolders ? primaryArtistName : keepBothArtistName;
        var mainArtist = new Artist(mainArtistName);

        var track = new Track
        {
            Title = title,
            MainArtist = mainArtist,
            TrackNumber = parsedTrackNumber,
            DiscNumber = 0,
            DiskNumber = 0
        };

        track.Artists = expandedArtists.ToList();
        track.Artist[MainArtistRole] = usePrimaryArtistFolders
            ? new List<string> { primaryArtistName }
            : track.Artists.ToList();
        if (usePrimaryArtistFolders && expandedArtists.Count > 1)
        {
            track.Artist[FeaturedArtistRole] = expandedArtists.Skip(1).ToList();
        }

        var album = new Album(string.IsNullOrWhiteSpace(albumTitle) ? "Singles" : albumTitle)
        {
            MainArtist = mainArtist
        };
        album.Artists = track.Artists.ToList();
        album.Artist[MainArtistRole] = usePrimaryArtistFolders
            ? new List<string> { primaryArtistName }
            : album.Artists.ToList();
        if (usePrimaryArtistFolders && expandedArtists.Count > 1)
        {
            album.Artist[FeaturedArtistRole] = expandedArtists.Skip(1).ToList();
        }

        track.Album = album;
        track.GenerateMainFeatStrings();
        return track;
    }

    private Track? BuildTrackFromShazamRecognition(
        string fullPath,
        bool usePrimaryArtistFolders,
        string? multiArtistSeparator,
        Action<string>? log)
    {
        if (!_shazamRecognitionService.IsAvailable)
        {
            log?.Invoke($"organizer skipped Shazam fallback because the recognizer is unavailable: {fullPath}");
            return null;
        }

        try
        {
            var recognition = _shazamRecognitionService.Recognize(fullPath);
            if (recognition?.HasCoreMetadata != true)
            {
                return null;
            }

            var title = string.IsNullOrWhiteSpace(recognition.Title)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : recognition.Title.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var expandedArtists = ResolveExpandedRecognitionArtists(recognition);
            if (expandedArtists.Count == 0)
            {
                return null;
            }

            var albumTitle = string.IsNullOrWhiteSpace(recognition.Album)
                ? SinglesAlbumTitle
                : recognition.Album.Trim();
            var primaryArtistName = expandedArtists[0];
            var keepBothArtistName = string.Join(GetArtistSeparator(multiArtistSeparator), expandedArtists);
            var mainArtistName = usePrimaryArtistFolders ? primaryArtistName : keepBothArtistName;
            var mainArtist = new Artist(mainArtistName);

            var track = new Track
            {
                Title = title,
                MainArtist = mainArtist,
                TrackNumber = recognition.TrackNumber.GetValueOrDefault(),
                DiscNumber = recognition.DiscNumber.GetValueOrDefault(),
                DiskNumber = recognition.DiscNumber.GetValueOrDefault()
            };

            track.Artists = expandedArtists.ToList();
            track.Artist[MainArtistRole] = usePrimaryArtistFolders
                ? new List<string> { primaryArtistName }
                : track.Artists.ToList();
            if (usePrimaryArtistFolders && expandedArtists.Count > 1)
            {
                track.Artist[FeaturedArtistRole] = expandedArtists.Skip(1).ToList();
            }

            var album = new Album(albumTitle)
            {
                MainArtist = mainArtist,
                Label = recognition.Label ?? string.Empty
            };
            album.Artists = track.Artists.ToList();
            album.Artist[MainArtistRole] = usePrimaryArtistFolders
                ? new List<string> { primaryArtistName }
                : album.Artists.ToList();
            if (usePrimaryArtistFolders && expandedArtists.Count > 1)
            {
                album.Artist[FeaturedArtistRole] = expandedArtists.Skip(1).ToList();
            }

            ApplyRecognitionReleaseDate(recognition.ReleaseDate, track, album);

            track.Album = album;
            track.GenerateMainFeatStrings();
            return track;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AutoTag organizer Shazam fallback failed for {Path}", fullPath);
            log?.Invoke($"organizer Shazam fallback failed for {fullPath}: {ex.Message}");
            return null;
        }
    }

    private static List<string> ResolveExpandedRecognitionArtists(ShazamRecognitionInfo recognition)
    {
        var normalizedArtists = recognition.Artists
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedArtists.Count == 0 && !string.IsNullOrWhiteSpace(recognition.Artist))
        {
            normalizedArtists.Add(recognition.Artist.Trim());
        }

        return ExpandArtistNames(normalizedArtists);
    }

    private static void ApplyRecognitionReleaseDate(string? releaseDate, Track track, Album album)
    {
        if (string.IsNullOrWhiteSpace(releaseDate)
            || !DateTime.TryParse(releaseDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedReleaseDate))
        {
            return;
        }

        album.Date.Year = parsedReleaseDate.Year.ToString();
        album.Date.Month = parsedReleaseDate.Month.ToString("00");
        album.Date.Day = parsedReleaseDate.Day.ToString("00");
        track.Date = album.Date;
    }

    private static string[] GetRelativePathParts(string fullPath, string rootPath, out string? directory)
    {
        var normalizedRoot = Path.GetFullPath(rootPath);
        directory = Path.GetDirectoryName(Path.GetFullPath(fullPath));
        var relative = string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : Path.GetRelativePath(normalizedRoot, directory);

        if (string.IsNullOrWhiteSpace(relative))
        {
            return Array.Empty<string>();
        }

        return relative
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part) && !part.Equals(".", StringComparison.Ordinal))
            .ToArray();
    }

    private static string? ResolveFallbackArtist(string? directory)
    {
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : Directory.GetParent(directory)?.Name?.Trim();
    }

    private static string ResolveArtistCredit(string[] relativeParts, string? fallbackArtist)
    {
        if (relativeParts.Length >= 2)
        {
            return relativeParts[0].Trim();
        }

        return !string.IsNullOrWhiteSpace(fallbackArtist)
            ? fallbackArtist
            : "Unknown Artist";
    }

    private static string ResolveAlbumTitle(string[] relativeParts)
    {
        return relativeParts.Length >= 2
            ? relativeParts[1].Trim()
            : "Singles";
    }

    private static (int TrackNumber, string Title) ExtractTrackNumberAndTitle(string? fileStem)
    {
        var original = fileStem?.Trim() ?? string.Empty;
        var working = original;
        if (string.IsNullOrWhiteSpace(working))
        {
            return (0, string.Empty);
        }

        var lastTrackNumber = 0;
        var safety = 0;
        while (safety++ < 24)
        {
            var match = LeadingTrackTokenRegex.Match(working);
            if (!match.Success)
            {
                break;
            }

            if (int.TryParse(match.Groups["num"].Value, out var parsed))
            {
                lastTrackNumber = parsed;
            }

            working = working.Substring(match.Length).Trim();
            if (string.IsNullOrWhiteSpace(working))
            {
                break;
            }
        }

        return (lastTrackNumber, string.IsNullOrWhiteSpace(working) ? original : working);
    }

    private static string TryRecoverSingleTrackTitle(string title, string albumTitle)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(albumTitle))
        {
            return title;
        }

        var normalizedAlbum = albumTitle.Trim();
        if (normalizedAlbum.EndsWith(" - Single", StringComparison.OrdinalIgnoreCase))
        {
            normalizedAlbum = normalizedAlbum[..^9].Trim();
        }
        else if (normalizedAlbum.EndsWith("(Single)", StringComparison.OrdinalIgnoreCase))
        {
            normalizedAlbum = normalizedAlbum[..^8].Trim();
        }

        if (string.IsNullOrWhiteSpace(normalizedAlbum))
        {
            return title;
        }

        var trimmedTitle = title.Trim();
        var looksTruncatedFeaturing =
            trimmedTitle.EndsWith("(feat", StringComparison.OrdinalIgnoreCase) ||
            trimmedTitle.EndsWith("(ft", StringComparison.OrdinalIgnoreCase) ||
            trimmedTitle.EndsWith("(featuring", StringComparison.OrdinalIgnoreCase) ||
            CountChars(trimmedTitle, '(') > CountChars(trimmedTitle, ')');

        if (!looksTruncatedFeaturing)
        {
            return title;
        }

        return normalizedAlbum.StartsWith(trimmedTitle, StringComparison.OrdinalIgnoreCase)
            ? normalizedAlbum
            : title;
    }

    private static int CountChars(string text, char value)
    {
        return text.Count(c => c == value);
    }

    private static bool IsMissingCoreTags(Tag tag)
    {
        var hasAlbum = !string.IsNullOrWhiteSpace(tag.Album);
        if (!hasAlbum)
        {
            return true;
        }

        var hasAlbumArtist = tag.AlbumArtists != null && tag.AlbumArtists.Any(a => !string.IsNullOrWhiteSpace(a));
        var hasPerformer = tag.Performers != null && tag.Performers.Any(a => !string.IsNullOrWhiteSpace(a));
        var hasJoined = !string.IsNullOrWhiteSpace(tag.FirstPerformer) || !string.IsNullOrWhiteSpace(tag.JoinedPerformers);
        return !(hasAlbumArtist || hasPerformer || hasJoined);
    }

    private static string ExtractFirstMainArtist(string? credit)
    {
        return ArtistNameNormalizer.ExtractPrimaryArtist(credit);
    }

    private static List<string> ExpandArtistNames(IEnumerable<string> credits)
    {
        return ArtistNameNormalizer.ExpandArtistNames(credits);
    }

    private static string GetArtistSeparator(string? multiArtistSeparator)
    {
        if (string.IsNullOrWhiteSpace(multiArtistSeparator) || string.Equals(multiArtistSeparator, "default", StringComparison.OrdinalIgnoreCase))
        {
            return ", ";
        }

        return multiArtistSeparator;
    }

    private static bool IsVariousArtists(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return VariousArtistsTokens.Contains(name.Trim());
    }

    private static bool IsSourceDirectoryVariousArtists(string rootPath, string sourceDir)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(sourceDir));
        var relativeParts = relative
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        return relativeParts.Any(IsVariousArtists);
    }

    private static bool IsCompilationDirectory(string sourceDir)
    {
        var name = Path.GetFileName(sourceDir)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("compilation", StringComparison.OrdinalIgnoreCase)
            || name.Contains("compilations", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFullTrackSet(IEnumerable<string> files)
    {
        var trackNumbers = new HashSet<int>();
        var expectedTrackCount = 0;
        foreach (var path in files)
        {
            try
            {
                using var file = TagLib.File.Create(path);
                var tag = file.Tag;
                if (tag.Track > 0)
                {
                    trackNumbers.Add((int)tag.Track);
                }

                if (tag.TrackCount > 0)
                {
                    expectedTrackCount = Math.Max(expectedTrackCount, (int)tag.TrackCount);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return false;
            }
        }

        if (expectedTrackCount <= 0)
        {
            return false;
        }

        return trackNumbers.Count >= expectedTrackCount;
    }

    private static string SanitizePathSegment(string value)
    {
        return CjkFilenameSanitizer.SanitizeSegment(
            value,
            fallback: "Unknown",
            replacement: "_",
            collapseWhitespace: true,
            trimTrailingDotsAndSpaces: true);
    }

    private static string GetUniquePath(string path, string? sourcePath = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return path;
        }

        if (!IOFile.Exists(path))
        {
            return path;
        }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var candidate = Path.GetFullPath(path);
            var source = Path.GetFullPath(sourcePath);
            if (string.Equals(candidate, source, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            if (IOFile.Exists(sourcePath) && AudioCollisionDedupe.IsDuplicate(path, sourcePath))
            {
                return path;
            }
        }

        var filename = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var uniqueFilename = DownloadUtils.GenerateUniqueFilename(dir, filename, extension);
        return Path.Join(dir, uniqueFilename + extension);
    }

    private static string GetUniqueDirectoryPath(string path, string? sourcePath = null)
    {
        var parent = Path.GetDirectoryName(path);
        var folderName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(folderName))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            return path;
        }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var candidate = Path.GetFullPath(path);
            var source = Path.GetFullPath(sourcePath);
            if (string.Equals(candidate, source, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        var suffix = 1;
        var candidatePath = path;
        while (Directory.Exists(candidatePath))
        {
            candidatePath = Path.Join(parent, $"{folderName} ({suffix})");
            suffix++;
        }

        return candidatePath;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (IOFile.Exists(path))
            {
                IOFile.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup.
        }
    }

    private static void MoveFileOverwrite(string sourcePath, string destinationPath)
    {
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IOFile.Exists(destinationPath))
        {
            IOFile.Delete(destinationPath);
        }

        IOFile.Move(sourcePath, destinationPath);
    }

    private static IEnumerable<string> EnumerateAudioFiles(string rootPath, bool includeSubfolders)
    {
        if (!Directory.Exists(rootPath))
        {
            return Array.Empty<string>();
        }

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".flac", ".wav", ".aiff", ".aif", ".alac", ".m4a", ".m4b", ".mp4", ".aac", ".mp3",
            ".wma", ".ogg", ".opus", ".oga", ".ape", ".wv", ".mp2", ".mp1", ".tta", ".dsf", ".dff", ".mka"
        };

        var search = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(rootPath, "*.*", search)
            .Where(path => extensions.Contains(Path.GetExtension(path)))
            .Where(path => !IsAnimatedArtworkFile(path));
    }

    private static bool IsAnimatedArtworkFile(string path)
    {
        if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var filename = Path.GetFileNameWithoutExtension(path);
        return filename.Equals("square_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || filename.Equals("tall_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || filename.EndsWith(" - square_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || filename.EndsWith(" - tall_animated_artwork", StringComparison.OrdinalIgnoreCase);
    }

    private void MoveSidecarFiles(SidecarMoveContext context)
    {
        if (!Directory.Exists(context.SourceDir))
        {
            return;
        }

        var sourceBase = Path.GetFileNameWithoutExtension(context.SourcePath);
        var destinationBase = Path.GetFileNameWithoutExtension(context.DestinationPath);
        var movedAny = false;
        foreach (var file in Directory.EnumerateFiles(context.SourceDir))
        {
            if (string.Equals(file, context.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var baseName = Path.GetFileNameWithoutExtension(file);
            if (!string.Equals(baseName, sourceBase, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            movedAny |= TryMoveSidecarFile(context, file, destinationBase);
        }

        if (movedAny && !context.Options.DryRun && context.Options.RemoveEmptyFolders)
        {
            DeleteEmptyDirectoryTree(context.SourceDir, context.RootPath, context.Log);
        }
    }

    private bool TryMoveSidecarFile(SidecarMoveContext context, string sourcePath, string destinationBase)
    {
        var ext = Path.GetExtension(sourcePath);
        var candidate = Path.Join(context.DestinationDir, destinationBase + ext);
        var target = ResolveSidecarTarget(sourcePath, candidate, context.Options, context.Report, context.Log);
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (context.Options.DryRun)
        {
            context.Log?.Invoke($"organizer dry-run: would move sidecar {sourcePath} -> {target}");
            return false;
        }

        try
        {
            if (ShouldMergeLyricsSidecar(sourcePath, target, context.Options))
            {
                MergeLyricsSidecar(sourcePath, target);
                TryDeleteFile(sourcePath);
                if (context.Report != null)
                {
                    context.Report.MergedLyricsSidecars++;
                }

                context.Report?.Entries.Add($"merge-lyrics-sidecar: {sourcePath} -> {target}");
                context.Log?.Invoke($"organizer merged lyrics sidecar: {sourcePath} -> {target}");
                return true;
            }

            MoveFileOverwrite(sourcePath, target);
            if (context.Report != null)
            {
                context.Report.MovedSidecars++;
            }

            _logger.LogInformation("AutoTag organizer moved sidecar {SourcePath} -> {DestinationPath}", sourcePath, target);
            context.Log?.Invoke($"organizer moved sidecar: {sourcePath} -> {target}");
            context.Report?.Entries.Add($"move-sidecar: {sourcePath} -> {target}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordOrganizerFailure(
                context.Report,
                context.Log,
                "move sidecar",
                sourcePath,
                target,
                ex);
            return false;
        }
    }

    private void MoveRemainingFilesIfAlbumDone(string rootPath, string sourceDir, string destinationDir, Action<string>? log, AutoTagOrganizerOptions options, AutoTagOrganizerReport? report)
    {
        try
        {
            if (!Directory.Exists(sourceDir))
            {
                return;
            }

            var remainingAudio = EnumerateAudioFiles(sourceDir, true)
                .Any(path => string.Equals(Path.GetDirectoryName(path), sourceDir, StringComparison.OrdinalIgnoreCase));
            if (remainingAudio)
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var target = GetUniquePath(Path.Join(destinationDir, Path.GetFileName(file)), file);
                if (options.DryRun)
                {
                    log?.Invoke($"organizer dry-run: would move leftover {file} -> {target}");
                    continue;
                }

                try
                {
                    MoveFileOverwrite(file, target);
                    if (report != null)
                    {
                        report.MovedLeftovers++;
                    }

                    _logger.LogInformation("AutoTag organizer moved leftover {SourcePath} -> {DestinationPath}", file, target);
                    log?.Invoke($"organizer moved leftover: {file} -> {target}");
                    report?.Entries.Add($"move-leftover: {file} -> {target}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordOrganizerFailure(
                        report,
                        log,
                        "move leftover",
                        file,
                        target,
                        ex);
                }
            }

            if (!options.DryRun && options.RemoveEmptyFolders)
            {
                DeleteEmptyDirectoryTree(sourceDir, rootPath, log);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordOrganizerFailure(
                report,
                log,
                "move leftovers folder",
                sourceDir,
                destinationDir,
                ex);
        }
    }

    private void DeleteVariousArtistsArt(string sourceArtistDir, AutoTagOrganizerReport? report, Action<string>? log)
    {
        foreach (var file in Directory.EnumerateFiles(sourceArtistDir))
        {
            var ext = Path.GetExtension(file);
            if (!IsImageExtension(ext))
            {
                continue;
            }

            IOFile.Delete(file);
            _logger.LogInformation("AutoTag organizer deleted various artists art {SourcePath}", file);
            log?.Invoke($"organizer deleted various artists art: {file}");
            report?.Entries.Add($"delete-various-artists-art: {file}");
        }
    }

    private static bool IsImageExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private void ReconcileOrphanCombinedArtistFolders(
        string rootPath,
        bool usePrimaryArtistFolders,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        if (!usePrimaryArtistFolders || !options.MoveMisplacedFiles || !Directory.Exists(rootPath))
        {
            return;
        }

        foreach (var sourceArtistDir in GetTopLevelArtistDirectories(rootPath))
        {
            ReconcileOrphanCombinedArtistDirectory(rootPath, sourceArtistDir, options, report, log);
        }
    }

    private static List<string> GetTopLevelArtistDirectories(string rootPath)
    {
        try
        {
            return Directory.EnumerateDirectories(rootPath).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new List<string>();
        }
    }

    private void ReconcileOrphanCombinedArtistDirectory(
        string rootPath,
        string sourceArtistDir,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        try
        {
            var artistFolderName = Path.GetFileName(sourceArtistDir)?.Trim();
            if (string.IsNullOrWhiteSpace(artistFolderName))
            {
                return;
            }

            var expandedArtists = ExpandArtistNames(new[] { artistFolderName });
            if (expandedArtists.Count <= 1)
            {
                return;
            }

            var primaryArtist = expandedArtists[0];
            var targetArtistDir = ResolvePrimaryArtistDirectory(rootPath, primaryArtist);
            if (string.Equals(sourceArtistDir, targetArtistDir, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var albumDir in Directory.EnumerateDirectories(sourceArtistDir).ToList())
            {
                ReconcileOrphanAlbumDirectory(rootPath, albumDir, targetArtistDir, options, report, log);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordOrganizerFailure(
                report,
                log,
                "reconcile orphan artist folder",
                sourceArtistDir,
                destinationPath: null,
                ex);
        }
    }

    private void ReconcileOrphanAlbumDirectory(
        string rootPath,
        string albumDir,
        string targetArtistDir,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        if (DirectoryContainsAudio(albumDir))
        {
            return;
        }

        var albumFolderName = Path.GetFileName(albumDir);
        if (string.IsNullOrWhiteSpace(albumFolderName))
        {
            return;
        }

        var targetAlbumDir = Path.Join(targetArtistDir, albumFolderName);
        if (options.DryRun)
        {
            log?.Invoke($"organizer dry-run: would reconcile orphan combined-artist folder {albumDir} -> {targetAlbumDir}");
            return;
        }

        try
        {
            ReconcileOrphanAlbumDirectoryCore(rootPath, albumDir, targetArtistDir, targetAlbumDir, options, report, log);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag organizer failed reconciling orphan combined-artist folder {SourceDir}", albumDir);
            log?.Invoke($"organizer failed reconciling orphan combined-artist folder: {albumDir} ({ex.Message})");
        }
    }

    private void ReconcileOrphanAlbumDirectoryCore(
        string rootPath,
        string albumDir,
        string targetArtistDir,
        string targetAlbumDir,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        Directory.CreateDirectory(targetArtistDir);
        if (!Directory.Exists(targetAlbumDir))
        {
            Directory.Move(albumDir, targetAlbumDir);
            if (report != null)
            {
                report.MovedFolders++;
            }

            log?.Invoke($"organizer reconciled orphan combined-artist folder: {albumDir} -> {targetAlbumDir}");
            report?.Entries.Add($"reconcile-orphan-folder: {albumDir} -> {targetAlbumDir}");
            return;
        }

        var movedFiles = MoveOrphanAlbumFiles(albumDir, targetAlbumDir, report, log);
        if (report != null && movedFiles > 0)
        {
            report.MovedLeftovers += movedFiles;
        }

        if (options.RemoveEmptyFolders)
        {
            DeleteEmptyDirectoryTree(albumDir, rootPath, log);
        }
    }

    private static int MoveOrphanAlbumFiles(
        string albumDir,
        string targetAlbumDir,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        var movedFiles = 0;
        foreach (var sourceFile in Directory.EnumerateFiles(albumDir, "*.*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(albumDir, sourceFile);
            var destinationFile = Path.Join(targetAlbumDir, relativePath);
            var destinationDir = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            destinationFile = GetUniquePath(destinationFile, sourceFile);
            MoveFileOverwrite(sourceFile, destinationFile);
            movedFiles++;
            log?.Invoke($"organizer moved orphan sidecar: {sourceFile} -> {destinationFile}");
            report?.Entries.Add($"move-orphan-sidecar: {sourceFile} -> {destinationFile}");
        }

        return movedFiles;
    }

    private static bool DirectoryContainsAudio(string directoryPath)
    {
        return EnumerateAudioFiles(directoryPath, includeSubfolders: true).Any();
    }

    private static string ResolvePrimaryArtistDirectory(string rootPath, string primaryArtist)
    {
        foreach (var existing in Directory.EnumerateDirectories(rootPath))
        {
            var name = Path.GetFileName(existing);
            if (string.Equals(name, primaryArtist, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
        }

        return Path.Join(rootPath, SanitizePathSegment(primaryArtist));
    }

    private static bool IsLyricsExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".srt", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveSidecarTarget(
        string sourcePath,
        string candidatePath,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        if (!IOFile.Exists(candidatePath))
        {
            return GetUniquePath(candidatePath, sourcePath);
        }

        var extension = Path.GetExtension(sourcePath);
        if (IsImageExtension(extension))
        {
            return ResolveImageSidecarTarget(sourcePath, candidatePath, options, report, log);
        }

        if (IsLyricsExtension(extension))
        {
            return ResolveLyricsSidecarTarget(sourcePath, candidatePath, options, report, log);
        }

        return options.KeepBothOnUnresolvedConflicts
            ? GetUniquePath(candidatePath, sourcePath)
            : null;
    }

    private static string? ResolveImageSidecarTarget(
        string sourcePath,
        string candidatePath,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        if (string.Equals(options.ArtworkPolicy, AutoTagOrganizerOptions.ArtworkPolicyPreserveExisting, StringComparison.OrdinalIgnoreCase))
        {
            RecordPreservedArtwork(report, sourcePath, candidatePath);
            log?.Invoke($"organizer preserved existing artwork: {candidatePath}");
            return null;
        }

        if (!string.Equals(options.ArtworkPolicy, AutoTagOrganizerOptions.ArtworkPolicyPreferHigherResolution, StringComparison.OrdinalIgnoreCase))
        {
            return GetUniquePath(candidatePath, sourcePath);
        }

        var incomingScore = TryGetArtworkScore(sourcePath);
        var existingScore = TryGetArtworkScore(candidatePath);
        if (existingScore >= incomingScore)
        {
            RecordPreservedArtwork(report, sourcePath, candidatePath, "higher/equal existing");
            log?.Invoke($"organizer preserved higher-resolution artwork: {candidatePath}");
            return null;
        }

        return candidatePath;
    }

    private static string? ResolveLyricsSidecarTarget(
        string sourcePath,
        string candidatePath,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report,
        Action<string>? log)
    {
        if (string.Equals(options.LyricsPolicy, AutoTagOrganizerOptions.LyricsPolicyPreserveExisting, StringComparison.OrdinalIgnoreCase))
        {
            report?.Entries.Add($"preserve-lyrics: {sourcePath} (existing {candidatePath})");
            log?.Invoke($"organizer preserved existing lyrics sidecar: {candidatePath}");
            return null;
        }

        return candidatePath;
    }

    private static void RecordPreservedArtwork(
        AutoTagOrganizerReport? report,
        string sourcePath,
        string candidatePath,
        string? reason = null)
    {
        if (report != null)
        {
            report.PreservedExistingArtwork++;
        }

        var reasonSuffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"{reason} ";
        report?.Entries.Add($"preserve-artwork: {sourcePath} ({reasonSuffix}existing {candidatePath})");
    }

    private static bool ShouldMergeLyricsSidecar(string sourcePath, string destinationPath, AutoTagOrganizerOptions options)
    {
        return IOFile.Exists(destinationPath)
            && IsLyricsExtension(Path.GetExtension(sourcePath))
            && string.Equals(options.LyricsPolicy, AutoTagOrganizerOptions.LyricsPolicyMerge, StringComparison.OrdinalIgnoreCase);
    }

    private static void MergeLyricsSidecar(string sourcePath, string destinationPath)
    {
        var destinationLines = IOFile.ReadAllLines(destinationPath)
            .Select(line => line.TrimEnd())
            .ToList();
        var existing = new HashSet<string>(destinationLines, StringComparer.OrdinalIgnoreCase);
        foreach (var line in IOFile.ReadLines(sourcePath)
                     .Select(line => line.TrimEnd())
                     .Where(line => existing.Add(line)))
        {
            destinationLines.Add(line);
        }

        IOFile.WriteAllLines(destinationPath, destinationLines);
    }

    private static long TryGetArtworkScore(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    private void DeleteEmptyDirectoryTree(string startDirectory, string rootPath, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(startDirectory) || string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        var current = Path.GetFullPath(startDirectory);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(current)
               && IsPathUnderRoot(current, root)
               && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current) ?? string.Empty;
                continue;
            }

            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                break;
            }

            try
            {
                Directory.Delete(current);
                _logger.LogInformation("AutoTag organizer deleted empty folder {SourceDir}", current);
                log?.Invoke($"organizer deleted empty folder: {current}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "AutoTag organizer failed deleting empty folder {SourceDir}", current);
                log?.Invoke($"organizer failed deleting empty folder: {current} ({ex.Message})");
                break;
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }
    }

    private void CleanupArtistFolders(string rootPath, bool usePrimaryArtistFolders, AutoTagOrganizerReport? report, Action<string>? log)
    {
        foreach (var artistDir in Directory.EnumerateDirectories(rootPath))
        {
            try
            {
                if (EnumerateAudioFiles(artistDir, true).Any() || !TryGetArtistName(artistDir, out var artistName))
                {
                    continue;
                }

                if (HandleVariousArtistsFolderCleanup(artistDir, artistName, report, log))
                {
                    continue;
                }

                if (usePrimaryArtistFolders)
                {
                    MovePrimaryArtistArtwork(rootPath, artistDir, artistName, log);
                }

                DeleteArtistFolderIfEmpty(artistDir, log);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordOrganizerFailure(
                    report,
                    log,
                    "cleanup artist folder",
                    artistDir,
                    destinationPath: null,
                    ex);
            }
        }
    }

    private static bool TryGetArtistName(string artistDir, out string artistName)
    {
        artistName = Path.GetFileName(artistDir);
        return !string.IsNullOrWhiteSpace(artistName);
    }

    private bool HandleVariousArtistsFolderCleanup(string artistDir, string artistName, AutoTagOrganizerReport? report, Action<string>? log)
    {
        if (!IsVariousArtists(artistName))
        {
            return false;
        }

        DeleteVariousArtistsArt(artistDir, report, log);
        DeleteArtistFolderIfEmpty(artistDir, log);
        return true;
    }

    private void MovePrimaryArtistArtwork(string rootPath, string artistDir, string artistName, Action<string>? log)
    {
        var primary = ExtractFirstMainArtist(artistName);
        if (string.IsNullOrWhiteSpace(primary) || string.Equals(primary, artistName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var destinationArtistDir = Path.Join(rootPath, SanitizePathSegment(primary));
        Directory.CreateDirectory(destinationArtistDir);
        foreach (var file in Directory.EnumerateFiles(artistDir))
        {
            if (!IsImageExtension(Path.GetExtension(file)))
            {
                continue;
            }

            var target = GetUniquePath(Path.Join(destinationArtistDir, Path.GetFileName(file)), file);
            MoveFileOverwrite(file, target);
            _logger.LogInformation("AutoTag organizer moved artist file SourcePath -> DestinationPath");
            log?.Invoke($"organizer moved artist file: {file} -> {target}");
        }
    }

    private void DeleteArtistFolderIfEmpty(string artistDir, Action<string>? log)
    {
        if (Directory.EnumerateFileSystemEntries(artistDir).Any())
        {
            return;
        }

        Directory.Delete(artistDir);
        _logger.LogInformation("AutoTag organizer deleted empty artist folder {SourceDir}", artistDir);
        log?.Invoke($"organizer deleted empty artist folder: {artistDir}");
    }

    private void RecordOrganizerFailure(
        AutoTagOrganizerReport? report,
        Action<string>? log,
        string action,
        string sourcePath,
        string? destinationPath,
        Exception ex)
    {
        if (report != null)
        {
            report.SkippedConflicts++;
        }

        var logLine = string.IsNullOrWhiteSpace(destinationPath)
            ? $"organizer failed to {action}: {sourcePath} ({ex.Message})"
            : $"organizer failed to {action}: {sourcePath} -> {destinationPath} ({ex.Message})";
        _logger.LogWarning(ex, "AutoTag organizer failed to {Action} {SourcePath} -> {DestinationPath}", action, sourcePath, destinationPath);
        log?.Invoke(logLine);
        report?.Entries.Add(logLine);
    }

}
