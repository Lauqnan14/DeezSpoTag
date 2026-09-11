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

    private static bool IsLastPlatform(AutoTagFileRunContext context)
        => context.PlatformIndex == context.Plan.PlatformCount - 1;

    private static bool WasTaggedByAnyPlatform(AutoTagFileRunContext context)
        => context.Plan.TaggedFileIndices.Contains(context.FileIndex);

    private static bool TryHandlePreSkippedFile(AutoTagFileRunContext context)
    {
        if (!context.Plan.PreSkippedFiles.Contains(context.File))
        {
            return false;
        }

        if (context.PlatformIndex == 0)
        {
            EmitSkippedStatus(context, "already tagged");
        }

        return true;
    }

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

    private static bool IsProviderNotConfigured(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is not InvalidOperationException)
            {
                continue;
            }

            var message = current.Message;
            if (message.Contains("not configured", StringComparison.OrdinalIgnoreCase)
                || message.Contains("must be connected", StringComparison.OrdinalIgnoreCase)
                || message.Contains("credentials are required", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private async Task EnsureManualArtistArtworkAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo identity,
        AutoTagTrack track)
    {
        if (!context.Plan.Settings.SaveArtworkArtist)
        {
            return;
        }

        var coreTrack = BuildCoreTrack(
            track,
            ResolveArtistSeparator(context.Plan.Config, context.File),
            context.Plan.TagSettings.SingleAlbumArtist,
            context.Plan.Settings);
        var artistPath = BuildTemplatePathInfo(coreTrack, context.Plan.Settings).ArtistPath;
        if (string.IsNullOrWhiteSpace(artistPath))
        {
            return;
        }

        var artworkKey = Path.GetFullPath(artistPath);
        if (!context.Plan.AttemptedArtistArtworkPaths.Add(artworkKey))
        {
            return;
        }

        using var scope = _serviceScopeFactory.CreateScope();
        var provider = scope.ServiceProvider;
        var artist = track.Artists.FirstOrDefault();
        var artistArtwork = await DownloadEngineArtworkHelper.ResolveArtistArtworkAsync(
            new DownloadEngineArtworkHelper.ArtistImageResolveRequest(
                _appleMusicCatalogService,
                _httpClientFactory,
                context.Plan.Settings,
                provider.GetService<DeezSpoTag.Integrations.Deezer.DeezerClient>(),
                provider.GetService<DeezSpoTag.Services.Download.ISpotifyArtworkResolver>(),
                provider.GetService<DeezSpoTag.Services.Download.ILastFmArtistImageResolver>(),
                AutoTagIdentityTags.ReadAppleTrackId(identity),
                AutoTagTagValueReader.ReadFirstTagValue(identity, DeezerTrackIdTag),
                AutoTagTagValueReader.ReadFirstTagValue(identity, SpotifyTrackIdTag),
                artist,
                _logger)
            {
                AppleArtistId = AutoTagIdentityTags.ReadAppleArtistId(identity)
                    ?? (context.Platform is "itunes" or "apple" or "applemusic" ? track.ArtistId : null),
                DeezerArtistId = string.Equals(context.Platform, "deezer", StringComparison.OrdinalIgnoreCase)
                    ? track.ArtistId
                    : null,
                SpotifyArtistId = string.Equals(context.Platform, "spotify", StringComparison.OrdinalIgnoreCase)
                    ? track.ArtistId
                    : null
            },
            context.Token);
        if (artistArtwork == null)
        {
            return;
        }

        _logger.LogInformation(
            "Artist artwork resolved from {Provider} using {ResolutionMethod} for {Artist}",
            artistArtwork.Provider,
            artistArtwork.ResolutionMethod,
            artist);

        _ = await DownloadEngineArtworkHelper.SaveArtistArtworkAsync(
            new DownloadEngineArtworkHelper.SaveArtistArtworkRequest(
                provider.GetRequiredService<ImageDownloader>(),
                provider.GetRequiredService<EnhancedPathTemplateProcessor>(),
                artistPath,
                artistArtwork.Url,
                context.Plan.Settings,
                coreTrack,
                AppleQueueHelpers.GetAppleArtworkSize(context.Plan.Settings),
                context.Plan.Settings.EmbedMaxQualityCover,
                _logger),
            context.Token);
    }

    private static void HandleRejectedManualRelease(
        AutoTagFileRunContext context,
        AutoTagAudioInfo source,
        AutoTagTrack candidate,
        string reason,
        bool usedShazam,
        ProviderTagPlan tagPlan,
        AutoTagMatchResult match)
    {
        if (IsLastPlatform(context) && !WasTaggedByAnyPlatform(context))
        {
            EmitReviewStatus(
                context,
                reason,
                usedShazam,
                AutoTagReviewMetadata.FromMatch(source, candidate),
                "rejected",
                tagPlan,
                match);
            context.Plan.ReviewedFiles.Add(context.File);
            return;
        }

        EmitSkippedStatus(context, reason, usedShazam, "rejected", tagPlan, match);
    }

    private static bool AlbumsReferToSameRelease(string? frozen, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(frozen) || string.IsNullOrWhiteSpace(candidate))
        {
            return true;
        }

        return string.Equals(
            AutoTagSimilarity.NormalizeText(frozen),
            AutoTagSimilarity.NormalizeText(candidate),
            StringComparison.Ordinal);
    }

    private async Task RunBoundedOptionalStepAsync(
        AutoTagFileRunContext context,
        string stepName,
        TimeSpan timeout,
        Func<CancellationToken, Task> action)
    {
        context.LogCallback($"onetagger_autotag: {context.Platform} {stepName} starting");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
        var stepTask = action(timeoutSource.Token);
        try
        {
            await stepTask.WaitAsync(timeout, context.Token);
            context.LogCallback($"onetagger_autotag: {context.Platform} {stepName} completed");
        }
        catch (TimeoutException)
        {
            timeoutSource.Cancel();
            ObserveBackgroundTask(stepTask);
            context.LogCallback(
                $"onetagger_autotag: {context.Platform} {stepName} timed out after {timeout.TotalSeconds:0}s; continuing");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Optional AutoTag step {Step} failed for {File} on {Platform}; continuing with provider metadata.",
                SanitizeLogValue(stepName),
                SanitizeLogValue(context.File),
                SanitizeLogValue(context.Platform));
            context.LogCallback(
                $"onetagger_autotag: {context.Platform} optional {stepName} failed; continuing with provider metadata");
        }
    }

    private static void ObserveBackgroundTask(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

    private static bool AreArtistIdentitiesCompatibleForOverwrite(
        IReadOnlyList<string> sourceArtists,
        IReadOnlyList<string> incomingArtists,
        double strictness)
    {
        var sourceCredits = SplitArtistCredits(sourceArtists);
        var incomingCredits = SplitArtistCredits(incomingArtists);
        if (HasDottedInitialArtistCollapse(sourceCredits, incomingCredits))
        {
            return false;
        }

        var normalizedSource = sourceCredits
            .Select(NormalizeArtistIdentity)
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .ToList();
        var normalizedIncoming = incomingCredits
            .Select(NormalizeArtistIdentity)
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .ToList();

        if (normalizedSource.Count == 0 || normalizedIncoming.Count == 0)
        {
            return true;
        }

        if (normalizedSource.Any(source => normalizedIncoming.Contains(source, StringComparer.Ordinal)))
        {
            return true;
        }

        var sourceJoined = string.Join(" ", normalizedSource);
        var incomingJoined = string.Join(" ", normalizedIncoming);
        var similarity = AutoTagSimilarity.ComputeScore(sourceJoined, incomingJoined);
        return similarity >= Math.Clamp(strictness + 0.15d, 0.80d, 0.98d);
    }

    private static bool HasDottedInitialArtistCollapse(IReadOnlyList<string> sourceArtists, IReadOnlyList<string> incomingArtists)
    {
        foreach (var sourceArtist in sourceArtists)
        {
            foreach (var incomingArtist in incomingArtists)
            {
                if (!string.Equals(
                        NormalizeArtistIdentity(sourceArtist),
                        NormalizeArtistIdentity(incomingArtist),
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsDottedInitialArtist(sourceArtist) != IsDottedInitialArtist(incomingArtist))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsDottedInitialArtist(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compact = Regex.Replace(value.Trim(), @"\s+", string.Empty, RegexOptions.None, RegexTimeout);
        return Regex.IsMatch(compact, @"^\p{L}(?:\.\p{L})+\.?$", RegexOptions.None, RegexTimeout);
    }

    private static string NormalizeArtistIdentity(string value)
    {
        return AutoTagSimilarity.NormalizeText(value);
    }

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

    private static void PreserveAtmosFileIsrc(string filePath, AutoTagAudioInfo source, AutoTagTrack? incoming)
        => PreserveAtmosFileIsrc(IsLocalAtmosFile(filePath), source, incoming);

    private static void PreserveAtmosFileIsrc(bool localFileIsAtmos, AutoTagAudioInfo source, AutoTagTrack? incoming)
    {
        if (incoming == null || !localFileIsAtmos)
        {
            return;
        }

        incoming.Isrc = source.Isrc;
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

    private async Task EnsureArtworkFallbackAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo info,
        AutoTagTrack track,
        CancellationToken token)
    {
        if (!WantsArtworkFromSettings(context.Plan.Config, context.Plan.Settings)
            || !string.IsNullOrWhiteSpace(track.Art))
        {
            return;
        }

        var providerOrder = ArtworkFallbackHelper.ResolveOrder(context.Plan.Settings);
        if (providerOrder.Count == 0)
        {
            return;
        }

        foreach (var platform in providerOrder
            .Select(ResolveArtworkFallbackPlatform)
            .Where(platform => !string.IsNullOrWhiteSpace(platform)
                && !string.Equals(platform, context.Platform, StringComparison.OrdinalIgnoreCase)))
        {
            AutoTagMatchResult? fallbackMatch;
            try
            {
                fallbackMatch = await MatchPlatformAsync(
                    platform!,
                    info,
                    new PlatformMatchContext
                    {
                        FilePath = context.File,
                        Config = context.Plan.Config,
                        Settings = context.Plan.Settings,
                        MatchingConfig = context.Plan.MatchingConfig,
                        ShazamCache = context.Plan.ShazamCache,
                        IsManualEnrichment = IsManualEnrichment(context.Plan.Config)
                    },
                    token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogArtworkFallbackMatchFailure(ex, context.File, platform!);
                continue;
            }

            var fallbackArt = fallbackMatch?.Track?.Art;
            if (string.IsNullOrWhiteSpace(fallbackArt))
            {
                continue;
            }
            if (IsManualEnrichment(context.Plan.Config)
                && (!AutoTagReleaseCategory.MatchesPreference(
                        fallbackMatch!.Track.ReleaseType,
                        fallbackMatch.Track.TrackTotal,
                        context.Plan.Config.ManualReleasePreference)
                    || (context.Plan.FrozenManualReleases.TryGetValue(context.FileIndex, out var frozenRelease)
                        && !AlbumsReferToSameRelease(frozenRelease.Album, fallbackMatch.Track.Album))))
            {
                continue;
            }

            track.Art = fallbackArt;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Artwork fallback resolved for {File} via {Platform}.", SanitizeLogValue(context.File), SanitizeLogValue(platform));
            }

            return;
        }
    }
}
