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
        TidalDownloadService tidalDownloads)
    {
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
        if (configuredSources.Length == 0
            || configuredSources.Any(source => !PublicApiSources.Contains(source)))
        {
            return WatchlistPublicApiReadiness.Ready();
        }

        var unavailable = new List<string>();
        foreach (var source in configuredSources)
        {
            var usable = source.ToLowerInvariant() switch
            {
                "amazon" => await HasUsableAmazonProviderAsync(cancellationToken),
                "qobuz" => await HasUsableQobuzProviderAsync(cancellationToken),
                "tidal" => await HasUsableTidalProviderAsync(cancellationToken),
                _ => true
            };
            if (usable)
            {
                return WatchlistPublicApiReadiness.Ready();
            }

            unavailable.Add(source);
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

        var sessionValid = providers.Any(static provider => provider.RequiresVerification)
                           && await _amazonDownloads.HasPublicDownloadSessionAsync(cancellationToken);
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

        var sessionValid = providers.Any(static provider => provider.RequiresVerification)
                           && await _qobuzDownloads.HasPublicDownloadSessionAsync(cancellationToken);
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

        var sessionValid = providers.Any(static provider => provider.RequiresVerification)
                           && await _tidalDownloads.HasPublicDownloadSessionAsync(cancellationToken);
        return providers.Any(provider => IsProviderUsable(
            provider.Enabled,
            provider.Status,
            provider.RequiresVerification,
            sessionValid));
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
