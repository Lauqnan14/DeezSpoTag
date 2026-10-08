using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     Builds the SoundCloud download request from a queue item.
/// </summary>
public static class SoundCloudRequestBuilder
{
    /// <summary>The quality assumed when neither the item nor the settings name one.</summary>
    private const string DefaultQuality = SoundCloudStereoQuality.Standard;

    public static SoundCloudDownloadRequest BuildRequest(SoundCloudQueueItem item, DeezSpoTagSettings settings)
    {
        var request = RequestBuilderCommon.CreateCommonRequest<SoundCloudDownloadRequest>(item, settings);
        var preferredQuality = RequestBuilderCommon.ResolvePreferredQuality(item.Quality, string.Empty, DefaultQuality);
        var preferredTier = SoundCloudStereoQuality.Normalize(preferredQuality);
        request.Quality = preferredTier == SoundCloudStereoQualityTier.Unknown
            ? preferredQuality
            : SoundCloudStereoQuality.ToFallbackQuality(preferredTier);

        // A SoundCloud permalink cannot be rebuilt from the numeric id the way a Tidal listen URL can, so the
        // exact URL is the only usable resolution and has to survive on the request.
        if (request.ServiceUrl.StartsWith("soundcloud:", StringComparison.OrdinalIgnoreCase))
        {
            request.ServiceUrl = string.Empty;
        }

        return request;
    }
}

/// <summary>
///     The resolved work for one SoundCloud download.
/// </summary>
public sealed class SoundCloudDownloadRequest : EngineDownloadRequestBase
{
    /// <summary>Gets or sets the SoundCloud quality code to satisfy.</summary>
    public string Quality { get; set; } = "";

    /// <summary>Gets or sets the SoundCloud track id, when the item or a fallback step carried one.</summary>
    public string SoundCloudId { get; set; } = "";

    /// <summary>Gets or sets the exact SoundCloud permalink, when the item carried one.</summary>
    public string SoundCloudTrackUrl { get; set; } = "";
}