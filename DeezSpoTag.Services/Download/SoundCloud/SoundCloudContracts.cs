using System.Collections.ObjectModel;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     One advertised SoundCloud audio stream.
/// </summary>
/// <param name="Url">The api-v2 stream endpoint exactly as SoundCloud advertised it.</param>
/// <param name="Quality">SoundCloud's own quality label: <c>hq</c>, <c>sq</c>, or <c>lq</c>.</param>
/// <param name="MimeType">The advertised MIME type. Only <c>audio/mpeg</c> is supported.</param>
/// <param name="Protocol">The advertised protocol. Only <c>hls</c> is supported.</param>
public sealed record SoundCloudTranscoding(string Url, string Quality, string MimeType, string Protocol)
{
    /// <summary>
    ///     Ranks a transcoding so the best advertised stream wins. Mirrors the ported picker: <c>hq</c> is
    ///     only advertised to an authenticated Go+ request, <c>sq</c> is the standard tier, and <c>lq</c> is
    ///     the last resort. An unrecognised label ranks with <c>lq</c> rather than being rejected, so a new
    ///     SoundCloud label degrades to the worst tier instead of failing the download.
    /// </summary>
    public static int QualityScore(string? quality)
        => (quality ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "hq" => 2,
            "sq" => 1,
            _ => 0
        };

    /// <summary>
    ///     Whether this transcoding is a plain-HLS MP3 stream this engine can actually transfer.
    /// </summary>
    /// <remarks>
    ///     The MIME type must be exactly <c>audio/mpeg</c>. <c>audio/mpegurl</c>, <c>audio/mp4</c>, and the
    ///     progressive variants are all different protocols that this engine does not implement, and
    ///     <c>cbc-encrypted-hls</c> is DRM. Accepting any of them would mean handing a playlist to the
    ///     downloader that it cannot read.
    /// </remarks>
    public bool IsSupportedPlainHlsMpeg
        => !string.IsNullOrWhiteSpace(Url)
           && MimeType.Trim().Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase)
           && Protocol.Trim().Equals("hls", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     A resolved SoundCloud track, carrying only what DeezSpoTag's matching and tracklist paths need.
/// </summary>
public sealed record SoundCloudTrack
{
    /// <summary>Gets the numeric SoundCloud track id.</summary>
    public long Id { get; init; }

    /// <summary>Gets the canonical permalink, e.g. <c>https://soundcloud.com/artist/track</c>.</summary>
    public string PermalinkUrl { get; init; } = string.Empty;

    /// <summary>Gets the track title as SoundCloud publishes it.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the uploader (artist) display name.</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>Gets the containing set or playlist title, when the track belongs to one.</summary>
    public string? Album { get; init; }

    /// <summary>Gets the artwork URL, when SoundCloud advertises one.</summary>
    public string? ArtworkUrl { get; init; }

    /// <summary>
    ///     Gets the track's canonical length in milliseconds.
    /// </summary>
    /// <remarks>
    ///     SoundCloud's <c>full_duration</c> where published, otherwise <c>duration</c>. The full length is
    ///     what matching and written metadata should use, because <c>duration</c> is the length of the preview
    ///     SoundCloud actually streams, which can be a fraction of the recording. This does not describe a
    ///     transfer: the HLS downloader's output length comes from the segments it fetches.
    /// </remarks>
    public int DurationMs { get; init; }

    /// <summary>Gets the advertised genre or tag, when present.</summary>
    public string? Genre { get; init; }

    /// <summary>Gets the ISRC, when SoundCloud exposes one.</summary>
    public string? Isrc { get; init; }

    /// <summary>Gets the label, when present.</summary>
    public string? Label { get; init; }

    /// <summary>Gets the release year, when present.</summary>
    public int? ReleaseYear { get; init; }

    /// <summary>Gets the per-track authorization token SoundCloud puts in the page hydration.</summary>
    /// <remarks>
    ///     This is a per-track secret. It must never be returned by an API, written to a log, or placed in an
    ///     exception message. <see cref="SoundCloudUrlRedactor"/> exists to strip it wherever a URL carrying it
    ///     has to be logged.
    /// </remarks>
    public string TrackAuthorization { get; init; } = string.Empty;

    /// <summary>Gets the advertised transcodings, in the order SoundCloud published them.</summary>
    public IReadOnlyList<SoundCloudTranscoding> Transcodings { get; init; } = new ReadOnlyCollection<SoundCloudTranscoding>([]);

    /// <summary>Gets the position within the containing set, 1-based, when known.</summary>
    public int? Position { get; init; }

    /// <summary>Gets the stable URN, e.g. <c>soundcloud:tracks:2394568125</c>.</summary>
    /// <remarks>
    ///     This, not <see cref="Id" />, is the identity new code should persist. The numeric id is retained
    ///     only because the streaming endpoints still take it.
    /// </remarks>
    public string Urn { get; init; } = string.Empty;

    /// <summary>
    ///     Gets SoundCloud's own optional artist-name field, populated when the artist differs from the uploader.
    /// </summary>
    /// <remarks>
    ///     Optional by design: SoundCloud documents it as absent when the uploader's profile name already is the
    ///     artist, and omits it in practice for most tracks. Its absence therefore says nothing about who the
    ///     recording artist is, which is why <see cref="PreferredArtist" /> has further sources.
    /// </remarks>
    public string? MetadataArtist { get; init; }

    /// <summary>Gets the distributor-supplied recording artist, when the payload carries one.</summary>
    /// <remarks>
    ///     <para>
    ///         Optional observed metadata, not a guaranteed contract. Real SoundCloud payloads expose
    ///         <c>publisher_metadata</c> with distributor metadata, but the current public OpenAPI Track schema
    ///         does not declare it, so every consumer must treat it as opportunistic rather than required.
    ///     </para>
    /// </remarks>
    public string? PublisherArtist { get; init; }

    /// <summary>Gets the uploader's handle, which is an account and not necessarily the recording artist.</summary>
    public string? UploaderUsername { get; init; }

    /// <summary>Gets the distributor-supplied album title, when present.</summary>
    public string? PublisherAlbumTitle { get; init; }

    /// <summary>Gets the tempo in beats per minute, when SoundCloud publishes one.</summary>
    public int? Bpm { get; init; }

    /// <summary>Gets the musical key, when SoundCloud publishes one.</summary>
    public string? KeySignature { get; init; }

    /// <summary>Gets the free-form tags the uploader attached.</summary>
    /// <remarks>
    ///     Unclassified. These are genres, styles, moods, places, and promotional text mixed together, so nothing
    ///     maps them onto Genre, Style, or Mood without a semantic classifier that does not exist here. Retained
    ///     for future genre-intelligence work only.
    /// </remarks>
    public IReadOnlyList<string> TagList { get; init; } = new ReadOnlyCollection<string>([]);

    /// <summary>Gets the release date, when published.</summary>
    public DateTimeOffset? ReleaseDate { get; init; }

    /// <summary>
    ///     Gets the recording artist, in SoundCloud's own order of authority.
    /// </summary>
    /// <remarks>
    ///     <see cref="MetadataArtist" /> first, then the distributor's <see cref="PublisherArtist" />, then the
    ///     uploader handle. The uploader is an account, not necessarily the artist, so it must not outrank
    ///     recording-artist metadata.
    /// </remarks>
    public string PreferredArtist
        => FirstNonEmpty(MetadataArtist, PublisherArtist, UploaderUsername) ?? Artist;

    private static string? FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim();
}

/// <summary>
///     A resolved SoundCloud set (playlist), in its original order.
/// </summary>
public sealed record SoundCloudSet
{
    /// <summary>Gets the numeric SoundCloud set id.</summary>
    public long Id { get; init; }

    /// <summary>Gets the canonical set permalink.</summary>
    public string PermalinkUrl { get; init; } = string.Empty;

    /// <summary>Gets the set title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the set's artwork URL, when advertised.</summary>
    public string? ArtworkUrl { get; init; }

    /// <summary>Gets the uploader (owner) display name.</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>Gets the curator's blurb for the set, when there is one.</summary>
    /// <remarks>
    ///     A user-owned set usually has none. An algorithmic <c>/discover/sets/...</c> page always carries one -
    ///     "Trending tracks in Trap" - and it is the only description such a set has, so dropping it left those
    ///     playlists blank in the tracklist header.
    /// </remarks>
    public string? Description { get; init; }

    /// <summary>Gets the constituent tracks, in playlist order.</summary>
    public IReadOnlyList<SoundCloudTrack> Tracks { get; init; } = new ReadOnlyCollection<SoundCloudTrack>([]);
}

/// <summary>
///     An authorized, ready-to-transfer HLS playlist location.
/// </summary>
/// <param name="PlaylistUrl">The media playlist to fetch and assemble.</param>
/// <param name="AdvertisedQuality">The <c>hq</c>/<c>sq</c>/<c>lq</c> label that was selected.</param>
public sealed record SoundCloudStream(string PlaylistUrl, string AdvertisedQuality);

/// <summary>
///     A SoundCloud track URL after normalization, with any private share token split out.
/// </summary>
/// <param name="PageUrl">The URL to hydrate, with a path-style secret token removed.</param>
/// <param name="SecretToken">The private share token, when the URL carried one.</param>
public sealed record SoundCloudTrackUrl(string PageUrl, string SecretToken);