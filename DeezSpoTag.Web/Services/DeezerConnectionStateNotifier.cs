using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DeezSpoTag.Web.Services;

public sealed class DeezerConnectionStateNotifier : IHostedService
{
    public const string EventName = "deezerConnectionStateChanged";

    private readonly DeezerSessionManager _sessionManager;
    private readonly IHubContext<CrossDeviceSyncHub> _hubContext;
    private readonly ILogger<DeezerConnectionStateNotifier> _logger;

    public DeezerConnectionStateNotifier(
        DeezerSessionManager sessionManager,
        IHubContext<CrossDeviceSyncHub> hubContext,
        ILogger<DeezerConnectionStateNotifier> logger)
    {
        _sessionManager = sessionManager;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.ConnectionStateChanged += HandleConnectionStateChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.ConnectionStateChanged -= HandleConnectionStateChanged;
        return Task.CompletedTask;
    }

    private void HandleConnectionStateChanged(
        object? sender,
        DeezerConnectionStateChangedEventArgs eventArgs)
    {
        _ = PublishAsync(eventArgs.State);
    }

    private async Task PublishAsync(DeezerConnectionState state)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync(EventName, new
            {
                state = state.ToString().ToLowerInvariant()
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to publish Deezer connection state transition.");
        }
    }
}
