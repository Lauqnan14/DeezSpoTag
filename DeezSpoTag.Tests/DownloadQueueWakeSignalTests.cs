using System;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadQueueWakeSignalTests
{
    [Fact]
    public async Task PulseBeforeWait_IsPreserved()
    {
        var signal = new DownloadQueueWakeSignal();

        signal.Pulse();

        var startedAt = DateTimeOffset.UtcNow;
        await signal.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RepeatedPulses_AreCoalescedWithoutThrowing()
    {
        var signal = new DownloadQueueWakeSignal();

        signal.Pulse();
        signal.Pulse();

        await signal.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
    }

    [Fact]
    public async Task GateOpenPulse_ProcessesTwoPendingItemsWithoutRecoveryDelay()
    {
        var signal = new DownloadQueueWakeSignal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var gateOpen = false;
        var pendingItems = 2;
        var processedItems = 0;
        var bothProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var loop = QueueProcessingLoop.RunAsync(
            "test",
            _ =>
            {
                if (!gateOpen || pendingItems == 0)
                {
                    return Task.CompletedTask;
                }

                pendingItems--;
                processedItems++;
                if (pendingItems > 0)
                {
                    signal.Pulse();
                }
                else
                {
                    bothProcessed.TrySetResult();
                }

                return Task.CompletedTask;
            },
            NullLogger.Instance,
            TimeSpan.FromMinutes(1),
            signal,
            cancellation.Token);

        await Task.Delay(50, cancellation.Token);
        gateOpen = true;
        signal.Pulse();

        await bothProcessed.Task.WaitAsync(cancellation.Token);
        Assert.Equal(2, processedItems);
        await cancellation.CancelAsync();
        await loop;
    }
}
