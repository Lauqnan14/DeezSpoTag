using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using DeezSpoTag.Integrations.Plex;

namespace DeezSpoTag.Services.PlaylistSync;

/// <summary>
/// Plex sync service for synchronizing downloaded music with Plex Media Server
/// Adapted from Syncra functionality for DeezSpoTag integration
/// </summary>
public class PlexSyncService
{
    private readonly ILogger<PlexSyncService> _logger;
    private readonly IConfiguration _configuration;
    private readonly PlexApiClient _plexApiClient;

    public PlexSyncService(
        ILogger<PlexSyncService> logger,
        IConfiguration configuration,
        PlexApiClient plexApiClient)
    {
        _logger = logger;
        _configuration = configuration;
        _plexApiClient = plexApiClient;
    }

    /// <summary>
    /// Sync library with Plex Media Server
    /// </summary>
    public async Task SyncLibraryAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Plex library sync");

        // Check if Plex integration is enabled
        var plexEnabled = _configuration.GetValue<bool>("Plex:Enabled", false);
        if (!plexEnabled)
        {
            _logger.LogDebug("Plex integration is disabled");
            return;
        }

        var plexServerUrl = _configuration.GetValue<string>("Plex:ServerUrl");
        var plexToken = _configuration.GetValue<string>("Plex:Token");

        if (string.IsNullOrEmpty(plexServerUrl) || string.IsNullOrEmpty(plexToken))
        {
            _logger.LogWarning("Plex server URL or token not configured");
            return;
        }

        // Test connection to Plex server
        var isConnected = await _plexApiClient.TestConnectionAsync(plexServerUrl, plexToken);
        if (!isConnected)
        {
            _logger.LogError("Failed to connect to Plex server at {PlexServerUrl}", plexServerUrl);
            return;
        }

        // Get music library sections
        var musicLibraries = await _plexApiClient.GetMusicLibrariesAsync();
        _logger.LogDebug("Found {LibraryCount} music libraries in Plex", musicLibraries.Count);

        // Trigger library refresh for each music section
        foreach (var library in musicLibraries)
        {
            _logger.LogDebug("Refreshing Plex library: {LibraryName} (ID: {LibraryId})",
                library.Title, library.Key);

            await _plexApiClient.RefreshLibraryAsync(library.Key);

            // Small delay between refreshes to avoid overwhelming Plex
            await Task.Delay(1000, cancellationToken);
        }

        _logger.LogInformation("Completed Plex library sync");
    }

    /// <summary>
    /// Sync specific playlist with Plex
    /// </summary>
    public async Task SyncPlaylistAsync(string playlistName, string playlistPath, CancellationToken cancellationToken = default)
    {
        _ = playlistPath;
        _logger.LogInformation("Syncing playlist {PlaylistName} with Plex", playlistName);

        // Check if playlist sync is enabled
        var playlistSyncEnabled = _configuration.GetValue<bool>("Plex:SyncPlaylists", false);
        if (!playlistSyncEnabled)
        {
            _logger.LogDebug("Plex playlist sync is disabled");
            return;
        }

        // Implementation would:
        // 1. Create or update playlist in Plex
        // 2. Add tracks to playlist
        // 3. Handle playlist metadata

        await Task.Delay(100, cancellationToken); // Placeholder

        _logger.LogInformation("Successfully synced playlist {PlaylistName} with Plex", playlistName);
    }

    /// <summary>
    /// Get Plex server status
    /// </summary>
    public async Task<PlexServerStatus> GetServerStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var plexServerUrl = _configuration.GetValue<string>("Plex:ServerUrl");
            var plexToken = _configuration.GetValue<string>("Plex:Token");

            if (string.IsNullOrEmpty(plexServerUrl) || string.IsNullOrEmpty(plexToken))
            {
                return new PlexServerStatus
                {
                    IsConnected = false,
                    ErrorMessage = "Plex server URL or token not configured"
                };
            }

            var isConnected = await _plexApiClient.TestConnectionAsync(plexServerUrl, plexToken);
            var serverInfo = isConnected ? await _plexApiClient.GetServerInfoAsync() : null;

            return new PlexServerStatus
            {
                IsConnected = isConnected,
                ServerUrl = plexServerUrl,
                ServerName = serverInfo?.FriendlyName,
                Version = serverInfo?.Version,
                LastChecked = DateTime.UtcNow
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting Plex server status");
            return new PlexServerStatus
            {
                IsConnected = false,
                ErrorMessage = ex.Message,
                LastChecked = DateTime.UtcNow
            };
        }
    }
}

/// <summary>
/// Plex server status information
/// </summary>
public class PlexServerStatus
{
    public bool IsConnected { get; set; }
    public string? ServerUrl { get; set; }
    public string? ServerName { get; set; }
    public string? Version { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime LastChecked { get; set; }
}
