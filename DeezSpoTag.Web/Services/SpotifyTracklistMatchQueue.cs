using System.Threading.Channels;

namespace DeezSpoTag.Web.Services;

public interface ISpotifyTracklistMatchQueue
{
    void Enqueue(string token, int index, SpotifyTrackSummary track, bool allowFallbackSearch);
    ChannelReader<SpotifyTracklistMatchWorkItem> Reader { get; }
}

public sealed class SpotifyTracklistMatchQueue : ISpotifyTracklistMatchQueue
{
    private readonly Channel<SpotifyTracklistMatchWorkItem> _channel = Channel.CreateUnbounded<SpotifyTracklistMatchWorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void Enqueue(
        string token,
        int index,
        SpotifyTrackSummary track,
        bool allowFallbackSearch)
    {
        _channel.Writer.TryWrite(new SpotifyTracklistMatchWorkItem(
            token,
            index,
            track,
            allowFallbackSearch));
    }

    public ChannelReader<SpotifyTracklistMatchWorkItem> Reader => _channel.Reader;
}

public sealed record SpotifyTracklistMatchWorkItem(
    string Token,
    int Index,
    SpotifyTrackSummary Track,
    bool AllowFallbackSearch);
