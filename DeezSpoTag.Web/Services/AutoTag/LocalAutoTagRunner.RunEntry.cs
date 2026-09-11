using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using TagLib;
using IOFile = System.IO.File;
using DownloadLyricsService = DeezSpoTag.Services.Download.Utils.LyricsService;
using LyricsProviderRegistry = DeezSpoTag.Services.Download.Utils.LyricsProviderRegistry;

namespace DeezSpoTag.Web.Services.AutoTag;

public partial class LocalAutoTagRunner
{

    private static bool IsMp4Family(string extension)
    {
        return AtlTagHelper.IsMp4Family(extension);
    }
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobTokens = new();
    private readonly ConcurrentDictionary<string, JobMatchCacheState> _jobMatchCaches = new();
    private readonly ILogger<LocalAutoTagRunner> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MusicBrainzMatcher _musicBrainzMatcher;
    private readonly BeatportMatcher _beatportMatcher;
    private readonly DiscogsMatcher _discogsMatcher;
    private readonly TraxsourceMatcher _traxsourceMatcher;
    private readonly BandcampMatcher _bandcampMatcher;
    private readonly BpmSupremeMatcher _bpmSupremeMatcher;
    private readonly ItunesMatcher _itunesMatcher;
    private readonly SpotifyMatcher _spotifyMatcher;
    private readonly DeezerMatcher _deezerMatcher;
    private readonly LastFmMatcher _lastFmMatcher;
    private readonly BoomplayMatcher _boomplayMatcher;
    private readonly AudiomackMatcher _audiomackMatcher;
    private readonly ShazamMatcher _shazamMatcher;
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly AppleLyricsService _appleLyricsService;
    private readonly AppleMusicCatalogService _appleMusicCatalogService;
    private readonly DownloadLyricsService _downloadLyricsService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ITrackIdentityResolver _trackIdentityResolver;
    private readonly PortedPlatformRegistry? _platformRegistry;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new MultipleMatchesSortConverter()
        }
    };

    public LocalAutoTagRunner(LocalAutoTagRunnerCollaborators collaborators)
    {
        _logger = collaborators.Logger;
        _albumIdentityStorePath = collaborators.AlbumIdentityStorePath;
        _httpClientFactory = collaborators.HttpClientFactory;
        _musicBrainzMatcher = collaborators.MusicBrainzMatcher;
        _beatportMatcher = collaborators.BeatportMatcher;
        _discogsMatcher = collaborators.DiscogsMatcher;
        _traxsourceMatcher = collaborators.TraxsourceMatcher;
        _bandcampMatcher = collaborators.BandcampMatcher;
        _bpmSupremeMatcher = collaborators.BpmSupremeMatcher;
        _itunesMatcher = collaborators.ItunesMatcher;
        _spotifyMatcher = collaborators.SpotifyMatcher;
        _deezerMatcher = collaborators.DeezerMatcher;
        _lastFmMatcher = collaborators.LastFmMatcher;
        _boomplayMatcher = collaborators.BoomplayMatcher;
        _audiomackMatcher = collaborators.AudiomackMatcher;
        _shazamMatcher = collaborators.ShazamMatcher;
        _shazamRecognitionService = collaborators.ShazamRecognitionService;
        _appleLyricsService = collaborators.AppleLyricsService;
        _appleMusicCatalogService = collaborators.AppleMusicCatalogService;
        _downloadLyricsService = collaborators.DownloadLyricsService;
        _settingsService = collaborators.SettingsService;
        _serviceScopeFactory = collaborators.ServiceScopeFactory;
        _trackIdentityResolver = collaborators.TrackIdentityResolver;
        _platformRegistry = collaborators.PlatformRegistry;
    }

    public async Task<AutoTagRunResult> RunAsync(
        string jobId,
        string rootPath,
        string configPath,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
        Func<IReadOnlyList<string>, CancellationToken, Task>? batchCompletedCallback,
        AutoTagResumeCursor? resumeCursor,
        CancellationToken cancellationToken)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _jobTokens[jobId] = linkedCts;
        var token = linkedCts.Token;
        PruneExpiredMatchCaches();
        var jobMatchCache = GetOrCreateMatchCache(jobId);

        try
        {
            var (runPlan, failure) = await PrepareAutoTagRunPlanAsync(jobId, rootPath, configPath, token);
            if (failure != null)
            {
                return failure;
            }

            var plan = runPlan!;
            LogShazamAvailability(plan, logCallback);
            await ExecutePlatformPassesAsync(
                plan,
                jobMatchCache,
                statusCallback,
                logCallback,
                batchCompletedCallback,
                resumeCursor,
                token);
            await ApplyPostLoopFallbackAsync(plan, token);
            PersistAlbumIdentities(plan);

            return AutoTagRunResult.Completed();
        }
        catch (OperationCanceledException)
        {
            return AutoTagRunResult.Stopped();
        }
        catch (AutoTagRunPausedException ex)
        {
            // Typed outcome: the message is the pause reason, no string prefix needed.
            return AutoTagRunResult.Paused(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Local AutoTag run failed.");
            return AutoTagRunResult.Failed(ex.ToString());
        }
        finally
        {
            _jobMatchCaches.TryRemove(jobId, out _);
            _jobTokens.TryRemove(jobId, out _);
        }
    }

    private async Task<(AutoTagRunPlan? Plan, AutoTagRunResult? Failure)> PrepareAutoTagRunPlanAsync(
        string jobId,
        string rootPath,
        string configPath,
        CancellationToken token)
    {
        if (!IOFile.Exists(configPath))
        {
            return (null, AutoTagRunResult.Failed("Config not found."));
        }

        var configJson = await IOFile.ReadAllTextAsync(configPath, token);
        var config = NormalizeConfig(JsonSerializer.Deserialize<AutoTagRunnerConfig>(configJson, _jsonOptions));
        var targetPath = string.IsNullOrWhiteSpace(rootPath) ? config.Path : rootPath;
        if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
        {
            return (null, AutoTagRunResult.Failed("Target path not found."));
        }

        var matchingConfig = new AutoTagMatchingConfig
        {
            MatchDuration = config.MatchDuration,
            MaxDurationDifferenceSeconds = config.MaxDurationDifference,
            Strictness = config.Strictness,
            MultipleMatches = config.MultipleMatches,
            PreferredReleaseType = IsManualEnrichment(config)
                ? config.ManualReleasePreference
                : null
        };
        var settings = LoadRuntimeSettings(config.Technical, config);
        settings.DownloadLocation = targetPath;
        var shazamBehavior = ResolveShazamEnrichmentBehavior(config);
        LoadPersistedAlbumIdentities();
        var plan = new AutoTagRunPlan
        {
            JobId = jobId,
            ConfigPath = configPath,
            Config = config,
            TargetPath = targetPath,
            MatchingConfig = matchingConfig,
            EffectivePlatforms = BuildEffectivePlatforms(config, settings),
            PlatformSupportedTags = BuildPlatformSupportedTags(),
            Settings = settings,
            TagSettings = BuildTagSettings(config, settings),
            Files = ResolveTargetFiles(targetPath, config).ToList(),
            ShazamCache = new Dictionary<string, ShazamRecognitionInfo?>(StringComparer.OrdinalIgnoreCase),
            EnableShazamFallback = shazamBehavior.EnableFallback,
            ForceShazamMatch = shazamBehavior.ForceMatch,
            ShazamConflictResolution = IsShazamConflictResolution(config)
        };
        SeedPlanAlbumIdentities(plan);

        if (config.SkipTagged)
        {
            plan.PreSkippedFiles.UnionWith(plan.Files.Where(HasExistingTags));
        }

        if (IsLibraryWideEnhancementBatchingEnabled(config))
        {
            // Enhancement runs are alphabetical by main artist: wave 1 holds the files
            // the library DB flagged as missing core metadata plus files with unknown
            // artist/album tags; wave 2 holds the rest. Within a wave: main artist,
            // then album, then track number. Albums stay contiguous inside an artist
            // block so batch windows can still honor album boundaries.
            foreach (var file in plan.Files)
            {
                plan.ArtistSortMeta[file] = ReadArtistSortMeta(file);
            }

            var orderedFiles = OrderFilesForEnhancementRun(
                plan.Files,
                plan.ArtistSortMeta,
                BuildNormalizedPathSet(config.PriorityTargetFiles));
            plan.Files.Clear();
            plan.Files.AddRange(orderedFiles);
        }

        return (plan, null);
    }

    private void LogShazamAvailability(AutoTagRunPlan plan, Action<string> logCallback)
    {
        var shazamRecognitionAvailable = IsShazamRecognitionAvailable();
        if ((plan.EnableShazamFallback
             || plan.ForceShazamMatch
             || plan.ShazamConflictResolution
             || plan.EffectivePlatforms.Contains(ShazamPlatform, StringComparer.OrdinalIgnoreCase))
            && !shazamRecognitionAvailable)
        {
            logCallback("onetagger_autotag: shazam unavailable");
        }
    }

    private async Task ExecutePlatformPassesAsync(
        AutoTagRunPlan plan,
        JobMatchCacheState jobMatchCache,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
        Func<IReadOnlyList<string>, CancellationToken, Task>? batchCompletedCallback,
        AutoTagResumeCursor? resumeCursor,
        CancellationToken token)
    {
        var resumeMismatchReason = GetResumeCheckpointMismatchReason(plan, resumeCursor);
        if (!string.IsNullOrWhiteSpace(resumeMismatchReason))
        {
            logCallback($"onetagger_autotag: resume checkpoint adjusted ({resumeMismatchReason})");
        }

        var (startPlatformIndex, startFileIndex) = ResolveResumeStartIndices(
            plan,
            resumeCursor,
            preferPathAnchor: true);

        // Mid-run pickup: files added while the run is in progress join the run when
        // their artist sorts after the current position, and are deferred to an
        // alphabetical end-wave when their position was already passed.
        var scheduler = new EnhancementPickupScheduler(plan, logCallback);
        var (passPlatformStart, passFileStart) = (startPlatformIndex, startFileIndex);
        while (true)
        {
            var passFileCount = plan.FileCount;
            if (IsLibraryWideEnhancementBatchingEnabled(plan.Config))
            {
                await ExecuteLibraryWideEnhancementBatchesAsync(
                    plan,
                    jobMatchCache,
                    statusCallback,
                    logCallback,
                    batchCompletedCallback,
                    passPlatformStart,
                    passFileStart,
                    passFileCount,
                    scheduler,
                    token);
            }
            else
            {
                await ExecutePlainPlatformPassAsync(
                    plan,
                    jobMatchCache,
                    statusCallback,
                    logCallback,
                    passPlatformStart,
                    passFileStart,
                    passFileCount,
                    scheduler,
                    token);
            }

            if (!scheduler.BeginNextPass(plan, logCallback))
            {
                break;
            }

            passPlatformStart = 0;
            passFileStart = passFileCount;
        }
    }

    private async Task ExecutePlainPlatformPassAsync(
        AutoTagRunPlan plan,
        JobMatchCacheState jobMatchCache,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
        int startPlatformIndex,
        int startFileIndex,
        int passFileCount,
        EnhancementPickupScheduler scheduler,
        CancellationToken token)
    {
        for (var platformIndex = startPlatformIndex; platformIndex < plan.PlatformCount; platformIndex++)
        {
            token.ThrowIfCancellationRequested();
            var platform = plan.EffectivePlatforms[platformIndex];
            logCallback($"{AutoTagProtocol.LogMarker} {AutoTagProtocol.StartingPlatformMessage}{platform}");

            var fileStart = platformIndex == startPlatformIndex ? startFileIndex : 0;
            for (var fileIndex = fileStart; fileIndex < passFileCount; fileIndex++)
            {
                token.ThrowIfCancellationRequested();
                if (plan.ReviewedFiles.Contains(plan.Files[fileIndex]))
                {
                    continue;
                }

                var context = new AutoTagFileRunContext
                {
                    Plan = plan,
                    JobMatchCache = jobMatchCache,
                    Platform = platform,
                    PlatformIndex = platformIndex,
                    FileIndex = fileIndex,
                    File = plan.Files[fileIndex],
                    Progress = ComputeOverallProgress(platformIndex, fileIndex, plan.PlatformCount, passFileCount),
                    NextPlatformIndex = ComputeNextPlatformIndex(platformIndex, fileIndex, plan.PlatformCount, passFileCount),
                    NextFileIndex = ComputeNextFileIndex(fileIndex, passFileCount),
                    StatusCallback = statusCallback,
                    LogCallback = logCallback,
                    Token = token
                };
                await ProcessPlatformFileAsync(context);
            }

            scheduler.ScanIfDue(Math.Min(passFileCount, plan.FileCount) - 1, token);
        }
    }

    /// <summary>
    /// Harvests files that appeared after the run started. Files whose artist sorts
    /// after the runner's current position join the run (appended before the deferred
    /// wave); files at or before the current position are deferred to the very end so
    /// a new file never jumps ahead of the alphabetical flow.
    /// </summary>
    private sealed class EnhancementPickupScheduler
    {
        private const int PickupScanIntervalSeconds = 30;

        private readonly AutoTagRunPlan _plan;
        private readonly Action<string> _log;
        private readonly bool _enabled;
        private readonly HashSet<string> _knownFiles;
        private readonly List<string> _included = new();
        private readonly List<string> _deferred = new();
        private DateTimeOffset _lastScanUtc = DateTimeOffset.MinValue;

        public EnhancementPickupScheduler(AutoTagRunPlan plan, Action<string> log)
        {
            _plan = plan;
            _log = log;
            // Pickups apply to library-wide runs only; scoped target-file runs keep
            // their explicit scope.
            _enabled = plan.Config.TargetFiles is null or { Count: 0 };
            _knownFiles = new HashSet<string>(plan.Files, StringComparer.OrdinalIgnoreCase);
        }

        public void ScanIfDue(int currentFileIndex, CancellationToken token)
        {
            if (!_enabled || currentFileIndex < 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastScanUtc < TimeSpan.FromSeconds(PickupScanIntervalSeconds))
            {
                return;
            }

            _lastScanUtc = now;
            var currentKey = ArtistKeyAt(currentFileIndex);
            var discovered = 0;
            foreach (var file in EnumerateAudioFiles(_plan.TargetPath, _plan.Config.IncludeSubfolders))
            {
                token.ThrowIfCancellationRequested();
                if (!_knownFiles.Add(file))
                {
                    continue;
                }

                discovered++;
                if (_plan.Config.SkipTagged && HasExistingTags(file))
                {
                    continue;
                }

                var meta = ReadArtistSortMeta(file);
                _plan.ArtistSortMeta[file] = meta;
                if (string.Compare(meta.ArtistKey, currentKey, StringComparison.Ordinal) <= 0)
                {
                    _deferred.Add(file);
                }
                else
                {
                    _included.Add(file);
                }
            }

            if (discovered > 0)
            {
                _log($"onetagger_autotag: {discovered} new file(s) detected mid-run "
                     + $"({_included.Count} join the run, {_deferred.Count} deferred to the end wave).");
            }
        }

        public bool BeginNextPass(AutoTagRunPlan plan, Action<string> log)
        {
            var source = _included.Count > 0 ? _included : _deferred;
            if (source.Count == 0)
            {
                return false;
            }

            var ordered = source
                .Select(file => (File: file, Meta: PlanMeta(plan, file)))
                .OrderBy(item => item.Meta.ArtistKey, StringComparer.Ordinal)
                .ThenBy(item => item.Meta.AlbumKey, StringComparer.Ordinal)
                .ThenBy(item => item.Meta.TrackNumber ?? int.MaxValue)
                .ThenBy(item => item.File, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.File)
                .ToList();
            var kind = ReferenceEquals(source, _included) ? "current-run" : "deferred";
            log($"onetagger_autotag: running {ordered.Count} mid-run pickup file(s) ({kind} wave).");
            source.Clear();
            plan.Files.AddRange(ordered);
            return true;
        }

        private string ArtistKeyAt(int fileIndex)
        {
            var index = Math.Clamp(fileIndex, 0, _plan.FileCount - 1);
            var file = _plan.Files[index];
            return _plan.ArtistSortMeta.TryGetValue(file, out var meta)
                ? meta.ArtistKey
                : string.Empty;
        }

        private static ArtistSortMeta PlanMeta(AutoTagRunPlan plan, string file) =>
            plan.ArtistSortMeta.TryGetValue(file, out var meta)
                ? meta
                : new ArtistSortMeta(string.Empty, string.Empty, null, true);
    }

    private async Task ExecuteLibraryWideEnhancementBatchesAsync(
        AutoTagRunPlan plan,
        JobMatchCacheState jobMatchCache,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
        Func<IReadOnlyList<string>, CancellationToken, Task>? batchCompletedCallback,
        int startPlatformIndex,
        int startFileIndex,
        int passFileCount,
        EnhancementPickupScheduler scheduler,
        CancellationToken token)
    {
        if (startPlatformIndex >= plan.PlatformCount)
        {
            return;
        }

        var batchSize = Math.Max(1, plan.Config.LibraryWideEnhancementBatchSize ?? DefaultLibraryWideEnhancementBatchSize);
        // The pass works on a frozen snapshot; mid-run pickups are appended between passes.
        var ranges = BuildLibraryWideEnhancementBatchRanges(plan.Files, passFileCount, batchSize);

        // Resume inside the range that contains the checkpoint's file index.
        var resumeRangeIndex = ranges.FindIndex(range => startFileIndex < range.End);
        if (resumeRangeIndex < 0)
        {
            return;
        }

        for (var rangeIndex = resumeRangeIndex; rangeIndex < ranges.Count; rangeIndex++)
        {
            var (batchStart, batchEnd) = ranges[rangeIndex];
            var firstPlatformIndex = rangeIndex == resumeRangeIndex ? startPlatformIndex : 0;
            var rangeFileStart = rangeIndex == resumeRangeIndex ? Math.Max(startFileIndex, batchStart) : batchStart;

            for (var platformIndex = firstPlatformIndex; platformIndex < plan.PlatformCount; platformIndex++)
            {
                token.ThrowIfCancellationRequested();
                var platform = plan.EffectivePlatforms[platformIndex];
                logCallback($"{AutoTagProtocol.LogMarker} {AutoTagProtocol.StartingPlatformMessage}{platform}");

                var fileStart = platformIndex == firstPlatformIndex ? rangeFileStart : batchStart;
                for (var fileIndex = fileStart; fileIndex < batchEnd; fileIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    if (plan.ReviewedFiles.Contains(plan.Files[fileIndex]))
                    {
                        continue;
                    }

                    var nextPlatformIndex = platformIndex;
                    var nextFileIndex = fileIndex + 1;
                    if (nextFileIndex >= batchEnd)
                    {
                        nextFileIndex = batchStart;
                        nextPlatformIndex += 1;
                    }

                    if (nextPlatformIndex >= plan.PlatformCount)
                    {
                        if (batchEnd >= passFileCount)
                        {
                            nextFileIndex = 0;
                            nextPlatformIndex = plan.PlatformCount;
                        }
                        else
                        {
                            nextFileIndex = batchEnd;
                            nextPlatformIndex = 0;
                        }
                    }

                    var context = new AutoTagFileRunContext
                    {
                        Plan = plan,
                        JobMatchCache = jobMatchCache,
                        Platform = platform,
                        PlatformIndex = platformIndex,
                        FileIndex = fileIndex,
                        File = plan.Files[fileIndex],
                        Progress = ComputeBatchOverallProgress(batchStart, batchEnd, platformIndex, fileIndex, plan.PlatformCount, plan.FileCount),
                        NextPlatformIndex = nextPlatformIndex,
                        NextFileIndex = nextFileIndex,
                        StatusCallback = statusCallback,
                        LogCallback = logCallback,
                        Token = token,
                        // True album-boundary batch position for the progress display.
                        BatchNumber = rangeIndex + 1,
                        BatchCount = ranges.Count,
                        BatchSize = batchEnd - batchStart,
                        BatchProcessed = fileIndex - batchStart + 1
                    };
                    await ProcessPlatformFileAsync(context);
                }
            }

            if (batchCompletedCallback != null)
            {
                // Album-coherent batch: the sidecar/refresh hook sees complete albums.
                var batchFiles = plan.Files.GetRange(batchStart, batchEnd - batchStart);
                await batchCompletedCallback(batchFiles, token);
            }

            scheduler.ScanIfDue(batchEnd - 1, token);
        }
    }

    private static (int PlatformIndex, int FileIndex) ResolveResumeStartIndices(
        AutoTagRunPlan plan,
        AutoTagResumeCursor? resumeCursor,
        bool preferPathAnchor = false)
    {
        if (plan.PlatformCount == 0 || plan.FileCount == 0 || resumeCursor == null)
        {
            return (0, 0);
        }

        var platformIndex = Math.Clamp(resumeCursor.PlatformIndex, 0, plan.PlatformCount - 1);
        var fileIndex = Math.Clamp(resumeCursor.FileIndex, 0, plan.FileCount);
        if (preferPathAnchor
            && !string.IsNullOrWhiteSpace(resumeCursor.LastPath))
        {
            var anchoredFileIndex = plan.Files.FindIndex(file =>
                string.Equals(file, resumeCursor.LastPath, StringComparison.OrdinalIgnoreCase));
            if (anchoredFileIndex >= 0)
            {
                fileIndex = anchoredFileIndex + 1;
            }
        }

        if (fileIndex >= plan.FileCount)
        {
            fileIndex = 0;
            platformIndex += 1;
        }

        if (platformIndex >= plan.PlatformCount)
        {
            return (plan.PlatformCount, 0);
        }

        return (platformIndex, fileIndex);
    }

    private static bool IsLibraryWideEnhancementBatchingEnabled(AutoTagRunnerConfig config)
        => (config.LibraryWideEnhancementBatchSize ?? 0) > 0;

    private static bool IsManualEnrichment(AutoTagRunnerConfig config)
        => !string.IsNullOrWhiteSpace(config.ManualReleasePreference)
           && config.ManualDestinationFolderId is > 0;

    private static bool WantsArtworkFromSettings(AutoTagRunnerConfig config, DeezSpoTagSettings settings)
        => HasAnyTags(config, AlbumArtTag)
           || settings.SaveArtwork
           || settings.EmbedMaxQualityCover;

    private static HashSet<string> BuildNormalizedPathSet(IEnumerable<string>? paths)
        => paths?
            .Select(NormalizeOrderPath)
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
           ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeOrderPath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path.Trim();
        }
    }

    private sealed record ArtistSortMeta(string ArtistKey, string AlbumKey, int? TrackNumber, bool WeakIdentity);

    /// <summary>
    /// Reads the ordering metadata for one file: the alphabetically-first main artist
    /// (multi-artist credits sort under their first artist), the album title, and the
    /// track number. Files that cannot be read sort as unknown-identity material.
    /// </summary>
    private static ArtistSortMeta ReadArtistSortMeta(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var artists = (tag.Performers ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            var albumArtist = !string.IsNullOrWhiteSpace(tag.FirstAlbumArtist)
                ? tag.FirstAlbumArtist
                : tag.FirstAlbumArtistSort ?? tag.JoinedAlbumArtists;
            if (artists.Count == 0 && !string.IsNullOrWhiteSpace(tag.FirstPerformer))
            {
                artists.Add(tag.FirstPerformer);
            }

            // Sort by album/main artist so featured credits do not pull a track
            // under another name. Fall back to track artists when album artist is empty.
            var artistKey = ArtistOrderKey.ResolveMainArtistKey(
                string.IsNullOrWhiteSpace(albumArtist) ? artists : new[] { albumArtist },
                artists.FirstOrDefault());
            var album = string.IsNullOrWhiteSpace(tag.Album) ? null : tag.Album;
            var weakIdentity = TrackIdentityTrust.IsWeakMetadataValue(artists.FirstOrDefault() ?? albumArtist)
                || TrackIdentityTrust.IsWeakMetadataValue(album);
            return new ArtistSortMeta(
                artistKey,
                AlbumTitleNormalizer.CoreTitle(album),
                tag.Track > 0 ? (int)tag.Track : null,
                weakIdentity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ArtistSortMeta(string.Empty, string.Empty, null, WeakIdentity: true);
        }
    }

    /// <summary>
    /// Two-wave enhancement order: wave 1 = files flagged missing core metadata by the
    /// library DB plus files with unknown artist/album tags; wave 2 = everything else.
    /// Within a wave: main artist, album, track number, path — all alphabetical.
    /// </summary>
    private static List<string> OrderFilesForEnhancementRun(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, ArtistSortMeta> meta,
        HashSet<string> priorityPaths)
    {
        ArtistSortMeta MetaFor(string file) =>
            meta.TryGetValue(file, out var value) ? value : new ArtistSortMeta(string.Empty, string.Empty, null, true);

        IEnumerable<string> Ordered(IEnumerable<string> source) => source
            .Select(file => (File: file, Meta: MetaFor(file)))
            .OrderBy(item => item.Meta.ArtistKey, StringComparer.Ordinal)
            .ThenBy(item => item.Meta.AlbumKey, StringComparer.Ordinal)
            .ThenBy(item => item.Meta.TrackNumber ?? int.MaxValue)
            .ThenBy(item => item.File, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.File);

        var wave1 = new List<string>();
        var wave2 = new List<string>();
        foreach (var file in files)
        {
            var isPriority = priorityPaths.Contains(NormalizeOrderPath(file)) || MetaFor(file).WeakIdentity;
            (isPriority ? wave1 : wave2).Add(file);
        }

        return Ordered(wave1).Concat(Ordered(wave2)).ToList();
    }

    private static string GetAlbumSortKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static bool SameAlbumDirectory(string? left, string? right)
        => string.Equals(GetAlbumSortKey(left), GetAlbumSortKey(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Contiguous batch ranges of at most <paramref name="batchSize"/> files, extended
    /// past the limit only to finish the album that is currently being processed
    /// ("active album"). With the album-major sort this makes every batch a set of
    /// complete albums — an album is never split across batches.
    /// </summary>
    internal static List<(int Start, int End)> BuildLibraryWideEnhancementBatchRanges(
        IReadOnlyList<string> files,
        int batchSize)
    {
        return BuildLibraryWideEnhancementBatchRanges(files, files.Count, batchSize);
    }

    internal static List<(int Start, int End)> BuildLibraryWideEnhancementBatchRanges(
        IReadOnlyList<string> files,
        int fileCount,
        int batchSize)
    {
        var ranges = new List<(int Start, int End)>();
        if (files.Count == 0 || fileCount <= 0)
        {
            return ranges;
        }

        var resolvedBatchSize = Math.Max(1, batchSize);
        var limit = Math.Min(fileCount, files.Count);
        var start = 0;
        while (start < limit)
        {
            var end = start + 1;
            while (end < limit
                   && (end - start < resolvedBatchSize
                       || SameAlbumDirectory(files[end - 1], files[end])))
            {
                end++;
            }

            ranges.Add((start, end));
            start = end;
        }

        return ranges;
    }

    private static string? GetResumeCheckpointMismatchReason(AutoTagRunPlan plan, AutoTagResumeCursor? resumeCursor)
    {
        if (resumeCursor == null)
        {
            return null;
        }

        if (resumeCursor.PlatformCount is > 0 and var checkpointPlatformCount
            && checkpointPlatformCount != plan.PlatformCount)
        {
            return $"platform count changed (checkpoint={checkpointPlatformCount}, current={plan.PlatformCount})";
        }

        if (resumeCursor.FileCount is > 0 and var checkpointFileCount
            && checkpointFileCount != plan.FileCount)
        {
            return $"file count changed (checkpoint={checkpointFileCount}, current={plan.FileCount})";
        }

        return null;
    }

    private static ProviderTagPlan BuildProviderTagPlan(AutoTagFileRunContext context)
    {
        var configured = context.Plan.Config.Tags
            .Select(tag => tag?.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => SupportedTagMap.TryGetValue(tag!, out var mapped) ? (SupportedTag?)mapped : null)
            .Where(tag => tag.HasValue)
            .Select(tag => tag!.Value)
            .ToHashSet();

        if (context.Plan.PlatformSupportedTags.TryGetValue(context.Platform, out var supported))
        {
            configured.IntersectWith(supported);
        }

        var retained = new HashSet<SupportedTag>();
        var eligible = new HashSet<SupportedTag>();
        try
        {
            using var file = TagLib.File.Create(context.File);
            var extension = Path.GetExtension(context.File);
            foreach (var tag in configured)
            {
                if (!ShouldOverwriteTag(context.Plan.Config, tag)
                    && HasTag(file, extension, tag, context.Plan.Config, context.Platform))
                {
                    retained.Add(tag);
                }
                else
                {
                    eligible.Add(tag);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            eligible.UnionWith(configured);
        }

        return new ProviderTagPlan(configured, eligible, retained);
    }

    private static HashSet<SupportedTag> CapturePresentTags(
        string filePath,
        AutoTagRunnerConfig config,
        string platformId,
        IEnumerable<SupportedTag> tags)
    {
        var present = new HashSet<SupportedTag>();
        using var file = TagLib.File.Create(filePath);
        var extension = Path.GetExtension(filePath);
        foreach (var tag in tags)
        {
            if (HasTag(file, extension, tag, config, platformId))
            {
                present.Add(tag);
            }
        }

        return present;
    }

    private static HashSet<SupportedTag> ResolveReturnedEligibleTags(AutoTagTrack track, ProviderTagPlan plan)
    {
        return CollectAutoTagTags(track)
            .Select(tag => SupportedTagMap.TryGetValue(tag, out var mapped) ? (SupportedTag?)mapped : null)
            .Where(tag => tag.HasValue && plan.Eligible.Contains(tag.Value))
            .Select(tag => tag!.Value)
            .ToHashSet();
    }

    private static HashSet<SupportedTag> VerifyPersistedTags(
        string filePath,
        AutoTagRunnerConfig config,
        string platformId,
        AutoTagTrack track,
        IEnumerable<SupportedTag> expectedTags)
    {
        var missing = new HashSet<SupportedTag>();
        var expected = expectedTags.ToHashSet();
        using var file = TagLib.File.Create(filePath);
        var extension = Path.GetExtension(filePath);
        if (expected.Contains(SupportedTag.Artist)
            && BuildConfiguredTagSet(config.Tags).Contains(ArtistsTag)
            && !string.Equals(
                config.Technical?.MultiArtistSeparator ?? MultiArtistSeparatorDefault,
                MultiArtistSeparatorDefault,
                StringComparison.OrdinalIgnoreCase)
            && track.Artists.Count > 0
            && !HasRawTag(file, extension, "ARTISTS"))
        {
            missing.Add(SupportedTag.Artist);
        }

        foreach (var tag in expected)
        {
            var persisted = tag switch
            {
                SupportedTag.OtherTags => VerifyOtherTagsPersisted(file, extension, track),
                SupportedTag.TtmlLyrics => IOFile.Exists(Path.ChangeExtension(filePath, TtmlExtension)),
                _ => HasTag(file, extension, tag, config, platformId)
            };
            if (!persisted)
            {
                missing.Add(tag);
            }
        }

        return missing;
    }

    private static bool VerifyOtherTagsPersisted(TagLib.File file, string extension, AutoTagTrack track)
    {
        var expectedRawTags = track.Other
            .Where(pair => pair.Value.Count > 0)
            .Where(pair => ShouldPersistOtherRawKey(pair.Key))
            .Select(pair => pair.Key)
            .ToList();
        return expectedRawTags.Count == 0
            || expectedRawTags.All(rawTag => HasRawTag(file, extension, rawTag));
    }

    private static string ToTagKey(SupportedTag tag)
    {
        return tag switch
        {
            SupportedTag.AlbumArt => AlbumArtTag,
            SupportedTag.BPM => BpmTag,
            SupportedTag.ISRC => IsrcTag,
            SupportedTag.URL => UrlTag,
            SupportedTag.TtmlLyrics => TtmlLyricsTag,
            _ => char.ToLowerInvariant(tag.ToString()[0]) + tag.ToString()[1..]
        };
    }

    private async Task ProcessPlatformFileAsync(AutoTagFileRunContext context)
    {
        if (TryHandlePreSkippedFile(context))
        {
            return;
        }

        var tagPlan = BuildProviderTagPlan(context);
        if (tagPlan.Eligible.Count == 0)
        {
            EmitSkippedStatus(
                context,
                "provider has no eligible configured fields for this file",
                outcome: "no_eligible_tags",
                tagPlan: tagPlan);
            return;
        }

        var isManualEnrichment = IsManualEnrichment(context.Plan.Config);
        AutoTagAudioInfo? cachedManualInfo = null;
        var firstManualPass = !isManualEnrichment
            || !context.Plan.ResolvedManualInfo.TryGetValue(context.FileIndex, out cachedManualInfo);
        var validationInfo = firstManualPass
            ? BuildAudioInfo(
                context.File,
                context.Plan.TargetPath,
                context.Plan.Config.ParseFilename,
                context.Plan.Config.TracknameTemplate,
                context.Plan.Config.TitleRegex)
            : CloneAudioInfo(context.Plan.OriginalManualInfo[context.FileIndex]);
        var info = firstManualPass
            ? CloneAudioInfo(validationInfo)
            : CloneAudioInfo(cachedManualInfo!);
        var shazamResult = firstManualPass
            ? TryApplyShazam(
                context.File,
                info,
                context.Plan.Config,
                context.Plan.EnableShazamFallback,
                context.Plan.ForceShazamMatch,
                context.Plan.ShazamCache,
                context.LogCallback,
                context.Token)
            : new ShazamEnrichmentResult(
                context.Plan.ShazamIdentifiedFiles.Contains(context.FileIndex),
                null,
                false);
        var usedShazamForStatus = shazamResult.UsedShazam
            || string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase);

        if (shazamResult.IsFatal)
        {
            throw new AutoTagRunPausedException(shazamResult.Error ?? "Shazam is unavailable.");
        }

        if (isManualEnrichment && shazamResult.FailureKind == ShazamFailureKind.NoMatch)
        {
            EmitReviewStatus(
                context,
                "Shazam could not identify the staged audio file.",
                usedShazamForStatus,
                AutoTagReviewMetadata.FromSourceOnly(validationInfo));
            context.Plan.ReviewedFiles.Add(context.File);
            return;
        }

        if (shazamResult.FailureKind == ShazamFailureKind.NoMatch
            && !string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase))
        {
            context.LogCallback(
                $"onetagger_autotag: shazam could not identify {Path.GetFileName(context.File)}; continuing with {context.Platform}");
        }

        if (isManualEnrichment && firstManualPass)
        {
            context.Plan.OriginalManualInfo[context.FileIndex] = CloneAudioInfo(validationInfo);
            await ApplyCentralIdentityForManualEnrichmentAsync(info, context.Plan.Config, context.LogCallback, context.Token);
            context.Plan.ResolvedManualInfo[context.FileIndex] = CloneAudioInfo(info);
            if (shazamResult.UsedShazam)
            {
                context.Plan.ShazamIdentifiedFiles.Add(context.FileIndex);
            }
        }

        var identityIsTrusted = IsTrustedSourceIdentity(validationInfo, context.File, context.Plan.Config);
        var matchInfo = string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase)
            && identityIsTrusted
            ? validationInfo
            : info;
        var match = await ResolvePlatformMatchAsync(context, matchInfo);
        if (match == null)
        {
            if (string.Equals(context.MatchFailureOutcome, "provider_error", StringComparison.Ordinal))
            {
                EmitErrorStatus(
                    context,
                    context.MatchFailureMessage ?? "provider request failed",
                    usedShazamForStatus,
                    "provider_error",
                    tagPlan);
                return;
            }

            if (isManualEnrichment
                && IsLastPlatform(context)
                && !WasTaggedByAnyPlatform(context))
            {
                EmitReviewStatus(
                    context,
                    $"No {context.Plan.Config.ManualReleasePreference} release could be resolved.",
                    usedShazamForStatus,
                    AutoTagReviewMetadata.FromSourceOnly(validationInfo),
                    context.MatchFailureOutcome ?? "not_in_catalog",
                    tagPlan);
                context.Plan.ReviewedFiles.Add(context.File);
            }
            else
            {
                EmitSkippedStatus(
                    context,
                    context.MatchFailureMessage ?? "no match",
                    usedShazamForStatus,
                    context.MatchFailureOutcome ?? "not_in_catalog",
                    tagPlan);
            }
            return;
        }

        await ApplyResolvedMatchAsync(context, info, validationInfo, match, usedShazamForStatus, tagPlan);
    }
}
