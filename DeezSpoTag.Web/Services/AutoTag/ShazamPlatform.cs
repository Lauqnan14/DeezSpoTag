namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class ShazamPlatform : AutoTagPlatformBase
{
    public ShazamPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "shazam",
            Name = "Shazam",
            Description = "ID-first metadata matching with fingerprint fallback",
            Version = "1.0.0",
            MaxThreads = 1,
            RequiresAuth = false,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Title,
                SupportedTag.Artist,
                SupportedTag.Album,
                SupportedTag.Genre,
                SupportedTag.Label,
                SupportedTag.ReleaseDate,
                SupportedTag.AlbumArt,
                SupportedTag.URL,
                SupportedTag.TrackId,
                SupportedTag.ISRC,
                SupportedTag.Duration,
                SupportedTag.TrackNumber,
                SupportedTag.DiscNumber,
                SupportedTag.Key,
                SupportedTag.Explicit,
                SupportedTag.OtherTags
            },
	            CustomOptions = new PlatformCustomOptions
	            {
	                Options = new List<PlatformCustomOption>
	                {
	                    CreateBooleanOption("id_first", "Use IDs/ISRC first"),
	                    CreateBooleanOption("fingerprint_fallback", "Fallback to fingerprinting"),
	                    CreateBooleanOption(
	                        "fallback_missing_core_tags",
	                        "Enable Shazam fallback for missing core tags",
	                        tooltip: "When enabled, enrichment uses Shazam to fill missing/noisy title and artist."),
	                    CreateBooleanOption(
	                        "force_match",
	                        "Force Shazam match (skip file if identify fails)",
	                        defaultValue: false,
	                        tooltip: "Requires successful Shazam identification; files that fail recognition are skipped."),
	                    CreateBooleanOption("prefer_hq_artwork", "Prefer HQ artwork"),
	                    CreateBooleanOption("include_album", "Include album from Shazam metadata"),
	                    CreateBooleanOption("include_genre", "Include genre from Shazam metadata"),
	                    CreateBooleanOption("include_label", "Include label from Shazam metadata"),
	                    CreateBooleanOption("include_release_date", "Include release date/year from Shazam metadata"),
	                    new()
	                    {
	                        Id = "min_title_similarity",
                        Label = "Minimum title similarity",
                        Value = new PlatformCustomOptionNumber { Min = 40, Max = 100, Step = 1, Value = 72, Slider = true },
                        Tooltip = "Reject Shazam fingerprint matches when title similarity (%) is below this threshold."
                    },
                    new()
                    {
                        Id = "min_artist_similarity",
                        Label = "Minimum artist similarity",
                        Value = new PlatformCustomOptionNumber { Min = 20, Max = 100, Step = 1, Value = 52, Slider = true },
                        Tooltip = "Reject Shazam fingerprint matches when artist similarity (%) is too low."
                    },
                    new()
                    {
                        Id = "max_duration_delta_seconds",
                        Label = "Max duration delta (seconds)",
                        Value = new PlatformCustomOptionNumber { Min = 5, Max = 90, Step = 1, Value = 20, Slider = true },
                        Tooltip = "Reject low-similarity matches when Shazam duration differs beyond this limit."
                    }
                }
            }
	        };

	        return CreateDescriptor(info, "shazam.png");
	    }

	    private static PlatformCustomOption CreateBooleanOption(
	        string id,
	        string label,
	        bool defaultValue = true,
	        string? tooltip = null)
	    {
	        return new PlatformCustomOption
	        {
	            Id = id,
	            Label = label,
	            Value = new PlatformCustomOptionBoolean { Value = defaultValue },
	            Tooltip = tooltip
	        };
	    }
	}
