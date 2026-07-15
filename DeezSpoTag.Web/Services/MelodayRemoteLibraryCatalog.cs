using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;

namespace DeezSpoTag.Web.Services;

public sealed record MelodayRemoteLibrary(string Service, string Id, string Name);

public sealed record MelodayRemoteLibrarySnapshot(
    string Service,
    bool Available,
    IReadOnlyList<MelodayRemoteLibrary> Libraries,
    string? Error);

public sealed class MelodayRemoteLibraryCatalog
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly NavidromeApiClient _navidromeApiClient;
    private readonly ILogger<MelodayRemoteLibraryCatalog> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public MelodayRemoteLibraryCatalog(
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient,
        NavidromeApiClient navidromeApiClient,
        ILogger<MelodayRemoteLibraryCatalog> logger)
    {
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
        _navidromeApiClient = navidromeApiClient;
        _logger = logger;
    }

    public Task<MelodayRemoteLibrarySnapshot> GetPlexAsync(
        PlexAuth plex,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
        => GetAsync(
            $"plex|{plex.Url?.TrimEnd('/')}|{plex.MachineIdentifier}|{plex.Token}",
            "plex",
            forceRefresh,
            async token => (await _plexApiClient.GetLibrarySectionsAsync(plex.Url!, plex.Token!, token))
                .Where(static library => string.Equals(library.Type, "artist", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(library.Type, "music", StringComparison.OrdinalIgnoreCase))
                .Where(static library => !string.IsNullOrWhiteSpace(library.Key))
                .Select(static library => new MelodayRemoteLibrary(
                    "plex",
                    library.Key.Trim(),
                    string.IsNullOrWhiteSpace(library.Title) ? library.Key.Trim() : library.Title.Trim()))
                .ToList(),
            cancellationToken);

    public Task<MelodayRemoteLibrarySnapshot> GetJellyfinAsync(
        JellyfinAuth jellyfin,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
        => GetAsync(
            $"jellyfin|{jellyfin.Url?.TrimEnd('/')}|{jellyfin.ServerName}|{jellyfin.ApiKey}",
            "jellyfin",
            forceRefresh,
            async token => (await _jellyfinApiClient.GetLibrariesAsync(jellyfin.Url!, jellyfin.ApiKey!, token))
                .Where(static library => string.Equals(library.CollectionType, "music", StringComparison.OrdinalIgnoreCase))
                .Where(static library => !string.IsNullOrWhiteSpace(library.Id))
                .Select(static library => new MelodayRemoteLibrary(
                    "jellyfin",
                    library.Id!.Trim(),
                    string.IsNullOrWhiteSpace(library.Name) ? library.Id.Trim() : library.Name.Trim()))
                .ToList(),
            cancellationToken);

    public Task<MelodayRemoteLibrarySnapshot> GetNavidromeAsync(
        NavidromeAuth navidrome,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
        => GetAsync(
            $"navidrome|{navidrome.Url?.TrimEnd('/')}|{navidrome.Username}|{navidrome.Password}",
            "navidrome",
            forceRefresh,
            async token => (await _navidromeApiClient.GetLibrariesAsync(
                    navidrome.Url!, navidrome.Username!, navidrome.Password!, token))
                .Where(static library => !string.IsNullOrWhiteSpace(library.Id))
                .Select(static library => new MelodayRemoteLibrary(
                    "navidrome",
                    library.Id.Trim(),
                    string.IsNullOrWhiteSpace(library.Name) ? library.Id.Trim() : library.Name.Trim()))
                .ToList(),
            cancellationToken);

    private async Task<MelodayRemoteLibrarySnapshot> GetAsync(
        string cacheKey,
        string service,
        bool forceRefresh,
        Func<CancellationToken, Task<IReadOnlyList<MelodayRemoteLibrary>>> discover,
        CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh
                && _cache.TryGetValue(cacheKey, out var cached)
                && cached.ExpiresUtc > DateTimeOffset.UtcNow)
            {
                return cached.Snapshot;
            }

            try
            {
                var libraries = (await discover(cancellationToken))
                    .GroupBy(static library => library.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .ToList();
                var snapshot = new MelodayRemoteLibrarySnapshot(
                    service,
                    libraries.Count > 0,
                    libraries,
                    libraries.Count == 0 ? $"{service} returned no discoverable music libraries." : null);
                _cache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow.Add(CacheLifetime), snapshot);
                return snapshot;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Meloday could not discover {Service} music libraries.", service);
                var snapshot = new MelodayRemoteLibrarySnapshot(
                    service,
                    false,
                    Array.Empty<MelodayRemoteLibrary>(),
                    ex.Message);
                _cache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow.AddMinutes(1), snapshot);
                return snapshot;
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private sealed record CacheEntry(DateTimeOffset ExpiresUtc, MelodayRemoteLibrarySnapshot Snapshot);
}
