using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class ManualUnavailableRetryService : BackgroundService
{
    private const int BatchSize = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ManualUnavailableRetryService> _logger;

    public ManualUnavailableRetryService(
        IServiceScopeFactory scopeFactory,
        ILogger<ManualUnavailableRetryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueRetriesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Manual unavailable retry sweep failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ProcessDueRetriesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return;
        }

        var dueTracks = await repository.GetDueManualUnavailableTracksAsync(
            DateTimeOffset.UtcNow,
            BatchSize,
            cancellationToken);
        if (dueTracks.Count == 0)
        {
            return;
        }

        var intentService = scope.ServiceProvider.GetRequiredService<DownloadIntentService>();
        foreach (var track in dueTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RetryTrackAsync(repository, intentService, track, cancellationToken);
        }
    }

    private async Task RetryTrackAsync(
        LibraryRepository repository,
        DownloadIntentService intentService,
        ManualUnavailableTrackDto track,
        CancellationToken cancellationToken)
    {
        var intent = BuildIntent(track);
        try
        {
            var result = await intentService.EnqueueManualAsync(intent, cancellationToken);
            if (result.Queued.Count > 0)
            {
                await repository.DeleteManualUnavailableTrackAsync(track.Id, cancellationToken);
                _logger.LogInformation(
                    "Manual unavailable retry queued {Title} by {Artist}; record removed.",
                    LogSanitizer.OneLine(track.Title),
                    LogSanitizer.OneLine(track.Artist));
                return;
            }

            var reason = FirstNonEmpty(result.Message, result.SkipReasons.FirstOrDefault(), "Track still unavailable from enabled sources.");
            await repository.ScheduleManualUnavailableTrackRetryAsync(
                track.Id,
                DateTimeOffset.UtcNow.Add(RetryDelay),
                reason,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Manual unavailable retry failed for {Title} by {Artist}.",
                LogSanitizer.OneLine(track.Title),
                LogSanitizer.OneLine(track.Artist));
            await repository.ScheduleManualUnavailableTrackRetryAsync(
                track.Id,
                DateTimeOffset.UtcNow.Add(RetryDelay),
                ex.Message,
                CancellationToken.None);
        }
    }

    private static DownloadIntent BuildIntent(ManualUnavailableTrackDto track)
    {
        var payload = ParsePayload(track.PayloadJson);
        return new DownloadIntent
        {
            SourceService = FirstNonEmpty(track.SourceService, ReadString(payload, "SourceService", "sourceService"), track.Engine) ?? string.Empty,
            SourceUrl = FirstNonEmpty(track.SourceUrl, ReadString(payload, "SourceUrl", "sourceUrl", "Url", "url")) ?? string.Empty,
            PreferredEngine = FirstNonEmpty(track.Engine, ReadString(payload, "PreferredEngine", "preferredEngine", "Engine", "engine")) ?? string.Empty,
            SpotifyId = FirstNonEmpty(track.SpotifyId, ReadString(payload, "SpotifyId", "spotifyId", "spotifyTrackId")) ?? string.Empty,
            DeezerId = FirstNonEmpty(track.DeezerId, ReadString(payload, "DeezerId", "deezerId", "deezerTrackId")) ?? string.Empty,
            AppleId = FirstNonEmpty(track.AppleId, ReadString(payload, "AppleId", "appleId", "appleTrackId")) ?? string.Empty,
            QobuzId = FirstNonEmpty(track.QobuzId, ReadString(payload, "QobuzId", "qobuzId", "qobuzTrackId")) ?? string.Empty,
            TidalId = FirstNonEmpty(track.TidalId, ReadString(payload, "TidalId", "tidalId", "tidalTrackId")) ?? string.Empty,
            AmazonId = FirstNonEmpty(track.AmazonId, ReadString(payload, "AmazonId", "amazonId", "amazonTrackId")) ?? string.Empty,
            Isrc = FirstNonEmpty(track.Isrc, ReadString(payload, "Isrc", "isrc")) ?? string.Empty,
            Title = FirstNonEmpty(track.Title, ReadString(payload, "Title", "title")) ?? string.Empty,
            Artist = FirstNonEmpty(track.Artist, ReadString(payload, "Artist", "artist")) ?? string.Empty,
            Album = FirstNonEmpty(track.Album, ReadString(payload, "Album", "album", "CollectionName", "collectionName")) ?? string.Empty,
            AlbumArtist = FirstNonEmpty(track.AlbumArtist, ReadString(payload, "AlbumArtist", "albumArtist")) ?? string.Empty,
            Cover = FirstNonEmpty(ReadString(payload, "Cover", "cover", "CoverUrl", "coverUrl"), string.Empty) ?? string.Empty,
            Quality = FirstNonEmpty(track.Quality, ReadString(payload, "Quality", "quality")) ?? string.Empty,
            ContentType = FirstNonEmpty(track.ContentType, ReadString(payload, "ContentType", "contentType"), "music") ?? "music",
            DestinationFolderId = track.DestinationFolderId ?? ReadInt64(payload, "DestinationFolderId", "destinationFolderId"),
            DurationMs = ReadInt32(payload, "DurationMs", "durationMs") ?? 0,
            TrackNumber = ReadInt32(payload, "TrackNumber", "trackNumber", "SpotifyTrackNumber", "spotifyTrackNumber") ?? 0,
            DiscNumber = ReadInt32(payload, "DiscNumber", "discNumber", "SpotifyDiscNumber", "spotifyDiscNumber") ?? 0,
            TrackTotal = ReadInt32(payload, "TrackTotal", "trackTotal", "SpotifyTotalTracks", "spotifyTotalTracks") ?? 0,
            ReleaseDate = FirstNonEmpty(ReadString(payload, "ReleaseDate", "releaseDate"), string.Empty) ?? string.Empty
        };
    }

    private static JsonObject ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(payloadJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string? ReadString(JsonObject payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (payload[key] is not JsonNode node)
            {
                continue;
            }

            var value = node.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static int? ReadInt32(JsonObject payload, params string[] keys)
        => int.TryParse(ReadString(payload, keys), out var value) ? value : null;

    private static long? ReadInt64(JsonObject payload, params string[] keys)
        => long.TryParse(ReadString(payload, keys), out var value) ? value : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .FirstOrDefault();
}
