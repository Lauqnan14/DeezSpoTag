namespace DeezSpoTag.Core.Enums;

/// <summary>
/// Track format enumeration (ported from deezspotag TrackFormats)
/// </summary>
public enum TrackFormat
{
    /// <summary>
    /// Default format
    /// </summary>
    DEFAULT = 0,

    /// <summary>
    /// Local file
    /// </summary>
    LOCAL = -1,

    /// <summary>
    /// MP3 128 kbps
    /// </summary>
    MP3_128 = 1,

    /// <summary>
    /// MP3 320 kbps
    /// </summary>
    MP3_320 = 3,

    /// <summary>
    /// FLAC lossless
    /// </summary>
    FLAC = 9,

    /// <summary>
    /// MP4 360 Reality Audio Low Quality
    /// </summary>
    MP4_RA1 = 13,

    /// <summary>
    /// MP4 360 Reality Audio Medium Quality
    /// </summary>
    MP4_RA2 = 14,

    /// <summary>
    /// MP4 360 Reality Audio High Quality
    /// </summary>
    MP4_RA3 = 15
}

/// <summary>
/// Track format constants (ported from deezer-sdk TrackFormats)
/// Provides backward compatibility with integer constants
/// </summary>
public static class TrackFormats
{
    public const int FLAC = 9;
    public const int LOCAL = 0;
    public const int MP3_320 = 3;
    public const int MP3_128 = 1;
    public const int DEFAULT = 8;
    public const int MP4_RA3 = 15;
    public const int MP4_RA2 = 14;
    public const int MP4_RA1 = 13;

    /// <summary>
    /// Convert integer format to enum
    /// </summary>
    public static TrackFormat ToEnum(int format)
    {
        return format switch
        {
            FLAC => TrackFormat.FLAC,
            LOCAL => TrackFormat.LOCAL,
            MP3_320 => TrackFormat.MP3_320,
            MP3_128 => TrackFormat.MP3_128,
            DEFAULT => TrackFormat.DEFAULT,
            MP4_RA3 => TrackFormat.MP4_RA3,
            MP4_RA2 => TrackFormat.MP4_RA2,
            MP4_RA1 => TrackFormat.MP4_RA1,
            _ => TrackFormat.DEFAULT
        };
    }

    /// <summary>
    /// Convert enum format to integer
    /// </summary>
    public static int ToInt(TrackFormat format)
    {
        return (int)format;
    }
}