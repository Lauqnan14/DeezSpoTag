namespace DeezSpoTag.Core.Models.Settings;

public sealed class AppleMusicSettings
{
    /// <summary>
    /// Cookie value from https://music.apple.com named "media-user-token".
    /// Required for AAC LC + Apple lyrics in the referenced tooling.
    /// </summary>
    public string MediaUserToken { get; set; } = string.Empty;

    /// <summary>
    /// Optional bearer token for Apple API calls when scraping fails.
    /// </summary>
    public string AuthorizationToken { get; set; } = string.Empty;

    public string Storefront { get; set; } = "us";

    /// <summary>
    /// When true, treat Apple artwork as the preferred cover source.
    /// UI control lives under the Artwork section to align with existing album cover settings.
    /// </summary>
    public bool PreferAppleCovers { get; set; } = true;

    /// <summary>
    /// Desired Apple audio profile. The download runner maps this to concrete
    /// formats and applies fallback when DeezSpoTagSettings.FallbackBitrate is enabled.
    /// </summary>
    public string PreferredAudioProfile { get; set; } = "atmos";

    /// <summary>
    /// Ported from reference GUI "Audio Quality" settings.
    /// </summary>
    public string GetM3u8Mode { get; set; } = "hires";

    /// <summary>
    /// Ported from reference GUI "Audio Quality" settings.
    /// Examples: aac-lc, aac, aac-binaural, aac-downmix.
    /// </summary>
    public string AacType { get; set; } = "aac-lc";

    /// <summary>
    /// Wrapper port used for decrypting M3U8 streams when device mode is enabled.
    /// </summary>
    public string DecryptM3u8Port { get; set; } = "127.0.0.1:10020";

    /// <summary>
    /// Wrapper port used for fetching M3U8 streams when device mode is enabled.
    /// </summary>
    public string GetM3u8Port { get; set; } = "127.0.0.1:20020";

    /// <summary>
    /// When true, attempt to fetch M3U8 streams via the wrapper device path.
    /// </summary>
    public bool GetM3u8FromDevice { get; set; } = true;

    /// <summary>
    /// Ported from reference GUI "Audio Quality" settings. Maximum sample rate for ALAC downloads.
    /// </summary>
    public int AlacMax { get; set; } = 192000;

    /// <summary>
    /// Ported from reference GUI "Audio Quality" settings. Maximum bitrate for Atmos downloads.
    /// </summary>
    public int AtmosMax { get; set; } = 2768;
}

public sealed class VideoSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base folder for video downloads. Must be explicitly configured.
    /// </summary>
    public string VideoDownloadLocation { get; set; } = string.Empty;

    public string Container { get; set; } = "mp4";
    public string AppleMusicVideoAudioType { get; set; } = "atmos"; // atmos | ac3 | aac
    public int AppleMusicVideoMaxResolution { get; set; } = 2160;
    public string AppleMusicVideoCodecPreference { get; set; } = "prefer-hevc"; // prefer-hevc | prefer-avc | auto

    public string ArtistFolderTemplate { get; set; } = "%artist%";
    public string TitleTemplate { get; set; } = "%title%";
}

public sealed class PodcastSettings
{
    /// <summary>
    /// Base folder for podcast/episode downloads. When blank, callers should use the
    /// selected library folder destination.
    /// </summary>
    public string DownloadLocation { get; set; } = string.Empty;
}
