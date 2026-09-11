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

    private void LogArtworkFallbackMatchFailure(Exception ex, string filePath, string platform)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(ex, "Artwork fallback match failed for {File} using {Platform}.", SanitizeLogValue(filePath), SanitizeLogValue(platform));
        }
    }

    private static string? ResolveArtworkFallbackPlatform(string provider)
    {
        var normalized = provider?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "apple" => ItunesPlatform,
            "deezer" => DeezerPlatform,
            SpotifyPlatform => SpotifyPlatform,
            _ => null
        };
    }

    private static void EmitSkippedStatus(
        AutoTagFileRunContext context,
        string message,
        bool usedShazam = false,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null)
    {
        EmitStatus(context, "skipped", message, null, usedShazam, outcome: outcome, tagPlan: tagPlan, match: match);
    }

    private static void EmitErrorStatus(
        AutoTagFileRunContext context,
        string message,
        bool usedShazam,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null)
    {
        EmitStatus(context, "error", message, null, usedShazam, outcome: outcome, tagPlan: tagPlan, match: match);
    }

    private static void EmitReviewStatus(
        AutoTagFileRunContext context,
        string message,
        bool usedShazam,
        AutoTagReviewMetadata? review,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null)
    {
        EmitStatus(context, "review", message, null, usedShazam, review, outcome, tagPlan, match);
    }

    private static void EmitTaggingStatus(AutoTagFileRunContext context, double? accuracy, bool usedShazam)
    {
        EmitStatus(context, "tagging", null, accuracy, usedShazam);
    }

    private static void EmitTaggedStatus(
        AutoTagFileRunContext context,
        double? accuracy,
        bool usedShazam,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null,
        IReadOnlyCollection<SupportedTag>? returnedTags = null,
        IReadOnlyCollection<SupportedTag>? writtenTags = null,
        IReadOnlyCollection<SupportedTag>? missingTags = null)
    {
        EmitStatus(
            context,
            "tagged",
            outcome == "matched_no_changes" ? "provider matched but supplied no new eligible values" : null,
            accuracy,
            usedShazam,
            outcome: outcome,
            tagPlan: tagPlan,
            match: match,
            returnedTags: returnedTags,
            writtenTags: writtenTags,
            missingTags: missingTags);
    }

    private static void EmitStatus(
        AutoTagFileRunContext context,
        string status,
        string? message,
        double? accuracy,
        bool usedShazam,
        AutoTagReviewMetadata? review = null,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null,
        IReadOnlyCollection<SupportedTag>? returnedTags = null,
        IReadOnlyCollection<SupportedTag>? writtenTags = null,
        IReadOnlyCollection<SupportedTag>? missingTags = null)
    {
        var isLyricsPlatform = string.Equals(context.Platform, LyricsPlatform, StringComparison.OrdinalIgnoreCase);
        context.StatusCallback(new TaggingStatusWrap
        {
            Platform = context.Platform,
            Progress = context.Progress,
            PlatformIndex = context.PlatformIndex,
            PlatformCount = context.Plan.PlatformCount,
            FileIndex = context.FileIndex,
            FileCount = context.Plan.FileCount,
            NextPlatformIndex = context.NextPlatformIndex,
            NextFileIndex = context.NextFileIndex,
            BatchNumber = context.BatchNumber,
            BatchCount = context.BatchCount,
            BatchSize = context.BatchSize,
            BatchProcessed = context.BatchProcessed,
            Status = new TaggingStatus
            {
                Status = status,
                Path = context.File,
                Message = message,
                Accuracy = accuracy,
                UsedShazam = usedShazam,
                Outcome = outcome,
                RecognitionStrategy = ResolveRecognitionStrategy(match),
                RequestedTags = (tagPlan?.Requested.AsEnumerable() ?? Enumerable.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                ReturnedTags = (returnedTags ?? Array.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                WrittenTags = (writtenTags ?? Array.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                RetainedTags = (tagPlan?.Retained.AsEnumerable() ?? Enumerable.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                MissingTags = (missingTags?.AsEnumerable() ?? tagPlan?.Eligible.AsEnumerable() ?? Enumerable.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                ReviewReason = review?.Reason ?? message,
                LyricsBadges = isLyricsPlatform
                    ? ResolveLyricsTimingBadges(context.File, context.Plan.Config, context.Plan.Settings)
                    : new List<string>(),
                ArtworkBadges = ResolveAnimatedArtworkBadges(context.File, context.Plan.Settings),
                LyricsCoverUrl = isLyricsPlatform ? ResolveLyricsRowCoverUrl(context.File) : null,
                SourceTitle = isLyricsPlatform
                    ? (match?.Track.Title ?? review?.SourceTitle)
                    : review?.SourceTitle,
                SourceArtist = isLyricsPlatform
                    ? (match?.Track.Artists.FirstOrDefault() ?? review?.SourceArtist)
                    : review?.SourceArtist,
                SourceIsrc = review?.SourceIsrc,
                SourceDurationSeconds = review?.SourceDurationSeconds,
                CandidateTitle = review?.CandidateTitle,
                CandidateArtist = review?.CandidateArtist,
                CandidateIsrc = review?.CandidateIsrc,
                CandidateDurationSeconds = review?.CandidateDurationSeconds
            }
        });
    }

    private static string? ResolveRecognitionStrategy(AutoTagMatchResult? match)
    {
        if (!string.IsNullOrWhiteSpace(match?.MatchStrategy))
        {
            return match.MatchStrategy;
        }

        return match?.Track.Other.TryGetValue("SHAZAM_MATCH_STRATEGY", out var strategies) == true
            ? strategies.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim().ToLowerInvariant()
            : null;
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

    private JobMatchCacheState GetOrCreateMatchCache(string jobId)
    {
        var cache = _jobMatchCaches.GetOrAdd(jobId, static _ => new JobMatchCacheState());
        cache.LastAccessUtc = DateTimeOffset.UtcNow;
        return cache;
    }

    private void PruneExpiredMatchCaches()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (jobId, cache) in _jobMatchCaches)
        {
            if (now - cache.LastAccessUtc > MatchCacheTtl)
            {
                _jobMatchCaches.TryRemove(jobId, out _);
            }
        }
    }

    private static bool TryGetCachedMatch(JobMatchCacheState cache, string key, out AutoTagMatchResult? match)
    {
        lock (cache.SyncRoot)
        {
            cache.LastAccessUtc = DateTimeOffset.UtcNow;
            if (cache.Entries.TryGetValue(key, out var entry))
            {
                match = entry.Match;
                return true;
            }
        }

        match = null;
        return false;
    }

    private static void StoreCachedMatch(JobMatchCacheState cache, string key, AutoTagMatchResult? match)
    {
        lock (cache.SyncRoot)
        {
            cache.LastAccessUtc = DateTimeOffset.UtcNow;
            cache.Entries[key] = new MatchCacheEntry(match);
            if (cache.Entries.Count > MaxCacheEntriesPerJob)
            {
                var keysToRemove = cache.Entries.Keys
                    .Take(cache.Entries.Count - MaxCacheEntriesPerJob)
                    .ToList();
                foreach (var staleKey in keysToRemove)
                {
                    cache.Entries.Remove(staleKey);
                }
            }
        }
    }

    private static bool IsPlatformUnavailable(JobMatchCacheState cache, string platform)
    {
        lock (cache.SyncRoot)
        {
            return cache.UnavailablePlatforms.Contains(platform);
        }
    }

    private static void MarkPlatformUnavailable(JobMatchCacheState cache, string platform)
    {
        lock (cache.SyncRoot)
        {
            cache.LastAccessUtc = DateTimeOffset.UtcNow;
            cache.UnavailablePlatforms.Add(platform);
        }
    }

    public Task<bool> StopAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_jobTokens.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private DeezSpoTagSettings LoadRuntimeSettings(TechnicalTagSettings? technical, AutoTagRunnerConfig config)
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            ApplyTechnicalOverrides(settings, technical);
            ApplyRuntimeConfigOverrides(settings, config);
            return settings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load runtime settings for AutoTag.");
            var fallback = DeezSpoTagSettingsService.GetStaticDefaultSettings();
            ApplyTechnicalOverrides(fallback, technical);
            ApplyRuntimeConfigOverrides(fallback, config);
            return fallback;
        }
    }

    private static void ApplyTechnicalOverrides(DeezSpoTagSettings settings, TechnicalTagSettings? technical)
    {
        if (technical == null)
        {
            return;
        }

        TechnicalLyricsSettingsApplier.Apply(settings, technical);
        settings.Tags ??= new TagSettings();

        settings.DateFormat = technical.DateFormat;
        settings.AlbumVariousArtists = technical.AlbumVariousArtists;
        settings.RemoveAlbumVersion = technical.RemoveAlbumVersion;
        settings.RemoveDuplicateArtists = technical.RemoveDuplicateArtists;
        settings.FeaturedToTitle = technical.FeaturedToTitle;
        settings.TitleCasing = technical.TitleCasing;
        settings.ArtistCasing = technical.ArtistCasing;

        settings.Tags.SavePlaylistAsCompilation = technical.SavePlaylistAsCompilation;
        settings.Tags.UseNullSeparator = technical.UseNullSeparator;
        settings.Tags.SaveID3v1 = technical.SaveID3v1;
        settings.Tags.MultiArtistSeparator = technical.MultiArtistSeparator;
        settings.Tags.SingleAlbumArtist = technical.SingleAlbumArtist;
        settings.Tags.CoverDescriptionUTF8 = technical.CoverDescriptionUTF8;
    }

    private static void ApplyRuntimeConfigOverrides(DeezSpoTagSettings settings, AutoTagRunnerConfig config)
    {
        ApplyFolderStructureOverrides(settings, config.FolderStructure);

        if (config.SaveArtwork.HasValue)
        {
            settings.SaveArtwork = config.SaveArtwork.Value;
        }

        if (config.DlAlbumcoverForPlaylist.HasValue)
        {
            settings.DlAlbumcoverForPlaylist = config.DlAlbumcoverForPlaylist.Value;
        }

        if (config.SaveArtworkArtist.HasValue)
        {
            settings.SaveArtworkArtist = config.SaveArtworkArtist.Value;
        }

        if (config.SaveAnimatedArtwork.HasValue)
        {
            settings.SaveAnimatedArtwork = config.SaveAnimatedArtwork.Value;
        }

        if (config.SaveSquareAnimatedArtwork.HasValue)
        {
            settings.SaveSquareAnimatedArtwork = config.SaveSquareAnimatedArtwork.Value;
        }

        if (config.SaveTallAnimatedArtwork.HasValue)
        {
            settings.SaveTallAnimatedArtwork = config.SaveTallAnimatedArtwork.Value;
        }

        if (!string.IsNullOrWhiteSpace(config.AnimatedArtworkFormats))
        {
            settings.AnimatedArtworkFormats = config.AnimatedArtworkFormats.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.CoverImageTemplate))
        {
            settings.CoverImageTemplate = config.CoverImageTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.AnimatedArtworkSquareFileName))
        {
            settings.AnimatedArtworkSquareFileName = config.AnimatedArtworkSquareFileName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.AnimatedArtworkTallFileName))
        {
            settings.AnimatedArtworkTallFileName = config.AnimatedArtworkTallFileName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.ArtistImageTemplate))
        {
            settings.ArtistImageTemplate = config.ArtistImageTemplate.Trim();
        }

        var normalizedArtworkFormat = NormalizeLocalArtworkFormat(config.LocalArtworkFormat);
        if (!string.IsNullOrWhiteSpace(normalizedArtworkFormat))
        {
            settings.LocalArtworkFormat = normalizedArtworkFormat;
        }

        if (config.EmbedMaxQualityCover.HasValue)
        {
            settings.EmbedMaxQualityCover = config.EmbedMaxQualityCover.Value;
        }

        if (config.AnimatedArtworkMaxSizeMb.HasValue)
        {
            settings.AnimatedArtworkMaxSizeMb = Math.Clamp(config.AnimatedArtworkMaxSizeMb.Value, 1, 200);
        }

        if (config.JpegImageQuality.HasValue)
        {
            settings.JpegImageQuality = Math.Clamp(config.JpegImageQuality.Value, 1, 100);
        }
    }

    private static void ApplyFolderStructureOverrides(DeezSpoTagSettings settings, FolderStructureSettings? folderStructure)
    {
        if (folderStructure == null)
        {
            return;
        }

        settings.CreateArtistFolder = folderStructure.CreateArtistFolder;
        settings.CreateAlbumFolder = folderStructure.CreateAlbumFolder;
        settings.CreateCDFolder = folderStructure.CreateCDFolder;
        settings.CreateStructurePlaylist = folderStructure.CreateStructurePlaylist;
        settings.CreateSingleFolder = folderStructure.CreateSingleFolder;
        settings.CreatePlaylistFolder = folderStructure.CreatePlaylistFolder;

        if (!string.IsNullOrWhiteSpace(folderStructure.ArtistNameTemplate))
        {
            settings.ArtistNameTemplate = folderStructure.ArtistNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.AlbumNameTemplate))
        {
            settings.AlbumNameTemplate = folderStructure.AlbumNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.PlaylistNameTemplate))
        {
            settings.PlaylistNameTemplate = folderStructure.PlaylistNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.IllegalCharacterReplacer))
        {
            settings.IllegalCharacterReplacer = folderStructure.IllegalCharacterReplacer.Trim();
        }
    }

    private static string? NormalizeLocalArtworkFormat(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "jpg",
            "png"
        };

        var normalized = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => allowed.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => value.ToLowerInvariant())
            .ToList();

        return normalized.Count == 0 ? null : string.Join(",", normalized);
    }

    private async Task PopulateAppleExtrasAsync(
        string platform,
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        CancellationToken token)
    {
        var itunesConfig = LoadConfig(config.Custom, ItunesPlatform, new ItunesMatchConfig());
        var saveAnimatedArtwork = itunesConfig.AnimatedArtwork ?? settings.SaveAnimatedArtwork;
        var wantsAnimatedArtwork = saveAnimatedArtwork
            && WantsArtworkFromSettings(config, settings);
        var wantsAppleLyrics = ShouldRequestAnyLyrics(config, settings);
        var wantsCatalogMetadata = string.Equals(platform, ItunesPlatform, StringComparison.OrdinalIgnoreCase)
            && HasAnyTags(
                config,
                GenreTag,
                IsrcTag,
                LabelTag,
                CopyrightTag,
                ComposerTag,
                InvolvedPeopleTag,
                OtherTagsTag,
                ExplicitTag);
        if (!wantsAnimatedArtwork && !wantsAppleLyrics && !wantsCatalogMetadata)
        {
            return;
        }

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
            ? "us"
            : settings.AppleMusic.Storefront;
        var appleIdentity = await ResolveAppleIdentityForExtrasAsync(track, storefront, settings, token);

        if (wantsCatalogMetadata && !string.IsNullOrWhiteSpace(appleIdentity?.AppleId))
        {
            await PopulateAppleCatalogMetadataAsync(
                track,
                config,
                IsLocalAtmosFile(filePath),
                appleIdentity.AppleId,
                storefront,
                settings.AppleMusic?.MediaUserToken,
                token);
        }

        if (wantsAppleLyrics)
        {
            await PopulatePlatformLyricsAsync(
                AppleProvider,
                filePath,
                track,
                config,
                settings,
                token,
                appleIdentity?.AppleId);
        }

        if (!wantsAnimatedArtwork)
        {
            return;
        }

        var outputDir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return;
        }

        var maxResolution = settings.Video?.AppleMusicVideoMaxResolution ?? 2160;
        var baseFileName = BuildAlbumArtworkBaseFileName(track, settings);

        await TryPopulateAppleAnimatedArtworkAsync(
            track,
            outputDir,
            storefront,
            maxResolution,
            baseFileName,
            appleIdentity,
            settings,
            token);
    }

    private async Task PopulateAppleCatalogMetadataAsync(
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        bool localFileIsAtmos,
        string appleTrackId,
        string storefront,
        string? mediaUserToken,
        CancellationToken token)
    {
        using var payload = await _appleMusicCatalogService.GetSongAsync(
            appleTrackId,
            storefront,
            "en-US",
            token,
            mediaUserToken);
        if (!payload.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0
            || !data[0].TryGetProperty("attributes", out var attributes))
        {
            return;
        }

        ApplyAppleCatalogMetadata(track, config, attributes, localFileIsAtmos);
    }

    private static void ApplyAppleCatalogMetadata(
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        JsonElement attributes,
        bool localFileIsAtmos)
    {
        if (HasAnyTags(config, GenreTag)
            && attributes.TryGetProperty("genreNames", out var genreNames)
            && genreNames.ValueKind == JsonValueKind.Array)
        {
            var genres = genreNames.EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Where(value => !string.Equals(value, "Music", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (genres.Count > 0)
            {
                track.Genres = track.Genres
                    .Concat(genres)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        var isrc = TryGetJsonString(attributes, "isrc");
        if (HasAnyTags(config, IsrcTag) && !string.IsNullOrWhiteSpace(isrc) && !localFileIsAtmos)
        {
            track.Isrc = isrc;
        }

        var recordLabel = TryGetJsonString(attributes, "recordLabel");
        if (HasAnyTags(config, LabelTag) && !string.IsNullOrWhiteSpace(recordLabel))
        {
            track.Label = recordLabel;
        }

        var composer = TryGetJsonString(attributes, "composerName");
        if (HasAnyTags(config, ComposerTag) && !string.IsNullOrWhiteSpace(composer))
        {
            track.Other[ComposerTag] = SplitCompositeRawValues(composer).ToList();
        }
        if (HasAnyTags(config, InvolvedPeopleTag) && !string.IsNullOrWhiteSpace(composer))
        {
            track.Other[InvolvedPeopleTag] = [.. SplitCompositeRawValues(composer).Select(name => $"Composer: {name}")];
        }

        var copyright = TryGetJsonString(attributes, "copyright");
        if (HasAnyTags(config, CopyrightTag) && !string.IsNullOrWhiteSpace(copyright))
        {
            track.Other[CopyrightTag] = [copyright];
        }

        if (HasAnyTags(config, ExplicitTag)
            && attributes.TryGetProperty("contentRating", out var rating)
            && rating.ValueKind == JsonValueKind.String)
        {
            track.Explicit = string.Equals(rating.GetString(), "explicit", StringComparison.OrdinalIgnoreCase);
        }

        if (!HasAnyTags(config, OtherTagsTag))
        {
            return;
        }

        track.RawTagsToRemove.Add("APPLE_AUDIO_TRAITS");
        track.RawTagsToRemove.Add("APPLE_IS_ATMOS");
        if (!localFileIsAtmos)
        {
            return;
        }

        track.Other["APPLE_AUDIO_TRAITS"] = ["atmos"];
        track.Other["APPLE_IS_ATMOS"] = ["1"];
    }

    private static bool IsLocalAtmosFile(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var codec = file.Properties.Codecs == null
                ? string.Empty
                : string.Join(' ', file.Properties.Codecs.Select(value => value.Description ?? string.Empty));
            return AudioVariantResolver.IsAtmosVariant(
                file.Properties.AudioChannels,
                codec,
                Path.GetExtension(filePath),
                filePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string? TryGetJsonString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private async Task TryPopulateAppleAnimatedArtworkAsync(
        AutoTagTrack track,
        string outputDir,
        string storefront,
        int maxResolution,
        string baseFileName,
        TrackIdentityResolution? appleIdentity,
        DeezSpoTagSettings settings,
        CancellationToken token)
    {
        var artist = track.Artists.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(appleIdentity?.AppleId))
        {
            return;
        }

        try
        {
            var animatedResult = await AppleQueueHelpers.SaveAnimatedArtworkAsync(
                _appleMusicCatalogService,
                _httpClientFactory,
                new AppleQueueHelpers.AnimatedArtworkSaveRequest
                {
                    AppleId = appleIdentity?.AppleId,
                    Artist = appleIdentity?.AppleArtistName ?? artist,
                    Album = appleIdentity?.AppleAlbumName ?? track.Album,
                    SquareFileName = settings.AnimatedArtworkSquareFileName,
                    TallFileName = settings.AnimatedArtworkTallFileName,
                    Storefront = storefront,
                    MaxResolution = maxResolution,
                    OutputDir = outputDir,
                    Logger = _logger,
                    CollectionType = string.IsNullOrWhiteSpace(appleIdentity?.AppleAlbumId) ? null : "album",
                    CollectionId = appleIdentity?.AppleAlbumId,
                    OutputFormats = AppleQueueHelpers.ResolveAnimatedArtworkFormats(settings),
                    MaxSizeMb = AppleQueueHelpers.ResolveAnimatedArtworkMaxSizeMb(settings),
                    SaveSquareVariant = settings.SaveAnimatedArtwork && settings.SaveSquareAnimatedArtwork,
                    SaveTallVariant = settings.SaveAnimatedArtwork && settings.SaveTallAnimatedArtwork
                },
                token);

            if (animatedResult.Paths.Count > 0)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("AutoTag Apple animated artwork saved for {Title} in {OutputDir}", SanitizeLogValue(track.Title), SanitizeLogValue(outputDir));
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "AutoTag Apple animated artwork {Status} for {Title}: {Message}",
                        animatedResult.Status,
                        SanitizeLogValue(track.Title),
                        animatedResult.Message);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple animated artwork resolution failed for {Title}.", SanitizeLogValue(track.Title));
            }
        }
    }

    private async Task PopulatePlatformLyricsAsync(
        string platform,
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        CancellationToken token,
        string? providerTrackId = null)
    {
        var provider = NormalizeLyricsLookupSource(platform.Trim().ToLowerInvariant());
        if (!LyricsProviderRegistry.IsRegistered(provider))
        {
            return;
        }

        var request = RestrictLyricsRequestToProvider(
            BuildLyricsPopulationRequest(filePath, track, config, settings),
            provider);
        if (!request.ShouldFetch)
        {
            return;
        }

        if (request.HasAllRequestedLyrics())
        {
            return;
        }

        if (provider is not LyricsProviderRegistry.YouLyPlus and not LyricsProviderRegistry.BetterLyrics
            && string.IsNullOrWhiteSpace(providerTrackId)
            && string.IsNullOrWhiteSpace(track.TrackId) &&
            string.IsNullOrWhiteSpace(track.Url) &&
            string.IsNullOrWhiteSpace(track.Isrc))
        {
            return;
        }

        var lookupTrack = BuildLyricsLookupTrack(track, provider);
        if (!string.IsNullOrWhiteSpace(providerTrackId))
        {
            lookupTrack.Id = providerTrackId;
            lookupTrack.SourceId = providerTrackId;
            AddLookupUrl(lookupTrack.Urls, $"{provider}_track_id", providerTrackId);
        }
        var lookupSettings = BuildLyricsLookupSettings(
            settings,
            request.WantsSynced,
            request.WantsUnsynced,
            request.WantsTtml);
        lookupSettings.LyricsFallbackEnabled = true;
        lookupSettings.LyricsFallbackOrder = provider;
        var providerOptions = BuildLyricsProviderOptions(config.Custom);
        LyricsBase? lyrics = null;
        try
        {
            lyrics = await _downloadLyricsService.ResolveLyricsAsync(
                lookupTrack,
                lookupSettings,
                providerOptions,
                token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Lyrics resolution failed for platform {Platform} and track {Title}.", SanitizeLogValue(provider), SanitizeLogValue(track.Title));
            }
            return;
        }

        if (lyrics == null || !lyrics.IsLoaded())
        {
            return;
        }

        ApplyResolvedLyrics(track, lyrics, request, settings);
    }

    private static LyricsPopulationRequest RestrictLyricsRequestToProvider(
        LyricsPopulationRequest request,
        string provider)
    {
        var supportsTtml = LyricsProviderRegistry.TryGet(provider, out var descriptor)
            && (descriptor.SupportsNativeTtml || descriptor.SupportsWordSynchronized);
        return request with { WantsTtml = request.WantsTtml && supportsTtml };
    }

    private async Task<TrackIdentityResolution?> ResolveAppleIdentityForExtrasAsync(
        AutoTagTrack track,
        string storefront,
        DeezSpoTagSettings settings,
        CancellationToken token)
    {
        try
        {
            var artist = track.Artists.FirstOrDefault();
            var persistedAppleId = TryGetFirstOtherValue(track.Other, AutoTagIdentityTags.AppleTrackIdAliases)
                ?? (track.Other.ContainsKey(AutoTagIdentityTags.AppleTrackId) ? track.TrackId : null);
            var identity = await _trackIdentityResolver.ResolveAsync(
                new TrackIdentityResolutionRequest(
                    SourcePlatform: null,
                    SourceUrl: track.Url,
                    Title: track.Title,
                    Artist: artist,
                    Album: track.Album,
                    Isrc: track.Isrc,
                    DurationMs: track.Duration.HasValue ? (int)Math.Round(track.Duration.Value.TotalMilliseconds) : null,
                    AppleId: persistedAppleId,
                    TargetPlatforms: new[] { "apple" },
                    Storefront: storefront,
                    Language: "en-US",
                    MediaUserToken: settings.AppleMusic?.MediaUserToken),
                token);
            return string.IsNullOrWhiteSpace(identity.AppleId) ? null : identity;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Central Apple identity lookup failed for animated artwork {Isrc}.", SanitizeLogValue(track.Isrc));
            }
            return null;
        }
    }

    private static string BuildAlbumArtworkBaseFileName(AutoTagTrack track, DeezSpoTagSettings settings)
    {
        var albumTitle = string.IsNullOrWhiteSpace(track.Album) ? "Unknown Album" : track.Album.Trim();
        var primaryArtist = track.Artists.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(primaryArtist))
        {
            primaryArtist = UnknownArtist;
        }

        var albumModel = new DeezSpoTag.Core.Models.Album(albumTitle)
        {
            MainArtist = new DeezSpoTag.Core.Models.Artist(primaryArtist),
            Artists = new List<string> { primaryArtist }
        };

        return PathTemplateGenerator.GenerateAlbumName(
            settings.CoverImageTemplate,
            albumModel,
            settings,
            playlist: null);
    }

    private static Track BuildLyricsLookupTrack(AutoTagTrack track, string platformId)
    {
        var normalizedPlatform = NormalizeLyricsLookupSource(platformId);
        var lookupTrack = new Track
        {
            Id = track.TrackId ?? string.Empty,
            Source = normalizedPlatform,
            SourceId = track.TrackId,
            Title = track.Title ?? string.Empty,
            Album = new Album(track.Album ?? string.Empty),
            ISRC = track.Isrc ?? string.Empty,
            DownloadURL = track.Url ?? string.Empty,
            Duration = track.Duration.HasValue ? (int)Math.Max(0, track.Duration.Value.TotalSeconds) : 0
        };

        var primaryArtist = track.Artists.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(primaryArtist))
        {
            lookupTrack.MainArtist = new DeezSpoTag.Core.Models.Artist
            {
                Id = "0",
                Name = primaryArtist,
                Role = "Main"
            };
            lookupTrack.Artists = new List<string> { primaryArtist };
            lookupTrack.Artist["Main"] = new List<string> { primaryArtist };
        }

        if (!string.IsNullOrWhiteSpace(track.Url))
        {
            lookupTrack.Urls[normalizedPlatform] = track.Url;
        }

        if (string.Equals(normalizedPlatform, DeezerPlatform, StringComparison.OrdinalIgnoreCase))
        {
            AddLookupUrl(lookupTrack.Urls, "deezer_track_id", track.TrackId);
        }
        else if (string.Equals(normalizedPlatform, SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
        {
            AddLookupUrl(lookupTrack.Urls, "spotify_track_id", track.TrackId);
        }
        else if (string.Equals(normalizedPlatform, AppleProvider, StringComparison.OrdinalIgnoreCase))
        {
            AddLookupUrl(lookupTrack.Urls, "apple_track_id", track.TrackId);
        }

        var other = track.Other ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        AddLookupUrl(lookupTrack.Urls, "deezer_track_id", TryGetFirstOtherValue(other, DeezerTrackIdTag, "DEEZERID", "DEEZER_ID"));
        AddLookupUrl(lookupTrack.Urls, "spotify_track_id", TryGetFirstOtherValue(other, SpotifyTrackIdTag, SpotifyTrackIdLegacyTag, SpotifyIdLegacyTag, SpotifyIdUnderscoreLegacyTag));
        AddLookupUrl(lookupTrack.Urls, "apple_track_id", TryGetFirstOtherValue(other, "APPLE_TRACK_ID", "APPLEID", "ITUNES_TRACK_ID", "ITUNESCATALOGID"));

        AddLookupUrl(lookupTrack.Urls, DeezerPlatform, TryGetFirstOtherValue(other, "DEEZER_URL"));
        AddLookupUrl(lookupTrack.Urls, SpotifyPlatform, TryGetFirstOtherValue(other, SpotifyUrlTag));
        AddLookupUrl(lookupTrack.Urls, AppleProvider, TryGetFirstOtherValue(other, "APPLE_URL", "ITUNES_URL"));

        if (string.IsNullOrWhiteSpace(lookupTrack.DownloadURL))
        {
            lookupTrack.DownloadURL = TryGetFirstOtherValue(other, "source_url", "URL", WwwAudioFileTag)
                ?? TryGetFirstOtherValue(other, "DEEZER_URL", SpotifyUrlTag, "APPLE_URL", "ITUNES_URL")
                ?? string.Empty;
        }

        return lookupTrack;
    }
}
