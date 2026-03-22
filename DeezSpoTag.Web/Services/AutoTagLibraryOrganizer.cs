using System.Linq;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using TagLib;
using IOFile = System.IO.File;

#pragma warning disable CA1847
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
        if (string.IsNullOrWhiteSpace(rootPath) || filePaths.Count == 0)
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
        if (report != null)
        {
            report.PlannedMoves += plan.Count;
        }

        ExecuteMovePlan(normalizedRoot, plan, options, report, log);
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

    private static IReadOnlyDictionary<string, SourceDirectoryPolicy> BuildSourceDirectoryPolicies(
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
        foreach (var filePath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            if (TryBuildMovePlanItem(
                    rootPath,
                    options,
                    settings,
                    usePrimaryArtistFolders,
                    sourcePolicies,
                    filePath,
                    report,
                    log,
                    out var item))
            {
                results.Add(item);
            }
        }

        return results;
    }

    private bool TryBuildMovePlanItem(
        string rootPath,
        AutoTagOrganizerOptions options,
        DeezSpoTagSettings settings,
        bool usePrimaryArtistFolders,
        IReadOnlyDictionary<string, SourceDirectoryPolicy> sourcePolicies,
        string filePath,
        AutoTagOrganizerReport? report,
        Action<string>? log,
        out MovePlanItem item)
    {
        item = default!;
        try
        {
            var fullPath = ResolveMovePlanSourcePath(filePath, rootPath);
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }

            var sourceDir = Path.GetDirectoryName(fullPath) ?? rootPath;
            if (sourcePolicies.TryGetValue(sourceDir, out var sourcePolicy) && !sourcePolicy.ShouldProcess)
            {
                RegisterSourceDirectorySkip(sourcePolicy.Reason, sourceDir, report, log);
                return false;
            }

            var target = ResolveMovePlanTarget(
                fullPath,
                rootPath,
                options,
                settings,
                usePrimaryArtistFolders,
                log);
            if (target is null)
            {
                return false;
            }

            var normalized = ApplyMovePlanOptions(fullPath, target, options);
            if (normalized is null)
            {
                return false;
            }

            if (target.IsUntagged)
            {
                if (report != null)
                {
                    report.SkippedUntagged++;
                }
            }

            item = normalized;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag organizer failed for {Path}", filePath);
            log?.Invoke($"organizer failed: {filePath} ({ex.GetType().Name}: {ex.Message})");
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
        Track? track;
        string downloadType;
        if (IsMissingCoreTags(tag))
        {
            if (context.Options.UseShazamForUntaggedFiles)
            {
                track = BuildTrackFromShazamRecognition(
                    context.FullPath,
                    context.UsePrimaryArtistFolders,
                    context.Settings.Tags?.MultiArtistSeparator,
                    context.Log);
                if (track == null)
                {
                    context.Log?.Invoke($"organizer left untagged file in place after Shazam produced no usable match: {context.FullPath}");
                    return null;
                }
            }
            else
            {
                track = BuildTrackFromPathFallback(
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
                        "organizer skipped missing core tags");
                }
            }

            downloadType = IsSingleTrackDownload(track) ? "track" : "album";
            context.Log?.Invoke(context.Options.UseShazamForUntaggedFiles
                ? $"organizer inferred core tags from Shazam: {context.FullPath}"
                : $"organizer inferred core tags from path: {context.FullPath}");
        }
        else
        {
            track = BuildTrackFromTag(
                tag,
                context.FullPath,
                context.UsePrimaryArtistFolders,
                context.Settings.Tags?.MultiArtistSeparator);
            downloadType = string.IsNullOrWhiteSpace(tag.Album) ? "track" : "album";
        }

        track.ApplySettings(context.Settings);
        var pathProcessor = new EnhancedPathTemplateProcessor(_loggerFactory.CreateLogger<EnhancedPathTemplateProcessor>());
        var pathResult = pathProcessor.GeneratePaths(track, downloadType, context.Settings);

        var destinationIoDir = DownloadPathResolver.ResolveIoPath(pathResult.FilePath);
        var destinationDir = string.IsNullOrWhiteSpace(destinationIoDir)
            ? context.RootPath
            : destinationIoDir;
        var extension = Path.GetExtension(context.FullPath);
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

    private static bool IsSingleTrackDownload(Track track)
    {
        return string.IsNullOrWhiteSpace(track.Album?.Title)
            || string.Equals(track.Album.Title, "Singles", StringComparison.OrdinalIgnoreCase);
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

    private void ExecuteMovePlan(string rootPath, List<MovePlanItem> plan, AutoTagOrganizerOptions options, AutoTagOrganizerReport? report, Action<string>? log)
    {
        if (plan.Count == 0)
        {
            return;
        }

        var actionsBySourceDir = plan
            .GroupBy(item => item.SourceDir, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in actionsBySourceDir)
        {
            var sourceDir = group.Key;
            var actions = group.ToList();
            var destinationDirs = actions
                .Select(item => item.DestinationDir)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (options.MoveMisplacedFiles
                && destinationDirs.Count == 1
                && Directory.Exists(destinationDirs[0])
                && !options.MergeIntoExistingDestinationFolders)
            {
                if (report != null)
                {
                    report.SkippedExistingFolderMerges++;
                }
                log?.Invoke($"organizer skipped merge into existing destination folder: {sourceDir} -> {destinationDirs[0]}");
                report?.Entries.Add($"skip: existing destination folder merge -> {sourceDir} -> {destinationDirs[0]}");
                continue;
            }

            if (options.MoveMisplacedFiles
                && destinationDirs.Count == 1
                && TryMoveFolder(rootPath, sourceDir, destinationDirs[0], options, report, log))
            {
                continue;
            }

            foreach (var action in actions)
            {
                MoveSingleFile(rootPath, action, options, report, log);
            }

            var destinationDir = destinationDirs.FirstOrDefault();
            if (options.MoveMisplacedFiles && !string.IsNullOrWhiteSpace(destinationDir))
            {
                MoveRemainingFilesIfAlbumDone(rootPath, sourceDir, destinationDir, log, options, report);
            }
        }
    }

    private bool TryMoveFolder(string rootPath, string sourceDir, string destinationDir, AutoTagOrganizerOptions options, AutoTagOrganizerReport? report, Action<string>? log)
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

        Directory.CreateDirectory(Path.GetDirectoryName(destinationDir) ?? destinationDir);
        Directory.Move(sourceDir, destinationDir);
        if (report != null)
        {
            report.MovedFolders++;
        }
        _logger.LogInformation("AutoTag organizer moved folder {SourceDir} -> {DestinationDir}", sourceDir, destinationDir);
        log?.Invoke($"organizer moved folder: {sourceDir} -> {destinationDir}");
        report?.Entries.Add($"move-folder: {sourceDir} -> {destinationDir}");
        return true;
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

    private void MoveSingleFile(string rootPath, MovePlanItem action, AutoTagOrganizerOptions options, AutoTagOrganizerReport? report, Action<string>? log) // NOSONAR
    {
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

        Directory.CreateDirectory(action.DestinationDir);
        MoveFileOverwrite(action.SourcePath, action.DestinationPath);
        if (report != null)
        {
            report.MovedFiles++;
        }
        _logger.LogInformation("AutoTag organizer moved file {SourcePath} -> {DestinationPath}", action.SourcePath, action.DestinationPath);
        log?.Invoke($"organizer moved file: {action.SourcePath} -> {action.DestinationPath}");
        report?.Entries.Add($"move-file: {action.SourcePath} -> {action.DestinationPath}");
        MoveSidecarFiles(rootPath, action.SourceDir, action.DestinationDir, action.SourcePath, action.DestinationPath, log, options, report);
        CleanupSourceDirectoryIfConfigured(rootPath, action.SourceDir, options, log);
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
            action.DestinationPath = GetUniquePath(action.DestinationPath, action.SourcePath);
            log?.Invoke($"organizer kept duplicate due to disabled conflict resolution: {action.SourcePath}");
            if (report != null)
            {
                report.KeptLowerQualityDuplicates++;
            }
            report?.Entries.Add($"keep-both-duplicate: {action.SourcePath} -> {action.DestinationPath}");
            return false;
        }

        if (string.Equals(options.DuplicateConflictPolicy, AutoTagOrganizerOptions.DuplicateConflictKeepBoth, StringComparison.OrdinalIgnoreCase))
        {
            action.DestinationPath = GetUniquePath(action.DestinationPath, action.SourcePath);
            log?.Invoke($"organizer kept both duplicates: {action.SourcePath} -> {action.DestinationPath}");
            if (report != null)
            {
                report.KeptLowerQualityDuplicates++;
            }
            report?.Entries.Add($"keep-both-duplicate: {action.SourcePath} -> {action.DestinationPath}");
            return false;
        }

        if (string.Equals(options.DuplicateConflictPolicy, AutoTagOrganizerOptions.DuplicateConflictKeepLower, StringComparison.OrdinalIgnoreCase))
        {
            action.DestinationPath = GetUniquePath(action.DestinationPath, action.SourcePath);
            log?.Invoke($"organizer preserved lower-quality duplicate: {action.SourcePath} -> {action.DestinationPath}");
            if (report != null)
            {
                report.KeptLowerQualityDuplicates++;
            }
            report?.Entries.Add($"keep-lower-duplicate: {action.SourcePath} -> {action.DestinationPath}");
            return false;
        }

        if (options.DryRun)
        {
            if (preferIncoming)
            {
                log?.Invoke($"organizer dry-run: would replace duplicate destination {action.DestinationPath} using {action.SourcePath}");
            }
            else
            {
                log?.Invoke($"organizer dry-run: would skip duplicate file {action.SourcePath} (already exists at {action.DestinationPath})");
            }
            return true;
        }

        if (string.Equals(options.DuplicateConflictPolicy, AutoTagOrganizerOptions.DuplicateConflictMoveToDuplicates, StringComparison.OrdinalIgnoreCase))
        {
            var losingPath = preferIncoming ? action.DestinationPath : action.SourcePath;
            var duplicatesRoot = Path.Join(rootPath, options.DuplicatesFolderName);
            Directory.CreateDirectory(duplicatesRoot);
            var target = GetUniquePath(Path.Join(duplicatesRoot, Path.GetFileName(losingPath)), losingPath);
            MoveFileOverwrite(losingPath, target);
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

            MoveSidecarFiles(rootPath, action.SourceDir, action.DestinationDir, action.SourcePath, action.DestinationPath, log, options, report);
            CleanupSourceDirectoryIfConfigured(rootPath, action.SourceDir, options, log);
            return true;
        }

        if (preferIncoming)
        {
            MoveFileOverwrite(action.SourcePath, action.DestinationPath);
            if (report != null)
            {
                report.ReplacedDuplicates++;
            }
            _logger.LogInformation("AutoTag organizer replaced duplicate destination {DestinationPath} using {SourcePath}", action.DestinationPath, action.SourcePath);
            log?.Invoke($"organizer replaced duplicate destination: {action.DestinationPath} using {action.SourcePath}");
            report?.Entries.Add($"replace-duplicate: {action.DestinationPath} <= {action.SourcePath}");
        }
        else
        {
            TryDeleteFile(action.SourcePath);
            _logger.LogInformation("AutoTag organizer skipped duplicate source {SourcePath} (existing {DestinationPath})", action.SourcePath, action.DestinationPath);
            log?.Invoke($"organizer skipped duplicate: {action.SourcePath} (existing {action.DestinationPath})");
            report?.Entries.Add($"skip-duplicate: {action.SourcePath} (existing {action.DestinationPath})");
        }

        MoveSidecarFiles(rootPath, action.SourceDir, action.DestinationDir, action.SourcePath, action.DestinationPath, log, options, report);
        CleanupSourceDirectoryIfConfigured(rootPath, action.SourceDir, options, log);
        return true;
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

    private static Track? BuildTrackFromPathFallback( // NOSONAR
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

            var normalizedArtists = recognition.Artists
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedArtists.Count == 0 && !string.IsNullOrWhiteSpace(recognition.Artist))
            {
                normalizedArtists.Add(recognition.Artist.Trim());
            }

            var expandedArtists = ExpandArtistNames(normalizedArtists);
            if (expandedArtists.Count == 0)
            {
                return null;
            }

            var albumTitle = string.IsNullOrWhiteSpace(recognition.Album)
                ? "Singles"
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

            if (!string.IsNullOrWhiteSpace(recognition.ReleaseDate)
                && DateTime.TryParse(recognition.ReleaseDate, out var parsedReleaseDate))
            {
                album.Date.Year = parsedReleaseDate.Year.ToString();
                album.Date.Month = parsedReleaseDate.Month.ToString("00");
                album.Date.Day = parsedReleaseDate.Day.ToString("00");
                track.Date = album.Date;
            }

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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (IOFile.Exists(path))
            {
                IOFile.Delete(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { // NOSONAR
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

    private void MoveSidecarFiles(
        string rootPath,
        string sourceDir,
        string destinationDir,
        string sourcePath,
        string destinationPath,
        Action<string>? log,
        AutoTagOrganizerOptions options,
        AutoTagOrganizerReport? report)
    {
        var sourceBase = Path.GetFileNameWithoutExtension(sourcePath);
        var destinationBase = Path.GetFileNameWithoutExtension(destinationPath);
        var movedAny = false;
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            if (string.Equals(file, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var baseName = Path.GetFileNameWithoutExtension(file);
            if (!string.Equals(baseName, sourceBase, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ext = Path.GetExtension(file);
            var candidate = Path.Join(destinationDir, destinationBase + ext);
            var target = ResolveSidecarTarget(file, candidate, options, report, log);
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            if (options.DryRun)
            {
                log?.Invoke($"organizer dry-run: would move sidecar {file} -> {target}");
                continue;
            }

            if (ShouldMergeLyricsSidecar(file, target, options))
            {
                MergeLyricsSidecar(file, target);
                TryDeleteFile(file);
                if (report != null)
                {
                    report.MergedLyricsSidecars++;
                }
                report?.Entries.Add($"merge-lyrics-sidecar: {file} -> {target}");
                log?.Invoke($"organizer merged lyrics sidecar: {file} -> {target}");
                movedAny = true;
                continue;
            }

            MoveFileOverwrite(file, target);
            if (report != null)
            {
                report.MovedSidecars++;
            }
            _logger.LogInformation("AutoTag organizer moved sidecar {SourcePath} -> {DestinationPath}", file, target);
            log?.Invoke($"organizer moved sidecar: {file} -> {target}");
            report?.Entries.Add($"move-sidecar: {file} -> {target}");
            movedAny = true;
        }

        if (movedAny && !options.DryRun && options.RemoveEmptyFolders)
        {
            DeleteEmptyDirectoryTree(sourceDir, rootPath, log);
        }
    }

    private void MoveRemainingFilesIfAlbumDone(string rootPath, string sourceDir, string destinationDir, Action<string>? log, AutoTagOrganizerOptions options, AutoTagOrganizerReport? report)
    {
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

            MoveFileOverwrite(file, target);
            if (report != null)
            {
                report.MovedLeftovers++;
            }
            _logger.LogInformation("AutoTag organizer moved leftover {SourcePath} -> {DestinationPath}", file, target);
            log?.Invoke($"organizer moved leftover: {file} -> {target}");
            report?.Entries.Add($"move-leftover: {file} -> {target}");
        }

        if (!options.DryRun && options.RemoveEmptyFolders)
        {
            DeleteEmptyDirectoryTree(sourceDir, rootPath, log);
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

    private string? ResolveSidecarTarget(
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
            if (string.Equals(options.ArtworkPolicy, AutoTagOrganizerOptions.ArtworkPolicyPreserveExisting, StringComparison.OrdinalIgnoreCase))
            {
                if (report != null)
                {
                    report.PreservedExistingArtwork++;
                }
                report?.Entries.Add($"preserve-artwork: {sourcePath} (existing {candidatePath})");
                log?.Invoke($"organizer preserved existing artwork: {candidatePath}");
                return null;
            }

            if (string.Equals(options.ArtworkPolicy, AutoTagOrganizerOptions.ArtworkPolicyPreferHigherResolution, StringComparison.OrdinalIgnoreCase))
            {
                var incomingScore = TryGetArtworkScore(sourcePath);
                var existingScore = TryGetArtworkScore(candidatePath);
                if (existingScore >= incomingScore)
                {
                    if (report != null)
                    {
                        report.PreservedExistingArtwork++;
                    }
                    report?.Entries.Add($"preserve-artwork: {sourcePath} (higher/equal existing {candidatePath})");
                    log?.Invoke($"organizer preserved higher-resolution artwork: {candidatePath}");
                    return null;
                }

                return candidatePath;
            }

            return GetUniquePath(candidatePath, sourcePath);
        }

        if (IsLyricsExtension(extension))
        {
            if (string.Equals(options.LyricsPolicy, AutoTagOrganizerOptions.LyricsPolicyPreserveExisting, StringComparison.OrdinalIgnoreCase))
            {
                report?.Entries.Add($"preserve-lyrics: {sourcePath} (existing {candidatePath})");
                log?.Invoke($"organizer preserved existing lyrics sidecar: {candidatePath}");
                return null;
            }

            if (string.Equals(options.LyricsPolicy, AutoTagOrganizerOptions.LyricsPolicyPreferIncoming, StringComparison.OrdinalIgnoreCase))
            {
                return candidatePath;
            }

            return candidatePath;
        }

        return options.KeepBothOnUnresolvedConflicts
            ? GetUniquePath(candidatePath, sourcePath)
            : null;
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
        foreach (var line in IOFile.ReadLines(sourcePath).Select(line => line.TrimEnd()))
        {
            if (existing.Add(line))
            {
                destinationLines.Add(line);
            }
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

            Directory.Delete(current);
            _logger.LogInformation("AutoTag organizer deleted empty folder {SourceDir}", current);
            log?.Invoke($"organizer deleted empty folder: {current}");
            current = Path.GetDirectoryName(current) ?? string.Empty;
        }
    }

    private void CleanupArtistFolders(string rootPath, bool usePrimaryArtistFolders, AutoTagOrganizerReport? report, Action<string>? log) // NOSONAR
    {
        foreach (var artistDir in Directory.EnumerateDirectories(rootPath))
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

}
