using DeezSpoTag.Web.Services;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/shazam")]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class ShazamRecognitionApiController : ControllerBase
{
    private const string QuickCaptureAttempt = "quick";
    private const string FinalCaptureAttempt = "final";

    private const long MaxUploadBytes = 128 * 1024 * 1024;
    private static readonly TimeSpan LogoResultCacheDuration = TimeSpan.FromMinutes(15);

    private readonly ShazamRecognitionService _recognitionService;
    private readonly ShazamDiscoveryService _discoveryService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<ShazamRecognitionApiController> _logger;

    public ShazamRecognitionApiController(
        ShazamRecognitionService recognitionService,
        ShazamDiscoveryService discoveryService,
        DeezSpoTagSettingsService settingsService,
        IMemoryCache memoryCache,
        ILogger<ShazamRecognitionApiController> logger)
    {
        _recognitionService = recognitionService;
        _discoveryService = discoveryService;
        _settingsService = settingsService;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    [HttpGet("available")]
    public IActionResult Available()
    {
        var available = _recognitionService.IsAvailable;
        return Ok(new
        {
            available,
            error = available ? null : _recognitionService.AvailabilityError
        });
    }

    [HttpGet("logo-result/{clientRequestId}")]
    public IActionResult LogoResult(string clientRequestId)
    {
        var sanitizedClientRequestId = SanitizeClientRequestId(clientRequestId);
        if (string.Equals(sanitizedClientRequestId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { available = false });
        }

        return _memoryCache.TryGetValue(BuildLogoResultCacheKey(sanitizedClientRequestId), out object? payload)
            ? Ok(payload)
            : NotFound(new { available = false });
    }

    [HttpPost("recognize-mic")]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> RecognizeMic(
        [FromForm] IFormFile? audio,
        [FromForm] string? capturePhase,
        [FromForm] string? captureAttempt,
        [FromForm] string? logoSessionId,
        [FromForm] string? clientRequestId,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateRecognizeMicRequest(audio);
        if (validationError is not null)
        {
            return validationError;
        }

        var audioFile = audio!;
        var extension = ResolveAudioExtension(audioFile.FileName);
        var tempPath = Path.Join(Path.GetTempPath(), $"deezspotag-shazam-{Guid.NewGuid():N}{extension}");

        try
        {
            return await ProcessMicRecognitionAsync(
                audioFile,
                tempPath,
                NormalizeCapturePhase(capturePhase),
                NormalizeCaptureAttempt(captureAttempt),
                SanitizeRequestToken(logoSessionId, fallback: "none"),
                SanitizeClientRequestId(clientRequestId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
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

    private async Task<IActionResult> ProcessMicRecognitionAsync(
        IFormFile audioFile,
        string tempPath,
        string capturePhase,
        string captureAttempt,
        string logoSessionId,
        string clientRequestId,
        CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Shazam mic request accepted: phase={CapturePhase}, attempt={CaptureAttempt}, logoSessionId={LogoSessionId}, clientRequestId={ClientRequestId}, bytes={Bytes}, contentType={ContentType}, fileName={FileName}.",
                capturePhase,
                captureAttempt,
                logoSessionId,
                clientRequestId,
                audioFile.Length,
                string.IsNullOrWhiteSpace(audioFile.ContentType) ? "unknown" : audioFile.ContentType,
                string.IsNullOrWhiteSpace(audioFile.FileName) ? "unknown" : audioFile.FileName);
        }

        await CopyUploadedAudioAsync(audioFile, tempPath, cancellationToken);

        var captureDurationSeconds = ResolveCaptureDurationSeconds();
        var signatureWindowSeconds = ResolveMicSignatureWindowSeconds(captureDurationSeconds);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Shazam mic recognition windows: phase={CapturePhase}, attempt={CaptureAttempt}, logoSessionId={LogoSessionId}, clientRequestId={ClientRequestId}, captureDurationSeconds={CaptureDurationSeconds}, signatureWindowSeconds={SignatureWindowSeconds}.",
                capturePhase,
                captureAttempt,
                logoSessionId,
                clientRequestId,
                captureDurationSeconds,
                signatureWindowSeconds);
        }

        var attempt = _recognitionService.RecognizeWithDetails(
            tempPath,
            signatureWindowSeconds,
            ShazamRecognitionService.RecognitionMode.AudioOnly,
            cancellationToken);
        if (!attempt.Matched)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Shazam mic match failed: phase={CapturePhase}, attempt={CaptureAttempt}, logoSessionId={LogoSessionId}, clientRequestId={ClientRequestId}, outcome={Outcome}, error={Error}.",
                    capturePhase,
                    captureAttempt,
                    logoSessionId,
                    clientRequestId,
                    attempt.Outcome,
                    string.IsNullOrWhiteSpace(attempt.Error) ? "none" : attempt.Error);
            }
            return BuildNoMatchResponse(attempt, capturePhase, captureAttempt, logoSessionId, clientRequestId);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Shazam mic matched: phase={CapturePhase}, attempt={CaptureAttempt}, logoSessionId={LogoSessionId}, clientRequestId={ClientRequestId}, title={Title}, artist={Artist}, trackId={TrackId}.",
                capturePhase,
                captureAttempt,
                logoSessionId,
                clientRequestId,
                attempt.Recognition?.Title,
                attempt.Recognition?.Artist,
                attempt.Recognition?.TrackId);
        }

        try
        {
            var payload = await BuildMatchPayloadAsync(
                attempt.Recognition!,
                capturePhase,
                captureAttempt,
                logoSessionId,
                clientRequestId,
                cancellationToken);
            CacheLogoResult(clientRequestId, payload);
            return Ok(payload);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Shazam enrichment failed after a successful recognition match.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                matched = true,
                capturePhase,
                captureAttempt,
                logoSessionId,
                clientRequestId,
                reason = "enrichment_failed",
                recognition = new
                {
                    title = attempt.Recognition!.Title,
                    artist = attempt.Recognition.Artist,
                    artists = attempt.Recognition.Artists,
                    isrc = attempt.Recognition.Isrc,
                    durationMs = attempt.Recognition.DurationMs,
                    trackId = attempt.Recognition.TrackId,
                    url = attempt.Recognition.Url,
                    genre = attempt.Recognition.Genre,
                    album = attempt.Recognition.Album,
                    label = attempt.Recognition.Label,
                    releaseDate = attempt.Recognition.ReleaseDate,
                    artworkUrl = attempt.Recognition.ArtworkUrl,
                    artworkHqUrl = attempt.Recognition.ArtworkHqUrl,
                    key = attempt.Recognition.Key
                },
                error = "Shazam recognized the audio, but result enrichment failed."
            });
        }
    }

    private static string NormalizeCapturePhase(string? capturePhase)
    {
        var value = (capturePhase ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            QuickCaptureAttempt => QuickCaptureAttempt,
            FinalCaptureAttempt => FinalCaptureAttempt,
            "file" => "file",
            "logo" => "logo",
            _ => "unknown"
        };
    }

    private static string NormalizeCaptureAttempt(string? captureAttempt)
    {
        var value = (captureAttempt ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            QuickCaptureAttempt => QuickCaptureAttempt,
            FinalCaptureAttempt => FinalCaptureAttempt,
            "file" => "file",
            _ => "none"
        };
    }

    private static string SanitizeClientRequestId(string? clientRequestId)
        => SanitizeRequestToken(clientRequestId, fallback: "none");

    private static string SanitizeRequestToken(string? value, string fallback)
    {
        var sanitized = DeezSpoTag.Core.Security.LogSanitizer.OneLine(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return fallback;
        }

        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }

    private int ResolveCaptureDurationSeconds()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            return Math.Clamp(settings.ShazamCaptureDurationSeconds, 3, 20);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Failed to resolve Shazam capture duration from settings. Falling back to default.");
            return 11;
        }
    }

    private static int ResolveMicSignatureWindowSeconds(int captureDurationSeconds)
    {
        // Match the reference Shazam usage where recognize() commonly runs with a 5s search segment.
        return Math.Clamp(Math.Min(captureDurationSeconds, 5), 3, 5);
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
            var availabilityError = _recognitionService.AvailabilityError;
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    error = string.IsNullOrWhiteSpace(availabilityError)
                        ? "Shazam recognition is unavailable."
                        : availabilityError
                });
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

    private ObjectResult BuildNoMatchResponse(
        ShazamRecognitionAttempt attempt,
        string capturePhase,
        string captureAttempt,
        string logoSessionId,
        string clientRequestId)
    {
        return attempt.Outcome switch
        {
            ShazamRecognitionOutcome.RecognizerUnavailable => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    matched = false,
                    capturePhase,
                    captureAttempt,
                    logoSessionId,
                    clientRequestId,
                    reason = "recognizer_unavailable",
                    error = attempt.Error ?? "Shazam recognizer is unavailable."
                }),
            ShazamRecognitionOutcome.RecognizerError => StatusCode(
                StatusCodes.Status502BadGateway,
                new
                {
                    matched = false,
                    capturePhase,
                    captureAttempt,
                    logoSessionId,
                    clientRequestId,
                    reason = "recognizer_error",
                    error = attempt.Error ?? "Shazam recognizer failed."
                }),
            ShazamRecognitionOutcome.InvalidInput => BadRequest(
                new
                {
                    matched = false,
                    capturePhase,
                    captureAttempt,
                    logoSessionId,
                    clientRequestId,
                    reason = "invalid_audio",
                    error = attempt.Error ?? "Audio sample is invalid."
                }),
            _ => Ok(
                new
                {
                    matched = false,
                    capturePhase,
                    captureAttempt,
                    logoSessionId,
                    clientRequestId,
                    reason = "no_match"
                })
        };
    }

    private async Task<object> BuildMatchPayloadAsync(
        ShazamRecognitionInfo recognition,
        string capturePhase,
        string captureAttempt,
        string logoSessionId,
        string clientRequestId,
        CancellationToken cancellationToken)
    {
        var trackId = recognition.TrackId;
        var query = BuildQuery(recognition);

        ShazamTrackCard? track = null;
        IReadOnlyList<ShazamTrackCard> related = Array.Empty<ShazamTrackCard>();
        var searchTask = SafeSearchTracksAsync(query, cancellationToken);
        if (!string.IsNullOrWhiteSpace(trackId))
        {
            var trackTask = SafeGetTrackAsync(trackId, cancellationToken);
            var relatedTask = SafeGetRelatedTracksAsync(trackId, cancellationToken);
            await Task.WhenAll(trackTask, relatedTask, searchTask);
            track = await trackTask;
            related = await relatedTask;
        }

        var searchResults = await searchTask;

        return BuildMatchPayload(
            new ShazamLogoMatchPayload(
                Recognition: recognition,
                Query: query,
                Track: track,
                Related: related,
                SearchResults: searchResults,
                CapturePhase: capturePhase,
                CaptureAttempt: captureAttempt,
                LogoSessionId: logoSessionId,
                ClientRequestId: clientRequestId));
    }

    private static object BuildMatchPayload(ShazamLogoMatchPayload payload)
    {
        var relatedList = payload.Related ?? Array.Empty<ShazamTrackCard>();
        var searchList = payload.SearchResults ?? Array.Empty<ShazamTrackCard>();
        var similarList = MergeSimilarCards(relatedList, searchList, payload.Track, payload.Recognition);

        return BuildMatchPayloadObject(payload, relatedList, searchList, similarList);
    }

    private static object BuildMatchPayloadObject(
        ShazamLogoMatchPayload payload,
        IReadOnlyList<ShazamTrackCard> relatedList,
        IReadOnlyList<ShazamTrackCard> searchList,
        List<ShazamTrackCard> similarList)
    {
        var recognition = payload.Recognition;
        return new
        {
            matched = true,
            capturePhase = payload.CapturePhase,
            captureAttempt = payload.CaptureAttempt,
            logoSessionId = payload.LogoSessionId,
            clientRequestId = payload.ClientRequestId,
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
            query = payload.Query,
            track = payload.Track,
            enrichment = new
            {
                trackResolved = payload.Track != null,
                relatedCount = relatedList.Count,
                searchResultCount = searchList.Count,
                similarCount = similarList.Count
            },
            related = relatedList,
            similar = similarList,
            searchResults = searchList
        };
    }

    private sealed record ShazamLogoMatchPayload(
        ShazamRecognitionInfo Recognition,
        string? Query,
        ShazamTrackCard? Track,
        IReadOnlyList<ShazamTrackCard> Related,
        IReadOnlyList<ShazamTrackCard> SearchResults,
        string CapturePhase,
        string CaptureAttempt,
        string LogoSessionId,
        string ClientRequestId);

    private static List<ShazamTrackCard> MergeSimilarCards(
        IReadOnlyList<ShazamTrackCard> related,
        IReadOnlyList<ShazamTrackCard> searchResults,
        ShazamTrackCard? matchedTrack,
        ShazamRecognitionInfo recognition)
    {
        var cards = new List<ShazamTrackCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedIdentity = BuildCardIdentity(matchedTrack)
            ?? BuildRecognitionIdentity(recognition);

        AddCards(related, cards, seen, matchedIdentity);
        AddCards(searchResults, cards, seen, matchedIdentity);
        return cards;
    }

    private static void AddCards(
        IReadOnlyList<ShazamTrackCard> source,
        List<ShazamTrackCard> destination,
        HashSet<string> seen,
        string? matchedIdentity)
    {
        foreach (var card in source)
        {
            if (card is null)
            {
                continue;
            }

            var identity = BuildCardIdentity(card);
            if (string.IsNullOrWhiteSpace(identity)
                || string.Equals(identity, matchedIdentity, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(identity))
            {
                continue;
            }

            destination.Add(card);
            if (destination.Count >= 20)
            {
                return;
            }
        }
    }

    private static string? BuildCardIdentity(ShazamTrackCard? card)
    {
        if (card is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(card.Id))
        {
            return $"id:{card.Id.Trim()}";
        }

        return BuildTextIdentity(card.Title, card.Artist);
    }

    private static string? BuildRecognitionIdentity(ShazamRecognitionInfo recognition)
    {
        if (!string.IsNullOrWhiteSpace(recognition.TrackId))
        {
            return $"id:{recognition.TrackId.Trim()}";
        }

        return BuildTextIdentity(recognition.Title, recognition.Artist);
    }

    private static string? BuildTextIdentity(string? title, string? artist)
    {
        var normalizedTitle = (title ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedArtist = (artist ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedArtist)
            ? null
            : $"ta:{normalizedTitle}|{normalizedArtist}";
    }

    private void CacheLogoResult(string clientRequestId, object payload)
    {
        if (string.Equals(clientRequestId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _memoryCache.Set(
            BuildLogoResultCacheKey(clientRequestId),
            payload,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = LogoResultCacheDuration,
                SlidingExpiration = TimeSpan.FromMinutes(5),
                Size = 1
            });
    }

    private static string BuildLogoResultCacheKey(string clientRequestId)
        => $"shazam:logo-result:{clientRequestId}";

    private async Task<IReadOnlyList<ShazamTrackCard>> SafeSearchTracksAsync(string? query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ShazamTrackCard>();
        }

        try
        {
            return await _discoveryService.SearchTracksAsync(query, limit: 20, offset: 0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Shazam search lookup failed for query '{Query}'.", query);
            return Array.Empty<ShazamTrackCard>();
        }
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
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
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
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Shazam related-track lookup failed for trackId {TrackId}.", trackId);
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
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // ignore temp cleanup errors
        }
    }
}
