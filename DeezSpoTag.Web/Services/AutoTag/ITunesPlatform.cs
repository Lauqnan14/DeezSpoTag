namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class ItunesPlatform : AutoTagPlatformBase
{
    public ItunesPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "itunes",
            Name = "iTunes",
            Description = "iTunes metadata/artwork pipeline. Lyrics require an active Apple Music subscription. Slow due to rate limits (~20 tracks / min).",
            Version = "1.0.0",
            MaxThreads = 1,
            RequiresAuth = false,
            DownloadTags = new List<string>
            {
                "title",
                "artist",
                "artists",
                "album",
                "albumArtist",
                "trackNumber",
                "trackTotal",
                "discNumber",
                "genre",
                "year",
                "date",
                "isrc",
                "length",
                "label",
                "cover",
                "source",
                "url",
                "trackId",
                "releaseId"
            },
            SupportedTags = SharedDownloadParityTags(),
            CustomOptions = new PlatformCustomOptions
            {
                Options = new List<PlatformCustomOption>
                {
                    new()
                    {
                        Id = "art_resolution",
                        Label = "Album art resolution",
                        Value = new PlatformCustomOptionNumber { Min = 100, Max = 5000, Step = 100, Value = 1000 }
                    },
                    new()
                    {
                        Id = "country",
                        Label = "Storefront country",
                        Value = new PlatformCustomOptionString { Value = "us" },
                        Tooltip = "2-letter country code used for iTunes search/lookup (example: us, gb, ke)."
                    },
                    new()
                    {
                        Id = "search_limit",
                        Label = "Search limit",
                        Value = new PlatformCustomOptionNumber { Min = 5, Max = 200, Step = 5, Value = 25 }
                    },
                    new()
                    {
                        Id = "match_by_id",
                        Label = "Match by existing iTunes ID first",
                        Value = new PlatformCustomOptionBoolean { Value = true },
                        Tooltip = "Uses existing ITUNES_TRACK_ID/ITUNESCATALOGID tags before text search."
                    }
                }
            }
        };

        return CreateDescriptor(info, "itunes.png");
    }

    internal static List<SupportedTag> SharedDownloadParityTags()
    {
        // Keep Apple enrichment/enhancement tag options aligned with Apple download tags.
        return new List<SupportedTag>
        {
            SupportedTag.Title,
            SupportedTag.Artist,
            SupportedTag.AlbumArtist,
            SupportedTag.Album,
            SupportedTag.AlbumArt,
            SupportedTag.UnsyncedLyrics,
            SupportedTag.SyncedLyrics,
            SupportedTag.TtmlLyrics,
            SupportedTag.URL,
            SupportedTag.TrackId,
            SupportedTag.ReleaseId,
            SupportedTag.Duration,
            SupportedTag.TrackNumber,
            SupportedTag.TrackTotal,
            SupportedTag.DiscNumber,
            SupportedTag.ISRC,
            SupportedTag.ReleaseDate,
            SupportedTag.Genre,
            SupportedTag.Label,
            SupportedTag.Explicit
        };
    }
}
