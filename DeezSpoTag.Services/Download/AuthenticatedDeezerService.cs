using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Services.Download;

/// <summary>
/// Service that manages authenticated Deezer client using centralized session management
/// EXACT PORT: Uses the same singleton DeezerClient instance as the main application
/// This ensures consistent authentication state across all services
/// </summary>
public class AuthenticatedDeezerService
{
    private readonly ILogger<AuthenticatedDeezerService> _logger;
    private readonly DeezerClient _deezerClient;

    public AuthenticatedDeezerService(ILogger<AuthenticatedDeezerService> logger, DeezerClient deezerClient)
    {
        _logger = logger;
        _deezerClient = deezerClient;
    }

    /// <summary>
    /// Ensure the client is authenticated - EXACT PORT: Uses singleton DeezerClient
    /// </summary>
    public Task<bool> EnsureAuthenticatedAsync()
    {
        if (_deezerClient.LoggedIn)
        {
            return Task.FromResult(true);
        }

        _logger.LogWarning("DeezerClient is not authenticated - user needs to login through the web interface");
        return Task.FromResult(false);
    }

    /// <summary>
    /// Get the authenticated Deezer client - EXACT PORT: Returns singleton instance
    /// </summary>
    public Task<DeezerClient?> GetAuthenticatedClientAsync()
    {
        if (_deezerClient.LoggedIn)
        {
            return Task.FromResult<DeezerClient?>(_deezerClient);
        }

        _logger.LogWarning("DeezerClient is not authenticated - user needs to login through the web interface");
        return Task.FromResult<DeezerClient?>(null);
    }

    /// <summary>
    /// Check if client is authenticated - EXACT PORT: Delegates to singleton
    /// </summary>
    public bool IsAuthenticated => _deezerClient.LoggedIn;

    /// <summary>
    /// Manually invalidate authentication - EXACT PORT: Delegates to singleton
    /// </summary>
    public async Task InvalidateAsync()
    {
        await _deezerClient.LogoutAsync();
        _logger.LogInformation("Authentication manually invalidated");
    }

    /// <summary>
    /// Get the current ARL token for downstream services - EXACT PORT: Uses singleton session
    /// </summary>
    public async Task<string?> GetArlAsync()
    {
        // Access the ARL from the singleton DeezerClient's session manager
        // This requires accessing the session manager's cookie container
        try
        {
            if (_deezerClient.LoggedIn)
            {
                var arl = _deezerClient.GetCookieValue("arl");
                if (!string.IsNullOrWhiteSpace(arl))
                {
                    return arl;
                }
            }
            return await GetArlFromLoginStorageAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting ARL token");
            return null;
        }
    }

    public Task<string?> GetSidAsync()
    {
        try
        {
            if (_deezerClient.LoggedIn)
            {
                return Task.FromResult(_deezerClient.GetCookieValue("sid"));
            }

            return Task.FromResult<string?>(null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting Deezer SID");
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Temporary method to get ARL from login storage
    /// </summary>
    private async Task<string?> GetArlFromLoginStorageAsync()
    {
        try
        {
            var configFolder = DeezSpoTag.Services.Authentication.DeezSpoTagConfigPathResolver.GetConfigFolder();
            var loginFilePath = Path.Join(configFolder, "login.json");

            if (!System.IO.File.Exists(loginFilePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(loginFilePath);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("arl", out var arlElement))
            {
                return null;
            }

            return arlElement.ValueKind == JsonValueKind.String
                ? arlElement.GetString()
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error loading ARL from login storage");
            return null;
        }
    }
}
