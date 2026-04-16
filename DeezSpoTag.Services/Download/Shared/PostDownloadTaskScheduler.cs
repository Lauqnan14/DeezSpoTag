using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared;

public sealed class PostDownloadTaskScheduler : BackgroundService, IPostDownloadTaskScheduler
{
    private const int QueueCapacity = 512;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PostDownloadTaskScheduler> _logger;
    private readonly Channel<PostDownloadTaskWorkItem> _channel;
    private readonly int _workerCount;

    public PostDownloadTaskScheduler(
        IServiceScopeFactory scopeFactory,
        ILogger<PostDownloadTaskScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _workerCount = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        _channel = Channel.CreateBounded<PostDownloadTaskWorkItem>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ValueTask EnqueueAsync(
        string queueUuid,
        string engine,
        Func<IServiceProvider, CancellationToken, Task> workItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueUuid);
        ArgumentNullException.ThrowIfNull(workItem);

        return _channel.Writer.WriteAsync(
            new PostDownloadTaskWorkItem(queueUuid, engine, workItem),
            cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _workerCount)
            .Select(workerId => RunWorkerAsync(workerId, stoppingToken));
        await Task.WhenAll(workers);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await item.Work(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Deferred post-download task canceled. engine={Engine} queue={QueueUuid} worker={WorkerId}",
                    item.Engine,
                    item.QueueUuid,
                    workerId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Deferred post-download task failed. engine={Engine} queue={QueueUuid} worker={WorkerId}",
                    item.Engine,
                    item.QueueUuid,
                    workerId);
            }
        }
    }

    private sealed record PostDownloadTaskWorkItem(
        string QueueUuid,
        string Engine,
        Func<IServiceProvider, CancellationToken, Task> Work);
}
