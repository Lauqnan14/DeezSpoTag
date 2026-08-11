using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Web.Services.Notifications;

public sealed class NotificationService : BackgroundService, INotificationSink
{
    void INotificationSink.Resolve(string dedupeKey, bool manuallyResolved, string? recoveryTitle, string? recoveryBody)
    {
        if (string.IsNullOrWhiteSpace(dedupeKey))
        {
            return;
        }

        var recovery = string.IsNullOrWhiteSpace(recoveryTitle)
            ? null
            : new NotificationRequest(
                NotificationKinds.ProviderRecovered,
                recoveryTitle,
                recoveryBody ?? string.Empty,
                NotificationSeverity.Info,
                $"{dedupeKey}:recovered");
        _queue.Writer.TryWrite(new NotificationWork(null, dedupeKey.Trim(), manuallyResolved, recovery));
    }

    void INotificationSink.Raise(
        string kind,
        string title,
        string body,
        string severity,
        string? dedupeKey,
        string? entityType,
        string? entityId,
        string? link)
        => Raise(new NotificationRequest(
            kind,
            title,
            body,
            Enum.TryParse<NotificationSeverity>(severity, ignoreCase: true, out var parsed)
                ? parsed
                : NotificationSeverity.Info,
            dedupeKey,
            entityType,
            entityId,
            link));

    private static readonly TimeSpan[] WebhookRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private sealed record NotificationWork(
        NotificationRequest? Raise,
        string? ResolveKey,
        bool ManuallyResolved,
        NotificationRequest? Recovery);

    private readonly Channel<NotificationWork> _queue =
        Channel.CreateBounded<NotificationWork>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly NotificationStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDeezSpoTagListener _events;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        NotificationStore store,
        IHttpClientFactory httpClientFactory,
        IDeezSpoTagListener events,
        ILogger<NotificationService> logger)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _events = events;
        _logger = logger;
    }

    public void Raise(NotificationRequest request)
    {
        if (!NotificationKinds.IsKnown(request.Kind))
        {
            return;
        }

        _queue.Writer.TryWrite(new NotificationWork(request, null, false, null));
    }

    /// <summary>
    /// Closes an open incident. When the condition cleared on its own the recovery is announced;
    /// when the user acted on it themselves it is closed silently.
    /// </summary>
    public async Task ResolveIncidentAsync(
        string dedupeKey,
        bool manuallyResolved,
        NotificationRequest? recovery = null)
    {
        var result = await _store.ResolveIncidentAsync(dedupeKey, manuallyResolved);
        if (!result.HadOpenIncident || manuallyResolved || recovery is null)
        {
            return;
        }

        Raise(recovery);
    }

    public async Task<bool> SendWebhookTestAsync(string url, CancellationToken cancellationToken)
    {
        var probe = new NotificationEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = NotificationKinds.RunCompleted,
            DedupeKey = "test",
            Title = "DeezSpoTag test notification",
            Body = "If you can read this, the webhook is configured correctly."
        };
        return await PostWebhookAsync(url, probe, singleAttempt: true, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (work.ResolveKey is not null)
                {
                    await ResolveIncidentAsync(work.ResolveKey, work.ManuallyResolved, work.Recovery);
                }
                else if (work.Raise is not null)
                {
                    await DispatchAsync(work.Raise, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed dispatching {Kind} notification.", work.Raise?.Kind ?? "resolve");
            }
        }
    }

    private async Task DispatchAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        var preferences = await _store.LoadPreferencesAsync();
        var channels = preferences.Resolve(request.Kind);
        if (!channels.InApp && !channels.Webhook)
        {
            return;
        }

        var added = await _store.AddOrCoalesceAsync(request, preferences.RetentionDays);
        var entry = added.Entry;
        if (!added.IsNewIncident)
        {
            // Same incident still open: counted, never re-announced.
            return;
        }

        if (channels.InApp)
        {
            try
            {
                _events.Send("notificationRaised", new
                {
                    entry.Id,
                    entry.Kind,
                    severity = entry.Severity.ToString(),
                    entry.Title,
                    entry.Body,
                    entry.Link,
                    entry.OccurrenceCount,
                    unreadCount = await _store.GetUnreadCountAsync()
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed pushing in-app notification.");
            }
        }

        if (channels.Webhook && !string.IsNullOrWhiteSpace(preferences.WebhookUrl))
        {
            await PostWebhookAsync(preferences.WebhookUrl, entry, singleAttempt: false, cancellationToken);
        }
    }

    private async Task<bool> PostWebhookAsync(
        string url,
        NotificationEntry entry,
        bool singleAttempt,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            entry.Id,
            entry.Kind,
            severity = entry.Severity.ToString(),
            entry.Title,
            entry.Body,
            entry.EntityType,
            entry.EntityId,
            entry.Link,
            entry.OccurrenceCount,
            timestamp = entry.LastSeenUtc
        });

        var attempts = singleAttempt ? 1 : WebhookRetryDelays.Length + 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var response = await client.PostAsync(url, content, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                _logger.LogWarning(
                    "Notification webhook returned HTTP {Status} for {Kind}.",
                    (int)response.StatusCode,
                    entry.Kind);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Notification webhook post failed for {Kind}.", entry.Kind);
            }

            if (attempt < attempts - 1)
            {
                await Task.Delay(WebhookRetryDelays[attempt], cancellationToken);
            }
        }

        return false;
    }
}
