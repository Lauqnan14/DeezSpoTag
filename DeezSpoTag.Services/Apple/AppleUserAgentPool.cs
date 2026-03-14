namespace DeezSpoTag.Services.Apple;

public static class AppleUserAgentPool
{
    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15"
    ];

    /// <summary>
    /// Pinned UA for authenticated requests (Media-User-Token / WebPlayback / license).
    /// Using a consistent UA prevents Apple from seeing each request as a different
    /// device/browser, which was triggering 2FA verification codes.
    /// </summary>
    private static readonly string AuthenticatedUserAgent = UserAgents[0];

    /// <summary>
    /// Random UA for unauthenticated catalog/scrape requests.
    /// </summary>
    public static string GetRandomUserAgent()
    {
        return UserAgents[Random.Shared.Next(UserAgents.Length)];
    }

    /// <summary>
    /// Stable UA for requests that carry user credentials (Media-User-Token, license, WebPlayback).
    /// Must be consistent across all authenticated calls to appear as a single session to Apple.
    /// </summary>
    public static string GetAuthenticatedUserAgent()
    {
        return AuthenticatedUserAgent;
    }
}
