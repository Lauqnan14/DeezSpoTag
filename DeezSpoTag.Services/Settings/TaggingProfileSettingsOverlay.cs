using System.Globalization;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Settings;

public static class TaggingProfileSettingsOverlay
{
    public static void ApplyProfileToSettings(
        DeezSpoTagSettings settings,
        TaggingProfile profile,
        string defaultTitleCasing = "nothing",
        string defaultArtistCasing = "nothing",
        string defaultArtworkFallbackOrder = "apple,deezer,spotify")
    {
        settings.Tags ??= new TagSettings();
        var technical = profile.Technical ?? new TechnicalTagSettings();
        var folder = profile.FolderStructure ?? new FolderStructureSettings();
        var tagConfig = profile.TagConfig ?? new UnifiedTagConfig();

        settings.Tags.SavePlaylistAsCompilation = technical.SavePlaylistAsCompilation;
        settings.Tags.UseNullSeparator = technical.UseNullSeparator;
        settings.Tags.SaveID3v1 = technical.SaveID3v1;
        settings.Tags.MultiArtistSeparator = technical.MultiArtistSeparator ?? "default";
        settings.Tags.SingleAlbumArtist = technical.SingleAlbumArtist;
        settings.Tags.CoverDescriptionUTF8 = technical.CoverDescriptionUTF8;
        settings.AlbumVariousArtists = technical.AlbumVariousArtists;
        settings.RemoveDuplicateArtists = technical.RemoveDuplicateArtists;
        settings.RemoveAlbumVersion = technical.RemoveAlbumVersion;
        settings.RemoveFeaturedFromAlbumTitle = technical.RemoveFeaturedFromAlbumTitle;
        settings.DateFormat = technical.DateFormat ?? "Y-M-D";
        settings.FeaturedToTitle = technical.FeaturedToTitle ?? "0";
        settings.TitleCasing = technical.TitleCasing ?? defaultTitleCasing;
        settings.ArtistCasing = technical.ArtistCasing ?? defaultArtistCasing;
        settings.SyncedLyrics = technical.SyncedLyrics;
        settings.SaveLyrics = technical.SaveLyrics;
        settings.LrcType = technical.LrcType ?? "lyrics,syllable-lyrics,ttml-lyrics,unsynced-lyrics";
        settings.LrcFormat = technical.LrcFormat ?? "richlyrics";
        settings.SynthesizeLrcFromTtml = technical.SynthesizeLrcFromTtml;
        settings.SynthesizeTtmlFromLrc = technical.SynthesizeTtmlFromLrc;
        settings.LrcTimingPreference = LrcTimingModes.Normalize(
            technical.LrcTimingPreference,
            technical.PreferEnhancedLrc);
        settings.PreferEnhancedLrc = LrcTimingModes.ImpliesEnhanced(settings.LrcTimingPreference);
        settings.LyricsFallbackEnabled = technical.LyricsFallbackEnabled;
        settings.LyricsFallbackOrder = technical.LyricsFallbackOrder ?? string.Join(",", LyricsProviderRegistry.DefaultOrder);
        settings.Lrclib = ResolveLrclibOptions(profile);
        settings.Musixmatch = ResolveMusixmatchOptions(profile);
        settings.BetterLyrics = ResolveBetterLyricsOptions(profile);
        settings.ArtworkFallbackEnabled = technical.ArtworkFallbackEnabled;
        settings.ArtworkFallbackOrder = technical.ArtworkFallbackOrder ?? defaultArtworkFallbackOrder;
        settings.ArtistArtworkFallbackEnabled = technical.ArtistArtworkFallbackEnabled;
        settings.ArtistArtworkFallbackOrder = technical.ArtistArtworkFallbackOrder ?? defaultArtworkFallbackOrder;
        settings.Tags.Lyrics = technical.EmbedLyrics
            && technical.SaveLyrics
            && UsesDownloadSource(tagConfig.UnsyncedLyrics);
        settings.Tags.SyncedLyrics = technical.EmbedLyrics
            && technical.SyncedLyrics
            && UsesDownloadSource(tagConfig.SyncedLyrics);

        settings.CreateArtistFolder = folder.CreateArtistFolder;
        settings.ArtistNameTemplate = folder.ArtistNameTemplate ?? "%artist%";
        settings.CreateAlbumFolder = folder.CreateAlbumFolder;
        settings.AlbumNameTemplate = folder.AlbumNameTemplate ?? "%album%";
        settings.CreateCDFolder = folder.CreateCDFolder;
        settings.CreateStructurePlaylist = folder.CreateStructurePlaylist;
        settings.CreateSingleFolder = folder.CreateSingleFolder;
        settings.CreatePlaylistFolder = folder.CreatePlaylistFolder;
        settings.PlaylistNameTemplate = folder.PlaylistNameTemplate ?? "%playlist%";
        settings.IllegalCharacterReplacer = folder.IllegalCharacterReplacer ?? "_";
    }

    private static bool UsesDownloadSource(TagSource source)
        => source is TagSource.DownloadSource or TagSource.Both;

    /// <summary>
    /// Reads the "lrclib" platform card out of the profile's untyped custom-options bag
    /// (<c>profile.autoTag.custom.lrclib</c>) — the same node the AutoTag runner loads through
    /// LoadConfig — so the download pipeline resolves the identical values. Missing, malformed or
    /// partial nodes fall back to the <see cref="LrclibOptions"/> defaults.
    /// </summary>
    private static LrclibOptions ResolveLrclibOptions(TaggingProfile profile)
    {
        var options = new LrclibOptions();
        if (!TryGetPlatformNode(profile, "lrclib", out var lrclib))
        {
            return options;
        }

        if (TryReadInt(lrclib, "duration_tolerance_seconds", out var tolerance))
        {
            options.DurationToleranceSeconds = Math.Clamp(tolerance, 0, 60);
        }

        if (TryReadBool(lrclib, "use_duration_hint", out var useDurationHint))
        {
            options.UseDurationHint = useDurationHint;
        }

        if (TryReadBool(lrclib, "search_fallback", out var searchFallback))
        {
            options.SearchFallback = searchFallback;
        }

        if (TryReadBool(lrclib, "prefer_synced", out var preferSynced))
        {
            options.PreferSynced = preferSynced;
        }

        return options;
    }

    /// <summary>
    /// Reads the "musixmatch" platform card (<c>profile.autoTag.custom.musixmatch</c>) so the
    /// download pipeline honours the same tunables the card exposes. Missing, malformed or partial
    /// nodes fall back to the <see cref="MusixmatchOptions"/> defaults, which preserve the constants
    /// that used to be hardcoded in LyricsService.
    /// </summary>
    private static MusixmatchOptions ResolveMusixmatchOptions(TaggingProfile profile)
    {
        var options = new MusixmatchOptions();
        if (!TryGetPlatformNode(profile, "musixmatch", out var musixmatch))
        {
            return options;
        }

        if (TryReadInt(musixmatch, "duration_tolerance_seconds", out var tolerance))
        {
            options.DurationToleranceSeconds = Math.Clamp(tolerance, 0, 60);
        }

        if (TryReadInt(musixmatch, "search_page_size", out var pageSize))
        {
            options.SearchPageSize = Math.Clamp(pageSize, 1, 100);
        }

        if (TryReadInt(musixmatch, "richsync_max_deviation_seconds", out var richsyncDeviation))
        {
            options.RichsyncMaxDeviationSeconds = Math.Clamp(richsyncDeviation, 0, 60);
        }

        if (TryReadInt(musixmatch, "subtitle_max_deviation_seconds", out var subtitleDeviation))
        {
            options.SubtitleMaxDeviationSeconds = Math.Clamp(subtitleDeviation, 0, 60);
        }

        return options;
    }

    /// <summary>
    /// Reads the "betterlyrics" platform card out of the profile's untyped custom-options bag
    /// (<c>profile.autoTag.custom.betterlyrics</c>) — the same node the AutoTag runner loads through
    /// LoadConfig — so the download pipeline resolves the identical values. Missing, malformed or
    /// partial nodes fall back to the <see cref="BetterLyricsOptions"/> defaults.
    /// </summary>
    private static BetterLyricsOptions ResolveBetterLyricsOptions(TaggingProfile profile)
    {
        var options = new BetterLyricsOptions();
        if (!TryGetPlatformNode(profile, "betterlyrics", out var betterLyrics))
        {
            return options;
        }

        if (TryReadInt(betterLyrics, "duration_tolerance_seconds", out var tolerance))
        {
            options.DurationToleranceSeconds = Math.Clamp(tolerance, 0, 120);
        }

        return options;
    }

    /// <summary>
    /// Locates <c>profile.autoTag.custom.&lt;platformId&gt;</c> as a JSON object, tolerating a
    /// missing custom bag or a missing/mistyped platform node.
    /// </summary>
    private static bool TryGetPlatformNode(TaggingProfile profile, string platformId, out JsonElement node)
    {
        node = default;
        return profile.AutoTag.Data.TryGetValue("custom", out var custom)
            && custom.ValueKind == JsonValueKind.Object
            && custom.TryGetProperty(platformId, out node)
            && node.ValueKind == JsonValueKind.Object;
    }

    private static bool TryReadInt(JsonElement node, string name, out int value)
    {
        value = 0;
        if (!node.TryGetProperty(name, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }

    private static bool TryReadBool(JsonElement node, string name, out bool value)
    {
        value = false;
        if (!node.TryGetProperty(name, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => Assign(true, out value),
            JsonValueKind.False => Assign(false, out value),
            JsonValueKind.String => bool.TryParse(element.GetString(), out value),
            _ => false
        };

        static bool Assign(bool source, out bool target)
        {
            target = source;
            return true;
        }
    }
}
