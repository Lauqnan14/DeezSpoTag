namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     The name a candidate's file carries, for the payloads that describe a candidate to a client.
/// </summary>
/// <remarks>
///     <para>
///         Three payloads describe a candidate to the search page: the live hub event, the live results
///         endpoint and the recorded results endpoint. They all used to carry only the peer's filename, so
///         the panel had no way to name a track, an artist or an album and fell back to the search term.
///     </para>
///     <para>
///         The facts come from the parser the scorer already runs, so the panel and the matcher read a
///         filename exactly the same way. The fields are returned unnamed because each payload writes them
///         out under its own literal names, which is what keeps the three shapes from drifting apart.
///     </para>
/// </remarks>
public static class SoulseekFilenameProjection
{
    /// <summary>
    ///     Reads the track title, artist, album and track number out of a candidate's path.
    /// </summary>
    /// <param name="filename">The candidate's full remote path.</param>
    /// <param name="durationSeconds">The duration slskd reported, when it determined one.</param>
    public static (string Title, string? Artist, string? Album, int? TrackNumber) Describe(
        string? filename,
        int? durationSeconds = null)
    {
        var facts = SoulseekFilenameParser.Parse(filename, durationSeconds);
        return (facts.Title, facts.Artist, facts.Album, facts.TrackNumber);
    }
}
