using DeezSpoTag.Web.Services;
using DeezSpoTag.Services.Settings;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/shazam")]
public sealed class ShazamRecognitionApiController : ControllerBase
{
    private const long MaxUploadBytes = 16 * 1024 * 1024;

    private readonly ShazamRecognitionService _recognitionService;
    private readonly ShazamDiscoveryService _discoveryService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<ShazamRecognitionApiController> _logger;

    public ShazamRecognitionApiController(
        ShazamRecognitionService recognitionService,
        ShazamDiscoveryService discoveryService,
        DeezSpoTagSettingsService settingsService,
        ILogger<ShazamRecognitionApiController> logger)
    {
        _recognitionService = recognitionService;
        _discoveryService = discoveryService;
        _settingsService = settingsService;
        _logger = logger;
    }

    [HttpGet("available")]
    public IActionResult Available()
    {
        return Ok(new
        {
            available = _recognitionService.IsAvailable
        });
    }

    [HttpPost("recognize-mic")]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> RecognizeMic([FromForm] IFormFile? audio, CancellationToken cancellationToken)
    {
        var validationError = ValidateRecognizeMicRequest(audio);
        if (validationError is not null)
        {
            return validationError;
        }

        var extension = ResolveAudioExtension(audio!.FileName);
        var tempPath = Path.Combine(Path.GetTempPath(), $"deezspotag-shazam-{Guid.NewGuid():N}{extension}");

        try
        {
            await CopyUploadedAudioAsync(audio, tempPath, cancellationToken);

            var captureDurationSeconds = ResolveCaptureDurationSeconds();
            var attempt = _recognitionService.RecognizeWithDetails(
                tempPath,
                captureDurationSeconds,
                cancellationToken);
            if (!attempt.Matched)
            {
                return BuildNoMatchResponse(attempt);
            }

            try
            {
                return Ok(await BuildMatchPayloadAsync(attempt.Recognition!, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Shazam enrichment failed after a successful recognition match.");
                return Ok(BuildMinimalMatchPayload(attempt.Recognition!));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shazam mic recognition failed.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "Shazam recognition failed."
            });
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private int ResolveCaptureDurationSeconds()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            return Math.Clamp(settings.ShazamCaptureDurationSeconds, 3, 20);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve Shazam capture duration from settings. Falling back to default.");
            return 11;
        }
    }

    private IActionResult? ValidateRecognizeMicRequest(IFormFile? audio)
    {
        if (audio == null || audio.Length <= 0)
        {
            return BadRequest(new { error = "Audio sample is required." });
        }

        if (audio.Length > MaxUploadBytes)
        {
            return BadRequest(new { error = "Audio sample is too large." });
        }

        if (!_recognitionService.IsAvailable)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Shazam recognition is unavailable." });
        }

        return null;
    }

    private static string ResolveAudioExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10)
        {
            return ".wav";
        }

        return extension;
    }

    private static async Task CopyUploadedAudioAsync(IFormFile audio, string tempPath, CancellationToken cancellationToken)
    {
        await using var stream = System.IO.File.Create(tempPath);
        await audio.CopyToAsync(stream, cancellationToken);
    }

    private ObjectResult BuildNoMatchResponse(ShazamRecognitionAttempt attempt)
    {
        return attempt.Outcome switch
        {
            ShazamRecognitionOutcome.RecognizerUnavailable => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    matched = false,
                    reason = "recognizer_unavailable",
                    error = attempt.Error ?? "Shazam recognizer is unavailable."
                }),
            ShazamRecognitionOutcome.RecognizerError => StatusCode(
                StatusCodes.Status502BadGateway,
                new
                {
                    matched = false,
                    reason = "recognizer_error",
                    error = attempt.Error ?? "Shazam recognizer failed."
                }),
            ShazamRecognitionOutcome.InvalidInput => BadRequest(
                new
                {
                    matched = false,
                    reason = "invalid_audio",
                    error = attempt.Error ?? "Audio sample is invalid."
                }),
            _ => Ok(
                new
                {
                    matched = false,
                    reason = "no_match"
                })
        };
    }

    private async Task<object> BuildMatchPayloadAsync(
        ShazamRecognitionInfo recognition,
        CancellationToken cancellationToken)
    {
        var trackId = recognition.TrackId;
        var query = BuildQuery(recognition);

        ShazamTrackCard? track = null;
        IReadOnlyList<ShazamTrackCard> related = Array.Empty<ShazamTrackCard>();
        IReadOnlyList<ShazamTrackCard> fallbackSearch = Array.Empty<ShazamTrackCard>();

        if (!string.IsNullOrWhiteSpace(trackId))
        {
            track = await SafeGetTrackAsync(trackId, cancellationToken);
            related = await SafeGetRelatedTracksAsync(trackId, cancellationToken);
        }

        if ((track == null || related.Count == 0) && !string.IsNullOrWhiteSpace(query))
        {
            fallbackSearch = await SafeSearchTracksAsync(query, cancellationToken);
            if (track == null && fallbackSearch.Count > 0)
            {
                track = fallbackSearch[0];
            }
        }

        return BuildMatchPayload(recognition, query, track, related, fallbackSearch);
    }

    private static object BuildMinimalMatchPayload(ShazamRecognitionInfo recognition)
    {
        var query = BuildQuery(recognition);
        return BuildMatchPayload(recognition, query, track: null, related: Array.Empty<ShazamTrackCard>(), fallbackSearch: Array.Empty<ShazamTrackCard>());
    }

    private static object BuildMatchPayload(
        ShazamRecognitionInfo recognition,
        string? query,
        ShazamTrackCard? track,
        IReadOnlyList<ShazamTrackCard> related,
        IReadOnlyList<ShazamTrackCard> fallbackSearch)
    {
        return new
        {
            matched = true,
            recognition = new
            {
                title = recognition.Title,
                artist = recognition.Artist,
                artists = recognition.Artists,
                isrc = recognition.Isrc,
                durationMs = recognition.DurationMs,
                trackId = recognition.TrackId,
                url = recognition.Url,
                genre = recognition.Genre,
                album = recognition.Album,
                label = recognition.Label,
                releaseDate = recognition.ReleaseDate,
                artworkUrl = recognition.ArtworkUrl,
                artworkHqUrl = recognition.ArtworkHqUrl,
                key = recognition.Key
            },
            query,
            track,
            related,
            fallbackSearch,
            relatedFallbackUsed = false
        };
    }

    private async Task<ShazamTrackCard?> SafeGetTrackAsync(string trackId, CancellationToken cancellationToken)
    {
        try
        {
            return await _discoveryService.GetTrackAsync(trackId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shazam track enrichment lookup failed for trackId {TrackId}.", trackId);
            return null;
        }
    }

    private async Task<IReadOnlyList<ShazamTrackCard>> SafeGetRelatedTracksAsync(string trackId, CancellationToken cancellationToken)
    {
        try
        {
            return await _discoveryService.GetRelatedTracksAsync(trackId, limit: 20, offset: 0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shazam related-track lookup failed for trackId {TrackId}.", trackId);
            return Array.Empty<ShazamTrackCard>();
        }
    }

    private async Task<IReadOnlyList<ShazamTrackCard>> SafeSearchTracksAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            return await _discoveryService.SearchTracksAsync(query, limit: 20, offset: 0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shazam fallback search failed for query {Query}.", query);
            return Array.Empty<ShazamTrackCard>();
        }
    }

    private static string? BuildQuery(ShazamRecognitionInfo info)
    {
        var parts = new[] { info.Title, info.Artist }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();

        if (parts.Count == 0)
        {
            return null;
        }

        return string.Join(" ", parts);
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch
        {
            // ignore temp cleanup errors
        }
    }
}
