using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;

namespace DeezSpoTag.Web.Services;

public sealed class MediaServerLibraryRefreshService
{
    private const string PlexService = "plex";
    private const string JellyfinService = "jellyfin";
    private const string NoneService = "none";

    private readonly PlatformAuthService _authService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly ILogger<MediaServerLibraryRefreshService> _logger;

    public MediaServerLibraryRefreshService(
        PlatformAuthService authService,
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient,
        ILogger<MediaServerLibraryRefreshService> logger)
    {
        _authService = authService;
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
        _logger = logger;
    }

    public async Task RefreshAsync(string? service, CancellationToken cancellationToken)
    {
        var normalizedService = (service ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedService == NoneService)
        {
            return;
        }

        var state = await _authService.LoadAsync();
        if (normalizedService == JellyfinService)
        {
            await RefreshJellyfinAsync(state.Jellyfin, cancellationToken);
            return;
        }

        if (normalizedService == PlexService)
        {
            await RefreshPlexAsync(state.Plex, cancellationToken);
            return;
        }

        if (state.Plex is { } plex
            && !string.IsNullOrWhiteSpace(plex.Url)
            && !string.IsNullOrWhiteSpace(plex.Token))
        {
            await RefreshPlexAsync(plex, cancellationToken);
            return;
        }

        await RefreshJellyfinAsync(state.Jellyfin, cancellationToken);
    }

    private async Task RefreshPlexAsync(PlexAuth? plex, CancellationToken cancellationToken)
    {
        if (plex == null
            || string.IsNullOrWhiteSpace(plex.Url)
            || string.IsNullOrWhiteSpace(plex.Token))
        {
            return;
        }

        var sections = await _plexApiClient.GetLibrarySectionsAsync(
            plex.Url,
            plex.Token,
            cancellationToken);
        var musicSections = sections
            .Where(section => string.Equals(section.Type, "artist", StringComparison.OrdinalIgnoreCase))
            .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var section in musicSections)
        {
            var refreshed = await _plexApiClient.RefreshLibraryAsync(
                plex.Url,
                plex.Token,
                section.Key,
                cancellationToken);
            if (!refreshed)
            {
                _logger.LogWarning(
                    "Plex library refresh request failed for section {SectionKey} ({SectionTitle}).",
                    section.Key,
                    section.Title);
            }
        }
    }

    private async Task RefreshJellyfinAsync(JellyfinAuth? jellyfin, CancellationToken cancellationToken)
    {
        if (jellyfin == null
            || string.IsNullOrWhiteSpace(jellyfin.Url)
            || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
        {
            return;
        }

        var refreshed = await _jellyfinApiClient.RefreshLibraryAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            cancellationToken);
        if (!refreshed)
        {
            _logger.LogWarning("Jellyfin library refresh request failed.");
        }
    }
}
