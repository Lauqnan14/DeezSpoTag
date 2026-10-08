using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Services.Notifications;
using Microsoft.Extensions.Options;

namespace DeezSpoTag.Web.Services.Updates;

/// <summary>The result of comparing the running build against the configured branch's newest release.</summary>
/// <param name="CurrentVersion">The running build version, for example <c>v0.1.27.5</c>.</param>
/// <param name="Branch">The branch the version is reported against.</param>
/// <param name="BranchUrl">The URL of that branch in the repository.</param>
/// <param name="RepositoryUrl">The public repository URL.</param>
/// <param name="LatestVersion">The newest release tag on that branch, or <see langword="null"/> when unknown.</param>
/// <param name="LatestUrl">The release page URL, when known.</param>
/// <param name="UpdateAvailable">Whether <paramref name="LatestVersion"/> is strictly newer.</param>
/// <param name="CheckedUtc">When the answer was produced.</param>
/// <param name="IsStale">Whether the answer is a cached one served inside the minimum check interval.</param>
public sealed record AppVersionStatus(
    string CurrentVersion,
    string Branch,
    string BranchUrl,
    string RepositoryUrl,
    string? LatestVersion,
    string? LatestUrl,
    bool UpdateAvailable,
    DateTimeOffset? CheckedUtc,
    bool IsStale);

/// <summary>
/// Reports the running version, finds the newest release on the configured branch, and announces a
/// new release once per tag through the existing notification system.
/// </summary>
/// <remarks>
/// Every trigger shares one outbound-call budget. A check that runs inside
/// <see cref="AppVersionOptions.MinimumCheckIntervalMinutes"/> of the previous outbound call
/// returns the cached answer instead of calling GitHub, which keeps this inside the
/// unauthenticated GitHub rate limit no matter how many clients poll.
/// </remarks>
public sealed class UpdateCheckService
{
    private const int MinIntervalMinutesFloor = 1;

    private readonly GitHubReleaseClient _releaseClient;
    private readonly AppVersionInfo _versionInfo;
    private readonly IOptions<AppVersionOptions> _options;
    private readonly INotificationSink _notifications;
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AppVersionStatus? _cached;
    private string? _announcedTag;

    public UpdateCheckService(
        GitHubReleaseClient releaseClient,
        AppVersionInfo versionInfo,
        IOptions<AppVersionOptions> options,
        INotificationSink notifications,
        ILogger<UpdateCheckService> logger)
    {
        _releaseClient = releaseClient;
        _versionInfo = versionInfo;
        _options = options;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>
    /// Returns the current version plus, when the branch is recognised, the newest release on that
    /// branch. A request made inside the minimum check interval returns the cached answer and is
    /// flagged <see cref="AppVersionStatus.IsStale"/>, so a manual check reports when the answer
    /// was produced rather than forcing another outbound call.
    /// </summary>
    public async Task<AppVersionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var branch = _versionInfo.Branch;
        if (!AppVersionOptions.TryResolveChannel(branch, out var channel))
        {
            _logger.LogWarning(
                "Skipping the update check: branch '{Branch}' is not a recognised release branch.",
                branch);
            return BuildStatus(latestVersion: null, latestUrl: null, updateAvailable: false, checkedUtc: null, isStale: false);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && !HasIntervalElapsed(_cached.CheckedUtc))
            {
                return _cached with { IsStale = true };
            }

            var release = await _releaseClient.GetLatestReleaseAsync(
                _versionInfo.RepositoryOwner,
                _versionInfo.RepositoryName,
                channel,
                cancellationToken);

            if (release is null)
            {
                // Keep the previous answer rather than clearing it, so a transient GitHub failure
                // does not make an available update disappear from the sidebar.
                if (_cached is not null)
                {
                    return _cached with { IsStale = true };
                }

                return BuildStatus(latestVersion: null, latestUrl: null, updateAvailable: false, checkedUtc: null, isStale: false);
            }

            var updateAvailable = AppVersionComparison.IsNewer(_versionInfo.CurrentVersion, release.Tag);
            var status = BuildStatus(release.Tag, release.Url, updateAvailable, DateTimeOffset.UtcNow, isStale: false);
            _cached = status;

            if (updateAvailable)
            {
                AnnounceOnce(status, release);
            }

            return status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Update check failed.");
            return _cached ?? BuildStatus(latestVersion: null, latestUrl: null, updateAvailable: false, checkedUtc: null, isStale: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Raises a notification the first time a given release tag is seen as available.</summary>
    private void AnnounceOnce(AppVersionStatus status, GitHubReleaseSummary release)
    {
        var tag = release.Tag;
        if (string.IsNullOrWhiteSpace(tag) || string.Equals(_announcedTag, tag, StringComparison.Ordinal))
        {
            return;
        }

        _announcedTag = tag;
        _notifications.Raise(
            NotificationKinds.AppUpdateAvailable,
            title: $"DeezSpoTag {tag} is available",
            body: $"You are running {_versionInfo.CurrentVersion}. {tag} was published to the {status.Branch} branch.",
            severity: "Info",
            dedupeKey: $"{NotificationKinds.AppUpdateAvailable}:{tag}",
            entityType: "app_version",
            entityId: tag,
            link: release.Url);
    }

    private bool HasIntervalElapsed(DateTimeOffset? checkedUtc)
    {
        if (checkedUtc is null)
        {
            return true;
        }

        var minutes = Math.Max(_options.Value.MinimumCheckIntervalMinutes, MinIntervalMinutesFloor);
        return DateTimeOffset.UtcNow - checkedUtc.Value >= TimeSpan.FromMinutes(minutes);
    }

    private AppVersionStatus BuildStatus(
        string? latestVersion,
        string? latestUrl,
        bool updateAvailable,
        DateTimeOffset? checkedUtc,
        bool isStale)
        => new(
            _versionInfo.CurrentVersion,
            _versionInfo.Branch,
            _versionInfo.BranchUrl,
            _versionInfo.RepositoryUrl,
            latestVersion,
            latestUrl,
            updateAvailable,
            checkedUtc,
            isStale);
}
