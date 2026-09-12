using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BpmSupremeResponse<T>
{
    public T Data { get; set; } = default!;
}

public sealed class BpmSupremeUser
{
    [JsonPropertyName("session_token")]
    public string SessionToken { get; set; } = "";
}

public sealed class BpmSupremeSong
{
    public string Artist { get; set; } = "";
    public long Bpm { get; set; }
    public BpmSupremeCategory Category { get; set; } = new();
    [JsonPropertyName("cover_url")]
    public string CoverUrl { get; set; } = "";
    [JsonPropertyName("depth_analysis")]
    public BpmSupremeDepthAnalysis? DepthAnalysis { get; set; }
    public BpmSupremeGenre Genre { get; set; } = new();
    public string? Key { get; set; }
    public string Label { get; set; } = "";
    public string Title { get; set; } = "";
    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }
    public long Id { get; set; }
    public List<BpmSupremeMedia> Media { get; set; } = new();

    public List<BpmSupremeTrackInfo> ToTracks()
    {
        var baseTrack = new BpmSupremeTrackInfo
        {
            Title = Title,
            Artists = new List<string> { Artist },
            Bpm = Bpm,
            Art = CoverUrl.Contains("default_cover.png", StringComparison.OrdinalIgnoreCase) ? null : CoverUrl,
            Genres = new List<string> { Genre.Name },
            Key = Key,
            Label = Label,
            ReleaseDate = CreatedAt,
            TrackId = Id.ToString(),
            Mood = DepthAnalysis?.Mood,
            Url = $"https://app.bpmsupreme.com/d/album/{Id}"
        };

        var output = new List<BpmSupremeTrackInfo> { baseTrack };
        foreach (var clone in Media.Select(media => baseTrack with { Title = media.Name }))
        {
            output.Add(clone);
        }

        return output;
    }
}

public sealed class BpmSupremeCategory
{
    public string Name { get; set; } = "";
}

public sealed class BpmSupremeDepthAnalysis
{
    public string Mood { get; set; } = "";
}

public sealed class BpmSupremeGenre
{
    public string Name { get; set; } = "";
}

public sealed class BpmSupremeMedia
{
    public string Name { get; set; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BpmSupremeLibrary
{
    Supreme,
    Latino
}

/// <summary>
/// BPM Supreme credentials and catalog choice. All three members are supplied at run time by
/// <c>AutoTagService.InjectPlatformAuthAsync</c> from the platform-auth store, which the user fills
/// in on the Login page's BPM Supreme tab (the Platform Authentication section) — email, password,
/// and the Library dropdown all live there. None of them is a control on the BPM Supreme
/// configuration card, which declares no options at all. Do not add card controls for them.
/// </summary>
public sealed class BpmSupremeConfig
{
    /// <summary>Account email, from the Login page's BPM Supreme tab.</summary>
    public string Email { get; set; } = "";

    /// <summary>Account password, from the Login page's BPM Supreme tab.</summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// Which catalog to search (Supreme or Latino). Chosen with the Library dropdown on the Login
    /// page's BPM Supreme tab, not on the configuration card.
    /// </summary>
    public BpmSupremeLibrary Library { get; set; } = BpmSupremeLibrary.Supreme;
}

public sealed record BpmSupremeTrackInfo
{
    public string Title { get; init; } = "";
    public List<string> Artists { get; init; } = new();
    public long? Bpm { get; init; }
    public string? Art { get; init; }
    public List<string> Genres { get; init; } = new();
    public string? Key { get; init; }
    public string? Label { get; init; }
    public DateTime? ReleaseDate { get; init; }
    public string TrackId { get; init; } = "";
    public string? Mood { get; init; }
    public string Url { get; init; } = "";
}
