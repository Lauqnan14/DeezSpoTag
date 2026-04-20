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
                    new()
                    {
                        Id = "id_first",
                        Label = "Use IDs/ISRC first",
                        Value = new PlatformCustomOptionBoolean { Value = true }
                    },
                    new()
                    {
                        Id = "fingerprint_fallback",
                        Label = "Fallback to fingerprinting",
                        Value = new PlatformCustomOptionBoolean { Value = true }
                    },
                    new()
                    {
                        Id = "fallback_missing_core_tags",
                        Label = "Enable Shazam fallback for missing core tags",
                        Value = new PlatformCustomOptionBoolean { Value = true },
                        Tooltip = "When enabled, enrichment uses Shazam to fill missing/noisy title and artist."
                    },
                    new()
                    {
                        Id = "force_match",
                        Label = "Force Shazam match (skip file if identify fails)",
                        Value = new PlatformCustomOptionBoolean { Value = false },
                        Tooltip = "Requires successful Shazam identification; files that fail recognition are skipped."
                    },
                    new()
                    {
                        Id = "prefer_hq_artwork",
                        Label = "Prefer HQ artwork",
                        Value = new PlatformCustomOptionBoolean { Value = true }
                    },
                    new()
                    {
                        Id = "include_album",
                        Label = "Include album from Shazam metadata",
                        Value = new PlatformCustomOptionBoolean { Value = true }
                    },
                    new()
                    {
                        Id = "include_genre",
                        Label = "Include genre from Shazam metadata",
                        Value = new PlatformCustomOptionBoolean { Value = true }
                    },
                    new()
                    {
                        Id = "include_label",
                        Label = "Include label from Shazam metadata",
                        Value = new PlatformCustomOptionBoolean { Value = true }
                    },
                    new()
                    {
                        Id = "include_release_date",
                        Label = "Include release date/year from Shazam metadata",
                        Value = new PlatformCustomOptionBoolean { Value = true }
                    },
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
}
