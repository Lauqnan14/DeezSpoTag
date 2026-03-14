using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Core.Exceptions;

/// <summary>
/// Base deezspotag exception (port of DeezSpoTagError from deezspotag errors.ts)
/// </summary>
public class DeezSpoTagException : Exception
{
    public DeezSpoTagException(string? message = null) : base(message)
    {
    }

    public DeezSpoTagException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Generation error (port of GenerationError from deezspotag errors.ts)
/// </summary>
public class GenerationException : DeezSpoTagException
{
    public string Link { get; }

    public GenerationException(string link, string message) : base(message)
    {
        Link = link;
    }
}

/// <summary>
/// ISRC not on Deezer error (port of ISRCnotOnDeezer from deezspotag errors.ts)
/// </summary>
public class IsrcNotOnDeezerException : GenerationException
{
    public static string ErrorId => "ISRCnotOnDeezer";

    public IsrcNotOnDeezerException(string link) 
        : base(link, "Track ISRC is not available on deezer")
    {
    }
}

/// <summary>
/// Not your private playlist error (port of NotYourPrivatePlaylist from deezspotag errors.ts)
/// </summary>
public class NotYourPrivatePlaylistException : GenerationException
{
    public static string ErrorId => "notYourPrivatePlaylist";

    public NotYourPrivatePlaylistException(string link) 
        : base(link, "You can't download others private playlists.")
    {
    }
}

/// <summary>
/// Track not on Deezer error (port of TrackNotOnDeezer from deezspotag errors.ts)
/// </summary>
public class TrackNotOnDeezerException : GenerationException
{
    public static string ErrorId => "trackNotOnDeezer";

    public TrackNotOnDeezerException(string link) 
        : base(link, "Track not found on deezer!")
    {
    }
}

/// <summary>
/// Album not on Deezer error (port of AlbumNotOnDeezer from deezspotag errors.ts)
/// </summary>
public class AlbumNotOnDeezerException : GenerationException
{
    public static string ErrorId => "albumNotOnDeezer";

    public AlbumNotOnDeezerException(string link) 
        : base(link, "Album not found on deezer!")
    {
    }
}

/// <summary>
/// Playlist not on Deezer error (port of PlaylistNotOnDeezer from deezspotag errors.ts)
/// </summary>
public class PlaylistNotOnDeezerException : GenerationException
{
    public static string ErrorId => "playlistNotOnDeezer";

    public PlaylistNotOnDeezerException(string link) 
        : base(link, "Playlist not found on deezer!")
    {
    }
}

/// <summary>
/// Invalid ID error (port of InvalidID from deezspotag errors.ts)
/// </summary>
public class InvalidIDException : GenerationException
{
    public static string ErrorId => "invalidID";

    public InvalidIDException(string link) 
        : base(link, "Link ID is invalid!")
    {
    }
}

/// <summary>
/// Link not supported error (port of LinkNotSupported from deezspotag errors.ts)
/// </summary>
public class LinkNotSupportedException : GenerationException
{
    public static string ErrorId => "unsupportedURL";

    public LinkNotSupportedException(string link) 
        : base(link, "Link is not supported.")
    {
    }
}

/// <summary>
/// Link not recognized error (port of LinkNotRecognized from deezspotag errors.ts)
/// </summary>
public class LinkNotRecognizedException : GenerationException
{
    public static string ErrorId => "invalidURL";

    public LinkNotRecognizedException(string link) 
        : base(link, "Link is not recognized.")
    {
    }
}

/// <summary>
/// Download error base class (port of DownloadError from deezspotag errors.ts)
/// </summary>
public class DownloadException : DeezSpoTagException
{
    public string? ErrorId { get; }
    public Track? Track { get; }

    public DownloadException(string? message = null, string? errorId = null, Track? track = null) 
        : base(message)
    {
        ErrorId = errorId;
        Track = track;
    }

    public DownloadException(string? message, Exception? innerException, string? errorId = null, Track? track = null) 
        : base(message, innerException)
    {
        ErrorId = errorId;
        Track = track;
    }
}

/// <summary>
/// Plugin not enabled error (port of PluginNotEnabledError from deezspotag errors.ts)
/// </summary>
public class PluginNotEnabledException : DeezSpoTagException
{
    public PluginNotEnabledException(string pluginName) 
        : base($"{pluginName} plugin not enabled")
    {
    }
}

/// <summary>
/// Download failed error (port of DownloadFailed from deezspotag errors.ts)
/// </summary>
public class DownloadFailedException : DownloadException
{
    public DownloadFailedException(string errorId, Track? track = null) 
        : base(ErrorMessages.GetMessage(errorId), errorId, track)
    {
    }
}

/// <summary>
/// Track not 360 error (port of TrackNot360 from deezspotag errors.ts)
/// </summary>
public class TrackNot360Exception : DownloadException
{
    public TrackNot360Exception() : base("Track is not available in Reality Audio 360", "trackNot360")
    {
    }
}

/// <summary>
/// Preferred bitrate not found error (port of PreferredBitrateNotFound from deezspotag errors.ts)
/// </summary>
public class PreferredBitrateNotFoundException : DownloadException
{
    public PreferredBitrateNotFoundException() : base("Preferred bitrate not found", "preferredBitrateNotFound")
    {
    }
}

/// <summary>
/// Download empty error (port of DownloadEmpty from deezspotag errors.ts)
/// </summary>
public class DownloadEmptyException : DeezSpoTagException
{
    public DownloadEmptyException() : base("Download is empty")
    {
    }
}

/// <summary>
/// Download canceled error (port of DownloadCanceled from deezspotag errors.ts)
/// </summary>
public class DownloadCanceledException : DeezSpoTagException
{
    public DownloadCanceledException() : base("Download was canceled")
    {
    }
}

/// <summary>
/// Track error base class (port of TrackError from deezspotag errors.ts)
/// </summary>
public class TrackException : DeezSpoTagException
{
    public TrackException(string? message = null) : base(message)
    {
    }
}

/// <summary>
/// MD5 not found error (port of MD5NotFound from deezspotag errors.ts)
/// </summary>
public class MD5NotFoundException : TrackException
{
    public MD5NotFoundException(string? message = null) : base(message)
    {
    }
}

/// <summary>
/// No data to parse error (port of NoDataToParse from deezspotag errors.ts)
/// </summary>
public class NoDataToParseException : TrackException
{
    public NoDataToParseException(string? message = null) : base(message)
    {
    }
}

/// <summary>
/// Album doesn't exist error (port of AlbumDoesntExists from deezspotag errors.ts)
/// </summary>
public class AlbumDoesntExistException : TrackException
{
    public AlbumDoesntExistException(string? message = null) : base(message)
    {
    }
}

/// <summary>
/// Error messages mapping (port of ErrorMessages from deezspotag errors.ts)
/// </summary>
public static class ErrorMessages
{
    private static readonly Dictionary<string, string> Messages = new()
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
        ["trackNot360"] = "Track is not available in Reality Audio 360.",
        ["preferredBitrateNotFound"] = "Preferred bitrate not found.",
        ["ISRCnotOnDeezer"] = "Track ISRC is not available on deezer",
        ["notYourPrivatePlaylist"] = "You can't download others private playlists.",
        ["trackNotOnDeezer"] = "Track not found on deezer!",
        ["albumNotOnDeezer"] = "Album not found on deezer!",
        ["playlistNotOnDeezer"] = "Playlist not found on deezer!",
        ["invalidID"] = "Link ID is invalid!",
        ["unsupportedURL"] = "Link is not supported.",
        ["invalidURL"] = "Link is not recognized."
    };

    public static string GetMessage(string errorId)
    {
        return Messages.TryGetValue(errorId, out var message) ? message : $"Unknown error: {errorId}";
    }

    public static bool HasMessage(string errorId)
    {
        return Messages.ContainsKey(errorId);
    }

    public static IReadOnlyDictionary<string, string> GetAllMessages()
    {
        return Messages;
    }
}
