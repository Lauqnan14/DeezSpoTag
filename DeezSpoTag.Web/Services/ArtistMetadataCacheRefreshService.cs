using System.Text.Json;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Metadata.Qobuz;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistMetadataCacheRefreshService
{
    private enum BiographyProvider
    {
        Spotify,
        Apple,
        Tidal,
        Qobuz,
        LastFm
    }

    private static readonly BiographyProvider[] BiographyProviders =
    [
        BiographyProvider.Spotify,
        BiographyProvider.Apple,
        BiographyProvider.Tidal,
        BiographyProvider.Qobuz,
        BiographyProvider.LastFm
    ];
    private readonly LibraryRepository _repository;
    private readonly ArtistArtworkCatalogService _artworkCatalog;
    private readonly SpotifyArtistService _spotify;
    private readonly AppleArtistBiographyService _apple;
    private readonly ITidalAccessTokenProvider _tidalTokens;
    private readonly QobuzArtistService _qobuz;
    private readonly LastFmArtistImageService _lastFm;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<ArtistMetadataCacheRefreshService> _logger;

    public ArtistMetadataCacheRefreshService(
        LibraryRepository repository,
        ArtistArtworkCatalogService artworkCatalog,
        SpotifyArtistService spotify,
        AppleArtistBiographyService apple,
        ITidalAccessTokenProvider tidalTokens,
        QobuzArtistService qobuz,
        LastFmArtistImageService lastFm,
        IHttpClientFactory httpClients,
        ILogger<ArtistMetadataCacheRefreshService> logger)
    {
        _repository = repository;
        _artworkCatalog = artworkCatalog;
        _spotify = spotify;
        _apple = apple;
        _tidalTokens = tidalTokens;
        _qobuz = qobuz;
        _lastFm = lastFm;
        _httpClients = httpClients;
        _logger = logger;
    }

    public async Task<ArtistMetadataCacheRefreshResult> RefreshAsync(
        ArtistMetadataCacheRefreshRequest request,
        IProgress<ArtistMetadataOperationProgress>? progress,
        CancellationToken cancellationToken)
        => await RefreshAsync(request, progress, completedArtistIds: null, cancellationToken);

    public async Task<ArtistMetadataCacheRefreshResult> RefreshAsync(
        ArtistMetadataCacheRefreshRequest request,
        IProgress<ArtistMetadataOperationProgress>? progress,
        IReadOnlySet<long>? completedArtistIds,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return new ArtistMetadataCacheRefreshResult(0, 0, 1, "Library database is not configured.");
        }

        var artists = (await _repository.GetArtistsAsync("all", request.FolderId, cancellationToken))
            .Where(artist => artist.Id > 0 && !string.IsNullOrWhiteSpace(artist.Name))
            .Where(artist => !request.ArtistId.HasValue || artist.Id == request.ArtistId.Value)
            .Where(artist => completedArtistIds is null || !completedArtistIds.Contains(artist.Id))
            .ToList();
        var succeeded = 0;
        var failed = 0;
        for (var index = 0; index < artists.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artist = artists[index];
            progress?.Report(new ArtistMetadataOperationProgress(
                index + 1, artists.Count, artist.Name, null, succeeded, failed));
            try
            {
                await RefreshArtistAsync(
                    artist.Id,
                    artist.Name,
                    request.Source,
                    request.IncludePopularSongs,
                    cancellationToken);
                succeeded++;
                progress?.Report(new ArtistMetadataOperationProgress(
                    index + 1, artists.Count, artist.Name, artist.Id, succeeded, failed));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                progress?.Report(new ArtistMetadataOperationProgress(
                    index + 1, artists.Count, artist.Name, artist.Id, succeeded, failed));
                _logger.LogWarning(ex, "Artist metadata cache refresh failed for artist {ArtistId}.", artist.Id);
            }
        }

        return new ArtistMetadataCacheRefreshResult(artists.Count, succeeded, failed, null);
    }

    public async Task<bool> RefreshArtistAsync(
        long artistId,
        string artistName,
        string? source,
        bool includePopularSongs,
        CancellationToken cancellationToken)
    {
        if (artistId <= 0 || string.IsNullOrWhiteSpace(artistName))
        {
            return false;
        }

        var selectedProvider = ParseProvider(source);
        var normalizedSource = selectedProvider.HasValue
            ? ProviderName(selectedProvider.Value)
            : "auto";
        var artist = await _repository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null)
        {
            return false;
        }
        await _artworkCatalog.RefreshAsync(
            artistId,
            artistName,
            artist.PreferredImagePath,
            cancellationToken,
            normalizedSource == "auto" ? null : normalizedSource,
            forceProviderRefresh: true);
        IReadOnlyList<BiographyProvider> requestedProviders = selectedProvider.HasValue
            ? [selectedProvider.Value]
            : BiographyProviders;

        cancellationToken.ThrowIfCancellationRequested();
        var resolved = await Task.WhenAll(requestedProviders
            .Select(provider => ResolveBiographyAsync(provider, artistId, artistName, cancellationToken)));
        var biographies = new List<(BiographyProvider Provider, string Biography)>();
        for (var index = 0; index < requestedProviders.Count; index++)
        {
            var biography = SanitizeBiography(resolved[index]);
            if (!string.IsNullOrWhiteSpace(biography))
            {
                biographies.Add((requestedProviders[index], biography!));
            }
        }

        if (includePopularSongs
            && selectedProvider.HasValue
            && selectedProvider.Value != BiographyProvider.Spotify)
        {
            await _spotify.GetArtistPageAsync(
                artistId,
                artistName,
                forceRefresh: true,
                forceRematch: false,
                cancellationToken,
                includeDeezerLinking: true);
        }

        var selectedBiographyProvider = biographies.FirstOrDefault().Provider;
        foreach (var biography in biographies)
        {
            var biographySource = ProviderName(biography.Provider);
            await _repository.UpsertArtistBiographyCacheAsync(
                artistId,
                biographySource,
                biography.Biography,
                biography.Provider == selectedBiographyProvider,
                cancellationToken);
        }

        return true;
    }

    private async Task<string?> ResolveBiographyAsync(
        BiographyProvider provider,
        long artistId,
        string artistName,
        CancellationToken cancellationToken)
    {
        try
        {
            return provider switch
            {
                BiographyProvider.Spotify => (await _spotify.GetArtistPageAsync(
                    artistId, artistName, forceRefresh: true, forceRematch: false, cancellationToken))?.Artist?.Biography,
                BiographyProvider.Apple => await ResolveAppleBiographyAsync(artistId, artistName, cancellationToken),
                BiographyProvider.Tidal => await ResolveTidalBiographyAsync(artistId, cancellationToken),
                BiographyProvider.Qobuz => await ResolveQobuzBiographyAsync(artistId, cancellationToken),
                BiographyProvider.LastFm => (await _lastFm.GetArtistBiographyAsync(artistName, cancellationToken))?.Biography,
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported biography provider.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Artist biography provider {Provider} failed for artist {ArtistId}.", provider, artistId);
            return null;
        }
    }

    private async Task<string?> ResolveAppleBiographyAsync(long artistId, string artistName, CancellationToken cancellationToken)
    {
        var appleId = await _repository.GetArtistSourceIdAsync(artistId, "apple", cancellationToken);
        AppleArtistBiographyResult? result;
        if (!string.IsNullOrWhiteSpace(appleId))
        {
            result = await _apple.ResolveByArtistIdAsync(appleId, artistName, cancellationToken);
        }
        else
        {
            var tracks = await _repository.GetArtistTrackTitlesAsync(artistId, 25, cancellationToken);
            result = await _apple.ResolveByExactArtistNameAndTracksAsync(artistName, tracks, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result?.AppleId))
            {
                await _repository.UpsertArtistSourceIdAsync(artistId, "apple", result.AppleId, cancellationToken);
            }
        }

        await _repository.UpdateArtistAppleBiographyAsync(artistId, result?.Biography, DateTimeOffset.UtcNow, cancellationToken);
        return result?.Biography;
    }

    private async Task<string?> ResolveQobuzBiographyAsync(long artistId, CancellationToken cancellationToken)
    {
        var sourceId = await _repository.GetArtistSourceIdAsync(artistId, "qobuz", cancellationToken);
        if (!int.TryParse(sourceId, out var qobuzId) || qobuzId <= 0)
        {
            return null;
        }

        var artist = await _qobuz.GetArtistWithDiscographyAsync(qobuzId, "us-en", cancellationToken);
        return FirstNonEmpty(artist?.Biography?.Content, artist?.Biography?.Summary);
    }

    private async Task<string?> ResolveTidalBiographyAsync(long artistId, CancellationToken cancellationToken)
    {
        var sourceId = await _repository.GetArtistSourceIdAsync(artistId, "tidal", cancellationToken);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        var token = await _tidalTokens.GetAccessTokenAsync(cancellationToken);
        var country = await _tidalTokens.GetCountryCodeAsync(cancellationToken) ?? "US";
        var url = $"https://openapi.tidal.com/v2/artists/{Uri.EscapeDataString(sourceId)}?countryCode={Uri.EscapeDataString(country)}&include=biography&collapseBy=FINGERPRINT";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClients.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !TryGetRelationshipId(data, "biography", out var biographyId)
            || !document.RootElement.TryGetProperty("included", out var included))
        {
            return null;
        }

        foreach (var item in included.EnumerateArray())
        {
            if (GetString(item, "id") != biographyId
                || !item.TryGetProperty("attributes", out var attributes))
            {
                continue;
            }

            return GetString(attributes, "text");
        }

        return null;
    }

    private static bool TryGetRelationshipId(JsonElement root, string name, out string id)
    {
        id = string.Empty;
        if (!root.TryGetProperty("relationships", out var relationships)
            || !relationships.TryGetProperty(name, out var relationship)
            || !relationship.TryGetProperty("data", out var data))
        {
            return false;
        }

        var value = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().FirstOrDefault() : data;
        id = GetString(value, "id") ?? string.Empty;
        return id.Length > 0;
    }

    private static BiographyProvider? ParseProvider(string? source)
    {
        var normalized = (source ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "spotify" => BiographyProvider.Spotify,
            "apple" => BiographyProvider.Apple,
            "tidal" => BiographyProvider.Tidal,
            "qobuz" => BiographyProvider.Qobuz,
            "lastfm" => BiographyProvider.LastFm,
            _ => null
        };
    }

    private static string ProviderName(BiographyProvider provider)
        => provider switch
        {
            BiographyProvider.Spotify => "spotify",
            BiographyProvider.Apple => "apple",
            BiographyProvider.Tidal => "tidal",
            BiographyProvider.Qobuz => "qobuz",
            BiographyProvider.LastFm => "lastfm",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported biography provider.")
        };

    private static string? SanitizeBiography(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? GetString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed record ArtistMetadataCacheRefreshRequest(
    long? ArtistId,
    long? FolderId,
    string? Source,
    bool IncludePopularSongs = false);
public sealed record ArtistMetadataCacheRefreshResult(int Total, int Succeeded, int Failed, string? Error);
public sealed record ArtistMetadataOperationProgress(int Processed, int Total, string? CurrentArtist, long? CompletedArtistId = null, int Succeeded = 0, int Failed = 0);
