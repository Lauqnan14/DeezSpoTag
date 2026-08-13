using System;
using System.Diagnostics;
using System.IO;
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
    public void TryEnqueue_ReturnsFalseInsteadOfBlockingWhenQueueIsFull()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        using var scheduler = new PostDownloadTaskScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PostDownloadTaskScheduler>.Instance);

        var stopwatch = Stopwatch.StartNew();
        var rejected = false;
        for (var i = 0; i < 600; i++)
        {
            if (!scheduler.TryEnqueue(
                    $"queued-{i}",
                    "apple",
                    static (_, _) => Task.Delay(TimeSpan.FromMinutes(5))))
            {
                rejected = true;
                break;
            }
        }

        stopwatch.Stop();
        Assert.True(rejected, "The bounded post-download queue did not report saturation.");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"TryEnqueue blocked for {stopwatch.Elapsed}.");
    }

    [Fact]
    public void DownloadPrefetch_UsesNonBlockingSchedulerAdmission()
    {
        var root = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Join(
            root,
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "EngineAudioPostDownloadHelper.cs"));

        Assert.Contains("request.TaskScheduler.TryEnqueue(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await request.TaskScheduler.EnqueueAsync(", source, StringComparison.Ordinal);
    }

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
            Assert.True(processed.Task.IsCompletedSuccessfully);
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await scheduler.StopAsync(stopTimeout.Token);
        }
    }
}
