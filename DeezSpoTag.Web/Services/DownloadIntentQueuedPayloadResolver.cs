using System.Text.Json;
using DeezSpoTag.Services.Download.Queue;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadIntentQueuedPayloadResolver : IQueuedDownloadPayloadResolver
{
    private const string FailedMessage = "Track unavailable in enabled download sources.";
    private readonly DownloadIntentService _downloadIntentService;

    public DownloadIntentQueuedPayloadResolver(DownloadIntentService downloadIntentService)
    {
        _downloadIntentService = downloadIntentService;
    }

    public async Task<QueuedDownloadPayloadResolution> ResolveAsync(
        DownloadQueueItem item,
        CancellationToken cancellationToken)
    {
        var originalPayloadJson = item.PayloadJson ?? string.Empty;
        var payload = QueuePreResolutionPayload.ParseOrEmpty(originalPayloadJson);
        var result = await _downloadIntentService.ResolveQueuedPayloadAsync(item, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            QueuePreResolutionPayload.ApplyFailed(payload, FailedMessage, DateTimeOffset.UtcNow);
            return new QueuedDownloadPayloadResolution(
                BuildIdentityUpdateItem(item, payload.ToJsonString(), result.Engine) with
                {
                    Status = "failed",
                    Error = FailedMessage
                },
                FailedMessage);
        }

        QueuePreResolutionPayload.ApplyResolved(payload, result, DateTimeOffset.UtcNow);
        return new QueuedDownloadPayloadResolution(
            BuildIdentityUpdateItem(item, payload.ToJsonString(), result.Engine),
            null);
    }

    private static DownloadQueueItem BuildIdentityUpdateItem(
        DownloadQueueItem current,
        string payloadJson,
        string? resolvedEngine)
    {
        using var document = ParsePayloadDocument(payloadJson);
        var root = document.RootElement;
        return current with
        {
            Engine = FirstNonEmpty(resolvedEngine, ReadString(root, "Engine", "engine"), current.Engine) ?? current.Engine,
            ArtistName = FirstNonEmpty(ReadString(root, "Artist", "artist"), current.ArtistName) ?? current.ArtistName,
            TrackTitle = FirstNonEmpty(ReadString(root, "Title", "title"), current.TrackTitle) ?? current.TrackTitle,
            Isrc = FirstNonEmpty(ReadString(root, "Isrc", "isrc"), current.Isrc),
            DeezerTrackId = FirstNonEmpty(ReadString(root, "DeezerId", "deezerId"), current.DeezerTrackId),
            DeezerAlbumId = FirstNonEmpty(ReadString(root, "DeezerAlbumId", "deezerAlbumId"), current.DeezerAlbumId),
            DeezerArtistId = FirstNonEmpty(ReadString(root, "DeezerArtistId", "deezerArtistId"), current.DeezerArtistId),
            SpotifyTrackId = FirstNonEmpty(ReadString(root, "SpotifyId", "spotifyId"), current.SpotifyTrackId),
            SpotifyAlbumId = FirstNonEmpty(ReadString(root, "SpotifyAlbumId", "spotifyAlbumId"), current.SpotifyAlbumId),
            SpotifyArtistId = FirstNonEmpty(ReadString(root, "SpotifyArtistId", "spotifyArtistId"), current.SpotifyArtistId),
            AppleTrackId = FirstNonEmpty(ReadString(root, "AppleId", "appleId"), current.AppleTrackId),
            AppleAlbumId = FirstNonEmpty(ReadString(root, "AppleAlbumId", "appleAlbumId"), current.AppleAlbumId),
            AppleArtistId = FirstNonEmpty(ReadString(root, "AppleArtistId", "appleArtistId"), current.AppleArtistId),
            QobuzTrackId = FirstNonEmpty(ReadString(root, "QobuzId", "qobuzId"), current.QobuzTrackId),
            TidalTrackId = FirstNonEmpty(ReadString(root, "TidalId", "tidalId"), current.TidalTrackId),
            AmazonTrackId = FirstNonEmpty(ReadString(root, "AmazonId", "amazonId"), current.AmazonTrackId),
            DurationMs = ReadDurationMs(root) ?? current.DurationMs,
            DestinationFolderId = ReadInt64(root, "DestinationFolderId", "destinationFolderId") ?? current.DestinationFolderId,
            ContentType = FirstNonEmpty(ReadString(root, "ContentType", "contentType"), current.ContentType),
            PayloadJson = payloadJson
        };
    }

    private static JsonDocument ParsePayloadDocument(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return JsonDocument.Parse("{}");
        }

        try
        {
            return JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .FirstOrDefault();

    private static string? ReadString(JsonElement root, string pascalName, string camelName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty(pascalName, out var pascalValue) && pascalValue.ValueKind == JsonValueKind.String)
        {
            return pascalValue.GetString();
        }

        return root.TryGetProperty(camelName, out var camelValue) && camelValue.ValueKind == JsonValueKind.String
            ? camelValue.GetString()
            : null;
    }

    private static int? ReadDurationMs(JsonElement root)
    {
        var durationMs = ReadInt32(root, "DurationMs", "durationMs");
        if (durationMs.HasValue && durationMs.Value > 0)
        {
            return durationMs.Value;
        }

        var durationSeconds = ReadInt32(root, "DurationSeconds", "durationSeconds");
        return durationSeconds.HasValue && durationSeconds.Value > 0
            ? durationSeconds.Value * 1000
            : null;
    }

    private static int? ReadInt32(JsonElement root, string pascalName, string camelName)
    {
        var value = ReadInt64(root, pascalName, camelName);
        if (!value.HasValue || value.Value < int.MinValue || value.Value > int.MaxValue)
        {
            return null;
        }

        return (int)value.Value;
    }

    private static long? ReadInt64(JsonElement root, string pascalName, string camelName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryReadInt64(root, pascalName, out var pascalValue))
        {
            return pascalValue;
        }

        return TryReadInt64(root, camelName, out var camelValue) ? camelValue : null;
    }

    private static bool TryReadInt64(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(element.GetString(), out value),
            _ => false
        };
    }
}
