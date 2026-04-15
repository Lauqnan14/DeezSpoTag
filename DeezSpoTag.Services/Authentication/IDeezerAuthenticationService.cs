namespace DeezSpoTag.Services.Authentication;

/// <summary>
/// Unified interface for Deezer authentication service
/// Combines both core authentication methods and API-compatible methods
/// </summary>
public interface IDeezerAuthenticationService
{
    /// <summary>
    /// Login with email and password
    /// </summary>
    Task<AuthenticationResult> LoginWithEmailPasswordAsync(string email, string password);

    /// <summary>
    /// Login with ARL token
    /// </summary>
    Task<AuthenticationResult> LoginWithArlAsync(string arl);

    /// <summary>
    /// Get current login status
    /// </summary>
    Task<AuthenticationResult> GetLoginStatusAsync();

    /// <summary>
    /// Logout and clear credentials
    /// </summary>
    Task LogoutAsync();

    /// <summary>
    /// Get stored login credentials
    /// </summary>
    Task<LoginCredentials> GetLoginCredentialsAsync();

    /// <summary>
    /// Check if user is currently logged in
    /// </summary>
    Task<bool> IsLoggedInAsync();

    // API-compatible methods (merged from API project)

    /// <summary>
    /// API-compatible login method (returns object for API compatibility)
    /// </summary>
    Task<object> LoginAsync(string arl, int? child = null);

    /// <summary>
    /// Get access token from email and password (API-compatible)
    /// </summary>
    Task<string?> GetAccessTokenFromEmailPasswordAsync(string email, string password);

    /// <summary>
    /// Get ARL from access token (API-compatible)
    /// </summary>
    Task<string?> GetArlFromAccessTokenAsync(string accessToken);

    /// <summary>
    /// Clear session data (API-compatible)
    /// </summary>
    Task ClearSessionAsync();
}