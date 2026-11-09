namespace DeezSpoTag.Core.Models;

public class AudioFeaturesBase
{
    public double? Danceability { get; set; }
    public double? Energy { get; set; }
    public double? Valence { get; set; }
    public double? Acousticness { get; set; }
    public double? Instrumentalness { get; set; }
    public double? Speechiness { get; set; }
    public double? Loudness { get; set; }
    public double? Tempo { get; set; }
    public int? TimeSignature { get; set; }
    public double? Liveness { get; set; }

    /// <summary>Shazam's algorithmic 0–1 melodicness analysis; null when absent or unverifiable.</summary>
    public double? Melodicness { get; set; }
}

public class MusicKeyAudioFeaturesBase : AudioFeaturesBase
{
    public string MusicKey { get; set; } = "";
}
