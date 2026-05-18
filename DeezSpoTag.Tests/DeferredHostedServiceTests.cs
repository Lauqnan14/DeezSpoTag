using System;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Runtime;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeferredHostedServiceTests
{
    [Fact]
    public async Task StopAsync_StopsServiceThatFinishesStartingWhileStopWaits()
    {
        var lifetime = new TestHostApplicationLifetime();
        var coordinator = new BackgroundWorkCoordinator();
        var innerService = new SlowStartHostedService();
        var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddSingleton(coordinator)
            .AddSingleton(innerService)
            .BuildServiceProvider();

        var deferred = new DeferredHostedService<SlowStartHostedService>(
            services,
            lifetime,
            coordinator,
            NullLogger<DeferredHostedService<SlowStartHostedService>>.Instance);

        await deferred.StartAsync(CancellationToken.None);
        lifetime.Start();
        coordinator.ReleaseBackgroundWorkers();

        await innerService.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopTask = deferred.StopAsync(CancellationToken.None);
        innerService.AllowStartToComplete.SetResult();

        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(innerService.StopCalled.Task.IsCompletedSuccessfully);
    }

    private sealed class SlowStartHostedService : IHostedService
    {
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStartToComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StopCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartEntered.SetResult();
            await AllowStartToComplete.Task.WaitAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCalled.SetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
        }

        public void Start()
        {
            _started.Cancel();
        }
    }
}
