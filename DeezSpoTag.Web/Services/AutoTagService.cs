using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Security;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services.CoverPort;
using DeezSpoTag.Web.Services.AutoTag;

namespace DeezSpoTag.Web.Services;

public sealed class AutoTagRunPausedException : Exception
{
    public AutoTagRunPausedException(string message)
        : base(message)
    {
    }
}

internal static class AutoTagLiterals
{
    internal const string QueuedStatus = "queued";
    internal const string RunningStatus = "running";
    internal const string TaggingStatus = "tagging";
    internal const string OkStatus = "ok";
    internal const string TaggedStatus = "tagged";
    internal const string ReviewStatus = "review";
    internal const string SkippedStatus = "skipped";
    internal const string ErrorStatus = "error";
    internal const string ManualTrigger = "manual";
    internal const string AutomationTrigger = "automation";
    internal const string ScheduleTrigger = "schedule";
    internal const string RecoveryTrigger = "recovery";
    internal const string InvalidTrigger = "invalid";
    internal const string RunIntentDefault = "default";
    internal const string RunIntentDownloadEnrichment = "download_enrichment";
    internal const string RunIntentEnhancementOnly = "enhancement_only";
    internal const string RunIntentEnhancementRecentDownloads = "enhancement_recent_downloads";
    internal const string RunIntentManualEnrichment = "manual_enrichment";
    internal const string RunIntentAliasMerge = "artist_alias_merge";
    internal const string CanceledStatus = "canceled";
    internal const string InterruptedStatus = "interrupted";
    internal const string PausedStatus = "paused";
    internal const string FailedStatus = "failed";
    internal const string CompletedStatus = "completed";
    internal const string ResumedStatus = "resumed";
    internal const string BlockedStatus = "blocked";
    internal const string EnrichmentStage = "enrichment";
    internal const string EnhancementStage = "enhancement";
    internal const string MultiPlatformKey = "multiplatform";
    internal const string OverwriteTagsKey = "overwriteTags";
    internal const string DownloadTagSourceKey = "downloadTagSource";
    internal const string FollowDownloadEngineSource = "engine";
    internal const string DeezerSource = "deezer";
    internal const string SpotifySource = "spotify";
    internal const string JsonFileSearchPattern = "*.json";
    internal const string PlatformsKey = "platforms";
    internal const string OverwriteKey = "overwrite";
    internal const string CustomKey = "custom";
    internal const string PlatformKey = "platform";
    internal const string AppleMusicPlatform = "applemusic";
    internal const string ITunesPlatform = "itunes";
    internal const string PlexPlatform = "plex";
    internal const string JellyfinPlatform = "jellyfin";
    internal const string DiscogsPlatform = "discogs";
    internal const string LastFmPlatform = "lastfm";
    internal const string BpmSupremePlatform = "bpmsupreme";
    internal const string MultiArtistSeparatorKey = "multiArtistSeparator";
    internal const string TargetFilesKey = "targetFiles";
    internal const string LibraryWideEnhancementBatchSizeKey = "libraryWideEnhancementBatchSize";
    internal const string IncludeSubfoldersKey = "includeSubfolders";
    internal const string ArtistTag = "artist";
    internal const string ReleaseDateTag = "releaseDate";
    internal const string LanguageTag = "language";
    internal const string DownloadTagsKey = "downloadTags";
    internal const string EnhancementFeatureGapFill = "tag-gap-fill";
    internal const string EnhancementFeatureFolderUniformity = "folder-uniformity";
    internal const string EnhancementFeatureQualityChecks = "quality-checks";
    internal const string EnhancementFeatureSidecars = "sidecars";
    internal const string EnhancementPhaseSidecarsLyrics = "sidecars-lyrics";
    internal const string EnhancementPhaseSidecarsCovers = "sidecars-covers";
    internal const string EnhancementFeatureCoverMaintenance = "cover-maintenance";
    internal const string EnhancementFeatureLyricsRefreshLegacy = "lyrics-refresh";
    internal const string EnhancementFeatureManualEnrichment = "manual-enrichment";
    internal const string ManualReleasePreferenceKey = "manualReleasePreference";
    internal const string ManualDestinationFolderIdKey = "manualDestinationFolderId";
    internal const string ManualForceFingerprintKey = "manualForceFingerprint";
    internal const string EnhancementForceFingerprintKey = "enhancementForceFingerprint";
    internal const string EnhancementUntrustedTargetsKey = "enhancementUntrustedTargets";
    internal const string PriorityTargetFilesKey = "priorityTargetFiles";
    internal const string EditionConflictReviewKey = "editionConflictReview";
}

public abstract class AutoTagRunState
{
    public string Id { get; init; } = "";
    public string Status { get; set; } = AutoTagLiterals.QueuedStatus;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public double Progress { get; set; }
    public int OkCount { get; set; }
    public int ErrorCount { get; set; }
    public int ReviewCount { get; set; }
    public int SkippedCount { get; set; }
    public string? RootPath { get; set; }
    public string Trigger { get; set; } = AutoTagLiterals.ManualTrigger;
    public string RunIntent { get; set; } = AutoTagLiterals.RunIntentDefault;
    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }
    public string? EnhancementFeature { get; set; }
    public string? EnhancementGroupId { get; set; }
    public string? CurrentPhase { get; set; }
    public int CurrentBatch { get; set; }
    public int BatchCount { get; set; }
    public int BatchProcessed { get; set; }
    public int BatchSize { get; set; }
    public int ProcessedItems { get; set; }
    public int TotalItems { get; set; }
    public string? TargetReason { get; set; }
    public int TargetRequested { get; set; }
    public int TargetUsable { get; set; }
    public string? EnhancementManifestPath { get; set; }
    public string? EnhancementDownloadBatchId { get; set; }
    public string? EnhancementDownloadOperation { get; set; }
    public int EnhancementDownloadItemCount { get; set; }
    public AutoTagMoveSummary? AutoMoveSummary { get; set; }
}

public class AutoTagJob : AutoTagRunState
{
    public string? CurrentPlatform { get; set; }
    /// <summary>
    /// Redacted, sanitized config captured when the job last ran, so the explicit resume
    /// endpoint can restart this exact scope even after runtime-config cleanup.
    /// </summary>
    public string? ResumeConfigJson { get; set; }
    public TaggingStatusWrap? LastStatus { get; set; }
    public List<TaggingStatusSnapshot> StatusHistory { get; } = new();
    public List<string> Logs { get; } = new();
    public List<EnhancementWorkflowResult> EnhancementWorkflows { get; } = new();
    public List<string> EnhancedFilePaths { get; } = new();
    public List<string> StartedPlatforms { get; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, AutoTagTagDiff> TagDiffs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public AutoTagResumeCheckpoint? ResumeCheckpoint { get; set; }
    public string? ResumeFromJobId { get; set; }
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AutoTagRunSummary : AutoTagRunState
{
    public int LogCount { get; set; }
    public int StatusEntryCount { get; set; }
    public string? ResumeFromJobId { get; set; }
    public DateTimeOffset? HistoryDate { get; set; }
}

public sealed class AutoTagRunDaySummary
{
    public string Date { get; set; } = string.Empty;
    public int RunCount { get; set; }
    public List<AutoTagRunSummary> Runs { get; set; } = new();
}

public sealed class AutoTagRunArchive
{
    public AutoTagRunSummary Summary { get; set; } = new();
    public List<string> Logs { get; set; } = new();
    public List<TaggingStatusSnapshot> StatusHistory { get; set; } = new();
}

public sealed class AutoTagRunIndexDocument
{
    public int Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AutoTagRunSummary> Runs { get; set; } = new();
}

public class TaggingStatusSnapshot
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public TaggingStatusWrap? Status { get; set; }
}

public class TaggingStatusWrap
{
    public TaggingStatus? Status { get; set; }
    public string Platform { get; set; } = "";
    public double Progress { get; set; }
    public int? PlatformIndex { get; set; }
    public int? PlatformCount { get; set; }
    public int? FileIndex { get; set; }
    public int? FileCount { get; set; }
    public int? NextPlatformIndex { get; set; }
    public int? NextFileIndex { get; set; }

    /// <summary>
    /// Actual batch position reported by the runner when the run uses album-boundary
    /// batches. Null when the run is not batched, in which case the job falls back to
    /// fixed-window display math.
    /// </summary>
    public int? BatchNumber { get; set; }
    public int? BatchCount { get; set; }
    public int? BatchSize { get; set; }
    public int? BatchProcessed { get; set; }
}

public class TaggingStatus
{
    public string Status { get; set; } = "";
    public string? ActivityState { get; set; }
    public string Path { get; set; } = "";
    public string? Message { get; set; }
    public double? Accuracy { get; set; }
    public bool UsedShazam { get; set; }
    public string? Outcome { get; set; }
    public string? RecognitionStrategy { get; set; }
    public List<string> RequestedTags { get; set; } = new();
    public List<string> ReturnedTags { get; set; } = new();
    public List<string> WrittenTags { get; set; } = new();
    public List<string> RetainedTags { get; set; } = new();
    public List<string> MissingTags { get; set; } = new();
    public string? ReviewReason { get; set; }
    public string? ReviewDestinationPath { get; set; }
    public string? ReviewReportPath { get; set; }
    public string? SourceTitle { get; set; }
    public string? SourceArtist { get; set; }
    public long? LyricsTrackId { get; set; }
    public string? LyricsCoverUrl { get; set; }
    public List<string> LyricsBadges { get; set; } = new();
    public List<string> ArtworkBadges { get; set; } = new();
    public string? SourceIsrc { get; set; }
    public double? SourceDurationSeconds { get; set; }
    public string? CandidateTitle { get; set; }
    public string? CandidateArtist { get; set; }
    public string? CandidateIsrc { get; set; }
    public double? CandidateDurationSeconds { get; set; }
}

public sealed class AutoTagResumeCheckpoint
{
    public string StageName { get; set; } = string.Empty;
    public string StageConfigHash { get; set; } = string.Empty;
    public int PlatformIndex { get; set; }
    public int FileIndex { get; set; }
    public int PlatformCount { get; set; }
    public int FileCount { get; set; }
    public string? LastPath { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AutoTagTagSnapshot
{
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public QuickTagDumpMeta Meta { get; set; } = new();
    public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AutoTagTagDiff
{
    public string Path { get; set; } = string.Empty;
    public string? LastPlatform { get; set; }
    public string? BasePlatform { get; set; }
    public string? TargetPlatform { get; set; }
    public bool IsFinalPlatformDiff { get; set; }
    public AutoTagTagSnapshot? Before { get; set; }
    public AutoTagTagSnapshot? After { get; set; }
    public Dictionary<string, string> RetainedSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AutoTagPlatformDiffSnapshot> PlatformDiffs { get; set; } = new();
}

public sealed class AutoTagPlatformDiffSnapshot
{
    public string Platform { get; set; } = string.Empty;
    public string? Status { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public AutoTagTagSnapshot? Before { get; set; }
    public AutoTagTagSnapshot? After { get; set; }
}

public partial class AutoTagService
{    private readonly record struct EnrichmentBuildContext(string RunIntent, string JobId);

    private readonly ConcurrentDictionary<string, AutoTagJob> _jobs = new();
    private readonly ConcurrentDictionary<string, byte> _activeJobIds = new();
    private readonly ConcurrentDictionary<string, string> _activeJobStages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastActivityLines = new();
    private readonly ConcurrentDictionary<string, byte> _stuckRecoveryJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCancellationSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRunIndexUpdateUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastJobFullSaveUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _archivedLogLineCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _archivedStatusEntryCounts = new(StringComparer.OrdinalIgnoreCase);
    private AutoTagJob? _latestTerminalJob;
    private readonly ILogger<AutoTagService> _logger;
    private readonly LibraryConfigStore _activityLog;
    private readonly AutoTagMetadataService _metadataService;
    private readonly DeezSpoTag.Web.Services.AutoTag.IAutoTagRunner _autoTagRunner;
    private readonly AutoTagLibraryOrganizer _libraryOrganizer;
    private readonly AutoTagDownloadMoveService _downloadMoveService;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly QuickTagService _quickTagService;
    private readonly PlatformAuthService _platformAuthService;
    private readonly PlexApiClient _plexApiClient;
    private readonly DeezSpoTag.Services.Settings.DeezSpoTagSettingsService _settingsService;
    private readonly LibraryRepository _libraryRepository;
    private readonly KnownLibraryFileIngestionService _knownFileIngestionService;
    private readonly LibraryScanRunner _libraryScanRunner;
    private readonly QualityScannerService _qualityScannerService;
    private readonly DuplicateCleanerService _duplicateCleanerService;
    private readonly LyricsRefreshQueueService _lyricsRefreshQueueService;
    private readonly LibraryArtistImageQueueService _artistImageQueueService;
    private readonly CoverLibraryMaintenanceService _coverMaintenanceService;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly MediaServerLibraryRefreshService _mediaServerRefreshService;
    private readonly MediaServerRefreshOutboxService _mediaServerRefreshOutboxService;
    private readonly UserPreferencesStore _userPreferencesStore;
    private readonly ActivitiesRealtimeService _activitiesRealtime;
    private readonly IDeezSpoTagListener _downloadEvents;
    private readonly INotificationSink _notifications;
    private readonly string _jobsDir;
    private readonly string _historyDir;
    private readonly string _workersHistoryDir;
    private readonly string _runtimeConfigDir;
    private readonly string _lastConfigPath;
    private readonly string _lastJobPath;
    private readonly string _runIndexPath;
    private readonly bool _disableAutoMove;
    private readonly ConcurrentDictionary<string, object> _archiveLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _archivedRunSummariesCacheLock = new();
    private readonly object _archivedRunPruneLock = new();
    private readonly object _runIndexLock = new();
    private IReadOnlyList<AutoTagRunSummary>? _archivedRunSummariesCache;
    private DateTimeOffset _archivedRunSummariesCacheExpiresUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastArchivedRunPruneUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan ArchivedRunSummariesCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ArchivedRunPruneInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RunIndexUpdateInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Minimum interval between full job-JSON rewrites for routine progress updates
    /// (per-file statuses and log lines). Checkpoint updates and terminal transitions
    /// always save immediately, so resume state is never delayed by this throttle.
    /// </summary>
    private static readonly TimeSpan JobSaveThrottleInterval = TimeSpan.FromSeconds(1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly JsonSerializerOptions _jsonCompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
    private static readonly Regex AnsiRegex = new(
        @"\x1B\[[0-9;]*m",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));
    private static readonly HashSet<string> BinaryArtworkTagKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "APIC",
        "COVERART",
        "COVERARTMIME",
        "METADATA_BLOCK_PICTURE",
        "PICTURE",
        "WM/Picture",
        "covr"
    };
    private static readonly HashSet<string> RedactedConfigKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "arl",
        "token",
        "password",
        "clientsecret",
        "client_secret",
        "sp_dc",
        "spdc",
        "access_token",
        "accesstoken",
        "refresh_token",
        "refreshtoken",
        "api_key",
        "apikey",
        "authorization",
        "cookie"
    };
    private static readonly Dictionary<string, string> SupportedTagKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [AutoTagTitleKey] = AutoTagTitleKey,
        [AutoTagLiterals.ArtistTag] = AutoTagLiterals.ArtistTag,
        [AutoTagArtistsKey] = AutoTagLiterals.ArtistTag,
        ["albumArtist"] = "albumArtist",
        ["album"] = "album",
        ["albumArt"] = "albumArt",
        ["cover"] = "albumArt",
        ["version"] = "version",
        ["remixer"] = "remixer",
        ["genre"] = "genre",
        ["style"] = "style",
        ["label"] = "label",
        ["releaseId"] = "releaseId",
        ["trackId"] = "trackId",
        ["recordingId"] = "recordingId",
        ["artistId"] = "artistId",
        ["albumArtistId"] = "albumArtistId",
        ["releaseGroupId"] = "releaseGroupId",
        ["albumId"] = "albumId",
        ["releaseStatus"] = "releaseStatus",
        ["releaseCountry"] = "releaseCountry",
        ["media"] = "media",
        ["bpm"] = "bpm",
        ["danceability"] = "danceability",
        ["energy"] = "energy",
        ["valence"] = "valence",
        ["acousticness"] = "acousticness",
        ["instrumentalness"] = "instrumentalness",
        ["speechiness"] = "speechiness",
        ["loudness"] = "loudness",
        ["tempo"] = "tempo",
        ["timeSignature"] = "timeSignature",
        ["liveness"] = "liveness",
        ["key"] = "key",
        ["mood"] = "mood",
        ["activity"] = "activity",
        ["catalogNumber"] = "catalogNumber",
        ["trackNumber"] = "trackNumber",
        ["discNumber"] = "discNumber",
        ["duration"] = "duration",
        ["trackTotal"] = "trackTotal",
        ["releaseType"] = "releaseType",
        ["discTotal"] = "discTotal",
        ["isrc"] = "isrc",
        ["publishDate"] = "publishDate",
        [AutoTagLiterals.ReleaseDateTag] = AutoTagLiterals.ReleaseDateTag,
        ["year"] = AutoTagLiterals.ReleaseDateTag,
        ["date"] = AutoTagLiterals.ReleaseDateTag,
        ["url"] = "url",
        ["otherTags"] = "otherTags",
        ["metaTags"] = "metaTags",
        ["unsyncedLyrics"] = "unsyncedLyrics",
        ["lyrics"] = "unsyncedLyrics",
        ["syncedLyrics"] = "syncedLyrics",
        ["ttmlLyrics"] = "ttmlLyrics",
        ["explicit"] = "explicit",
        ["length"] = "duration",
        ["barcode"] = "barcode",
        ["upc"] = "barcode",
        ["replayGain"] = "replayGain",
        ["copyright"] = "copyright",
        ["composer"] = "composer",
        ["lyricist"] = "lyricist",
        ["involvedPeople"] = "involvedPeople",
        ["publisher"] = "publisher",
        ["description"] = "description",
        ["comment"] = "description",
        ["comments"] = "description",
        ["source"] = "source",
        ["rating"] = "rating",
        [AutoTagLiterals.LanguageTag] = AutoTagLiterals.LanguageTag
    };
    private static readonly HashSet<string> EnrichmentStageAllowedKeys = BuildEnrichmentStageAllowedKeys();
    private static readonly HashSet<string> EnhancementStageAllowedKeys = BuildEnhancementStageAllowedKeys();
    private static readonly HashSet<string> EligibleAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac",
        ".wav",
        ".aiff",
        ".aif",
        ".alac",
        ".m4a",
        ".m4b",
        ".mp4",
        ".aac",
        ".mp3",
        ".wma",
        ".ogg",
        ".opus",
        ".oga",
        ".ape",
        ".wv",
        ".mp2",
        ".mp1",
        ".tta",
        ".dsf",
        ".dff",
        ".mka"
    };

    private const string AutoTagFolderName = "autotag";
    private const string HistoryFolderName = "history";
    private const string TracknameTemplateKey = "tracknameTemplate";
    private const string AutoTagTitleKey = "title";
    private const string AutoTagArtistsKey = "artists";
    private static readonly string[] DiffMetaKeys =
    {
        AutoTagTitleKey,
        AutoTagArtistsKey,
        "album",
        "albumArtists",
        "composers",
        "trackNumber",
        "trackTotal",
        "releaseType",
        "discNumber",
        "discTotal",
        "genres",
        "bpm",
        "rating",
        "year",
        "key",
        "isrc",
        "hasArtwork",
        "artworkDescription",
        "artworkType"
    };
    private const double IdentityReviewTitleSimilarityThreshold = 0.55d;
    private const double IdentityReviewArtistSimilarityThreshold = 0.50d;

    public event Action<AutoTagJob>? JobCompleted;

    /// <summary>
    /// Raised when a persisted job is found running at load time without an active owner
    /// (i.e. it was interrupted by an application restart). Consumers queue the resume.
    /// </summary>
    public event Action<AutoTagJob>? JobRecovered;

    public AutoTagService(
        IWebHostEnvironment env,
        ILogger<AutoTagService> logger,
        AutoTagServiceCollaborators collaborators)
    {
        _logger = logger;
        _activityLog = collaborators.ActivityLog;
        _metadataService = collaborators.MetadataService;
        _autoTagRunner = collaborators.AutoTagRunner;
        _libraryOrganizer = collaborators.LibraryOrganizer;
        _downloadMoveService = collaborators.DownloadMoveService;
        _queueRepository = collaborators.QueueRepository;
        _quickTagService = collaborators.QuickTagService;
        _platformAuthService = collaborators.PlatformAuthService;
        _plexApiClient = collaborators.PlexApiClient;
        _settingsService = collaborators.SettingsService;
        _libraryRepository = collaborators.LibraryRepository;
        _knownFileIngestionService = collaborators.KnownFileIngestionService;
        _libraryScanRunner = collaborators.LibraryScanRunner;
        _qualityScannerService = collaborators.QualityScannerService;
        _duplicateCleanerService = collaborators.DuplicateCleanerService;
        _lyricsRefreshQueueService = collaborators.LyricsRefreshQueueService;
        _artistImageQueueService = collaborators.ArtistImageQueueService;
        _coverMaintenanceService = collaborators.CoverMaintenanceService;
        _profileResolutionService = collaborators.ProfileResolutionService;
        _mediaServerRefreshService = collaborators.MediaServerRefreshService;
        _mediaServerRefreshOutboxService = collaborators.MediaServerRefreshOutboxService;
        _userPreferencesStore = collaborators.UserPreferencesStore;
        _activitiesRealtime = collaborators.ActivitiesRealtime;
        _downloadEvents = collaborators.DownloadEvents;
        _notifications = collaborators.Notifications ?? NullNotificationSink.Instance;
        var configuration = collaborators.Configuration;
        var appDataRoot = AppDataPaths.GetDataRoot(env);
        var autoTagRoot = Path.Join(appDataRoot, AutoTagFolderName);
        _jobsDir = Path.Join(autoTagRoot, "jobs");
        _historyDir = Path.Join(autoTagRoot, HistoryFolderName);
        var workerDataRoot = AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir());
        _workersHistoryDir = Path.Join(workerDataRoot, AutoTagFolderName, HistoryFolderName);
        _runtimeConfigDir = Path.Join(autoTagRoot, "runtime");
        _lastConfigPath = Path.Join(autoTagRoot, "last-config.json");
        _lastJobPath = Path.Join(autoTagRoot, "last-job.json");
        _runIndexPath = Path.Join(autoTagRoot, "run-index.json");
        Directory.CreateDirectory(autoTagRoot);
        Directory.CreateDirectory(_jobsDir);
        Directory.CreateDirectory(_historyDir);
        Directory.CreateDirectory(_runtimeConfigDir);
        _disableAutoMove = ResolveDisableAutoMove();
        PruneExpiredArchivedRuns(force: true);
        if (ShouldBackfillArchivedRunsOnStartup(configuration))
        {
            BackfillArchivedRuns();
            PruneExpiredArchivedRuns(force: true);
        }
    }
}
