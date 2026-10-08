namespace DeezSpoTag.Web.Services.ArtistLocation;

/// <summary>
/// One place an artist's location can come from.
/// </summary>
/// <remarks>
/// Implementations must be non-destructive: returning null means "I do not know",
/// never "there is no location". That is what lets the resolver fall through to
/// the next source instead of blanking what a previous source found.
/// </remarks>
public interface IArtistLocationSource
{
    /// <summary>
    /// The value reported in <see cref="ArtistLocationResult.Source"/> and shown
    /// on the artist page. Stable, lowercase, and used by the client to decide
    /// whether the location is user-editable.
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Resolves a location for the artist, or null when this source has nothing.
    /// </summary>
    /// <param name="artistId">Library artist id, used to read and cache identity.</param>
    /// <param name="artistName">The artist name the library holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ArtistLocationResult?> ResolveAsync(
        long artistId,
        string? artistName,
        CancellationToken cancellationToken = default);
}
