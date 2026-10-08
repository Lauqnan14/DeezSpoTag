namespace DeezSpoTag.Web.Services.Updates;

/// <summary>The release channel a branch publishes to.</summary>
public enum AppVersionChannel
{
    /// <summary>Not a recognised release branch.</summary>
    Unknown = 0,
    Prerelease = 1,
    Stable = 2
}

/// <summary>
/// Repository coordinates used to report the running version and to look for newer releases.
/// </summary>
public sealed class AppVersionOptions
{
    public const string SectionName = "AppVersion";

    public string Owner { get; set; } = "Lauqnan14";

    public string Repository { get; set; } = "DeezSpoTag";

    /// <summary>
    /// The branch this instance reports its version against, and the only branch whose releases
    /// update checks will ever consider. <c>main</c> today; <c>stable</c> once it exists.
    /// </summary>
    public string Branch { get; set; } = "main";

    /// <summary>
    /// The shortest gap between two outbound GitHub calls. Every trigger (background poll, sidebar
    /// load, manual button) shares this budget so the unauthenticated GitHub rate limit of 60
    /// requests per hour per address is never exhausted.
    /// </summary>
    public int MinimumCheckIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Maps a release branch to the channel it publishes. This is the single place that decides
    /// which releases belong to a branch.
    /// </summary>
    /// <remarks>
    /// The mapping mirrors the publishing workflow and is deliberately an allow-list rather than a
    /// heuristic: an unrecognised branch resolves to <see cref="AppVersionChannel.Unknown"/> so the
    /// caller skips the check entirely instead of falling back to some other branch's releases.
    /// Publishing a release to <c>main</c> tags it <c>-pre</c> and marks it a prerelease; the
    /// stable channel is only produced by a manual dispatch and is a plain tag.
    /// </remarks>
    public static bool TryResolveChannel(string? branch, out AppVersionChannel channel)
    {
        channel = AppVersionChannel.Unknown;
        if (string.IsNullOrWhiteSpace(branch))
        {
            return false;
        }

        switch (branch.Trim().ToLowerInvariant())
        {
            case "main":
                channel = AppVersionChannel.Prerelease;
                return true;
            case "stable":
                channel = AppVersionChannel.Stable;
                return true;
            default:
                return false;
        }
    }
}
