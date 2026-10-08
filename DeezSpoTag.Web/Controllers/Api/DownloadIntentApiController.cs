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
    private readonly ILogger<DownloadIntentApiController> _logger;

    public DownloadIntentApiController(
        DownloadIntentService intentService,
        ILogger<DownloadIntentApiController> logger)
    {
        _intentService = intentService;
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
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
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
        if (request?.Intents is not { Count: > 0 })
        {
            return BadRequest(new { error = "No intents supplied." });
        }

        // Checked per intent rather than once for the batch, because a batch may legitimately mix a chosen
        // Soulseek file with a library track, and only the first kind has to carry a peer. A null element is a
        // malformed body rather than a validation failure, and it is answered as such: reading it here would
        // turn a bad request into a server error.
        foreach (var intent in request.Intents)
        {
            if (intent is null)
            {
                return BadRequest(new { error = "An intent in the request was empty." });
            }

            if (ValidateSoulseekCandidateIntent(intent) is { } pinError)
            {
                return BadRequest(new { error = pinError });
            }
        }

        return null;
    }

    /// <summary>
    ///     Checks that a request made from the Soulseek results tab still names the file the reader chose.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         That tab queues a file, not a track: the reader picked a peer and a path out of a result list. If
    ///         either half is missing by the time the request arrives, the item can only be a fresh search, which
    ///         downloads something like the file rather than the file - and the reader is told nothing until the
    ///         queue reports a failure that looks like the network's fault.
    ///     </para>
    ///     <para>
    ///         The rule keys off <c>SourceService</c>, not <c>PreferredEngine</c>. A library or automation request
    ///         that prefers Soulseek has no peer file to give and never will, so keying off the engine would
    ///         refuse every track queued from anywhere else in the app.
    ///     </para>
    ///     <para>
    ///         The destination is not inspected here. That check belongs to the existing destination validation,
    ///         and the reader never sees a local path.
    ///     </para>
    /// </remarks>
    /// <returns>The message to refuse the request with, or <see langword="null" /> when it is acceptable.</returns>
    private static string? ValidateSoulseekCandidateIntent(DownloadIntent intent)
    {
        if (!string.Equals(intent.SourceService, "soulseek", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hasPeer = !string.IsNullOrWhiteSpace(intent.SoulseekUsername);
        var hasPath = !string.IsNullOrWhiteSpace(intent.SoulseekRemotePath);
        if (hasPeer && hasPath)
        {
            return null;
        }

        return "The selected Soulseek file is missing its peer or remote path. Refresh the Soulseek results and select it again.";
    }

    private async Task<object> EnqueueImmediatelyAsync(DownloadIntentBatchRequest request, CancellationToken cancellationToken)
    {
        var state = new ImmediateQueueState();
        foreach (var intent in request.Intents)
        {
            ApplyDestinationDefaults(intent, request);
            var result = await _intentService.EnqueueManualVisibleAsync(intent, cancellationToken);
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
