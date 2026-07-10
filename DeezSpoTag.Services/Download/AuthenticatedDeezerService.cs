using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Services.Download;

/// <summary>
/// Service that manages authenticated Deezer client using centralized session management
/// EXACT PORT: Uses the same singleton DeezerClient instance as the main application
/// This ensures consistent authentication state across all services
/// </summary>
public class AuthenticatedDeezerService
{
    private static readonly TimeSpan SavedArlLoginTimeout = TimeSpan.FromSeconds(8);
    private readonly ILogger<AuthenticatedDeezerService> _logger;
    private readonly DeezerClient _deezerClient;
    private readonly ILoginStorageService _loginStorage;
    private readonly SemaphoreSlim _authGate = new(1, 1);

    public AuthenticatedDeezerService(
        ILogger<AuthenticatedDeezerService> logger,
        DeezerClient deezerClient,
        ILoginStorageService loginStorage)
    {
        _logger = logger;
        _deezerClient = deezerClient;
        _loginStorage = loginStorage;
    }

    /// <summary>
    /// Ensure the client is authenticated - EXACT PORT: Uses singleton DeezerClient
    /// </summary>
    public async Task<bool> EnsureAuthenticatedAsync()
    {
        if (_deezerClient.LoggedIn)
        {
            return true;
        }

        await _authGate.WaitAsync();
        try
        {
            if (_deezerClient.LoggedIn)
            {
                return true;
            }

            var loginData = await _loginStorage.LoadLoginCredentialsAsync();
            var arl = DeezerAuthUtils.NormalizeArl(loginData?.Arl);
            if (string.IsNullOrWhiteSpace(arl) || !DeezerAuthUtils.IsValidArlLength(arl))
            {
                _logger.LogWarning("DeezerClient is not authenticated and no valid saved ARL is available.");
                return false;
            }

            var loggedIn = await _deezerClient.LoginWithArlAsync(arl).WaitAsync(SavedArlLoginTimeout);
            if (!loggedIn)
            {
                _logger.LogWarning("Saved Deezer ARL could not authenticate the Deezer client.");
                return false;
            }

            _logger.LogInformation("Authenticated Deezer client from saved ARL.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to authenticate Deezer client from saved login credentials.");
            return false;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Timed out authenticating Deezer client from saved login credentials.");
            return false;
        }
        finally
        {
            _authGate.Release();
        }
    }

    /// <summary>
    /// Get the authenticated Deezer client - EXACT PORT: Returns singleton instance
    /// </summary>
    public async Task<DeezerClient?> GetAuthenticatedClientAsync()
    {
        if (await EnsureAuthenticatedAsync())
        {
            return _deezerClient;
        }

        _logger.LogWarning("DeezerClient is not authenticated - user needs to login through the web interface");
        return null;
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
            var loginData = await _loginStorage.LoadLoginCredentialsAsync();
            return loginData?.Arl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error loading ARL from login storage");
            return null;
        }
    }
}
