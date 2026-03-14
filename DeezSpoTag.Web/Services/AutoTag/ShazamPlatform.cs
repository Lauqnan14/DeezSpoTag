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
                    }
                }
            }
        };

        return CreateDescriptor(info, "shazam.png");
    }
}
