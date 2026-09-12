namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class MusixmatchPlatform : AutoTagPlatformBase
{
    public MusixmatchPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "musixmatch",
            Name = "Musixmatch",
            Description = "Fetch lyrics from the largest lyrics platform in the world",
            Version = "1.0.0",
            MaxThreads = 1,
            RequiresAuth = false,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.SyncedLyrics,
                SupportedTag.UnsyncedLyrics,
                SupportedTag.TtmlLyrics
            },
            CustomOptions = new PlatformCustomOptions
            {
                Options = new List<PlatformCustomOption>
                {
                    new()
                    {
                        Id = "duration_tolerance_seconds",
                        Label = "Duration tolerance (seconds)",
                        Tooltip = "Maximum difference between the file duration and a Musixmatch candidate's length for the candidate to stay eligible.",
                        Value = new PlatformCustomOptionNumber
                        {
                            Min = 0,
                            Max = 60,
                            Step = 1,
                            Value = 10,
                            Slider = true
                        }
                    },
                    new()
                    {
                        Id = "search_page_size",
                        Label = "Search results per query",
                        Tooltip = "How many candidates the Musixmatch track search may return. Higher values find more matches but take longer.",
                        Value = new PlatformCustomOptionNumber
                        {
                            Min = 1,
                            Max = 100,
                            Step = 1,
                            Value = 10,
                            Slider = true
                        }
                    },
                    new()
                    {
                        Id = "richsync_max_deviation_seconds",
                        Label = "Word-sync drift allowance (seconds)",
                        Tooltip = "How far word-level synchronisation may drift from the track length before Musixmatch refuses to serve it.",
                        Value = new PlatformCustomOptionNumber
                        {
                            Min = 0,
                            Max = 60,
                            Step = 1,
                            Value = 10,
                            Slider = true
                        }
                    },
                    new()
                    {
                        Id = "subtitle_max_deviation_seconds",
                        Label = "Line-sync drift allowance (seconds)",
                        Tooltip = "The same allowance for line-level (subtitle) synchronisation.",
                        Value = new PlatformCustomOptionNumber
                        {
                            Min = 0,
                            Max = 60,
                            Step = 1,
                            Value = 10,
                            Slider = true
                        }
                    }
                }
            }
        };

        return CreateDescriptor(info, "musixmatch.png");
    }
}