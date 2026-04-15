using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Utils;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

internal enum SpotifyTracklistResolveOutcome
{
    Matched,
    HardMismatch,
    TransientFailure
}

internal sealed record SpotifyTracklistResolveResult(
    string? DeezerId,
    SpotifyTracklistResolveOutcome Outcome,
    string Reason);

internal sealed record SpotifyTrackResolveOptions(
    bool AllowFallbackSearch,
    bool PreferIsrcOnly,
    bool UseSongLink,
    bool StrictMode,
    bool BypassNegativeCanonicalCache,
    ILogger Logger,
    CancellationToken CancellationToken);

internal static class SpotifyTracklistResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PositiveCanonicalCacheTtl = TimeSpan.FromDays(14);
    private static readonly TimeSpan NegativeCanonicalCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PersistentFlushInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> IsrcCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> MetadataCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CanonicalCacheEntry> CanonicalTrackCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PersistentCacheEntry> PersistentCanonicalCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DerivativeMarkers =
    {
        "cover",
        "karaoke",
        "tribute",
        "instrumental",
        "re recorded",
        "as made famous by"
    };

    private static readonly string[] VersionMarkers =
    {
        "ver",
        "version",
        "remix",
        "mix",
        "edit",
        "live",
        "acoustic",
        "instrumental",
        "demo",
        "clean",
        "explicit",
        "radio",
        "single",
        "deluxe",
        "bonus",
        "sped",
        "slowed",
        "nightcore",
        "member",
        "remaster",
        "remastered",
        "anniversary",
        "expanded",
        "special",
        "edition",
        "stereo",
        "mono"
    };

    private static readonly object PersistentCacheSync = new();
    private static readonly string PersistentCachePath = ResolvePersistentCachePath();

    private static bool _persistentCacheLoaded;
    private static DateTimeOffset _lastPersistentFlush = DateTimeOffset.MinValue;
    private static bool _persistentCacheDirty;

    private readonly record struct ResolveContext(
        DeezerClient DeezerClient,
        SongLinkResolver SongLinkResolver,
        SpotifyTrackSummary Track,
        SpotifyTrackResolveOptions Options,
        string CanonicalKey,
        string? NormalizedIsrc,
        bool PersistCanonical,
        bool StrictWithoutIsrc);

    private readonly record struct ResolveStepResult(
        SpotifyTracklistResolveResult? Resolution,
        bool SawTransientFailure);

    private readonly record struct IsrcLookupOutcome(
        string? DeezerId,
        bool SawTransientFailure,
        bool ShouldCache);

    private readonly record struct MetadataLookupOutcome(
        string? DeezerId,
        bool SawTransientFailure);

    private const double MinAcceptedTitleSimilarity = 0.72;
    private const double MinAcceptedTitleSimilarityStrictNoIsrc = 0.80;
    private const double MinAcceptedArtistSimilarity = 0.55;
    private const double MinAcceptedArtistSimilarityStrictNoIsrc = 0.60;
    private const int MaxAcceptedDurationDiffSeconds = 12;
    private const int MaxAcceptedDurationDiffSecondsStrictNoIsrc = 8;
    private const double MinAcceptedCompositeScore = 0.68;
    private const double MinAcceptedCompositeScoreStrict = 0.74;
    private const double MinAcceptedCompositeScoreStrictNoIsrc = 0.82;
    private const double MinAcceptedAlbumSimilarityStrictNoIsrc = 0.55;
    private static string ReplaceWithTimeout(string input, string pattern, string replacement, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
        => System.Text.RegularExpressions.Regex.Replace(input, pattern, replacement, options, RegexTimeout);
    private static string ReplaceWithTimeout(string input, string pattern, System.Text.RegularExpressions.MatchEvaluator evaluator, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
        => System.Text.RegularExpressions.Regex.Replace(input, pattern, evaluator, options, RegexTimeout);
    private static string[] SplitWithTimeout(string input, string pattern, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
        => System.Text.RegularExpressions.Regex.Split(input, pattern, options, RegexTimeout);
    private static System.Text.RegularExpressions.Match MatchWithTimeout(string input, string pattern, System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None)
        => System.Text.RegularExpressions.Regex.Match(input, pattern, options, RegexTimeout);

    internal static async Task<string?> ResolveDeezerTrackIdAsync(
        DeezerClient deezerClient,
        SongLinkResolver songLinkResolver,
        SpotifyTrackSummary track,
        SpotifyTrackResolveOptions options)
    {
        var result = await ResolveDeezerTrackAsync(
            deezerClient,
            songLinkResolver,
            track,
            options);
        return result.DeezerId;
    }

    internal static async Task<SpotifyTracklistResolveResult> ResolveDeezerTrackAsync(
        DeezerClient deezerClient,
        SongLinkResolver songLinkResolver,
        SpotifyTrackSummary track,
        SpotifyTrackResolveOptions options)
    {
        EnsurePersistentCacheLoaded(options.Logger);

        var normalizedIsrc = NormalizeIsrc(track.Isrc);
        var strictWithoutIsrc = options.StrictMode && string.IsNullOrWhiteSpace(normalizedIsrc);
        var context = new ResolveContext(
            deezerClient,
            songLinkResolver,
            track,
            options,
            BuildCanonicalTrackKey(track, normalizedIsrc),
            normalizedIsrc,
            PersistCanonical: !strictWithoutIsrc,
            StrictWithoutIsrc: strictWithoutIsrc);
        var sawTransientFailure = false;

        try
        {
            var canonicalResolution = await TryResolveFromCanonicalCacheAsync(context);
            if (canonicalResolution != null)
            {
                return canonicalResolution;
            }

            var isrcStep = await TryResolveFromIsrcAsync(context);
            sawTransientFailure |= isrcStep.SawTransientFailure;
            if (isrcStep.Resolution != null)
            {
                return isrcStep.Resolution;
            }

            var preferIsrcOnlyResolution = TryResolvePreferIsrcOnly(context, sawTransientFailure);
            if (preferIsrcOnlyResolution != null)
            {
                return preferIsrcOnlyResolution;
            }

            var metadataStep = await TryResolveFromMetadataAsync(context);
            sawTransientFailure |= metadataStep.SawTransientFailure;
            if (metadataStep.Resolution != null)
            {
                return metadataStep.Resolution;
            }

            var songLinkStep = await TryResolveFromSongLinkAsync(context);
            sawTransientFailure |= songLinkStep.SawTransientFailure;
            if (songLinkStep.Resolution != null)
            {
                return songLinkStep.Resolution;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsTransientException(ex))
            {
                sawTransientFailure = true;
                if (context.Options.Logger.IsEnabled(LogLevel.Debug))
                {
                    context.Options.Logger.LogDebug(ex, "Transient resolver failure for Spotify track {TrackName}", context.Track.Name);
                }
            }
            else
            {
                context.Options.Logger.LogWarning(ex, "Failed to resolve Deezer track for Spotify track {TrackName}", context.Track.Name);
            }
        }
        finally
        {
            TryFlushPersistentCache(context.Options.Logger);
        }

        if (sawTransientFailure)
        {
            return new SpotifyTracklistResolveResult(
                null,
                SpotifyTracklistResolveOutcome.TransientFailure,
                "transient_upstream_failure");
        }

        CacheCanonicalMiss(context.CanonicalKey);
        return new SpotifyTracklistResolveResult(
            null,
            SpotifyTracklistResolveOutcome.HardMismatch,
            "unresolved");
    }

    private static async Task<SpotifyTracklistResolveResult?> TryResolveFromCanonicalCacheAsync(ResolveContext context)
    {
        if (context.StrictWithoutIsrc || string.IsNullOrWhiteSpace(context.CanonicalKey))
        {
            return null;
        }

        if (!TryGetCanonicalCached(context.CanonicalKey, out var cachedEntry, out var cachedNegative))
        {
            return null;
        }

        var cachedCanonicalId = cachedEntry.DeezerId;

        if (!string.IsNullOrWhiteSpace(cachedCanonicalId))
        {
            if (NeedsCachedVariantValidation(context, cachedEntry))
            {
                var isVariantMismatch = await IsDisallowedVariantCachedHitAsync(context, cachedCanonicalId);
                if (isVariantMismatch)
                {
                    InvalidateCanonicalCacheEntry(context.CanonicalKey, context.Options.Logger);
                    return null;
                }

                PromoteValidatedCanonicalCacheEntry(context.CanonicalKey, cachedEntry, cachedCanonicalId, context.Options.Logger);
            }

            return new SpotifyTracklistResolveResult(
                cachedCanonicalId,
                SpotifyTracklistResolveOutcome.Matched,
                "canonical_cache_hit");
        }

        if (!cachedNegative)
        {
            return null;
        }

        if (context.Options.BypassNegativeCanonicalCache)
        {
            CanonicalTrackCache.TryRemove(context.CanonicalKey, out _);
            return null;
        }

        return new SpotifyTracklistResolveResult(
            null,
            SpotifyTracklistResolveOutcome.HardMismatch,
            "canonical_negative_cache_hit");
    }

    private static bool NeedsCachedVariantValidation(ResolveContext context, CanonicalCacheEntry cachedEntry)
    {
        if (!string.IsNullOrWhiteSpace(context.NormalizedIsrc))
        {
            return false;
        }

        if (SourceAllowsVariant(context.Track))
        {
            return false;
        }

        var strategy = cachedEntry.Strategy?.Trim();
        return string.Equals(strategy, "metadata", StringComparison.OrdinalIgnoreCase)
               || string.Equals(strategy, "metadata-cache", StringComparison.OrdinalIgnoreCase)
               || string.Equals(strategy, "songlink", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsDisallowedVariantCachedHitAsync(ResolveContext context, string deezerId)
    {
        try
        {
            var candidate = await context.DeezerClient.GetTrackAsync(deezerId);
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Id) || candidate.Id == "0")
            {
                return true;
            }

            return IsVariantCandidate(candidate);
        }
        catch (Exception ex) when (IsTransientException(ex))
        {
            if (context.Options.Logger.IsEnabled(LogLevel.Debug))
            {
                context.Options.Logger.LogDebug(
                    ex,
                    "Transient Deezer cache-validation failure for {TrackName}",
                    context.Track.Name);
            }
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (context.Options.Logger.IsEnabled(LogLevel.Debug))
            {
                context.Options.Logger.LogDebug(
                    ex,
                    "Deezer cache-validation skipped for {TrackName}",
                    context.Track.Name);
            }
            return false;
        }
    }

    private static async Task<ResolveStepResult> TryResolveFromIsrcAsync(ResolveContext context)
    {
        if (string.IsNullOrWhiteSpace(context.NormalizedIsrc))
        {
            return new ResolveStepResult(null, false);
        }

        var sawTransientFailure = false;
        var normalizedIsrc = context.NormalizedIsrc!;
        if (!TryGetCached(IsrcCache, normalizedIsrc, out var cachedIsrcId))
        {
            var lookupOutcome = await LookupAndValidateIsrcCandidateAsync(context, normalizedIsrc);
            sawTransientFailure |= lookupOutcome.SawTransientFailure;
            cachedIsrcId = lookupOutcome.DeezerId;

            if (lookupOutcome.ShouldCache)
            {
                CacheValue(IsrcCache, normalizedIsrc, cachedIsrcId);
            }
        }

        if (!string.IsNullOrWhiteSpace(cachedIsrcId))
        {
            CacheCanonicalMatch(context.CanonicalKey, cachedIsrcId, "isrc-cache", 1d, persist: context.PersistCanonical, context.Options.Logger);
            return new ResolveStepResult(
                new SpotifyTracklistResolveResult(
                    cachedIsrcId,
                    SpotifyTracklistResolveOutcome.Matched,
                    "isrc_cache_hit"),
                sawTransientFailure);
        }

        if (!context.Options.AllowFallbackSearch)
        {
            if (sawTransientFailure)
            {
                return new ResolveStepResult(
                    new SpotifyTracklistResolveResult(
                        null,
                        SpotifyTracklistResolveOutcome.TransientFailure,
                        "isrc_lookup_transient_failure"),
                    true);
            }

            CacheCanonicalMiss(context.CanonicalKey);
            return new ResolveStepResult(
                new SpotifyTracklistResolveResult(
                    null,
                    SpotifyTracklistResolveOutcome.HardMismatch,
                    "isrc_only_no_match"),
                false);
        }

        return new ResolveStepResult(null, sawTransientFailure);
    }

    private static async Task<IsrcLookupOutcome> LookupAndValidateIsrcCandidateAsync(ResolveContext context, string normalizedIsrc)
    {
        var lookupTransient = false;
        var sawTransientFailure = false;
        ApiTrack? deezerTrack = null;
        try
        {
            deezerTrack = await context.DeezerClient.GetTrackByIsrcAsync(normalizedIsrc);
        }
        catch (Exception ex) when (IsTransientException(ex))
        {
            lookupTransient = true;
            sawTransientFailure = true;
            if (context.Options.Logger.IsEnabled(LogLevel.Debug))
            {
                context.Options.Logger.LogDebug(
                    ex,
                    "Transient Deezer ISRC lookup failure for {TrackName}",
                    context.Track.Name);
            }
        }

        var deezerId = NormalizeDeezerId(deezerTrack?.Id?.ToString());
        if (!string.IsNullOrWhiteSpace(deezerId))
        {
            var validation = ValidateCandidate(context.Track, deezerTrack!, context.Options.StrictMode);
            if (!validation.IsAccepted)
            {
                if (context.Options.Logger.IsEnabled(LogLevel.Debug))
                {
                    context.Options.Logger.LogDebug(
                        "Rejected ISRC Deezer candidate {DeezerId} for {TrackName}: {Reason} (score={Score:F3})",
                        deezerId,
                        context.Track.Name,
                        validation.Reason,
                        validation.Score);
                }
                deezerId = null;
                sawTransientFailure |= validation.IsTransient;
            }
            else
            {
                CacheCanonicalMatch(context.CanonicalKey, deezerId, "isrc", validation.Score, persist: context.PersistCanonical, context.Options.Logger);
            }
        }

        return new IsrcLookupOutcome(
            deezerId,
            sawTransientFailure,
            !lookupTransient || !string.IsNullOrWhiteSpace(deezerId));
    }

    private static SpotifyTracklistResolveResult? TryResolvePreferIsrcOnly(ResolveContext context, bool sawTransientFailure)
    {
        if (!context.Options.PreferIsrcOnly)
        {
            return null;
        }

        if (sawTransientFailure)
        {
            return new SpotifyTracklistResolveResult(
                null,
                SpotifyTracklistResolveOutcome.TransientFailure,
                "prefer_isrc_only_transient_failure");
        }

        CacheCanonicalMiss(context.CanonicalKey);
        return new SpotifyTracklistResolveResult(
            null,
            SpotifyTracklistResolveOutcome.HardMismatch,
            "prefer_isrc_only_no_match");
    }

    private static async Task<ResolveStepResult> TryResolveFromMetadataAsync(ResolveContext context)
    {
        if (!context.Options.AllowFallbackSearch)
        {
            return new ResolveStepResult(null, false);
        }

        var cacheKey = BuildMetadataKey(context.Track);
        var sawTransientFailure = false;

        var cacheStep = await TryResolveFromMetadataCacheAsync(context, cacheKey);
        sawTransientFailure |= cacheStep.SawTransientFailure;
        if (cacheStep.Resolution != null)
        {
            return new ResolveStepResult(cacheStep.Resolution, sawTransientFailure);
        }

        var lookupStep = await TryResolveFromMetadataLookupAsync(context, cacheKey);
        sawTransientFailure |= lookupStep.SawTransientFailure;
        if (lookupStep.Resolution != null)
        {
            return new ResolveStepResult(lookupStep.Resolution, sawTransientFailure);
        }

        return new ResolveStepResult(null, sawTransientFailure);
    }

    private static async Task<ResolveStepResult> TryResolveFromMetadataCacheAsync(ResolveContext context, string? cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey)
            || !TryGetCached(MetadataCache, cacheKey, out var cachedMetadataId)
            || string.IsNullOrWhiteSpace(cachedMetadataId))
        {
            return new ResolveStepResult(null, false);
        }

        var validation = await ValidateCandidateAsync(
            context.DeezerClient,
            context.Track,
            cachedMetadataId,
            context.Options.Logger,
            context.Options.StrictMode);
        if (validation.IsAccepted)
        {
            CacheCanonicalMatch(context.CanonicalKey, cachedMetadataId, "metadata-cache", validation.Score, persist: context.PersistCanonical, context.Options.Logger);
            return new ResolveStepResult(
                new SpotifyTracklistResolveResult(
                    cachedMetadataId,
                    SpotifyTracklistResolveOutcome.Matched,
                    "metadata_cache_hit"),
                false);
        }

        if (!validation.IsTransient)
        {
            CacheValue(MetadataCache, cacheKey, null);
        }

        return new ResolveStepResult(null, validation.IsTransient);
    }

    private static async Task<ResolveStepResult> TryResolveFromMetadataLookupAsync(ResolveContext context, string? cacheKey)
    {
        if (string.IsNullOrWhiteSpace(context.Track.Name))
        {
            return new ResolveStepResult(null, false);
        }

        var lookupOutcome = await LookupMetadataCandidateIdAsync(context);
        if (!string.IsNullOrWhiteSpace(cacheKey)
            && (!lookupOutcome.SawTransientFailure || !string.IsNullOrWhiteSpace(lookupOutcome.DeezerId)))
        {
            CacheValue(MetadataCache, cacheKey, lookupOutcome.DeezerId);
        }

        if (string.IsNullOrWhiteSpace(lookupOutcome.DeezerId))
        {
            return new ResolveStepResult(null, lookupOutcome.SawTransientFailure);
        }

        var validation = await ValidateCandidateAsync(
            context.DeezerClient,
            context.Track,
            lookupOutcome.DeezerId,
            context.Options.Logger,
            context.Options.StrictMode);
        if (validation.IsAccepted)
        {
            CacheCanonicalMatch(context.CanonicalKey, lookupOutcome.DeezerId, "metadata", validation.Score, persist: context.PersistCanonical, context.Options.Logger);
            return new ResolveStepResult(
                new SpotifyTracklistResolveResult(
                    lookupOutcome.DeezerId,
                    SpotifyTracklistResolveOutcome.Matched,
                    "metadata_match"),
                lookupOutcome.SawTransientFailure);
        }

        if (context.Options.Logger.IsEnabled(LogLevel.Debug))
        {
            context.Options.Logger.LogDebug(
                "Rejected metadata Deezer candidate {DeezerId} for {TrackName}: {Reason} (score={Score:F3})",
                lookupOutcome.DeezerId,
                context.Track.Name,
                validation.Reason,
                validation.Score);
        }

        if (!string.IsNullOrWhiteSpace(cacheKey) && !validation.IsTransient)
        {
            CacheValue(MetadataCache, cacheKey, null);
        }

        return new ResolveStepResult(null, lookupOutcome.SawTransientFailure || validation.IsTransient);
    }

    private static async Task<MetadataLookupOutcome> LookupMetadataCandidateIdAsync(ResolveContext context)
    {
        var artist = context.Track.Artists ?? string.Empty;
        var album = context.Track.Album ?? string.Empty;
        var sawTransientFailure = false;
        string? resolvedMetadataId = null;

        if (!string.IsNullOrWhiteSpace(artist))
        {
            try
            {
                var resolvedId = await context.DeezerClient.GetTrackIdFromMetadataAsync(
                    artist,
                    context.Track.Name,
                    album,
                    context.Track.DurationMs);
                resolvedMetadataId = NormalizeDeezerId(resolvedId);
            }
            catch (Exception ex) when (IsTransientException(ex))
            {
                sawTransientFailure = true;
                if (context.Options.Logger.IsEnabled(LogLevel.Debug))
                {
                    context.Options.Logger.LogDebug(
                        ex,
                        "Transient Deezer metadata lookup failure for {TrackName}",
                        context.Track.Name);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedMetadataId))
        {
            try
            {
                var fastId = await context.DeezerClient.GetTrackIdFromMetadataFastAsync(
                    artist,
                    context.Track.Name,
                    context.Track.DurationMs);
                resolvedMetadataId = NormalizeDeezerId(fastId);
            }
            catch (Exception ex) when (IsTransientException(ex))
            {
                sawTransientFailure = true;
                if (context.Options.Logger.IsEnabled(LogLevel.Debug))
                {
                    context.Options.Logger.LogDebug(
                        ex,
                        "Transient Deezer fast metadata lookup failure for {TrackName}",
                        context.Track.Name);
                }
            }
        }

        return new MetadataLookupOutcome(resolvedMetadataId, sawTransientFailure);
    }

    private static async Task<ResolveStepResult> TryResolveFromSongLinkAsync(ResolveContext context)
    {
        if (!context.Options.UseSongLink || string.IsNullOrWhiteSpace(context.Track.Id))
        {
            return new ResolveStepResult(null, false);
        }

        var sawTransientFailure = false;
        try
        {
            var songLinkLookup = await ResolveSongLinkDeezerIdAsync(context);
            sawTransientFailure |= songLinkLookup.SawTransientFailure;
            var normalized = NormalizeDeezerId(songLinkLookup.DeezerId);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return new ResolveStepResult(null, sawTransientFailure);
            }

            var validation = await ValidateCandidateAsync(
                context.DeezerClient,
                context.Track,
                normalized,
                context.Options.Logger,
                context.Options.StrictMode);
            if (validation.IsAccepted)
            {
                CacheCanonicalMatch(context.CanonicalKey, normalized, "songlink", validation.Score, persist: context.PersistCanonical, context.Options.Logger);
                return new ResolveStepResult(
                    new SpotifyTracklistResolveResult(
                        normalized,
                        SpotifyTracklistResolveOutcome.Matched,
                        "songlink_match"),
                    sawTransientFailure);
            }

            sawTransientFailure |= validation.IsTransient;
            if (context.Options.Logger.IsEnabled(LogLevel.Debug))
            {
                context.Options.Logger.LogDebug(
                    "Rejected Song.link Deezer candidate {DeezerId} for {TrackName}: {Reason} (score={Score:F3})",
                    normalized,
                    context.Track.Name,
                    validation.Reason,
                    validation.Score);
            }
        }
        catch (Exception ex) when (IsTransientException(ex))
        {
            sawTransientFailure = true;
            if (context.Options.Logger.IsEnabled(LogLevel.Debug))
            {
                context.Options.Logger.LogDebug(
                    ex,
                    "Transient Song.link resolution failure for {TrackName}",
                    context.Track.Name);
            }
        }

        return new ResolveStepResult(null, sawTransientFailure);
    }

    private static async Task<MetadataLookupOutcome> ResolveSongLinkDeezerIdAsync(ResolveContext context)
    {
        var songLink = await context.SongLinkResolver.ResolveSpotifyTrackAsync(context.Track.Id, context.Options.CancellationToken);
        var deezerId = songLink?.DeezerId
            ?? TryExtractDeezerId(songLink?.DeezerUrl);
        if (!string.IsNullOrWhiteSpace(deezerId) || string.IsNullOrWhiteSpace(songLink?.Isrc))
        {
            return new MetadataLookupOutcome(deezerId, false);
        }

        var songLinkIsrc = NormalizeIsrc(songLink.Isrc);
        if (string.IsNullOrWhiteSpace(songLinkIsrc))
        {
            return new MetadataLookupOutcome(null, false);
        }

        return await LookupSongLinkIsrcCandidateAsync(context, songLinkIsrc);
    }

    private static async Task<MetadataLookupOutcome> LookupSongLinkIsrcCandidateAsync(ResolveContext context, string songLinkIsrc)
    {
        var lookupTransient = false;
        ApiTrack? deezerTrack = null;
        try
        {
            deezerTrack = await context.DeezerClient.GetTrackByIsrcAsync(songLinkIsrc);
        }
        catch (Exception ex) when (IsTransientException(ex))
        {
            lookupTransient = true;
            if (context.Options.Logger.IsEnabled(LogLevel.Debug))
            {
                context.Options.Logger.LogDebug(
                    ex,
                    "Transient Deezer Song.link ISRC lookup failure for {TrackName}",
                    context.Track.Name);
            }
        }

        var deezerId = NormalizeDeezerId(deezerTrack?.Id?.ToString());
        if (!lookupTransient || !string.IsNullOrWhiteSpace(deezerId))
        {
            CacheValue(IsrcCache, songLinkIsrc, deezerId);
        }

        return new MetadataLookupOutcome(deezerId, lookupTransient);
    }

    private static async Task<CandidateValidationResult> ValidateCandidateAsync(
        DeezerClient deezerClient,
        SpotifyTrackSummary sourceTrack,
        string deezerId,
        ILogger logger,
        bool strictMode)
    {
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return CandidateValidationResult.Reject("empty_id");
        }

        try
        {
            var candidate = await deezerClient.GetTrackAsync(deezerId);
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Id) || candidate.Id == "0")
            {
                return CandidateValidationResult.Reject("missing_candidate");
            }

            return ValidateCandidate(sourceTrack, candidate, strictMode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Deezer candidate validation failed for Deezer track {DeezerId}", deezerId);
            }
            return CandidateValidationResult.Reject("validation_error", isTransient: true);
        }
    }

    private static CandidateValidationResult ValidateCandidate(
        SpotifyTrackSummary sourceTrack,
        ApiTrack candidate,
        bool strictMode)
    {
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.Id) || candidate.Id == "0")
        {
            return CandidateValidationResult.Reject("missing_candidate");
        }

        var sourceIsrc = NormalizeIsrc(sourceTrack.Isrc);
        var strictIsrcResult = TryValidateStrictIsrc(sourceIsrc, candidate, strictMode);
        if (strictIsrcResult != null)
        {
            return strictIsrcResult;
        }

        if (HasDerivativeMismatch(sourceTrack, candidate))
        {
            return CandidateValidationResult.Reject("derivative_mismatch");
        }

        var strictWithoutIsrc = strictMode && string.IsNullOrWhiteSpace(sourceIsrc);

        var titleCheck = TryValidateTitleScore(sourceTrack, candidate, strictWithoutIsrc, out var titleScore);
        if (titleCheck != null)
        {
            return titleCheck;
        }

        var artistCheck = TryValidateArtistScore(sourceTrack, candidate, strictWithoutIsrc, titleScore, out var artistScore);
        if (artistCheck != null)
        {
            return artistCheck;
        }

        var durationCheck = TryValidateDurationScore(sourceTrack, candidate, strictWithoutIsrc, out var durationScore);
        if (durationCheck != null)
        {
            return durationCheck;
        }

        var albumCheck = TryValidateAlbumScore(sourceTrack, candidate, strictWithoutIsrc, titleScore, out var albumScore);
        if (albumCheck != null)
        {
            return albumCheck;
        }

        var releaseYearScore = ComputeReleaseYearScore(
            ParseReleaseYear(sourceTrack.ReleaseDate),
            ParseCandidateReleaseYear(candidate));
        var compositeScore = ComputeCompositeScore(titleScore, artistScore, durationScore, albumScore, releaseYearScore);
        if (compositeScore < ResolveCompositeThreshold(strictMode, strictWithoutIsrc))
        {
            return CandidateValidationResult.Reject("composite_below_threshold", compositeScore);
        }

        return CandidateValidationResult.Accept(compositeScore);
    }

    private static CandidateValidationResult? TryValidateStrictIsrc(string? sourceIsrc, ApiTrack candidate, bool strictMode)
    {
        if (!strictMode || string.IsNullOrWhiteSpace(sourceIsrc))
        {
            return null;
        }

        var candidateIsrc = NormalizeIsrc(candidate.Isrc);
        return string.IsNullOrWhiteSpace(candidateIsrc) ||
               !string.Equals(candidateIsrc, sourceIsrc, StringComparison.OrdinalIgnoreCase)
            ? CandidateValidationResult.Reject("isrc_mismatch")
            : null;
    }

    private static bool HasDerivativeMismatch(SpotifyTrackSummary sourceTrack, ApiTrack candidate)
    {
        var sourceAllowsVariant = SourceAllowsVariant(sourceTrack);
        return !sourceAllowsVariant && IsVariantCandidate(candidate);
    }

    private static bool SourceAllowsVariant(SpotifyTrackSummary sourceTrack)
    {
        var titleDescriptor = NormalizeDescriptor(sourceTrack.Name);
        var albumDescriptor = NormalizeDescriptor(sourceTrack.Album);
        return ContainsDerivativeMarker(titleDescriptor)
               || ContainsVersionMarker(titleDescriptor)
               || ContainsVersionMarker(albumDescriptor);
    }

    private static bool IsVariantCandidate(ApiTrack track)
    {
        return IsDerivativeCandidate(track) || IsVersionCandidate(track);
    }

    private static bool IsVersionCandidate(ApiTrack track)
    {
        var descriptor = NormalizeDescriptor($"{track.Title} {track.TitleShort} {track.TitleVersion}");
        return ContainsVersionMarker(descriptor);
    }

    private static CandidateValidationResult? TryValidateTitleScore(
        SpotifyTrackSummary sourceTrack,
        ApiTrack candidate,
        bool strictWithoutIsrc,
        out double titleScore)
    {
        var expectedTitle = NormalizeDescriptor(RemoveFeaturing(sourceTrack.Name));
        titleScore = ComputeTitleScore(sourceTrack, candidate);
        var minTitleSimilarity = strictWithoutIsrc
            ? MinAcceptedTitleSimilarityStrictNoIsrc
            : MinAcceptedTitleSimilarity;
        return !string.IsNullOrWhiteSpace(expectedTitle) && titleScore < minTitleSimilarity
            ? CandidateValidationResult.Reject("title_mismatch", titleScore)
            : null;
    }

    private static double ComputeTitleScore(SpotifyTrackSummary sourceTrack, ApiTrack candidate)
    {
        var expectedTitle = NormalizeDescriptor(RemoveFeaturing(sourceTrack.Name));
        var candidateTitle = NormalizeDescriptor(RemoveFeaturing(candidate.Title));
        var expectedCanonicalTitle = NormalizeTitleForMatch(sourceTrack.Name);
        var candidateCanonicalTitle = NormalizeTitleForMatch(candidate.Title);
        var candidateCanonicalWithVersion = NormalizeTitleForMatch($"{candidate.Title} {candidate.TitleVersion}".Trim());

        var titleCandidates = new List<double>();
        if (!string.IsNullOrWhiteSpace(expectedTitle) && !string.IsNullOrWhiteSpace(candidateTitle))
        {
            titleCandidates.Add(ComputeSimilarity(expectedTitle, candidateTitle));
        }
        if (!string.IsNullOrWhiteSpace(expectedCanonicalTitle) && !string.IsNullOrWhiteSpace(candidateCanonicalTitle))
        {
            titleCandidates.Add(ComputeSimilarity(expectedCanonicalTitle, candidateCanonicalTitle));
        }
        if (!string.IsNullOrWhiteSpace(expectedCanonicalTitle) && !string.IsNullOrWhiteSpace(candidateCanonicalWithVersion))
        {
            titleCandidates.Add(ComputeSimilarity(expectedCanonicalTitle, candidateCanonicalWithVersion));
        }

        return titleCandidates.Count == 0 ? 0.65 : titleCandidates.Max();
    }

    private static CandidateValidationResult? TryValidateArtistScore(
        SpotifyTrackSummary sourceTrack,
        ApiTrack candidate,
        bool strictWithoutIsrc,
        double titleScore,
        out double artistScore)
    {
        var expectedArtist = NormalizeDescriptor(GetPrimaryArtist(sourceTrack.Artists));
        var candidateArtist = NormalizeDescriptor(candidate.Artist?.Name);
        artistScore = ComputeArtistScore(sourceTrack, expectedArtist, candidateArtist);

        var minArtistSimilarity = strictWithoutIsrc
            ? MinAcceptedArtistSimilarityStrictNoIsrc
            : MinAcceptedArtistSimilarity;
        return !string.IsNullOrWhiteSpace(expectedArtist)
               && !string.IsNullOrWhiteSpace(candidateArtist)
               && artistScore < minArtistSimilarity
               && titleScore < 0.9
            ? CandidateValidationResult.Reject("artist_mismatch", artistScore)
            : null;
    }

    private static double ComputeArtistScore(SpotifyTrackSummary sourceTrack, string expectedArtist, string candidateArtist)
    {
        if (string.IsNullOrWhiteSpace(expectedArtist) || string.IsNullOrWhiteSpace(candidateArtist))
        {
            return 0.65;
        }

        var artistCandidates = new List<double>
        {
            ComputeSimilarity(expectedArtist, candidateArtist)
        };

        var expectedNoThe = StripThePrefix(expectedArtist);
        var candidateNoThe = StripThePrefix(candidateArtist);
        if (!string.Equals(expectedNoThe, expectedArtist, StringComparison.Ordinal)
            || !string.Equals(candidateNoThe, candidateArtist, StringComparison.Ordinal))
        {
            artistCandidates.Add(ComputeSimilarity(expectedNoThe, candidateNoThe));
        }

        var fullSourceArtist = NormalizeDescriptor(sourceTrack.Artists);
        if (!string.IsNullOrWhiteSpace(fullSourceArtist)
            && !string.Equals(fullSourceArtist, expectedArtist, StringComparison.Ordinal))
        {
            artistCandidates.Add(ComputeSimilarity(fullSourceArtist, candidateArtist));
        }

        return artistCandidates.Max();
    }

    private static CandidateValidationResult? TryValidateDurationScore(
        SpotifyTrackSummary sourceTrack,
        ApiTrack candidate,
        bool strictWithoutIsrc,
        out double durationScore)
    {
        durationScore = 0.65;
        if (sourceTrack.DurationMs is not > 0 || candidate.Duration <= 0)
        {
            return null;
        }

        var sourceSeconds = (int)Math.Round(sourceTrack.DurationMs.Value / 1000d);
        var durationDiff = Math.Abs(sourceSeconds - candidate.Duration);
        var maxDurationDiff = strictWithoutIsrc
            ? MaxAcceptedDurationDiffSecondsStrictNoIsrc
            : MaxAcceptedDurationDiffSeconds;
        if (durationDiff > maxDurationDiff)
        {
            return CandidateValidationResult.Reject("duration_mismatch");
        }

        durationScore = 1d - Math.Min(1d, durationDiff / (double)maxDurationDiff);
        return null;
    }

    private static CandidateValidationResult? TryValidateAlbumScore(
        SpotifyTrackSummary sourceTrack,
        ApiTrack candidate,
        bool strictWithoutIsrc,
        double titleScore,
        out double albumScore)
    {
        var expectedAlbum = NormalizeDescriptor(sourceTrack.Album);
        var candidateAlbum = NormalizeDescriptor(candidate.Album?.Title);
        albumScore = (!string.IsNullOrWhiteSpace(expectedAlbum) && !string.IsNullOrWhiteSpace(candidateAlbum))
            ? ComputeSimilarity(expectedAlbum, candidateAlbum)
            : 0.65;

        if (!strictWithoutIsrc
            || string.IsNullOrWhiteSpace(expectedAlbum)
            || string.IsNullOrWhiteSpace(candidateAlbum)
            || albumScore >= MinAcceptedAlbumSimilarityStrictNoIsrc
            || titleScore >= 0.95)
        {
            return null;
        }

        return CandidateValidationResult.Reject("album_mismatch", albumScore);
    }

    private static int? ParseCandidateReleaseYear(ApiTrack candidate)
    {
        return ParseReleaseYear(candidate.PhysicalReleaseDate)
               ?? ParseReleaseYear(candidate.ReleaseDate)
               ?? ParseReleaseYear(candidate.Album?.ReleaseDate);
    }

    private static double ComputeCompositeScore(
        double titleScore,
        double artistScore,
        double durationScore,
        double albumScore,
        double releaseYearScore)
    {
        return (titleScore * 0.50) +
               (artistScore * 0.20) +
               (durationScore * 0.20) +
               (albumScore * 0.10) +
               (releaseYearScore * 0.05);
    }

    private static double ResolveCompositeThreshold(bool strictMode, bool strictWithoutIsrc)
    {
        if (strictWithoutIsrc)
        {
            return MinAcceptedCompositeScoreStrictNoIsrc;
        }

        if (strictMode)
        {
            return MinAcceptedCompositeScoreStrict;
        }

        return MinAcceptedCompositeScore;
    }

    private static string? NormalizeDeezerId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0")
        {
            return null;
        }

        var extracted = TryExtractDeezerId(value);
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            return extracted;
        }

        return value;
    }

    private static string? BuildMetadataKey(SpotifyTrackSummary track)
    {
        var title = NormalizeToken(track.Name);
        var artist = NormalizeToken(track.Artists);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var durationSeconds = track.DurationMs is > 0
            ? (int)Math.Round(track.DurationMs.Value / 1000d)
            : 0;
        var durationToken = durationSeconds > 0 ? $"|{durationSeconds}s" : string.Empty;
        var album = NormalizeToken(track.Album);
        var year = ParseReleaseYear(track.ReleaseDate);
        var yearToken = year.HasValue ? $"|{year.Value}" : string.Empty;
        return string.IsNullOrWhiteSpace(album)
            ? $"{artist}|{title}{durationToken}{yearToken}"
            : $"{artist}|{title}|{album}{durationToken}{yearToken}";
    }

    private static string BuildCanonicalTrackKey(SpotifyTrackSummary track, string? normalizedIsrc)
    {
        var spotifyId = NormalizeToken(ExtractSpotifyTrackId(track));
        var artist = NormalizeToken(GetPrimaryArtist(track.Artists));
        var title = NormalizeToken(RemoveFeaturing(track.Name));
        var year = ParseReleaseYear(track.ReleaseDate)?.ToString(CultureInfo.InvariantCulture) ?? "0";
        var isrc = normalizedIsrc ?? "-";
        return $"{spotifyId}|{isrc}|{artist}|{title}|{year}";
    }

    private static string ExtractSpotifyTrackId(SpotifyTrackSummary track)
    {
        var normalizedTrackId = TrackIdNormalization.NormalizeSpotifyTrackIdOrNull(track.Id);
        if (!string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            return normalizedTrackId;
        }

        return TrackIdNormalization.ExtractSpotifyTrackIdFromUrl(track.SourceUrl) ?? string.Empty;
    }

    private static bool IsDerivativeCandidate(ApiTrack track)
    {
        var descriptor = NormalizeDescriptor($"{track.Title} {track.TitleShort} {track.TitleVersion} {track.Artist?.Name}");
        return ContainsDerivativeMarker(descriptor);
    }

    private static bool ContainsDerivativeMarker(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return DerivativeMarkers.Any(marker => ContainsWholeMarker(normalized, marker));
    }

    private static bool ContainsWholeMarker(string text, string marker)
    {
        return TextMatchUtils.ContainsWholeMarker(text, marker);
    }

    private static string NormalizeDescriptor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = normalized.Replace("–", " ").Replace("—", " ");
        normalized = StripDiacritics(normalized);
        normalized = ReplaceWithTimeout(normalized, @"[^\p{L}\p{Nd}]+", " ").Trim();
        normalized = ReplaceWithTimeout(normalized, @"\s+", " ");
        return normalized;
    }

    private static string StripDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark))
        {
            sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string NormalizeTitleForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var working = RemoveFeaturing(value);
        working = StripVersionDecorators(working);
        return NormalizeDescriptor(working);
    }

    private static string StripVersionDecorators(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var working = value;

        // Remove bracketed version descriptors e.g. "(Member ver.)", "[Remix]".
        working = ReplaceWithTimeout(
            working,
            @"[\(\[]\s*([^\)\]]+)\s*[\)\]]",
            static m =>
            {
                var inner = m.Groups[1].Value;
                return ContainsVersionMarker(inner) ? " " : m.Value;
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove trailing dash qualifiers that are version-like.
        working = ReplaceWithTimeout(
            working,
            @"\s*[-–—]\s*([^–—-]+)$",
            static m =>
            {
                var suffix = m.Groups[1].Value;
                return ContainsVersionMarker(suffix) ? string.Empty : m.Value;
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return working.Trim();
    }

    private static bool ContainsVersionMarker(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = NormalizeDescriptor(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return VersionMarkers.Any(marker => ContainsWholeMarker(normalized, marker));
    }

    private static string RemoveFeaturing(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Remove parenthetical/bracketed "with Artist" credits e.g. "(with Artist)"
        var stripped = ReplaceWithTimeout(
            value,
            @"\s*[\(\[]\s*with\s+[^\)\]]+[\)\]]",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Remove "feat/ft/featuring" and everything after
        stripped = ReplaceWithTimeout(
            stripped,
            @"\s*[\(\[]?\s*(feat|ft|featuring)\b.*$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return stripped.Trim();
    }

    private static string GetPrimaryArtist(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var parts = SplitWithTimeout(
            value,
            @"\s*(,|&| and | with | feat\. | ft\. | featuring )\s*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var separators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ",",
            "&",
            "and",
            "with",
            "feat.",
            "ft.",
            "featuring"
        };
        var primary = parts
            .Select(part => part?.Trim())
            .FirstOrDefault(trimmed =>
                !string.IsNullOrWhiteSpace(trimmed)
                && !separators.Contains(trimmed));
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary;
        }

        return value.Trim();
    }

    private static string StripThePrefix(string normalized)
    {
        if (normalized.StartsWith("the ", StringComparison.Ordinal) && normalized.Length > 4)
        {
            return normalized.Substring(4);
        }

        return normalized;
    }

    private static double ComputeSimilarity(string source, string candidate)
    {
        return TextMatchUtils.ComputeNormalizedSimilarity(source, candidate);
    }

    private static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = normalized.Replace("–", "-").Replace("—", "-");
        normalized = ReplaceWithTimeout(normalized, @"\(.*?\)", string.Empty);
        normalized = ReplaceWithTimeout(normalized, @"[^\p{L}\p{Nd}]+", " ").Trim();
        return normalized;
    }

    private static string? NormalizeIsrc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace("-", string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 12)
        {
            return null;
        }

        if (!normalized.All(char.IsLetterOrDigit))
        {
            return null;
        }

        return normalized;
    }

    private static int? ParseReleaseYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 4 && int.TryParse(trimmed.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
        {
            return year;
        }

        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.Year;
        }

        return null;
    }

    private static double ComputeReleaseYearScore(int? sourceYear, int? candidateYear)
    {
        if (!sourceYear.HasValue || !candidateYear.HasValue)
        {
            return 0.6;
        }

        var diff = Math.Abs(sourceYear.Value - candidateYear.Value);
        if (diff == 0)
        {
            return 1.0;
        }

        if (diff == 1)
        {
            return 0.75;
        }

        if (diff == 2)
        {
            return 0.5;
        }

        return 0.0;
    }

    private static string? TryExtractDeezerId(string? deezerUrl)
    {
        if (string.IsNullOrWhiteSpace(deezerUrl))
        {
            return null;
        }

        var match = MatchWithTimeout(
            deezerUrl,
            @"deezer\.com\/track\/(?<id>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static bool IsTransientException(Exception ex)
    {
        if (ex is TaskCanceledException || ex is TimeoutException || ex is OperationCanceledException)
        {
            return true;
        }

        var message = ex.Message ?? string.Empty;
        if (message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("temporar", StringComparison.OrdinalIgnoreCase)
            || message.Contains("network", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetCached(
        System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> cache,
        string key,
        out string? deezerId)
    {
        deezerId = null;
        if (cache.TryGetValue(key, out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.Stamp < CacheTtl)
            {
                deezerId = entry.DeezerId;
                return true;
            }

            cache.TryRemove(key, out _);
        }

        return false;
    }

    private static bool TryGetCanonicalCached(string key, out CanonicalCacheEntry entry, out bool isNegative)
    {
        entry = default!;
        isNegative = false;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (!CanonicalTrackCache.TryGetValue(key, out var cachedEntry))
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - cachedEntry.Stamp;
        var ttl = string.IsNullOrWhiteSpace(cachedEntry.DeezerId)
            ? NegativeCanonicalCacheTtl
            : PositiveCanonicalCacheTtl;

        if (age > ttl)
        {
            CanonicalTrackCache.TryRemove(key, out _);
            return false;
        }

        entry = cachedEntry;
        isNegative = string.IsNullOrWhiteSpace(cachedEntry.DeezerId);
        return true;
    }

    private static void PromoteValidatedCanonicalCacheEntry(
        string key,
        CanonicalCacheEntry cachedEntry,
        string deezerId,
        ILogger logger)
    {
        var stamp = DateTimeOffset.UtcNow;
        var promoted = new CanonicalCacheEntry(stamp, deezerId, "validated", cachedEntry.Score);
        CanonicalTrackCache[key] = promoted;

        if (PersistentCanonicalCache.ContainsKey(key))
        {
            PersistentCanonicalCache[key] = new PersistentCacheEntry(stamp, deezerId, "validated", cachedEntry.Score);
            _persistentCacheDirty = true;
            TryFlushPersistentCache(logger);
        }
    }

    private static void InvalidateCanonicalCacheEntry(string key, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        CanonicalTrackCache.TryRemove(key, out _);
        if (PersistentCanonicalCache.TryRemove(key, out _))
        {
            _persistentCacheDirty = true;
            TryFlushPersistentCache(logger);
        }
    }

    private static void CacheValue(
        System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> cache,
        string key,
        string? deezerId)
    {
        cache[key] = new CacheEntry(DateTimeOffset.UtcNow, deezerId);
    }

    private static void CacheCanonicalMiss(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        CanonicalTrackCache[key] = new CanonicalCacheEntry(DateTimeOffset.UtcNow, null, "unresolved", 0d);
    }

    private static void CacheCanonicalMatch(
        string key,
        string deezerId,
        string strategy,
        double score,
        bool persist,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(deezerId))
        {
            return;
        }

        var stamp = DateTimeOffset.UtcNow;
        CanonicalTrackCache[key] = new CanonicalCacheEntry(stamp, deezerId, strategy, score);

        if (!persist)
        {
            return;
        }

        PersistentCanonicalCache[key] = new PersistentCacheEntry(stamp, deezerId, strategy, score);
        _persistentCacheDirty = true;
        TryFlushPersistentCache(logger);
    }

    private static void EnsurePersistentCacheLoaded(ILogger logger)
    {
        if (_persistentCacheLoaded)
        {
            return;
        }

        lock (PersistentCacheSync)
        {
            if (_persistentCacheLoaded)
            {
                return;
            }

            try
            {
                LoadPersistentCacheFromDisk();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(ex, "Failed to load Spotify-Deezer persistent mapping cache from {Path}", PersistentCachePath);
                }
            }
            finally
            {
                _persistentCacheLoaded = true;
            }
        }
    }

    private static void LoadPersistentCacheFromDisk()
    {
        if (!TryReadPersistentCacheDocument(out var document) || document?.Entries == null)
        {
            return;
        }

        foreach (var entry in document.Entries)
        {
            if (!TryGetPersistentEntryStamp(entry, out var stamp))
            {
                continue;
            }

            var strategy = entry!.Strategy ?? "persisted";
            CanonicalTrackCache[entry.Key] = new CanonicalCacheEntry(stamp, entry.DeezerId, strategy, entry.Score);
            PersistentCanonicalCache[entry.Key] = new PersistentCacheEntry(stamp, entry.DeezerId, strategy, entry.Score);
        }
    }

    private static bool TryReadPersistentCacheDocument(out PersistentCacheDocument? document)
    {
        document = null;
        if (!File.Exists(PersistentCachePath))
        {
            return false;
        }

        var payload = File.ReadAllText(PersistentCachePath);
        document = JsonSerializer.Deserialize<PersistentCacheDocument>(payload);
        return document != null;
    }

    private static bool TryGetPersistentEntryStamp(PersistentCacheRecord? entry, out DateTimeOffset stamp)
    {
        stamp = DateTimeOffset.MinValue;
        if (entry == null
            || string.IsNullOrWhiteSpace(entry.Key)
            || string.IsNullOrWhiteSpace(entry.DeezerId))
        {
            return false;
        }

        stamp = DateTimeOffset.FromUnixTimeSeconds(entry.StampUnix);
        return DateTimeOffset.UtcNow - stamp <= PositiveCanonicalCacheTtl;
    }

    private static void TryFlushPersistentCache(ILogger logger)
    {
        if (!_persistentCacheDirty)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastPersistentFlush < PersistentFlushInterval)
        {
            return;
        }

        lock (PersistentCacheSync)
        {
            if (!_persistentCacheDirty)
            {
                return;
            }

            try
            {
                var path = PersistentCachePath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var now = DateTimeOffset.UtcNow;
                var entries = PersistentCanonicalCache
                    .Where(kvp => now - kvp.Value.Stamp <= PositiveCanonicalCacheTtl)
                    .Select(kvp => new PersistentCacheRecord(
                        kvp.Key,
                        kvp.Value.DeezerId,
                        kvp.Value.Strategy,
                        kvp.Value.Score,
                        kvp.Value.Stamp.ToUnixTimeSeconds()))
                    .ToList();

                var document = new PersistentCacheDocument(entries);
                var json = JsonSerializer.Serialize(document);
                File.WriteAllText(path, json);

                _lastPersistentFlush = now;
                _persistentCacheDirty = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(ex, "Failed to persist Spotify-Deezer mapping cache to {Path}", PersistentCachePath);
                }
            }
        }
    }

    private static string ResolvePersistentCachePath()
    {
        var dataDir = AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir());

        return Path.Join(dataDir, "spotify", "spotify-deezer-track-map.json");
    }

    private sealed record CacheEntry(DateTimeOffset Stamp, string? DeezerId);

    private sealed record CanonicalCacheEntry(
        DateTimeOffset Stamp,
        string? DeezerId,
        string Strategy,
        double Score);

    private sealed record PersistentCacheEntry(
        DateTimeOffset Stamp,
        string DeezerId,
        string Strategy,
        double Score);

    private sealed record PersistentCacheDocument(List<PersistentCacheRecord> Entries);

    private sealed record PersistentCacheRecord(
        string Key,
        string DeezerId,
        string? Strategy,
        double Score,
        long StampUnix);

    private sealed record CandidateValidationResult(
        bool IsAccepted,
        string Reason,
        double Score,
        bool IsTransient)
    {
        public static CandidateValidationResult Accept(double score)
            => new(true, "accepted", score, false);

        public static CandidateValidationResult Reject(string reason, double score = 0d, bool isTransient = false)
            => new(false, reason, score, isTransient);
    }
}
