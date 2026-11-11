using DeezSpoTag.Services.Runtime;

namespace DeezSpoTag.Web.Services;

public sealed class DeferredHostedService<TService> : IHostedService
    where TService : class, IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly BackgroundWorkCoordinator _workCoordinator;
    private readonly ILogger<DeferredHostedService<TService>> _logger;
    private readonly object _stateLock = new();
    private TService? _service;
    private Task? _startTask;

    public DeferredHostedService(
        IServiceProvider serviceProvider,
        IHostApplicationLifetime applicationLifetime,
        BackgroundWorkCoordinator workCoordinator,
        ILogger<DeferredHostedService<TService>> logger)
    {
        _serviceProvider = serviceProvider;
        _applicationLifetime = applicationLifetime;
        _workCoordinator = workCoordinator;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_applicationLifetime.ApplicationStarted.IsCancellationRequested)
        {
            StartDeferredService();
            return Task.CompletedTask;
        }

        _applicationLifetime.ApplicationStarted.Register(StartDeferredService);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? startTask;
        TService? service;
        lock (_stateLock)
        {
            startTask = _startTask;
            service = _service;
        }

        if (startTask is not null)
        {
            try
            {
                await startTask.WaitAsync(cancellationToken);
                lock (_stateLock)
                {
                    service = _service;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (service is not null)
        {
            await service.StopAsync(cancellationToken);
        }
    }

    private void StartDeferredService()
    {
        lock (_stateLock)
        {
            if (_startTask is not null)
            {
                return;
            }

            _startTask = StartDeferredServiceAsync();
        }
    }

    private async Task StartDeferredServiceAsync()
    {
        try
        {
            await _workCoordinator.WaitForStartupGraceAsync(_applicationLifetime.ApplicationStopping);
            var service = _serviceProvider.GetRequiredService<TService>();
            lock (_stateLock)
            {
                _service = service;
            }

            await service.StartAsync(_applicationLifetime.ApplicationStopping);
            ObserveBackgroundServiceFaults(service);
        }
        catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Normal shutdown while deferred service is starting.
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogError(ex, "Deferred hosted service {ServiceName} failed to start.", typeof(TService).FullName);
        }
    }

    /// <summary>
    /// A deferred BackgroundService's ExecuteTask faults outside StartAsync, and until
    /// now nothing observed it: a worker that died minutes after startup stayed
    /// invisible. Attach a detached monitor to the real ExecuteTask after the start
    /// has completed and record asynchronous faults. Ordinary (non-BackgroundService)
    /// hosted services keep their host-owned behavior, and StopAsync still sends stop
    /// without waiting for normal worker completion.
    /// </summary>
    private void ObserveBackgroundServiceFaults(TService service)
    {
        if (service is not BackgroundService background)
        {
            return;
        }

        _ = ObserveExecuteTaskAsync(background);
    }

    private async Task ObserveExecuteTaskAsync(BackgroundService background)
    {
        var executeTask = background.ExecuteTask;
        if (executeTask is null)
        {
            return;
        }

        try
        {
            await executeTask;
        }
        catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Deferred hosted service {ServiceName} stopped with the application shutdown.",
                typeof(TService).FullName);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Deferred hosted service {ServiceName} was cancelled unexpectedly after startup.",
                typeof(TService).FullName);
        }
        // Filtered, not broad: cancellation is handled above, so this arm logs
        // only real failures, as the broad-catch guardrail requires.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Deferred hosted service {ServiceName} failed while running after startup.",
                typeof(TService).FullName);
        }
    }
}
