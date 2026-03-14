using System.Text.Json;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DeezSpoTag.Web.Services;

public sealed class SignalRDeezSpoTagListener : IDeezSpoTagListener
{
    private readonly IHubContext<DeezerQueueHub> _hubContext;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly ILogger<SignalRDeezSpoTagListener> _logger;

    public SignalRDeezSpoTagListener(
        IHubContext<DeezerQueueHub> hubContext,
        DownloadQueueRepository queueRepository,
        ILogger<SignalRDeezSpoTagListener> logger)
    {
        _hubContext = hubContext;
        _queueRepository = queueRepository;
        _logger = logger;
    }

    public void Send(string eventName, object? data = null)
    {
        if (data != null)
        {
            _ = _hubContext.Clients.All.SendAsync(eventName, data);
            if (eventName == "updateQueue" || eventName == "downloadProgress")
            {
                _ = PersistQueueProgressAsync(data);
            }
        }
    }

    private async Task PersistQueueProgressAsync(object data)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(data));
            var root = doc.RootElement;
            var payload = root.TryGetProperty("updateData", out var updateData) && updateData.ValueKind == JsonValueKind.Object
                ? updateData
                : root;

            var uuid = GetString(payload, "uuid");
            if (string.IsNullOrWhiteSpace(uuid))
            {
                return;
            }

            var progress = GetDouble(payload, "progress");
            var downloaded = GetInt(payload, "downloaded");
            var failed = GetInt(payload, "failed");

            if (progress == null && downloaded == null && failed == null)
            {
                return;
            }

            await _queueRepository.UpdateProgressAsync(
                uuid,
                progress,
                downloaded,
                failed,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to persist queue progress update.");
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static double? GetDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var textNumber))
        {
            return textNumber;
        }

        return null;
    }

    private static int? GetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var textNumber))
        {
            return textNumber;
        }

        return null;
    }
}
