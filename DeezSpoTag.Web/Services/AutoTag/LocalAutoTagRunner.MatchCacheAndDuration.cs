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

    private static string NormalizeCacheToken(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static string ComputeCacheHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static bool HasAnyTags(AutoTagRunnerConfig config, params string[] tags)
    {
        if (config.Tags == null || config.Tags.Count == 0)
        {
            return false;
        }

        var configured = BuildConfiguredTagSet(config.Tags);
        return tags.Any(configured.Contains);
    }

    private static HashSet<string> BuildConfiguredTagSet(IEnumerable<string>? tags)
    {
        var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tags == null)
        {
            return configured;
        }

        foreach (var trimmed in tags
            .Select(static rawTag => rawTag?.Trim())
            .Where(static trimmed => !string.IsNullOrWhiteSpace(trimmed)))
        {
            configured.Add(trimmed!);
            var normalized = NormalizeConfiguredTagKey(trimmed!);
            if (!string.Equals(normalized, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                configured.Add(normalized);
            }
        }

        return configured;
    }

    private static string NormalizeConfiguredTagKey(string tag)
    {
        return tag.Trim().ToLowerInvariant() switch
        {
            YearTag => ReleaseDateTag,
            DateTag => ReleaseDateTag,
            LengthTag => DurationTag,
            LyricsTag => UnsyncedLyricsTag,
            CoverTag => AlbumArtTag,
            _ => tag.Trim()
        };
    }

    private static T LoadConfig<T>(JsonObject? custom, string key, T fallback) where T : class, new()
    {
        if (custom == null || !custom.TryGetPropertyValue(key, out var node) || node == null)
        {
            return fallback;
        }

        try
        {
            var parsed = node.Deserialize<T>(CaseInsensitiveJsonOptions);
            return parsed ?? fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fallback;
        }
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
            logCallback($"onetagger_autotag: shazam identified {Path.GetFileName(filePath)}");
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

    private static bool IsRawCoreMetadata(AutoTagAudioInfo info)
    {
        return !info.HasEmbeddedTitle
            && !info.HasEmbeddedArtist
            && string.IsNullOrWhiteSpace(info.Isrc);
    }

    private static bool IsLikelyNoisyCoreMetadata(AutoTagAudioInfo info)
        => TrackIdentityTrust.IsWeakMetadataValue(info.Title)
            || TrackIdentityTrust.IsWeakMetadataValue(info.Artist);

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

    private static AutoTagRunnerConfig NormalizeConfig(AutoTagRunnerConfig? raw)
    {
        raw ??= new AutoTagRunnerConfig();
        var effectiveSaveArtwork = raw.SaveArtwork ?? false;
        return new AutoTagRunnerConfig
        {
            Platforms = raw.Platforms ?? new List<string>(),
            DownloadTagSource = raw.DownloadTagSource,
            Path = raw.Path,
            TargetFiles = raw.TargetFiles?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList(),
            PriorityTargetFiles = raw.PriorityTargetFiles?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList(),
            EditionConflictReview = raw.EditionConflictReview,
            Tags = raw.Tags ?? new List<string>(),
            OverwriteTags = raw.OverwriteTags ?? new List<string>(),
            Separators = raw.Separators == null
                ? null
                : new AutoTagSeparators
                {
                    Id3 = raw.Separators.Id3,
                    Vorbis = raw.Separators.Vorbis,
                    Mp4 = raw.Separators.Mp4
            },
            Overwrite = raw.Overwrite,
            MergeGenres = raw.MergeGenres,
            Camelot = raw.Camelot,
            ShortTitle = raw.ShortTitle,
            Strictness = raw.Strictness,
            MatchDuration = raw.MatchDuration,
            MaxDurationDifference = raw.MaxDurationDifference,
            MatchById = raw.MatchById,
            EnableShazam = raw.EnableShazam,
            ForceShazam = raw.ForceShazam,
            EnhancementUntrustedTargets = raw.EnhancementUntrustedTargets,
            ConflictResolution = raw.ConflictResolution,
            SkipTagged = raw.SkipTagged,
            IncludeSubfolders = raw.IncludeSubfolders,
            ParseFilename = raw.ParseFilename,
            Id3v24 = raw.Id3v24,
            TrackNumberLeadingZeroes = raw.TrackNumberLeadingZeroes,
            StylesOptions = raw.StylesOptions,
            MultipleMatches = raw.MultipleMatches,
            TitleRegex = raw.TitleRegex,
            Custom = raw.Custom,
            StylesCustomTag = raw.StylesCustomTag == null
                ? null
                : new AutoTagStylesCustomTag
                {
                    Id3 = raw.StylesCustomTag.Id3,
                    Vorbis = raw.StylesCustomTag.Vorbis,
                    Mp4 = raw.StylesCustomTag.Mp4
                },
            Id3CommLang = raw.Id3CommLang,
            CapitalizeGenres = raw.CapitalizeGenres,
            TracknameTemplate = raw.TracknameTemplate,
            FolderStructure = raw.FolderStructure,
            SaveArtwork = effectiveSaveArtwork,
            DlAlbumcoverForPlaylist = raw.DlAlbumcoverForPlaylist,
            SaveArtworkArtist = raw.SaveArtworkArtist,
            SaveAnimatedArtwork = raw.SaveAnimatedArtwork,
            SaveSquareAnimatedArtwork = raw.SaveSquareAnimatedArtwork,
            SaveTallAnimatedArtwork = raw.SaveTallAnimatedArtwork,
            AnimatedArtworkFormats = raw.AnimatedArtworkFormats,
            CoverImageTemplate = raw.CoverImageTemplate,
            AnimatedArtworkSquareFileName = raw.AnimatedArtworkSquareFileName,
            AnimatedArtworkTallFileName = raw.AnimatedArtworkTallFileName,
            ArtistImageTemplate = raw.ArtistImageTemplate,
            LocalArtworkFormat = raw.LocalArtworkFormat,
            MaterializeToTemplatePath = raw.MaterializeToTemplatePath,
            OrganizeSidecarsIntoTemplateFolders = raw.OrganizeSidecarsIntoTemplateFolders,
            EmbedMaxQualityCover = raw.EmbedMaxQualityCover,
            JpegImageQuality = raw.JpegImageQuality,
            AnimatedArtworkMaxSizeMb = raw.AnimatedArtworkMaxSizeMb,
            Technical = raw.Technical,
            ProfileId = raw.ProfileId,
            ProfileName = raw.ProfileName,
            LibraryWideEnhancementBatchSize = raw.LibraryWideEnhancementBatchSize,
            ManualReleasePreference = NormalizeManualReleasePreference(raw.ManualReleasePreference),
            ManualDestinationFolderId = raw.ManualDestinationFolderId
        };
    }

    private static string? NormalizeManualReleasePreference(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            AutoTagReleaseCategory.Album => AutoTagReleaseCategory.Album,
            AutoTagReleaseCategory.Single => AutoTagReleaseCategory.Single,
            _ => null
        };

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

    private AutoTagAudioInfo BuildAudioInfo(string filePath, string rootPath, bool parseFilename, string? tracknameTemplate, string? titleRegex)
    {
        var extension = Path.GetExtension(filePath);
        try
        {
            using var file = TagLib.File.Create(filePath);
            var draft = BuildAudioInfoDraft(file, filePath);

            PopulateAudioInfoTagMap(file, extension, draft.Tags);
            ApplyDraftTagFallbacks(draft);
            ApplyTracknameTemplateFallbacks(draft, filePath, parseFilename, tracknameTemplate);
            EnsureArtistFallbacks(draft, filePath, rootPath);
            draft.Title = ResolveTitleWithFallback(draft.Title, filePath, titleRegex);

            return CreateAudioInfoFromDraft(
                draft,
                filePath,
                extension,
                (int?)file.Properties.Duration.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed reading tags for {File}", SanitizeLogValue(filePath));
            return BuildAudioInfoFallback(filePath, rootPath, parseFilename, tracknameTemplate, titleRegex);
        }
    }

    private static AudioInfoDraft BuildAudioInfoDraft(TagLib.File file, string filePath)
    {
        var performerCredits = file.Tag.Performers?
            .Where(credit => !string.IsNullOrWhiteSpace(credit))
            .ToList()
            ?? new List<string>();
        if (!string.IsNullOrWhiteSpace(file.Tag.FirstPerformer))
        {
            performerCredits.Add(file.Tag.FirstPerformer!);
        }

        var artists = SplitArtistCredits(performerCredits);
        if (artists.Count > 0 && artists.All(IsWeakMetadataValue))
        {
            artists.Clear();
        }

        var firstPerformer = IsWeakMetadataValue(file.Tag.FirstPerformer)
            ? string.Empty
            : file.Tag.FirstPerformer ?? string.Empty;
        var title = IsWeakMetadataValue(file.Tag.Title) ? string.Empty : file.Tag.Title ?? string.Empty;
        var album = IsWeakMetadataValue(file.Tag.Album)
            ? InferAlbumFromPath(filePath)
            : file.Tag.Album;
        return new AudioInfoDraft
        {
            Title = title,
            Artist = artists.FirstOrDefault() ?? firstPerformer,
            Artists = artists,
            Album = string.IsNullOrWhiteSpace(album)
                ? InferAlbumFromPath(filePath)
                : album,
            Isrc = file.Tag.ISRC,
            TrackNumber = file.Tag.Track > 0 ? (int?)file.Tag.Track : null,
            HasEmbeddedTitle = !string.IsNullOrWhiteSpace(title),
            HasEmbeddedArtist = artists.Count > 0,
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void PopulateAudioInfoTagMap(TagLib.File file, string extension, Dictionary<string, List<string>> tags)
    {
        AddTagIfAny(tags, "BEATPORT_TRACK_ID", ReadRawTagValues(file, extension, "BEATPORT_TRACK_ID"));
        AddTagIfAny(tags, "DISCOGS_RELEASE_ID", ReadRawTagValues(file, extension, "DISCOGS_RELEASE_ID"));
        AddTagIfAny(tags, AutoTagIdentityTags.ItunesTrackId, ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleTrackIdAliases));
        AddTagIfAny(tags, AutoTagIdentityTags.AppleTrackId, ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleTrackIdAliases));
        AddTagIfAny(tags, "ITUNES_RELEASE_ID", ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleReleaseIdAliases));
        AddTagIfAny(tags, "ITUNES_ARTIST_ID", ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleArtistIdAliases));
        AddTagIfAny(tags, DeezerTrackIdTag, ReadRawTagValuesAny(file, extension, DeezerTrackIdTag, "DEEZERID", "DEEZER_ID"));
        AddTagIfAny(tags, "DEEZER_RELEASE_ID", ReadRawTagValuesAny(file, extension, "DEEZER_RELEASE_ID"));
        AddTagIfAny(tags, SpotifyTrackIdTag, ReadRawTagValuesAny(file, extension, SpotifyTrackIdTag, SpotifyTrackIdLegacyTag, SpotifyIdLegacyTag, SpotifyIdUnderscoreLegacyTag));
        AddTagIfAny(tags, SpotifyUrlTag, NormalizeSpotifyTrackUrls(ReadRawTagValuesAny(file, extension, SpotifyUrlTag, "SPOTIFYURI", "SPOTIFY_URI")));
        AddTagIfAny(tags, "URL", ReadRawTagValuesAny(file, extension, "URL"));
        AddTagIfAny(tags, WwwAudioFileTag, ReadRawTagValuesAny(file, extension, WwwAudioFileTag));
        AddTagIfAny(tags, "MUSICBRAINZ_RECORDING_ID", ReadRawTagValuesAny(file, extension, "MUSICBRAINZ_RECORDING_ID", "MUSICBRAINZ_RECORDINGID", "MUSICBRAINZ_TRACK_ID", "MUSICBRAINZ_TRACKID"));
        AddTagIfAny(tags, RecordingIdRawTag, ReadRawTagValuesAny(file, extension, RecordingIdRawTag));
        AddTagIfAny(tags, ArtistIdRawTag, ReadRawTagValuesAny(file, extension, ArtistIdRawTag));
        AddTagIfAny(tags, AlbumArtistIdRawTag, ReadRawTagValuesAny(file, extension, AlbumArtistIdRawTag));
        AddTagIfAny(tags, ReleaseGroupIdRawTag, ReadRawTagValuesAny(file, extension, ReleaseGroupIdRawTag));
        AddTagIfAny(tags, AlbumIdRawTag, ReadRawTagValuesAny(file, extension, AlbumIdRawTag));
        AddTagIfAny(tags, ReleaseStatusRawTag, ReadRawTagValuesAny(file, extension, ReleaseStatusRawTag));
        AddTagIfAny(tags, ReleaseCountryRawTag, ReadRawTagValuesAny(file, extension, ReleaseCountryRawTag));
        AddTagIfAny(tags, MediaRawTag, ReadRawTagValuesAny(file, extension, MediaRawTag));

        foreach (var shazamTag in ShazamRawTagHints)
        {
            AddTagIfAny(tags, shazamTag, ReadRawTagValuesAny(file, extension, shazamTag));
        }
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

    private static void ApplyDraftTagFallbacks(AudioInfoDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Isrc))
        {
            draft.Isrc = ReadFirstTagValue(draft.Tags, "SHAZAM_ISRC", "ISRC");
        }

        if (IsWeakMetadataValue(draft.Album))
        {
            draft.Album = ReadFirstTagValue(draft.Tags, "SHAZAM_ALBUM", AlbumUpperTag);
        }

        if (!draft.TrackNumber.HasValue)
        {
            draft.TrackNumber = ParsePositiveInt(ReadFirstTagValue(draft.Tags, "SHAZAM_TRACK_NUMBER", TrackNumberUpperTag));
        }
    }

    private static void ApplyTracknameTemplateFallbacks(
        AudioInfoDraft draft,
        string filePath,
        bool parseFilename,
        string? tracknameTemplate)
    {
        if (!parseFilename || (!IsWeakMetadataValue(draft.Title) && !IsWeakMetadataValue(draft.Artist)))
        {
            return;
        }

        var template = OneTaggerMatching.ParseFilenameTemplate(tracknameTemplate);
        if (!TryParseFilename(Path.GetFileName(filePath), template, out var parsedArtist, out var parsedTitle))
        {
            return;
        }

        if (IsWeakMetadataValue(draft.Artist))
        {
            draft.Artist = parsedArtist;
        }

        if (IsWeakMetadataValue(draft.Title))
        {
            draft.Title = parsedTitle;
        }

        if (draft.Artists.Count == 0 && !string.IsNullOrWhiteSpace(parsedArtist))
        {
            draft.Artists = SplitArtistCredits(new[] { parsedArtist });
        }
    }

    private static void EnsureArtistFallbacks(AudioInfoDraft draft, string filePath, string rootPath)
    {
        if (draft.Artists.Count > 0 && draft.Artists.All(IsWeakMetadataValue))
        {
            draft.Artists.Clear();
        }

        if (draft.Artists.Count == 0 && !IsWeakMetadataValue(draft.Artist))
        {
            draft.Artists = SplitArtistCredits(new[] { draft.Artist });
            draft.Artist = draft.Artists.FirstOrDefault() ?? draft.Artist;
        }

        if (!IsWeakMetadataValue(draft.Artist))
        {
            return;
        }

        draft.Artist = InferArtistFromPath(filePath, rootPath);
        if (draft.Artists.Count == 0 && !string.IsNullOrWhiteSpace(draft.Artist))
        {
            draft.Artists = SplitArtistCredits(new[] { draft.Artist });
            draft.Artist = draft.Artists.FirstOrDefault() ?? draft.Artist;
        }
    }

    private static string ResolveTitleWithFallback(string title, string filePath, string? titleRegex)
    {
        var resolved = IsWeakMetadataValue(title)
            ? InferTitleFromFilename(filePath)
            : title;

        return ApplyTitleRegexFilter(resolved, titleRegex);
    }

    private static string ApplyTitleRegexFilter(string title, string? titleRegex)
    {
        if (string.IsNullOrWhiteSpace(titleRegex) || string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        try
        {
            var regex = new Regex(titleRegex, RegexOptions.IgnoreCase, RegexTimeout);
            return regex.Replace(title, string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return title;
        }
    }

    private static AutoTagAudioInfo CreateAudioInfoFromDraft(
        AudioInfoDraft draft,
        string filePath,
        string extension,
        int? durationSeconds)
    {
        var normalizedDurationSeconds = NormalizeDurationSeconds(filePath, extension, durationSeconds)
            ?? ResolveDurationSecondsFromTags(draft.Tags)
            ?? ResolveDurationSecondsWithFfprobe(filePath, extension);

        return new AutoTagAudioInfo
        {
            Title = draft.Title,
            Artist = draft.Artist,
            Artists = draft.Artists.Count == 0 && !string.IsNullOrWhiteSpace(draft.Artist)
                ? new List<string> { draft.Artist }
                : draft.Artists,
            Album = string.IsNullOrWhiteSpace(draft.Album) ? null : draft.Album,
            DurationSeconds = normalizedDurationSeconds,
            Isrc = string.IsNullOrWhiteSpace(draft.Isrc) ? null : draft.Isrc,
            TrackNumber = draft.TrackNumber,
            FilePath = filePath,
            Tags = draft.Tags,
            HasEmbeddedTitle = draft.HasEmbeddedTitle,
            HasEmbeddedArtist = draft.HasEmbeddedArtist
        };
    }

    private static AutoTagAudioInfo BuildAudioInfoFallback(
        string filePath,
        string rootPath,
        bool parseFilename,
        string? tracknameTemplate,
        string? titleRegex)
    {
        var draft = new AudioInfoDraft
        {
            Title = InferTitleFromFilename(filePath),
            Artist = InferArtistFromPath(filePath, rootPath),
            Album = InferAlbumFromPath(filePath),
            Artists = new List<string>(),
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
        if (!string.IsNullOrWhiteSpace(draft.Artist))
        {
            draft.Artists = SplitArtistCredits(new[] { draft.Artist });
        }

        ApplyTracknameTemplateFallbacks(draft, filePath, parseFilename, tracknameTemplate);
        draft.Title = ApplyTitleRegexFilter(draft.Title, titleRegex);

        return new AutoTagAudioInfo
        {
            Title = draft.Title,
            Artist = draft.Artist,
            Artists = draft.Artists,
            Album = string.IsNullOrWhiteSpace(draft.Album) ? null : draft.Album,
            FilePath = filePath,
            HasEmbeddedTitle = false,
            HasEmbeddedArtist = false
        };
    }

    private sealed class AudioInfoDraft
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public List<string> Artists { get; set; } = new();
        public string? Album { get; set; }
        public string? Isrc { get; set; }
        public int? TrackNumber { get; set; }
        public bool HasEmbeddedTitle { get; set; }
        public bool HasEmbeddedArtist { get; set; }
        public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string InferArtistFromPath(string filePath, string rootPath)
    {
        try
        {
            var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (string.IsNullOrWhiteSpace(fileDir))
            {
                return string.Empty;
            }

            var rootFull = Path.GetFullPath(rootPath);
            var relativeDir = Path.GetRelativePath(rootFull, fileDir);
            if (!string.IsNullOrWhiteSpace(relativeDir) &&
                !relativeDir.StartsWith("..", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relativeDir))
            {
                var parts = relativeDir.Split(
                        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries)
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .ToArray();
                if (parts.Length >= 2)
                {
                    return parts[0].Trim();
                }
            }

            var parent = Directory.GetParent(fileDir);
            return parent?.Name?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string InferAlbumFromPath(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return string.IsNullOrWhiteSpace(dir) ? string.Empty : Path.GetFileName(dir).Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static bool IsSpecificFolderArtist(string? value)
        => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value);

    private static bool IsWeakMetadataValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim().Trim('[', ']');
        return WeakMetadataValues.Contains(normalized);
    }

    private static bool IsVariousArtistsValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Trim('[', ']').ToLowerInvariant();
        return normalized is "various artists" or "various" or "va" or "v/a";
    }

    private static string InferTitleFromFilename(string filePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return string.Empty;
        }

        var cleaned = LeadingTrackNumberRegex.Replace(baseName, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? baseName.Trim() : cleaned;
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
}
