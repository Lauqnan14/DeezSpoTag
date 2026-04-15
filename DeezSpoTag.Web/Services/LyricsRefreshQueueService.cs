using System.Text.Json;
using System.Threading.Channels;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class LyricsRefreshQueueService : BackgroundService
{
    public const string JobTypeLyricsRefresh = "lyrics_refresh";

    private readonly LibraryRepository _repository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly LyricsService _lyricsService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<LyricsRefreshQueueService> _logger;
    private readonly Channel<QueueItem> _channel = Channel.CreateUnbounded<QueueItem>();
    private readonly Dictionary<long, QueueItem> _queueItems = new();
    private readonly object _queueLock = new();
    private long? _processingTrackId;
    private DateTimeOffset? _lastProcessedUtc;
    private int _processedCount;
    private int _failedCount;

    private string QueuePath => Path.Join(AppDataPaths.GetDataRoot(_environment), "lyrics-refresh-queue.json");

    public LyricsRefreshQueueService(
        LibraryRepository repository,
        DeezSpoTagSettingsService settingsService,
        LyricsService lyricsService,
        IWebHostEnvironment environment,
        ILogger<LyricsRefreshQueueService> logger)
    {
        _repository = repository;
        _settingsService = settingsService;
        _lyricsService = lyricsService;
        _environment = environment;
        _logger = logger;
    }

    public LyricsRefreshQueueStatus GetStatus()
    {
        lock (_queueLock)
        {
            return new LyricsRefreshQueueStatus(
                JobTypeLyricsRefresh,
                _queueItems.Count,
                _processingTrackId,
                _lastProcessedUtc,
                _processedCount,
                _failedCount);
        }
    }

    public LyricsRefreshEnqueueResult Enqueue(IReadOnlyCollection<long> trackIds)
    {
        var requested = (trackIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (requested.Count == 0)
        {
            return new LyricsRefreshEnqueueResult(JobTypeLyricsRefresh, 0, 0, 0);
        }

        var enqueued = 0;
        var skipped = 0;
        foreach (var trackId in requested)
        {
            if (TryEnqueue(new QueueItem(JobTypeLyricsRefresh, trackId)))
            {
                enqueued++;
            }
            else
            {
                skipped++;
            }
        }

        return new LyricsRefreshEnqueueResult(JobTypeLyricsRefresh, requested.Count, enqueued, skipped);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        LoadQueueSnapshot();
        foreach (var item in SnapshotQueueItems())
        {
            _channel.Writer.TryWrite(item);
        }
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            lock (_queueLock)
            {
                _processingTrackId = item.TrackId;
            }

            try
            {
                await ProcessTrackLyricsRefreshAsync(item.TrackId, stoppingToken);
                lock (_queueLock)
                {
                    _processedCount++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Lyrics refresh failed for track {TrackId}", item.TrackId);
                lock (_queueLock)
                {
                    _failedCount++;
                }
            }
            finally
            {
                lock (_queueLock)
                {
                    _lastProcessedUtc = DateTimeOffset.UtcNow;
                    _processingTrackId = null;
                }
                CompleteItem(item);
            }
        }
    }

    private async Task ProcessTrackLyricsRefreshAsync(long trackId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        var info = await _repository.GetTrackAudioInfoAsync(trackId, cancellationToken);
        if (info is null || string.IsNullOrWhiteSpace(info.FilePath) || !File.Exists(info.FilePath))
        {
            return;
        }

        var sourceLinks = await _repository.GetTrackSourceLinksAsync(trackId, cancellationToken);
        var track = BuildTrack(info, sourceLinks);
        var settings = _settingsService.LoadSettings();
        if (!LyricsSettingsPolicy.CanFetchLyrics(settings))
        {
            return;
        }

        var directory = Path.GetDirectoryName(info.FilePath);
        var filename = Path.GetFileNameWithoutExtension(info.FilePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(filename))
        {
            return;
        }

        var paths = (
            FilePath: directory,
            Filename: filename,
            ExtrasPath: directory,
            CoverPath: string.Empty,
            ArtistPath: string.Empty);

        await _lyricsService.SaveLyricsAsync(track, paths, settings, cancellationToken);
    }

    private static Track BuildTrack(TrackAudioInfoDto info, TrackSourceLinksDto? links)
    {
        var source = ResolveSource(links);
        var sourceId = ResolveSourceId(links, source);
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AddUrl(urls, "deezer_track_id", links?.DeezerTrackId);
        AddUrl(urls, "spotify_track_id", links?.SpotifyTrackId);
        AddUrl(urls, "apple_track_id", links?.AppleTrackId);
        AddUrl(urls, "deezer", links?.DeezerUrl);
        AddUrl(urls, "spotify", links?.SpotifyUrl);
        AddUrl(urls, "apple", links?.AppleUrl);
        AddUrl(urls, "source_url", links?.DeezerUrl ?? links?.SpotifyUrl ?? links?.AppleUrl);

        return new Track
        {
            Id = !string.IsNullOrWhiteSpace(links?.DeezerTrackId) ? links!.DeezerTrackId! : info.TrackId.ToString(),
            Title = info.Title ?? string.Empty,
            Duration = Math.Max(0, (info.DurationMs ?? 0) / 1000),
            MainArtist = new Artist(info.ArtistName ?? string.Empty),
            Album = new Album(info.AlbumTitle ?? string.Empty),
            Source = source,
            SourceId = sourceId,
            Urls = urls,
            DownloadURL = links?.DeezerUrl ?? links?.SpotifyUrl ?? links?.AppleUrl ?? string.Empty
        };
    }

    private static void AddUrl(Dictionary<string, string> urls, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            urls[key] = value.Trim();
        }
    }

    private static string? ResolveSource(TrackSourceLinksDto? links)
    {
        if (!string.IsNullOrWhiteSpace(links?.DeezerTrackId))
        {
            return "deezer";
        }
        if (!string.IsNullOrWhiteSpace(links?.SpotifyTrackId))
        {
            return "spotify";
        }
        if (!string.IsNullOrWhiteSpace(links?.AppleTrackId))
        {
            return "apple";
        }
        return null;
    }

    private static string? ResolveSourceId(TrackSourceLinksDto? links, string? source)
    {
        return source switch
        {
            "deezer" => links?.DeezerTrackId,
            "spotify" => links?.SpotifyTrackId,
            "apple" => links?.AppleTrackId,
            _ => null
        };
    }

    private bool TryEnqueue(QueueItem item)
    {
        lock (_queueLock)
        {
            if (_queueItems.ContainsKey(item.TrackId))
            {
                return false;
            }

            _queueItems[item.TrackId] = item;
            PersistQueueSnapshot();
        }

        return _channel.Writer.TryWrite(item);
    }

    private void CompleteItem(QueueItem item)
    {
        lock (_queueLock)
        {
            _queueItems.Remove(item.TrackId);
            PersistQueueSnapshot();
        }
    }

    private List<QueueItem> SnapshotQueueItems()
    {
        lock (_queueLock)
        {
            return _queueItems.Values.ToList();
        }
    }

    private void LoadQueueSnapshot()
    {
        lock (_queueLock)
        {
            if (!File.Exists(QueuePath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(QueuePath);
                var items = JsonSerializer.Deserialize<List<QueueItem>>(json) ?? new List<QueueItem>();
                _queueItems.Clear();
                foreach (var item in items.Where(item => item.TrackId > 0))
                {
                    _queueItems[item.TrackId] = item;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to load lyrics refresh queue snapshot.");
            }
        }
    }

    private void PersistQueueSnapshot()
    {
        var items = _queueItems.Values.ToList();
        var json = JsonSerializer.Serialize(items);
        Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
        File.WriteAllText(QueuePath, json);
    }

    private sealed record QueueItem(string JobType, long TrackId);
}

public sealed record LyricsRefreshEnqueueResult(string JobType, int Requested, int Enqueued, int Skipped);

public sealed record LyricsRefreshQueueStatus(
    string JobType,
    int Pending,
    long? ProcessingTrackId,
    DateTimeOffset? LastProcessedUtc,
    int Processed,
    int Failed);
