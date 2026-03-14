namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class MusicBrainzPlatform : AutoTagPlatformBase
{
    private static readonly string[] PreferredReleaseTypes = ["Any", "Album", "Single", "EP"];

    public MusicBrainzPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "musicbrainz",
            Name = "MusicBrainz",
            Description = "Published & unpublished, western & non-western",
            Version = "1.1.0",
            MaxThreads = 4,
            CustomOptions = CreateOptions(
                NumberOption("search_limit", "Search limit", new NumberOptionValues(5, 100, 5, 25), "How many MusicBrainz recording candidates to evaluate per query."),
                BooleanOption("use_isrc_first", "Use ISRC search first", true, "Prioritizes exact ISRC lookup before text search."),
                BooleanOption("match_by_id", "Match by existing MusicBrainz ID first", true, "Uses embedded MusicBrainz recording ID tags before ISRC/text search."),
                BooleanOption("prefer_official", "Prefer official releases", true),
                BooleanOption("exclude_compilations", "De-prioritize compilations", true, "Pushes compilation release groups lower in ranking."),
                SelectOption("preferred_primary_type", "Preferred release type", "Any", PreferredReleaseTypes),
                StringOption("preferred_release_countries", "Preferred release countries (comma separated)", string.Empty, "Example: US,GB,XW (XW = worldwide). Earlier entries are preferred."),
                StringOption("preferred_media_formats", "Preferred media formats (comma separated)", "Digital Media,CD", "Example: Digital Media,CD,Vinyl."),
                BooleanOption("prefer_release_year", "Prefer release year close to matched recording year", true),
                NumberOption("official_weight", "Weight: official releases", new NumberOptionValues(0, 30, 1, 10, Slider: true), "Higher values prioritize official releases more strongly."),
                NumberOption("compilation_penalty_weight", "Weight: compilation penalty", new NumberOptionValues(0, 40, 1, 20, Slider: true), "Higher values push compilations lower when enabled."),
                NumberOption("primary_type_weight", "Weight: preferred release type", new NumberOptionValues(0, 30, 1, 7, Slider: true), "Influence for preferred release type matching."),
                NumberOption("country_weight", "Weight: preferred release countries", new NumberOptionValues(0, 20, 1, 3, Slider: true), "Influence for preferred country order."),
                NumberOption("format_weight", "Weight: preferred media formats", new NumberOptionValues(0, 20, 1, 2, Slider: true), "Influence for preferred format order."),
                NumberOption("year_weight", "Weight: release year proximity", new NumberOptionValues(0, 10, 1, 1, Slider: true), "Penalty scale per year difference from preferred year.")),
            SupportedTags = CreateSupportedTags(
                SupportedTag.Title,
                SupportedTag.Artist,
                SupportedTag.AlbumArtist,
                SupportedTag.Album,
                SupportedTag.URL,
                SupportedTag.ReleaseId,
                SupportedTag.TrackId,
                SupportedTag.Duration,
                SupportedTag.ISRC,
                SupportedTag.Label,
                SupportedTag.CatalogNumber,
                SupportedTag.TrackNumber,
                SupportedTag.Genre),
            RequiresAuth = false
        };

        return CreateDescriptor(info, "musicbrainz.png");
    }
}
