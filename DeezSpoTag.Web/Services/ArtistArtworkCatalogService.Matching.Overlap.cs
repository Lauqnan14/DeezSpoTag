using DeezSpoTag.Core.Models.Deezer;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Album-overlap based artist-identity matching for Deezer/Qobuz/Tidal, mirroring the
/// Spotify matcher's philosophy: find artists by name, then accept a candidate only
/// when its own discography overlaps the library's local album titles.
/// </summary>
public sealed partial class ArtistArtworkCatalogService
{
    private const int OverlapCandidateLimit = 5;
    private const int OverlapAlbumFetchLimit = 100;

    private async Task<IReadOnlyList<string>> GetLocalAlbumTitlesForOverlapAsync(
        long artistId,
        CancellationToken cancellationToken)
    {
        if (artistId <= 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            var albums = await _repository.GetArtistAlbumsAsync(artistId, cancellationToken);
            return albums
                .Select(album => album.Title ?? string.Empty)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not load local album titles for overlap matching. artist={ArtistId}", artistId);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Searches the platform for artist-name candidates and accepts the one whose
    /// discography overlaps the local album titles. When overlap is required, a
    /// candidate with zero overlap is rejected even if the name matches.
    /// </summary>
    private async Task<string?> MatchArtistIdByAlbumOverlapAsync(
        string source,
        string artistName,
        IReadOnlyList<string> localAlbumTitles,
        bool requireOverlap,
        CancellationToken cancellationToken)
    {
        var candidates = await SearchPlatformArtistCandidatesAsync(source, artistName, cancellationToken);
        if (candidates.Count == 0)
        {
            return null;
        }

        var nameMatches = candidates
            .Where(candidate => ArtistIdentityTextNormalizer.NamesEquivalent(artistName, candidate.Name))
            .Take(OverlapCandidateLimit)
            .ToList();

        string? bestId = null;
        var bestOverlap = 0;
        var bestAlbumCount = 0;
        foreach (var (candidateId, _, rank) in nameMatches.Select((candidate, rank) => (candidate.Id, candidate.Name, rank)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var albumTitles = await FetchPlatformArtistAlbumTitlesAsync(source, candidateId, cancellationToken);
            if (albumTitles is null)
            {
                continue;
            }

            var overlap = ArtistIdentityTextNormalizer.CountAlbumOverlap(localAlbumTitles, albumTitles);
            var albumCount = albumTitles.Count;
            if (overlap > bestOverlap
                || (overlap == bestOverlap && overlap > 0 && albumCount > bestAlbumCount))
            {
                bestOverlap = overlap;
                bestAlbumCount = albumCount;
                bestId = candidateId;
            }

            if (bestOverlap > 0 && rank >= OverlapCandidateLimit - 1)
            {
                break;
            }
        }

        if (bestId is not null && (bestOverlap > 0 || !requireOverlap))
        {
            return bestId;
        }

        if (requireOverlap && nameMatches.Count > 0)
        {
            _logger.LogInformation(
                "Artist {Source} name candidates found but none overlapped the local albums; no id stored.",
                source);
        }

        return null;
    }

    private async Task<List<(string Id, string Name)>> SearchPlatformArtistCandidatesAsync(
        string source,
        string artistName,
        CancellationToken cancellationToken)
        => source.ToLowerInvariant() switch
        {
            "deezer" => await SearchDeezerArtistCandidatesAsync(artistName, cancellationToken),
            "qobuz" => await SearchQobuzArtistCandidatesAsync(artistName, cancellationToken),
            "tidal" => await SearchTidalArtistCandidatesAsync(artistName, cancellationToken),
            _ => []
        };

    private async Task<List<string>?> FetchPlatformArtistAlbumTitlesAsync(
        string source,
        string candidateId,
        CancellationToken cancellationToken)
        => source.ToLowerInvariant() switch
        {
            "deezer" => await FetchDeezerArtistAlbumTitlesAsync(candidateId, cancellationToken),
            "qobuz" => await FetchQobuzArtistAlbumTitlesAsync(candidateId, cancellationToken),
            "tidal" => await FetchTidalArtistAlbumTitlesAsync(candidateId, cancellationToken),
            _ => null
        };

    private async Task<List<(string Id, string Name)>> SearchDeezerArtistCandidatesAsync(
        string artistName,
        CancellationToken cancellationToken)
    {
        try
        {
            var search = await _deezer
                .SearchArtistAsync(artistName, new ApiOptions { Limit = 10 })
                .WaitAsync(cancellationToken);
            var candidates = new List<(string, string)>();
            foreach (var raw in search.Data ?? Array.Empty<object>())
            {
                if (raw is not JObject obj)
                {
                    continue;
                }

                var id = obj["id"]?.ToString()?.Trim();
                var name = obj["name"]?.Value<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                {
                    candidates.Add((id, name));
                }
            }

            return candidates;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer artist search failed for {ArtistName}.", artistName);
            return [];
        }
    }

    private async Task<List<string>?> FetchDeezerArtistAlbumTitlesAsync(
        string candidateId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClients
                .CreateClient()
                .GetAsync($"https://api.deezer.com/artist/{Uri.EscapeDataString(candidateId)}/albums?limit={OverlapAlbumFetchLimit}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return payload["data"]?
                .OfType<JObject>()
                .Select(album => album["title"]?.Value<string>()?.Trim())
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Cast<string>()
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer artist albums fetch failed for {ArtistId}.", candidateId);
            return null;
        }
    }

    private async Task<List<(string Id, string Name)>> SearchQobuzArtistCandidatesAsync(
        string artistName,
        CancellationToken cancellationToken)
    {
        try
        {
            var artists = await _qobuzMetadata.SearchArtists(artistName, cancellationToken);
            return artists
                .Where(artist => artist.Id > 0 && !string.IsNullOrWhiteSpace(artist.Name))
                .Select(artist => (artist.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), artist.Name!.Trim()))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Qobuz artist search failed for {ArtistName}.", artistName);
            return [];
        }
    }

    private async Task<List<string>?> FetchQobuzArtistAlbumTitlesAsync(
        string candidateId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(candidateId, out var qobuzId) || qobuzId <= 0)
        {
            return null;
        }

        try
        {
            var artist = await _qobuz.GetArtistWithDiscographyAsync(qobuzId, "us-en", cancellationToken);
            return artist?.Albums?.Items
                .Select(album => album.Title?.Trim())
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Cast<string>()
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Qobuz artist albums fetch failed for {ArtistId}.", candidateId);
            return null;
        }
    }

    private async Task<List<(string Id, string Name)>> SearchTidalArtistCandidatesAsync(
        string artistName,
        CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = await _tidalTokens.GetAccessTokenAsync(cancellationToken);
            var country = await _tidalTokens.GetCountryCodeAsync(cancellationToken) ?? "US";
            var url =
                $"https://api.tidal.com/v1/search/artists?query={Uri.EscapeDataString(artistName)}&limit=10&offset=0&countryCode={Uri.EscapeDataString(country)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClients.CreateClient().SendAsync(request, cancellationToken);
            ArtistMetadataProviderGate.ThrowIfRateLimited(response);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var candidates = new List<(string, string)>();
            foreach (var item in items.EnumerateArray())
            {
                if (TryReadTidalArtist(item, out var candidateId, out var candidateName))
                {
                    candidates.Add((candidateId, candidateName));
                }
            }

            return candidates;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ArtistMetadataProviderGate.IsRateLimited(ex))
        {
            _logger.LogDebug(ex, "Tidal artist search failed for {ArtistName}.", artistName);
            return [];
        }
    }

    private async Task<List<string>?> FetchTidalArtistAlbumTitlesAsync(
        string candidateId,
        CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = await _tidalTokens.GetAccessTokenAsync(cancellationToken);
            var country = await _tidalTokens.GetCountryCodeAsync(cancellationToken) ?? "US";
            var url =
                $"https://api.tidal.com/v1/artists/{Uri.EscapeDataString(candidateId)}/albums?countryCode={Uri.EscapeDataString(country)}&limit={OverlapAlbumFetchLimit}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClients.CreateClient().SendAsync(request, cancellationToken);
            ArtistMetadataProviderGate.ThrowIfRateLimited(response);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var titles = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString()?.Trim() : null;
                if (!string.IsNullOrWhiteSpace(title))
                {
                    titles.Add(title);
                }
            }

            return titles;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ArtistMetadataProviderGate.IsRateLimited(ex))
        {
            _logger.LogDebug(ex, "Tidal artist albums fetch failed for {ArtistId}.", candidateId);
            return null;
        }
    }

    /// <summary>
    /// Revalidates stored Deezer/Qobuz/Tidal IDs against the local album titles when
    /// the provider is about to be queried anyway. A stored ID with zero album overlap
    /// is treated as stale and rematched (mirrors the Spotify stored-id check).
    /// </summary>
    private async Task RevalidateStoredPlatformArtistIdsAsync(
        long artistId,
        string artistName,
        HashSet<string> rematchedProviders,
        Func<string, bool> providerWillBeQueried,
        CancellationToken cancellationToken)
    {
        var localAlbums = ArtistIdentityTextNormalizer.FilterResolvableTitles(
            await GetLocalAlbumTitlesForOverlapAsync(artistId, cancellationToken));
        if (!ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(localAlbums))
        {
            return;
        }

        foreach (var source in new[] { "deezer", "qobuz", "tidal" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rematchedProviders.Contains(source) || !providerWillBeQueried(source))
            {
                continue;
            }

            var storedId = await _repository.GetArtistSourceIdAsync(artistId, source, cancellationToken);
            if (string.IsNullOrWhiteSpace(storedId))
            {
                continue;
            }

            var storedAlbumTitles = await FetchPlatformArtistAlbumTitlesAsync(source, storedId, cancellationToken);
            if (storedAlbumTitles is null)
            {
                // API failure is not evidence of a bad id; keep the stored id.
                continue;
            }

            if (ArtistIdentityTextNormalizer.CountAlbumOverlap(localAlbums, storedAlbumTitles) > 0)
            {
                continue;
            }

            _logger.LogWarning(
                "Stored {Source} artist id {StoredId} has no local album overlap; rematching. artist={ArtistId}",
                source,
                storedId,
                artistId);

            var replacement = await MatchArtistIdByAlbumOverlapAsync(
                source,
                artistName,
                localAlbums,
                requireOverlap: true,
                cancellationToken);
            if (replacement is not null
                && await ReplaceSourceIdIfChangedAsync(artistId, source, replacement, cancellationToken))
            {
                rematchedProviders.Add(source);
            }
            else if (replacement is null)
            {
                await _repository.RemoveArtistSourceIdAsync(artistId, source, storedId, cancellationToken);
                _logger.LogWarning(
                    "Removed stored {Source} artist id {StoredId}; no candidate shares a local album. artist={ArtistId}",
                    source,
                    storedId,
                    artistId);
            }
        }
    }
}
