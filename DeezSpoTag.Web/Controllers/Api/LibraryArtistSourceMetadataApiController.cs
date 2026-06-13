using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/artists")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class LibraryArtistSourceMetadataApiController : ControllerBase
{
    private const string SpotifySource = "spotify";
    private const string AppleSource = "apple";
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly SpotifyArtistService _spotifyArtistService;
    private readonly ArtistPageCacheRepository _artistPageCache;
    private readonly SpotifyMetadataCacheRepository _spotifyMetadataCache;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<LibraryArtistSourceMetadataApiController> _logger;

    public LibraryArtistSourceMetadataApiController(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        LibraryArtistMetadataServices metadataServices,
        ILogger<LibraryArtistSourceMetadataApiController> logger)
    {
        _repository = repository;
        _configStore = configStore;
        _spotifyArtistService = metadataServices.SpotifyArtistService;
        _artistPageCache = metadataServices.ArtistPageCache;
        _spotifyMetadataCache = metadataServices.SpotifyMetadataCache;
        _environment = metadataServices.Environment;
        _logger = logger;
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

        var artistsWithSpotifySource = await _repository.GetArtistIdsWithSourceAsync(SpotifySource, cancellationToken);
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

            if (artistsWithSpotifySource.Contains(artist.Id))
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
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Spotify artist request: artistId={ArtistId} refresh={Refresh} rematch={Rematch}", id, refresh, rematch);
        }

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

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null)
        {
            return NotFound();
        }

        var existingSpotifyId = await _repository.GetArtistSourceIdAsync(id, SpotifySource, cancellationToken);
        var spotifyId = request.SpotifyId.Trim();
        if (!IsValidSpotifyEntityId(spotifyId))
        {
            return BadRequest("Spotify ID should be a 22-character alphanumeric value.");
        }

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

        var appleId = await _repository.GetArtistSourceIdAsync(id, AppleSource, cancellationToken);
        return Ok(new { appleId });
    }

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
        await _repository.UpsertArtistSourceIdAsync(id, AppleSource, appleId, cancellationToken);
        await _repository.UpdateArtistAppleBiographyAsync(id, null, DateTimeOffset.MinValue, cancellationToken);

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"[apple] manual id set for artist {id}."));

        return Ok(new { appleId });
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
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to remove {Label} {FilePath}", label, filePath);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Access denied removing {Label} {FilePath}", label, filePath);
            }
        }
    }

    private async Task<string?> ResolveArtistNameAsync(long id, CancellationToken cancellationToken)
    {
        if (_repository.IsConfigured)
        {
            var artist = await _repository.GetArtistAsync(id, cancellationToken);
            return artist?.Name;
        }

        var localArtist = (await _configStore.GetLocalArtistsAsync()).FirstOrDefault(item => item.Id == id);
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

    private static bool IsValidSpotifyEntityId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 22)
        {
            return false;
        }

        return value.All(char.IsLetterOrDigit);
    }

    public sealed record SpotifyIdUpdateRequest(string SpotifyId);

    public sealed record AppleIdUpdateRequest(string AppleId);

    private sealed record UnmatchedSpotifyArtistDto(long ArtistId, string ArtistName);
}
