using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Download.Fallback;

namespace DeezSpoTag.Services.Download.Queue;

public static class QueuePreResolutionPayload
{
    private const string ResolutionStatusPascalKey = "ResolutionStatus";
    private const string ResolutionStatusCamelKey = "resolutionStatus";
    private const string ResolutionErrorPascalKey = "ResolutionError";
    private const string ResolutionErrorCamelKey = "resolutionError";

    public const string Pending = "pending";
    public const string Resolving = "resolving";
    public const string Resolved = "resolved";
    public const string Failed = "failed";

    public sealed record ResolutionResult(
        string Engine,
        string? SourceUrl,
        string? Quality,
        int? AutoIndex,
        IReadOnlyList<FallbackPlanStep>? FallbackPlan,
        string? Error,
        string? Isrc = null,
        string? DeezerId = null,
        string? DeezerAlbumId = null,
        string? DeezerArtistId = null,
        string? SpotifyId = null,
        string? SpotifyAlbumId = null,
        string? SpotifyArtistId = null,
        string? AppleId = null,
        string? AppleAlbumId = null,
        string? AppleArtistId = null,
        string? QobuzId = null,
        string? TidalId = null,
        string? AmazonId = null,
        int? DurationMs = null,
        long? DestinationFolderId = null,
        string? ContentType = null,
        string? Album = null,
        string? AlbumArtist = null);

    public static JsonObject ParseOrEmpty(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(payloadJson) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return new JsonObject();
        }
    }

    public static string ReadStatus(JsonObject payload)
        => ReadString(payload, ResolutionStatusPascalKey)
           ?? ReadString(payload, ResolutionStatusCamelKey)
           ?? Pending;

    public static DateTimeOffset? ReadResolvedAt(JsonObject payload)
        => ReadDateTimeOffset(payload, "ResolvedAtUtc")
           ?? ReadDateTimeOffset(payload, "resolvedAtUtc");

    public static DateTimeOffset? ReadFailedAt(JsonObject payload)
        => ReadDateTimeOffset(payload, "ResolutionFailedAtUtc")
           ?? ReadDateTimeOffset(payload, "resolutionFailedAtUtc");

    public static string? ReadResolvedSourceUrl(JsonObject payload)
        => ReadString(payload, "ResolvedSourceUrl")
           ?? ReadString(payload, "resolvedSourceUrl");

    public static string? ReadResolvedEngine(JsonObject payload)
        => ReadString(payload, "ResolvedEngine")
           ?? ReadString(payload, "resolvedEngine");

    public static bool IsResolved(JsonObject payload)
    {
        var status = ReadStatus(payload);
        return string.Equals(status, Resolved, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(ReadResolvedSourceUrl(payload));
    }

    public static bool IsResolving(JsonObject payload)
        => string.Equals(ReadStatus(payload), Resolving, StringComparison.OrdinalIgnoreCase);

    public static bool IsFailedOnCooldown(JsonObject payload, TimeSpan retryDelay, DateTimeOffset now)
    {
        if (!string.Equals(ReadStatus(payload), Failed, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var failedAt = ReadFailedAt(payload);
        return failedAt.HasValue && now - failedAt.Value < retryDelay;
    }

    public static void MarkResolving(JsonObject payload, DateTimeOffset now)
    {
        SetResolutionPair(payload, ResolutionStatusPascalKey, ResolutionStatusCamelKey, Resolving);
        SetResolutionPair(payload, "ResolutionStartedAtUtc", "resolutionStartedAtUtc", now);
        SetResolutionPair(payload, ResolutionErrorPascalKey, ResolutionErrorCamelKey, string.Empty);
    }

    public static void ApplyResolved(JsonObject payload, ResolutionResult result, DateTimeOffset now)
    {
        SetResolutionPair(payload, ResolutionStatusPascalKey, ResolutionStatusCamelKey, Resolved);
        SetResolutionPair(payload, "ResolvedAtUtc", "resolvedAtUtc", now);
        SetResolutionPair(payload, "ResolvedEngine", "resolvedEngine", result.Engine);
        SetResolutionPair(payload, "Engine", "engine", result.Engine);
        SetResolutionPair(payload, "SourceService", "sourceService", result.Engine);
        SetResolutionPair(payload, ResolutionErrorPascalKey, ResolutionErrorCamelKey, string.Empty);

        if (!string.IsNullOrWhiteSpace(result.SourceUrl))
        {
            SetResolutionPair(payload, "ResolvedSourceUrl", "resolvedSourceUrl", result.SourceUrl);
            SetResolutionPair(payload, "SourceUrl", "sourceUrl", result.SourceUrl);
        }

        if (!string.IsNullOrWhiteSpace(result.Quality))
        {
            SetResolutionPair(payload, "ResolvedQuality", "resolvedQuality", result.Quality);
            SetResolutionPair(payload, "Quality", "quality", result.Quality);
        }

        SetResolutionPairIfPresent(payload, "Isrc", "isrc", result.Isrc);
        SetResolutionPairIfPresent(payload, "DeezerId", "deezerId", result.DeezerId);
        SetResolutionPairIfPresent(payload, "DeezerAlbumId", "deezerAlbumId", result.DeezerAlbumId);
        SetResolutionPairIfPresent(payload, "DeezerArtistId", "deezerArtistId", result.DeezerArtistId);
        SetResolutionPairIfPresent(payload, "SpotifyId", "spotifyId", result.SpotifyId);
        SetResolutionPairIfPresent(payload, "SpotifyAlbumId", "spotifyAlbumId", result.SpotifyAlbumId);
        SetResolutionPairIfPresent(payload, "SpotifyArtistId", "spotifyArtistId", result.SpotifyArtistId);
        SetResolutionPairIfPresent(payload, "AppleId", "appleId", result.AppleId);
        SetResolutionPairIfPresent(payload, "AppleAlbumId", "appleAlbumId", result.AppleAlbumId);
        SetResolutionPairIfPresent(payload, "AppleArtistId", "appleArtistId", result.AppleArtistId);
        SetResolutionPairIfPresent(payload, "QobuzId", "qobuzId", result.QobuzId);
        SetResolutionPairIfPresent(payload, "TidalId", "tidalId", result.TidalId);
        SetResolutionPairIfPresent(payload, "AmazonId", "amazonId", result.AmazonId);
        SetResolutionPairIfPresent(payload, "ContentType", "contentType", result.ContentType);
        SetResolutionPairIfPresent(payload, "Album", "album", result.Album);
        SetResolutionPairIfPresent(payload, "CollectionName", "collectionName", result.Album);
        SetResolutionPairIfPresent(payload, "AlbumArtist", "albumArtist", result.AlbumArtist);

        if (result.DurationMs.HasValue && result.DurationMs.Value > 0)
        {
            SetResolutionPair(payload, "DurationMs", "durationMs", result.DurationMs.Value);
        }

        if (result.DestinationFolderId.HasValue && result.DestinationFolderId.Value > 0)
        {
            SetResolutionPair(payload, "DestinationFolderId", "destinationFolderId", result.DestinationFolderId.Value);
        }

        if (result.AutoIndex.HasValue)
        {
            SetResolutionPair(payload, "ResolvedAutoIndex", "resolvedAutoIndex", result.AutoIndex.Value);
            SetResolutionPair(payload, "AutoIndex", "autoIndex", result.AutoIndex.Value);
        }

        if (result.FallbackPlan is { Count: > 0 })
        {
            var plan = JsonSerializer.SerializeToNode(result.FallbackPlan) ?? new JsonArray();
            payload["FallbackPlan"] = plan.DeepClone();
            payload["fallbackPlan"] = plan.DeepClone();
        }
    }

    public static void ApplyFailed(JsonObject payload, string error, DateTimeOffset now)
    {
        SetResolutionPair(payload, ResolutionStatusPascalKey, ResolutionStatusCamelKey, Failed);
        SetResolutionPair(payload, "ResolutionFailedAtUtc", "resolutionFailedAtUtc", now);
        SetResolutionPair(payload, ResolutionErrorPascalKey, ResolutionErrorCamelKey, error);
    }

    public static void ApplyPending(JsonObject payload)
    {
        SetResolutionPair(payload, ResolutionStatusPascalKey, ResolutionStatusCamelKey, Pending);
        SetResolutionPair(payload, ResolutionErrorPascalKey, ResolutionErrorCamelKey, string.Empty);
    }

    private static string? ReadString(JsonObject payload, string key)
    {
        if (payload[key] is not JsonNode node)
        {
            return null;
        }

        var value = node.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonObject payload, string key)
    {
        var value = ReadString(payload, key);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static void SetResolutionPair(JsonObject payload, string pascalKey, string camelKey, string value)
    {
        payload[pascalKey] = value;
        payload[camelKey] = value;
    }

    private static void SetResolutionPair(JsonObject payload, string pascalKey, string camelKey, int value)
    {
        payload[pascalKey] = value;
        payload[camelKey] = value;
    }

    private static void SetResolutionPair(JsonObject payload, string pascalKey, string camelKey, long value)
    {
        payload[pascalKey] = value;
        payload[camelKey] = value;
    }

    private static void SetResolutionPair(JsonObject payload, string pascalKey, string camelKey, DateTimeOffset value)
    {
        var text = value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        payload[pascalKey] = text;
        payload[camelKey] = text;
    }

    private static void SetResolutionPairIfPresent(JsonObject payload, string pascalKey, string camelKey, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            SetResolutionPair(payload, pascalKey, camelKey, value.Trim());
        }
    }
}
