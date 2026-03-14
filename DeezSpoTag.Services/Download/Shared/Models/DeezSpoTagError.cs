using System.Text.Json.Serialization;

namespace DeezSpoTag.Services.Download.Shared.Models;

/// <summary>
/// DeezSpoTag error types and messages (ported from deezspotag errors.ts)
/// </summary>
public static class DeezSpoTagErrorMessages
{
    public static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>
    {
        ["notOnDeezer"] = "Track not available on Deezer!",
        ["notEncoded"] = "Track not yet encoded!",
        ["notEncodedNoAlternative"] = "Track not yet encoded and no alternative found!",
        ["wrongBitrate"] = "Track not found at desired bitrate.",
        ["wrongBitrateNoAlternative"] = "Track not found at desired bitrate and no alternative found!",
        ["wrongLicense"] = "Your account can't stream the track at the desired bitrate.",
        ["no360RA"] = "Track is not available in Reality Audio 360.",
        ["notAvailable"] = "Track not available on deezer's servers!",
        ["notAvailableNoAlternative"] = "Track not available on deezer's servers and no alternative found!",
        ["noSpaceLeft"] = "No space left on target drive, clean up some space for the tracks.",
        ["albumDoesntExists"] = "Track's album does not exist, failed to gather info.",
        ["notLoggedIn"] = "You need to login to download tracks.",
        ["wrongGeolocation"] = "Your account can't stream the track from your current country.",
        ["wrongGeolocationNoAlternative"] = "Your account can't stream the track from your current country and no alternative found.",
        ["mapping_miss"] = "Spotify to Deezer mapping did not resolve to a valid Deezer track.",
        ["mapped_but_quality_unavailable"] = "Track is mapped on Deezer, but requested quality is unavailable.",
        ["mapped_but_quality_unavailable_360"] = "Track is mapped on Deezer, but 360 Reality Audio is unavailable.",
        ["mapped_but_geo_blocked"] = "Track is mapped on Deezer, but blocked in your region.",
        ["mapped_but_license_blocked"] = "Track is mapped on Deezer, but account license cannot stream requested quality.",
        ["transient_token_failure"] = "Track is mapped on Deezer, but token validation failed. Retry may succeed.",
        ["transient_network_failure"] = "Track is mapped on Deezer, but availability checks failed due to transient network/auth issues.",
        ["trackNotOnDeezer"] = "Track not found on Deezer!",
        ["albumNotOnDeezer"] = "Album not found on Deezer!",
        ["invalidURL"] = "URL not recognized",
        ["unsupportedURL"] = "URL not supported yet",
        ["ISRCnotOnDeezer"] = "Track ISRC is not available on Deezer",
        ["notYourPrivatePlaylist"] = "You can't download others private playlists.",
        ["invalidID"] = "Link ID is invalid!",
        ["downloadCanceled"] = "Download was canceled",
        ["downloadFailed"] = "Download failed"
    };

    public static string GetMessage(string errorId)
    {
        return Messages.TryGetValue(errorId, out var message) ? message : "Unknown error occurred";
    }
}

/// <summary>

/// <summary>
/// Exception types for deezspotag downloads (ported from deezspotag errors.ts)
/// </summary>
public class DeezSpoTagDownloadException : Exception
{
    public string ErrorId { get; }
    public Dictionary<string, object>? TrackData { get; }

    public DeezSpoTagDownloadException(string errorId, Dictionary<string, object>? trackData = null) 
        : base(DeezSpoTagErrorMessages.GetMessage(errorId))
    {
        ErrorId = errorId;
        TrackData = trackData;
    }
}

public class DeezSpoTagDownloadCanceledException : Exception
{
    public DeezSpoTagDownloadCanceledException() : base("Download was canceled") { }
}

public class DeezSpoTagGenerationException : Exception
{
    public string Link { get; }
    public string ErrorId { get; }

    public DeezSpoTagGenerationException(string link, string errorId) 
        : base(DeezSpoTagErrorMessages.GetMessage(errorId))
    {
        Link = link;
        ErrorId = errorId;
    }
}
