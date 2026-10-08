namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
///     SoundCloud as an AutoTag metadata source.
/// </summary>
/// <remarks>
///     <para>
///         Public metadata only. No end-user SoundCloud sign-in is required to read a public track, so this
///         platform is not marked <c>RequiresAuth</c>; the optional account token raises what is reachable but
///         is not a precondition for tagging.
///     </para>
///     <para>
///         The advertised tags are exactly what <see cref="SoundcloudMatcher" /> can return. Album, album
///         artist, track and disc numbers, copyright, mood, style, release type, lyrics, barcode and explicit
///         are all absent from this list because SoundCloud does not supply those semantics reliably:
///         <c>release</c> on a track is the track title rather than an album, and there is no explicit flag in
///         the Track schema at all.
///     </para>
/// </remarks>
public sealed class SoundcloudPlatform : AutoTagPlatformBase
{
    public SoundcloudPlatform(IWebHostEnvironment environment)
        : base(environment)
    {
    }

    /// <inheritdoc />
    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "soundcloud",
            Name = "SoundCloud",
            Description = "Metadata from public SoundCloud tracks. No account needed; an optional token unlocks "
                          + "the tracks your account can reach.",
            Version = "1.0.0",
            MaxThreads = 1,
            RequiresAuth = false,
            DownloadTags = new List<string>
            {
                "title", "artist", "url", "trackId", "source",
                "length", "isrc", "date", "genre", "bpm", "key", "label", "cover"
            },
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Title, SupportedTag.Artist, SupportedTag.URL, SupportedTag.TrackId,
                SupportedTag.Source, SupportedTag.Duration, SupportedTag.ISRC, SupportedTag.ReleaseDate,
                SupportedTag.Genre, SupportedTag.BPM, SupportedTag.Key, SupportedTag.Label,
                SupportedTag.AlbumArt
            },
            CustomOptions = new PlatformCustomOptions
            {
                Options = new List<PlatformCustomOption>
                {
                    new()
                    {
                        Id = "match_by_id",
                        Label = "Match by existing SoundCloud ID/URL tag first",
                        Value = new PlatformCustomOptionBoolean { Value = false }
                    },
                    new()
                    {
                        Id = "search_limit",
                        Label = "Search candidates to evaluate",
                        Value = new PlatformCustomOptionNumber { Min = 5, Max = 30, Step = 1, Value = 12, Slider = true }
                    }
                }
            }
        };

        return CreateDescriptor(info, "soundcloud.png");
    }
}