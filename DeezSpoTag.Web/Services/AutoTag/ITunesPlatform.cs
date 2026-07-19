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
                "releaseType",
                "discNumber",
                "discTotal",
                "genre",
                "explicit",
                "year",
                "date",
                "isrc",
                "length",
                "barcode",
                "label",
                "lyrics",
                "syncedLyrics",
                "ttmlLyrics",
                "copyright",
                "composer",
                "involvedPeople",
                "otherTags",
                "cover",
                "source",
                "url",
                "trackId",
                "releaseId",
                "recordingId",
                "artistId",
                "albumId"
            },
            SupportedTags = AutoTagSupportedTags(),
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
                        Tooltip = "Uses existing APPLE_TRACK_ID, Apple Music, or iTunes track ID tags. Text search is used only when no valid ID is present."
                    }
                }
            }
        };

        return CreateDescriptor(info, "itunes.png");
    }

    internal static List<SupportedTag> AutoTagSupportedTags()
    {
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
            SupportedTag.RecordingId,
            SupportedTag.ArtistId,
            SupportedTag.AlbumId,
            SupportedTag.Duration,
            SupportedTag.TrackNumber,
            SupportedTag.TrackTotal,
            SupportedTag.ReleaseType,
            SupportedTag.DiscNumber,
            SupportedTag.DiscTotal,
            SupportedTag.ISRC,
            SupportedTag.ReleaseDate,
            SupportedTag.Genre,
            SupportedTag.Label,
            SupportedTag.Source,
            SupportedTag.Copyright,
            SupportedTag.Composer,
            SupportedTag.InvolvedPeople,
            SupportedTag.OtherTags,
            SupportedTag.Explicit
        };
    }
}
