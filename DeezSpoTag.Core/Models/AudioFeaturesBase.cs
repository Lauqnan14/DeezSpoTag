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
}

public class MusicKeyAudioFeaturesBase : AudioFeaturesBase
{
    public string MusicKey { get; set; } = "";
}
