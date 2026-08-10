using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Web.Services.Notifications;

public sealed class NotificationService : BackgroundService, INotificationSink
{
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

    private readonly Channel<NotificationRequest> _queue =
        Channel.CreateBounded<NotificationRequest>(new BoundedChannelOptions(500)
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

        _queue.Writer.TryWrite(request);
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
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DispatchAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed dispatching {Kind} notification.", request.Kind);
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

        var entry = await _store.AddOrCoalesceAsync(request, preferences.RetentionDays);

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
