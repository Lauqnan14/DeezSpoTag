namespace DeezSpoTag.Services.Download.Shared;

/// <summary>
/// Track queue data for async queue processing - matches deezspotag { track, pos } structure
/// </summary>
public class TrackQueueData
{
    public Dictionary<string, object> Track { get; set; } = new();
    public int Position { get; set; }
}