using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
///     Per-platform tunables for the SoundCloud AutoTag matcher (profile <c>custom</c> JSON).
/// </summary>
/// <remarks>
///     Deliberately two options. Both are implemented, and nothing is exposed that the matcher ignores.
/// </remarks>
public sealed class SoundcloudMatchConfig
{
    /// <summary>
    ///     Gets or sets whether an existing SoundCloud identity on the file is resolved directly.
    /// </summary>
    /// <remarks>
    ///     Off by default, matching <c>Audiomack</c>. When it is on and the embedded identity cannot be
    ///     resolved, the run falls through to search, which can replace it; see
    ///     <c>SoundcloudMatcher</c> for why that differs from the Spotify behaviour.
    /// </remarks>
    [JsonPropertyName("match_by_id")]
    public bool MatchById { get; set; }

    /// <summary>Gets or sets how many search candidates to evaluate.</summary>
    [JsonPropertyName("search_limit")]
    public int SearchLimit { get; set; } = 12;
}