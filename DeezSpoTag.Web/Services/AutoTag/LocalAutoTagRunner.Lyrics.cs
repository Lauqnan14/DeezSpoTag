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

    private static string NormalizeLyricsLookupSource(string platformId)
    {
        return platformId switch
        {
            ItunesPlatform => AppleProvider,
            _ => string.IsNullOrWhiteSpace(platformId) ? string.Empty : platformId
        };
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
            // Same source as the download pipeline: the profile's lrclib / musixmatch /
            // betterlyrics cards.
            Lrclib = baseSettings.Lrclib ?? new LrclibOptions(),
            Musixmatch = baseSettings.Musixmatch ?? new MusixmatchOptions(),
            BetterLyrics = baseSettings.BetterLyrics ?? new BetterLyricsOptions(),
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

    private static List<string> ResolveLyricsTimingBadges(string filePath, AutoTagRunnerConfig config, DeezSpoTagSettings settings)
        => LyricsSidecarTimingBadges.FromAudioPath(filePath).ToList();

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

    private static void ApplyTrackAndLyricsTagWrites(
        TagLib.File file,
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context)
    {
        WriteDiscNumberTag(file, context);
        WriteDiscTotalTag(file, context);
        WriteTrackNumberTag(file, context);
        WriteBarcodeTag(tagWriteContext, context);
        WriteReplayGainTag(tagWriteContext, context);
        WriteCopyrightTag(tagWriteContext, context);
        WriteComposerTag(tagWriteContext, context);
        WriteLyricistTag(tagWriteContext, context);
        WriteInvolvedPeopleTag(tagWriteContext, context);
        WritePublisherTag(tagWriteContext, context);
        WriteDescriptionTag(tagWriteContext, context);
        WriteSourceTag(tagWriteContext, context);
        WriteRatingTag(tagWriteContext, context);
        WriteLanguageTag(tagWriteContext, context);
        WriteSyncedLyrics(file, context);
        WriteUnsyncedLyrics(file, context);
        WriteExplicitTag(tagWriteContext, context);
        WriteOtherTags(tagWriteContext, context);
        WriteMetaTag(tagWriteContext, context);
    }

    private static void WriteLyricistTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(LyricistTag) || !context.EffectiveTagSettings.Lyricist)
        {
            return;
        }

        var values = ResolveFirstClassOrOtherValues(context.SourceTrack.Lyricist, context.SourceTrack, LyricistTag, LyricistRawTag, "TEXT");
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, LyricistTag, ResolveLyricistRawName(context.Extension), values);
    }

    private static void WriteSyncedLyrics(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!ShouldWriteSyncedLyrics(context))
        {
            return;
        }

        if (WriteLyrics(file, context.Extension, context.SourceTrack, true, context.Config))
        {
            context.AttemptedTags.Add(SupportedTag.SyncedLyrics);
        }
    }

    private static void WriteUnsyncedLyrics(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!ShouldWriteUnsyncedLyrics(context))
        {
            return;
        }

        if (WriteLyrics(file, context.Extension, context.SourceTrack, false, context.Config))
        {
            context.AttemptedTags.Add(SupportedTag.UnsyncedLyrics);
        }
    }

    private static bool ShouldWriteSyncedLyrics(TagWriteExecutionContext context)
    {
        return context.EnabledTags.Contains(SyncedLyricsTag)
            && context.EffectiveTagSettings.SyncedLyrics
            && context.AllowsLyricsBySettings
            && context.AllowsSyncedType
            && !context.ShouldSkipEmbeddedLyrics;
    }

    private static bool ShouldWriteUnsyncedLyrics(TagWriteExecutionContext context)
    {
        return context.EnabledTags.Contains(UnsyncedLyricsTag)
            && context.EffectiveTagSettings.Lyrics
            && context.AllowsLyricsBySettings
            && context.AllowsUnsyncedType
            && !context.ShouldSkipEmbeddedLyrics;
    }

    private static async Task<LyricsSidecarWriteResult> WriteLyricsSidecarsAsync(
        TagWriteExecutionContext context,
        CancellationToken token)
    {
        var wroteLrcSidecar = false;
        var wroteTtmlSidecar = false;
        var sidecarLrcLines = ResolveLrcSidecarLines(context.SourceTrack, context.FilePath, context.Settings);
        if (context.AllowsLyricsBySettings
            && context.AllowsLrcByFormat
            && (context.AllowsSyncedType || context.AllowsUnsyncedType)
            && sidecarLrcLines.Count > 0)
        {
            var lrcPath = BuildLyricsSidecarPath(context, ".lrc");
            if (!IOFile.Exists(lrcPath) || ShouldUpgradeLrcSidecarToWordTiming(context, lrcPath, sidecarLrcLines))
            {
                await IOFile.WriteAllLinesAsync(lrcPath, sidecarLrcLines, token);
                wroteLrcSidecar = true;
            }
        }

        var sidecarTtml = ResolveTtmlSidecarPayload(context.SourceTrack, context.FilePath);
        if (context.EnabledTags.Contains(TtmlLyricsTag)
            && context.AllowsLyricsBySettings
            && context.AllowsTtmlByFormat
            && AppleLyricsService.IsWordSyncedTtml(sidecarTtml))
        {
            var ttmlPath = BuildLyricsSidecarPath(context, TtmlExtension);
            if (!IOFile.Exists(ttmlPath) || ShouldUpgradeTtmlSidecarToWordTiming(ttmlPath, sidecarTtml))
            {
                await IOFile.WriteAllTextAsync(ttmlPath, sidecarTtml, token);
                wroteTtmlSidecar = true;
            }
        }

        return new LyricsSidecarWriteResult(wroteLrcSidecar, wroteTtmlSidecar);
    }

    private static bool ShouldUpgradeTtmlSidecarToWordTiming(string ttmlPath, string? incomingTtml)
    {
        try
        {
            var existing = IOFile.ReadAllText(ttmlPath);
            if (!AppleLyricsService.IsWordSyncedTtml(existing))
            {
                return true;
            }

            return AppleLyricsService.IsAppleNativeTtml(incomingTtml)
                && !AppleLyricsService.IsAppleNativeTtml(existing);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static bool ShouldUpgradeLrcSidecarToWordTiming(
        TagWriteExecutionContext context,
        string lrcPath,
        IReadOnlyList<string> sidecarLrcLines)
    {
        if (!context.Settings.PreferEnhancedLrc || !LrcContent.IsWordSynchronized(sidecarLrcLines))
        {
            return false;
        }

        try
        {
            return !LrcContent.IsWordSynchronized(IOFile.ReadAllLines(lrcPath));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static string BuildLyricsSidecarPath(TagWriteExecutionContext context, string extension)
    {
        if (context.Config.OrganizeSidecarsIntoTemplateFolders == true)
        {
            var pathInfo = BuildTemplatePathInfo(context.CoreTrack, context.Settings);
            if (!string.IsNullOrWhiteSpace(pathInfo.FilePath)
                && !string.IsNullOrWhiteSpace(pathInfo.Filename))
            {
                Directory.CreateDirectory(pathInfo.FilePath);
                return Path.Join(pathInfo.FilePath, $"{pathInfo.Filename}{extension}");
            }
        }

        return Path.ChangeExtension(context.FilePath, extension);
    }

    private static bool ShouldAllowLyricsOtherTagKey(
        string key,
        bool allowsLyricsBySettings,
        bool allowsSyncedType,
        bool allowsUnsyncedType,
        bool allowsTtmlByFormat,
        bool allowLyricsPayloadWrites)
    {
        if (!IsLyricsPayloadKey(key))
        {
            return true;
        }

        if (!allowsLyricsBySettings || !allowLyricsPayloadWrites)
        {
            return false;
        }

        if (key.Equals(SyncedLyricsTag, StringComparison.OrdinalIgnoreCase))
        {
            return allowsSyncedType;
        }

        if (key.Equals(UnsyncedLyricsTag, StringComparison.OrdinalIgnoreCase))
        {
            return allowsUnsyncedType;
        }

        if (key.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase))
        {
            return allowsSyncedType && allowsTtmlByFormat;
        }

        return allowsSyncedType || allowsUnsyncedType;
    }

    private static bool IsLyricsPayloadKey(string key)
    {
        return key.Equals(LyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(SyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(UnsyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(SyncedLyricsSourceFormatTag, StringComparison.OrdinalIgnoreCase);
    }

    private static (bool HasAny, bool HasLrc, bool HasTtml, bool HasTxt, string TxtPath) GetLyricsSidecarState(string filePath)
    {
        var lrcPath = Path.ChangeExtension(filePath, ".lrc");
        var ttmlPath = Path.ChangeExtension(filePath, TtmlExtension);
        var txtPath = Path.ChangeExtension(filePath, ".txt");
        var hasLrc = IOFile.Exists(lrcPath);
        var hasTtml = HasTimedTtmlSidecar(ttmlPath);
        var hasTxt = IOFile.Exists(txtPath);
        return (hasLrc || hasTtml || hasTxt, hasLrc, hasTtml, hasTxt, txtPath);
    }

    private static bool HasTimedTtmlSidecar(string path)
    {
        if (!IOFile.Exists(path))
        {
            return false;
        }

        try
        {
            return AppleLyricsService.IsWordSyncedTtml(IOFile.ReadAllText(path));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static void AddAutoTagLyricsAndOtherTags(AutoTagTrack track, Action<string, bool> add)
    {
        var otherKeys = track.Other.Keys.ToList();
        var hasSyncedLyrics = HasOtherKey(otherKeys, SyncedLyricsTag);
        var hasUnsyncedLyrics = HasAnyOtherKey(otherKeys, UnsyncedLyricsTag, LyricsTag);
        var hasTtmlLyrics = HasOtherKey(otherKeys, TtmlLyricsTag);
        add(SyncedLyricsTag, hasSyncedLyrics);
        add(UnsyncedLyricsTag, hasUnsyncedLyrics);
        add(TtmlLyricsTag, hasTtmlLyrics);

        var hasOtherTags = HasNonLyricsOtherTag(otherKeys);
        add(OtherTagsTag, hasOtherTags);
    }

    private static bool HasNonLyricsOtherTag(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (key.Equals(SyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(UnsyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(LyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(SyncedLyricsSourceFormatTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(ReleaseTypeRawTag, StringComparison.OrdinalIgnoreCase)
                || IsNonPersistedOtherRawKey(key))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool HasTimestampedLyricsPayload(IEnumerable<string> values)
    {
        return values.Any(ContainsTimestampedLyrics);
    }

    private static bool ContainsTimestampedLyrics(string? rawLyrics)
    {
        if (string.IsNullOrWhiteSpace(rawLyrics))
        {
            return false;
        }

        return rawLyrics
            .Split(LyricsLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => TryParseLrcLine(line, out _, out _));
    }

    private static bool WriteLyrics(TagLib.File file, string extension, AutoTagTrack track, bool synced, AutoTagRunnerConfig config)
    {
        if (!TryResolveLyricsLines(track, synced, out var lyricsLines))
        {
            return false;
        }

        var lyricsText = string.Join(Environment.NewLine, lyricsLines);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return WriteId3Lyrics(file, synced, config, lyricsLines, lyricsText);
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return WriteVorbisLyrics(file, synced, config, lyricsText);
        }

        return WriteGenericLyrics(file, synced, config, lyricsText);
    }

    private static bool TryResolveLyricsLines(AutoTagTrack track, bool synced, out List<string> lyricsLines)
    {
        var key = synced ? SyncedLyricsTag : UnsyncedLyricsTag;
        if (track.Other.TryGetValue(key, out var preferred) && preferred is { Count: > 0 })
        {
            lyricsLines = preferred;
            return true;
        }

        if (track.Other.TryGetValue(LyricsTag, out var fallback) && fallback is { Count: > 0 })
        {
            lyricsLines = fallback;
            return true;
        }

        lyricsLines = new List<string>();
        return false;
    }

    private static bool WriteId3Lyrics(
        TagLib.File file,
        bool synced,
        AutoTagRunnerConfig config,
        IReadOnlyList<string> lyricsLines,
        string lyricsText)
    {
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        if (synced)
        {
            return WriteId3SyncedLyrics(id3, config, lyricsLines);
        }

        return WriteId3UnsyncedLyrics(id3, config, lyricsText);
    }

    private static bool WriteId3SyncedLyrics(TagLib.Id3v2.Tag id3, AutoTagRunnerConfig config, IReadOnlyList<string> lyricsLines)
    {
        if (!ShouldOverwriteTag(config, SupportedTag.SyncedLyrics)
            && id3.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>("SYLT").Any())
        {
            return true;
        }

        if (!lyricsLines.Any(line => line.StartsWith('[')))
        {
            return false;
        }

        var lang = string.IsNullOrWhiteSpace(config.Id3CommLang) ? "eng" : config.Id3CommLang;
        var frame = new TagLib.Id3v2.SynchronisedLyricsFrame(string.Empty, lang, TagLib.Id3v2.SynchedTextType.Lyrics)
        {
            Format = TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds
        };

        frame.Text = BuildSyncedLyricsItems(lyricsLines).ToArray();
        id3.AddFrame(frame);
        return true;
    }

    private static List<TagLib.Id3v2.SynchedText> BuildSyncedLyricsItems(IReadOnlyList<string> lyricsLines)
    {
        var items = new List<TagLib.Id3v2.SynchedText>();
        foreach (var line in lyricsLines)
        {
            if (!TryParseLrcLine(line, out var timestamp, out var text))
            {
                continue;
            }

            items.Add(new TagLib.Id3v2.SynchedText((long)timestamp.TotalMilliseconds, text));
        }

        return items;
    }

    private static bool WriteId3UnsyncedLyrics(TagLib.Id3v2.Tag id3, AutoTagRunnerConfig config, string lyricsText)
    {
        if (!ShouldOverwriteTag(config, SupportedTag.UnsyncedLyrics)
            && id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>("USLT").Any())
        {
            return true;
        }

        var lang = string.IsNullOrWhiteSpace(config.Id3CommLang) ? "eng" : config.Id3CommLang;
        var frame = TagLib.Id3v2.UnsynchronisedLyricsFrame.Get(id3, string.Empty, lang, true);
        frame.Text = lyricsText;
        return true;
    }

    private static bool WriteVorbisLyrics(TagLib.File file, bool synced, AutoTagRunnerConfig config, string lyricsText)
    {
        var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        var supportedTag = synced ? SupportedTag.SyncedLyrics : SupportedTag.UnsyncedLyrics;
        if (!ShouldOverwriteTag(config, supportedTag) && TagRawProbe.HasVorbisRaw(vorbis, LyricsUpperTag))
        {
            return true;
        }

        vorbis.SetField(LyricsUpperTag, lyricsText);
        return true;
    }

    private static bool WriteGenericLyrics(TagLib.File file, bool synced, AutoTagRunnerConfig config, string lyricsText)
    {
        var supportedTag = synced ? SupportedTag.SyncedLyrics : SupportedTag.UnsyncedLyrics;
        if (!ShouldOverwriteTag(config, supportedTag) && !string.IsNullOrWhiteSpace(file.Tag.Lyrics))
        {
            return true;
        }

        file.Tag.Lyrics = lyricsText;
        return true;
    }

    private static bool TryParseLrcLine(string line, out TimeSpan timestamp, out string text)
    {
        timestamp = TimeSpan.Zero;
        text = "";
        if (line.Length < 6 || line[0] != '[')
        {
            return false;
        }

        var end = line.IndexOf(']');
        if (end <= 0)
        {
            return false;
        }

        var ts = line[1..end];
        var parts = ts.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var minutes))
        {
            return false;
        }

        if (!double.TryParse(parts[1], out var seconds))
        {
            return false;
        }

        timestamp = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        text = line[(end + 1)..].Trim();
        return true;
    }

    private static IReadOnlyList<string> ResolveLrcSidecarLines(AutoTagTrack sourceTrack, string filePath, DeezSpoTagSettings settings)
    {
        var syncedPayload = ResolveLyricsPayloadLines(sourceTrack, SyncedLyricsTag);
        var payloadUsable = syncedPayload.Count > 0 && HasLrcSidecarSourceFormat(sourceTrack);
        var timingPreference = LrcTimingModes.Normalize(settings.LrcTimingPreference, settings.PreferEnhancedLrc);
        var payloadIsWord = payloadUsable && LrcContent.IsWordSynchronized(syncedPayload);

        var existingLrc = ResolveExistingLrcSidecar(filePath);
        if (existingLrc.Count > 0)
        {
            var existingIsWord = LrcContent.IsWordSynchronized(existingLrc);
            if (LrcTimingModes.ImpliesEnhanced(timingPreference)
                && payloadIsWord
                && !existingIsWord)
            {
                return syncedPayload;
            }

            return existingLrc;
        }

        if (timingPreference == LrcTimingModes.WordEnhanced)
        {
            return payloadIsWord ? syncedPayload : Array.Empty<string>();
        }

        return payloadUsable ? syncedPayload : Array.Empty<string>();
    }

    private static bool HasLrcSidecarSourceFormat(AutoTagTrack sourceTrack)
    {
        return sourceTrack.Other.TryGetValue(SyncedLyricsSourceFormatTag, out var values)
            && values.Any(value =>
                string.Equals(value, LyricsSourceFormat.DownloadedLrc.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, LyricsSourceFormat.ProviderSyncedJson.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ResolveExistingLrcSidecar(string filePath)
    {
        var existingLrcPath = Path.ChangeExtension(filePath, ".lrc");
        if (!IOFile.Exists(existingLrcPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            return NormalizeLyricsLines(IOFile.ReadAllLines(existingLrcPath), requireTimestamp: true);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ResolveLyricsPayloadLines(AutoTagTrack sourceTrack, string key)
    {
        if (!sourceTrack.Other.TryGetValue(key, out var payload) || payload.Count == 0)
        {
            return Array.Empty<string>();
        }

        return NormalizeLyricsLines(payload, requireTimestamp: true);
    }

    private static string? ResolveTtmlSidecarPayload(AutoTagTrack sourceTrack, string filePath)
    {
        var existingTtmlPath = Path.ChangeExtension(filePath, TtmlExtension);
        if (IOFile.Exists(existingTtmlPath)
            && AppleLyricsService.IsWordSyncedTtml(ReadFileOrEmpty(existingTtmlPath)))
        {
            return null;
        }

        if (sourceTrack.Other.TryGetValue(TtmlLyricsTag, out var ttmlPayload) && ttmlPayload.Count > 0)
        {
            var existing = ComposeTtmlPayload(ttmlPayload);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        return null;
    }

    private static List<string> NormalizeLyricsLines(IEnumerable<string> lines, bool requireTimestamp)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trimmed in lines
            .Select(static line => line?.Trim())
            .Where(static trimmed => !string.IsNullOrWhiteSpace(trimmed)))
        {
            if (requireTimestamp && !trimmed!.StartsWith('['))
            {
                continue;
            }

            if (seen.Add(trimmed!))
            {
                normalized.Add(trimmed!);
            }
        }

        return normalized;
    }

    private static string? ComposeTtmlPayload(IEnumerable<string> payloadLines)
    {
        var ttml = string.Join(Environment.NewLine, payloadLines.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(ttml) ? null : ttml;
    }

    private static void AddMp4AtlLyricsValues(List<string> values, ATL.Track atlTrack)
    {
        if (atlTrack.Lyrics == null || atlTrack.Lyrics.Count == 0)
        {
            return;
        }

        foreach (var line in atlTrack.Lyrics)
        {
            AddIfPresent(values, line?.UnsynchronizedLyrics);
        }
    }

    private static string ResolveLyricistRawName(string extension)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return "TEXT";
        }

        return LyricistRawTag;
    }
}
