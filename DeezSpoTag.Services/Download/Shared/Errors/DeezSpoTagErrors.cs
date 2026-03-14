using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Services.Download.Shared.Errors;

/// <summary>
/// EXACT port of deezspotag error classes from /src/deezspotag/deezspotag/src/errors.ts
/// </summary>
public class DeezSpoTagException : Exception
{
    public DeezSpoTagException(string? message = null) : base(message)
    {
    }
}

public class GenerationException : DeezSpoTagException
{
    public string Link { get; }

    public GenerationException(string link, string message) : base(message)
    {
        Link = link;
    }
}

public class IsrcNotOnDeezerException : GenerationException
{
    public string ErrorId { get; } = "ISRCnotOnDeezer";

    public IsrcNotOnDeezerException(string link) : base(link, "Track ISRC is not available on deezer")
    {
    }
}

public class NotYourPrivatePlaylistException : GenerationException
{
    public string ErrorId { get; } = "notYourPrivatePlaylist";

    public NotYourPrivatePlaylistException(string link) : base(link, "You can't download others private playlists.")
    {
    }
}

public class TrackNotOnDeezerException : GenerationException
{
    public string ErrorId { get; } = "trackNotOnDeezer";

    public TrackNotOnDeezerException(string link) : base(link, "Track not found on deezer!")
    {
    }
}

public class AlbumNotOnDeezerException : GenerationException
{
    public string ErrorId { get; } = "albumNotOnDeezer";

    public AlbumNotOnDeezerException(string link) : base(link, "Album not found on deezer!")
    {
    }
}

public class InvalidIdException : GenerationException
{
    public string ErrorId { get; } = "invalidID";

    public InvalidIdException(string link) : base(link, "Link ID is invalid!")
    {
    }
}

public class LinkNotSupportedException : GenerationException
{
    public string ErrorId { get; } = "unsupportedURL";

    public LinkNotSupportedException(string link) : base(link, "Link is not supported.")
    {
    }
}

public class LinkNotRecognizedException : GenerationException
{
    public string ErrorId { get; } = "invalidURL";

    public LinkNotRecognizedException(string link) : base(link, "Link is not recognized.")
    {
    }
}

public class DownloadException : DeezSpoTagException
{
    public DownloadException() : base()
    {
    }
}

public class PluginNotEnabledException : DeezSpoTagException
{
    public PluginNotEnabledException(string pluginName) : base($"{pluginName} plugin not enabled")
    {
    }
}

/// <summary>
/// EXACT port of ErrorMessages from deezspotag errors.ts
/// </summary>
public static class ErrorMessages
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
        ["albumDoesntExists"] = "Track's album does not exsist, failed to gather info.",
        ["notLoggedIn"] = "You need to login to download tracks.",
        ["wrongGeolocation"] = "Your account can't stream the track from your current country.",
        ["wrongGeolocationNoAlternative"] = "Your account can't stream the track from your current country and no alternative found.",
        ["mapping_miss"] = "Spotify to Deezer mapping did not resolve to a valid Deezer track.",
        ["mapped_but_quality_unavailable"] = "Track is mapped on Deezer, but requested quality is unavailable.",
        ["mapped_but_quality_unavailable_360"] = "Track is mapped on Deezer, but 360 Reality Audio is unavailable.",
        ["mapped_but_geo_blocked"] = "Track is mapped on Deezer, but blocked in your region.",
        ["mapped_but_license_blocked"] = "Track is mapped on Deezer, but account license cannot stream requested quality.",
        ["transient_token_failure"] = "Track is mapped on Deezer, but token validation failed. Retry may succeed.",
        ["transient_network_failure"] = "Track is mapped on Deezer, but availability checks failed due to transient network/auth issues."
    };
}

public class DeezSpoTagDownloadFailedException : DownloadException
{
    public string ErrorId { get; }
    public Track? Track { get; }

    public DeezSpoTagDownloadFailedException(string errorId, Track? track = null) : base()
    {
        ErrorId = errorId;
        Track = track;
    }

    public override string Message => ErrorMessages.Messages.GetValueOrDefault(ErrorId, ErrorId);
}

public class TrackNot360Exception : DownloadException
{
    public TrackNot360Exception() : base()
    {
    }
}

public class PreferredBitrateNotFoundException : DownloadException
{
    public PreferredBitrateNotFoundException() : base()
    {
    }
}

public class DownloadEmptyException : DeezSpoTagException
{
    public DownloadEmptyException() : base()
    {
    }
}

public class DownloadCanceledException : DeezSpoTagException
{
    public DownloadCanceledException() : base()
    {
    }
}

public class TrackException : DeezSpoTagException
{
    public TrackException(string? message = null) : base(message)
    {
    }
}

public class Md5NotFoundException : TrackException
{
    public Md5NotFoundException(string? message = null) : base(message)
    {
    }
}

public class NoDataToParseException : TrackException
{
    public NoDataToParseException(string? message = null) : base(message)
    {
    }
}

public class AlbumDoesntExistException : TrackException
{
    public AlbumDoesntExistException(string? message = null) : base(message)
    {
    }
}
