using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DeezSpoTag.Web.Services;

public sealed class DeezerConnectionStateNotifier : IHostedService
{
    public const string EventName = "deezerConnectionStateChanged";
    public const string PublicDownloadSessionEventName = "publicDownloadSessionStateChanged";

    private readonly DeezerSessionManager _sessionManager;
    private readonly ZarzSignedSessionCoordinator _zarzSessions;
    private readonly IHubContext<CrossDeviceSyncHub> _hubContext;
    private readonly ILogger<DeezerConnectionStateNotifier> _logger;

    public DeezerConnectionStateNotifier(
        DeezerSessionManager sessionManager,
        ZarzSignedSessionCoordinator zarzSessions,
        IHubContext<CrossDeviceSyncHub> hubContext,
        ILogger<DeezerConnectionStateNotifier> logger)
    {
        _sessionManager = sessionManager;
        _zarzSessions = zarzSessions;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.ConnectionStateChanged += HandleConnectionStateChanged;
        _zarzSessions.StateChanged += HandlePublicDownloadSessionStateChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.ConnectionStateChanged -= HandleConnectionStateChanged;
        _zarzSessions.StateChanged -= HandlePublicDownloadSessionStateChanged;
        return Task.CompletedTask;
    }

    private void HandleConnectionStateChanged(
        object? sender,
        DeezerConnectionStateChangedEventArgs eventArgs)
    {
        _ = PublishAsync(eventArgs.State);
    }

    private void HandlePublicDownloadSessionStateChanged(
        object? sender,
        ZarzSessionStateChangedEventArgs eventArgs)
    {
        _ = PublishPublicDownloadSessionAsync(eventArgs);
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

    private async Task PublishPublicDownloadSessionAsync(ZarzSessionStateChangedEventArgs state)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync(PublicDownloadSessionEventName, new
            {
                provider = state.Provider,
                connected = state.IsUsable,
                verificationRequired = state.VerificationRequired
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to publish public download session state transition.");
        }
    }
}
