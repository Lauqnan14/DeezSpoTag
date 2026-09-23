using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string AudiomackSource = DeezSpoTag.Web.Services.Audiomack.AudiomackApiClient.SourceName;
    private const string AppleSource = "apple";
    private const string TidalSource = "tidal";
    private const string QobuzSource = "qobuz";
    private const string LibraryDbNotConfiguredMessage = "Library DB not configured.";
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly SpotifyArtistService _spotifyArtistService;
    private readonly ArtistPageCacheRepository _artistPageCache;
    private readonly SpotifyMetadataCacheRepository _spotifyMetadataCache;
    private readonly LastFmArtistImageService _lastFmArtistImageService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<LibraryArtistSourceMetadataApiController> _logger;
    private readonly DeezSpoTag.Web.Services.Audiomack.AudiomackArtistLocationService _audiomackArtistLocation;
    private readonly DeezSpoTag.Services.Library.ArtistLocationOverrideStore _locationOverrides;

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
        _lastFmArtistImageService = metadataServices.LastFmArtistImageService;
        _environment = metadataServices.Environment;
        _audiomackArtistLocation = metadataServices.AudiomackArtistLocation;
        _locationOverrides = metadataServices.LocationOverrides;
        _logger = logger;
    }

    [HttpGet("lastfm-biography")]
    public async Task<IActionResult> GetLastFmBiography(
        [FromQuery] string? artistName,
        CancellationToken cancellationToken)
    {
        var name = (artistName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("artistName is required.");
        }

        var result = await _lastFmArtistImageService.GetArtistBiographyAsync(name, cancellationToken);
        return Ok(new
        {
            available = result is not null,
            biography = result?.Biography ?? string.Empty
        });
    }

    [HttpGet("{id:long}/biographies")]
    public async Task<IActionResult> GetBiographies(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(LibraryDbNotConfiguredMessage);
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null)
        {
            return NotFound();
        }

        var rows = await _repository.GetArtistBiographyRowsAsync(id, cancellationToken);
        var selected = rows.FirstOrDefault(row => row.Selected)?.Source;
        return Ok(new
        {
            sources = rows.Select(row => new
            {
                source = row.Source,
                biography = row.Biography,
                selected = row.Selected
            }),
            selected
        });
    }

    [HttpPost("{id:long}/biographies/select")]
    public async Task<IActionResult> SelectBiographySource(
        long id,
        [FromBody] BiographySourceSelectionRequest request,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(LibraryDbNotConfiguredMessage);
        }

        var source = (request?.Source ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(source))
        {
            return BadRequest("source is required.");
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null)
        {
            return NotFound();
        }

        // SelectArtistBiographySourceAsync keeps the requested source when it exists in
        // the artist's biography cache; otherwise it leaves the current selection intact.
        var cachedSources = await _repository.GetArtistBiographyRowsAsync(id, cancellationToken);
        if (!cachedSources.Any(row => string.Equals(row.Source, source, StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest($"No cached biography for source '{source}'.");
        }

        await _repository.SelectArtistBiographySourceAsync(id, source, cancellationToken);
        return Ok(new { selected = source });
    }

    /// <summary>
    /// Live Audiomack biography for the artist page. Resolves the artist's public
    /// Audiomack profile (the same shared fetch/cache the location pipeline uses)
    /// and persists a non-empty result into the biography cache so the source
    /// selector can keep serving it. A miss or a failure answers available=false
    /// and leaves any previously cached biography untouched.
    /// </summary>
    [HttpGet("{id:long}/audiomack-biography")]
    public async Task<IActionResult> GetAudiomackBiography(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(LibraryDbNotConfiguredMessage);
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return NotFound();
        }

        var biography = ArtistBiographySanitizer.Clean(
            await _audiomackArtistLocation.ResolveBiographyAsync(id, artist.Name, cancellationToken));
        if (!string.IsNullOrWhiteSpace(biography))
        {
            // Preserve any existing selection so a refresh never deselects the source.
            var alreadySelected = (await _repository.GetArtistBiographyRowsAsync(id, cancellationToken))
                .Any(row => string.Equals(row.Source, AudiomackSource, StringComparison.OrdinalIgnoreCase) && row.Selected);
            await _repository.UpsertArtistBiographyCacheAsync(id, AudiomackSource, biography, alreadySelected, cancellationToken);
        }

        return Ok(new
        {
            available = biography is not null,
            biography = biography ?? string.Empty
        });
    }

    /// <summary>
    /// Provider-independent artist location for the library hero. Audiomack is the
    /// only source of an auto-resolved location, and this route serves it directly
    /// (manual override first) so an artist with no Spotify node still renders one.
    /// A miss answers available=false and changes nothing.
    /// </summary>
    [HttpGet("{id:long}/location")]
    public async Task<IActionResult> GetArtistLocation(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Ok(new { available = false, location = default(object) });
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return Ok(new { available = false, location = default(object) });
        }

        ArtistLocationPayload? payload;
        try
        {
            payload = await ResolveArtistLocationAsync(id, artist.Name, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist location endpoint failed for artist {ArtistId}", id);
            payload = null;
        }

        return Ok(new
        {
            available = payload is not null,
            location = payload is null
                ? null
                : new
                {
                    city = payload.City,
                    country = payload.Country,
                    country_code = payload.CountryCode,
                    source = payload.Source
                }
        });
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

        if (cacheOnly || (!refresh && !rematch))
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

        return await CreateSpotifyArtistResultAsync(id, result, cancellationToken: cancellationToken);
    }

    [HttpPost("{id:long}/spotify/discography/refresh")]
    public async Task<IActionResult> RefreshSpotifyDiscography(long id, CancellationToken cancellationToken)
    {
        var resolvedArtistName = await ResolveArtistNameAsync(id, cancellationToken);
        var spotifyId = await _repository.GetArtistSourceIdAsync(id, SpotifySource, cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return CreateSpotifyUnavailableResult();
        }

        var refreshed = await _spotifyArtistService.RefreshLatestDiscographyAsync(
            spotifyId,
            string.IsNullOrWhiteSpace(resolvedArtistName) ? spotifyId : resolvedArtistName,
            cancellationToken);
        return refreshed is null
            ? CreateSpotifyUnavailableResult()
            : await CreateSpotifyArtistResultAsync(id, refreshed, attachLocation: false, cancellationToken: cancellationToken);
    }

    [HttpPost("{id:long}/spotify-reset")]
    public async Task<IActionResult> ResetSpotifyMatch(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(LibraryDbNotConfiguredMessage);
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return NotFound();
        }

        var existingSpotifyIds = await _repository.GetArtistSourceIdsAsync(id, SpotifySource, cancellationToken);
        foreach (var existingSpotifyId in existingSpotifyIds)
        {
            await _artistPageCache.ClearEntryAsync(SpotifySource, existingSpotifyId, cancellationToken);
            await _spotifyMetadataCache.ClearEntryAsync("artist", existingSpotifyId, cancellationToken);
        }

        await _spotifyArtistService.EnsureAliasSpotifyIdentitiesAsync(id, artist.Name, cancellationToken);

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
            return BadRequest(LibraryDbNotConfiguredMessage);
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

    [HttpGet("{id:long}/audiomack-id")]
    public async Task<IActionResult> GetAudiomackId(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Ok(new { audiomackId = default(string) });
        }

        var audiomackId = await _repository.GetArtistSourceIdAsync(id, AudiomackSource, cancellationToken);
        return Ok(new { audiomackId });
    }

    /// <summary>
    /// Stores the Audiomack artist identity (canonical profile slug) used by the
    /// artist-location pipeline. Auto-discovered anonymously via Audiomack's web
    /// search API; this endpoint lets the user correct a mismatch — a wrong slug
    /// can never attach a wrong artist's location because the page parser
    /// cross-checks the profile name, and both the old and new slug location
    /// caches are cleared so the next page load re-resolves fresh.
    /// </summary>
    [HttpPut("{id:long}/audiomack-id")]
    public async Task<IActionResult> UpdateAudiomackId(long id, [FromBody] DeezSpoTag.Web.Services.Audiomack.AudiomackIdUpdateRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.AudiomackId))
        {
            return BadRequest("Audiomack artist slug or profile URL is required.");
        }

        if (!_repository.IsConfigured)
        {
            return BadRequest(LibraryDbNotConfiguredMessage);
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null)
        {
            return NotFound();
        }

        var audiomackId = DeezSpoTag.Web.Services.Audiomack.AudiomackIdNormalizer.Normalize(request.AudiomackId);
        if (audiomackId is null)
        {
            return BadRequest("Enter an Audiomack artist slug (letters, numbers, dashes) or a profile URL.");
        }

        var existingAudiomackId = await _repository.GetArtistSourceIdAsync(id, AudiomackSource, cancellationToken);
        await _repository.UpsertArtistSourceIdAsync(id, AudiomackSource, audiomackId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(existingAudiomackId))
        {
            await _artistPageCache.ClearEntryAsync(
                DeezSpoTag.Web.Services.Audiomack.AudiomackArtistLocationService.LocationCacheSource,
                existingAudiomackId,
                cancellationToken);
        }

        await _artistPageCache.ClearEntryAsync(
            DeezSpoTag.Web.Services.Audiomack.AudiomackArtistLocationService.LocationCacheSource,
            audiomackId,
            cancellationToken);

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"[audiomack] manual id set for artist {id}."));

        return Ok(new { audiomackId });
    }

    /// <summary>
    /// Manual artist location override rendered by the library hero. When both
    /// fields are empty the override is removed and the page falls back to the
    /// Audiomack-resolved location. The country code is derived from the country
    /// name with the same normalizer the Audiomack pipeline uses, so flags stay
    /// consistent.
    /// </summary>
    [HttpPut("{id:long}/location-override")]
    public async Task<IActionResult> UpdateLocationOverride(long id, [FromBody] LocationOverrideUpdateRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        if (!_repository.IsConfigured)
        {
            return BadRequest(LibraryDbNotConfiguredMessage);
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null)
        {
            return NotFound();
        }

        const int maxFieldLength = 64;
        var city = request.City?.Trim() ?? string.Empty;
        var country = request.Country?.Trim() ?? string.Empty;
        if (city.Length > maxFieldLength || country.Length > maxFieldLength)
        {
            return BadRequest("City and country must be 64 characters or fewer.");
        }

        if (city.Length == 0 && country.Length == 0)
        {
            await _locationOverrides.SetAsync(id, null, null, null, cancellationToken);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"[location] manual override cleared for artist {id}."));
            return Ok(new { cleared = true });
        }

        // Reuse the Audiomack normalizer so "City, Country" input derives a valid
        // ISO code exactly like the resolved pipeline does.
        var normalized = DeezSpoTag.Web.Services.Audiomack.AudiomackLocationNormalizer.Normalize(
            country.Length > 0
                ? string.IsNullOrWhiteSpace(city) ? country : $"{city}, {country}"
                : city);

        var storedCity = normalized?.City ?? (city.Length > 0 ? city : null);
        var storedCountry = normalized?.Country ?? (country.Length > 0 ? country : null);
        await _locationOverrides.SetAsync(id, storedCity, storedCountry, normalized?.CountryCode, cancellationToken);

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"[location] manual override set for artist {id}: {storedCity}, {storedCountry}."));

        return Ok(new
        {
            city = storedCity,
            country = storedCountry,
            countryCode = normalized?.CountryCode,
            source = "manual"
        });
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
            return BadRequest(LibraryDbNotConfiguredMessage);
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

    [HttpGet("{id:long}/tidal-id")]
    public async Task<IActionResult> GetTidalId(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Ok(new { tidalId = default(string) });
        }

        var tidalId = await _repository.GetArtistSourceIdAsync(id, TidalSource, cancellationToken);
        return Ok(new { tidalId });
    }

    [HttpPut("{id:long}/tidal-id")]
    public async Task<IActionResult> UpdateTidalId(long id, [FromBody] TidalIdUpdateRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.TidalId))
        {
            return BadRequest("Tidal artist ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return BadRequest(LibraryDbNotConfiguredMessage);
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null)
        {
            return NotFound();
        }

        var tidalId = request.TidalId.Trim();
        if (!tidalId.All(char.IsDigit))
        {
            return BadRequest("Tidal artist ID should be numeric.");
        }

        await _repository.UpsertArtistSourceIdAsync(id, TidalSource, tidalId, cancellationToken);

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"[tidal] manual id set for artist {id}."));

        return Ok(new { tidalId });
    }

    [HttpGet("{id:long}/qobuz-id")]
    public async Task<IActionResult> GetQobuzId(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Ok(new { qobuzId = default(string) });
        }

        var qobuzId = await _repository.GetArtistSourceIdAsync(id, QobuzSource, cancellationToken);
        return Ok(new { qobuzId });
    }

    [HttpPut("{id:long}/qobuz-id")]
    public async Task<IActionResult> UpdateQobuzId(long id, [FromBody] QobuzIdUpdateRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.QobuzId))
        {
            return BadRequest("Qobuz artist ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return BadRequest(LibraryDbNotConfiguredMessage);
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null)
        {
            return NotFound();
        }

        var qobuzId = request.QobuzId.Trim();
        if (!qobuzId.All(char.IsDigit))
        {
            return BadRequest("Qobuz artist ID should be numeric.");
        }

        await _repository.UpsertArtistSourceIdAsync(id, QobuzSource, qobuzId, cancellationToken);

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"[qobuz] manual id set for artist {id}."));

        return Ok(new { qobuzId });
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
        // Location has its own route. Keeping it off this read lets the cached
        // catalogue return without an Audiomack lookup.
        return cached is null
            ? CreateSpotifyUnavailableResult()
            : await CreateSpotifyArtistResultAsync(id, cached, attachLocation: false, cancellationToken: cancellationToken);
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

    private async Task<OkObjectResult> CreateSpotifyArtistResultAsync(
        long artistId,
        SpotifyArtistPageResult result,
        bool attachLocation = true,
        CancellationToken cancellationToken = default)
    {
        result = await _spotifyArtistService.MergeAliasVisualsAsync(artistId, result, cancellationToken);
        var artistPagePayload = SpotifyArtistPagePayloadMapper.Build(result);
        var artistNode = attachLocation
            ? await AttachArtistLocationToArtistNodeAsync(artistId, result)
            : JsonSerializer.SerializeToNode(result.Artist, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Ok(new
        {
            available = result.Available,
            artist = artistNode,
            albums = result.Albums,
            appearsOn = result.AppearsOn,
            topTracks = result.TopTracks,
            relatedArtists = result.RelatedArtists,
            artistPage = artistPagePayload
        });
    }

    /// <summary>
    /// Serializes the artist profile and embeds the resolved location
    /// (city/country/country_code) so the library artist hero can render a flag.
    /// Failures degrade silently to the plain profile.
    /// </summary>
    private async Task<JsonNode?> AttachArtistLocationToArtistNodeAsync(long artistId, SpotifyArtistPageResult result)
    {
        var artistName = result.Artist?.Name;
        JsonNode? node;
        try
        {
            node = JsonSerializer.SerializeToNode(result.Artist, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not serialize artist profile for location attach");
            return null;
        }

        if (node is not JsonObject artistObject || string.IsNullOrWhiteSpace(artistName))
        {
            return node;
        }

        try
        {
            // Manual override wins; otherwise Audiomack's own profile location.
            var location = await ResolveArtistLocationAsync(artistId, artistName, CancellationToken.None);
            if (location == null)
            {
                return node;
            }

            var locationNode = JsonSerializer.SerializeToNode(new
            {
                city = location.City,
                country = location.Country,
                country_code = location.CountryCode,
                source = location.Source
            });
            if (locationNode != null)
            {
                artistObject["location"] = locationNode;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist location lookup failed; continuing without location");
        }

        return node;
    }

    /// <summary>Location payload attached to an artist node or served by its own route.</summary>
    private sealed record ArtistLocationPayload(string? City, string? Country, string? CountryCode, string Source);

    /// <summary>
    /// Resolves an artist's location independently of any provider payload: the
    /// library manual override always wins, otherwise Audiomack's own artist profile
    /// is used. This is the single path both the Spotify artist response and the
    /// standalone location route go through, so an Audiomack-only artist (no Spotify
    /// node) still gets a location. Returns null when nothing is available; callers
    /// must leave existing metadata untouched.
    /// </summary>
    private async Task<ArtistLocationPayload?> ResolveArtistLocationAsync(
        long artistId,
        string artistName,
        CancellationToken cancellationToken)
    {
        // Manual override (library page "Location" editor) always wins over the
        // Audiomack-resolved value.
        var manualOverride = await _locationOverrides.GetAsync(artistId, cancellationToken);
        if (manualOverride != null
            && (!string.IsNullOrWhiteSpace(manualOverride.City) || !string.IsNullOrWhiteSpace(manualOverride.Country)))
        {
            return new ArtistLocationPayload(manualOverride.City, manualOverride.Country, manualOverride.CountryCode, "manual");
        }

        var location = await _audiomackArtistLocation.ResolveAsync(artistId, artistName, cancellationToken);
        if (location == null)
        {
            return null;
        }

        return new ArtistLocationPayload(location.City, location.Country, location.CountryCode, location.Source);
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

    public sealed record LocationOverrideUpdateRequest(string? City, string? Country);

    public sealed record TidalIdUpdateRequest(string TidalId);

    public sealed record QobuzIdUpdateRequest(string QobuzId);

    public sealed record BiographySourceSelectionRequest(string? Source);

    private sealed record UnmatchedSpotifyArtistDto(long ArtistId, string ArtistName);
}
