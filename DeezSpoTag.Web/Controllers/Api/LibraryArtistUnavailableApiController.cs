using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/artists")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class LibraryArtistUnavailableApiController : ControllerBase
{
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly ILogger<LibraryArtistUnavailableApiController> _logger;

    public LibraryArtistUnavailableApiController(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        ILogger<LibraryArtistUnavailableApiController> logger)
    {
        _repository = repository;
        _configStore = configStore;
        _logger = logger;
    }

    [HttpGet("{id:long}/unavailable")]
    public async Task<IActionResult> GetUnavailableAlbums(
        long id,
        [FromServices] IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var localContext = await ResolveLocalArtistAlbumsContextAsync(id, cancellationToken);
        if (localContext is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(localContext.ArtistName))
        {
            return Ok(Array.Empty<object>());
        }

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            var selected = await SelectDeezerArtistAsync(
                httpClient,
                localContext.ArtistName,
                localContext.LocalTitleSet,
                cancellationToken);
            if (selected is null)
            {
                return Ok(Array.Empty<object>());
            }

            var albums = selected.PrefetchedAlbums
                ?? await FetchArtistAlbumsAsync(httpClient, selected.Artist.Id, cancellationToken);
            var unavailable = BuildUnavailableAlbums(
                albums,
                localContext.LocalTitleSet,
                localContext.LocalStereoTrackCountsByTitle);
            return Ok(unavailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to load unavailable albums for {ArtistName}", localContext.ArtistName);
            return Ok(Array.Empty<object>());
        }
    }

    private async Task<LocalArtistAlbumsContext?> ResolveLocalArtistAlbumsContextAsync(long id, CancellationToken cancellationToken)
    {
        if (_repository.IsConfigured)
        {
            var artist = await _repository.GetArtistAsync(id, cancellationToken);
            if (artist is null)
            {
                return null;
            }

            var albums = await _repository.GetArtistAlbumsAsync(id, cancellationToken);
            var localStereoTrackCountsByTitle = BuildLocalStereoTrackCountsByTitle(
                albums,
                album => album.Title,
                album => album.LocalStereoTrackCount);
            return new LocalArtistAlbumsContext(
                artist.Name ?? string.Empty,
                new HashSet<string>(localStereoTrackCountsByTitle.Keys),
                localStereoTrackCountsByTitle);
        }

        var localArtist = (await _configStore.GetLocalArtistsAsync()).FirstOrDefault(item => item.Id == id);
        if (localArtist is null)
        {
            return null;
        }

        var localAlbums = await _configStore.GetLocalAlbumsAsync(id);
        var localCounts = BuildLocalStereoTrackCountsByTitle(
            localAlbums,
            album => album.Title,
            album => album.LocalStereoTrackCount);
        return new LocalArtistAlbumsContext(
            localArtist.Name ?? string.Empty,
            new HashSet<string>(localCounts.Keys),
            localCounts);
    }

    private static Dictionary<string, int> BuildLocalStereoTrackCountsByTitle<TAlbum>(
        IEnumerable<TAlbum> albums,
        Func<TAlbum, string?> titleSelector,
        Func<TAlbum, int> localStereoCountSelector)
    {
        return albums
            .Select(album => new
            {
                Key = NormalizeTitle(titleSelector(album) ?? string.Empty),
                Count = Math.Max(0, localStereoCountSelector(album))
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Count));
    }

    private async Task<SelectedDeezerArtist?> SelectDeezerArtistAsync(
        HttpClient httpClient,
        string artistName,
        HashSet<string> localTitleSet,
        CancellationToken cancellationToken)
    {
        var candidates = await FetchDeezerArtistCandidatesAsync(httpClient, artistName, cancellationToken);
        if (candidates.Count == 0)
        {
            return null;
        }

        var limitedCandidates = GetLimitedArtistCandidates(candidates, artistName);
        var bestByOverlap = await SelectBestOverlapArtistAsync(httpClient, limitedCandidates, localTitleSet, cancellationToken);
        if (bestByOverlap is not null)
        {
            return bestByOverlap;
        }

        var fallback = limitedCandidates
            .OrderByDescending(candidate => candidate.Fans)
            .FirstOrDefault();
        return fallback is null ? null : new SelectedDeezerArtist(fallback, null);
    }

    private async Task<List<DeezerArtistCandidate>> FetchDeezerArtistCandidatesAsync(
        HttpClient httpClient,
        string artistName,
        CancellationToken cancellationToken)
    {
        var searchUrl = $"https://api.deezer.com/search/artist?q={Uri.EscapeDataString(artistName)}";
        var searchResponse = await httpClient.GetAsync(searchUrl, cancellationToken);
        if (!searchResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Deezer artist search failed for {ArtistName}: {StatusCode}", artistName, searchResponse.StatusCode);
            return [];
        }

        var searchContent = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
        using var searchDoc = JsonDocument.Parse(searchContent);
        if (!searchDoc.RootElement.TryGetProperty("data", out var searchData) || searchData.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return searchData
            .EnumerateArray()
            .Select(item => TryParseArtistCandidate(item, out var candidate) ? candidate : (DeezerArtistCandidate?)null)
            .Where(static candidate => candidate is not null)
            .Select(static candidate => candidate!)
            .ToList();
    }

    private static bool TryParseArtistCandidate(JsonElement item, out DeezerArtistCandidate candidate)
    {
        candidate = default!;
        if (!item.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var fans = item.TryGetProperty("nb_fan", out var fansProp) && fansProp.ValueKind == JsonValueKind.Number
            ? fansProp.GetInt64()
            : 0;
        candidate = new DeezerArtistCandidate(idProp.GetInt64(), name, fans);
        return true;
    }

    private static List<DeezerArtistCandidate> GetLimitedArtistCandidates(
        IReadOnlyList<DeezerArtistCandidate> candidates,
        string artistName)
    {
        var exactMatches = candidates
            .Where(candidate => candidate.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var candidatePool = exactMatches.Count > 0 ? exactMatches : candidates;
        return candidatePool
            .OrderByDescending(candidate => candidate.Fans)
            .Take(5)
            .ToList();
    }

    private async Task<SelectedDeezerArtist?> SelectBestOverlapArtistAsync(
        HttpClient httpClient,
        IReadOnlyList<DeezerArtistCandidate> candidates,
        HashSet<string> localTitleSet,
        CancellationToken cancellationToken)
    {
        if (localTitleSet.Count == 0)
        {
            return null;
        }

        var bestOverlap = 0;
        SelectedDeezerArtist? selected = null;
        foreach (var candidate in candidates)
        {
            var albums = await FetchArtistAlbumsAsync(httpClient, candidate.Id, cancellationToken);
            if (albums.Count == 0)
            {
                continue;
            }

            var overlap = albums.Count(album => localTitleSet.Contains(NormalizeTitle(album.Title)));
            if (overlap <= bestOverlap)
            {
                continue;
            }

            bestOverlap = overlap;
            selected = new SelectedDeezerArtist(candidate, albums);
        }

        return selected;
    }

    private static List<object> BuildUnavailableAlbums(
        IReadOnlyList<DeezerAlbumCandidate> albums,
        HashSet<string> localTitleSet,
        Dictionary<string, int> localStereoTrackCountsByTitle)
    {
        var uniqueIds = new HashSet<long>();
        var unavailable = new List<object>();

        foreach (var album in albums)
        {
            if (!uniqueIds.Add(album.Id) || string.IsNullOrWhiteSpace(album.Title))
            {
                continue;
            }

            var normalizedTitle = NormalizeTitle(album.Title);
            if (IsAlbumFullyDownloaded(normalizedTitle, album.TrackCount, localTitleSet, localStereoTrackCountsByTitle))
            {
                continue;
            }

            unavailable.Add(new
            {
                id = album.Id,
                title = album.Title,
                coverUrl = album.CoverUrl,
                link = album.Link ?? $"https://www.deezer.com/album/{album.Id}",
                recordType = album.RecordType,
                releaseDate = album.ReleaseDate,
                trackCount = album.TrackCount
            });
        }

        return unavailable;
    }

    private static bool IsAlbumFullyDownloaded(
        string normalizedTitle,
        int remoteTrackCount,
        HashSet<string> localTitleSet,
        Dictionary<string, int> localStereoTrackCountsByTitle)
    {
        if (!localTitleSet.Contains(normalizedTitle))
        {
            return false;
        }

        localStereoTrackCountsByTitle.TryGetValue(normalizedTitle, out var localStereoTrackCount);
        var normalizedRemoteTrackCount = Math.Max(0, remoteTrackCount);
        return normalizedRemoteTrackCount > 0
            ? localStereoTrackCount >= normalizedRemoteTrackCount
            : localStereoTrackCount > 0;
    }

    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[title.Length];
        var index = 0;
        foreach (var ch in title.Where(char.IsLetterOrDigit))
        {
            buffer[index++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..index]);
    }

    private async Task<IReadOnlyList<DeezerAlbumCandidate>> FetchArtistAlbumsAsync(HttpClient httpClient, long artistId, CancellationToken cancellationToken)
    {
        var albumsUrl = $"https://api.deezer.com/artist/{artistId}/albums?limit=200";
        var albumsResponse = await httpClient.GetAsync(albumsUrl, cancellationToken);
        if (!albumsResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Deezer albums fetch failed for artist {ArtistId}: {StatusCode}", artistId, albumsResponse.StatusCode);
            return Array.Empty<DeezerAlbumCandidate>();
        }

        var albumsContent = await albumsResponse.Content.ReadAsStringAsync(cancellationToken);
        using var albumsDoc = JsonDocument.Parse(albumsContent);
        if (!albumsDoc.RootElement.TryGetProperty("data", out var albumsData) || albumsData.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<DeezerAlbumCandidate>();
        }

        return albumsData
            .EnumerateArray()
            .Select(album => TryParseAlbumCandidate(album, out var parsedAlbum) ? parsedAlbum : (DeezerAlbumCandidate?)null)
            .Where(static album => album is not null)
            .Select(static album => album!)
            .ToArray();
    }

    private static bool TryParseAlbumCandidate(JsonElement album, out DeezerAlbumCandidate candidate)
    {
        candidate = default!;
        if (!album.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var title = album.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        candidate = new DeezerAlbumCandidate(
            idProp.GetInt64(),
            title,
            GetAlbumCoverUrl(album),
            GetOptionalString(album, "link"),
            GetOptionalString(album, "record_type"),
            GetOptionalString(album, "release_date"),
            GetNonNegativeInt(album, "nb_tracks"));
        return true;
    }

    private static string? GetAlbumCoverUrl(JsonElement album)
    {
        if (album.TryGetProperty("cover_medium", out var coverMedium))
        {
            return coverMedium.GetString();
        }

        if (album.TryGetProperty("cover", out var cover))
        {
            return cover.GetString();
        }

        return null;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;

    private static int GetNonNegativeInt(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number)
        {
            return Math.Max(0, property.GetInt32());
        }

        return 0;
    }

    private sealed record DeezerArtistCandidate(long Id, string Name, long Fans);

    private sealed record SelectedDeezerArtist(DeezerArtistCandidate Artist, IReadOnlyList<DeezerAlbumCandidate>? PrefetchedAlbums);

    private sealed record LocalArtistAlbumsContext(
        string ArtistName,
        HashSet<string> LocalTitleSet,
        Dictionary<string, int> LocalStereoTrackCountsByTitle);

    private sealed record DeezerAlbumCandidate(
        long Id,
        string Title,
        string? CoverUrl,
        string? Link,
        string? RecordType,
        string? ReleaseDate,
        int TrackCount);
}
