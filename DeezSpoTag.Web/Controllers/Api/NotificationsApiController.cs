using DeezSpoTag.Web.Services.Notifications;
using DeezSpoTag.Web.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/notifications")]
[Authorize]
[ApiTokenAwareValidateAntiforgery]
public sealed class NotificationsApiController : ControllerBase
{
    private readonly NotificationStore _store;
    private readonly NotificationService _service;
    private readonly ILogger<NotificationsApiController> _logger;

    public NotificationsApiController(
        NotificationStore store,
        NotificationService service,
        ILogger<NotificationsApiController> logger)
    {
        _store = store;
        _service = service;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetNotifications([FromQuery] bool unreadOnly = false, [FromQuery] int limit = 50)
    {
        var entries = await _store.GetAsync(unreadOnly, limit);
        return Ok(new
        {
            unreadCount = await _store.GetUnreadCountAsync(),
            notifications = entries.Select(entry => new
            {
                entry.Id,
                entry.Kind,
                severity = entry.Severity.ToString(),
                entry.Title,
                entry.Body,
                entry.Link,
                entry.EntityType,
                entry.EntityId,
                entry.OccurrenceCount,
                entry.CreatedUtc,
                entry.LastSeenUtc,
                isRead = entry.IsRead
            })
        });
    }

    [HttpPost("read")]
    public async Task<IActionResult> MarkRead([FromBody] MarkReadRequest request)
    {
        if (request?.Ids is not { Count: > 0 })
        {
            return BadRequest(new { error = "At least one notification id is required." });
        }

        return Ok(new
        {
            updated = await _store.MarkReadAsync(request.Ids),
            unreadCount = await _store.GetUnreadCountAsync()
        });
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead()
        => Ok(new { updated = await _store.MarkAllReadAsync(), unreadCount = 0 });

    [HttpPost("clear")]
    public async Task<IActionResult> Clear([FromBody] MarkReadRequest? request)
    {
        var removed = request?.Ids is { Count: > 0 }
            ? await _store.RemoveAsync(request.Ids)
            : await _store.ClearAsync();
        return Ok(new { removed, unreadCount = await _store.GetUnreadCountAsync() });
    }

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences()
    {
        var preferences = await _store.LoadPreferencesAsync();
        return Ok(BuildPreferencesResponse(preferences));
    }

    [HttpPost("preferences")]
    public async Task<IActionResult> SavePreferences([FromBody] NotificationPreferencesRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { error = "A preferences payload is required." });
        }

        var preferences = await _store.LoadPreferencesAsync();
        if (request.Events is not null)
        {
            foreach (var pair in request.Events.Where(pair => NotificationKinds.IsKnown(pair.Key)))
            {
                preferences.Events[pair.Key] = new NotificationChannelPreference
                {
                    InApp = pair.Value?.InApp ?? true,
                    Webhook = pair.Value?.Webhook ?? false
                };
            }
        }

        if (request.WebhookUrl is not null)
        {
            preferences.WebhookUrl = request.WebhookUrl.Trim();
        }

        if (request.Provider is not null)
        {
            preferences.Provider = NotificationTransportAdapter.ParseProvider(
                request.Provider,
                preferences.ResolvedProvider);
        }

        if (request.ApprisePayloadMode is not null)
        {
            preferences.AppriseMode = NotificationTransportAdapter.ParsePayloadMode(
                request.ApprisePayloadMode,
                preferences.ResolvedApprisePayloadMode);
        }

        if (request.RetentionDays is { } retention)
        {
            preferences.RetentionDays = Math.Clamp(retention, 1, 365);
        }

        return Ok(BuildPreferencesResponse(await _store.SavePreferencesAsync(preferences)));
    }

    [HttpPost("webhook-test")]
    public async Task<IActionResult> TestWebhook(CancellationToken cancellationToken)
    {
        var preferences = await _store.LoadPreferencesAsync();
        if (string.IsNullOrWhiteSpace(preferences.WebhookUrl))
        {
            return BadRequest(new { error = "Save a webhook URL before sending a test." });
        }

        try
        {
            var delivered = await _service.SendWebhookTestAsync(
                preferences.WebhookUrl,
                cancellationToken,
                preferences);
            return delivered
                ? Ok(new { delivered = true })
                : StatusCode(502, new { error = "The webhook endpoint did not accept the test notification." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Notification webhook test failed.");
            return StatusCode(502, new { error = "Failed to reach the webhook endpoint." });
        }
    }

    private static object BuildPreferencesResponse(NotificationPreferences preferences)
        => new
        {
            events = NotificationKinds.All.ToDictionary(
                kind => kind,
                kind => new
                {
                    inApp = preferences.Resolve(kind).InApp,
                    webhook = preferences.Resolve(kind).Webhook
                }),
            provider = NotificationTransportAdapter.ResolveProviderValue(preferences.ResolvedProvider),
            apprisePayloadMode = NotificationTransportAdapter.ResolvePayloadModeValue(preferences.ResolvedApprisePayloadMode),
            hasWebhookUrl = !string.IsNullOrWhiteSpace(preferences.WebhookUrl),
            webhookPreview = preferences.BuildWebhookPreview(),
            preferences.RetentionDays
        };

    public sealed record MarkReadRequest(List<string>? Ids);
    public sealed record NotificationChannelRequest(bool? InApp, bool? Webhook);
    public sealed record NotificationPreferencesRequest(
        Dictionary<string, NotificationChannelRequest>? Events,
        string? WebhookUrl,
        string? Provider,
        string? ApprisePayloadMode,
        int? RetentionDays);
}
