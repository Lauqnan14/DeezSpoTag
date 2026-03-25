using DeezSpoTag.Core.Exceptions;

namespace DeezSpoTag.Services.Download.Shared.Models;

/// <summary>
/// DeezSpoTag error types and messages (ported from deezspotag errors.ts)
/// </summary>
public static class DeezSpoTagErrorMessages
{
    public static readonly IReadOnlyDictionary<string, string> Messages = BuildMessages();

    public static string GetMessage(string errorId)
    {
        return Messages.TryGetValue(errorId, out var message) ? message : "Unknown error occurred";
    }

    private static Dictionary<string, string> BuildMessages()
    {
        var messages = new Dictionary<string, string>(ErrorMessages.GetAllMessages(), StringComparer.Ordinal)
        {
            ["trackNotOnDeezer"] = "Track not found on Deezer!",
            ["albumNotOnDeezer"] = "Album not found on Deezer!",
            ["invalidURL"] = "URL not recognized",
            ["unsupportedURL"] = "URL not supported yet",
            ["ISRCnotOnDeezer"] = "Track ISRC is not available on Deezer",
            ["downloadCanceled"] = "Download was canceled",
            ["downloadFailed"] = "Download failed"
        };
        return messages;
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
