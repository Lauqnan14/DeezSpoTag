using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/watchlist")]
[ApiController]
[Authorize]
public class LibraryWatchlistApiController : ControllerBase
{
    private const string SpotifySource = "spotify";
    private const string AppleSource = "apple";
    private const string DeezerSource = "deezer";
    private const string AddWatchlistFailedMessage = "Failed to add watchlist entry.";

    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly ArtistWatchService _artistWatchService;

    public LibraryWatchlistApiController(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        ArtistWatchService artistWatchService)
    {
        _repository = repository;
        _configStore = configStore;
        _artistWatchService = artistWatchService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var items = await _repository.GetWatchlistAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{artistId:long}")]
    public async Task<IActionResult> GetStatus(long artistId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var watching = await _repository.IsWatchlistedAsync(artistId, cancellationToken);
        return Ok(new { watching });
    }

    [HttpGet("spotify/{spotifyId}")]
    public async Task<IActionResult> GetSpotifyStatus(string spotifyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return BadRequest("Spotify ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var watching = await _repository.IsWatchlistedBySpotifyIdAsync(spotifyId, cancellationToken);
        if (!watching)
        {
            var artistId = await _repository.GetArtistIdBySourceIdAsync(SpotifySource, spotifyId, cancellationToken);
            if (artistId.HasValue)
            {
                watching = await _repository.IsWatchlistedAsync(artistId.Value, cancellationToken);
            }
        }

        return Ok(new { watching });
    }

    [HttpGet("apple/{appleId}")]
    public async Task<IActionResult> GetAppleStatus(string appleId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return BadRequest("Apple ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await _repository.GetArtistIdBySourceIdAsync(AppleSource, appleId, cancellationToken);
        var watching = artistId.HasValue && await _repository.IsWatchlistedAsync(artistId.Value, cancellationToken);
        return Ok(new { watching });
    }

    [HttpGet("deezer/{deezerId}")]
    public async Task<IActionResult> GetDeezerStatus(string deezerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return BadRequest("Deezer ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await _repository.GetArtistIdBySourceIdAsync(DeezerSource, deezerId, cancellationToken);
        var watching = artistId.HasValue && await _repository.IsWatchlistedAsync(artistId.Value, cancellationToken);
        return Ok(new { watching });
    }

    public sealed record WatchlistRequest(long? ArtistId, string ArtistName);
    public sealed record SpotifyWatchlistRequest(string SpotifyId, string ArtistName, string? DeezerId);
    public sealed record AppleWatchlistRequest(string AppleId, string ArtistName, string? SpotifyId, string? DeezerId);
    public sealed record DeezerWatchlistRequest(string DeezerId, string ArtistName, string? SpotifyId);

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] WatchlistRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !request.ArtistId.HasValue || request.ArtistId.Value <= 0 || string.IsNullOrWhiteSpace(request.ArtistName))
        {
            return BadRequest("Artist ID and name are required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = request.ArtistId.Value;
        var spotifyId = await _repository.GetArtistSourceIdAsync(artistId, SpotifySource, cancellationToken);
        var deezerId = await _repository.GetArtistSourceIdAsync(artistId, DeezerSource, cancellationToken);
        var added = await _repository.AddWatchlistAsync(
            artistId,
            request.ArtistName,
            spotifyId,
            deezerId,
            cancellationToken);
        return CreateAddedResponse(request.ArtistName, added);
    }

    [HttpPost("spotify")]
    public async Task<IActionResult> AddSpotify([FromBody] SpotifyWatchlistRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SpotifyId) || string.IsNullOrWhiteSpace(request.ArtistName))
        {
            return BadRequest("Spotify ID and artist name are required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await ResolveArtistIdForSpotifyAsync(request.SpotifyId, request.DeezerId, cancellationToken);
        var added = await _repository.AddWatchlistAsync(
            artistId,
            request.ArtistName,
            request.SpotifyId,
            string.IsNullOrWhiteSpace(request.DeezerId) ? null : request.DeezerId,
            cancellationToken);
        return CreateAddedResponse(request.ArtistName, added);
    }

    [HttpPost("apple")]
    public async Task<IActionResult> AddApple([FromBody] AppleWatchlistRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.AppleId) || string.IsNullOrWhiteSpace(request.ArtistName))
        {
            return BadRequest("Apple ID and artist name are required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await ResolveArtistIdForAppleAsync(request.AppleId, request.DeezerId, request.SpotifyId, cancellationToken);
        await _repository.UpsertArtistSourceIdAsync(artistId, AppleSource, request.AppleId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            await _repository.UpsertArtistSourceIdAsync(artistId, SpotifySource, request.SpotifyId, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.DeezerId))
        {
            await _repository.UpsertArtistSourceIdAsync(artistId, DeezerSource, request.DeezerId, cancellationToken);
        }

        var spotifyId = !string.IsNullOrWhiteSpace(request.SpotifyId)
            ? request.SpotifyId
            : await _repository.GetArtistSourceIdAsync(artistId, SpotifySource, cancellationToken);
        var deezerId = !string.IsNullOrWhiteSpace(request.DeezerId)
            ? request.DeezerId
            : await _repository.GetArtistSourceIdAsync(artistId, DeezerSource, cancellationToken);

        var added = await _repository.AddWatchlistAsync(
            artistId,
            request.ArtistName,
            spotifyId,
            deezerId,
            cancellationToken);
        return CreateAddedResponse(request.ArtistName, added);
    }

    [HttpPost("deezer")]
    public async Task<IActionResult> AddDeezer([FromBody] DeezerWatchlistRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DeezerId) || string.IsNullOrWhiteSpace(request.ArtistName))
        {
            return BadRequest("Deezer ID and artist name are required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await ResolveArtistIdForDeezerAsync(request.DeezerId, request.SpotifyId, cancellationToken);
        await _repository.UpsertArtistSourceIdAsync(artistId, DeezerSource, request.DeezerId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            await _repository.UpsertArtistSourceIdAsync(artistId, SpotifySource, request.SpotifyId, cancellationToken);
        }

        var spotifyId = !string.IsNullOrWhiteSpace(request.SpotifyId)
            ? request.SpotifyId
            : await _repository.GetArtistSourceIdAsync(artistId, SpotifySource, cancellationToken);

        var added = await _repository.AddWatchlistAsync(
            artistId,
            request.ArtistName,
            spotifyId,
            request.DeezerId,
            cancellationToken);
        return CreateAddedResponse(request.ArtistName, added);
    }

    [HttpDelete("{artistId:long}")]
    public async Task<IActionResult> Remove(long artistId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var removed = await _repository.RemoveWatchlistAsync(artistId, cancellationToken);
        return Ok(new { removed });
    }

    [HttpDelete("spotify/{spotifyId}")]
    public async Task<IActionResult> RemoveSpotify(string spotifyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return BadRequest("Spotify ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var removed = await _repository.RemoveWatchlistBySpotifyIdAsync(spotifyId, cancellationToken);
        if (!removed)
        {
            var artistId = await _repository.GetArtistIdBySourceIdAsync(SpotifySource, spotifyId, cancellationToken);
            if (artistId.HasValue)
            {
                removed = await _repository.RemoveWatchlistAsync(artistId.Value, cancellationToken);
            }
        }

        return Ok(new { removed });
    }

    [HttpDelete("apple/{appleId}")]
    public async Task<IActionResult> RemoveApple(string appleId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return BadRequest("Apple ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await _repository.GetArtistIdBySourceIdAsync(AppleSource, appleId, cancellationToken);
        var removed = artistId.HasValue && await _repository.RemoveWatchlistAsync(artistId.Value, cancellationToken);
        return Ok(new { removed });
    }

    [HttpDelete("deezer/{deezerId}")]
    public async Task<IActionResult> RemoveDeezer(string deezerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return BadRequest("Deezer ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await _repository.GetArtistIdBySourceIdAsync(DeezerSource, deezerId, cancellationToken);
        var removed = artistId.HasValue && await _repository.RemoveWatchlistAsync(artistId.Value, cancellationToken);
        return Ok(new { removed });
    }

    [HttpPost("trigger-check")]
    public async Task<IActionResult> TriggerAll(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var items = await _repository.GetWatchlistAsync(cancellationToken);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _artistWatchService.CheckArtistWatchItemAsync(item, cancellationToken);
        }

        return Ok(new { triggered = items.Count });
    }

    [HttpPost("trigger-check/{artistId:long}")]
    public async Task<IActionResult> TriggerOne(long artistId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var items = await _repository.GetWatchlistAsync(cancellationToken);
        var item = items.FirstOrDefault(entry => entry.ArtistId == artistId);
        if (item == null)
        {
            return NotFound("Artist watchlist entry not found.");
        }

        await _artistWatchService.CheckArtistWatchItemAsync(item, cancellationToken);
        return Ok(new { triggered = 1 });
    }

    private ObjectResult DatabaseNotConfigured()
    {
        return StatusCode(503, new { error = "Library DB not configured." });
    }

    private IActionResult CreateAddedResponse(string artistName, object? addedEntry)
    {
        if (addedEntry is null)
        {
            return StatusCode(500, AddWatchlistFailedMessage);
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Watchlist added: {artistName}."));

        return Ok(addedEntry);
    }

    private async Task<long> ResolveArtistIdForSpotifyAsync(string spotifyId, string? deezerId, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetArtistIdBySourceIdAsync(SpotifySource, spotifyId, cancellationToken);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        if (!string.IsNullOrWhiteSpace(deezerId))
        {
            var byDeezer = await _repository.GetArtistIdBySourceIdAsync(DeezerSource, deezerId, cancellationToken);
            if (byDeezer.HasValue)
            {
                return byDeezer.Value;
            }

            if (long.TryParse(deezerId, out var parsed))
            {
                return parsed;
            }
        }

        return GetSyntheticArtistId(SpotifySource, spotifyId);
    }

    private async Task<long> ResolveArtistIdForAppleAsync(string appleId, string? deezerId, string? spotifyId, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetArtistIdBySourceIdAsync(AppleSource, appleId, cancellationToken);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            var bySpotify = await _repository.GetArtistIdBySourceIdAsync(SpotifySource, spotifyId, cancellationToken);
            if (bySpotify.HasValue)
            {
                return bySpotify.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(deezerId))
        {
            var byDeezer = await _repository.GetArtistIdBySourceIdAsync(DeezerSource, deezerId, cancellationToken);
            if (byDeezer.HasValue)
            {
                return byDeezer.Value;
            }

            if (long.TryParse(deezerId, out var parsed))
            {
                return parsed;
            }
        }

        return GetSyntheticArtistId(AppleSource, appleId);
    }

    private async Task<long> ResolveArtistIdForDeezerAsync(string deezerId, string? spotifyId, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetArtistIdBySourceIdAsync(DeezerSource, deezerId, cancellationToken);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        if (long.TryParse(deezerId, out var parsed))
        {
            return parsed;
        }

        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            var bySpotify = await _repository.GetArtistIdBySourceIdAsync(SpotifySource, spotifyId, cancellationToken);
            if (bySpotify.HasValue)
            {
                return bySpotify.Value;
            }
        }

        return GetSyntheticArtistId(DeezerSource, deezerId);
    }

    private static long GetSyntheticArtistId(string source, string sourceId)
    {
        const ulong offset = 1469598103934665603;
        const ulong prime = 1099511628211;
        ulong hash = offset;
        var input = $"{source}:{sourceId}";
        foreach (var ch in input)
        {
            hash ^= (byte)ch;
            hash *= prime;
        }

        var value = unchecked((long)hash);
        if (value >= 0)
        {
            value = -value - 1;
        }
        if (value == 0)
        {
            value = long.MinValue + 1;
        }
        return value;
    }
}
