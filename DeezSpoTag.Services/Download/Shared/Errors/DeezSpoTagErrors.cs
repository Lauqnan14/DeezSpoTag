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

public class DeezSpoTagDownloadFailedException : DownloadException
{
    public string ErrorId { get; }
    public Track? Track { get; }

    public DeezSpoTagDownloadFailedException(string errorId, Track? track = null) : base()
    {
        ErrorId = errorId;
        Track = track;
    }

    public override string Message => DeezSpoTag.Core.Exceptions.ErrorMessages.GetMessage(ErrorId);
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
