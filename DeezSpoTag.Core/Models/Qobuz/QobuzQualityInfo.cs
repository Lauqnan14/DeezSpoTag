namespace DeezSpoTag.Core.Models.Qobuz;

public sealed class QobuzQualityInfo
{
    public int BitDepth { get; set; }
    public double SampleRate { get; set; }
    public bool IsHiRes { get; set; }
    public bool IsStreamable { get; set; }
    public bool IsDownloadable { get; set; }
    public bool IsPurchasable { get; set; }
}
