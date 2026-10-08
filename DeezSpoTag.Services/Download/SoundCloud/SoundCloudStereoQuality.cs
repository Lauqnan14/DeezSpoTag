using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     The advertised SoundCloud stereo tiers, ranked.
/// </summary>
/// <remarks>
///     SoundCloud publishes exactly three lossy tiers and never advertises lossless, Hi-Res, or Atmos. The
///     ladder therefore stops at <see cref="High"/> and has no Atmos rung, which is why this enum has three
///     tiers where <c>TidalStereoQualityTier</c> has six.
/// </remarks>
internal enum SoundCloudStereoQualityTier
{
    Unknown = 0,
    Low = 1,
    Standard = 2,
    High = 3
}

/// <summary>
///     SoundCloud's own quality vocabulary, normalized and ranked.
/// </summary>
/// <remarks>
///     <para>
///         SoundCloud labels its transcodings <c>lq</c>, <c>sq</c>, and <c>hq</c>. Those are used verbatim as
///         the engine's selectable codes, uppercased to match every other engine in the catalog, rather than
///         borrowing another engine's names for tiers that mean something different.
///     </para>
///     <para>
///         <c>hq</c> is a Go+ stream and is advertised only to an account entitled to it. A saved token on
///         its own is not enough: measured against a live authenticated account, tracks still resolved to
///         <c>sq</c>. That is a runtime fact, not a catalog fact, so the tier stays in the ladder and is simply
///         not offered at download time.
///     </para>
/// </remarks>
internal static class SoundCloudStereoQuality
{
    /// <summary>Lowest delivered bitrate the bottom (Low) plan step will accept.</summary>
    private const int MinLowBitrateKbps = 64;

    /// <summary>Lowest delivered bitrate the Standard plan step will accept.</summary>
    private const int MinStandardBitrateKbps = 128;

    /// <summary>
    ///     Lowest delivered bitrate the High plan step will accept.
    /// </summary>
    /// <remarks>
    ///     256kbps, not 320. SoundCloud's Go+ <c>hq</c> stream is a 256kbps MP3 - the figure the ported
    ///     reference documents for the authenticated tier. A 320kbps floor here would fail every legitimate
    ///     Go+ delivery at verification time and fall the item down the ladder for no reason.
    /// </remarks>
    private const int MinHighBitrateKbps = 256;

    /// <summary>SoundCloud's low tier, 64kbps.</summary>
    public const string Low = "LQ";

    /// <summary>SoundCloud's standard tier, 128kbps.</summary>
    public const string Standard = "SQ";

    /// <summary>SoundCloud's Go+ high tier, 256kbps, advertised only to an authenticated request.</summary>
    public const string High = "HQ";

    /// <summary>
    ///     Maps any known spelling of a SoundCloud quality code onto a tier.
    /// </summary>
    /// <remarks>
    ///     An empty code resolves to <see cref="SoundCloudStereoQualityTier.Standard"/> because SoundCloud's
    ///     unauthenticated default is the standard stream; that mirrors Tidal defaulting an unset code to CD
    ///     lossless, its own mid-ladder default.
    /// </remarks>
    public static SoundCloudStereoQualityTier Normalize(string? quality)
    {
        var normalized = (quality ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "" => SoundCloudStereoQualityTier.Standard,
            "LQ" or "LOW" or "LOW_64K" or "SOUNDCLOUD_LOW" => SoundCloudStereoQualityTier.Low,
            "SQ" or "STANDARD" or "STD" or "SQ_128K" or "SOUNDCLOUD_STANDARD" => SoundCloudStereoQualityTier.Standard,
            "HQ" or "HIGH" or "HQ_320K" or "SOUNDCLOUD_HIGH" => SoundCloudStereoQualityTier.High,
            _ => SoundCloudStereoQualityTier.Unknown
        };
    }

    /// <summary>
    ///     Maps a tier onto the code stored on the queue payload and the fallback plan.
    /// </summary>
    public static string ToFallbackQuality(SoundCloudStereoQualityTier tier)
        => tier switch
        {
            SoundCloudStereoQualityTier.Low => Low,
            SoundCloudStereoQualityTier.Standard => Standard,
            SoundCloudStereoQualityTier.High => High,
            _ => string.Empty
        };

    /// <summary>
    ///     Maps a tier onto the label SoundCloud advertises, which is what stream selection compares against.
    /// </summary>
    public static string ToAdvertisedQuality(SoundCloudStereoQualityTier tier)
        => tier switch
        {
            SoundCloudStereoQualityTier.Low => "lq",
            SoundCloudStereoQualityTier.Standard => "sq",
            SoundCloudStereoQualityTier.High => "hq",
            _ => string.Empty
        };

    /// <summary>
    ///     Ranks an advertised SoundCloud label.
    /// </summary>
    /// <remarks>
    ///     Shared with the transcoding picker so the ladder and the picker cannot disagree about which label
    ///     is better.
    /// </remarks>
    public static int RankAdvertisedQuality(string? advertisedQuality)
        => (advertisedQuality ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "hq" => 2,
            "sq" => 1,
            _ => 0
        };

    /// <summary>
    ///     Whether a delivered file satisfies a requested tier.
    /// </summary>
    /// <remarks>
    ///     Every tier carries a real floor. Without one, a 64kbps delivery would satisfy a High step and the
    ///     fallback ladder would stop early on worse audio than the step it was standing on.
    /// </remarks>
    public static bool Accepts(SoundCloudStereoQualityTier requested, ActualAudioQuality actual)
        => requested switch
        {
            SoundCloudStereoQualityTier.Low => actual.BitrateKbps >= MinLowBitrateKbps,
            SoundCloudStereoQualityTier.Standard => actual.BitrateKbps >= MinStandardBitrateKbps,
            SoundCloudStereoQualityTier.High => actual.BitrateKbps >= MinHighBitrateKbps,
            _ => true
        };

    /// <summary>
    ///     Renders a requested quality for the activity log and error messages.
    /// </summary>
    public static string FormatRequested(string? quality)
        => Normalize(quality) switch
        {
            SoundCloudStereoQualityTier.Low => "SoundCloud Low (64kbps)",
            SoundCloudStereoQualityTier.Standard => "SoundCloud Standard (128kbps)",
            SoundCloudStereoQualityTier.High => "SoundCloud HQ (256kbps)",
            _ => string.IsNullOrWhiteSpace(quality) ? "SoundCloud requested quality" : quality
        };
}