using Microsoft.Extensions.Logging;
using DeezSpoTag.Core.Enums;
using System;
using System.Collections.Generic;
using System.IO;

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
    public bool StrictEngineQuality { get; set; } = false;
    public bool LogErrors { get; set; } = true;
    public bool LogSearched { get; set; } = false;
    public string OverwriteFile { get; set; } = "n"; // OverwriteOption.DONT_OVERWRITE
    public bool AutoMaxBitrate { get; set; } = true;
    public bool CreateM3U8File { get; set; } = false;
    public string PlaylistFilenameTemplate { get; set; } = "playlist";
    public bool SyncedLyrics { get; set; } = true;
    public string QueueOrder { get; set; } = "fifo";

    // Lyrics preference + fallback
    public bool LyricsFallbackEnabled { get; set; } = true;
    public string LyricsFallbackOrder { get; set; } = "apple,deezer,spotify,lrclib";
    public bool NormalizeGenreTags { get; set; } = false;
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
    public int ShazamCaptureDurationSeconds { get; set; } = 11;
    public bool ShazamAllowHttpFileFallback { get; set; } = true;
    public bool ShazamRemoteMemoryOnly { get; set; } = true;
    
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
    public bool SaveArtworkArtist { get; set; } = true;
    public string ArtistImageTemplate { get; set; } = "folder";
    public int JpegImageQuality { get; set; } = 100;

    // EXACT PORT: Metadata processing settings from deezspotag DEFAULT_SETTINGS
    public string DateFormat { get; set; } = "Y-M-D";
    public bool AlbumVariousArtists { get; set; } = true;
    public bool RemoveAlbumVersion { get; set; } = false;
    public bool RemoveDuplicateArtists { get; set; } = true;
    public string FeaturedToTitle { get; set; } = "0"; // FeaturesOption.NO_CHANGE
    public string TitleCasing { get; set; } = "nothing";
    public string ArtistCasing { get; set; } = "nothing";

    // EXACT PORT: Additional settings for compatibility from deezspotag DEFAULT_SETTINGS
    public bool ClearQueueOnExit { get; set; } = false;
    public bool SaveDownloadQueue { get; set; } = false;
    public string TagsLanguage { get; set; } = "";
    public int PreviewVolume { get; set; } = 80;
    public string Arl { get; set; } = string.Empty;
    public bool EmbedMaxQualityCover { get; set; } = true;
    public string TidalQuality { get; set; } = "LOSSLESS";
    public string QobuzQuality { get; set; } = "6";

    // Apple-derived download options (engine-agnostic)
    public string AuthorizationToken { get; set; } = "";
    public string LrcType { get; set; } = "lyrics,syllable-lyrics,unsynced-lyrics";
    public string LrcFormat { get; set; } = "both";
    public bool SaveAnimatedArtwork { get; set; } = true;
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
    public string MetadataSource { get; set; } = "spotify";
    public bool Fallback { get; set; } = false;

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
    public bool StrictSpotifyDeezerMode { get; set; } = false;

    // UI preferences
    public bool RememberTabsPreference { get; set; } = true;
    public string DeezerLanguage { get; set; } = "en";
    public string DeezerCountry { get; set; } = "US";
    public string ApiToken { get; set; } = string.Empty;

    // Watchlist settings
    public bool WatchEnabled { get; set; } = false;
    public int WatchPollIntervalSeconds { get; set; } = 3600;
    public int WatchMaxItemsPerRun { get; set; } = 50;
    public int WatchDelayBetweenPlaylistsSeconds { get; set; } = 2;
    public int WatchDelayBetweenArtistsSeconds { get; set; } = 5;
    public bool WatchUseSnapshotIdChecking { get; set; } = true;
    public List<string> WatchedArtistAlbumGroup { get; set; } = new() { "album", "single" };

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

public sealed class GenreTagAliasRule
{
    public string Alias { get; set; } = string.Empty;
    public string Canonical { get; set; } = string.Empty;
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
