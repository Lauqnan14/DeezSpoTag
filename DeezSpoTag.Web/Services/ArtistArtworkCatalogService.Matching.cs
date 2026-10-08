using System.Text.Json;
using DeezSpoTag.Core.Models.Deezer;
using Newtonsoft.Json.Linq;

namespace DeezSpoTag.Web.Services;

public sealed partial class ArtistArtworkCatalogService
{
    private async Task<HashSet<string>> EnsureMatchedSourceIdsAsync(
        long artistId,
        string artistName,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? lookupNames = null)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var titles = await LoadLocalTitlesAsync(artistId, cancellationToken);
        if (titles.Count == 0)
        {
            return changed;
        }

        // Existing name-based discovery for an Artist Alias artist: the canonical name first,
        // then an alias only when that name produced nothing usable. A single-entry list keeps
        // every non-alias artist on exactly today's single-name lookup.
        var names = lookupNames is { Count: > 1 } ? lookupNames : [artistName];
        var localAlbumTitles = await GetLocalAlbumTitlesForOverlapAsync(artistId, cancellationToken);
        var resolvableAlbums = ArtistIdentityTextNormalizer.FilterResolvableTitles(localAlbumTitles);
        var requireOverlap = ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(resolvableAlbums);
        var providerGate = new ArtistMetadataProviderGate(_logger);

        if (await MatchSourceIdSafelyAsync(
                artistId,
                SpotifySource,
                async token =>
                {
                    var previousId = await _repository.GetArtistSourceIdAsync(artistId, SpotifySource, token);
                    await _spotify.EnsureSpotifyArtistIdAsync(artistId, artistName, token);
                    var nextId = await _repository.GetArtistSourceIdAsync(artistId, SpotifySource, token);
                    if (string.Equals(previousId, nextId, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    await _repository.DeleteArtistArtworkCacheBySourceAsync(artistId, SpotifySource, token);
                    return true;
                },
                providerGate,
                cancellationToken))
        {
            changed.Add(SpotifySource);
        }

        if (await MatchSourceIdSafelyAsync(
                artistId,
                DeezerSource,
                async token => await ReplaceSourceIdIfChangedAsync(
                    artistId,
                    DeezerSource,
                    await MatchDeezerArtistIdAsync(names, titles, token)
                    ?? await MatchArtistIdByAlbumOverlapAsync(DeezerSource, names, resolvableAlbums, requireOverlap, token),
                    token),
                providerGate,
                cancellationToken))
        {
            changed.Add(DeezerSource);
        }

        if (await MatchSourceIdSafelyAsync(
                artistId,
                AppleSource,
                async token => await ReplaceSourceIdIfChangedAsync(
                    artistId,
                    AppleSource,
                    await _apple.ResolveArtistIdFromLocalTracksAsync(names, titles, token),
                    token),
                providerGate,
                cancellationToken))
        {
            changed.Add(AppleSource);
            changed.Add("itunes");
        }

        if (await MatchSourceIdSafelyAsync(
                artistId,
                TidalSource,
                async token => await ReplaceSourceIdIfChangedAsync(
                    artistId,
                    TidalSource,
                    await MatchTidalArtistIdAsync(names, titles, token)
                    ?? await MatchArtistIdByAlbumOverlapAsync(TidalSource, names, resolvableAlbums, requireOverlap, token),
                    token),
                providerGate,
                cancellationToken))
        {
            changed.Add(TidalSource);
        }

        if (await MatchSourceIdSafelyAsync(
                artistId,
                QobuzSource,
                async token => await ReplaceSourceIdIfChangedAsync(
                    artistId,
                    QobuzSource,
                    await MatchQobuzArtistIdAsync(names, titles, token)
                    ?? await MatchArtistIdByAlbumOverlapAsync(QobuzSource, names, resolvableAlbums, requireOverlap, token),
                    token),
                providerGate,
                cancellationToken))
        {
            changed.Add(QobuzSource);
        }

        return changed;
    }

    private async Task<bool> MatchSourceIdSafelyAsync(
        long artistId,
        string provider,
        Func<CancellationToken, Task<bool>> match,
        ArtistMetadataProviderGate providerGate,
        CancellationToken cancellationToken)
    {
        try
        {
            return await providerGate.RunAsync(provider, match, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Artist source matcher {Provider} failed for artist {ArtistId}.",
                provider,
                artistId);
            return false;
        }
    }

    private async Task<IReadOnlyList<string>> LoadLocalTitlesAsync(long artistId, CancellationToken cancellationToken)
    {
        var titles = (await _repository.GetArtistTrackTitlesAsync(artistId, 8, cancellationToken))
            .Concat((await _repository.GetArtistAlbumsAsync(artistId, cancellationToken)).Select(album => album.Title))
            .Select(title => (title ?? string.Empty).Trim())
            .Where(title => title.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        return titles;
    }

    private async Task<bool> ReplaceSourceIdIfChangedAsync(
        long artistId,
        string source,
        string? matchedId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(matchedId))
        {
            return false;
        }

        var stored = await _repository.GetArtistSourceIdAsync(artistId, source, cancellationToken);
        if (string.Equals(stored, matchedId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await _repository.UpsertArtistSourceIdAsync(artistId, source, matchedId, cancellationToken);
        await _repository.DeleteArtistArtworkCacheBySourceAsync(artistId, source, cancellationToken);
        if (string.Equals(source, AppleSource, StringComparison.OrdinalIgnoreCase))
        {
            await _repository.DeleteArtistArtworkCacheBySourceAsync(artistId, "itunes", cancellationToken);
        }

        return true;
    }

    private async Task<string?> MatchDeezerArtistIdAsync(
        IReadOnlyList<string> lookupNames,
        IReadOnlyList<string> titles,
        CancellationToken cancellationToken)
    {
        foreach (var artistName in lookupNames)
        {
            var matched = await MatchDeezerArtistIdForNameAsync(artistName, titles, cancellationToken);
            if (!string.IsNullOrWhiteSpace(matched))
            {
                return matched;
            }
        }

        return null;
    }

    private async Task<string?> MatchDeezerArtistIdForNameAsync(
        string artistName,
        IReadOnlyList<string> titles,
        CancellationToken cancellationToken)
    {
        var normalizedArtist = artistName.Trim();
        foreach (var title in titles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var search = await _deezer.SearchTrackAsync(
                $"{normalizedArtist} {title}",
                new ApiOptions { Limit = 10, Strict = true }).WaitAsync(cancellationToken);
            foreach (var raw in search.Data ?? Array.Empty<object>())
            {
                var obj = raw as JObject ?? (raw is JToken tokenValue ? tokenValue as JObject : null);
                var artistObj = obj?["artist"] as JObject;
                var candidateName = artistObj?["name"]?.Value<string>()?.Trim();
                var candidateId = artistObj?["id"]?.ToString()?.Trim();
                var trackTitle = obj?["title"]?.Value<string>()?.Trim();
                var albumTitle = (obj?["album"] as JObject)?["title"]?.Value<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(candidateId)
                    && ArtistNameAcceptsCandidate(normalizedArtist, candidateName)
                    && (ArtistIdentityTextNormalizer.TitleMatchesAny(trackTitle, titles, isTrackTitle: true)
                        || ArtistIdentityTextNormalizer.TitleMatchesAny(albumTitle, titles, isTrackTitle: false)))
                {
                    return candidateId;
                }
            }
        }

        return null;
    }

    private async Task<string?> MatchQobuzArtistIdAsync(
        IReadOnlyList<string> lookupNames,
        IReadOnlyList<string> titles,
        CancellationToken cancellationToken)
    {
        foreach (var artistName in lookupNames)
        {
            var matched = await MatchQobuzArtistIdForNameAsync(artistName, titles, cancellationToken);
            if (!string.IsNullOrWhiteSpace(matched))
            {
                return matched;
            }
        }

        return null;
    }

    private async Task<string?> MatchQobuzArtistIdForNameAsync(
        string artistName,
        IReadOnlyList<string> titles,
        CancellationToken cancellationToken)
    {
        var normalizedArtist = artistName.Trim();
        foreach (var title in titles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tracks = await _qobuzMetadata.SearchTracks($"{normalizedArtist} {title}", cancellationToken);
            foreach (var track in tracks)
            {
                var candidateName = track.Performer?.Name?.Trim();
                var candidateId = track.Performer?.Id;
                if (candidateId > 0
                    && ArtistNameAcceptsCandidate(normalizedArtist, candidateName)
                    && (ArtistIdentityTextNormalizer.TitleMatchesAny(track.Title?.Trim(), titles, isTrackTitle: true)
                        || ArtistIdentityTextNormalizer.TitleMatchesAny(track.Album?.Title?.Trim(), titles, isTrackTitle: false)))
                {
                    return candidateId.ToString();
                }
            }
        }

        return null;
    }

    private async Task<string?> MatchTidalArtistIdAsync(
        IReadOnlyList<string> lookupNames,
        IReadOnlyList<string> titles,
        CancellationToken cancellationToken)
    {
        foreach (var artistName in lookupNames)
        {
            var matched = await MatchTidalArtistIdForNameAsync(artistName, titles, cancellationToken);
            if (!string.IsNullOrWhiteSpace(matched))
            {
                return matched;
            }
        }

        return null;
    }

    private async Task<string?> MatchTidalArtistIdForNameAsync(
        string artistName,
        IReadOnlyList<string> titles,
        CancellationToken cancellationToken)
    {
        var accessToken = await _tidalTokens.GetAccessTokenAsync(cancellationToken);
        var country = await _tidalTokens.GetCountryCodeAsync(cancellationToken) ?? "US";
        var normalizedArtist = artistName.Trim();
        foreach (var title in titles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url =
                $"https://api.tidal.com/v1/search/tracks?query={Uri.EscapeDataString($"{normalizedArtist} {title}")}&limit=10&offset=0&countryCode={Uri.EscapeDataString(country)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClients.CreateClient().SendAsync(request, cancellationToken);
            ArtistMetadataProviderGate.ThrowIfRateLimited(response);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                var trackTitle = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString()?.Trim() : null;
                if (!TryReadTidalArtist(item, out var candidateId, out var candidateName))
                {
                    continue;
                }

                if (ArtistNameAcceptsCandidate(normalizedArtist, candidateName)
                    && ArtistIdentityTextNormalizer.TitleMatchesAny(trackTitle, titles, isTrackTitle: true))
                {
                    return candidateId;
                }
            }
        }

        return null;
    }

    private static bool TryReadTidalArtist(JsonElement track, out string artistId, out string artistName)
    {
        artistId = string.Empty;
        artistName = string.Empty;
        if (track.TryGetProperty("artist", out var artist) && artist.ValueKind == JsonValueKind.Object)
        {
            artistId = artist.TryGetProperty("id", out var idElement) ? idElement.ToString() : string.Empty;
            artistName = artist.TryGetProperty("name", out var nameElement) ? nameElement.GetString()?.Trim() ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(artistId) && !string.IsNullOrWhiteSpace(artistName))
            {
                return true;
            }
        }

        if (!track.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in artists.EnumerateArray())
        {
            artistId = item.TryGetProperty("id", out var idElement) ? idElement.ToString() : string.Empty;
            artistName = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString()?.Trim() ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(artistId) && !string.IsNullOrWhiteSpace(artistName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Shared artist-name acceptance for the platform matchers: alias-equivalent
    /// (diacritic/case/punctuation tolerant) or a canonical prefix of the other side.
    /// </summary>
    private static bool ArtistNameAcceptsCandidate(string localName, string? candidateName)
    {
        if (ArtistIdentityTextNormalizer.NamesEquivalent(localName, candidateName))
        {
            return true;
        }

        var localKey = ArtistIdentityTextNormalizer.NormalizeNameWhitespace(localName);
        var candidateKey = ArtistIdentityTextNormalizer.NormalizeNameWhitespace(candidateName);
        if (localKey.Length == 0 || candidateKey.Length == 0)
        {
            return false;
        }

        return candidateKey.StartsWith(localKey + " ", StringComparison.OrdinalIgnoreCase)
               || localKey.StartsWith(candidateKey + " ", StringComparison.OrdinalIgnoreCase);
    }
}
