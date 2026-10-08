using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
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

    /// <summary>
    ///     The engine ids below are aliased to the one canonical definition.
    /// </summary>
    /// <remarks>
    ///     Each one is both the key a track link is stored under in the job's url map and the value
    ///     the job records as its source. A copy that drifted would resolve the id from one map and
    ///     record it under another, so a queued lyrics refresh would find no track id and silently
    ///     fetch nothing.
    /// </remarks>
    private const string DeezerSource = DownloadTagSourceHelper.DeezerSource;

    private const string SpotifySource = DownloadTagSourceHelper.SpotifySource;

    private const string AppleSource = DownloadTagSourceHelper.AppleSource;

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
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask>? onWritePhaseStarted = null,
        Func<LyricsResolutionProgress, CancellationToken, ValueTask>? onLookupProgress = null)
    {
        return await ProcessTrackLyricsRefreshAsync(
            trackId, options ?? LyricsRefreshOptions.Default, cancellationToken, onWritePhaseStarted, onLookupProgress);
    }

    public async Task<LyricsRefreshPlan> PlanTrackRefreshAsync(
        long trackId,
        LyricsRefreshOptions options,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return LyricsRefreshPlan.Skip(trackId, null, "Library repository is not configured.");
        }

        var info = await _repository.GetTrackAudioInfoAsync(trackId, cancellationToken);
        if (info is null || string.IsNullOrWhiteSpace(info.FilePath) || !File.Exists(info.FilePath))
        {
            return LyricsRefreshPlan.Skip(trackId, info?.FilePath, "Audio file is unavailable.");
        }

        if (info.DestinationFolderId <= 0)
        {
            return LyricsRefreshPlan.Skip(trackId, info.FilePath, "Library folder profile could not be resolved.");
        }

        var profile = await _profileSettingsResolver.ResolveProfileAsync(info.DestinationFolderId, cancellationToken);
        if (profile?.Technical == null)
        {
            return LyricsRefreshPlan.Skip(trackId, info.FilePath, "Library folder profile could not be resolved.");
        }

        var settings = _settingsService.LoadSettings();
        TechnicalLyricsSettingsApplier.Apply(settings, profile.Technical);
        return PlanExistingLyrics(trackId, info.FilePath, settings, options ?? LyricsRefreshOptions.Default);
    }

    public static LyricsRefreshPlan PlanExistingLyrics(
        long trackId,
        string audioPath,
        DeezSpoTagSettings settings,
        LyricsRefreshOptions options)
    {
        var badges = LyricsSidecarTimingBadges.FromAudioPath(audioPath);
        var ttmlPath = Path.ChangeExtension(audioPath, ".ttml");
        var nonWordTtml = TtmlSidecarCleanup.IsNonWordTimed(ttmlPath);

        if (!options.RefreshLyrics)
        {
            if (options.RewriteLineSyncedTtml
                && nonWordTtml
                && LyricsSettingsPolicy.WantsTtmlOutput(settings))
            {
                return new LyricsRefreshPlan(
                    trackId,
                    audioPath,
                    LyricsSidecarWorkKind.RewriteTtmlToWord,
                    badges,
                    null);
            }

            if (options.RemoveLineSyncedTtml && nonWordTtml)
            {
                return new LyricsRefreshPlan(
                    trackId,
                    audioPath,
                    LyricsSidecarWorkKind.RemoveLineSyncedTtml,
                    badges,
                    null);
            }

            return new LyricsRefreshPlan(
                trackId,
                audioPath,
                LyricsSidecarWorkKind.None,
                badges,
                "Lyrics refresh was not selected for this file.");
        }

        if (!LyricsSettingsPolicy.CanFetchLyrics(settings))
        {
            if (options.RemoveLineSyncedTtml && nonWordTtml)
            {
                return new LyricsRefreshPlan(
                    trackId,
                    audioPath,
                    LyricsSidecarWorkKind.RemoveLineSyncedTtml,
                    badges,
                    null);
            }

            return new LyricsRefreshPlan(
                trackId,
                audioPath,
                LyricsSidecarWorkKind.None,
                badges,
                "Lyrics fetching is disabled by the assigned profile.");
        }

        var work = LyricsSidecarWorkKind.None;
        var lrcPath = Path.ChangeExtension(audioPath, ".lrc");
        var lrcTiming = TryReadFile(lrcPath, out var lrc)
            ? LrcContent.ClassifyTiming(lrc)
            : LrcTimingKind.None;
        var wantsLrc = LyricsSettingsPolicy.WantsLrcOutput(settings);
        if (wantsLrc)
        {
            if (lrcTiming == LrcTimingKind.None)
            {
                work |= LyricsSidecarWorkKind.FetchMissing;
            }
            else if (lrcTiming == LrcTimingKind.Line && LyricsSettingsPolicy.WantsEnhancedLrc(settings))
            {
                work |= LyricsSidecarWorkKind.UpgradeLrcToWord;
            }
        }

        var wantsTtml = LyricsSettingsPolicy.WantsTtmlOutput(settings);
        var hasTtmlFile = File.Exists(ttmlPath);
        var wordTtml = hasTtmlFile
            && TryReadFile(ttmlPath, out var ttml)
            && AppleLyricsService.IsWordSyncedTtml(ttml);
        if (wantsTtml && !wordTtml)
        {
            if (hasTtmlFile)
            {
                work |= LyricsSidecarWorkKind.RewriteTtmlToWord;
            }
            else
            {
                work |= LyricsSidecarWorkKind.FetchMissing;
            }
        }

        if (options.RemoveLineSyncedTtml
            && nonWordTtml
            && !work.HasFlag(LyricsSidecarWorkKind.RewriteTtmlToWord))
        {
            work |= LyricsSidecarWorkKind.RemoveLineSyncedTtml;
        }

        var wantsTxt = LyricsSettingsPolicy.WantsUnsyncedTextOutput(settings);
        var richLyricsPresent = lrcTiming != LrcTimingKind.None || (wantsTtml && wordTtml);
        var txtSatisfied = !wantsTxt
            || richLyricsPresent
            || (TryReadFile(Path.ChangeExtension(audioPath, ".txt"), out var txt) && !string.IsNullOrWhiteSpace(txt));
        if (!txtSatisfied)
        {
            work |= LyricsSidecarWorkKind.FetchUnsyncedTxt;
        }

        return new LyricsRefreshPlan(
            trackId,
            audioPath,
            work,
            badges,
            work == LyricsSidecarWorkKind.None ? "Existing lyrics satisfy the assigned profile." : null);
    }

    private async Task<LyricsRefreshTrackResult> ProcessTrackLyricsRefreshAsync(
        long trackId,
        LyricsRefreshOptions options,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask>? onWritePhaseStarted = null,
        Func<LyricsResolutionProgress, CancellationToken, ValueTask>? onLookupProgress = null)
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

        return await ProcessResolvedFileLyricsAsync(
            trackId,
            info,
            track,
            settings,
            options,
            onWritePhaseStarted,
            onLookupProgress,
            cancellationToken);
    }

    /// <summary>
    ///     Plans the lyrics work for a staged audio file that has no library track.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A file being enriched is not a library track, and manufacturing one in order to look its
    ///         lyrics up by id is the wrong shape of work: the provider ids and final tags the tagging chain
    ///         just wrote are already on the file.
    ///     </para>
    ///     <para>
    ///         The plan is the same computation the library path runs - what is on disk, and what the profile
    ///         asks for - because it is a pure function of the path and the settings.
    ///     </para>
    /// </remarks>
    public LyricsRefreshPlan PlanStagedFileRefresh(
        string filePath,
        DeezSpoTagSettings settings,
        LyricsRefreshOptions options)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return LyricsRefreshPlan.Skip(0, filePath, "Audio file is unavailable.");
        }

        return PlanExistingLyrics(0, filePath, settings, options ?? LyricsRefreshOptions.Default);
    }

    /// <summary>
    ///     Resolves and writes lyrics for a staged audio file, beside it, before it moves.
    /// </summary>
    /// <remarks>
    ///     The write target is the staged file's own directory and stem, so the lyrics it produces share that
    ///     stem and travel with the audio when the move carries the sidecars. <paramref name="trackId" /> is
    ///     carried only for reporting; a staged file has none.
    /// </remarks>
    public async Task<LyricsRefreshTrackResult> RefreshStagedFileNowAsync(
        string filePath,
        DeezSpoTagSettings settings,
        LyricsRefreshOptions options,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask>? onWritePhaseStarted = null,
        Func<LyricsResolutionProgress, CancellationToken, ValueTask>? onLookupProgress = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return LyricsRefreshTrackResult.Skipped(0, filePath, "Audio file is unavailable.");
        }

        var stagedInfo = ReadStagedTrackInfo(filePath);
        var stagedTrack = BuildStagedTrack(stagedInfo);

        return await ProcessResolvedFileLyricsAsync(
            0,
            stagedInfo,
            stagedTrack,
            settings,
            options ?? LyricsRefreshOptions.Default,
            onWritePhaseStarted,
            onLookupProgress,
            cancellationToken);
    }

    /// <summary>
    ///     Reads a staged audio file's own identity: the tags the tagging chain wrote, not a library row.
    /// </summary>
    private static TrackAudioInfoDto ReadStagedTrackInfo(string filePath)
    {
        try
        {
            using var audio = TagLib.File.Create(filePath);
            var title = audio.Tag.Title?.Trim() ?? string.Empty;
            var artist = string.Join(
                ", ",
                audio.Tag.Performers.Where(name => !string.IsNullOrWhiteSpace(name)));
            var album = audio.Tag.Album?.Trim() ?? string.Empty;

            return new TrackAudioInfoDto(
                TrackId: 0,
                Title: title,
                ArtistName: string.IsNullOrWhiteSpace(artist) ? audio.Tag.Performers.FirstOrDefault() ?? string.Empty : artist,
                AlbumTitle: album,
                DurationMs: audio.Tag.Track is > 0 ? (int)audio.Tag.Track : null,
                FilePath: filePath,
                CoverPath: null,
                // No library folder: a staged file belongs to no folder yet. The profile's technical
                // settings arrive through the settings the caller supplies, which is what actually decides
                // lyrics format and naming here.
                DestinationFolderId: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException)
        {
            // Unreadable tags are not a failure: the lookup can still run on whatever identity is available,
            // and a file whose tags cannot be read is reported rather than silently skipped.
            return new TrackAudioInfoDto(0, string.Empty, string.Empty, string.Empty, null, filePath, null, 0);
        }
    }

    /// <summary>
    ///     Builds a lookup track from a staged file's tags, carrying the confirmed provider ids forward.
    /// </summary>
    /// <remarks>
    ///     The provider ids are the point: the tagging chain wrote them onto the file, and they are what let a
    ///     lyrics provider answer with the right words instead of guessing from a title. A file with none
    ///     still gets looked up - by title, artist and album - it simply has nothing to confirm against.
    /// </remarks>
    private static Track BuildStagedTrack(TrackAudioInfoDto info)
    {
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var isrc = string.Empty;

        try
        {
            using var audio = TagLib.File.Create(info.FilePath);
            var extension = Path.GetExtension(info.FilePath);
            isrc = audio.Tag.ISRC?.Trim() ?? string.Empty;

            // The runner's own raw tag reader, rather than a second one here. It already knows the format
            // quirks - MP4 needs the ATL path, Apple dash-box frames are a different box again - and a
            // second reader would quietly disagree with the tags the tagging chain actually wrote.
            AddStagedProviderId(audio, extension, urls, "deezer_track_id", "DEEZER_TRACK_ID", "DEEZERID", "DEEZER_ID");
            AddStagedProviderId(audio, extension, urls, "spotify_track_id", "SPOTIFY_TRACK_ID", "SPOTIFY_ID");
            AddStagedProviderId(audio, extension, urls, "apple_track_id", "ITUNES_TRACK_ID", "APPLE_MUSIC_TRACK_ID");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException)
        {
            // Fall through: an unreadable tag block still leaves the textual identity to look up by.
        }

        return new Track
        {
            Id = urls.TryGetValue("deezer_track_id", out var deezerId) ? deezerId : string.Empty,
            Title = info.Title,
            Duration = Math.Max(0, (info.DurationMs ?? 0) / 1000),
            MainArtist = new Artist(info.ArtistName),
            Album = new Album(info.AlbumTitle),
            ISRC = isrc,
            Source = urls.ContainsKey("deezer_track_id") ? DeezerSource : string.Empty,
            SourceId = urls.TryGetValue("deezer_track_id", out var sourceId) ? sourceId : string.Empty,
            Urls = urls,
            DownloadURL = string.Empty
        };
    }

    private static void AddStagedProviderId(
        TagLib.File audio,
        string extension,
        IDictionary<string, string> urls,
        string urlKey,
        params string[] tagNames)
    {
        foreach (var tagName in tagNames)
        {
            var value = AutoTag.LocalAutoTagRunner.ReadRawTagValues(audio, extension, tagName)
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            if (!string.IsNullOrWhiteSpace(value))
            {
                urls[urlKey] = value.Trim();
                return;
            }
        }
    }

    /// <summary>
    ///     The library-agnostic half of a lyrics refresh: given a resolved identity and a path, plan, fetch
    ///     and write beside the audio.
    /// </summary>
    /// <remarks>
    ///     Extracted so the library path and the staged path cannot drift. Both need the same decision about
    ///     what is on disk, the same provider chain, the same save rules and the same outcome wording; only
    ///     where the identity came from differs.
    /// </remarks>
    private async Task<LyricsRefreshTrackResult> ProcessResolvedFileLyricsAsync(
        long trackId,
        TrackAudioInfoDto info,
        Track track,
        DeezSpoTagSettings settings,
        LyricsRefreshOptions options,
        Func<CancellationToken, ValueTask>? onWritePhaseStarted,
        Func<LyricsResolutionProgress, CancellationToken, ValueTask>? onLookupProgress,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(info.FilePath);
        var filename = Path.GetFileNameWithoutExtension(info.FilePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(filename))
        {
            return LyricsRefreshTrackResult.Skipped(trackId, info.FilePath, "Audio path is invalid.");
        }

        var ttmlPath = Path.Join(directory, $"{filename}.ttml");
        var plan = PlanExistingLyrics(trackId, info.FilePath, settings, options);
        var shouldFetch = plan.NeedsNetwork;

        if (shouldFetch && !LyricsSettingsPolicy.CanFetchLyrics(settings) && !plan.NeedsLocalOnly)
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
        LyricsResolutionResult? resolution = null;
        LyricsResolutionProgress? latestProgress = null;
        if (shouldFetch && LyricsSettingsPolicy.CanFetchLyrics(settings))
        {
            resolution = await _lyricsService.ResolveLyricsWithDetailsAsync(
                track,
                settings,
                providerOptions: null,
                async (progress, progressToken) =>
                {
                    latestProgress = progress;
                    if (onLookupProgress != null)
                    {
                        await onLookupProgress(progress, progressToken);
                    }
                },
                cancellationToken);
            if (resolution.Lyrics?.IsLoaded() == true)
            {
                if (onWritePhaseStarted != null)
                {
                    await onWritePhaseStarted(cancellationToken);
                }
                savedLyrics = await _lyricsService.SaveLyricsAsync(
                    resolution.Lyrics,
                    track,
                    paths,
                    settings,
                    cancellationToken);
            }
            else if (resolution.Incomplete)
            {
                savedLyrics = LyricsSaveResult.Failed;
            }
        }

        var deletedLineTtml = (options.RemoveLineSyncedTtml || plan.NeedsLocalOnly)
            && TtmlSidecarCleanup.TryDeleteNonWordTimed(ttmlPath);
        var formats = savedLyrics.FilesByFormat.Keys
            .OrderBy(format => format, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var embeddedUpdated = File.GetLastWriteTimeUtc(info.FilePath) > audioModifiedBefore;
        var timingBadges = MergeTimingBadges(
            ResolveTimingBadges(savedLyrics.FilesByFormat),
            LyricsSidecarTimingBadges.FromAudioPath(info.FilePath));
        var result = formats.Count > 0 || embeddedUpdated
            ? LyricsRefreshTrackResult.Completed(trackId, info.FilePath, formats, embeddedUpdated)
            : savedLyrics.LookupFailed
                ? LyricsRefreshTrackResult.Skipped(trackId, info.FilePath, "Lyrics could not be verified.") with
                    {
                        LookupFailed = true
                    }
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
                            ? LyricsRefreshTrackResult.ConfirmedAbsent(trackId, info.FilePath, "No lyrics were returned by the enabled providers.")
                            : LyricsRefreshTrackResult.Skipped(trackId, info.FilePath, "No lyrics cleanup was required.");
        var remainingOutputs = latestProgress?.RemainingOutputs ?? Array.Empty<string>();
        var incompleteReason = resolution?.Incomplete == true
            ? resolution.Error
            : remainingOutputs.Count > 0
                ? $"Requested lyrics not found: {string.Join(", ", remainingOutputs)}."
                : null;
        return result with
        {
            Title = info.Title,
            ArtistName = info.ArtistName,
            CoverPath = info.CoverPath,
            TimingBadges = timingBadges,
            ProviderOutcomes = resolution?.ProviderOutcomes ?? Array.Empty<LyricsProviderOutcome>(),
            RemainingOutputs = remainingOutputs,
            IncompleteReason = incompleteReason,
            Message = string.IsNullOrWhiteSpace(incompleteReason)
                ? result.Message
                : $"{result.Message} {incompleteReason}"
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
        TryReadFile(filesByFormat.GetValueOrDefault("ttml") ?? string.Empty, out var ttml);
        TryReadFile(filesByFormat.GetValueOrDefault("lrc") ?? string.Empty, out var lrc);
        return LyricsSidecarTimingBadges.FromSidecars(
            ttml,
            lrc,
            filesByFormat.ContainsKey("txt"));
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
        AddUrl(urls, DeezerSource, links?.DeezerUrl);
        AddUrl(urls, SpotifySource, links?.SpotifyUrl);
        AddUrl(urls, AppleSource, links?.AppleUrl);
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
            return DeezerSource;
        }
        if (!string.IsNullOrWhiteSpace(links?.SpotifyTrackId))
        {
            return SpotifySource;
        }
        if (!string.IsNullOrWhiteSpace(links?.AppleTrackId))
        {
            return AppleSource;
        }
        return null;
    }

    private static string? ResolveSourceId(TrackSourceLinksDto? links, string? source)
    {
        return source switch
        {
            DeezerSource => links?.DeezerTrackId,
            SpotifySource => links?.SpotifyTrackId,
            AppleSource => links?.AppleTrackId,
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

public sealed record LyricsRefreshPlan(
    long TrackId,
    string? FilePath,
    LyricsSidecarWorkKind Work,
    IReadOnlyList<string> CurrentBadges,
    string? SkipReason)
{
    public bool ShouldFetchLyrics => NeedsNetwork;

    public bool NeedsNetwork => Work.HasFlag(LyricsSidecarWorkKind.FetchMissing)
        || Work.HasFlag(LyricsSidecarWorkKind.UpgradeLrcToWord)
        || Work.HasFlag(LyricsSidecarWorkKind.RewriteTtmlToWord)
        || Work.HasFlag(LyricsSidecarWorkKind.FetchUnsyncedTxt);

    public bool NeedsLocalOnly => Work.HasFlag(LyricsSidecarWorkKind.RemoveLineSyncedTtml) && !NeedsNetwork;

    public bool HasAnyWork => Work != LyricsSidecarWorkKind.None;

    public static LyricsRefreshPlan Skip(long trackId, string? filePath, string reason)
        => new(trackId, filePath, LyricsSidecarWorkKind.None, Array.Empty<string>(), reason);
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

    /// <summary>
    /// True only when the configured lyrics sources were actually consulted and none of
    /// them returned lyrics. A timeout or an error must never set this: elapsed time is
    /// "could not verify", not "no lyrics".
    /// </summary>
    public bool LyricsConfirmedAbsent { get; init; }

    public bool LookupFailed { get; init; }
    public IReadOnlyList<LyricsProviderOutcome> ProviderOutcomes { get; init; } = Array.Empty<LyricsProviderOutcome>();
    public IReadOnlyList<string> RemainingOutputs { get; init; } = Array.Empty<string>();
    public string? IncompleteReason { get; init; }

    public static LyricsRefreshTrackResult ConfirmedAbsent(long trackId, string? filePath, string message)
        => new(trackId, filePath, false, false, Array.Empty<string>(), message) { LyricsConfirmedAbsent = true };

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
