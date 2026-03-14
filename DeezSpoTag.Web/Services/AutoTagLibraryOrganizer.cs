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
    private const string MainArtistRole = "Main";
    private const string FeaturedArtistRole = "Featured";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly ILogger<AutoTagLibraryOrganizer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeezSpoTagSettingsService _settingsService;

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
        DeezSpoTagSettingsService settingsService)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _settingsService = settingsService;
    }

    public Task OrganizeAsync(string rootPath, IReadOnlyCollection<string> filePaths, Action<string>? log = null)
    {
        return OrganizeAsync(rootPath, filePaths, new AutoTagOrganizerOptions(), log);
    }

    public Task OrganizePathAsync(string rootPath, Action<string>? log = null)
    {
        return OrganizePathAsync(rootPath, new AutoTagOrganizerOptions(), log);
    }

    public Task OrganizePathAsync(string rootPath, AutoTagOrganizerOptions options, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Task.CompletedTask;
        }

        var normalizedRoot = Path.GetFullPath(rootPath);
        var files = EnumerateAudioFiles(normalizedRoot, options.IncludeSubfolders).ToList();
        return OrganizeAsync(normalizedRoot, files, options, log);
    }

    private Task OrganizeAsync(string rootPath, IReadOnlyCollection<string> filePaths, AutoTagOrganizerOptions options, Action<string>? log)
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

        if (!string.IsNullOrWhiteSpace(options.MultiArtistSeparatorOverride))
        {
            settings.Tags.MultiArtistSeparator = options.MultiArtistSeparatorOverride!.Trim();
        }

        var usePrimaryArtistFolders = options.UsePrimaryArtistFoldersOverride
            ?? settings.Tags.SingleAlbumArtist;
        var plan = BuildMovePlan(normalizedRoot, filePaths, options, settings, usePrimaryArtistFolders, log);
        ExecuteMovePlan(normalizedRoot, plan, options, log);
        if (!options.DryRun && options.RemoveEmptyFolders)
        {
            CleanupArtistFolders(normalizedRoot, usePrimaryArtistFolders, log);
        }
        return Task.CompletedTask;
    }

    private sealed class MovePlanItem
    {
        public required string SourcePath { get; init; }
        public required string SourceDir { get; init; }
        public required string DestinationPath { get; init; }
        public required string DestinationDir { get; init; }
        public bool IsUntagged { get; init; }
    }

    private sealed record MovePlanTargetContext(
        string FullPath,
        string RootPath,
        string SourceDir,
        DeezSpoTagSettings Settings,
        bool UsePrimaryArtistFolders,
        AutoTagOrganizerOptions Options,
        Action<string>? Log);

    private List<MovePlanItem> BuildMovePlan(
        string rootPath,
        IReadOnlyCollection<string> filePaths,
        AutoTagOrganizerOptions options,
        DeezSpoTagSettings settings,
        bool usePrimaryArtistFolders,
        Action<string>? log)
    {
        var results = new List<MovePlanItem>();
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
                    filePath,
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
        string filePath,
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

            downloadType = IsSingleTrackDownload(track) ? "track" : "album";
            context.Log?.Invoke($"organizer inferred core tags from path: {context.FullPath}");
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

    private void ExecuteMovePlan(string rootPath, List<MovePlanItem> plan, AutoTagOrganizerOptions options, Action<string>? log)
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
                && TryMoveFolder(rootPath, sourceDir, destinationDirs[0], options, log))
            {
                continue;
            }

            foreach (var action in actions)
            {
                MoveSingleFile(rootPath, action, options, log);
            }

            var destinationDir = destinationDirs.FirstOrDefault();
            if (options.MoveMisplacedFiles && !string.IsNullOrWhiteSpace(destinationDir))
            {
                MoveRemainingFilesIfAlbumDone(rootPath, sourceDir, destinationDir, log, options);
            }
        }
    }

    private bool TryMoveFolder(string rootPath, string sourceDir, string destinationDir, AutoTagOrganizerOptions options, Action<string>? log)
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
        _logger.LogInformation("AutoTag organizer moved folder {SourceDir} -> {DestinationDir}", sourceDir, destinationDir);
        log?.Invoke($"organizer moved folder: {sourceDir} -> {destinationDir}");
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

    private void MoveSingleFile(string rootPath, MovePlanItem action, AutoTagOrganizerOptions options, Action<string>? log) // NOSONAR
    {
        if (HandleDuplicateFileCollision(rootPath, action, options, log))
        {
            return;
        }

        if (options.PreferredExtensions.Count > 0
            && IOFile.Exists(action.DestinationPath)
            && PreferredExtensionComparer.ShouldSkipForPreferredExtension(action.SourcePath, action.DestinationPath, options.PreferredExtensions))
        {
            log?.Invoke($"organizer skipped (preferred format exists): {action.SourcePath}");
            return;
        }

        if (options.DryRun)
        {
            log?.Invoke($"organizer dry-run: would move file {action.SourcePath} -> {action.DestinationPath}");
            return;
        }

        Directory.CreateDirectory(action.DestinationDir);
        MoveFileOverwrite(action.SourcePath, action.DestinationPath);
        _logger.LogInformation("AutoTag organizer moved file {SourcePath} -> {DestinationPath}", action.SourcePath, action.DestinationPath);
        log?.Invoke($"organizer moved file: {action.SourcePath} -> {action.DestinationPath}");
        MoveSidecarFiles(rootPath, action.SourceDir, action.DestinationDir, action.SourcePath, action.DestinationPath, log, options);
        CleanupSourceDirectoryIfConfigured(rootPath, action.SourceDir, options, log);
    }

    private bool HandleDuplicateFileCollision(
        string rootPath,
        MovePlanItem action,
        AutoTagOrganizerOptions options,
        Action<string>? log)
    {
        if (!IOFile.Exists(action.DestinationPath)
            || string.Equals(action.SourcePath, action.DestinationPath, StringComparison.OrdinalIgnoreCase)
            || !AudioCollisionDedupe.IsDuplicate(action.DestinationPath, action.SourcePath))
        {
            return false;
        }

        var preferIncoming = AudioCollisionDedupe.ShouldPreferIncoming(action.DestinationPath, action.SourcePath);
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

        if (preferIncoming)
        {
            MoveFileOverwrite(action.SourcePath, action.DestinationPath);
            _logger.LogInformation("AutoTag organizer replaced duplicate destination {DestinationPath} using {SourcePath}", action.DestinationPath, action.SourcePath);
            log?.Invoke($"organizer replaced duplicate destination: {action.DestinationPath} using {action.SourcePath}");
        }
        else
        {
            TryDeleteFile(action.SourcePath);
            _logger.LogInformation("AutoTag organizer skipped duplicate source {SourcePath} (existing {DestinationPath})", action.SourcePath, action.DestinationPath);
            log?.Invoke($"organizer skipped duplicate: {action.SourcePath} (existing {action.DestinationPath})");
        }

        MoveSidecarFiles(rootPath, action.SourceDir, action.DestinationDir, action.SourcePath, action.DestinationPath, log, options);
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
        AutoTagOrganizerOptions options)
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
            var target = GetUniquePath(candidate, file);
            if (options.DryRun)
            {
                log?.Invoke($"organizer dry-run: would move sidecar {file} -> {target}");
                continue;
            }

            MoveFileOverwrite(file, target);
            _logger.LogInformation("AutoTag organizer moved sidecar {SourcePath} -> {DestinationPath}", file, target);
            log?.Invoke($"organizer moved sidecar: {file} -> {target}");
            movedAny = true;
        }

        if (movedAny && !options.DryRun && options.RemoveEmptyFolders)
        {
            DeleteEmptyDirectoryTree(sourceDir, rootPath, log);
        }
    }

    private void MoveRemainingFilesIfAlbumDone(string rootPath, string sourceDir, string destinationDir, Action<string>? log, AutoTagOrganizerOptions options)
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
            _logger.LogInformation("AutoTag organizer moved leftover {SourcePath} -> {DestinationPath}", file, target);
            log?.Invoke($"organizer moved leftover: {file} -> {target}");
        }

        if (!options.DryRun && options.RemoveEmptyFolders)
        {
            DeleteEmptyDirectoryTree(sourceDir, rootPath, log);
        }
    }

    private void DeleteVariousArtistsArt(string sourceArtistDir, Action<string>? log)
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

    private void CleanupArtistFolders(string rootPath, bool usePrimaryArtistFolders, Action<string>? log) // NOSONAR
    {
        foreach (var artistDir in Directory.EnumerateDirectories(rootPath))
        {
            if (EnumerateAudioFiles(artistDir, true).Any() || !TryGetArtistName(artistDir, out var artistName))
            {
                continue;
            }

            if (HandleVariousArtistsFolderCleanup(artistDir, artistName, log))
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

    private bool HandleVariousArtistsFolderCleanup(string artistDir, string artistName, Action<string>? log)
    {
        if (!IsVariousArtists(artistName))
        {
            return false;
        }

        DeleteVariousArtistsArt(artistDir, log);
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
