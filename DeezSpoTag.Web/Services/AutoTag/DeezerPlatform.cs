namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class DeezerPlatform : AutoTagPlatformBase
{
    public DeezerPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        return CreateDescriptor(
            new PlatformInfo
            {
                Id = "deezer",
                Name = "Deezer",
                Description = "Fast metadata source; ARL login is only required for Deezer lyrics",
                Version = "1.0.0",
                MaxThreads = 2,
                RequiresAuth = false,
                SupportedTags = SpotifyPlatform.SharedDownloadParityTags(),
                DownloadTags = CreateDownloadTags(
                    "title",
                    "artist",
                    "artists",
                    "album",
                    "albumArtist",
                    "trackNumber",
                    "trackTotal",
                    "discNumber",
                    "discTotal",
                    "genre",
                    "year",
                    "date",
                    "explicit",
                    "isrc",
                    "length",
                    "barcode",
                    "bpm",
                    "replayGain",
                    "label",
                    "lyrics",
                    "syncedLyrics",
                    "copyright",
                    "composer",
                    "involvedPeople",
                    "cover",
                    "source",
                    "url",
                    "trackId",
                    "releaseId"),
                CustomOptions = CreateOptions(
                    NumberOption("art_resolution", "Album Art Resolution", new NumberOptionValues(100, 1600, 100, 1200)),
                    BooleanOption("match_by_id", "Match by existing Deezer ID tag first", true))
            },
            "deezer.png");
    }
}
