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

public sealed partial class LocalAutoTagRunner : IAutoTagRunner
{

    private static HashSet<string> SharedSongDetailKeys => SupportedTagFeatureMappings.ShazamSongDetailsKeys;

    private static readonly (string Key, Func<AutoTagTrack, double?> Read, Action<AutoTagTrack, double?> Write)[] SharedSongFeatures =
    [
        ("melodicness", track => track.Melodicness, (track, value) => track.Melodicness = value),
        ("acousticness", track => track.Acousticness, (track, value) => track.Acousticness = value),
        ("valence", track => track.Valence, (track, value) => track.Valence = value),
        ("danceability", track => track.Danceability, (track, value) => track.Danceability = value),
        ("energy", track => track.Energy, (track, value) => track.Energy = value)
    ];

    private Task<AutoTagTrack?> GetSharedSongDetailsAsync(AutoTagFileRunContext context)
    {
        context.Token.ThrowIfCancellationRequested();
        if (!context.Plan.Config.Tags.Any(SharedSongDetailKeys.Contains)) return Task.FromResult<AutoTagTrack?>(null);
        // File index is stable when the existing organizer materializes/moves its path.
        return context.Plan.SharedSongDetails.GetOrAdd(context.FileIndex,
            _ => new Lazy<Task<AutoTagTrack?>>(() => LoadSharedSongDetailsAsync(context))).Value;
    }

    private async Task<AutoTagTrack?> LoadSharedSongDetailsAsync(AutoTagFileRunContext context)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            if (!context.Plan.ShazamCache.TryGetValue(context.File, out var recognized))
            {
                if (_shazamRecognitionService is null || !_shazamRecognitionService.IsAvailable) return null;
                recognized = await _shazamRecognitionService.RecognizeAsync(context.File, bounded.Token);
                context.Plan.ShazamCache[context.File] = recognized;
            }
            if (recognized?.SongDetails is { } existing) return existing;
            if (recognized is null || _shazamRecognitionService is null) return null;
            var details = await _shazamRecognitionService.GetSongDetailsAsync(recognized, bounded.Token);
            return details?.SongDetails;
        }
        catch (OperationCanceledException) when (!context.Token.IsCancellationRequested)
        { context.LogCallback("onetagger_autotag: optional Shazam song details timed out; continuing"); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Optional Shazam Song Details unavailable");
            context.LogCallback("onetagger_autotag: optional Shazam song details unavailable; continuing");
        }
        return null;
    }

    private static bool SameSharedDetailsRecording(AutoTagTrack native, AutoTagTrack details)
    {
        static string Identity(string? text) => string.Concat((text ?? "").Normalize().Where(char.IsLetterOrDigit)).ToUpperInvariant();
        var nativeTitle = OneTaggerMatching.FullTitle(native.Title, native.Version);
        var detailsTitle = OneTaggerMatching.FullTitle(details.Title, details.Version);
        return Identity(nativeTitle) == Identity(detailsTitle)
            && TrackTitleMatcher.HasCompatibleTitleIdentity(nativeTitle, detailsTitle)
            && native.Artists.Any(artist => details.Artists.Any(candidate => Identity(artist) == Identity(candidate)))
            && (string.IsNullOrWhiteSpace(native.Isrc) || string.IsNullOrWhiteSpace(details.Isrc) || Identity(native.Isrc) == Identity(details.Isrc))
            && (!native.Duration.HasValue || !details.Duration.HasValue || Math.Abs((native.Duration.Value - details.Duration.Value).TotalSeconds) <= 2);
    }

    private static HashSet<string> MergeSharedSongDetails(AutoTagTrack track, AutoTagTrack details, HashSet<string> eligible)
    {
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!SameSharedDetailsRecording(track, details)) return added;
        SupportedTagCreditMappings.NormalizeTrackCredits(track);
        if (eligible.Contains("bpm") && !track.Bpm.HasValue && details.Bpm is > 0)
        { track.Bpm = details.Bpm; added.Add("bpm"); }
        foreach (var field in SharedSongFeatures)
        {
            if (eligible.Contains(field.Key) && field.Read(track) is null && field.Read(details) is { } value)
            { field.Write(track, value); added.Add(field.Key); }
        }
        foreach (var role in SupportedTagCreditMappings.Definitions)
        {
            if (!eligible.Contains(role.Key) || CollectCreditRoleValues(track, role).Count > 0
                || !details.Other.TryGetValue(role.Key, out var values) || values.Count == 0) continue;
            track.Other[role.Key] = new List<string>(values);
            added.Add(role.Key);
        }
        if (eligible.Contains("involvedPeople") && !track.Other.ContainsKey("involvedPeople")
            && details.Other.TryGetValue("involvedPeople", out var legacy))
        { track.Other["involvedPeople"] = new List<string>(legacy); added.Add("involvedPeople"); }
        return added;
    }

    private static AutoTagRunnerConfig BuildSharedDetailsWriteConfig(AutoTagRunnerConfig config,
        HashSet<string> provisional, AutoTagTrack native)
    {
        var replace = CollectAutoTagTags(native).Where(key => provisional.Contains(key)).ToList();
        if (replace.Count == 0) return config;
        var effective = config.CopyForSharedSongDetails();
        effective.OverwriteTags = config.OverwriteTags.Concat(replace).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return effective;
    }

    private static HashSet<string> MissingSharedDetailKeys(AutoTagFileRunContext context)
    {
        var missing = context.Plan.Config.Tags.Where(SharedSongDetailKeys.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var file = TagLib.File.Create(context.File);
        var extension = Path.GetExtension(context.File);
        context.Plan.NativeSongDetailTags.TryGetValue(context.FileIndex, out var nativeFields);
        // The user may refresh pre-run values. A successful native provider's values
        // remain authoritative over this optional source for the rest of this run.
        missing.RemoveWhere(key => SupportedTagMap.TryGetValue(key, out var tag)
            && HasTag(file, extension, tag, context.Plan.Config, context.Platform)
            && (nativeFields?.Contains(key) == true || !ShouldOverwriteTag(context.Plan.Config, tag)));
        return missing;
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

    private async Task<AutoTagMatchResult?> MatchShazamAsync(
        string filePath,
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        AutoTagMatchingConfig matchingConfig,
        IDictionary<string, ShazamRecognitionInfo?> shazamCache,
        CancellationToken token)
    {
        var shazamConfig = LoadConfig(config.Custom, ShazamPlatform, new ShazamMatchConfig());
        var identityIsTrusted = IsTrustedSourceIdentity(info, filePath, config);
        if (shazamConfig.IdFirst && identityIsTrusted)
        {
            var idFirstMatch = await TryMatchShazamByIdsAsync(
                info,
                config,
                matchingConfig,
                token);
            if (idFirstMatch != null)
            {
                return idFirstMatch;
            }
        }

        if (!shazamConfig.FingerprintFallback)
        {
            return null;
        }

        return await _shazamMatcher.MatchAsync(
            filePath,
            info,
            matchingConfig,
            shazamConfig,
            shazamCache,
            token,
            trustSourceIdentity: identityIsTrusted);
    }

    private async Task<AutoTagMatchResult?> TryMatchShazamByIdsAsync(
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        AutoTagMatchingConfig matchingConfig,
        CancellationToken token)
    {
        var effectiveInfo = BuildShazamIdFirstInfo(info);

        var hasDeezerId = HasTagValue(effectiveInfo, DeezerTrackIdTag, "DEEZERID", "DEEZER_ID");
        var hasSpotifyId = HasTagValue(effectiveInfo, SpotifyTrackIdTag, SpotifyTrackIdLegacyTag, SpotifyIdLegacyTag, SpotifyIdUnderscoreLegacyTag);
        var hasIsrc = !string.IsNullOrWhiteSpace(effectiveInfo.Isrc);

        if (hasDeezerId)
        {
            var deezerConfig = ResolveDeezerMatchConfig(config);
            deezerConfig.MatchById = true;
            var byDeezerId = await _deezerMatcher.MatchAsync(effectiveInfo, matchingConfig, deezerConfig, token);
            if (HasUsableMatchIdentity(byDeezerId))
            {
                return PrepareShazamIdFirstMatch(byDeezerId!, DeezerPlatform, info);
            }
        }

        if (hasSpotifyId)
        {
            var bySpotifyId = await _spotifyMatcher.MatchAsync(effectiveInfo, matchingConfig, token);
            if (HasUsableMatchIdentity(bySpotifyId))
            {
                return PrepareShazamIdFirstMatch(bySpotifyId!, SpotifyPlatform, info);
            }
        }

        if (hasIsrc)
        {
            var deezerConfig = ResolveDeezerMatchConfig(config);
            deezerConfig.MatchById = true;
            var byDeezerIsrc = await _deezerMatcher.MatchAsync(effectiveInfo, matchingConfig, deezerConfig, token);
            if (HasUsableMatchIdentity(byDeezerIsrc))
            {
                return PrepareShazamIdFirstMatch(byDeezerIsrc!, DeezerPlatform, info);
            }

            var bySpotifyIsrc = await _spotifyMatcher.MatchAsync(effectiveInfo, matchingConfig, token);
            if (HasUsableMatchIdentity(bySpotifyIsrc))
            {
                return PrepareShazamIdFirstMatch(bySpotifyIsrc!, SpotifyPlatform, info);
            }
        }

        return null;
    }

    private static AutoTagAudioInfo BuildShazamIdFirstInfo(AutoTagAudioInfo source)
    {
        var cloned = CloneAudioInfo(source);

        var spotifyId = ExtractSpotifyTrackIdFromTags(cloned.Tags);
        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            cloned.Tags[SpotifyTrackIdTag] = new List<string> { spotifyId };
        }

        return cloned;
    }

    private static void RestoreRecognizedShazamIdentity(AutoTagMatchResult match, ShazamRecognitionInfo recognized)
    {
        if (string.IsNullOrWhiteSpace(recognized.TrackId)) return;
        var recording = new AutoTagTrack
        {
            Title = recognized.Title,
            Artists = ResolveShazamArtists(recognized),
            Isrc = recognized.Isrc,
            Duration = recognized.DurationMs is > 0 ? TimeSpan.FromMilliseconds(recognized.DurationMs.Value) : null
        };
        // The resolver's metadata must describe the fingerprinted recording before its
        // identity can be attached. Keep the original core metadata and provider IDs.
        if (!SameSharedDetailsRecording(match.Track, recording)) return;
        match.Track.TrackId = recognized.TrackId.Trim();
        match.Track.Other["SHAZAM_TRACK_ID"] = [match.Track.TrackId];
        match.Track.Url = ShazamOwnedUrl(recognized.Url);
        match.ProviderIdentity = CaptureProviderIdentity(ShazamPlatform, match);
    }

    private static AutoTagMatchResult PrepareShazamIdFirstMatch(
        AutoTagMatchResult match,
        string provider,
        AutoTagAudioInfo sourceInfo)
    {
        match.Track.Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        match.Track.Other["SHAZAM_MATCH_STRATEGY"] = new List<string> { "ID_FIRST" };
        match.Track.Other["SHAZAM_MATCH_PROVIDER"] = new List<string> { provider.ToUpperInvariant() };
        match.MatchStrategy = "id_first";

        var shazamTrackId = ReadFirstTagValue(sourceInfo.Tags, "SHAZAM_TRACK_ID", "SHAZAM_TRACK_KEY");
        var shazamUrl = ReadFirstTagValue(sourceInfo.Tags, "SHAZAM_URL");
        match.Track.Url = string.IsNullOrWhiteSpace(shazamUrl) ? null : shazamUrl.Trim();
        match.Track.ReleaseId = null;
        if (string.IsNullOrWhiteSpace(shazamTrackId))
        {
            match.Track.TrackId = null;
        }
        else
        {
            match.Track.TrackId = shazamTrackId.Trim();
            match.Track.Other["SHAZAM_TRACK_ID"] = [match.Track.TrackId];
        }

        return match;
    }

    private ShazamEnrichmentResult TryApplyShazam(
        string filePath,
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        bool enableShazamFallback,
        bool forceShazamMatch,
        Dictionary<string, ShazamRecognitionInfo?> cache,
        Action<string> logCallback,
        CancellationToken token)
    {
        var identityIsTrusted = IsTrustedSourceIdentity(info, filePath, config);
        if (!ShouldAttemptShazam(info, enableShazamFallback, forceShazamMatch, identityIsTrusted))
        {
            return new ShazamEnrichmentResult(false, null, false);
        }

        if (!IsShazamRecognitionAvailable())
        {
            if (forceShazamMatch)
            {
                return new ShazamEnrichmentResult(
                    false,
                    "shazam unavailable",
                    true,
                    ShazamFailureKind.Infrastructure);
            }

            // Degrade gracefully when optional Shazam fallback is unavailable.
            return new ShazamEnrichmentResult(false, "shazam unavailable", false);
        }

        token.ThrowIfCancellationRequested();

        var fromCache = cache.TryGetValue(filePath, out var recognized);
        ShazamRecognitionAttempt? attempt = null;
        if (!fromCache)
        {
            attempt = RecognizeWithShazamAttempt(filePath, token);
            recognized = attempt?.Recognition;
            cache[filePath] = recognized;
        }

        if (recognized == null)
        {
            var outcome = attempt?.Outcome ?? ShazamRecognitionOutcome.NoMatch;
            if (outcome is ShazamRecognitionOutcome.RecognizerError or ShazamRecognitionOutcome.RecognizerUnavailable)
            {
                cache.Remove(filePath); // Infrastructure failures must be retried, not cached as no-match.
                return new ShazamEnrichmentResult(
                    false,
                    attempt?.Error ?? "shazam unavailable",
                    true,
                    ShazamFailureKind.Infrastructure);
            }

            return forceShazamMatch
                ? new ShazamEnrichmentResult(false, "shazam could not identify track", false, ShazamFailureKind.NoMatch)
                : new ShazamEnrichmentResult(false, null, false);
        }

        if (!fromCache)
        {
            logCallback($"onetagger_autotag: shazam fingerprint identified {recognized.Artist} - {recognized.Title} for {Path.GetFileName(filePath)}");
        }

        var preferShazamCore = forceShazamMatch || !identityIsTrusted || IsLikelyNoisyCoreMetadata(info);
        var shazamConfig = LoadConfig(config.Custom, ShazamPlatform, new ShazamMatchConfig());
        ApplyShazamRecognition(info, recognized, preferShazamCore, shazamConfig);
        return new ShazamEnrichmentResult(true, null, false);
    }

    private static bool ShouldAttemptShazam(
        AutoTagAudioInfo info,
        bool enableShazamFallback,
        bool forceShazamMatch,
        bool identityIsTrusted)
    {
        if (forceShazamMatch || !identityIsTrusted)
        {
            return true;
        }

        if (!enableShazamFallback)
        {
            return false;
        }

        // Always attempt Shazam for raw files with no embedded core metadata.
        if (IsRawCoreMetadata(info))
        {
            return true;
        }

        // Shazam fallback is needed when core tags are missing or clearly noisy.
        return !info.HasEmbeddedTitle || !info.HasEmbeddedArtist || IsLikelyNoisyCoreMetadata(info);
    }

    private static (bool EnableFallback, bool ForceMatch) ResolveShazamEnrichmentBehavior(AutoTagRunnerConfig config)
    {
        var shazamEnabled = IsShazamPlatformEnabled(config);
        var shazamConfig = LoadConfig(config.Custom, ShazamPlatform, new ShazamMatchConfig());

        var hasShazamConfig = config.Custom != null
            && config.Custom.TryGetPropertyValue(ShazamPlatform, out var shazamNode)
            && shazamNode is JsonObject;

        if (config.ForceShazam || (hasShazamConfig && shazamConfig.ForceMatch))
        {
            return (true, true);
        }

        if (!shazamEnabled)
        {
            return (false, false);
        }

        if (hasShazamConfig)
        {
            return (shazamConfig.FallbackMissingCoreTags, shazamConfig.ForceMatch);
        }

        // Legacy fallback for older profiles/configs. At this point Shazam is enabled,
        // no explicit Shazam platform block exists, and ForceShazam was already handled.
        return (true, false);
    }

    private bool IsShazamRecognitionAvailable()
    {
        return _shazamRecognitionService.IsAvailable;
    }

    private static bool IsShazamConflictResolution(AutoTagRunnerConfig config)
    {
        return IsShazamPlatformEnabled(config)
            && string.Equals(config.ConflictResolution, ShazamPlatform, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShazamPlatformEnabled(AutoTagRunnerConfig config)
    {
        if (config.Platforms.Count == 0)
        {
            return false;
        }

        return config.Platforms.Any(platform => string.Equals(platform?.Trim(), ShazamPlatform, StringComparison.OrdinalIgnoreCase));
    }

    private ShazamRecognitionAttempt? RecognizeWithShazamAttempt(string filePath, CancellationToken token)
    {
        try
        {
            return _shazamRecognitionService.RecognizeWithDetails(filePath, cancellationToken: token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Shazam recognize failed for {File}", SanitizeLogValue(filePath));
            }
            return new ShazamRecognitionAttempt
            {
                Outcome = ShazamRecognitionOutcome.RecognizerError,
                Error = ex.Message
            };
        }
    }

    private static void ApplyShazamRecognition(
        AutoTagAudioInfo info,
        ShazamRecognitionInfo payload,
        bool forceShazam,
        ShazamMatchConfig config)
    {
        var shazamArtists = ResolveShazamArtists(payload);
        ApplyShazamCoreValues(info, payload, shazamArtists, forceShazam, config);
        ApplyShazamDurationAndTrackNumber(info, payload);
        ApplyShazamBaseTags(info, payload, config);
        ApplyShazamOptionalScalarTags(info, payload);
        ApplyShazamCollectionTags(info, payload);
    }

    private static List<string> ResolveShazamArtists(ShazamRecognitionInfo payload)
    {
        var shazamArtists = payload.Artists
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (shazamArtists.Count == 0 && !string.IsNullOrWhiteSpace(payload.Artist))
        {
            shazamArtists.Add(payload.Artist.Trim());
        }

        return shazamArtists;
    }

    private static void ApplyShazamCoreValues(
        AutoTagAudioInfo info,
        ShazamRecognitionInfo payload,
        List<string> shazamArtists,
        bool forceShazam,
        ShazamMatchConfig config)
    {
        if ((forceShazam || !info.HasEmbeddedTitle || string.IsNullOrWhiteSpace(info.Title))
            && !string.IsNullOrWhiteSpace(payload.Title))
        {
            info.Title = payload.Title.Trim();
        }

        if ((forceShazam || !info.HasEmbeddedArtist || string.IsNullOrWhiteSpace(info.Artist)) && shazamArtists.Count > 0)
        {
            info.Artists = shazamArtists.ToList();
            info.Artist = shazamArtists[0];
        }

        if (config.IncludeAlbum
            && (forceShazam || string.IsNullOrWhiteSpace(info.Album))
            && !string.IsNullOrWhiteSpace(payload.Album))
        {
            info.Album = payload.Album.Trim();
        }

        if ((forceShazam || string.IsNullOrWhiteSpace(info.Isrc)) && !string.IsNullOrWhiteSpace(payload.Isrc))
        {
            info.Isrc = payload.Isrc.Trim();
        }
    }

    private static void ApplyShazamDurationAndTrackNumber(AutoTagAudioInfo info, ShazamRecognitionInfo payload)
    {
        if (!info.DurationSeconds.HasValue && payload.DurationMs.HasValue)
        {
            var seconds = (int)Math.Round(payload.DurationMs.Value / 1000d);
            if (seconds > 0)
            {
                info.DurationSeconds = seconds;
            }
        }

        if (!info.TrackNumber.HasValue && payload.TrackNumber.HasValue && payload.TrackNumber.Value > 0)
        {
            info.TrackNumber = payload.TrackNumber.Value;
        }
    }

    private static void ApplyShazamBaseTags(AutoTagAudioInfo info, ShazamRecognitionInfo payload, ShazamMatchConfig config)
    {
        var preferredArtwork = config.PreferHqArtwork
            ? FirstNonEmpty(payload.ArtworkHqUrl, payload.ArtworkUrl)
            : FirstNonEmpty(payload.ArtworkUrl, payload.ArtworkHqUrl);
        SetShazamTag(info, "SHAZAM_TRACK_ID", payload.TrackId);
        SetShazamTag(info, "SHAZAM_TRACK_KEY", payload.TrackId);
        SetShazamTag(info, "SHAZAM_URL", payload.Url);
        SetShazamTag(info, "SHAZAM_TITLE", payload.Title);
        SetShazamTag(info, "SHAZAM_ARTIST", payload.Artist);
        if (config.IncludeGenre)
        {
            SetShazamTag(info, "SHAZAM_GENRE", payload.Genre);
        }

        if (config.IncludeAlbum)
        {
            SetShazamTag(info, "SHAZAM_ALBUM", payload.Album);
        }

        if (config.IncludeLabel)
        {
            SetShazamTag(info, "SHAZAM_LABEL", payload.Label);
        }

        if (config.IncludeReleaseDate)
        {
            SetShazamTag(info, "SHAZAM_RELEASE_DATE", payload.ReleaseDate);
        }

        SetShazamTag(info, "SHAZAM_ARTWORK", preferredArtwork);
        SetShazamTag(info, "SHAZAM_ARTWORK_HQ", payload.ArtworkHqUrl);
        SetShazamTag(info, "SHAZAM_ISRC", payload.Isrc);
        SetShazamTag(info, "SHAZAM_KEY", payload.Key);
        SetShazamTag(info, "SHAZAM_ALBUM_ADAM_ID", payload.AlbumAdamId);
        SetShazamTag(info, "SHAZAM_APPLE_MUSIC_URL", payload.AppleMusicUrl);
        SetShazamTag(info, "SHAZAM_SPOTIFY_URL", payload.SpotifyUrl);
        SetShazamTag(info, "SHAZAM_YOUTUBE_URL", payload.YoutubeUrl);
        SetShazamTag(info, "SHAZAM_LANGUAGE", payload.Language);
        SetShazamTag(info, "SHAZAM_COMPOSER", payload.Composer);
        SetShazamTag(info, "SHAZAM_LYRICIST", payload.Lyricist);
        SetShazamTag(info, "SHAZAM_PUBLISHER", payload.Publisher);
    }

    private static void ApplyShazamOptionalScalarTags(AutoTagAudioInfo info, ShazamRecognitionInfo payload)
    {
        if (payload.DurationMs.HasValue && payload.DurationMs.Value > 0)
        {
            SetShazamTag(info, "SHAZAM_DURATION_MS", payload.DurationMs.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (payload.TrackNumber.HasValue && payload.TrackNumber.Value > 0)
        {
            SetShazamTag(info, "SHAZAM_TRACK_NUMBER", payload.TrackNumber.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (payload.DiscNumber.HasValue && payload.DiscNumber.Value > 0)
        {
            SetShazamTag(info, "SHAZAM_DISC_NUMBER", payload.DiscNumber.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (payload.Explicit.HasValue)
        {
            SetShazamTag(info, "SHAZAM_EXPLICIT", payload.Explicit.Value ? "true" : "false");
        }
    }

    private static void ApplyShazamCollectionTags(AutoTagAudioInfo info, ShazamRecognitionInfo payload)
    {
        SetShazamTagValues(info, "SHAZAM_ARTIST_IDS", payload.ArtistIds);
        SetShazamTagValues(info, "SHAZAM_ARTIST_ADAM_IDS", payload.ArtistAdamIds);

        foreach (var (tagKey, tagValues) in payload.Tags)
        {
            SetShazamTagValues(info, tagKey, tagValues);
        }
    }

    private static void SetShazamTag(AutoTagAudioInfo info, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        info.Tags[key] = new List<string> { value.Trim() };
    }

    private static void SetShazamTagValues(AutoTagAudioInfo info, string key, IEnumerable<string>? values)
    {
        if (string.IsNullOrWhiteSpace(key) || values == null)
        {
            return;
        }

        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
        {
            return;
        }

        info.Tags[key.Trim()] = normalized;
    }
}
