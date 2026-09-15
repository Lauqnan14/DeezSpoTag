using DeezSpoTag.Integrations.Amazon;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed record WatchlistPublicApiReadiness(
    bool Usable,
    string? Message,
    IReadOnlyList<string> UnavailableSources)
{
    public static WatchlistPublicApiReadiness Ready()
        => new(true, null, Array.Empty<string>());
}

public sealed class WatchlistPublicApiReadinessService
{
    private static readonly HashSet<string> PublicApiSources =
        new(["amazon", "qobuz", "tidal"], StringComparer.OrdinalIgnoreCase);

    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IAmazonPublicProviderRegistry _amazonProviders;
    private readonly IQobuzPublicProviderRegistry _qobuzProviders;
    private readonly ITidalPublicProviderRegistry _tidalProviders;
    private readonly IAmazonDownloadService _amazonDownloads;
    private readonly IQobuzDownloadService _qobuzDownloads;
    private readonly TidalDownloadService _tidalDownloads;

    public WatchlistPublicApiReadinessService(
        DeezSpoTagSettingsService settingsService,
        IAmazonPublicProviderRegistry amazonProviders,
        IQobuzPublicProviderRegistry qobuzProviders,
        ITidalPublicProviderRegistry tidalProviders,
        IAmazonDownloadService amazonDownloads,
        IQobuzDownloadService qobuzDownloads,
        TidalDownloadService tidalDownloads,
        DeezSpoTag.Services.Download.Shared.Models.INotificationSink? notifications = null)
    {
        _notifications = notifications ?? DeezSpoTag.Services.Download.Shared.Models.NullNotificationSink.Instance;
        _settingsService = settingsService;
        _amazonProviders = amazonProviders;
        _qobuzProviders = qobuzProviders;
        _tidalProviders = tidalProviders;
        _amazonDownloads = amazonDownloads;
        _qobuzDownloads = qobuzDownloads;
        _tidalDownloads = tidalDownloads;
    }

    public async Task<WatchlistPublicApiReadiness> EvaluateAsync(
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var configuredSources = DownloadSourceOrder.ResolveAutoSources(settings, includeDeezer: true)
            .Select(DownloadSourceOrder.DecodeAutoSource)
            .Select(static step => step.Source)
            .Where(static source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var unavailable = new List<string>();
        foreach (var source in configuredSources.Where(source => PublicApiSources.Contains(source)))
        {
            var usable = source.ToLowerInvariant() switch
            {
                "amazon" => await HasUsableAmazonProviderAsync(cancellationToken),
                "qobuz" => await HasUsableQobuzProviderAsync(cancellationToken),
                "tidal" => await HasUsableTidalProviderAsync(cancellationToken),
                _ => true
            };
            if (!usable)
            {
                unavailable.Add(source);
            }
        }

        if (configuredSources.Length == 0
            || configuredSources.Any(source => !PublicApiSources.Contains(source))
            || unavailable.Count < configuredSources.Length)
        {
            return WatchlistPublicApiReadiness.Ready();
        }

        return new WatchlistPublicApiReadiness(
            false,
            "Waiting for an enabled download API.",
            unavailable);
    }

    private async Task<bool> HasUsableAmazonProviderAsync(CancellationToken cancellationToken)
    {
        var providers = (await _amazonProviders.CheckEnabledProvidersAsync(cancellationToken))
            .Where(static provider => provider.Enabled
                                      && string.Equals(provider.Status, "online", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (providers.Length == 0)
        {
            return false;
        }

        var requiresVerification = providers.Any(static provider => provider.RequiresVerification);
        var sessionValid = requiresVerification
                           && await _amazonDownloads.HasPublicDownloadSessionAsync(cancellationToken);
        NotifyVerificationRequired("Amazon Music", "amazon", requiresVerification, sessionValid);
        return providers.Any(provider => IsProviderUsable(
            provider.Enabled,
            provider.Status,
            provider.RequiresVerification,
            sessionValid));
    }

    private async Task<bool> HasUsableQobuzProviderAsync(CancellationToken cancellationToken)
    {
        var providers = (await _qobuzProviders.CheckEnabledProvidersAsync(cancellationToken))
            .Where(static provider => provider.Enabled
                                      && string.Equals(provider.Status, "online", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (providers.Length == 0)
        {
            return false;
        }

        var requiresVerification = providers.Any(static provider => provider.RequiresVerification);
        var sessionValid = requiresVerification
                           && await _qobuzDownloads.HasPublicDownloadSessionAsync(cancellationToken);
        NotifyVerificationRequired("Qobuz", "qobuz", requiresVerification, sessionValid);
        return providers.Any(provider => IsProviderUsable(
            provider.Enabled,
            provider.Status,
            provider.RequiresVerification,
            sessionValid));
    }

    private async Task<bool> HasUsableTidalProviderAsync(CancellationToken cancellationToken)
    {
        var providers = (await _tidalProviders.CheckEnabledProvidersAsync(cancellationToken))
            .Where(static provider => provider.Enabled
                                      && string.Equals(provider.Status, "online", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (providers.Length == 0)
        {
            return false;
        }

        var requiresVerification = providers.Any(static provider => provider.RequiresVerification);
        var sessionValid = requiresVerification
                           && await _tidalDownloads.HasPublicDownloadSessionAsync(cancellationToken);
        NotifyVerificationRequired("Tidal", "tidal", requiresVerification, sessionValid);
        return providers.Any(provider => IsProviderUsable(
            provider.Enabled,
            provider.Status,
            provider.RequiresVerification,
            sessionValid));
    }

    private readonly DeezSpoTag.Services.Download.Shared.Models.INotificationSink _notifications;

    private void NotifyVerificationRequired(string platform, string slug, bool anyRequiresVerification, bool sessionValid)
    {
        var key = $"verification_required:{slug}";
        if (!anyRequiresVerification || sessionValid)
        {
            if (sessionValid)
            {
                _notifications.Resolve(
                    key,
                    manuallyResolved: false,
                    $"{platform} public API verified",
                    "Downloads can use it again. No action was needed.");
            }

            return;
        }

        _notifications.Raise(
            "verification_required",
            $"{platform} public API needs verification",
            $"Downloads and watchlist runs using the {platform} public API are blocked until the session is verified in Settings.",
            "ActionRequired",
            key,
            "platform",
            slug);
    }

    internal static bool IsProviderUsable(
        bool enabled,
        string? healthStatus,
        bool requiresVerification,
        bool verificationValid)
        => enabled
           && string.Equals(healthStatus, "online", StringComparison.OrdinalIgnoreCase)
           && (!requiresVerification || verificationValid);
}
