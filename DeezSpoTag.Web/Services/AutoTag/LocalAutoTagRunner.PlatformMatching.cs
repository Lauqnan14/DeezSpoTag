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

    private async Task ApplyCentralIdentityForManualEnrichmentAsync(
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        Action<string> logCallback,
        CancellationToken token)
    {
        var resolution = await _trackIdentityResolver.ResolveAsync(
            new TrackIdentityResolutionRequest(
                SourcePlatform: ShazamPlatform,
                SourceUrl: AutoTagTagValueReader.ReadFirstTagValue(info, "URL", WwwAudioFileTag),
                Title: info.Title,
                Artist: info.Artists.FirstOrDefault() ?? info.Artist,
                Album: info.Album,
                Isrc: info.Isrc,
                DurationMs: info.DurationSeconds is > 0 ? info.DurationSeconds.Value * 1000 : null,
                TargetPlatforms: ["spotify", "deezer", "apple", "qobuz", "tidal", "amazon"],
                PreferredReleaseType: config.ManualReleasePreference),
            token);

        info.Title = FirstNonEmpty(resolution.Title, info.Title) ?? info.Title;
        info.Artist = FirstNonEmpty(resolution.Artist, info.Artist) ?? info.Artist;
        info.Album = FirstNonEmpty(resolution.Album, info.Album);
        info.Isrc = FirstNonEmpty(resolution.Isrc, info.Isrc);
        var spotifyUrl = FirstNonEmpty(
            NormalizeSpotifyTrackUrl(resolution.SpotifyUrl),
            NormalizeSpotifyTrackUrl(AutoTagTagValueReader.ReadFirstTagValue(info, "SHAZAM_SPOTIFY_URL")),
            NormalizeSpotifyTrackUrl(resolution.SpotifyId));
        var spotifyId = FirstNonEmpty(resolution.SpotifyId, ExtractSpotifyTrackIdFromTags(info.Tags));
        AddResolvedIdentity(info, SpotifyTrackIdTag, spotifyId);
        AddResolvedIdentity(info, SpotifyUrlTag, spotifyUrl);
        AddResolvedIdentity(info, DeezerTrackIdTag, resolution.DeezerId);
        AddResolvedIdentity(info, "DEEZER_URL", resolution.DeezerUrl);
        AddResolvedIdentity(info, "ITUNES_TRACK_ID", resolution.AppleId);
        AddResolvedIdentity(info, "APPLE_MUSIC_TRACK_ID", resolution.AppleId);
        AddResolvedIdentity(info, "APPLE_MUSIC_URL", resolution.AppleUrl);
        AddResolvedIdentity(info, "QOBUZ_TRACK_ID", resolution.QobuzId);
        AddResolvedIdentity(info, "TIDAL_TRACK_ID", resolution.TidalId);
        AddResolvedIdentity(info, "AMAZON_TRACK_ID", resolution.AmazonId);
        logCallback(
            $"onetagger_autotag: central identity resolved spotify={HasValue(resolution.SpotifyId)}, deezer={HasValue(resolution.DeezerId)}, apple={HasValue(resolution.AppleId)}, qobuz={HasValue(resolution.QobuzId)}, tidal={HasValue(resolution.TidalId)}, amazon={HasValue(resolution.AmazonId)}");
    }

    private static void AddResolvedIdentity(AutoTagAudioInfo info, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            info.Tags[key] = [value.Trim()];
        }
    }

    private static string HasValue(string? value) => string.IsNullOrWhiteSpace(value) ? "no" : "yes";

    private async Task<AutoTagMatchResult?> ResolvePlatformMatchAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo info)
    {
        var useMatchCache = CanUseMatchCache(info);
        var matchCacheKey = useMatchCache
            ? BuildMatchCacheKey(context.Platform, info, context.Plan.Config, context.Plan.Settings, context.Plan.MatchingConfig)
            : string.Empty;
        if (IsPlatformUnavailable(context.JobMatchCache, context.Platform))
        {
            context.MatchFailureOutcome = "provider_unavailable";
            context.MatchFailureMessage = $"{context.Platform} skipped after earlier match timeout";
            context.LogCallback(
                $"onetagger_autotag: {context.Platform} skipped; platform unavailable after earlier match timeout");
            return null;
        }

        if (useMatchCache && TryGetCachedMatch(context.JobMatchCache, matchCacheKey, out var cachedMatch))
        {
            PreserveAtmosFileIsrc(context.File, info, cachedMatch?.Track);
            return cachedMatch;
        }

        AutoTagMatchResult? match;
        context.MatchFailureOutcome = null;
        context.MatchFailureMessage = null;
        try
        {
            match = await RunPlatformMatchWithTimeoutAsync(
                context,
                CreateCatalogLookupInfo(context.File, info),
                new PlatformMatchContext
                {
                    FilePath = context.File,
                    Config = context.Plan.Config,
                    Settings = context.Plan.Settings,
                    MatchingConfig = context.Plan.MatchingConfig,
                    ShazamCache = context.Plan.ShazamCache,
                    IsManualEnrichment = IsManualEnrichment(context.Plan.Config)
                });
            if (match == null)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag platform {Platform} failed for {File}", SanitizeLogValue(context.Platform), SanitizeLogValue(context.File));
            context.MatchFailureOutcome = IsProviderNotConfigured(ex) ? "not_configured" : "provider_error";
            context.MatchFailureMessage = ex.Message;
            return null;
        }

        PreserveAtmosFileIsrc(context.File, info, match.Track);
        if (useMatchCache)
        {
            StoreCachedMatch(context.JobMatchCache, matchCacheKey, match);
        }

        return match;
    }

    private async Task<AutoTagMatchResult?> RunPlatformMatchWithTimeoutAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo info,
        PlatformMatchContext matchContext)
    {
        context.LogCallback($"onetagger_autotag: {context.Platform} match starting");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
        var matchTask = MatchPlatformAsync(
            context.Platform,
            info,
            matchContext,
            timeoutSource.Token);

        try
        {
            var match = await matchTask.WaitAsync(PlatformMatchTimeout, context.Token);
            context.LogCallback($"onetagger_autotag: {context.Platform} match completed");
            return match;
        }
        catch (TimeoutException)
        {
            timeoutSource.Cancel();
            ObserveBackgroundTask(matchTask);
            MarkPlatformUnavailable(context.JobMatchCache, context.Platform);
            context.MatchFailureOutcome = "provider_error";
            context.MatchFailureMessage = $"provider timed out after {PlatformMatchTimeout.TotalSeconds:0}s";
            context.LogCallback(
                $"onetagger_autotag: {context.Platform} match timed out after {PlatformMatchTimeout.TotalSeconds:0}s; skipping remaining {context.Platform} matches in this run");
            return null;
        }
    }

    private async Task ApplyResolvedMatchAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo info,
        AutoTagAudioInfo validationInfo,
        AutoTagMatchResult match,
        bool usedShazamForStatus,
        ProviderTagPlan tagPlan)
    {
        var isManualEnrichment = IsManualEnrichment(context.Plan.Config);
        if (string.Equals(context.Platform, BoomplayPlatform, StringComparison.OrdinalIgnoreCase))
        {
            var boomplayGuardReason = EvaluateBoomplayReliabilityGuard(validationInfo, match, context.Plan.MatchingConfig);
            if (!string.IsNullOrWhiteSpace(boomplayGuardReason))
            {
                EmitSkippedStatus(context, boomplayGuardReason, usedShazamForStatus, "rejected", tagPlan);
                return;
            }
        }

        if (isManualEnrichment
            && !AutoTagReleaseCategory.MatchesPreference(
                match.Track.ReleaseType,
                match.Track.TrackTotal,
                context.Plan.Config.ManualReleasePreference))
        {
            HandleRejectedManualRelease(
                context,
                info,
                match.Track,
                $"Provider returned a {match.Track.ReleaseType ?? "different"} release instead of the requested {context.Plan.Config.ManualReleasePreference} release.",
                usedShazamForStatus,
                tagPlan,
                match);
            return;
        }

        context.Plan.FrozenManualReleases.TryGetValue(context.FileIndex, out var frozenRelease);
        if (isManualEnrichment && frozenRelease != null)
        {
            var frozenMismatch = EvaluateGlobalMismatchGuard(
                frozenRelease.ToAudioInfo(),
                match,
                context.Plan.MatchingConfig);
            if (!string.IsNullOrWhiteSpace(frozenMismatch)
                || !AlbumsReferToSameRelease(frozenRelease.Album, match.Track.Album))
            {
                EmitSkippedStatus(
                    context,
                    frozenMismatch ?? "provider release conflicts with the frozen manual-enrichment release",
                    usedShazamForStatus,
                    outcome: "rejected",
                    tagPlan: tagPlan);
                return;
            }

            frozenRelease.ApplyTo(match.Track);
        }

        var identityIsTrusted = IsTrustedSourceIdentity(validationInfo, context.File, context.Plan.Config);
        var validationBasis = isManualEnrichment
            || (usedShazamForStatus
                && !string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase))
            ? info
            : validationInfo;
        var mismatchReason = EvaluateGlobalMismatchGuard(
            validationBasis,
            match,
            context.Plan.MatchingConfig,
            context.File,
            treatSourceAsUntrusted: !identityIsTrusted);
        if (!string.IsNullOrWhiteSpace(mismatchReason))
        {
            if (string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase))
            {
                EmitReviewStatus(
                    context,
                    mismatchReason,
                    usedShazamForStatus,
                    AutoTagReviewMetadata.FromMatch(validationInfo, match.Track),
                    "rejected",
                    tagPlan,
                    match);
                context.Plan.ReviewedFiles.Add(context.File);
                return;
            }

            EmitSkippedStatus(context, mismatchReason, usedShazamForStatus, "rejected", tagPlan);
            return;
        }

        // Album/folder-aware edition handling: when the file's album and the matched
        // candidate are the same album but different editions (standard vs deluxe,
        // 2009 vs 2011 remaster), never silently rewrite the album identity — keep
        // the edition the user's download source provided, or flag for review when
        // the profile opts into edition-conflict review.
        var editionConflict = PreserveAlbumEditionIdentity(validationBasis, match.Track);
        if (editionConflict && context.Plan.Config.EditionConflictReview == true)
        {
            var editionMessage = "album edition conflict: file and provider describe different editions of the same album";
            EmitReviewStatus(
                context,
                editionMessage,
                usedShazamForStatus,
                AutoTagReviewMetadata.FromMatch(validationInfo, match.Track),
                "rejected",
                tagPlan,
                match);
            context.Plan.ReviewedFiles.Add(context.File);
            return;
        }

        EmitTaggingStatus(context, match.Accuracy, usedShazamForStatus);

        try
        {
            var originalFile = context.File;
            PreserveSourceTitleWording(validationBasis, match.Track);
            PreserveRicherArtistCreditsFromSource(info, match.Track, context.Plan.Settings);
            ApplyFolderContextGuards(context.File, context.Plan.TargetPath, match.Track);
            ApplyAlbumIdentityConsensus(context, validationBasis, match.Track);
            if (isManualEnrichment && frozenRelease == null)
            {
                frozenRelease = ManualReleaseIdentity.FromTrack(match.Track);
                context.Plan.FrozenManualReleases[context.FileIndex] = frozenRelease;
            }
            if (!isManualEnrichment
                || !context.Plan.MaterializedManualPaths.TryGetValue(context.FileIndex, out var materializedPath))
            {
                materializedPath = MaterializeFileToTemplatePath(
                    context.File,
                    match.Track,
                    context.Plan.Config,
                    context.Plan.Settings,
                    context.Plan.TagSettings);
                if (isManualEnrichment)
                {
                    context.Plan.MaterializedManualPaths[context.FileIndex] = materializedPath;
                    PersistManualMaterializedTargetPath(
                        context.Plan,
                        context.File,
                        materializedPath);
                }
            }
            context.File = materializedPath;
            context.Plan.Files[context.FileIndex] = context.File;
            var presentBefore = CapturePresentTags(context.File, context.Plan.Config, context.Platform, tagPlan.Eligible);
            await RunBoundedOptionalStepAsync(
                context,
                "artwork fallback",
                ArtworkFallbackTimeout,
                stepToken => EnsureArtworkFallbackAsync(context, info, match.Track, stepToken));
            if (isManualEnrichment && frozenRelease != null)
            {
                frozenRelease.Art ??= match.Track.Art;
                frozenRelease.ApplyTo(match.Track);
            }
            if (ShouldRequestAnyLyrics(context.Plan.Config, context.Plan.Settings))
            {
                await RunBoundedOptionalStepAsync(
                    context,
                    "lyrics",
                    LyricsResolutionTimeout,
                    stepToken => PopulatePlatformLyricsAsync(
                        context.Platform,
                        context.File,
                        match.Track,
                        context.Plan.Config,
                        context.Plan.Settings,
                        stepToken));
            }
            if (context.Plan.AttemptedAppleExtras.Add(context.FileIndex))
            {
                await RunBoundedOptionalStepAsync(
                    context,
                    "Apple extras",
                    AppleExtrasTimeout,
                    stepToken => PopulateAppleExtrasAsync(
                        context.Platform,
                        context.File,
                        match.Track,
                        context.Plan.Config,
                        context.Plan.Settings,
                        stepToken));
            }
            var writeResult = await TagFileAsync(
                context.File,
                match.Track,
                context.Plan.TagSettings,
                context.Plan.Config,
                context.Plan.Settings,
                context.Platform,
                context.Token);
            var returnedTags = ResolveReturnedEligibleTags(match.Track, tagPlan);
            returnedTags.IntersectWith(writeResult.AttemptedTags);
            var persistenceFailures = VerifyPersistedTags(
                context.File,
                context.Plan.Config,
                context.Platform,
                match.Track,
                returnedTags);
            if (persistenceFailures.Remove(SupportedTag.AlbumArt))
            {
                returnedTags.Remove(SupportedTag.AlbumArt);
                context.LogCallback(
                    $"onetagger_autotag: {context.Platform} artwork was not persisted; retaining provider metadata and reporting artwork as missing");
            }
            if (persistenceFailures.Remove(SupportedTag.OtherTags))
            {
                returnedTags.Remove(SupportedTag.OtherTags);
                context.LogCallback(
                    $"onetagger_autotag: {context.Platform} extra tags were not persisted; identity tags were kept");
            }
            if (persistenceFailures.Count > 0)
            {
                throw new IOException($"Metadata persistence verification failed for: {string.Join(", ", persistenceFailures.Select(ToTagKey))}.");
            }
            if (isManualEnrichment)
            {
                await EnsureManualArtistArtworkAsync(context, info, match.Track);
            }
            context.Plan.TaggedByAnyPlatform.Add(originalFile);
            context.Plan.TaggedByAnyPlatform.Add(context.File);
            context.Plan.TaggedFileIndices.Add(context.FileIndex);
            var writtenTags = returnedTags
                .Where(tag => ShouldOverwriteTag(context.Plan.Config, tag) || !presentBefore.Contains(tag))
                .ToHashSet();
            var missingTags = tagPlan.Eligible
                .Where(tag => !returnedTags.Contains(tag))
                .ToHashSet();
            var outcome = writtenTags.Count > 0 ? "tagged" : "matched_no_changes";
            EmitTaggedStatus(
                context,
                match.Accuracy,
                usedShazamForStatus,
                outcome,
                tagPlan,
                match,
                returnedTags,
                writtenTags,
                missingTags);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag failed for {File} on {Platform}", SanitizeLogValue(context.File), SanitizeLogValue(context.Platform));
            EmitErrorStatus(context, ex.Message, usedShazamForStatus, "provider_error", tagPlan, match);
        }
    }

    private static string? EvaluateGlobalMismatchGuard(
        AutoTagAudioInfo info,
        AutoTagMatchResult match,
        AutoTagMatchingConfig matchingConfig)
        => EvaluateGlobalMismatchGuard(info, match, matchingConfig, filePath: null, treatSourceAsUntrusted: false);

    private static string? EvaluateGlobalMismatchGuard(
        AutoTagAudioInfo info,
        AutoTagMatchResult match,
        AutoTagMatchingConfig matchingConfig,
        string? filePath,
        bool treatSourceAsUntrusted)
    {
        if (match.Track == null)
        {
            return null;
        }

        if (treatSourceAsUntrusted
            || TrackIdentityTrust.IsUntrustedIdentity(info.Title, info.Artist, filePath))
        {
            return null;
        }

        var incomingFullTitle = OneTaggerMatching.FullTitle(match.Track.Title, match.Track.Version);
        if (TrackTitleMatcher.HasVersionDrift(info.Title, incomingFullTitle))
        {
            return "match rejected by quality guard (version drift)";
        }

        if (IsAuthoritativeIdMatch(match.MatchStrategy))
        {
            return null;
        }

        if (HasMatchingIsrc(info.Isrc, match.Track.Isrc))
        {
            return null;
        }

        if (!TrackTitleMatcher.HasCompatibleTitleIdentity(info.Title, incomingFullTitle))
        {
            return "match rejected by quality guard (title identity)";
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

        var artistStrictness = Math.Clamp(matchingConfig.Strictness - 0.05d, 0.45d, 0.95d);
        var artistCompatible = sourceArtists.Count == 0
            || incomingArtists.Count == 0
            || AreArtistIdentitiesCompatibleForOverwrite(sourceArtists, incomingArtists, artistStrictness);

        if (!artistCompatible)
        {
            return "match rejected by quality guard (artist mismatch)";
        }

        var sourceTitle = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(info.Title));
        var incomingTitle = AutoTagSimilarity.NormalizeText(
            OneTaggerMatching.CleanTitleMatching(
                OneTaggerMatching.FullTitle(match.Track.Title, match.Track.Version)));
        var titleSimilarity = AutoTagSimilarity.ComputeScore(sourceTitle, incomingTitle);

        var durationMismatch = HasDurationMismatch(info.DurationSeconds, match.Track.Duration, matchingConfig.MaxDurationDifferenceSeconds);
        var minTitleSimilarity = Math.Clamp(matchingConfig.Strictness - 0.08d, 0.62d, 0.95d);

        if (titleSimilarity < minTitleSimilarity)
        {
            return $"match rejected by quality guard (title similarity {titleSimilarity:0.000} < {minTitleSimilarity:0.000})";
        }

        if (durationMismatch && titleSimilarity < 0.90d)
        {
            return "match rejected by quality guard (duration mismatch)";
        }

        return null;
    }

    private static bool IsAuthoritativeIdMatch(string? matchStrategy)
        => (matchStrategy ?? string.Empty).Trim().ToLowerInvariant() is "id" or "id_first";

    private static AutoTagAudioInfo CreateCatalogLookupInfo(string filePath, AutoTagAudioInfo source)
        => CreateCatalogLookupInfo(IsLocalAtmosFile(filePath), source);

    private static AutoTagAudioInfo CreateCatalogLookupInfo(bool localFileIsAtmos, AutoTagAudioInfo source)
    {
        if (!localFileIsAtmos)
        {
            return source;
        }

        var lookup = CloneAudioInfo(source);
        lookup.Isrc = ReadFirstTagValue(lookup.Tags, "SHAZAM_ISRC");
        return lookup;
    }

    private static bool HasMatchingIsrc(string? sourceIsrc, string? incomingIsrc)
    {
        if (string.IsNullOrWhiteSpace(sourceIsrc) || string.IsNullOrWhiteSpace(incomingIsrc))
        {
            return false;
        }

        return string.Equals(sourceIsrc.Trim(), incomingIsrc.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDurationMismatch(int? sourceDurationSeconds, TimeSpan? incomingDuration, int maxDifferenceSeconds)
    {
        if (!sourceDurationSeconds.HasValue || !incomingDuration.HasValue || sourceDurationSeconds.Value <= 0 || incomingDuration.Value <= TimeSpan.Zero)
        {
            return false;
        }

        var incomingSeconds = (int)Math.Round(incomingDuration.Value.TotalSeconds);
        return Math.Abs(sourceDurationSeconds.Value - incomingSeconds) > Math.Max(1, maxDifferenceSeconds);
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

    private async Task<AutoTagMatchResult?> MatchPlatformAsync(
        string platform,
        AutoTagAudioInfo info,
        PlatformMatchContext context,
        CancellationToken token)
    {
        var enableLyrics = ShouldRequestAnyLyrics(context.Config, context.Settings);
        var hasLyricsSidecar = enableLyrics && LyricsSidecarsSatisfyPreference(context.FilePath, context.Config, context.Settings);
        var beatportReleaseMeta = HasAnyTags(context.Config, AlbumArtistTag, TrackTotalTag);
        var traxsourceExtend = HasAnyTags(context.Config, AlbumArtTag, AlbumTag, CatalogNumberTag, ReleaseIdTag, AlbumArtistTag, TrackNumberTag, TrackTotalTag);
        var traxsourceAlbumMeta = HasAnyTags(context.Config, CatalogNumberTag, TrackNumberTag, AlbumArtTag, TrackTotalTag, AlbumArtistTag);
        var discogsNeedsLabelCatalog = HasAnyTags(context.Config, LabelTag, CatalogNumberTag);
        if (string.Equals(platform, LyricsPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return await MatchLyricsProviderAsync(
                string.Join(",", ResolveLyricsProviderOrder(context.Config)),
                info,
                context,
                enableLyrics,
                hasLyricsSidecar,
                token);
        }

        switch (platform.Trim().ToLowerInvariant())
        {
            case "musicbrainz":
                return await _musicBrainzMatcher.MatchAsync(info, context.MatchingConfig, LoadConfig(context.Config.Custom, "musicbrainz", new MusicBrainzMatchConfig()), token);
            case "beatport":
                return await _beatportMatcher.MatchAsync(info, context.MatchingConfig, LoadConfig(context.Config.Custom, "beatport", new BeatportMatchConfig()), beatportReleaseMeta, context.Config.MatchById, token);
            case "discogs":
                return await _discogsMatcher.MatchAsync(info, context.MatchingConfig, LoadConfig(context.Config.Custom, "discogs", new DiscogsConfig()), context.Config.MatchById, discogsNeedsLabelCatalog, token);
            case "traxsource":
                return await _traxsourceMatcher.MatchAsync(info, context.MatchingConfig, traxsourceExtend, traxsourceAlbumMeta, token);
            case "bandcamp":
                return await _bandcampMatcher.MatchAsync(info, context.MatchingConfig, token);
            case "bpmsupreme":
                return await _bpmSupremeMatcher.MatchAsync(info, context.MatchingConfig, LoadConfig(context.Config.Custom, "bpmsupreme", new BpmSupremeConfig()), token);
            case ItunesPlatform:
                return await _itunesMatcher.MatchAsync(info, context.MatchingConfig, LoadConfig(context.Config.Custom, ItunesPlatform, new ItunesMatchConfig()), token);
            case SpotifyPlatform:
                return await _spotifyMatcher.MatchAsync(info, context.MatchingConfig, token);
            case DeezerPlatform:
                var deezerConfig = ResolveDeezerMatchConfig(context.Config);
                return await _deezerMatcher.MatchAsync(info, context.MatchingConfig, deezerConfig, token);
            case BoomplayPlatform:
                return await _boomplayMatcher.MatchAsync(
                    info,
                    context.MatchingConfig,
                    LoadConfig(context.Config.Custom, BoomplayPlatform, new BoomplayConfig()),
                    token);
            case AudiomackPlatform:
                return await _audiomackMatcher.MatchAsync(
                    info,
                    context.MatchingConfig,
                    LoadConfig(context.Config.Custom, AudiomackPlatform, new AudiomackMatchConfig()),
                    token);
            case "lastfm":
                return await _lastFmMatcher.MatchAsync(info, LoadConfig(context.Config.Custom, "lastfm", new LastFmConfig()), token);
            case ShazamPlatform:
                return await MatchShazamAsync(context.FilePath, info, context.Config, context.Settings, context.MatchingConfig, context.ShazamCache, token);
            default:
                return null;
        }
    }

    private static DeezerConfig ResolveDeezerMatchConfig(AutoTagRunnerConfig config)
        => LoadConfig(config.Custom, DeezerPlatform, new DeezerConfig());

    private static string? FirstNonEmpty(params string?[] values)
        => values
            .Select(value => value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool HasUsableMatchIdentity(AutoTagMatchResult? match)
    {
        if (match?.Track == null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(match.Track.Title)
            && match.Track.Artists.Exists(static artist => !string.IsNullOrWhiteSpace(artist));
    }

    private static string? ExtractSpotifyTrackIdFromTags(Dictionary<string, List<string>> tags)
    {
        var candidates = new[]
        {
            SpotifyTrackIdTag,
            SpotifyTrackIdLegacyTag,
            SpotifyIdLegacyTag,
            SpotifyIdUnderscoreLegacyTag,
            SpotifyUrlTag,
            "SHAZAM_SPOTIFY_URL",
            "SPOTIFY_URI",
            "SPOTIFYURI",
            "URL",
            WwwAudioFileTag
        };

        foreach (var key in candidates)
        {
            if (!tags.TryGetValue(key, out var values) || values == null || values.Count == 0)
            {
                continue;
            }

            foreach (var raw in values.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (SpotifyMetadataService.TryParseSpotifyUrl(raw.Trim(), out var type, out var parsedId)
                    && type.Equals("track", StringComparison.OrdinalIgnoreCase)
                    && IsSpotifyTrackId(parsedId))
                {
                    return parsedId;
                }

                var trimmed = raw.Trim();
                if (IsSpotifyTrackId(trimmed))
                {
                    return trimmed;
                }
            }
        }

        return null;
    }

    private static bool IsSpotifyTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 22)
        {
            return false;
        }

        return value.All(char.IsLetterOrDigit);
    }

    private static bool CanUseMatchCache(AutoTagAudioInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Isrc))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(info.Title))
        {
            return false;
        }

        var hasArtist = !string.IsNullOrWhiteSpace(info.Artist)
            || info.Artists.Any(artist => !string.IsNullOrWhiteSpace(artist));
        if (!hasArtist)
        {
            return false;
        }

        return info.DurationSeconds.HasValue && info.DurationSeconds.Value > 0;
    }

    private static string BuildMatchCacheKey(
        string platform,
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        AutoTagMatchingConfig matchingConfig)
    {
        var platformKey = NormalizeCacheToken(platform);
        JsonNode? customNode = null;
        if (config.Custom != null)
        {
            config.Custom.TryGetPropertyValue(platformKey, out customNode);
        }

        var normalizedTags = config.Tags
            .Select(NormalizeCacheToken)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();
        var normalizedArtists = info.Artists
            .Select(NormalizeCacheToken)
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .ToList();

        var builder = new StringBuilder();
        builder.Append("platform=").Append(platformKey).Append(';');
        builder.Append("title=").Append(NormalizeCacheToken(info.Title)).Append(';');
        builder.Append("artist=").Append(NormalizeCacheToken(info.Artist)).Append(';');
        builder.Append("artists=").Append(string.Join(',', normalizedArtists)).Append(';');
        builder.Append("album=").Append(NormalizeCacheToken(info.Album)).Append(';');
        builder.Append("isrc=").Append(NormalizeCacheToken(info.Isrc)).Append(';');
        builder.Append("duration=").Append(info.DurationSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(';');
        builder.Append("track=").Append(info.TrackNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(';');
        builder.Append("matchDuration=").Append(matchingConfig.MatchDuration).Append(';');
        builder.Append("maxDiff=").Append(matchingConfig.MaxDurationDifferenceSeconds.ToString(CultureInfo.InvariantCulture)).Append(';');
        builder.Append("strictness=").Append(matchingConfig.Strictness.ToString("0.###", CultureInfo.InvariantCulture)).Append(';');
        builder.Append("multiple=").Append(matchingConfig.MultipleMatches).Append(';');
        builder.Append("preferredRelease=").Append(NormalizeCacheToken(matchingConfig.PreferredReleaseType)).Append(';');
        builder.Append("matchById=").Append(config.MatchById).Append(';');
        builder.Append("enableLyrics=").Append(ShouldRequestAnyLyrics(config, settings)).Append(';');
        builder.Append("lyricsSyncedToggle=").Append(settings.SyncedLyrics).Append(';');
        builder.Append("lyricsUnsyncedToggle=").Append(settings.SaveLyrics).Append(';');
        builder.Append("lyricsType=").Append(NormalizeCacheToken(settings.LrcType)).Append(';');
        builder.Append("lyricsFormat=").Append(NormalizeCacheToken(settings.LrcFormat)).Append(';');
        builder.Append("lyricsSynthesizeLrcFromTtml=").Append(settings.SynthesizeLrcFromTtml).Append(';');
        builder.Append("lyricsSynthesizeTtmlFromLrc=").Append(settings.SynthesizeTtmlFromLrc).Append(';');
        builder.Append("lyricsPreferEnhancedLrc=").Append(settings.PreferEnhancedLrc).Append(';');
        builder.Append("beatportReleaseMeta=").Append(normalizedTags.Any(tag => tag is "albumartist" or "tracktotal")).Append(';');
        builder.Append("traxsourceExtend=").Append(normalizedTags.Any(tag => tag is "albumart" or AlbumTag or "catalognumber" or "releaseid" or "albumartist" or "tracknumber" or "tracktotal")).Append(';');
        builder.Append("traxsourceAlbumMeta=").Append(normalizedTags.Any(tag => tag is "catalognumber" or "tracknumber" or "albumart" or "tracktotal" or "albumartist")).Append(';');
        builder.Append("discogsLabelCatalog=").Append(normalizedTags.Any(tag => tag is LabelTag or "catalognumber")).Append(';');
        builder.Append("custom=").Append(customNode?.ToJsonString() ?? string.Empty).Append(';');

        var fingerprint = ComputeCacheHash(builder.ToString());
        return $"{platformKey}:{fingerprint}";
    }

    private static string ComputeCacheHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static bool IsTrustedSourceIdentity(AutoTagAudioInfo info, string filePath, AutoTagRunnerConfig config)
    {
        if (config.EnhancementUntrustedTargets)
        {
            return false;
        }

        if (!info.HasEmbeddedTitle || !info.HasEmbeddedArtist)
        {
            return false;
        }

        return !TrackIdentityTrust.IsUntrustedIdentity(info.Title, info.Artist, filePath);
    }

    private static List<string> NormalizeSpotifyTrackUrls(IEnumerable<string> values)
    {
        return values
            .Select(NormalizeSpotifyTrackUrl)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? NormalizeSpotifyTrackUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        string? trackId = null;
        if (SpotifyMetadataService.TryParseSpotifyUrl(trimmed, out var type, out var parsedId)
            && type.Equals("track", StringComparison.OrdinalIgnoreCase))
        {
            trackId = parsedId;
        }
        else if (IsSpotifyTrackId(trimmed))
        {
            trackId = trimmed;
        }

        return IsSpotifyTrackId(trackId)
            ? $"https://open.spotify.com/track/{trackId}"
            : null;
    }

    private static int? NormalizeDurationSeconds(string filePath, string extension, int? durationSeconds)
    {
        if (!durationSeconds.HasValue || durationSeconds.Value <= 0)
        {
            return null;
        }

        if (!IsMp4Family(extension))
        {
            return durationSeconds;
        }

        if (durationSeconds.Value >= 20)
        {
            return durationSeconds;
        }

        try
        {
            var lengthBytes = new FileInfo(filePath).Length;
            if (lengthBytes >= 8L * 1024L * 1024L)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // best effort
        }

        return durationSeconds;
    }

    private static int? ResolveDurationSecondsFromTags(Dictionary<string, List<string>> tags)
    {
        var raw = ReadFirstTagValue(tags, LengthUpperTag, "TLEN", "SHAZAM_DURATION_MS", "SHAZAM_META_DURATION", "SHAZAM_META_TIME", "SHAZAM_META_LENGTH");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim();
        if (normalized.Contains(':', StringComparison.Ordinal) && TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var parsedTime))
        {
            return parsedTime.TotalSeconds > 0
                ? (int)Math.Round(parsedTime.TotalSeconds)
                : null;
        }

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return null;
        }

        var seconds = value >= 10000d ? value / 1000d : value;
        return seconds > 0 ? (int)Math.Round(seconds) : null;
    }

    private static int? ResolveDurationSecondsWithFfprobe(string filePath, string extension)
    {
        if (!IsMp4Family(extension))
        {
            return null;
        }

        try
        {
            var ffprobePath = ExternalToolResolver.ResolveFfprobePath();
            if (string.IsNullOrWhiteSpace(ffprobePath))
            {
                return null;
            }

            var startInfo = ExternalToolProcessStartInfo.CreateRedirected(ffprobePath);
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("format=duration");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            startInfo.ArgumentList.Add(filePath);

            using var process = Process.Start(startInfo);

            if (process == null)
            {
                return null;
            }

            if (!process.WaitForExit(3000))
            {
                TryKillProcess(process);
                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? (int)Math.Round(seconds)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static void WriteSourceIdentityTags(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        WriteSingleRawTag(tagWriteContext, context, RecordingIdTag, SupportedTag.RecordingId, RecordingIdRawTag, context.SourceTrack.RecordingId);
        WriteSingleRawTag(tagWriteContext, context, ArtistIdTag, SupportedTag.ArtistId, ArtistIdRawTag, context.SourceTrack.ArtistId);
        // The generic ALBUMARTISTID/ALBUMID tags are one shared namespace: only
        // GUID-shaped (MusicBrainz) values may be written there. Platform-local ids
        // (Deezer numeric ids, Spotify ids, …) keep their own <PLATFORM>_… tags, so
        // files of one album cannot end up with mixed id namespaces.
        WriteSingleRawTag(tagWriteContext, context, AlbumArtistIdTag, SupportedTag.AlbumArtistId, AlbumArtistIdRawTag, ToMusicBrainzShapedId(context.SourceTrack.AlbumArtistId));
        WriteSingleRawTag(tagWriteContext, context, ReleaseGroupIdTag, SupportedTag.ReleaseGroupId, ReleaseGroupIdRawTag, ToMusicBrainzShapedId(context.SourceTrack.ReleaseGroupId));
        WriteSingleRawTag(tagWriteContext, context, AlbumIdTag, SupportedTag.AlbumId, AlbumIdRawTag, ToMusicBrainzShapedId(context.SourceTrack.AlbumId));
        WriteSingleRawTag(tagWriteContext, context, ReleaseStatusTag, SupportedTag.ReleaseStatus, ReleaseStatusRawTag, context.SourceTrack.ReleaseStatus);
        WriteSingleRawTag(tagWriteContext, context, ReleaseCountryTag, SupportedTag.ReleaseCountry, ReleaseCountryRawTag, context.SourceTrack.ReleaseCountry);
        WriteSingleRawTag(tagWriteContext, context, BarcodeTag, SupportedTag.Barcode, BarcodeRawTag, context.SourceTrack.Barcode);
        if (context.EnabledTags.Contains(MediaTag) && context.SourceTrack.Media.Count > 0)
        {
            SetRaw(tagWriteContext, MediaRawTag, SupportedTag.Media, context.SourceTrack.Media);
        }
    }

    private static string? ToMusicBrainzShapedId(string? value)
        => Guid.TryParse(value, out _) ? value : null;

    private static void WriteDurationTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DurationTag) || !context.SourceTrack.Duration.HasValue)
        {
            return;
        }

        var totalMilliseconds = ((int)Math.Round(context.SourceTrack.Duration.Value.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);
        SetField(
            tagWriteContext,
            new TagFieldBinding("TLEN", LengthUpperTag, LengthUpperTag, SupportedTag.Duration),
            new List<string> { totalMilliseconds });
    }

    private static void WriteIsrcTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("isrc") || string.IsNullOrWhiteSpace(context.SourceTrack.Isrc))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TSRC", "ISRC", "ISRC", SupportedTag.ISRC), new List<string> { context.SourceTrack.Isrc });
    }

    private static bool IsRuntimeMatchMetadataKey(string key)
    {
        return key.StartsWith("SHAZAM_MATCH_", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_SIMILARITY", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SHAZAM_DURATION_DIFF_SECONDS", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SHAZAM_TITLE_SIMILARITY", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SHAZAM_ARTIST_SIMILARITY", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToCamelot(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        foreach (var (original, camelot) in CamelotNotes)
        {
            if (string.Equals(original, key, StringComparison.OrdinalIgnoreCase))
            {
                return camelot;
            }
        }
        return key;
    }

    private static List<string> ReadRawTagValuesCore(
        TagLib.File file,
        string extension,
        string name,
        Func<TagLib.Mpeg4.AppleTag, string, List<string>> readAppleValues)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
            if (id3 == null) return new List<string>();
            if (name.Length == 4)
            {
                var frame = TagLib.Id3v2.TextInformationFrame.Get(id3, name, false);
                return frame?.Text?.ToList() ?? new List<string>();
            }

            var user = TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, false);
            return user?.Text?.ToList() ?? new List<string>();
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
            return vorbis?.GetField(name).ToList() ?? new List<string>();
        }

        if (IsMp4Family(extension))
        {
            var apple = (TagLib.Mpeg4.AppleTag?)file.GetTag(TagTypes.Apple, false);
            var normalizedName = Mp4RawTagNameNormalizer.Normalize(name);
            if (apple != null)
            {
                var dashValues = readAppleValues(apple, normalizedName);
                if (dashValues.Count > 0)
                {
                    return dashValues;
                }
            }

            return ReadMp4AtlRawValues(file.Name, normalizedName);
        }

        return new List<string>();
    }
}
