using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/artists")]
[ApiController]
[Authorize]
public class LibraryArtistsApiController : ControllerBase
{
    private const string SpotifySource = "spotify";
    private readonly LibraryRepository _repository;
    private readonly DeezSpoTag.Web.Services.LibraryConfigStore _configStore;
    private readonly DeezSpoTag.Web.Services.SpotifyArtistService _spotifyArtistService;
    private readonly ArtistPageCacheRepository _artistPageCache;
    private readonly SpotifyMetadataCacheRepository _spotifyMetadataCache;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<LibraryArtistsApiController> _logger;

    public LibraryArtistsApiController(
        LibraryRepository repository,
        DeezSpoTag.Web.Services.LibraryConfigStore configStore,
        DeezSpoTag.Web.Services.SpotifyArtistService spotifyArtistService,
        ArtistPageCacheRepository artistPageCache,
        SpotifyMetadataCacheRepository spotifyMetadataCache,
        IWebHostEnvironment environment,
        ILogger<LibraryArtistsApiController> logger)
    {
        _repository = repository;
        _configStore = configStore;
        _spotifyArtistService = spotifyArtistService;
        _artistPageCache = artistPageCache;
        _spotifyMetadataCache = spotifyMetadataCache;
        _environment = environment;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? availability,
        [FromQuery] long? folderId,
        [FromQuery] string? search,
        [FromQuery] string? sort,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            var localArtists = _configStore.GetLocalArtists().Select(localArtist => new
            {
                localArtist.Id,
                localArtist.Name,
                AvailableLocally = true,
                PreferredImagePath = localArtist.ImagePath,
                PreferredBackgroundPath = localArtist.BackgroundImagePath
            });
            return Ok(localArtists);
        }

        if (page.HasValue || pageSize.HasValue)
        {
            var artistPage = await _repository.GetArtistsPageAsync(
                availability,
                folderId,
                page ?? 1,
                pageSize ?? 300,
                search,
                sort,
                cancellationToken);
            return Ok(new
            {
                items = artistPage.Items,
                totalCount = artistPage.TotalCount,
                page = artistPage.Page,
                pageSize = artistPage.PageSize,
                hasMore = (artistPage.Page * artistPage.PageSize) < artistPage.TotalCount
            });
        }

        var dbArtists = await _repository.GetArtistsAsync(availability, folderId, cancellationToken);
        return Ok(dbArtists);
    }

    [HttpGet("{id:long}/albums")]
    public async Task<IActionResult> GetAlbums(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            var localAlbums = _configStore.GetLocalAlbums(id);
            return Ok(localAlbums);
        }

        var dbAlbums = await _repository.GetArtistAlbumsAsync(id, cancellationToken);
        return Ok(dbAlbums);
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

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetArtist(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            var localArtist = _configStore.GetLocalArtists().FirstOrDefault(item => item.Id == id);
            if (localArtist is null)
            {
                return NotFound();
            }
            return Ok(new { localArtist.Id, localArtist.Name, PreferredImagePath = localArtist.ImagePath, PreferredBackgroundPath = localArtist.BackgroundImagePath });
        }

        var dbArtist = await _repository.GetArtistAsync(id, cancellationToken);
        if (dbArtist is null)
        {
            return NotFound();
        }

        return Ok(dbArtist);
    }

    [HttpGet("unmatched-spotify")]
    public async Task<IActionResult> GetUnmatchedSpotifyArtists(
        [FromQuery] int limit = 50,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!_repository.IsConfigured)
        {
            return Ok(Array.Empty<object>());
        }

        var safeLimit = Math.Clamp(limit, 1, 200);
        var searchText = (search ?? string.Empty).Trim();
        var artists = await _repository.GetArtistsAsync("local", cancellationToken);
        if (artists.Count == 0)
        {
            return Ok(Array.Empty<object>());
        }

        var unmatched = new List<UnmatchedSpotifyArtistDto>(safeLimit);
        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(artist.Name))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(searchText)
                && artist.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var spotifyId = await _repository.GetArtistSourceIdAsync(artist.Id, SpotifySource, cancellationToken);
            if (!string.IsNullOrWhiteSpace(spotifyId))
            {
                continue;
            }

            unmatched.Add(new UnmatchedSpotifyArtistDto(artist.Id, artist.Name));
            if (unmatched.Count >= safeLimit)
            {
                break;
            }
        }

        return Ok(unmatched);
    }

    [HttpGet("{id:long}/spotify-suggestions")]
    public async Task<IActionResult> GetSpotifyMatchSuggestions(
        long id,
        [FromQuery] int limit = 8,
        CancellationToken cancellationToken = default)
    {
        if (!_repository.IsConfigured)
        {
            return Ok(Array.Empty<object>());
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return NotFound();
        }

        var suggestions = await _spotifyArtistService.GetArtistMatchSuggestionsAsync(
            id,
            artist.Name,
            limit,
            cancellationToken);

        return Ok(suggestions);
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

        var localArtist = _configStore.GetLocalArtists().FirstOrDefault(item => item.Id == id);
        if (localArtist is null)
        {
            return null;
        }

        var localAlbums = _configStore.GetLocalAlbums(id);
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

        var candidates = new List<DeezerArtistCandidate>();
        foreach (var item in searchData.EnumerateArray())
        {
            if (TryParseArtistCandidate(item, out var candidate))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
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

    [HttpGet("{id:long}/spotify")]
    public async Task<IActionResult> GetSpotifyArtist(
        long id,
        [FromQuery] bool refresh,
        [FromQuery] bool rematch,
        [FromQuery] bool cacheOnly,
        [FromQuery] string? spotifyId,
        [FromQuery] string? artistName,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Spotify artist request: artistId={ArtistId} refresh={Refresh} rematch={Rematch}", id, refresh, rematch);
        var resolvedArtistName = await ResolveArtistNameAsync(id, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedArtistName))
        {
            resolvedArtistName = NormalizeArtistNameInput(artistName);
        }

        if (cacheOnly)
        {
            return await GetCachedSpotifyArtistPageResultAsync(id, spotifyId, resolvedArtistName, cancellationToken);
        }

        var result = await ResolveSpotifyArtistPageResultAsync(
            id,
            refresh,
            rematch,
            spotifyId,
            artistName,
            resolvedArtistName,
            cancellationToken);
        if (result == null)
        {
            _logger.LogWarning("Spotify artist request returned no data: artistId={ArtistId}", id);
            return CreateSpotifyUnavailableResult();
        }

        return CreateSpotifyArtistResult(result);
    }

    private async Task<IActionResult> GetCachedSpotifyArtistPageResultAsync(
        long id,
        string? spotifyId,
        string? resolvedArtistName,
        CancellationToken cancellationToken)
    {
        var effectiveSpotifyId = !string.IsNullOrWhiteSpace(spotifyId)
            ? spotifyId.Trim()
            : await _repository.GetArtistSourceIdAsync(id, SpotifySource, cancellationToken);
        if (string.IsNullOrWhiteSpace(effectiveSpotifyId))
        {
            return CreateSpotifyUnavailableResult();
        }

        var effectiveArtistName = string.IsNullOrWhiteSpace(resolvedArtistName)
            ? effectiveSpotifyId
            : resolvedArtistName;
        var cached = await _spotifyArtistService.TryGetCachedArtistPageAsync(
            effectiveSpotifyId,
            effectiveArtistName,
            allowStale: true,
            cancellationToken);
        return cached is null ? CreateSpotifyUnavailableResult() : CreateSpotifyArtistResult(cached);
    }

    private async Task<SpotifyArtistPageResult?> ResolveSpotifyArtistPageResultAsync(
        long id,
        bool refresh,
        bool rematch,
        string? spotifyId,
        string? artistName,
        string? resolvedArtistName,
        CancellationToken cancellationToken)
    {
        var explicitSpotifyId = !string.IsNullOrWhiteSpace(spotifyId) ? spotifyId.Trim() : null;
        if (string.IsNullOrWhiteSpace(explicitSpotifyId))
        {
            if (string.IsNullOrWhiteSpace(resolvedArtistName))
            {
                return null;
            }

            return await _spotifyArtistService.GetArtistPageAsync(
                id,
                resolvedArtistName,
                refresh,
                rematch,
                cancellationToken,
                includeDeezerLinking: false);
        }

        if (string.IsNullOrWhiteSpace(resolvedArtistName))
        {
            var fallbackName = string.IsNullOrWhiteSpace(artistName) ? explicitSpotifyId : artistName.Trim();
            return await _spotifyArtistService.GetArtistPageBySpotifyIdAsync(explicitSpotifyId, fallbackName, refresh, cancellationToken);
        }

        if (rematch)
        {
            var fallbackName = string.IsNullOrWhiteSpace(resolvedArtistName) ? explicitSpotifyId : resolvedArtistName;
            return await _spotifyArtistService.GetArtistPageBySpotifyIdAsync(
                explicitSpotifyId,
                fallbackName,
                forceRefresh: true,
                cancellationToken);
        }

        return await _spotifyArtistService.GetArtistPageAsync(
            id,
            resolvedArtistName,
            refresh,
            rematch,
            cancellationToken,
            includeDeezerLinking: false);
    }

    private OkObjectResult CreateSpotifyUnavailableResult()
    {
        return Ok(new { available = false });
    }

    private OkObjectResult CreateSpotifyArtistResult(SpotifyArtistPageResult result)
    {
        var artistPagePayload = SpotifyArtistPagePayloadMapper.Build(result);
        return Ok(new
        {
            available = result.Available,
            artist = result.Artist,
            albums = result.Albums,
            appearsOn = result.AppearsOn,
            topTracks = result.TopTracks,
            relatedArtists = result.RelatedArtists,
            artistPage = artistPagePayload
        });
    }

    public sealed record SpotifyIdUpdateRequest(string SpotifyId);

    [HttpPost("{id:long}/spotify-reset")]
    public async Task<IActionResult> ResetSpotifyMatch(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest("Library DB not configured.");
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return NotFound();
        }

        var existingSpotifyId = await _repository.GetArtistSourceIdAsync(id, SpotifySource, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existingSpotifyId))
        {
            await _artistPageCache.ClearEntryAsync(SpotifySource, existingSpotifyId, cancellationToken);
            await _spotifyMetadataCache.ClearEntryAsync("artist", existingSpotifyId, cancellationToken);
            await PurgeSpotifyVisualFilesAsync(id, existingSpotifyId, cancellationToken);
        }

        await _repository.RemoveArtistSourceAsync(id, SpotifySource, cancellationToken);

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"[spotify] reset match requested for artist {id}."));

        return Ok(new
        {
            reset = true,
            artistId = id,
            artistName = artist.Name
        });
    }

    [HttpPut("{id:long}/spotify-id")]
    public async Task<IActionResult> UpdateSpotifyId(long id, [FromBody] SpotifyIdUpdateRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            return BadRequest("Spotify ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return BadRequest("Library DB not configured.");
        }

        var existingSpotifyId = await _repository.GetArtistSourceIdAsync(id, SpotifySource, cancellationToken);
        var spotifyId = request.SpotifyId.Trim();
        await _repository.UpsertArtistSourceIdAsync(id, SpotifySource, spotifyId, cancellationToken);

        if (!string.Equals(existingSpotifyId, spotifyId, StringComparison.OrdinalIgnoreCase))
        {
            await PurgeSpotifyVisualFilesAsync(id, existingSpotifyId, cancellationToken);
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"[spotify] manual id set for artist {id}."));

        return Ok(new { spotifyId });
    }

    [HttpGet("{id:long}/apple-id")]
    public async Task<IActionResult> GetAppleId(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Ok(new { appleId = default(string) });
        }

        var appleId = await _repository.GetArtistSourceIdAsync(id, "apple", cancellationToken);
        return Ok(new { appleId });
    }

    public sealed record AppleIdUpdateRequest(string AppleId);

    [HttpPut("{id:long}/apple-id")]
    public async Task<IActionResult> UpdateAppleId(long id, [FromBody] AppleIdUpdateRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.AppleId))
        {
            return BadRequest("Apple Music artist ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return BadRequest("Library DB not configured.");
        }

        var appleId = request.AppleId.Trim();
        await _repository.UpsertArtistSourceIdAsync(id, "apple", appleId, cancellationToken);

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"[apple] manual id set for artist {id}."));

        return Ok(new { appleId });
    }

    private async Task PurgeSpotifyVisualFilesAsync(long artistId, string? previousSpotifyId, CancellationToken cancellationToken)
    {
        var spotifyRoot = Path.GetFullPath(Path.Join(AppDataPaths.GetDataRoot(_environment), "library-artist-images", SpotifySource));
        var artistVisualDir = Path.Join(spotifyRoot, "artists", artistId.ToString());
        TryDeleteArtistVisualDirectory(artistVisualDir, artistId);
        RemoveStaleSpotifyCacheFiles(spotifyRoot, previousSpotifyId, artistId);
        await ClearPreferredArtistVisualsAsync(artistId, spotifyRoot, cancellationToken);
    }

    private void TryDeleteArtistVisualDirectory(string artistVisualDir, long artistId)
    {
        try
        {
            if (Directory.Exists(artistVisualDir))
            {
                Directory.Delete(artistVisualDir, true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to remove artist visuals folder for artist {ArtistId}", artistId);
        }
    }

    private void RemoveStaleSpotifyCacheFiles(string spotifyRoot, string? previousSpotifyId, long artistId)
    {
        var trimmedSpotifyId = (previousSpotifyId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedSpotifyId) || !Directory.Exists(spotifyRoot))
        {
            return;
        }

        try
        {
            var staleFiles = Directory.GetFiles(spotifyRoot, $"*{trimmedSpotifyId}.*", SearchOption.TopDirectoryOnly);
            foreach (var file in staleFiles)
            {
                TryDeleteFile(file, "stale spotify cache file");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to remove stale spotify cache files for artist {ArtistId}", artistId);
        }
    }

    private async Task ClearPreferredArtistVisualsAsync(long artistId, string spotifyRoot, CancellationToken cancellationToken)
    {
        try
        {
            var artist = await _repository.GetArtistAsync(artistId, cancellationToken);
            await ClearPreferredVisualPathAsync(
                artistId,
                artist?.PreferredImagePath,
                spotifyRoot,
                "preferred spotify image",
                _repository.UpdateArtistImagePathAsync,
                cancellationToken);
            await ClearPreferredVisualPathAsync(
                artistId,
                artist?.PreferredBackgroundPath,
                spotifyRoot,
                "preferred spotify background",
                _repository.UpdateArtistBackgroundPathAsync,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to clear preferred artist visuals for artist {ArtistId}", artistId);
        }
    }

    private async Task ClearPreferredVisualPathAsync(
        long artistId,
        string? preferredPath,
        string spotifyRoot,
        string label,
        Func<long, string, CancellationToken, Task> clearPathInRepository,
        CancellationToken cancellationToken)
    {
        var trimmedPath = (preferredPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(trimmedPath);
        if (!fullPath.StartsWith(spotifyRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TryDeleteFile(fullPath, label);
        await clearPathInRepository(artistId, string.Empty, cancellationToken);
    }

    private void TryDeleteFile(string filePath, string label)
    {
        if (!System.IO.File.Exists(filePath))
        {
            return;
        }

        try
        {
            System.IO.File.Delete(filePath);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Failed to remove {Label} {FilePath}", label, filePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Access denied removing {Label} {FilePath}", label, filePath);
        }
    }

    private async Task<string?> ResolveArtistNameAsync(long id, CancellationToken cancellationToken)
    {
        if (_repository.IsConfigured)
        {
            var artist = await _repository.GetArtistAsync(id, cancellationToken);
            return artist?.Name;
        }

        var localArtist = _configStore.GetLocalArtists().FirstOrDefault(item => item.Id == id);
        return localArtist?.Name;
    }

    private static string? NormalizeArtistNameInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var normalized = input.Trim();
        if (normalized.Equals("Artist", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized;
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

    private sealed record DeezerArtistCandidate(long Id, string Name, long Fans);
    private sealed record UnmatchedSpotifyArtistDto(long ArtistId, string ArtistName);

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

        var albums = new List<DeezerAlbumCandidate>();
        foreach (var album in albumsData.EnumerateArray())
        {
            if (TryParseAlbumCandidate(album, out var parsedAlbum))
            {
                albums.Add(parsedAlbum);
            }
        }

        return albums;
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
    {
        return element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
    }

    private static int GetNonNegativeInt(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number)
        {
            return Math.Max(0, property.GetInt32());
        }

        return 0;
    }

}
