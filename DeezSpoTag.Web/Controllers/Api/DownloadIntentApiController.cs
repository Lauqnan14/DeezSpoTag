using DeezSpoTag.Services.Download.Shared.Models;
using System.Text.Json.Serialization;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/download/intent")]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class DownloadIntentApiController : ControllerBase
{
    private static readonly string[] InternalErrorReasonCodes = { "download_enqueue_internal_error" };

    private readonly DownloadIntentService _intentService;
    private readonly IDownloadIntentBackgroundQueue _backgroundQueue;
    private readonly ILogger<DownloadIntentApiController> _logger;

    public DownloadIntentApiController(
        DownloadIntentService intentService,
        IDownloadIntentBackgroundQueue backgroundQueue,
        ILogger<DownloadIntentApiController> logger)
    {
        _intentService = intentService;
        _backgroundQueue = backgroundQueue;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Enqueue([FromBody] DownloadIntentBatchRequest request)
    {
        var validationResult = ValidateRequest(request);
        if (validationResult is not null)
        {
            return validationResult;
        }

        try
        {
            if (!request.ResolveImmediately)
            {
                return Ok(EnqueueDeferred(request));
            }

            var immediateResponse = await EnqueueImmediatelyAsync(request, CancellationToken.None);
            return Ok(immediateResponse);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, new
            {
                success = false,
                message = "Download request was canceled."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download intent enqueue failed.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                message = "Download enqueue failed due to an internal error.",
                reasonCodes = InternalErrorReasonCodes
            });
        }
    }

    private BadRequestObjectResult? ValidateRequest(DownloadIntentBatchRequest? request)
    {
        if (request?.Intents is { Count: > 0 })
        {
            return null;
        }

        return BadRequest(new { error = "No intents supplied." });
    }

    private object EnqueueDeferred(DownloadIntentBatchRequest request)
    {
        var deferred = 0;
        var skipped = 0;

        foreach (var intent in request.Intents)
        {
            ApplyDestinationDefaults(intent, request);
            if (_backgroundQueue.Enqueue(intent))
            {
                deferred++;
            }
            else
            {
                skipped++;
            }
        }

        return new
        {
            success = deferred > 0,
            queued = Array.Empty<string>(),
            deferred = deferred > 0,
            deferredCount = deferred,
            skipped,
            engine = "background",
            message = deferred > 0
                ? $"Queued {deferred} item(s) for background intent resolution."
                : "Nothing queued."
        };
    }

    private async Task<object> EnqueueImmediatelyAsync(DownloadIntentBatchRequest request, CancellationToken cancellationToken)
    {
        var state = new ImmediateQueueState();
        foreach (var intent in request.Intents)
        {
            ApplyDestinationDefaults(intent, request);
            var result = await _intentService.EnqueueManualAsync(intent, cancellationToken);
            state.Apply(result);
        }

        return new
        {
            success = state.Queued.Count > 0,
            queued = state.Queued,
            skipped = state.Skipped,
            engine = state.Engine,
            message = state.Queued.Count > 0
                ? $"Queued {state.Queued.Count} item(s)."
                : (state.Errors.FirstOrDefault() ?? "Nothing queued."),
            reasonCodes = state.ReasonCodes
        };
    }

    private static void ApplyDestinationDefaults(DownloadIntent intent, DownloadIntentBatchRequest request)
    {
        intent.DestinationFolderId ??= request.DestinationFolderId;
        intent.SecondaryDestinationFolderId ??= request.SecondaryDestinationFolderId;
    }

    private sealed class ImmediateQueueState
    {
        public List<string> Queued { get; } = new();
        public List<string> Errors { get; } = new();
        public List<string> ReasonCodes { get; } = new();
        public int Skipped { get; private set; }
        public string Engine { get; private set; } = string.Empty;

        public void Apply(DownloadIntentResult result)
        {
            if (!string.IsNullOrWhiteSpace(result.Engine))
            {
                Engine = result.Engine;
            }

            if (result.Success && result.Queued.Count > 0)
            {
                Queued.AddRange(result.Queued);
                return;
            }

            Skipped += Math.Max(result.Skipped, 1);
            if (result.SkipReasonCodes.Count > 0)
            {
                ReasonCodes.AddRange(result.SkipReasonCodes);
            }

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                Errors.Add(result.Message);
            }
        }
    }
}

public sealed class DownloadIntentBatchRequest
{
    public List<DownloadIntent> Intents { get; set; } = new();
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? DestinationFolderId { get; set; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? SecondaryDestinationFolderId { get; set; }
    public bool ResolveImmediately { get; set; } = true;
}
