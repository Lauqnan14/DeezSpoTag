namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class AudiomackPlatform : AutoTagPlatformBase
{
    public AudiomackPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "audiomack",
            Name = "Audiomack",
            Description = "Metadata from Audiomack songs; strong African and urban catalog coverage. Anonymous access, no account needed.",
            Version = "1.0.0",
            MaxThreads = 1,
            RequiresAuth = false,
            DownloadTags = new List<string>
            {
                "title",
                "artist",
                "albumArtist",
                "album",
                "cover",
                "url",
                "source",
                "trackId",
                "recordingId",
                "releaseId",
                "albumId",
                "length",
                "date",
                "genre",
                "style",
                "mood",
                "label"
            },
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Title,
                SupportedTag.Artist,
                SupportedTag.AlbumArtist,
                SupportedTag.Album,
                SupportedTag.AlbumArt,
                SupportedTag.URL,
                SupportedTag.Source,
                SupportedTag.TrackId,
                SupportedTag.RecordingId,
                SupportedTag.ReleaseId,
                SupportedTag.AlbumId,
                SupportedTag.Duration,
                SupportedTag.ReleaseDate,
                SupportedTag.Genre,
                SupportedTag.Style,
                SupportedTag.Mood,
                SupportedTag.Label
            },
            CustomOptions = new PlatformCustomOptions
            {
                Options = new List<PlatformCustomOption>
                {
                    new()
                    {
                        Id = "match_by_id",
                        Label = "Match by existing Audiomack ID/URL tag first",
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

        return CreateDescriptor(info, "audiomack.png");
    }
}
