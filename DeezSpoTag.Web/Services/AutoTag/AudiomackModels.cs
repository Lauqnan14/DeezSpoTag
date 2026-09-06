using System.Text.Json;

namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>Per-platform tunables for the Audiomack AutoTag matcher (profile `custom` JSON).</summary>
public sealed class AudiomackMatchConfig
{
    public bool MatchById { get; set; }
    public int SearchLimit { get; set; } = 12;
}
