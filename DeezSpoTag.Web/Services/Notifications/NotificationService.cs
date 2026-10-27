using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Services;

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
        Enqueue(new NotificationWork(null, dedupeKey.Trim(), manuallyResolved, recovery));
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
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly object _enqueueGate = new();
    private Task _enqueueTail = Task.CompletedTask;
    private bool _accepting = true;
    private readonly SemaphoreSlim _webhookWake = new(0, 1);
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

    public void NotifyChanged() => _events.Send("notificationsChanged", new { });

    public void Raise(NotificationRequest request)
    {
        if (!NotificationKinds.IsKnown(request.Kind))
        {
            return;
        }

        Enqueue(new NotificationWork(request, null, false, null));
    }

    private void Enqueue(NotificationWork work)
    {
        lock (_enqueueGate)
        {
            if (!_accepting)
            {
                _logger.LogWarning("Notification service is stopped; work was not accepted.");
                return;
            }
            if (_enqueueTail.IsCompleted && _queue.Writer.TryWrite(work)) return;
            // The sink is synchronous. Own deferred writes, preserve their order and drain
            // them at shutdown without blocking a request thread or dropping older work.
            _enqueueTail = EnqueueAfterAsync(_enqueueTail, work);
        }
    }

    private async Task EnqueueAfterAsync(Task previous, NotificationWork work)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await _queue.Writer.WriteAsync(work).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            _logger.LogWarning("Notification service stopped before pending work could be accepted.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task pending;
        lock (_enqueueGate) { _accepting = false; pending = _enqueueTail; }
        try
        {
            if (ExecuteTask is not null) await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _queue.Writer.TryComplete();
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void WakeWebhookDelivery()
    {
        if (_webhookWake.CurrentCount == 0)
        {
            try { _webhookWake.Release(); }
            catch (SemaphoreFullException) { /* Another producer already signalled. */ }
        }
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

        await DispatchAsync(recovery with
        {
            DedupeKey = $"{recovery.DedupeKey}:{result.IncidentId}"
        }, CancellationToken.None).ConfigureAwait(false);
    }

    public Task<bool> SendWebhookTestAsync(string url, CancellationToken cancellationToken)
        => SendWebhookTestAsync(url, cancellationToken, preferences: null);

    public async Task<bool> SendWebhookTestAsync(
        string url,
        CancellationToken cancellationToken,
        NotificationPreferences? preferences)
    {
        var probe = new NotificationEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = NotificationKinds.RunCompleted,
            DedupeKey = "test",
            Title = "DeezSpoTag test notification",
            Body = "If you can read this, the webhook is configured correctly."
        };
        return await PostWebhookAsync(url, probe, cancellationToken, preferences);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var shutdownRegistration = stoppingToken.Register(() => _queue.Writer.TryComplete());
        try
        {
            await Task.WhenAll(ProcessNotificationsAsync(stoppingToken), DeliverWebhooksAsync(stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _queue.Writer.TryComplete(); }
    }

    private async Task ProcessNotificationsAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
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
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
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

        var added = await _store.AddOrCoalesceAsync(request, preferences.RetentionDays,
            channels.Webhook && !string.IsNullOrWhiteSpace(preferences.WebhookUrl)).ConfigureAwait(false);
        var entry = added.Entry;

        if (channels.InApp)
        {
            try
            {
                _events.Send(added.IsNewIncident ? "notificationRaised" : "notificationsChanged", new
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
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Failed pushing in-app notification.");
            }
        }

        if (entry.WebhookPending) WakeWebhookDelivery();
    }

    private async Task DeliverWebhooksAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = TimeSpan.FromSeconds(30);
            try
            {
                var pending = await _store.GetPendingWebhooksAsync().ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var entry = pending.OrderBy(item => item.WebhookNextAttemptUtc ?? DateTimeOffset.MinValue).FirstOrDefault();
                if (entry is not null)
                {
                    var due = entry.WebhookNextAttemptUtc ?? now;
                    if (due <= now)
                    {
                        var preferences = await _store.LoadPreferencesAsync().ConfigureAwait(false);
                        if (!preferences.Resolve(entry.Kind).Webhook || string.IsNullOrWhiteSpace(preferences.WebhookUrl))
                        {
                            await _store.RecordWebhookAttemptAsync(entry.Id, false, null).ConfigureAwait(false);
                        }
                        else
                        {
                            var delivered = await PostWebhookAsync(preferences.WebhookUrl, entry,
                                stoppingToken, preferences).ConfigureAwait(false);
                            var delay = !delivered && entry.WebhookAttempts < WebhookRetryDelays.Length
                                ? (TimeSpan?)WebhookRetryDelays[entry.WebhookAttempts] : null;
                            await _store.RecordWebhookAttemptAsync(entry.Id, delivered, delay).ConfigureAwait(false);
                        }
                        continue;
                    }
                    wait = due - now < wait ? due - now : wait;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Notification webhook delivery could not complete; pending work is retained.");
                wait = TimeSpan.FromSeconds(2);
            }
            await _webhookWake.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> PostWebhookAsync(
        string url,
        NotificationEntry entry,
        CancellationToken cancellationToken,
        NotificationPreferences? preferences = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var webhookUri)
            || !await OutboundUrlGuard.IsAllowedRemoteUriAsync(
                webhookUri,
                _logger,
                cancellationToken,
                allowPrivateLan: true))
        {
            _logger.LogWarning("Rejected notification webhook URL by remote host policy.");
            return false;
        }

        var transport = preferences ?? NotificationPreferences.CreateDefault();
        var payload = JsonSerializer.Serialize(NotificationTransportAdapter.BuildPayload(entry, transport));

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return true;

            _logger.LogWarning("Notification webhook returned HTTP {Status} for {Kind}.",
                (int)response.StatusCode, entry.Kind);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Notification webhook post failed for {Kind}.", entry.Kind);
        }

        return false;
    }
}
