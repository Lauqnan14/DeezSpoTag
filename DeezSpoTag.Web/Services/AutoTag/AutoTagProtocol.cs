namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
/// Single source of truth for the text markers that cross component boundaries
/// (runner → service, organizer → service). Emitters and parsers must both
/// reference these constants, so a marker rename fails the build instead of
/// silently breaking platform tracking, resume, or manifest path rewriting.
/// </summary>
internal static class AutoTagProtocol
{
    /// <summary>Prefix of runner log lines that carry run metadata for the service.</summary>
    public const string LogMarker = "onetagger_autotag:";

    /// <summary>Runner log message announcing the start of a platform pass.</summary>
    public const string StartingPlatformMessage = "starting ";

    /// <summary>Fallback error text for a stopped run.</summary>
    public const string StoppedOutcome = "stopped";

    /// <summary>Organizer report entry announcing a file move.</summary>
    public const string MoveFileEntryPrefix = "move-file: ";

    /// <summary>Separator between source and destination in a move-file entry.</summary>
    public const string MoveFileEntrySeparator = " -> ";
}
