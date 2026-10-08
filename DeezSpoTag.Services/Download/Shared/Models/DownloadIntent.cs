using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Services.Download.Shared.Models;

public sealed class DownloadIntent : MusicKeyAudioFeaturesBase
{
    public string SourceService { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string SpotifyId { get; set; } = "";
    public string SpotifyArtistId { get; set; } = "";
    public string DeezerId { get; set; } = "";
    public string QobuzId { get; set; } = "";
    public string TidalId { get; set; } = "";
    public string AmazonId { get; set; } = "";

    /// <summary>
    ///     Gets or sets the SoundCloud track id, when the request carries one.
    /// </summary>
    /// <remarks>
    ///     Carried so a SoundCloud download keeps its identity through retries and cross-engine fallback. The
    ///     permalink is the authoritative value: a SoundCloud id cannot be turned back into a URL, because the
    ///     path carries the uploader and track slugs rather than the numeric id.
    /// </remarks>
    public string SoundCloudId { get; set; } = "";

    /// <summary>Gets or sets the exact SoundCloud permalink, when the request carries one.</summary>
    public string SoundCloudUrl { get; set; } = "";

    public string DeezerAlbumId { get; set; } = "";
    public string DeezerArtistId { get; set; } = "";
    public string Isrc { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Cover { get; set; } = "";

    /// <summary>
    ///     Catalogue artwork to show in the queue, and nothing else.
    /// </summary>
    /// <remarks>
    ///     Deliberately separate from <see cref="Cover" />. The base cover is read by the post-download
    ///     artwork pipeline and becomes the tagger's prefetched artwork source, so writing a display-only
    ///     value there would override the per-profile artwork preference. This value is carried for
    ///     presentation and must never be promoted to the base cover.
    /// </remarks>
    public string DisplayCoverUrl { get; set; } = "";

    /// <summary>
    ///     Gets or sets the Soulseek peer whose file the reader chose, when they chose one.
    /// </summary>
    /// <remarks>
    ///     Empty for a track request that the library made, which searches for itself when it downloads. Set
    ///     for a request made from the search tab, where the reader picked a specific candidate: the engine
    ///     then fetches that peer file instead of running a second search and risking a different answer.
    /// </remarks>
    public string SoulseekUsername { get; set; } = "";

    /// <summary>
    ///     Gets or sets the chosen candidate's full remote path, on the same terms as
    ///     <see cref="SoulseekUsername" />.
    /// </summary>
    public string SoulseekRemotePath { get; set; } = "";

    /// <summary>Gets or sets the chosen candidate's size in bytes, used to verify the delivered file.</summary>
    public long SoulseekRemoteSizeBytes { get; set; }

    /// <summary>
    ///     Gets or sets the full remote paths of non-audio files the reader chose to take from the same peer
    ///     folder: a cover image, lyrics or a cue sheet.
    /// </summary>
    /// <remarks>
    ///     Empty unless peer artwork or peer lyrics was explicitly turned on. A release that is on no streaming
    ///     service often has its only artwork in the folder the peer is sharing, so this is occasionally the
    ///     only artwork a track will ever have. These are decoration, so a sidecar that fails to arrive is
    ///     skipped and never fails the audio.
    /// </remarks>
    public List<string> SoulseekSidecarRemotePaths { get; set; } = new();

    /// <summary>
    ///     Gets or sets what the peer's folder is known to be: an album, a single, or nothing known.
    /// </summary>
    /// <remarks>
    ///     Null means genuinely unknown, and is distinct from a single. It selects which destination profile
    ///     the reader's album-against-single preference is applied to, so an honest unknown is carried all
    ///     the way through rather than guessed at and frozen into the queue.
    /// </remarks>
    public string? SoulseekReleaseCategory { get; set; }
    public int DurationMs { get; set; }
    public int Position { get; set; }
    public List<string> Genres { get; set; } = new();
    public string Label { get; set; } = "";
    public string Copyright { get; set; } = "";
    public bool? Explicit { get; set; }
    public string Composer { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public int TrackTotal { get; set; }
    public int DiscTotal { get; set; }
    public string Url { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string PreferredEngine { get; set; } = "";
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string Quality { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long? DestinationFolderId { get; set; }
    public long? SecondaryDestinationFolderId { get; set; }
    public string AppleId { get; set; } = "";
    public string AppleArtistId { get; set; } = "";
    public string AppleAlbumId { get; set; } = "";
    public string AppleAlbumName { get; set; } = "";
    public string AppleArtistName { get; set; } = "";
    public string AppleIsrc { get; set; } = "";
    public int? AppleDurationMs { get; set; }
    public string WatchlistSource { get; set; } = "";
    public string WatchlistPlaylistId { get; set; } = "";
    public string WatchlistTrackId { get; set; } = "";
    public string WatchlistOrigin { get; set; } = "";
    public string WatchlistUnavailableSettingsFingerprint { get; set; } = "";
    public bool HasAtmos { get; set; }
    public bool HasAppleDigitalMaster { get; set; }
    public bool AllowQualityUpgrade { get; set; }
    public DownloadEngineOrderSettings? DownloadEngineOrder { get; set; }
    public string EnhancementBatchId { get; set; } = "";
    public string EnhancementOperation { get; set; } = "";
    public long? EnhancementSourceTrackId { get; set; }
    public long? EnhancementSourceAlbumId { get; set; }
    public long? EnhancementSourceAudioFileId { get; set; }
    public string EnhancementSourceAudioPath { get; set; } = "";
    public string EnhancementDuplicatesFolderName { get; set; } = "";
}

internal sealed class FlexibleStringJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number => ReadNumberAsString(ref reader),
            JsonTokenType.True => bool.TrueString.ToLowerInvariant(),
            JsonTokenType.False => bool.FalseString.ToLowerInvariant(),
            JsonTokenType.Null => string.Empty,
            _ => throw new JsonException($"Unsupported token type '{reader.TokenType}' for string conversion.")
        };
    }

    private static string ReadNumberAsString(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out var intValue))
        {
            return intValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return reader.TryGetDouble(out var doubleValue)
            ? doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

public sealed class DownloadIntentResult
{
    public bool Success { get; set; }
    public string Engine { get; set; } = "";
    public string Message { get; set; } = "";
    public List<string> Queued { get; set; } = new();
    public List<string> RelatedQueueUuids { get; set; } = new();
    public int Skipped { get; set; }
    public List<string> SkipReasonCodes { get; set; } = new();
    public List<string> SkipReasons { get; set; } = new();
}
