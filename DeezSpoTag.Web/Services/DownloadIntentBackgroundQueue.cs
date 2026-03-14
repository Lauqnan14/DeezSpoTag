using System.Threading.Channels;
using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Web.Services;

public interface IDownloadIntentBackgroundQueue
{
    bool Enqueue(DownloadIntent intent);
    ChannelReader<DownloadIntent> Reader { get; }
}

public sealed class DownloadIntentBackgroundQueue : IDownloadIntentBackgroundQueue
{
    private readonly Channel<DownloadIntent> _channel = Channel.CreateUnbounded<DownloadIntent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public bool Enqueue(DownloadIntent intent)
    {
        return _channel.Writer.TryWrite(intent);
    }

    public ChannelReader<DownloadIntent> Reader => _channel.Reader;
}
