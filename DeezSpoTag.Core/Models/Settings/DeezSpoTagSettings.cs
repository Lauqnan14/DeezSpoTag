using Microsoft.Extensions.Logging;
using DeezSpoTag.Core.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using DeezSpoTag.Core.Utils;

namespace DeezSpoTag.Core.Models.Settings;

/// <summary>
/// PHASE 3: Complete DeezSpoTagSettings - Exact port from deezspotag settings.ts DEFAULT_SETTINGS
/// All settings match deezspotag TypeScript implementation exactly
/// </summary>
public class DeezSpoTagSettings
{
    private const string ContainerDownloadsPath = "/downloads";

    // EXACT PORT: Core settings from deezspotag DEFAULT_SETTINGS
    public string DownloadLocation { get; set; } = GetDefaultDownloadLocation();
    public string ReviewFolderPath { get; set; } = "";
    public string ExecuteCommand { get; set; } = "";
    public TagSettings Tags { get; set; } = new TagSettings();

    // EXACT PORT: Template settings from deezspotag DEFAULT_SETTINGS
    public string TracknameTemplate { get; set; } = "%artist% - %title%";
    public string AlbumTracknameTemplate { get; set; } = "%tracknumber% - %title%";
    public string PlaylistTracknameTemplate { get; set; } = "%artist% - %title%";

    // EXACT PORT: Folder structure settings from deezspotag DEFAULT_SETTINGS
    public bool CreatePlaylistFolder { get; set; } = true;
    public string PlaylistNameTemplate { get; set; } = "%playlist%";
    public bool CreateArtistFolder { get; set; } = true;
    public string ArtistNameTemplate { get; set; } = "%artist%";
    public bool CreateAlbumFolder { get; set; } = true;
    public string AlbumNameTemplate { get; set; } = "%album%";
    public bool CreateCDFolder { get; set; } = true;
    public bool CreateStructurePlaylist { get; set; } = false;
    public bool CreateSingleFolder { get; set; } = false;

    // EXACT PORT: Track naming settings from deezspotag DEFAULT_SETTINGS
    public bool PadTracks { get; set; } = true;
    public bool PadSingleDigit { get; set; } = true;
    public int PaddingSize { get; set; } = 0;
    public string IllegalCharacterReplacer { get; set; } = "_";

    // EXACT PORT: Download settings from deezspotag DEFAULT_SETTINGS
    public int MaxBitrate { get; set; } = 1; // TrackFormats.MP3_128 = 1
    public bool FeelingLucky { get; set; } = false;
    public bool FallbackBitrate { get; set; } = true;
    public bool FallbackSearch { get; set; } = false;
    public bool FallbackISRC { get; set; } = false;
    public bool LogErrors { get; set; } = true;
    public bool LogSearched { get; set; } = false;
    public string OverwriteFile { get; set; } = "n"; // OverwriteOption.DONT_OVERWRITE
    public bool AutoMaxBitrate { get; set; } = true;
    public bool CreateM3U8File { get; set; } = false;
    public string PlaylistFilenameTemplate { get; set; } = "playlist";
    public bool SyncedLyrics { get; set; } = true;
    public string QueueOrder { get; set; } = "fifo";
    public int QueuePreResolutionRetryMinutes { get; set; } = 2;
    public DownloadEngineOrderSettings DownloadEngineOrder { get; set; } = DownloadEngineOrderSettings.CreateDefault();

    // Lyrics preference + fallback
    public bool SynthesizeLrcFromTtml { get; set; } = false;
    public bool SynthesizeTtmlFromLrc { get; set; } = true;
    public bool PreferEnhancedLrc { get; set; } = true;
    public string LrcTimingPreference { get; set; } = LrcTimingModes.PreferEnhanced;
    public bool LyricsFallbackEnabled { get; set; } = true;
    public string LyricsFallbackOrder { get; set; } = "apple,deezer,spotify,lrclib,musixmatch,youlyplus,betterlyrics";

    /// <summary>LRCLIB lookup tunables. Kept in step with the profile's "lrclib" platform card so
    /// the AutoTag runner and the download pipeline resolve identical values.</summary>
    public LrclibOptions Lrclib { get; set; } = new();

    /// <summary>Musixmatch lookup tunables, kept in step with the profile's "musixmatch" platform card.</summary>
    public MusixmatchOptions Musixmatch { get; set; } = new();

    /// <summary>BetterLyrics lookup tunables, kept in step with the profile's "betterlyrics" platform card.</summary>
    public BetterLyricsOptions BetterLyrics { get; set; } = new();
    public int LyricsProviderRegistryVersion { get; set; }
    public int LyricsFormatSchemaVersion { get; set; }

    /// <summary>
    /// The current genre-normalization preferences, supplied by Genre Intelligence
    /// when settings are loaded.
    ///
    /// This is a carrier, not a second source of truth. It exists so the models
    /// and helpers in this assembly can apply the user's preferences without
    /// referencing the store. Nothing persists it; assigning it here is what makes
    /// a settings load authoritative.
    /// </summary>
    [JsonIgnore]
    public GenreNormalizationOptions GenreNormalization { get; set; } = GenreNormalizationOptions.Default;
    // ─────────────────────────────────────────────────────────────────────────
    // DEPRECATED — MIGRATION ONLY. Genre Intelligence owns these now.
    //
    // They are still deserialized so an existing configuration file keeps
    // loading, and they are imported exactly once into Genre Intelligence. After
    // that no runtime code reads them, nothing writes them, and the Settings page
    // does not expose them. They exist only so a user's existing preferences can
    // be carried across, and can be deleted in a later schema cleanup.
    // ─────────────────────────────────────────────────────────────────────────
    [Obsolete("Owned by Genre Intelligence. Read PersonalGenreSettings.NormalizeGenreTags instead.")]
    public bool NormalizeGenreTags { get; set; } = false;

    [Obsolete("Owned by Genre Intelligence. Read PersonalGenreSettings.GenreTagBlockList instead.")]
    public List<string> GenreTagBlockList { get; set; } = new() { "other", "others", "Worldwide" };

    [Obsolete("Owned by Genre Intelligence. Read PersonalGenreSettings.GenreTagAliasRules instead.")]
    public List<GenreTagAliasRule> GenreTagAliasRules { get; set; } = new()
    {
        new GenreTagAliasRule
        {
            Alias = "Afro-Pop",
            Canonical = "Afropop"
        },
        new GenreTagAliasRule
        {
            Alias = "Afro Pop",
            Canonical = "Afropop"
        },
        new GenreTagAliasRule
        {
            Alias = "Hip-Hop",
            Canonical = "HipHop"
        },
        new GenreTagAliasRule
        {
            Alias = "Hip Hop",
            Canonical = "HipHop"
        }
    };

    // Artwork preference + fallback
    public bool ArtworkFallbackEnabled { get; set; } = true;
    public string ArtworkFallbackOrder { get; set; } = "apple,deezer,spotify";
    public bool ArtistArtworkFallbackEnabled { get; set; } = true;
    public string ArtistArtworkFallbackOrder { get; set; } = "apple,deezer,spotify";

    // Shazam UI/capture settings
    public bool ShazamEnabled { get; set; } = true;
    public bool ShazamUseCenteredOverlay { get; set; } = true;
    // Must be >= the 12s signature window the recognizer needs to match, plus room for the
    // early attempt to answer before the capture ends. Normalized on load, so a persisted
    // value below the floor is lifted rather than kept.
    public int ShazamCaptureDurationSeconds { get; set; } = 16;
    public bool ShazamAllowHttpFileFallback { get; set; } = true;
    public bool ShazamRemoteMemoryOnly { get; set; } = true;
    public int ShazamCaptureSettingsVersion { get; set; }

    // Spotizerr-phoenix download behavior and retries
    public int MaxConcurrentDownloads { get; set; } = 3;
    public bool RealTime { get; set; } = false;
    public int RealTimeMultiplier { get; set; } = 1;
    public bool RecursiveQuality { get; set; } = false;
    public bool SeparateTracksByUser { get; set; } = false;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 3;
    public int RetryDelayIncrease { get; set; } = 5;
    public int RedownloadCooldownMinutes { get; set; } = 720;
    public int AutoTagHistoryRetentionDays { get; set; } = 7;

    // Spotizerr-phoenix conversion settings
    public string ConvertTo { get; set; } = "";
    public string Bitrate { get; set; } = "AUTO";

    // EXACT PORT: Artwork settings from deezspotag DEFAULT_SETTINGS
    public int EmbeddedArtworkSize { get; set; } = 1200;
    public int LocalArtworkSize { get; set; } = 1200;
    public int AppleArtworkSize { get; set; } = 1200;
    public string AppleArtworkSizeText { get; set; } = "5000x5000";
    public string LocalArtworkFormat { get; set; } = "jpg";
    public bool SaveArtwork { get; set; } = true;
    public string CoverImageTemplate { get; set; } = "cover";
    public string AnimatedArtworkSquareFileName { get; set; } = "cover";
    public string AnimatedArtworkTallFileName { get; set; } = "cover_tall";
    public bool SaveArtworkArtist { get; set; } = true;
    public string ArtistImageTemplate { get; set; } = "folder";
    public int JpegImageQuality { get; set; } = 100;

    // EXACT PORT: Metadata processing settings from deezspotag DEFAULT_SETTINGS
    public string DateFormat { get; set; } = "Y-M-D";
    public bool AlbumVariousArtists { get; set; } = true;
    public bool RemoveAlbumVersion { get; set; } = false;

    /// <summary>
    /// Removes featured artists from the album title ("Rise Up (feat. Falz)" becomes "Rise Up").
    /// Applies to the album tag and, because the default album folder template is "%album%", to
    /// the album folder name as well. Independent of FeaturedToTitle, so it can be combined with
    /// any of the title options.
    /// </summary>
    public bool RemoveFeaturedFromAlbumTitle { get; set; } = false;
    public bool RemoveDuplicateArtists { get; set; } = true;
    public string FeaturedToTitle { get; set; } = "0"; // FeaturesOption.NO_CHANGE
    public string TitleCasing { get; set; } = "nothing";
    public string ArtistCasing { get; set; } = "nothing";

    // EXACT PORT: Additional settings for compatibility from deezspotag DEFAULT_SETTINGS
    public bool ClearQueueOnExit { get; set; } = false;
    public bool SaveDownloadQueue { get; set; } = false;
    public string TagsLanguage { get; set; } = "";
    public int PreviewVolume { get; set; } = 80;
    public bool EmbedMaxQualityCover { get; set; } = true;
    public string TidalQuality { get; set; } = "LOSSLESS";
    public string QobuzQuality { get; set; } = "6";

    // Apple-derived download options (engine-agnostic)
    public string AuthorizationToken { get; set; } = "";
    public string LrcType { get; set; } = "lyrics,syllable-lyrics,ttml-lyrics,unsynced-lyrics";
    public string LrcFormat { get; set; } = "both";
    // Legacy master switch, kept in sync by the UI as square OR tall.
    public bool SaveAnimatedArtwork { get; set; } = true;
    public bool SaveSquareAnimatedArtwork { get; set; } = true;
    public bool SaveTallAnimatedArtwork { get; set; } = true;
    public string AnimatedArtworkFormats { get; set; } = "mp4";
    public int AnimatedArtworkMaxSizeMb { get; set; } = 10;
    public int LimitMax { get; set; } = 200;
    public bool DlAlbumcoverForPlaylist { get; set; } = true;
    public bool GetM3u8FromDevice { get; set; } = true;

    // Conversion options (engine-agnostic)
    public bool ConvertAfterDownload { get; set; } = true;
    public string ConvertFormat { get; set; } = "";
    public bool ConvertKeepOriginal { get; set; } = true;
    public bool ConvertSkipIfSourceMatches { get; set; } = true;
    public string ConvertExtraArgs { get; set; } = "";
    public bool ConvertWarnLossyToLossless { get; set; } = false;
    public bool ConvertSkipLossyToLossless { get; set; } = false;

    // Music video settings (engine-agnostic)
    public string MvFileFormat { get; set; } = "{ArtistName} - {VideoName} [{ReleaseYear}]";

    // Engine settings (spotizerr-phoenix compatibility)
    public string Service { get; set; } = "auto";
    public string DownloadSourceContentType { get; set; } = "stereo";
    public bool Fallback { get; set; } = false;

    // Soulseek download behaviour. Connection details (slskd URL and API key) are NOT here on purpose:
    // they live in the encrypted platform auth state configured from the login page.
    public SoulseekDownloadSettings Soulseek { get; set; } = new SoulseekDownloadSettings();

    // Public download API (qobuz/tidal/amazon) session verification record.
    //
    // This exists so verification-driven retry survives a process restart. Without it, a queue item
    // stamped "was queued while a public API was unverified" would have no way to tell, after a
    // reboot, whether the session it was waiting on has since been verified.
    //
    // Semantics: a slug absent from the map (or mapped to null) means NEVER verified, which is
    // deliberately treated the same as unverified. Downloading through a public API that has not
    // been verified cannot succeed, so an absent entry must not be read as "probably fine".
    // A value is written only when a session verification actually completes.
    public Dictionary<string, string?> PublicApiSessionVerifiedAt { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set once verification-driven retry has run its initial unfiltered sweep, which releases every
    /// failed queue item. After that, only items stamped while a public API was unverified are
    /// released. Persisted so a reboot does not repeat the initial sweep.
    /// </summary>
    public bool VerificationRetryInitialSweepCompleted { get; set; } = false;

    // Spotizerr-phoenix compatibility (currently used by Settings UI)
    public int LibrespotConcurrency { get; set; } = 2;

    // Spotify playlist matching
    public int SpotifyResolveConcurrency { get; set; } = 10;
    public int SpotifyMatchConcurrency { get; set; } = 10;
    public int SpotifyIsrcHydrationConcurrency { get; set; } = 8;
    public string SpotifyPlaylistTrackSource { get; set; } = "pathfinder";
    public bool SpotifyHomeFeedCacheEnabled { get; set; } = true;
    public bool SpotifyHomeFeedAutoRefreshEnabled { get; set; } = true;
    public int SpotifyHomeFeedAutoRefreshHours { get; set; } = 2;
    public int SpotifyBrowseCacheMinutes { get; set; } = 30;
    public int SpotifyArtistMetadataFetchBatchSize { get; set; } = 25;
    public bool StrictSpotifyDeezerMode { get; set; } = true;

    // UI preferences
    public bool RememberTabsPreference { get; set; } = true;
    public bool TrackAvailabilityColumnEnabled { get; set; } = false;
    public string DeezerLanguage { get; set; } = "en";
    public string DeezerCountry { get; set; } = "US";
    public string ApiToken { get; set; } = string.Empty;

    // Watchlist settings
    public bool WatchEnabled { get; set; } = false;
    public int WatchPollIntervalSeconds { get; set; } = 3600;
    public int WatchMaxItemsPerRun { get; set; } = 50;
    public int WatchMaxReleasesPerArtist { get; set; } = 50;
    public int WatchMaxTracksPerPlaylistCheck { get; set; } = 50;
    public int WatchDelayBetweenPlaylistsSeconds { get; set; } = 2;
    public int WatchDelayBetweenArtistsSeconds { get; set; } = 5;
    public bool WatchUseSnapshotIdChecking { get; set; } = true;
    public bool WatchArtistTopSongsEnabled { get; set; } = false;
    public bool WatchArtistLatestReleasesOnly { get; set; } = false;
    public List<string> WatchedArtistAlbumGroup { get; set; } = new() { "album", "single" };

    /// <summary>
    /// Maximum number of tracks DeezSpoTag will collect into a single playlist snapshot. This bounds
    /// snapshot work predictably while still letting very large playlists take part in
    /// reconciliation, and is deliberately independent of any provider's HTTP page size: providers
    /// keep requesting their own valid page size and the engine simply stops paging once this many
    /// candidates are held. A playlist larger than this is snapshotted partially and reconciled as
    /// incomplete, never as a source failure, and tracks beyond the fetched window are left
    /// untouched rather than treated as removed.
    /// </summary>
    public const int DefaultPlaylistSnapshotTrackLimit = 3000;

    /// <summary>Applies to every supported streaming platform. One shared value, not per provider.</summary>
    public int PlaylistSnapshotTrackLimit { get; set; } = DefaultPlaylistSnapshotTrackLimit;

    // Download layout preferences
    public bool PreferAlbumLayoutForPlaylists { get; set; } = true;

    // Artist page merge settings
    public double SpotifyHeroDiscographyMatchThreshold { get; set; } = 0.6;
    public List<string> SpotifyHeroOverrideArtistNames { get; set; } = new();

    // Apple Music integration (new)
    public AppleMusicSettings AppleMusic { get; set; } = new();

    // Video settings (new, cross-engine)
    public VideoSettings Video { get; set; } = new();

    // Podcast/episode download destination
    public PodcastSettings Podcast { get; set; } = new();

    // Multi-quality downloads (e.g., Atmos + stereo)
    public MultiQualityDownloadSettings MultiQuality { get; set; } = new();

    // Legacy property for compatibility
    public bool SaveLyrics { get; set; } = false;

    private static string GetDefaultDownloadLocation()
    {
        if (IsRunningInContainer())
        {
            return ContainerDownloadsPath;
        }

        var musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrWhiteSpace(musicFolder))
        {
            return musicFolder;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? Path.Join(Environment.CurrentDirectory, "Music")
            : Path.Join(userProfile, "Music");
    }

    private static bool IsRunningInContainer()
    {
        var runningInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        return string.Equals(runningInContainer, "true", StringComparison.OrdinalIgnoreCase);
    }
}

public class DownloadEngineOrderSettings
{
    public bool Enabled { get; set; } = false;
    public List<DownloadEngineOrderItem> Engines { get; set; } = new();

    public static DownloadEngineOrderSettings CreateDefault()
    {
        return new DownloadEngineOrderSettings
        {
            Enabled = false,
            Engines = new List<DownloadEngineOrderItem>
            {
                CreateEngine("qobuz", "27", "7", "6", "5"),
                CreateEngine("tidal", "HI_RES_LOSSLESS", "HI_RES", "LOSSLESS", "HIGH", "LOW", "DOLBY_ATMOS"),
                CreateEngine("apple", "ALAC", "AAC", "ATMOS"),
                CreateEngine("amazon", "ULTRA_HD_FLAC", "HD_FLAC", "OPUS", "DOLBY_ATMOS"),
                CreateEngine("deezer", "9", "3", "1"),
                // Soulseek is opt-in and is deliberately absent from the canonical auto quality order in
                // DownloadSourceOrder, so it is added last here and only takes part in Custom selection.
                CreateEngine("soulseek", "FLAC_HI_RES_LOSSLESS", "FLAC_HI_RES", "FLAC", "LOSSLESS", "MP3_320", "MP3_256", "MP3_192", "MP3_128", "UNKNOWN"),
                // SoundCloud publishes only lossy MP3 tiers, so it has no lossless or Atmos entry. Its three
                // tiers sit at the ladder positions matching their bitrates.
                CreateEngine("soundcloud", "HQ", "SQ", "LQ")
            }
        };
    }

    private static DownloadEngineOrderItem CreateEngine(string engine, params string[] qualities)
    {
        return new DownloadEngineOrderItem
        {
            Engine = engine,
            Enabled = true,
            Qualities = qualities
                .Select(quality => new DownloadEngineQualityItem { Quality = quality, Enabled = true })
                .ToList()
        };
    }
}

public class DownloadEngineOrderItem
{
    public string Engine { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<DownloadEngineQualityItem> Qualities { get; set; } = new();
}

public class DownloadEngineQualityItem
{
    public string Quality { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class GenreTagAliasRule
{
    public string Alias { get; set; } = string.Empty;
    public string Canonical { get; set; } = string.Empty;
}

/// <summary>
/// The genre-normalization preferences, in the form every tag-writing path can
/// use.
///
/// Genre Intelligence owns these values. This record lives here because
/// <see cref="Track"/> and the download tagger live in this assembly and cannot
/// reference the store. It is a carrier, not a second source of truth: the values
/// are copied from Genre Intelligence when settings are applied.
/// </summary>
public sealed record GenreNormalizationOptions
{
    public static GenreNormalizationOptions Default { get; } = new();

    /// <summary>Whether the user's spelling preferences are applied.</summary>
    public bool Enabled { get; init; }

    /// <summary>Alias lookup, or empty when normalization is off.</summary>
    public IReadOnlyDictionary<string, string> AliasMap { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<string> BlockList { get; init; } = GenreTagAliasNormalizer.DefaultBlockedGenres;
}

/// <summary>
/// PHASE 3: Complete DeezSpoTagTagsSettings - Exact port from deezspotag settings.ts DEFAULT_SETTINGS.tags
/// All tag settings match deezspotag TypeScript implementation exactly
/// </summary>
public class DeezSpoTagTagsSettings
{
    // EXACT PORT: Basic tag settings from deezspotag DEFAULT_SETTINGS.tags
    public bool Title { get; set; } = true;
    public bool Artist { get; set; } = true;
    public bool Artists { get; set; } = true;
    public bool Album { get; set; } = true;
    public bool Cover { get; set; } = true;
    public bool TrackNumber { get; set; } = true;
    public bool TrackTotal { get; set; } = false;
    public bool DiscNumber { get; set; } = true;
    public bool DiscTotal { get; set; } = false;
    public bool AlbumArtist { get; set; } = true;
    public bool Genre { get; set; } = true;
    public bool Year { get; set; } = true;
    public bool Date { get; set; } = true;
    public bool Explicit { get; set; } = false;

    // EXACT PORT: Advanced tag settings from deezspotag DEFAULT_SETTINGS.tags
    public bool Isrc { get; set; } = true;
    public bool Length { get; set; } = true;
    public bool Barcode { get; set; } = true;
    public bool Bpm { get; set; } = true;
    public bool ReplayGain { get; set; } = false;
    public bool Label { get; set; } = true;
    public bool Lyrics { get; set; } = false;
    public bool SyncedLyrics { get; set; } = false;
    public bool Copyright { get; set; } = false;
    public bool Composer { get; set; } = false;
    public bool InvolvedPeople { get; set; } = false;
    public bool Source { get; set; } = false;
    public bool Rating { get; set; } = false;

    // EXACT PORT: Special tag settings from deezspotag DEFAULT_SETTINGS.tags
    public bool SavePlaylistAsCompilation { get; set; } = false;
    public bool UseNullSeparator { get; set; } = false;
    public bool SaveID3v1 { get; set; } = true;
    public string MultiArtistSeparator { get; set; } = "default";
    public bool SingleAlbumArtist { get; set; } = true;
    public bool CoverDescriptionUTF8 { get; set; } = false;
}
