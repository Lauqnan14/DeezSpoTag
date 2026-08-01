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
        string? AppleAlbumName = null,
        string? AppleArtistName = null,
        string? AppleIsrc = null,
        int? AppleDurationMs = null,
        string? QobuzId = null,
        string? TidalId = null,
        string? AmazonId = null,
        int? DurationMs = null,
        long? DestinationFolderId = null,
        string? ContentType = null,
        string? Album = null,
        string? AlbumArtist = null,
        ResolvedMetadata? Metadata = null);

    public sealed record ResolvedMetadata(
        string? Title = null,
        string? Artist = null,
        string? Album = null,
        string? AlbumArtist = null,
        string? Cover = null,
        IReadOnlyList<string>? Genres = null,
        string? Label = null,
        string? Copyright = null,
        bool? Explicit = null,
        string? Composer = null,
        string? ReleaseDate = null,
        int? TrackNumber = null,
        int? DiscNumber = null,
        int? TrackTotal = null,
        int? DiscTotal = null,
        string? Url = null,
        string? Barcode = null,
        double? Danceability = null,
        double? Energy = null,
        double? Valence = null,
        double? Acousticness = null,
        double? Instrumentalness = null,
        double? Speechiness = null,
        double? Loudness = null,
        double? Tempo = null,
        int? TimeSignature = null,
        double? Liveness = null,
        string? MusicKey = null);

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
        var resolvedAutoIndex = ResolveCoherentAutoIndex(result);
        SetResolutionPair(payload, ResolutionStatusPascalKey, ResolutionStatusCamelKey, Resolved);
        SetResolutionPair(payload, "ResolvedAtUtc", "resolvedAtUtc", now);
        SetResolutionPair(payload, "ResolvedEngine", "resolvedEngine", result.Engine);
        SetResolutionPair(payload, "Engine", "engine", result.Engine);
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
        SetResolutionPairIfPresent(payload, "AppleAlbumName", "appleAlbumName", result.AppleAlbumName);
        SetResolutionPairIfPresent(payload, "AppleArtistName", "appleArtistName", result.AppleArtistName);
        SetResolutionPairIfPresent(payload, "AppleIsrc", "appleIsrc", result.AppleIsrc);
        if (result.AppleDurationMs.HasValue && result.AppleDurationMs.Value > 0)
        {
            SetResolutionPair(payload, "AppleDurationMs", "appleDurationMs", result.AppleDurationMs.Value);
        }
        SetResolutionPairIfPresent(payload, "QobuzId", "qobuzId", result.QobuzId);
        SetResolutionPairIfPresent(payload, "TidalId", "tidalId", result.TidalId);
        SetResolutionPairIfPresent(payload, "AmazonId", "amazonId", result.AmazonId);
        SetResolutionPairIfPresent(payload, "ContentType", "contentType", result.ContentType);
        SetResolutionPairIfPresent(payload, "Album", "album", result.Album);
        SetResolutionPairIfPresent(payload, "CollectionName", "collectionName", result.Album);
        SetResolutionPairIfPresent(payload, "AlbumArtist", "albumArtist", result.AlbumArtist);
        ApplyResolvedMetadata(payload, result.Metadata);

        if (result.DurationMs.HasValue && result.DurationMs.Value > 0)
        {
            SetResolutionPair(payload, "DurationMs", "durationMs", result.DurationMs.Value);
            var durationSeconds = Math.Max(1, (int)Math.Round(result.DurationMs.Value / 1000d));
            SetResolutionPair(payload, "DurationSeconds", "durationSeconds", durationSeconds);
        }

        if (result.DestinationFolderId.HasValue && result.DestinationFolderId.Value > 0)
        {
            SetResolutionPair(payload, "DestinationFolderId", "destinationFolderId", result.DestinationFolderId.Value);
        }

        if (resolvedAutoIndex.HasValue)
        {
            SetResolutionPair(payload, "ResolvedAutoIndex", "resolvedAutoIndex", resolvedAutoIndex.Value);
            SetResolutionPair(payload, "AutoIndex", "autoIndex", resolvedAutoIndex.Value);
        }

        if (result.FallbackPlan is { Count: > 0 })
        {
            var plan = JsonSerializer.SerializeToNode(result.FallbackPlan) ?? new JsonArray();
            payload["FallbackPlan"] = plan.DeepClone();
            payload["fallbackPlan"] = plan.DeepClone();
        }
    }

    private static int? ResolveCoherentAutoIndex(ResolutionResult result)
    {
        if (result.FallbackPlan is not { Count: > 0 } || string.IsNullOrWhiteSpace(result.Quality))
        {
            return result.AutoIndex;
        }

        var matchingIndexes = result.FallbackPlan
            .Select((step, index) => (step, index))
            .Where(candidate =>
                string.Equals(candidate.step.Engine, result.Engine, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.step.Quality, result.Quality, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.index)
            .ToList();
        if (matchingIndexes.Count == 0)
        {
            throw new InvalidOperationException(
                $"Resolved engine/quality '{result.Engine}|{result.Quality}' is not present in the persisted fallback plan.");
        }

        if (result.AutoIndex.HasValue && matchingIndexes.Contains(result.AutoIndex.Value))
        {
            return result.AutoIndex.Value;
        }

        return matchingIndexes[0];
    }

    private static void ApplyResolvedMetadata(JsonObject payload, ResolvedMetadata? metadata)
    {
        if (metadata == null)
        {
            return;
        }

        SetResolutionPairIfPresent(payload, "Title", "title", metadata.Title);
        SetResolutionPairIfPresent(payload, "Artist", "artist", metadata.Artist);
        SetResolutionPairIfPresent(payload, "Album", "album", metadata.Album);
        SetResolutionPairIfPresent(payload, "AlbumArtist", "albumArtist", metadata.AlbumArtist);
        SetResolutionPairIfPresent(payload, "Cover", "cover", metadata.Cover);
        SetResolutionPairIfPresent(payload, "Label", "label", metadata.Label);
        SetResolutionPairIfPresent(payload, "Copyright", "copyright", metadata.Copyright);
        SetResolutionPairIfPresent(payload, "Composer", "composer", metadata.Composer);
        SetResolutionPairIfPresent(payload, "ReleaseDate", "releaseDate", metadata.ReleaseDate);
        SetResolutionPairIfPresent(payload, "Url", "url", metadata.Url);
        SetResolutionPairIfPresent(payload, "Barcode", "barcode", metadata.Barcode);
        SetResolutionPairIfPresent(payload, "MusicKey", "musicKey", metadata.MusicKey);

        if (metadata.Genres is { Count: > 0 })
        {
            var genres = JsonSerializer.SerializeToNode(metadata.Genres) ?? new JsonArray();
            payload["Genres"] = genres.DeepClone();
            payload["genres"] = genres.DeepClone();
        }

        SetResolutionPairIfPresent(payload, "Explicit", "explicit", metadata.Explicit);
        SetResolutionPairIfPositive(payload, "TrackNumber", "trackNumber", metadata.TrackNumber);
        SetResolutionPairIfPositive(payload, "DiscNumber", "discNumber", metadata.DiscNumber);
        SetResolutionPairIfPositive(payload, "TrackTotal", "trackTotal", metadata.TrackTotal);
        SetResolutionPairIfPositive(payload, "DiscTotal", "discTotal", metadata.DiscTotal);
        SetResolutionPairIfPresent(payload, "Danceability", "danceability", metadata.Danceability);
        SetResolutionPairIfPresent(payload, "Energy", "energy", metadata.Energy);
        SetResolutionPairIfPresent(payload, "Valence", "valence", metadata.Valence);
        SetResolutionPairIfPresent(payload, "Acousticness", "acousticness", metadata.Acousticness);
        SetResolutionPairIfPresent(payload, "Instrumentalness", "instrumentalness", metadata.Instrumentalness);
        SetResolutionPairIfPresent(payload, "Speechiness", "speechiness", metadata.Speechiness);
        SetResolutionPairIfPresent(payload, "Loudness", "loudness", metadata.Loudness);
        SetResolutionPairIfPresent(payload, "Tempo", "tempo", metadata.Tempo);
        SetResolutionPairIfPresent(payload, "TimeSignature", "timeSignature", metadata.TimeSignature);
        SetResolutionPairIfPresent(payload, "Liveness", "liveness", metadata.Liveness);
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

    private static void SetResolutionPairIfPresent(JsonObject payload, string pascalKey, string camelKey, bool? value)
    {
        if (value.HasValue)
        {
            payload[pascalKey] = value.Value;
            payload[camelKey] = value.Value;
        }
    }

    private static void SetResolutionPairIfPresent(JsonObject payload, string pascalKey, string camelKey, double? value)
    {
        if (value.HasValue)
        {
            payload[pascalKey] = value.Value;
            payload[camelKey] = value.Value;
        }
    }

    private static void SetResolutionPairIfPresent(JsonObject payload, string pascalKey, string camelKey, int? value)
    {
        if (value.HasValue)
        {
            payload[pascalKey] = value.Value;
            payload[camelKey] = value.Value;
        }
    }

    private static void SetResolutionPairIfPositive(JsonObject payload, string pascalKey, string camelKey, int? value)
    {
        if (value is > 0)
        {
            payload[pascalKey] = value.Value;
            payload[camelKey] = value.Value;
        }
    }
}
