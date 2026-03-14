using DeezSpoTag.Integrations.Deezer;

namespace DeezSpoTag.Services.Download.Shared.Models;

/// <summary>
/// Exception thrown when a download fails with specific error information
/// Ported from: deezspotag/src/errors.ts - DownloadFailed class
/// </summary>
public class DownloadFailedException : Exception
{
    public string ErrorId { get; set; }
    public Track? Track { get; set; }

    public DownloadFailedException(string errorId, Track? track = null) 
        : base(GetErrorMessage(errorId))
    {
        ErrorId = errorId;
        Track = track;
    }

    public DownloadFailedException(string message, string errorId, Track? track = null) 
        : base(message)
    {
        ErrorId = errorId;
        Track = track;
    }

    private static string GetErrorMessage(string errorId)
    {
        return errorId switch
        {
            "notOnDeezer" => "Track not available on Deezer!",
            "notEncoded" => "Track not yet encoded!",
            "notEncodedNoAlternative" => "Track not yet encoded and no alternative found!",
            "wrongBitrate" => "Track not found at desired bitrate.",
            "wrongBitrateNoAlternative" => "Track not found at desired bitrate and no alternative found!",
            "wrongLicense" => "Your account can't stream the track at the desired bitrate.",
            "no360RA" => "Track is not available in Reality Audio 360.",
            "notAvailable" => "Track not available on deezer's servers!",
            "notAvailableNoAlternative" => "Track not available on deezer's servers and no alternative found!",
            "albumDoesntExists" => "Track's album does not exist, failed to gather info.",
            "notLoggedIn" => "You need to login to download tracks.",
            "wrongGeolocation" => "Your account can't stream the track from your current country.",
            "wrongGeolocationNoAlternative" => "Your account can't stream the track from your current country and no alternative found.",
            "mapping_miss" => "Spotify to Deezer mapping did not resolve to a valid Deezer track.",
            "mapped_but_quality_unavailable" => "Track is mapped on Deezer, but requested quality is unavailable.",
            "mapped_but_quality_unavailable_360" => "Track is mapped on Deezer, but 360 Reality Audio is unavailable.",
            "mapped_but_geo_blocked" => "Track is mapped on Deezer, but blocked in your region.",
            "mapped_but_license_blocked" => "Track is mapped on Deezer, but account license cannot stream requested quality.",
            "transient_token_failure" => "Track is mapped on Deezer, but token validation failed. Retry may succeed.",
            "transient_network_failure" => "Track is mapped on Deezer, but availability checks failed due to transient network/auth issues.",
            _ => "Download failed"
        };
    }
}

/// <summary>
/// Exception thrown when a download is cancelled
/// Ported from: deezspotag/src/errors.ts - DownloadCanceled class
/// </summary>
public class DownloadCanceledException : Exception
{
    public DownloadCanceledException() : base("Download was cancelled") { }
    public DownloadCanceledException(string message) : base(message) { }
}
