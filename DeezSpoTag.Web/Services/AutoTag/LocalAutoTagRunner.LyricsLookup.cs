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

    private static string NormalizeLyricsLookupSource(string platformId)
    {
        return platformId switch
        {
            ItunesPlatform => AppleProvider,
            _ => string.IsNullOrWhiteSpace(platformId) ? string.Empty : platformId
        };
    }

    private static string? TryGetFirstOtherValue(Dictionary<string, List<string>> other, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!other.TryGetValue(key, out var values) || values == null)
            {
                continue;
            }

            var value = values.FirstOrDefault(static raw => !string.IsNullOrWhiteSpace(raw))?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static void AddLookupUrl(Dictionary<string, string> urls, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
        {
            urls[key] = value.Trim();
        }
    }

    private static DeezSpoTagSettings BuildLyricsLookupSettings(
        DeezSpoTagSettings baseSettings,
        bool wantsSynced,
        bool wantsUnsynced,
        bool wantsTtml)
    {
        var allowsSyncedBySettings = baseSettings.SyncedLyrics;
        var allowsUnsyncedBySettings = baseSettings.SaveLyrics;
        var shouldFetchUnsyncedPayload = wantsUnsynced || wantsTtml;
        return new DeezSpoTagSettings
        {
            DeezerCountry = baseSettings.DeezerCountry,
            AppleMusic = baseSettings.AppleMusic,
            Video = baseSettings.Video,
            SyncedLyrics = allowsSyncedBySettings && (wantsSynced || wantsTtml),
            SaveLyrics = allowsUnsyncedBySettings && shouldFetchUnsyncedPayload,
            SynthesizeLrcFromTtml = baseSettings.SynthesizeLrcFromTtml,
            SynthesizeTtmlFromLrc = baseSettings.SynthesizeTtmlFromLrc,
            PreferEnhancedLrc = baseSettings.PreferEnhancedLrc,
            LyricsFallbackEnabled = baseSettings.LyricsFallbackEnabled,
            LyricsFallbackOrder = string.IsNullOrWhiteSpace(baseSettings.LyricsFallbackOrder)
                ? string.Join(",", LyricsProviderRegistry.DefaultOrder)
                : baseSettings.LyricsFallbackOrder,
            LrcFormat = NormalizeLyricsFormat(baseSettings.LrcFormat),
            LrcType = string.IsNullOrWhiteSpace(baseSettings.LrcType)
                ? "lyrics,syllable-lyrics,ttml-lyrics,unsynced-lyrics"
                : baseSettings.LrcType,
            Tags = new TagSettings
            {
                Lyrics = allowsUnsyncedBySettings && shouldFetchUnsyncedPayload,
                SyncedLyrics = allowsSyncedBySettings && (wantsSynced || wantsTtml)
            }
        };
    }

    private static LyricsProviderOptions? BuildLyricsProviderOptions(JsonObject? custom)
    {
        if (custom == null
            || !custom.TryGetPropertyValue(LrclibProvider, out var lrclibNode)
            || lrclibNode is not JsonObject)
        {
            return null;
        }

        var lrclibConfig = LoadConfig(custom, LrclibProvider, new LrclibConfig());
        return new LyricsProviderOptions
        {
            Lrclib = new LrclibLyricsProviderOptions
            {
                DurationToleranceSeconds = lrclibConfig.DurationToleranceSeconds,
                UseDurationHint = lrclibConfig.UseDurationHint,
                SearchFallback = lrclibConfig.SearchFallback,
                PreferSynced = lrclibConfig.PreferSynced
            }
        };
    }

    private static LyricsPopulationRequest BuildLyricsPopulationRequest(
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings)
    {
        var requestFlags = ApplyLyricsPreferenceGate(
            settings,
            new LyricsRequestFlags(
                HasAnyTags(config, SyncedLyricsTag),
                HasAnyTags(config, UnsyncedLyricsTag),
                HasAnyTags(config, TtmlLyricsTag)));

        var sidecarState = GetLyricsSidecarState(filePath);
        var timingPreference = LrcTimingModes.Normalize(settings.LrcTimingPreference, settings.PreferEnhancedLrc);
        var existingLrcIsWord = sidecarState.HasLrc
            && LrcContent.IsWordSynchronized(ReadFileOrEmpty(Path.ChangeExtension(filePath, ".lrc")));
        var lrcSatisfies = sidecarState.HasLrc
            && (timingPreference == LrcTimingModes.Line || existingLrcIsWord);
        if (lrcSatisfies)
        {
            requestFlags = requestFlags with { WantsSynced = false, WantsUnsynced = false };
        }

        if (sidecarState.HasTtml)
        {
            requestFlags = requestFlags with { WantsTtml = false };
        }

        return new LyricsPopulationRequest(
            requestFlags.WantsSynced,
            requestFlags.WantsUnsynced,
            requestFlags.WantsTtml,
            track.Other.TryGetValue(SyncedLyricsTag, out var existingSynced) && existingSynced.Count > 0,
            (track.Other.TryGetValue(UnsyncedLyricsTag, out var existingUnsynced) && existingUnsynced.Count > 0)
                || (track.Other.TryGetValue(LyricsTag, out var existingLyrics) && existingLyrics.Count > 0),
            track.Other.TryGetValue(TtmlLyricsTag, out var existingTtml)
                && existingTtml.Any(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static void ApplyResolvedLyrics(
        AutoTagTrack track,
        LyricsBase lyrics,
        LyricsPopulationRequest request,
        DeezSpoTagSettings settings)
    {
        ApplySyncedLyrics(track, lyrics, request);
        ApplyUnsyncedLyrics(track, lyrics, request);
        ApplyTtmlLyrics(track, lyrics, request, settings);
    }

    private static void ApplySyncedLyrics(AutoTagTrack track, LyricsBase lyrics, LyricsPopulationRequest request)
    {
        if (!request.WantsSynced || request.HasSynced)
        {
            return;
        }

        var syncedLines = lyrics.SyncedLyrics?
            .Where(line => line.IsValid())
            .Select(line => line.ToString())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (syncedLines is not { Count: > 0 })
        {
            return;
        }

        SetLyrics(track, SyncedLyricsTag, syncedLines);
        if (lyrics.CanSaveLrcSidecar())
        {
            track.Other[SyncedLyricsSourceFormatTag] = new List<string> { lyrics.SyncedLyricsSourceFormat.ToString() };
        }
    }

    private static void ApplyUnsyncedLyrics(AutoTagTrack track, LyricsBase lyrics, LyricsPopulationRequest request)
    {
        if (!request.WantsUnsynced || request.HasUnsynced || string.IsNullOrWhiteSpace(lyrics.UnsyncedLyrics))
        {
            return;
        }

        var unsyncedLines = lyrics.UnsyncedLyrics
            .Split(LyricsLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (unsyncedLines.Count == 0)
        {
            return;
        }

        SetLyrics(track, UnsyncedLyricsTag, unsyncedLines);
    }

    private static void ApplyTtmlLyrics(
        AutoTagTrack track,
        LyricsBase lyrics,
        LyricsPopulationRequest request,
        DeezSpoTagSettings settings)
    {
        if (!request.WantsTtml || request.HasTtml)
        {
            return;
        }

        if (!AppleLyricsService.IsWordSyncedTtml(lyrics.TtmlLyrics))
        {
            DownloadLyricsService.TryApplySynthesizedWordTtml(lyrics, settings);
        }

        if (!AppleLyricsService.IsWordSyncedTtml(lyrics.TtmlLyrics))
        {
            return;
        }

        track.Other[TtmlLyricsTag] = new List<string> { lyrics.TtmlLyrics! };
    }

    private static void SetLyrics(AutoTagTrack track, string tag, List<string> lines)
    {
        track.Other[tag] = lines;
        if (!track.Other.TryGetValue(LyricsTag, out var existingLyricsLines) || existingLyricsLines.Count == 0)
        {
            track.Other[LyricsTag] = lines;
        }
    }

    private static LyricsRequestFlags ApplyLyricsPreferenceGate(
        DeezSpoTagSettings settings,
        LyricsRequestFlags requestFlags)
    {
        var allowsSyncedByToggle = settings.SyncedLyrics;
        var allowsUnsyncedByToggle = settings.SaveLyrics;
        if (!allowsSyncedByToggle && !allowsUnsyncedByToggle)
        {
            return requestFlags with { WantsSynced = false, WantsUnsynced = false, WantsTtml = false };
        }

        var selectedTypes = ParseLyricsTypeSelection(settings.LrcType);
        var allowsSyncedTypes = selectedTypes.Contains(LyricsTag) || selectedTypes.Contains(SyllableLyricsType);
        var allowsUnsyncedTypes = selectedTypes.Contains(UnsyncedLyricsType);
        var allowsTtmlTypes = selectedTypes.Contains(TtmlLyricsType);
        var selectedFormats = ParseLyricsFormatSelection(settings.LrcFormat);

        return requestFlags with
        {
            WantsSynced = requestFlags.WantsSynced && allowsSyncedByToggle && allowsSyncedTypes,
            WantsUnsynced = requestFlags.WantsUnsynced && allowsUnsyncedByToggle && allowsUnsyncedTypes,
            WantsTtml = requestFlags.WantsTtml
                && allowsSyncedByToggle
                && allowsTtmlTypes
                && selectedFormats.Contains("ttml")
        };
    }

    private static bool LyricsSidecarsSatisfyPreference(
        string filePath,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings)
    {
        var flags = ApplyLyricsPreferenceGate(
            settings,
            new LyricsRequestFlags(
                HasAnyTags(config, SyncedLyricsTag),
                HasAnyTags(config, UnsyncedLyricsTag),
                HasAnyTags(config, TtmlLyricsTag)));

        if (flags.WantsTtml)
        {
            var ttmlPath = Path.ChangeExtension(filePath, TtmlExtension);
            if (!IOFile.Exists(ttmlPath) || !AppleLyricsService.IsWordSyncedTtml(ReadFileOrEmpty(ttmlPath)))
            {
                return false;
            }
        }

        if (flags.WantsSynced)
        {
            var lrcPath = Path.ChangeExtension(filePath, ".lrc");
            if (!IOFile.Exists(lrcPath))
            {
                return false;
            }
            if (LrcTimingModes.ImpliesEnhanced(LrcTimingModes.Normalize(settings.LrcTimingPreference, settings.PreferEnhancedLrc))
                && !LrcContent.IsWordSynchronized(ReadFileOrEmpty(lrcPath)))
            {
                return false;
            }
        }

        if (flags.WantsUnsynced && !flags.WantsSynced && !flags.WantsTtml
            && !IOFile.Exists(Path.ChangeExtension(filePath, ".txt")))
        {
            return false;
        }

        return flags.WantsSynced || flags.WantsUnsynced || flags.WantsTtml;
    }

    private static string ReadFileOrEmpty(string path)
    {
        try
        {
            return IOFile.Exists(path) ? IOFile.ReadAllText(path) : string.Empty;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return string.Empty;
        }
    }

    private static List<string> ResolveLyricsTimingBadges(string filePath, AutoTagRunnerConfig config, DeezSpoTagSettings settings)
        => LyricsSidecarTimingBadges.FromAudioPath(filePath).ToList();

    private static List<string> ResolveAnimatedArtworkBadges(string filePath, DeezSpoTagSettings settings)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new List<string>();
        }

        return Directory.EnumerateFiles(directory)
            .Any(path => AnimatedArtworkNaming.IsAlbumAnimatedArtworkSidecar(
                path,
                settings.AnimatedArtworkSquareFileName,
                settings.AnimatedArtworkTallFileName))
            ? new List<string> { "animated-artwork" }
            : new List<string>();
    }

    private static string? ResolveLyricsRowCoverUrl(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        foreach (var name in new[] { "cover.jpg", "cover.png", "folder.jpg", "folder.png" })
        {
            var candidate = Path.Join(directory, name);
            if (IOFile.Exists(candidate))
            {
                return $"/api/library/image?path={Uri.EscapeDataString(candidate)}&size=240";
            }
        }

        return null;
    }

    private static bool ShouldRequestAnyLyrics(AutoTagRunnerConfig config, DeezSpoTagSettings settings)
    {
        var requestFlags = ApplyLyricsPreferenceGate(
            settings,
            new LyricsRequestFlags(
                HasAnyTags(config, SyncedLyricsTag),
                HasAnyTags(config, UnsyncedLyricsTag),
                HasAnyTags(config, TtmlLyricsTag)));
        return requestFlags.WantsSynced || requestFlags.WantsUnsynced || requestFlags.WantsTtml;
    }

    private static HashSet<string> ParseLyricsTypeSelection(string? raw)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            selected.Add(LyricsTag);
            selected.Add(SyllableLyricsType);
            selected.Add(TtmlLyricsType);
            selected.Add(UnsyncedLyricsType);
            return selected;
        }

        foreach (var normalized in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value =>
            {
                var normalized = value.Trim().ToLowerInvariant();
                return normalized switch
                {
                    "synced-lyrics" => LyricsTag,
                    "time-synced-lyrics" or "timesynced-lyrics" or "time_synced_lyrics" => SyllableLyricsType,
                    "ttml" or "ttmllyrics" or "ttml_lyrics" => TtmlLyricsType,
                    "unsyncedlyrics" or "unsynced" => UnsyncedLyricsType,
                    _ => normalized
                };
            }))
        {
            selected.Add(normalized);
        }

        if (selected.Count == 0)
        {
            selected.Add(LyricsTag);
            selected.Add(SyllableLyricsType);
            selected.Add(TtmlLyricsType);
            selected.Add(UnsyncedLyricsType);
        }

        return selected;
    }

    private static string NormalizeLyricsFormat(string? raw)
    {
        var formats = ParseLyricsFormatSelection(raw);
        if (formats.Contains("lrc") && formats.Contains("ttml"))
        {
            return "both";
        }

        if (formats.Contains("ttml"))
        {
            return "ttml";
        }

        return "lrc";
    }

    private static HashSet<string> ParseLyricsFormatSelection(string? raw)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var normalized in NormalizeLyricsFormatToken(token))
            {
                selected.Add(normalized);
            }
        }

        if (selected.Count == 0)
        {
            selected.Add("lrc");
            selected.Add("ttml");
        }

        return selected;
    }

    private static IReadOnlyList<string> NormalizeLyricsFormatToken(string? token)
        => token?.Trim().ToLowerInvariant() switch
        {
            "lrc" => ["lrc"],
            "standard-lrc" => ["lrc"],
            "synced" => ["lrc"],
            "synced-lyrics" => ["lrc"],
            "elrc" => ["lrc"],
            "enhanced-lrc" => ["lrc"],
            "enhanced-synchronized-lyrics" => ["lrc"],
            "ttml" => ["ttml"],
            "both" => ["lrc", "ttml"],
            "richlyrics" => ["lrc", "ttml"],
            "rich-lyrics" => ["lrc", "ttml"],
            "lyrics" => ["lrc", "ttml"],
            "lrc+ttml" => ["lrc", "ttml"],
            "ttml+lrc" => ["lrc", "ttml"],
            "all" => ["lrc", "ttml"],
            _ => []
        };

    private static IEnumerable<string> EnumerateAudioFiles(string rootPath, bool includeSubfolders)
    {
        var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(rootPath, "*.*", option)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path))
                && !AnimatedArtworkFileNaming.IsAnimatedArtworkSidecar(path));
    }

    private static IEnumerable<string> ResolveTargetFiles(string rootPath, AutoTagRunnerConfig config)
    {
        if (config.TargetFiles == null || config.TargetFiles.Count == 0)
        {
            return EnumerateAudioFiles(rootPath, config.IncludeSubfolders);
        }

        var normalizedRoot = NormalizeScopePath(DownloadPathResolver.ResolveIoPath(rootPath));
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in config.TargetFiles)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var ioPath = DownloadPathResolver.ResolveIoPath(rawPath.Trim());
            if (string.IsNullOrWhiteSpace(ioPath))
            {
                continue;
            }

            var normalizedPath = NormalizeScopePath(ioPath);
            if (string.IsNullOrWhiteSpace(normalizedPath)
                || !IsPathWithinScope(normalizedPath, normalizedRoot)
                || !IOFile.Exists(normalizedPath)
                || !SupportedExtensions.Contains(Path.GetExtension(normalizedPath))
                || AnimatedArtworkFileNaming.IsAnimatedArtworkSidecar(normalizedPath))
            {
                continue;
            }

            selected.Add(normalizedPath);
        }

        return selected;
    }

    private static string NormalizeScopePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool IsPathWithinScope(string candidatePath, string scopePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(scopePath))
        {
            return false;
        }

        if (string.Equals(candidatePath, scopePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var scopeWithSeparator = scopePath.EndsWith(Path.DirectorySeparatorChar)
            || scopePath.EndsWith(Path.AltDirectorySeparatorChar)
            ? scopePath
            : scopePath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(scopeWithSeparator, StringComparison.OrdinalIgnoreCase);
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

    private static List<string> ResolveLyricsProviderOrder(AutoTagRunnerConfig config)
        => config.Platforms
            .Select(platform => platform?.Trim())
            .Where(platform => !string.IsNullOrWhiteSpace(platform) && IsLyricsOnlyPlatform(platform!))
            .Select(platform => platform!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsLyricsOnlyPlatform(string platform)
        => LyricsProviderRegistry.TryGet(platform, out var provider)
           && provider.IsLyricsOnly;

    private Dictionary<string, HashSet<SupportedTag>> BuildPlatformSupportedTags()
    {
        var map = (_platformRegistry?.DescribeAll() ?? Array.Empty<AutoTagPlatformDescriptor>())
            .Where(descriptor => !string.IsNullOrWhiteSpace(descriptor.Id))
            .GroupBy(descriptor => descriptor.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(descriptor => descriptor.SupportedTags).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);
        map[LyricsPlatform] = new HashSet<SupportedTag>
        {
            SupportedTag.SyncedLyrics,
            SupportedTag.UnsyncedLyrics,
            SupportedTag.TtmlLyrics
        };
        return map;
    }

    private sealed class PlatformMatchContext
    {
        public required string FilePath { get; init; }
        public required AutoTagRunnerConfig Config { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required AutoTagMatchingConfig MatchingConfig { get; init; }
        public required IDictionary<string, ShazamRecognitionInfo?> ShazamCache { get; init; }
        public required bool IsManualEnrichment { get; init; }
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

    private async Task<AutoTagMatchResult?> MatchLyricsProviderAsync(
        string provider,
        AutoTagAudioInfo info,
        PlatformMatchContext context,
        bool enableLyrics,
        bool hasLyricsSidecar,
        CancellationToken token)
    {
        if (!enableLyrics || hasLyricsSidecar)
        {
            return null;
        }

        var track = BuildLyricsOnlyAutoTagTrack(info);
        var request = BuildLyricsPopulationRequest(context.FilePath, track, context.Config, context.Settings);
        if (!request.ShouldFetch || request.HasAllRequestedLyrics())
        {
            return null;
        }

        var lookupSettings = BuildLyricsLookupSettings(
            context.Settings,
            request.WantsSynced,
            request.WantsUnsynced,
            request.WantsTtml);
        lookupSettings.LyricsFallbackEnabled = true;
        lookupSettings.LyricsFallbackOrder = provider;

        var lyrics = await _downloadLyricsService.ResolveLyricsAsync(
            BuildLyricsLookupTrack(track, provider),
            lookupSettings,
            BuildLyricsProviderOptions(context.Config.Custom),
            token);
        if (lyrics == null || !lyrics.IsLoaded())
        {
            return null;
        }

        ApplyResolvedLyrics(track, lyrics, request, context.Settings);
        return new AutoTagMatchResult
        {
            Accuracy = 1.0,
            Track = track
        };
    }

    private static AutoTagTrack BuildLyricsOnlyAutoTagTrack(AutoTagAudioInfo info)
    {
        var artists = info.Artists
            .Where(static artist => !string.IsNullOrWhiteSpace(artist))
            .Select(static artist => artist.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count == 0 && !string.IsNullOrWhiteSpace(info.Artist))
        {
            artists.Add(info.Artist.Trim());
        }

        return new AutoTagTrack
        {
            Title = info.Title ?? string.Empty,
            Artists = artists,
            Album = info.Album,
            Duration = info.DurationSeconds is > 0 ? TimeSpan.FromSeconds(info.DurationSeconds.Value) : null,
            Isrc = info.Isrc,
            TrackNumber = info.TrackNumber,
            Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
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

    private static DeezerConfig ResolveDeezerMatchConfig(AutoTagRunnerConfig config)
        => LoadConfig(config.Custom, DeezerPlatform, new DeezerConfig());

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

    private static AutoTagAudioInfo CloneAudioInfo(AutoTagAudioInfo source)
    {
        return new AutoTagAudioInfo
        {
            Title = source.Title,
            Artist = source.Artist,
            Artists = source.Artists.ToList(),
            Album = source.Album,
            DurationSeconds = source.DurationSeconds,
            Isrc = source.Isrc,
            TrackNumber = source.TrackNumber,
            FilePath = source.FilePath,
            Tags = source.Tags.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            HasEmbeddedTitle = source.HasEmbeddedTitle,
            HasEmbeddedArtist = source.HasEmbeddedArtist
        };
    }

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

    private static bool HasTagValue(AutoTagAudioInfo info, params string[] keys)
    {
        return !string.IsNullOrWhiteSpace(ReadFirstTagValue(info.Tags, keys));
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
}
