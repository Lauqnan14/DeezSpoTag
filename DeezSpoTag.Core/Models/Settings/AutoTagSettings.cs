using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models.Settings;

public sealed class AutoTagSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Data { get; set; } = new();
}

/// <summary>
/// Per-profile AutoTag runtime options for the final Genre Intelligence stage.
/// These are execution choices only; taxonomy, mappings, rules and locks are
/// shared across profiles.
/// </summary>
public sealed class AutoTagGenreIntelligenceSettings
{
    public bool Enabled { get; set; }
    public int MaxGenres { get; set; } = 3;

    /// <summary>
    /// Keep a file tag the taxonomy does not recognise instead of dropping it.
    /// A personal value such as "My Afro Mix" is written back to the field it was
    /// read from rather than deleted.
    /// </summary>
    public bool PreserveUnmappedTags { get; set; } = true;

    public bool IncludeParentGenres { get; set; }
    public bool WriteSubstyle { get; set; }
    public bool WriteContext { get; set; }
    public bool WriteScene { get; set; }
}
