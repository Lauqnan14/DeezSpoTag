using System;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PostDownloadTaskSchedulerTests
{
    [Fact]
    public async Task EnqueuedWork_ContinuesAfterTaskCanceledException()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        using var scheduler = new PostDownloadTaskScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PostDownloadTaskScheduler>.Instance);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 20; i++)
            {
                await scheduler.EnqueueAsync(
                    $"cancel-{i}",
                    "apple",
                    static (_, _) => Task.FromException(new TaskCanceledException("Simulated artwork timeout")),
                    CancellationToken.None);
            }

            var processed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await scheduler.EnqueueAsync(
                "success-item",
                "apple",
                (_, _) =>
                {
                    processed.TrySetResult(true);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await processed.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await scheduler.StopAsync(stopTimeout.Token);
        }
    }
}
