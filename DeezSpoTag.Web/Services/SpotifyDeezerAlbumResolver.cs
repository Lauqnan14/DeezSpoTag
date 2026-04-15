using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models.Deezer;
using DeezerGwTrack = DeezSpoTag.Core.Models.Deezer.GwTrack;
using DeezSpoTag.Integrations.Deezer;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyDeezerAlbumResolver
{
    private const string AlbumType = "album";
    private const string ArtistType = "artist";
    private const string TitleKey = "title";
    private const string NameKey = "name";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly DeezerClient _deezerClient;
    private readonly ILogger<SpotifyDeezerAlbumResolver> _logger;

    public SpotifyDeezerAlbumResolver(
        DeezerClient deezerClient,
        ILogger<SpotifyDeezerAlbumResolver> logger)
    {
        _deezerClient = deezerClient;
        _logger = logger;
    }

    public async Task<string?> ResolveAlbumIdAsync(SpotifyUrlMetadata metadata, CancellationToken cancellationToken)
    {
        var deezerId = await ResolveAlbumIdFromTracksAsync(metadata);
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            deezerId = await ResolveAlbumIdByTrackSearchAsync(metadata);
        }

        if (string.IsNullOrWhiteSpace(deezerId))
        {
            var query = BuildAlbumQuery(metadata.Name, metadata.Subtitle);
            deezerId = await ResolveAlbumIdByTrackOverlapAsync(query, metadata);
            if (string.IsNullOrWhiteSpace(deezerId))
            {
                deezerId = await ResolveAlbumIdByAlbumSearchAsync(query, metadata.Name, metadata.Subtitle);
            }
        }

        if (string.IsNullOrWhiteSpace(deezerId))
        {
            deezerId = await ResolveAlbumIdByTrackOverlapAsync(metadata.Name, metadata);
            if (string.IsNullOrWhiteSpace(deezerId))
            {
                deezerId = await ResolveAlbumIdByAlbumSearchAsync(metadata.Name, metadata.Name, metadata.Subtitle);
            }
        }

        return deezerId;
    }

    private async Task<string?> ResolveAlbumIdFromTracksAsync(SpotifyUrlMetadata metadata)
    {
        var albumName = NormalizeToken(metadata.Name);
        var artistName = NormalizeToken(metadata.Subtitle);
        var candidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in metadata.TrackList.Where(track => !string.IsNullOrWhiteSpace(track.Isrc)).Take(8))
        {
            try
            {
                var deezerTrack = await _deezerClient.GetTrackByIsrcAsync(track.Isrc!);
                var deezerAlbumId = deezerTrack?.Album?.Id.ToString();
                if (string.IsNullOrWhiteSpace(deezerAlbumId) || deezerAlbumId == "0")
                {
                    continue;
                }

                if (!MatchesExactToken(albumName, deezerTrack?.Album?.Title)
                    || !MatchesExactToken(artistName, deezerTrack?.Artist?.Name))
                {
                    continue;
                }

                IncrementCandidate(candidates, deezerAlbumId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "ISRC album match failed.");
            }
        }

        return candidates.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();
    }

    private async Task<string?> ResolveAlbumIdByTrackSearchAsync(SpotifyUrlMetadata metadata)
    {
        if (metadata.TrackList.Count == 0)
        {
            return null;
        }

        var targetAlbum = NormalizeToken(metadata.Name);
        var targetArtist = NormalizeToken(metadata.Subtitle);
        var candidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in BuildTrackSearchQueries(metadata))
        {
            var result = await TrySearchTrackAsync(query);
            if (result?.Data == null)
            {
                continue;
            }

            foreach (var albumId in EnumerateMatchingTrackSearchAlbumIds(result, targetAlbum, targetArtist))
            {
                IncrementCandidate(candidates, albumId);
            }
        }

        return candidates.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();
    }

    private async Task<string?> ResolveAlbumIdByTrackOverlapAsync(string query, SpotifyUrlMetadata metadata)
    {
        if (metadata.TrackList.Count == 0)
        {
            return null;
        }

        var spotifyTracks = metadata.TrackList
            .Select(track => NormalizeTrackToken(track.Name))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToList();

        if (spotifyTracks.Count == 0)
        {
            return null;
        }

        var result = await TrySearchAlbumAsync(query, 8);
        if (result?.Data == null || result.Data.Length == 0)
        {
            return null;
        }

        string? bestAlbumId = null;
        var bestScore = 0;

        foreach (var item in result.Data.Take(5))
        {
            var overlapCandidate = await GetOverlapCandidateAsync(item, spotifyTracks);
            if (overlapCandidate == null)
            {
                continue;
            }
            if (overlapCandidate.Value.Score > bestScore)
            {
                bestScore = overlapCandidate.Value.Score;
                bestAlbumId = overlapCandidate.Value.AlbumId;
            }
        }

        if (bestAlbumId == null)
        {
            return null;
        }

        var minTracks = Math.Max(1, Math.Min(spotifyTracks.Count, 6));
        return bestScore >= Math.Min(3, minTracks) ? bestAlbumId : null;
    }

    private async Task<string?> ResolveAlbumIdByAlbumSearchAsync(
        string query,
        string albumName,
        string? artistName)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var result = await TrySearchAlbumAsync(query, 10);
        if (result == null)
        {
            return null;
        }

        return SelectBestAlbumId(result, albumName, artistName) ?? SelectFirstId(result);
    }

    private static string BuildAlbumQuery(string name, string? artist)
    {
        var safeName = StripQuotes(name);
        if (string.IsNullOrWhiteSpace(artist))
        {
            return safeName;
        }

        var safeArtist = StripQuotes(artist);
        return $"{safeName} {safeArtist}".Trim();
    }

    private static string? SelectBestAlbumId(DeezerSearchResult result, string albumName, string? artistName)
    {
        if (result.Data == null || result.Data.Length == 0)
        {
            return null;
        }

        var targetTitle = NormalizeToken(albumName);
        var targetArtist = NormalizeToken(artistName);
        foreach (var item in result.Data)
        {
            if (item is not JsonElement element)
            {
                continue;
            }

            var title = JsonElementReader.GetString(element, TitleKey) ?? JsonElementReader.GetString(element, NameKey);
            var artist = GetNestedString(element, ArtistType, NameKey) ?? JsonElementReader.GetString(element, ArtistType);
            if (MatchesExactToken(targetTitle, title) && MatchesOptionalExactToken(targetArtist, artist))
            {
                return JsonElementReader.GetString(element, "id");
            }
        }

        return null;
    }

    private static string? SelectFirstId(DeezerSearchResult result)
    {
        if (result.Data == null || result.Data.Length == 0)
        {
            return null;
        }

        var first = result.Data[0];
        return first is JsonElement element ? JsonElementReader.GetString(element, "id") : null;
    }

    private static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = normalized.Replace("–", "-").Replace("—", "-");
        normalized = Regex.Replace(normalized, @"\(.*?\)", string.Empty, RegexOptions.None, RegexTimeout);
        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", " ", RegexOptions.None, RegexTimeout).Trim();
        return normalized;
    }

    private static string NormalizeTrackToken(string? value)
    {
        var normalized = NormalizeToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        normalized = Regex.Replace(normalized, @"\b(feat|ft)\b.*", string.Empty, RegexOptions.None, RegexTimeout).Trim();
        return normalized;
    }

    private static string StripQuotes(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? value : value.Replace("\"", string.Empty);
    }

    private static void IncrementCandidate(Dictionary<string, int> candidates, string albumId)
    {
        candidates[albumId] = candidates.TryGetValue(albumId, out var count) ? count + 1 : 1;
    }

    private static bool MatchesExactToken(string target, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return true;
        }

        var normalizedCandidate = NormalizeToken(candidate);
        return !string.IsNullOrWhiteSpace(normalizedCandidate) && normalizedCandidate == target;
    }

    private static bool MatchesOptionalExactToken(string target, string? candidate)
    {
        return string.IsNullOrWhiteSpace(target) || MatchesExactToken(target, candidate);
    }

    private static string? GetNestedString(JsonElement element, string objectName, string propertyName)
    {
        if (!element.TryGetProperty(objectName, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JsonElementReader.GetString(nested, propertyName);
    }

    private static IEnumerable<string> BuildTrackSearchQueries(SpotifyUrlMetadata metadata)
    {
        return metadata.TrackList
            .Take(5)
            .Select(track => StripQuotes($"{track.Name} {track.Artists}".Trim()))
            .Where(static query => !string.IsNullOrWhiteSpace(query));
    }

    private async Task<DeezerSearchResult?> TrySearchTrackAsync(string query)
    {
        try
        {
            return await _deezerClient.SearchTrackAsync(query, new ApiOptions { Limit = 5 });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Track search failed for album match.");
            return null;
        }
    }

    private static IEnumerable<string> EnumerateMatchingTrackSearchAlbumIds(
        DeezerSearchResult result,
        string targetAlbum,
        string targetArtist)
    {
        foreach (var item in result.Data!.Take(3))
        {
            if (item is not JsonElement element)
            {
                continue;
            }

            var albumId = GetNestedString(element, AlbumType, "id") ?? JsonElementReader.GetString(element, "album_id");
            if (string.IsNullOrWhiteSpace(albumId))
            {
                continue;
            }

            if (!MatchesExactToken(targetAlbum, GetNestedString(element, AlbumType, TitleKey))
                || !MatchesExactToken(targetArtist, GetNestedString(element, ArtistType, NameKey)))
            {
                continue;
            }

            yield return albumId;
        }
    }

    private async Task<DeezerSearchResult?> TrySearchAlbumAsync(string query, int limit)
    {
        try
        {
            return await _deezerClient.SearchAlbumAsync(query, new ApiOptions { Limit = limit });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Album search failed for query {Query}", query);
            }
            return null;
        }
    }

    private async Task<(string AlbumId, int Score)?> GetOverlapCandidateAsync(
        object item,
        List<string> spotifyTracks)
    {
        if (item is not JsonElement element)
        {
            return null;
        }

        var albumId = JsonElementReader.GetString(element, "id");
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return null;
        }

        try
        {
            var deezerTracks = await _deezerClient.GetAlbumTracksAsync(albumId);
            if (deezerTracks.Count == 0)
            {
                return null;
            }

            var deezerTrackNames = deezerTracks
                .Select(track => NormalizeTrackToken(track.SngTitle))
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (albumId, spotifyTracks.Count(name => deezerTrackNames.Contains(name)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to fetch Deezer tracks for album {AlbumId}", albumId);
            }
            return null;
        }
    }
}
