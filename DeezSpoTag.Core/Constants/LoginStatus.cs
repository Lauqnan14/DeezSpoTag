namespace DeezSpoTag.Core.Constants;

/// <summary>
/// Login status constants
/// Exact port from: /deezspotag/webui/src/server/routes/api/post/loginArl.ts
/// </summary>
public static class LoginStatus
{
    /// <summary>
    /// Deezer is not available in this region
    /// </summary>
    public const int NOT_AVAILABLE = -1;

    /// <summary>
    /// Login failed (invalid credentials or other error)
    /// </summary>
    public const int FAILED = 0;

    /// <summary>
    /// Login successful
    /// </summary>
    public const int SUCCESS = 1;

    /// <summary>
    /// Already logged in with valid session
    /// </summary>
    public const int ALREADY_LOGGED = 2;

    /// <summary>
    /// Forced success (used in specific scenarios)
    /// </summary>
    public const int FORCED_SUCCESS = 3;
}