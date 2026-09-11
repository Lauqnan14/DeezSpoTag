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

public sealed partial class LocalAutoTagRunner
{

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

    private static bool IsLibraryWideEnhancementBatchingEnabled(AutoTagRunnerConfig config)
        => (config.LibraryWideEnhancementBatchSize ?? 0) > 0;

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

    private static bool IsLastPlatform(AutoTagFileRunContext context)
        => context.PlatformIndex == context.Plan.PlatformCount - 1;

    private static bool WasTaggedByAnyPlatform(AutoTagFileRunContext context)
        => context.Plan.TaggedFileIndices.Contains(context.FileIndex);

    private static string? EvaluateBoomplayReliabilityGuard(
        AutoTagAudioInfo info,
        AutoTagMatchResult match,
        AutoTagMatchingConfig matchingConfig)
    {
        if (match.Track == null)
        {
            return "match rejected by Boomplay guard (missing track payload)";
        }

        List<string> sourceArtists;
        if (info.Artists.Count > 0)
        {
            sourceArtists = info.Artists;
        }
        else if (string.IsNullOrWhiteSpace(info.Artist))
        {
            sourceArtists = [];
        }
        else
        {
            sourceArtists = [info.Artist];
        }

        var incomingArtists = match.Track.Artists ?? new List<string>();
        var artistStrictness = Math.Clamp(matchingConfig.Strictness + 0.12d, 0.80d, 0.98d);
        var artistCompatible = sourceArtists.Count > 0
            && incomingArtists.Count > 0
            && AreArtistIdentitiesCompatibleForOverwrite(sourceArtists, incomingArtists, artistStrictness);
        if (!artistCompatible)
        {
            return "match rejected by Boomplay guard (artist mismatch)";
        }

        var hasMatchingIsrc = HasMatchingIsrc(info.Isrc, match.Track.Isrc);
        var minAccuracy = Math.Clamp(matchingConfig.Strictness + 0.10d, 0.80d, 0.99d);
        if (!hasMatchingIsrc && match.Accuracy < minAccuracy)
        {
            return $"match rejected by Boomplay guard (accuracy {match.Accuracy:0.000} < {minAccuracy:0.000})";
        }

        var incomingFullTitle = OneTaggerMatching.FullTitle(match.Track.Title, match.Track.Version);
        if (TrackTitleMatcher.HasVersionDrift(info.Title, incomingFullTitle))
        {
            return "match rejected by Boomplay guard (version drift)";
        }

        if (!hasMatchingIsrc && !TrackTitleMatcher.HasCompatibleTitleIdentity(info.Title, incomingFullTitle))
        {
            return "match rejected by Boomplay guard (title identity)";
        }

        var sourceTitle = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(info.Title));
        var incomingTitle = AutoTagSimilarity.NormalizeText(
            OneTaggerMatching.CleanTitleMatching(incomingFullTitle));
        if (string.IsNullOrWhiteSpace(sourceTitle) || string.IsNullOrWhiteSpace(incomingTitle))
        {
            return hasMatchingIsrc ? null : "match rejected by Boomplay guard (insufficient title evidence)";
        }

        var titleSimilarity = AutoTagSimilarity.ComputeScore(sourceTitle, incomingTitle);
        var minTitleSimilarity = Math.Clamp(matchingConfig.Strictness + 0.10d, 0.82d, 0.98d);
        if (titleSimilarity < minTitleSimilarity)
        {
            return $"match rejected by Boomplay guard (title similarity {titleSimilarity:0.000} < {minTitleSimilarity:0.000})";
        }

        if (HasDurationMismatch(info.DurationSeconds, match.Track.Duration, matchingConfig.MaxDurationDifferenceSeconds))
        {
            return "match rejected by Boomplay guard (duration mismatch)";
        }

        return null;
    }

    private static async Task ApplyPostLoopFallbackAsync(AutoTagRunPlan plan, CancellationToken token)
    {
        if (plan.ShazamConflictResolution || !plan.Config.ParseFilename)
        {
            return;
        }

        foreach (var file in plan.Files.Where(file => !plan.PreSkippedFiles.Contains(file) && !plan.TaggedByAnyPlatform.Contains(file)))
        {
            await EnsureCoreTagsFromPathAsync(
                file,
                plan.TargetPath,
                plan.Settings.Tags?.SingleAlbumArtist ?? true,
                token);
        }
    }

    private static double ComputeOverallProgress(int platformIndex, int fileIndex, int platformCount, int fileCount)
    {
        var fileProgress = fileCount == 0
            ? 1.0
            : (fileIndex + 1) / (double)fileCount;

        return platformCount == 0
            ? 1.0
            : (platformIndex / (double)platformCount) + (fileProgress / platformCount);
    }

    private static double ComputeBatchOverallProgress(
        int batchStart,
        int batchEnd,
        int platformIndex,
        int fileIndex,
        int platformCount,
        int fileCount)
    {
        if (platformCount == 0 || fileCount == 0)
        {
            return 1.0;
        }

        var completedFilesBeforeBatch = batchStart;
        var batchFileCount = Math.Max(1, batchEnd - batchStart);
        var batchProgress = (platformIndex / (double)platformCount)
            + (((fileIndex - batchStart) + 1) / (double)batchFileCount / platformCount);
        return Math.Min(1.0, (completedFilesBeforeBatch + (batchProgress * batchFileCount)) / fileCount);
    }

    private static int ComputeNextPlatformIndex(int platformIndex, int fileIndex, int platformCount, int fileCount)
    {
        var nextFileIndex = fileIndex + 1;
        return nextFileIndex >= fileCount ? platformIndex + 1 : platformIndex;
    }

    private static int ComputeNextFileIndex(int fileIndex, int fileCount)
    {
        var nextFileIndex = fileIndex + 1;
        return nextFileIndex >= fileCount ? 0 : nextFileIndex;
    }

    private static List<string> BuildEffectivePlatforms(AutoTagRunnerConfig config, DeezSpoTagSettings? settings = null)
    {
        var platforms = config.Platforms
            .Select(platform => platform?.Trim())
            .Where(platform => !string.IsNullOrWhiteSpace(platform))
            .Select(platform => platform!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tagPlatforms = platforms.Where(platform => !IsLyricsOnlyPlatform(platform)).ToList();
        if (platforms.Any(IsLyricsOnlyPlatform) && ShouldRequestAnyLyrics(config, settings ?? new DeezSpoTagSettings()))
        {
            tagPlatforms.Add(LyricsPlatform);
        }

        return tagPlatforms;
    }

    private void SeedPlanAlbumIdentities(AutoTagRunPlan plan)
    {
        if (_albumIdentityStore == null)
        {
            return;
        }

        foreach (var (key, identity, updatedAt) in _albumIdentityStore.Entries)
        {
            plan.AlbumIdentities.Seed(key, identity, updatedAt);
        }
    }

    private static Track BuildCoreTrack(
        AutoTagTrack track,
        string? separator,
        bool singleAlbumArtist,
        DeezSpoTagSettings settings)
    {
        var artists = track.Artists.Count == 0 ? new List<string> { UnknownArtist } : track.Artists;
        var albumArtists = track.AlbumArtists.Count == 0 ? artists : track.AlbumArtists;
        var album = new Album(track.Album ?? "")
        {
            TrackTotal = track.TrackTotal ?? 0,
            DiscTotal = null,
            Genre = track.Genres.ToList(),
            Label = track.Label,
            ReleaseDate = track.ReleaseDate
        };

        var primaryAlbumArtist = albumArtists
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?.Trim();
        if (string.IsNullOrWhiteSpace(primaryAlbumArtist))
        {
            primaryAlbumArtist = artists[0];
        }

        var albumMainArtists = singleAlbumArtist
            ? new List<string> { primaryAlbumArtist }
            : albumArtists.ToList();

        album.MainArtist = new DeezSpoTag.Core.Models.Artist(primaryAlbumArtist);
        album.Artists = albumMainArtists.ToList();
        album.Artist["Main"] = albumMainArtists.ToList();

        var coreTrack = new Track
        {
            Title = track.Title,
            Artists = artists.ToList(),
            MainArtist = new DeezSpoTag.Core.Models.Artist(artists[0]),
            Album = album,
            TrackNumber = track.TrackNumber ?? 0,
            DiscNumber = track.DiscNumber ?? 0,
            Bpm = track.Bpm ?? 0,
            Explicit = track.Explicit ?? false,
            ISRC = track.Isrc ?? "",
            Duration = (int?)track.Duration?.TotalSeconds ?? 0
        };

        if (singleAlbumArtist && artists.Count > 1)
        {
            coreTrack.Artist["Main"] = new List<string> { artists[0] };
            coreTrack.Artist["Featured"] = artists.Skip(1).ToList();
            coreTrack.MainArtist = new DeezSpoTag.Core.Models.Artist(artists[0]);
        }
        else
        {
            coreTrack.Artist["Main"] = artists.ToList();
        }

        coreTrack.GenerateMainFeatStrings();
        coreTrack.ArtistString = coreTrack.MainArtist?.Name ?? artists[0];
        coreTrack.ArtistsString = string.IsNullOrWhiteSpace(separator) ? string.Join(", ", artists) : string.Join(separator, artists);

        if (track.ReleaseDate.HasValue)
        {
            coreTrack.Date = CustomDate.FromDateTime(track.ReleaseDate.Value);
            coreTrack.DateString = coreTrack.Date.Format("ymd");
        }

        settings.Tags ??= new TagSettings();
        coreTrack.ApplySettings(settings);

        return coreTrack;
    }

    private static void ReplaceTargetPathInRuntimeConfig(
        string configPath,
        string previousPath,
        string materializedPath)
    {
        try
        {
            var root = JsonNode.Parse(IOFile.ReadAllText(configPath)) as JsonObject;
            if (root?["targetFiles"] is not JsonArray targets)
            {
                return;
            }

            var changed = false;
            for (var index = 0; index < targets.Count; index++)
            {
                var existing = targets[index]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(existing) || !PathsReferToSameFile(existing, previousPath))
                {
                    continue;
                }

                targets[index] = materializedPath;
                changed = true;
            }

            if (changed)
            {
                IOFile.WriteAllText(configPath, root.ToJsonString(CaseInsensitiveJsonOptions), new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new IOException($"Failed to persist manual enrichment staging path '{materializedPath}'.", ex);
        }
    }
}
