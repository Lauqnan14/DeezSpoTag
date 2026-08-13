using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Runtime;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class LyricsRefreshQueueService : BackgroundService
{
    public const string JobTypeLyricsRefresh = "lyrics_refresh";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex LeadingTrackNumberRegex = new(
        @"^\s*(?:\d+\s*[-._)\]]\s*)+",
        RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex ArtistTitleFilenameRegex = new(
        @"^\s*(?<artist>.+?)\s+-\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled,
        RegexTimeout);
    private static readonly HashSet<string> WeakIdentityValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown",
        "unknown artist",
        "unknown album artist",
        "unknown album",
        "untitled",
        "track",
        "audio"
    };

    private readonly LibraryRepository _repository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IDownloadTagSettingsResolver _profileSettingsResolver;
    private readonly LyricsService _lyricsService;
    private readonly IWebHostEnvironment _environment;
    private readonly BackgroundWorkCoordinator _workCoordinator;
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
        IDownloadTagSettingsResolver profileSettingsResolver,
        LyricsService lyricsService,
        IWebHostEnvironment environment,
        BackgroundWorkCoordinator workCoordinator,
        ILogger<LyricsRefreshQueueService> logger)
    {
        _repository = repository;
        _settingsService = settingsService;
        _profileSettingsResolver = profileSettingsResolver;
        _lyricsService = lyricsService;
        _environment = environment;
        _workCoordinator = workCoordinator;
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
        try
        {
            await _workCoordinator.WaitForStartupGraceAsync(stoppingToken);

            await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                lock (_queueLock)
                {
                    _processingTrackId = item.TrackId;
                }

                try
                {
                    _ = await ProcessTrackLyricsRefreshAsync(item.TrackId, LyricsRefreshOptions.Default, stoppingToken);
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
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Lyrics refresh queue stopped because cancellation was requested.");
        }
    }

    public async Task<LyricsRefreshTrackResult> RefreshTrackNowAsync(
        long trackId,
        CancellationToken cancellationToken)
    {
        return await RefreshTrackNowAsync(trackId, LyricsRefreshOptions.Default, cancellationToken);
    }

    public async Task<LyricsRefreshTrackResult> RefreshTrackNowAsync(
        long trackId,
        LyricsRefreshOptions options,
        CancellationToken cancellationToken)
    {
        return await ProcessTrackLyricsRefreshAsync(trackId, options ?? LyricsRefreshOptions.Default, cancellationToken);
    }

    private async Task<LyricsRefreshTrackResult> ProcessTrackLyricsRefreshAsync(
        long trackId,
        LyricsRefreshOptions options,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return LyricsRefreshTrackResult.Skipped(trackId, null, "Library repository is not configured.");
        }

        var info = await _repository.GetTrackAudioInfoAsync(trackId, cancellationToken);
        if (info is null || string.IsNullOrWhiteSpace(info.FilePath) || !File.Exists(info.FilePath))
        {
            return LyricsRefreshTrackResult.Skipped(trackId, info?.FilePath, "Audio file is unavailable.");
        }

        var sourceLinks = await _repository.GetTrackSourceLinksAsync(trackId, cancellationToken);
        var track = BuildTrack(info, sourceLinks);
        if (info.DestinationFolderId <= 0)
        {
            return LyricsRefreshTrackResult.Skipped(trackId, info.FilePath, "Library folder profile could not be resolved.");
        }

        var profile = await _profileSettingsResolver.ResolveProfileAsync(info.DestinationFolderId, cancellationToken);
        if (profile?.Technical == null)
        {
            return LyricsRefreshTrackResult.Skipped(trackId, info.FilePath, "Library folder profile could not be resolved.");
        }

        var settings = _settingsService.LoadSettings();
        TechnicalLyricsSettingsApplier.Apply(settings, profile.Technical);

        var directory = Path.GetDirectoryName(info.FilePath);
        var filename = Path.GetFileNameWithoutExtension(info.FilePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(filename))
        {
            return LyricsRefreshTrackResult.Skipped(trackId, info.FilePath, "Audio path is invalid.");
        }

        var ttmlPath = Path.Join(directory, $"{filename}.ttml");
        var hadNonWordTtml = TtmlSidecarCleanup.IsNonWordTimed(ttmlPath);
        var shouldFetch = options.RefreshLyrics
            || (options.RewriteLineSyncedTtml
                && hadNonWordTtml
                && LyricsSettingsPolicy.WantsTtmlOutput(settings));

        if (shouldFetch && !LyricsSettingsPolicy.CanFetchLyrics(settings) && !options.RemoveLineSyncedTtml)
        {
            return BuildExistingLyricsResult(
                trackId,
                info,
                "Existing lyrics kept; lyrics refresh was not selected for this file.",
                "Lyrics fetching is disabled by the assigned profile.");
        }

        var paths = (
            FilePath: directory,
            Filename: filename,
            ExtrasPath: directory,
            CoverPath: string.Empty,
            ArtistPath: string.Empty);

        var audioModifiedBefore = File.GetLastWriteTimeUtc(info.FilePath);
        var savedLyrics = LyricsSaveResult.Empty;
        if (shouldFetch && LyricsSettingsPolicy.CanFetchLyrics(settings))
        {
            savedLyrics = await _lyricsService.SaveLyricsAsync(track, paths, settings, cancellationToken);
        }

        var deletedLineTtml = options.RemoveLineSyncedTtml && TtmlSidecarCleanup.TryDeleteNonWordTimed(ttmlPath);
        var formats = savedLyrics.FilesByFormat.Keys
            .OrderBy(format => format, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var embeddedUpdated = File.GetLastWriteTimeUtc(info.FilePath) > audioModifiedBefore;
        var timingBadges = MergeTimingBadges(
            ResolveTimingBadges(savedLyrics.FilesByFormat),
            LyricsSidecarTimingBadges.FromAudioPath(info.FilePath));
        var result = formats.Count > 0 || embeddedUpdated
            ? LyricsRefreshTrackResult.Completed(trackId, info.FilePath, formats, embeddedUpdated)
            : deletedLineTtml
                ? LyricsRefreshTrackResult.Skipped(
                    trackId,
                    info.FilePath,
                    "Line-synced TTML removed.")
                : timingBadges.Count > 0
                    ? LyricsRefreshTrackResult.Skipped(
                        trackId,
                        info.FilePath,
                        "Existing lyrics kept; overwrite was not selected.")
                    : shouldFetch
                        ? LyricsRefreshTrackResult.Skipped(trackId, info.FilePath, "No lyrics were returned by the enabled providers.")
                        : LyricsRefreshTrackResult.Skipped(trackId, info.FilePath, "No lyrics cleanup was required.");
        return result with
        {
            Title = info.Title,
            ArtistName = info.ArtistName,
            CoverPath = info.CoverPath,
            TimingBadges = timingBadges
        };
    }

    private static LyricsRefreshTrackResult BuildExistingLyricsResult(
        long trackId,
        TrackAudioInfoDto info,
        string keptMessage,
        string missingMessage)
    {
        var existingBadges = LyricsSidecarTimingBadges.FromAudioPath(info.FilePath);
        return LyricsRefreshTrackResult.Skipped(
                trackId,
                info.FilePath,
                existingBadges.Count > 0 ? keptMessage : missingMessage) with
        {
            Title = info.Title,
            ArtistName = info.ArtistName,
            CoverPath = info.CoverPath,
            TimingBadges = existingBadges
        };
    }

    private static IReadOnlyList<string> MergeTimingBadges(
        IReadOnlyList<string> written,
        IReadOnlyList<string> existing)
    {
        return written.Concat(existing)
            .Where(badge => !string.IsNullOrWhiteSpace(badge))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ResolveTimingBadges(IReadOnlyDictionary<string, string> filesByFormat)
    {
        var badges = new List<string>();
        if (filesByFormat.TryGetValue("ttml", out var ttmlPath) && TryReadFile(ttmlPath, out var ttml)
            && AppleLyricsService.IsWordSyncedTtml(ttml))
        {
            badges.Add("time-synced");
        }

        if (filesByFormat.TryGetValue("lrc", out var lrcPath) && TryReadFile(lrcPath, out var lrc))
        {
            badges.Add(LrcContent.IsWordSynchronized(lrc) ? "enhanced-synchronized" : "synced");
        }

        if (badges.Count == 0 && filesByFormat.ContainsKey("txt"))
        {
            badges.Add("unsynced");
        }

        return badges;
    }

    private static bool TryReadFile(string path, out string content)
    {
        content = string.Empty;
        try
        {
            var resolved = DownloadPathResolver.ResolveIoPath(path);
            if (!File.Exists(resolved))
            {
                return false;
            }
            content = File.ReadAllText(resolved);
            return true;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static Track BuildTrack(TrackAudioInfoDto info, TrackSourceLinksDto? links)
    {
        var identity = ResolveLookupIdentity(info);
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
            Title = identity.Title,
            Duration = Math.Max(0, (info.DurationMs ?? 0) / 1000),
            MainArtist = new Artist(identity.Artist),
            Album = new Album(identity.Album),
            ISRC = links?.Isrc?.Trim() ?? identity.Isrc ?? string.Empty,
            Source = source,
            SourceId = sourceId,
            Urls = urls,
            DownloadURL = links?.DeezerUrl ?? links?.SpotifyUrl ?? links?.AppleUrl ?? string.Empty
        };
    }

    private static LyricsLookupIdentity ResolveLookupIdentity(TrackAudioInfoDto info)
    {
        var title = NormalizeIdentityValue(info.Title);
        var artist = NormalizeIdentityValue(info.ArtistName);
        var album = NormalizeIdentityValue(info.AlbumTitle);
        string? isrc = null;

        try
        {
            using var audio = TagLib.File.Create(info.FilePath);
            if (IsWeakIdentityValue(title))
            {
                title = NormalizeIdentityValue(audio.Tag.Title);
            }
            if (IsWeakIdentityValue(artist))
            {
                artist = audio.Tag.Performers?
                    .Select(NormalizeIdentityValue)
                    .FirstOrDefault(value => !IsWeakIdentityValue(value))
                    ?? NormalizeIdentityValue(audio.Tag.FirstPerformer);
            }
            if (IsWeakIdentityValue(album))
            {
                album = NormalizeIdentityValue(audio.Tag.Album);
            }
            isrc = NormalizeIdentityValue(audio.Tag.ISRC);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The indexed file still provides a safe filename/folder identity below.
        }

        var fileStem = NormalizeIdentityValue(Path.GetFileNameWithoutExtension(info.FilePath));
        var cleanedStem = LeadingTrackNumberRegex.Replace(fileStem, string.Empty).Trim();
        var filenameMatch = ArtistTitleFilenameRegex.Match(cleanedStem);
        if (filenameMatch.Success)
        {
            if (IsWeakIdentityValue(artist))
            {
                artist = NormalizeIdentityValue(filenameMatch.Groups["artist"].Value);
            }
            if (IsWeakIdentityValue(title))
            {
                title = NormalizeIdentityValue(filenameMatch.Groups["title"].Value);
            }
        }
        else if (IsWeakIdentityValue(title))
        {
            title = cleanedStem;
        }

        var albumDirectory = Path.GetDirectoryName(info.FilePath);
        if (IsWeakIdentityValue(album) && !string.IsNullOrWhiteSpace(albumDirectory))
        {
            album = NormalizeIdentityValue(Path.GetFileName(albumDirectory));
        }

        if (IsWeakIdentityValue(artist) && !string.IsNullOrWhiteSpace(albumDirectory))
        {
            var artistDirectory = Directory.GetParent(albumDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(artistDirectory))
            {
                artist = NormalizeIdentityValue(Path.GetFileName(artistDirectory));
            }
        }

        return new LyricsLookupIdentity(
            IsWeakIdentityValue(title) ? string.Empty : title,
            IsWeakIdentityValue(artist) ? string.Empty : artist,
            IsWeakIdentityValue(album) ? string.Empty : album,
            isrc);
    }

    private static string NormalizeIdentityValue(string? value)
        => value?.Trim().Trim('[', ']') ?? string.Empty;

    private static bool IsWeakIdentityValue(string? value)
        => string.IsNullOrWhiteSpace(value) || WeakIdentityValues.Contains(NormalizeIdentityValue(value));

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
    private sealed record LyricsLookupIdentity(string Title, string Artist, string Album, string? Isrc);
}

public sealed record LyricsRefreshOptions(
    bool RefreshLyrics = true,
    bool RemoveLineSyncedTtml = false,
    bool RewriteLineSyncedTtml = false)
{
    public static LyricsRefreshOptions Default { get; } = new();
}

public sealed record LyricsRefreshEnqueueResult(string JobType, int Requested, int Enqueued, int Skipped);

public sealed record LyricsRefreshTrackResult(
    long TrackId,
    string? FilePath,
    bool Success,
    bool EmbeddedUpdated,
    IReadOnlyList<string> SidecarFormats,
    string Message)
{
    public string? Title { get; init; }
    public string? ArtistName { get; init; }
    public string? CoverPath { get; init; }
    public IReadOnlyList<string> TimingBadges { get; init; } = Array.Empty<string>();

    public static LyricsRefreshTrackResult Completed(
        long trackId,
        string filePath,
        IReadOnlyList<string> sidecarFormats,
        bool embeddedUpdated)
        => new(
            trackId,
            filePath,
            true,
            embeddedUpdated,
            sidecarFormats,
            $"Lyrics updated ({(sidecarFormats.Count == 0 ? "embedded" : string.Join(", ", sidecarFormats))}).");

    public static LyricsRefreshTrackResult Skipped(long trackId, string? filePath, string message)
        => new(trackId, filePath, false, false, Array.Empty<string>(), message);
}

public sealed record LyricsRefreshQueueStatus(
    string JobType,
    int Pending,
    long? ProcessingTrackId,
    DateTimeOffset? LastProcessedUtc,
    int Processed,
    int Failed);
