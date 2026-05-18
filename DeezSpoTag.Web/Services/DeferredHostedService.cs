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
        }
        catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Normal shutdown while deferred service is starting.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deferred hosted service {ServiceName} failed to start.", typeof(TService).FullName);
        }
    }
}
