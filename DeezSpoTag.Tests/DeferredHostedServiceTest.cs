using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Runtime;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeferredHostedServiceTest
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

    [Fact]
    public async Task FaultedExecuteTaskAfterStart_IsObservedAndLoggedOnce()
    {
        var lifetime = new TestHostApplicationLifetime();
        var coordinator = new BackgroundWorkCoordinator();
        var inner = new FaultingBackgroundService(token => throw new InvalidOperationException("watch cycle exploded"));
        var logger = new DeferredServiceLogger();
        var deferred = CreateDeferred(lifetime, coordinator, inner, logger);

        await deferred.StartAsync(CancellationToken.None);
        lifetime.Start();
        coordinator.ReleaseBackgroundWorkers();
        await inner.ExecuteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitAsyncLoggedAsync(logger, LogLevel.Error);

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains(typeof(FaultingBackgroundService).FullName ?? "service", StringComparison.Ordinal)
            && entry.Exception is InvalidOperationException);
        // The wrapper itself must not fault: the fault is observed, not propagated.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await deferred.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShutdownCancellationAfterStart_IsNotLoggedAsFailure()
    {
        var lifetime = new TestHostApplicationLifetime();
        var coordinator = new BackgroundWorkCoordinator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new FaultingBackgroundService(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var logger = new DeferredServiceLogger();
        var deferred = CreateDeferred(lifetime, coordinator, inner, logger);

        await deferred.StartAsync(CancellationToken.None);
        lifetime.Start();
        coordinator.ReleaseBackgroundWorkers();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifetime.StopApplication();
        await inner.ExecuteFaulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        await deferred.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task UnexpectedCancellationAfterStart_IsLoggedButIsNotAnError()
    {
        var lifetime = new TestHostApplicationLifetime();
        var coordinator = new BackgroundWorkCoordinator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new FaultingBackgroundService(_ =>
        {
            entered.TrySetResult();
            // Cancellation of an internal token, not application shutdown.
            return Task.FromException(new OperationCanceledException("unexpected internal cancellation"));
        });
        var logger = new DeferredServiceLogger();
        var deferred = CreateDeferred(lifetime, coordinator, inner, logger);

        await deferred.StartAsync(CancellationToken.None);
        lifetime.Start();
        coordinator.ReleaseBackgroundWorkers();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitAsyncLoggedAsync(logger, LogLevel.Warning);

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        lifetime.StopApplication();
        await deferred.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FiniteCompletionAfterStart_IsNotAnError()
    {
        var lifetime = new TestHostApplicationLifetime();
        var coordinator = new BackgroundWorkCoordinator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new FaultingBackgroundService(_ => { entered.TrySetResult(); return Task.CompletedTask; });
        var logger = new DeferredServiceLogger();
        var deferred = CreateDeferred(lifetime, coordinator, inner, logger);

        await deferred.StartAsync(CancellationToken.None);
        lifetime.Start();
        coordinator.ReleaseBackgroundWorkers();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await inner.ExecuteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        lifetime.StopApplication();
        await deferred.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OrdinaryHostedServiceFault_IsNotObservedByTheWrapper()
    {
        var lifetime = new TestHostApplicationLifetime();
        var coordinator = new BackgroundWorkCoordinator();
        var inner = new OrdinaryHostedService();
        var logger = new DeferredServiceLogger();
        var deferred = CreateDeferred(lifetime, coordinator, inner, logger);

        await deferred.StartAsync(CancellationToken.None);
        lifetime.Start();
        coordinator.ReleaseBackgroundWorkers();
        await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // An ordinary IHostedService has no ExecuteTask for the wrapper to watch;
        // the host owns those faults, exactly as before this repair.
        await Task.Delay(100);
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains("while running after startup", StringComparison.Ordinal)
            || entry.Message.Contains("cancelled unexpectedly after startup", StringComparison.Ordinal));
        lifetime.StopApplication();
        await deferred.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(inner.Stopped.Task.IsCompletedSuccessfully);
    }

    private sealed class OrdinaryHostedService : IHostedService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StartFailure_IsStillLoggedAsBefore()
    {
        var lifetime = new TestHostApplicationLifetime();
        var coordinator = new BackgroundWorkCoordinator();
        var inner = new FaultingStartHostedService();
        var logger = new DeferredServiceLogger();
        var deferred = CreateDeferred(lifetime, coordinator, inner, logger);

        await deferred.StartAsync(CancellationToken.None);
        lifetime.Start();
        coordinator.ReleaseBackgroundWorkers();
        await inner.StartFaulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains("failed to start", StringComparison.Ordinal));
        lifetime.StopApplication();
        await deferred.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static DeferredHostedService<TService> CreateDeferred<TService>(
        TestHostApplicationLifetime lifetime,
        BackgroundWorkCoordinator coordinator,
        TService service,
        DeferredServiceLogger logger)
        where TService : class, IHostedService
    {
        var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddSingleton(coordinator)
            .AddSingleton(service)
            .BuildServiceProvider();
        return new DeferredHostedService<TService>(
            services,
            lifetime,
            coordinator,
            new ForwardingLogger<DeferredHostedService<TService>>(logger));
    }

    private static async Task WaitAsyncLoggedAsync(DeferredServiceLogger logger, LogLevel level)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (logger.Entries.Any(entry => entry.Level == level)) return;
            await Task.Delay(20);
        }
        Assert.Fail($"Expected a {level} log entry; saw: [{string.Join(", ", logger.Entries.Select(entry => $"{entry.Level}: {entry.Message}"))}]");
    }

    private sealed class ForwardingLogger<T> : ILogger<T>
    {
        private readonly DeferredServiceLogger _sink;
        public ForwardingLogger(DeferredServiceLogger sink) => _sink = sink;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _sink.Entries.Add(new DeferredServiceLogger.Entry(logLevel, formatter(state, exception), exception));
    }

    private sealed class DeferredServiceLogger
    {
        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);
        public List<Entry> Entries { get; } = new();
    }

    private class FaultingBackgroundService : BackgroundService
    {
        private readonly Func<CancellationToken, Task> _behavior;

        public TaskCompletionSource ExecuteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExecuteFaulted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExecuteCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FaultingBackgroundService(Func<CancellationToken, Task> behavior) => _behavior = behavior;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                ExecuteEntered.TrySetResult();
                await _behavior(stoppingToken);
                ExecuteCompleted.TrySetResult();
            }
            catch
            {
                ExecuteFaulted.TrySetResult();
                throw;
            }
        }
    }

    private sealed class FaultingStartHostedService : IHostedService
    {
        public TaskCompletionSource StartFaulted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var faulted = Task.FromException(new InvalidOperationException("deferred start failure"));
            StartFaulted.TrySetResult();
            return faulted;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
