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
{
    private readonly record struct EnrichmentBuildContext(string RunIntent, string JobId);
    private readonly record struct EnhancementBuildContext(string RunIntent, string JobId);
    private readonly record struct AutoMoveExecutionResult(bool Completed, AutoTagMoveSummary Summary);
    private sealed record ResumeCheckpointSeed(
        string SourceJobId,
        string ResumeJobId,
        DateTimeOffset StartedAt,
        AutoTagResumeCheckpoint Checkpoint);

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

    private static HashSet<string> BuildEnrichmentStageAllowedKeys()
    {
        var keys = BuildStageAllowedKeys(
            includeSkipTagged: true,
            includeConflictResolution: true,
            includeTargetFiles: true,
            includeLibraryWideEnhancementBatchSize: true);
        keys.Add(AutoTagLiterals.ManualReleasePreferenceKey);
        keys.Add(AutoTagLiterals.ManualDestinationFolderIdKey);
        keys.Add(AutoTagLiterals.ManualForceFingerprintKey);
        return keys;
    }

    private static HashSet<string> BuildEnhancementStageAllowedKeys()
    {
        var keys = BuildStageAllowedKeys(
            includeSkipTagged: true,
            includeConflictResolution: true,
            includeTargetFiles: true,
            includeLibraryWideEnhancementBatchSize: true);
        keys.Add(AutoTagLiterals.EnhancementStage);
        keys.Add(AutoTagLiterals.EnhancementForceFingerprintKey);
        keys.Add(AutoTagLiterals.ManualForceFingerprintKey);
        keys.Add(AutoTagLiterals.EnhancementUntrustedTargetsKey);
        return keys;
    }

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
    private sealed record PlatformTagCapabilities(HashSet<string> SupportedTags, bool RequiresAuth);
    private sealed record AutoTagStageConfig(string Name, string ConfigPath, int TagCount, string ConfigHash);
    private sealed class FileTagOutcome
    {
        public bool Seen { get; set; }
        public bool Tagged { get; set; }
        public bool CompletedWithoutChanges { get; set; }
    }

    public sealed class AutoTagServiceCollaborators
    {
        public required IConfiguration Configuration { get; init; }
        public required LibraryConfigStore ActivityLog { get; init; }
        public required AutoTagMetadataService MetadataService { get; init; }
        public required DeezSpoTag.Web.Services.AutoTag.IAutoTagRunner AutoTagRunner { get; init; }
        public required AutoTagLibraryOrganizer LibraryOrganizer { get; init; }
        public required AutoTagDownloadMoveService DownloadMoveService { get; init; }
        public required DownloadQueueRepository QueueRepository { get; init; }
        public required QuickTagService QuickTagService { get; init; }
        public required PlatformAuthService PlatformAuthService { get; init; }
        public required PlexApiClient PlexApiClient { get; init; }
        public required DeezSpoTag.Services.Settings.DeezSpoTagSettingsService SettingsService { get; init; }
        public required LibraryRepository LibraryRepository { get; init; }
        public required KnownLibraryFileIngestionService KnownFileIngestionService { get; init; }
        public required LibraryScanRunner LibraryScanRunner { get; init; }
        public required QualityScannerService QualityScannerService { get; init; }
        public required DuplicateCleanerService DuplicateCleanerService { get; init; }
        public required LyricsRefreshQueueService LyricsRefreshQueueService { get; init; }
        public required LibraryArtistImageQueueService ArtistImageQueueService { get; init; }
        public required CoverLibraryMaintenanceService CoverMaintenanceService { get; init; }
        public required AutoTagProfileResolutionService ProfileResolutionService { get; init; }
        public required MediaServerLibraryRefreshService MediaServerRefreshService { get; init; }
        public required MediaServerRefreshOutboxService MediaServerRefreshOutboxService { get; init; }
        public required UserPreferencesStore UserPreferencesStore { get; init; }
        public required ActivitiesRealtimeService ActivitiesRealtime { get; init; }
        public required IDeezSpoTagListener DownloadEvents { get; init; }
        public INotificationSink? Notifications { get; init; }
    }

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

    public bool HasRunningJobs()
    {
        return !_activeJobIds.IsEmpty;
    }

    public bool TryGetRunningEnhancementJobId(out string? jobId)
    {
        var stage = _activeJobStages.FirstOrDefault(
            static entry => string.Equals(entry.Value, AutoTagLiterals.EnhancementStage, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(stage.Key))
        {
            jobId = stage.Key;
            return true;
        }

        var activeJobId = _activeJobIds.Keys.FirstOrDefault(activeJobId =>
            _jobs.TryGetValue(activeJobId, out var activeJob)
            && string.Equals(activeJob.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase)
            && (IsEnhancementRunIntent(activeJob.RunIntent)
                || IsManualEnrichmentRunIntent(activeJob.RunIntent)));
        if (!string.IsNullOrWhiteSpace(activeJobId))
        {
            jobId = activeJobId;
            return true;
        }

        jobId = null;
        return false;
    }

    public bool TryGetRunningEnrichmentJobId(out string? jobId)
    {
        var stage = _activeJobStages.FirstOrDefault(
            static entry => string.Equals(entry.Value, AutoTagLiterals.EnrichmentStage, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(stage.Key))
        {
            jobId = stage.Key;
            return true;
        }

        var manualJobId = _activeJobIds.Keys.FirstOrDefault(activeJobId =>
            _jobs.TryGetValue(activeJobId, out var activeJob)
            && string.Equals(activeJob.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase)
            && IsManualEnrichmentRunIntent(activeJob.RunIntent));
        if (!string.IsNullOrWhiteSpace(manualJobId))
        {
            jobId = manualJobId;
            return true;
        }

        jobId = null;
        return false;
    }

    public bool TryGetAnyRunningJobId(out string? jobId)
    {
        var running = _activeJobIds.Keys.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(running))
        {
            jobId = running;
            return true;
        }

        jobId = null;
        return false;
    }
    /* StartJobOptions moved to AutoTagService.Lifecycle.cs */

    /* StartJob moved to AutoTagService.Lifecycle.cs */

    /* PrepareRuntimeConfigAndRunJobAsync moved to AutoTagService.Lifecycle.cs */

    /* TryCreateBlockedJobForTriggerPolicy moved to AutoTagService.Lifecycle.cs */

    /* ShouldSkipForActiveDownloadsAsync moved to AutoTagService.Lifecycle.cs */

    /* TryCreateBlockedJobForActiveJobPolicy moved to AutoTagService.Lifecycle.cs */

    /* TryCreateBlockedJobForScopePolicyAsync moved to AutoTagService.Lifecycle.cs */

    /* HydrateResumeJob moved to AutoTagService.Lifecycle.cs */

    /* CreateBlockedJob moved to AutoTagService.Lifecycle.cs */

    /* CreateSkippedJob moved to AutoTagService.Lifecycle.cs */

    /* ValidateRunIntentScopeAsync moved to AutoTagService.Lifecycle.cs */

    /* ResolveAllowedLibraryRootsAsync moved to AutoTagService.Lifecycle.cs */

    /* TryResolveResumeCheckpointSeed moved to AutoTagService.Lifecycle.cs */

    /* FindLatestResumeScopeJob moved to AutoTagService.Lifecycle.cs */

    /* TryLoadResumeScopeJob moved to AutoTagService.Lifecycle.cs */

    /* IsEligibleResumeCandidate moved to AutoTagService.Lifecycle.cs */

    /* BuildResumeCheckpointSeed moved to AutoTagService.Lifecycle.cs */

    /* ResolveResumeRootJob moved to AutoTagService.Lifecycle.cs */

    /* IsResumeScopeMatch moved to AutoTagService.Lifecycle.cs */

    /* IsResumeCandidate moved to AutoTagService.Lifecycle.cs */

    /* CloneResumeCheckpoint moved to AutoTagService.Lifecycle.cs */

    /* NormalizePathForJob moved to AutoTagService.Lifecycle.cs */

    /* HasEligibleInputFiles moved to AutoTagService.Lifecycle.cs */

    /* NormalizeRootPath moved to AutoTagService.Lifecycle.cs */

    /* TryParseAutoTagConfig moved to AutoTagService.Lifecycle.cs */

    /* HasEligibleTargetFiles moved to AutoTagService.Lifecycle.cs */

    /* HasEligibleFilesInDirectory moved to AutoTagService.Lifecycle.cs */

    /* IsPathWithinScope moved to AutoTagService.Lifecycle.cs */

    /* GetJob moved to AutoTagService.Lifecycle.cs */

    /* GetLatestJob moved to AutoTagService.Lifecycle.cs */

    /* GetArchivedRunCalendar moved to AutoTagService.Lifecycle.cs */

    /* GetArchivedRunsByDate moved to AutoTagService.Lifecycle.cs */

    /* GetRunHistoryTimestamp moved to AutoTagService.Lifecycle.cs */

    /* GetRunDate moved to AutoTagService.Lifecycle.cs */

    /* GetRunDateToken moved to AutoTagService.Lifecycle.cs */

    /* GetArchivedRun moved to AutoTagService.Lifecycle.cs */

    /* GetTagDiff moved to AutoTagService.Lifecycle.cs */

    /* TryResolveTagDiff moved to AutoTagService.Lifecycle.cs */

    /* SelectRequestedPlatformDiff moved to AutoTagService.Lifecycle.cs */

    /* CloneDiff moved to AutoTagService.Lifecycle.cs */

    /* ClonePlatformDiff moved to AutoTagService.Lifecycle.cs */

    /* ComputeRetainedSources moved to AutoTagService.Lifecycle.cs */

    /* AddRetainedMetaSources moved to AutoTagService.Lifecycle.cs */

    /* AddRetainedTagSources moved to AutoTagService.Lifecycle.cs */

    /* ResolveValueSource moved to AutoTagService.Lifecycle.cs */

    /* ResolveCurrentValueAndSource moved to AutoTagService.Lifecycle.cs */

    /* ResolveFallbackTransitionSource moved to AutoTagService.Lifecycle.cs */

    /* ResolveMergedValueSources moved to AutoTagService.Lifecycle.cs */

    /* GetMetaFieldValue moved to AutoTagService.Lifecycle.cs */


    private static string NormalizeCompareValue(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text.Trim().ToLowerInvariant();
        }

        if (value is IEnumerable<string> stringValues)
        {
            return string.Join(
                "|",
                stringValues
                    .Select(item => item?.Trim() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.ToLowerInvariant()));
        }

        if (value is bool boolean)
        {
            return boolean ? "true" : "false";
        }

        return value.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static HashSet<string> NormalizeCompareParts(object? value)
    {
        if (value is IEnumerable<string> stringValues)
        {
            return stringValues
                .Select(item => item?.Trim() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);
        }

        var normalized = NormalizeCompareValue(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(new[] { normalized }, StringComparer.Ordinal);
    }
    /* TryGetLastJobId moved to AutoTagService.RunHistoryQuery.cs */

    /* TryGetLastConfigJson moved to AutoTagService.RunHistoryQuery.cs */

    /* StopJobAsync moved to AutoTagService.RunHistoryQuery.cs */

    /* StopJobWithStatusAsync moved to AutoTagService.RunHistoryQuery.cs */

    /* StopJobInternalAsync moved to AutoTagService.RunHistoryQuery.cs */


    /// <summary>
    /// Outcome of an explicit resume attempt for API surfacing.
    /// </summary>
    public sealed record ResumeJobOutcome(bool Success, string? Error, string? ResumedJobId);

    public sealed record StopJobOutcome(bool Stopped, string? Status);
    /* ResumeJobAsync moved to AutoTagService.Resume.cs */

    /* IsBlockedResumeSuccessor moved to AutoTagService.Resume.cs */

    /* NotifyRunStopped moved to AutoTagService.Resume.cs */

    /* ResolveStopStatus moved to AutoTagService.Resume.cs */

    /* NormalizeStopReason moved to AutoTagService.Resume.cs */

    /* BuildStopError moved to AutoTagService.Resume.cs */

    /* BuildStopActivityLog moved to AutoTagService.Resume.cs */

    /* RunJobAsync moved to AutoTagService.Resume.cs */

    /* IsActiveJobStatus moved to AutoTagService.Resume.cs */

    /* IsPausedOrInterruptedRunStatus moved to AutoTagService.Resume.cs */

    /* CreateCompactTerminalJob moved to AutoTagService.Resume.cs */

    /* CreateJobPersistenceSnapshot moved to AutoTagService.Resume.cs */

    /* HasOtherActiveJobs moved to AutoTagService.Resume.cs */

    /* RunJobCoreAsync moved to AutoTagService.Resume.cs */

    /* FinalizeStageExecution moved to AutoTagService.Resume.cs */

    /* HandleRunJobCanceled moved to AutoTagService.Resume.cs */

    /* IsTerminalStopStatus moved to AutoTagService.Resume.cs */

    /* ShouldPreserveRuntimeConfigFilesForResume moved to AutoTagService.Resume.cs */

    /* InitializeRuntimeConfigPaths moved to AutoTagService.Resume.cs */

    /* RegisterStageRuntimeConfigPaths moved to AutoTagService.Resume.cs */

    /* TryMarkNoStagesConfigured moved to AutoTagService.Resume.cs */

    /* StageRunResult moved to AutoTagService.Resume.cs */

    /* ExecuteStagesAsync moved to AutoTagService.Resume.cs */

    /* StageExecutionResult moved to AutoTagService.Resume.cs */

    /* ExecuteSingleStageAsync moved to AutoTagService.Resume.cs */

    /* TryHandlePausedStage moved to AutoTagService.Resume.cs */


    private static StageExecutionResult HandleStoppedStage(AutoTagJob job)
    {
        if (IsTerminalStopStatus(job.Status))
        {
            return new StageExecutionResult(false);
        }

        // The runner stopped without StopJobAsync having stamped a status (external kill).
        // Enhancement runs stay resumable: interrupted, never canceled.
        job.Status = IsEnhancementRunIntent(job.RunIntent) || IsManualEnrichmentRunIntent(job.RunIntent)
            ? AutoTagLiterals.InterruptedStatus
            : AutoTagLiterals.CanceledStatus;
        job.Error = IsEnhancementRunIntent(job.RunIntent) || IsManualEnrichmentRunIntent(job.RunIntent)
            ? "Interrupted. Resume is available."
            : "Stopped by user.";
        return new StageExecutionResult(false);
    }

    private void EnsureInitialEnrichmentResumeCheckpoint(AutoTagJob job, AutoTagStageConfig stage)
    {
        if (job.ResumeCheckpoint != null
            || !string.Equals(stage.Name, AutoTagLiterals.EnrichmentStage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        job.ResumeCheckpoint = new AutoTagResumeCheckpoint
        {
            StageName = stage.Name,
            StageConfigHash = stage.ConfigHash,
            PlatformIndex = 0,
            FileIndex = 0,
            PlatformCount = 1,
            FileCount = 1,
            LastPath = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        SaveJob(job);
    }

    private void EnsureInitialEnrichmentResumeCheckpoint(AutoTagJob job, IReadOnlyList<AutoTagStageConfig> stages)
    {
        if (job.ResumeCheckpoint != null)
        {
            return;
        }

        var stage = stages.FirstOrDefault(stage =>
            string.Equals(stage.Name, AutoTagLiterals.EnrichmentStage, StringComparison.OrdinalIgnoreCase));
        if (stage == null)
        {
            return;
        }

        EnsureInitialEnrichmentResumeCheckpoint(job, stage);
    }

    private async Task RunSuccessPostProcessingAsync(
        AutoTagJob job,
        string path,
        SuccessPostProcessingContext context,
        CancellationToken cancellationToken)
    {
        var isManualEnrichment = IsManualEnrichmentRunIntent(job.RunIntent);
        var autoMove = await RunFinalAutoMoveAsync(job, path, context.ConfigPath, context.FileOutcomes, cancellationToken);
        if (isManualEnrichment && !autoMove.Completed)
        {
            throw new InvalidOperationException(
                autoMove.Summary.Error ?? "Manual enrichment finalization did not move every fully enriched file.");
        }
        await RunIntegratedEnhancementWorkflowsAsync(
            job,
            path,
            context.ConfigPath,
            context.IncludesEnhancementWorkflows,
            cancellationToken,
            autoMove.Summary);
        var hasEnhancementWork = context.IncludesEnhancementStage
            || context.IncludesEnhancementWorkflows
            || isManualEnrichment;
        if (autoMove.Completed && !isManualEnrichment)
        {
            await TriggerPlexScanAfterMoveAsync(job, cancellationToken);
        }
        if (!isManualEnrichment)
        {
            // Manual enrichment already ingested the moved paths before its sidecar
            // lookup; the lookup resolves track identities at the moved paths.
            await IngestKnownFilesAfterAutoMoveAsync(
                job,
                autoMove.Summary,
                cancellationToken);
        }
        await TriggerConfiguredMediaServerRefreshAfterEnhancementAsync(
            job,
            hasEnhancementWork,
            cancellationToken);
    }

    private sealed class SuccessPostProcessingContext
    {
        public required string ConfigPath { get; init; }
        public required bool IncludesEnrichmentStage { get; init; }
        public required bool IncludesEnhancementStage { get; init; }
        public required bool IncludesEnhancementWorkflows { get; init; }
        public required Dictionary<string, FileTagOutcome> FileOutcomes { get; init; }
    }
    /* RunFinalAutoMoveAsync moved to AutoTagService.StagePipeline.cs */

    /* HandleRunJobFailureAsync moved to AutoTagService.StagePipeline.cs */

    /* BuildStageConfigsAsync moved to AutoTagService.StagePipeline.cs */

    /* IngestKnownFilesAfterAutoMoveAsync moved to AutoTagService.StagePipeline.cs */

    /* ResolveChangedLibraryFolderIdsAsync moved to AutoTagService.StagePipeline.cs */

    /* ResolveChangedLibraryFilesByFolderAsync moved to AutoTagService.StagePipeline.cs */

    /* TryAddPathToFolderGroup moved to AutoTagService.StagePipeline.cs */

    /* AddMatchingLibraryFolders moved to AutoTagService.StagePipeline.cs */

    /* ParseFolderIds moved to AutoTagService.StagePipeline.cs */

    /* IsMusicCapableFolder moved to AutoTagService.StagePipeline.cs */

    /* PathsOverlap moved to AutoTagService.StagePipeline.cs */

    /* IsPathUnderRoot moved to AutoTagService.StagePipeline.cs */

    /* ReadBoundedInt moved to AutoTagService.StagePipeline.cs */

    /* ReadOptionalInt moved to AutoTagService.StagePipeline.cs */

    /* LoadConfigRoot moved to AutoTagService.StagePipeline.cs */

    /* BuildFolderUniformityOptions moved to AutoTagService.StagePipeline.cs */

    /* ResolveEnhancementRequestedTags moved to AutoTagService.StagePipeline.cs */

    /* TryBuildEnhancementStage moved to AutoTagService.StagePipeline.cs */

    /* CloneRoot moved to AutoTagService.StagePipeline.cs */

    /* ComputeConfigHash moved to AutoTagService.StagePipeline.cs */

    /* ReadStringList moved to AutoTagService.StagePipeline.cs */

    /* WriteStringList moved to AutoTagService.StagePipeline.cs */

    /* BuildStageAllowedKeys moved to AutoTagService.StagePipeline.cs */

    /* ApplyStageSchema moved to AutoTagService.StagePipeline.cs */

    /* AppendStageSchemaLog moved to AutoTagService.StagePipeline.cs */

    /* LoadPlatformCapabilitiesAsync moved to AutoTagService.StagePipeline.cs */

    /* GetPlatformId moved to AutoTagService.StagePipeline.cs */

    /* ReadPlatformList moved to AutoTagService.StagePipeline.cs */

    /* ReadPlatformRequiresAuth moved to AutoTagService.StagePipeline.cs */

    /* FilterSupportedTags moved to AutoTagService.StagePipeline.cs */

    /* ResolveEligiblePlatformsAsync moved to AutoTagService.StagePipeline.cs */

    /* RequiresPlatformAuth moved to AutoTagService.StagePipeline.cs */

    /* IsPlatformAuthenticated moved to AutoTagService.StagePipeline.cs */

    /* IsSpotifyAuthenticated moved to AutoTagService.StagePipeline.cs */

    /* HasBpmSupremeCredentials moved to AutoTagService.StagePipeline.cs */

    /* IsPlexAuthenticated moved to AutoTagService.StagePipeline.cs */

    /* IsJellyfinAuthenticated moved to AutoTagService.StagePipeline.cs */

    /* ReadBool moved to AutoTagService.StagePipeline.cs */

    /* NormalizeDownloadTagSource moved to AutoTagService.StagePipeline.cs */

    /* ResolveDownloadSourcePlatform moved to AutoTagService.StagePipeline.cs */

    /* NormalizeSupportedTagKey moved to AutoTagService.StagePipeline.cs */

    /* NotifyCompleted moved to AutoTagService.StagePipeline.cs */

    /* LoadOrganizerOptions moved to AutoTagService.StagePipeline.cs */

    /* LoadOrganizerOptionsAsync moved to AutoTagService.StagePipeline.cs */

    /* ApplyJobProfileOrganizerOverridesAsync moved to AutoTagService.StagePipeline.cs */

    /* ResolveJobProfileAsync moved to AutoTagService.StagePipeline.cs */

    /* MoveAfterAutoTagAsync moved to AutoTagService.StagePipeline.cs */

    /* ApplyAutoMoveSummary moved to AutoTagService.StagePipeline.cs */

    /* TriggerPlexScanAfterMoveAsync moved to AutoTagService.StagePipeline.cs */

    /* TriggerConfiguredMediaServerRefreshAfterEnhancementAsync moved to AutoTagService.StagePipeline.cs */

    /* LoadConfiguredPlexForScanAsync moved to AutoTagService.StagePipeline.cs */

    /* TriggerPlexScanAsync moved to AutoTagService.StagePipeline.cs */


    private static bool ResolveDisableAutoMove()
    {
        var env = Environment.GetEnvironmentVariable("DEEZSPOTAG_DISABLE_AUTOMOVE");
        if (!string.IsNullOrWhiteSpace(env) && bool.TryParse(env, out var value))
        {
            return value;
        }
        return false;
    }
    /* SanitizeConfigJson moved to AutoTagService.RuntimeConfig.cs */

    /* EnsureEffectivePlatforms moved to AutoTagService.RuntimeConfig.cs */

    /* EnsureShazamFlagsFollowPlatforms moved to AutoTagService.RuntimeConfig.cs */

    /* EnsureTracknameTemplateCanonical moved to AutoTagService.RuntimeConfig.cs */

    /* EnsureSupportedDownloadTagSource moved to AutoTagService.RuntimeConfig.cs */

    /* EnsureOverwriteDefaults moved to AutoTagService.RuntimeConfig.cs */

    /* EnsureEnhancementFolderScopesCanonical moved to AutoTagService.RuntimeConfig.cs */

    /* CanonicalizeEnhancementFolderScopeSection moved to AutoTagService.RuntimeConfig.cs */

    /* TryParseLegacyFolderId moved to AutoTagService.RuntimeConfig.cs */

    /* EnsureLegacyFolderUniformityStructureMirrorsRemoved moved to AutoTagService.RuntimeConfig.cs */

    /* EnsureLegacyOrganizerConfigRemoved moved to AutoTagService.RuntimeConfig.cs */

    /* EnsureLegacyDeezerAuthRemoved moved to AutoTagService.RuntimeConfig.cs */

    /* RedactSensitiveConfigJson moved to AutoTagService.RuntimeConfig.cs */

    /* RedactSensitiveNode moved to AutoTagService.RuntimeConfig.cs */

    /* ShouldRedactConfigKey moved to AutoTagService.RuntimeConfig.cs */

    /* NormalizeConfigKeyForRedaction moved to AutoTagService.RuntimeConfig.cs */

    /* TrySaveLastConfig moved to AutoTagService.RuntimeConfig.cs */

    /* WriteRuntimeConfigFile moved to AutoTagService.RuntimeConfig.cs */

    /* CleanupRuntimeConfigFiles moved to AutoTagService.RuntimeConfig.cs */

    /* IsRuntimeConfigPath moved to AutoTagService.RuntimeConfig.cs */

    /* TrySaveLastJobId moved to AutoTagService.RuntimeConfig.cs */

    /* InjectPlatformDefaultsAsync moved to AutoTagService.RuntimeConfig.cs */

    /* TryGetPlatformOptionDefaults moved to AutoTagService.RuntimeConfig.cs */

    /* GetOrCreateCustomNode moved to AutoTagService.RuntimeConfig.cs */

    /* GetOrCreatePlatformCustomNode moved to AutoTagService.RuntimeConfig.cs */

    /* TryApplyPlatformOptionDefault moved to AutoTagService.RuntimeConfig.cs */

    /* NormalizeRunTrigger moved to AutoTagService.RuntimeConfig.cs */

    /* NormalizeRunIntent moved to AutoTagService.RuntimeConfig.cs */

    /* NormalizeEnhancementFeature moved to AutoTagService.RuntimeConfig.cs */

    /* IsEnhancementRunIntent moved to AutoTagService.RuntimeConfig.cs */

    /* IsManualEnrichmentRunIntent moved to AutoTagService.RuntimeConfig.cs */

    /* IsAllowedEnhancementTrigger moved to AutoTagService.RuntimeConfig.cs */

    /* ShouldRunEnrichmentForIntent moved to AutoTagService.RuntimeConfig.cs */

    /* ShouldRunEnhancementForIntent moved to AutoTagService.RuntimeConfig.cs */

    /* ShouldRunIntegratedWorkflowsForIntent moved to AutoTagService.RuntimeConfig.cs */

    /* InjectRunTrigger moved to AutoTagService.RuntimeConfig.cs */

    /* InjectProfileRuntimeSettings moved to AutoTagService.RuntimeConfig.cs */

    /* InjectPlatformAuthAsync moved to AutoTagService.RuntimeConfig.cs */

    /* ApplyDiscogsAuthDefaults moved to AutoTagService.RuntimeConfig.cs */

    /* ApplyLastFmAuthDefaults moved to AutoTagService.RuntimeConfig.cs */

    /* ApplyBpmSupremeAuthDefaults moved to AutoTagService.RuntimeConfig.cs */

    /* SetIfEmpty moved to AutoTagService.RuntimeConfig.cs */

    /* RemoveNulls moved to AutoTagService.RuntimeConfig.cs */

    /* EnsureSpotifySecret moved to AutoTagService.RuntimeConfig.cs */

    /* UpdateStatus moved to AutoTagService.StatusStreaming.cs */

    /* TrackEnhancedFilePath moved to AutoTagService.StatusStreaming.cs */

    /* RouteReviewFileIfNeeded moved to AutoTagService.StatusStreaming.cs */

    /* PauseForMissingReviewFolder moved to AutoTagService.StatusStreaming.cs */

    /* NotifyDownloadToast moved to AutoTagService.StatusStreaming.cs */

    /* ResolveReviewFolderIoPath moved to AutoTagService.StatusStreaming.cs */

    /* IsReviewFolderWritable moved to AutoTagService.StatusStreaming.cs */

    /* HasShazamReviewCandidate moved to AutoTagService.StatusStreaming.cs */

    /* ResolveReviewDestinationPath moved to AutoTagService.StatusStreaming.cs */

    /* GetAvailableReviewPath moved to AutoTagService.StatusStreaming.cs */

    /* BuildReviewReport moved to AutoTagService.StatusStreaming.cs */

    /* FirstNonEmpty moved to AutoTagService.StatusStreaming.cs */

    /* FormatNullableDouble moved to AutoTagService.StatusStreaming.cs */

    /* IsTerminalStatus moved to AutoTagService.StatusStreaming.cs */

    /* TryUpdateResumeCheckpoint moved to AutoTagService.StatusStreaming.cs */

    /* ResolveResumeCursor moved to AutoTagService.StatusStreaming.cs */

    /* CanApplyResumeCheckpoint moved to AutoTagService.StatusStreaming.cs */

    /* TryCaptureTagDiff moved to AutoTagService.StatusStreaming.cs */

    /* ApplyIdentityReviewGuard moved to AutoTagService.StatusStreaming.cs */

    /* IsSuccessfulTagStatus moved to AutoTagService.StatusStreaming.cs */

    /* EvaluateIdentityReviewGuard moved to AutoTagService.StatusStreaming.cs */

    /* HasMeaningfulIdentityChange moved to AutoTagService.StatusStreaming.cs */

    /* ComputeIdentitySimilarity moved to AutoTagService.StatusStreaming.cs */

    /* TryResolveCaptureMode moved to AutoTagService.StatusStreaming.cs */

    /* TryBuildDiffSnapshot moved to AutoTagService.StatusStreaming.cs */

    /* GetOrCreateTagDiff moved to AutoTagService.StatusStreaming.cs */

    /* GetOrCreatePlatformDiff moved to AutoTagService.StatusStreaming.cs */

    /* ApplyCapturedDiffSnapshot moved to AutoTagService.StatusStreaming.cs */

    /* BuildTagSnapshot moved to AutoTagService.StatusStreaming.cs */

    /* CloneTags moved to AutoTagService.StatusStreaming.cs */

    /* NormalizeDiffPath moved to AutoTagService.StatusStreaming.cs */

    /* TrackFileOutcome moved to AutoTagService.StatusStreaming.cs */

    /* BuildMoveFileSets moved to AutoTagService.StatusStreaming.cs */

    /* ScaleProgress moved to AutoTagService.StatusStreaming.cs */

    /* BuildStageStartedLog moved to AutoTagService.StatusStreaming.cs */

    /* BuildStageFinishedLog moved to AutoTagService.StatusStreaming.cs */

    /* FormatStageName moved to AutoTagService.StatusStreaming.cs */

    /* AppendStatusHistory moved to AutoTagService.StatusStreaming.cs */

    /* AppendLog moved to AutoTagService.StatusStreaming.cs */

    /* AppendActivityLog moved to AutoTagService.StatusStreaming.cs */

    /* TrackStartedPlatform moved to AutoTagService.StatusStreaming.cs */

    /* AppendPlatformSummary moved to AutoTagService.StatusStreaming.cs */

    /* TryExtractStartedPlatform moved to AutoTagService.StatusStreaming.cs */

    /* ResolveLogLevel moved to AutoTagService.StatusStreaming.cs */

    /* StripLinePrefix moved to AutoTagService.StatusStreaming.cs */

    /* InitializeRunArchive moved to AutoTagService.RunArchive.cs */

    /* GetArchivedRunSummaries moved to AutoTagService.RunArchive.cs */

    /* LoadRunIndexSummaries moved to AutoTagService.RunArchive.cs */

    /* WarmRunIndexIfMissing moved to AutoTagService.RunArchive.cs */

    /* TryLoadRunIndex moved to AutoTagService.RunArchive.cs */

    /* UpdateRunIndex moved to AutoTagService.RunArchive.cs */

    /* ShouldUpdateRunIndex moved to AutoTagService.RunArchive.cs */

    /* IsTerminalRunStatus moved to AutoTagService.RunArchive.cs */

    /* PersistRunIndex moved to AutoTagService.RunArchive.cs */

    /* PruneExpiredArchivedRuns moved to AutoTagService.RunArchive.cs */

    /* ResolveArchivedRunRetentionPeriod moved to AutoTagService.RunArchive.cs */

    /* IsExpiredArchivedRun moved to AutoTagService.RunArchive.cs */

    /* DeleteArchivedRunFiles moved to AutoTagService.RunArchive.cs */

    /* PruneOrphanedArchivedRunArtifacts moved to AutoTagService.RunArchive.cs */

    /* PruneOrphanedHistoryDirectories moved to AutoTagService.RunArchive.cs */

    /* PruneOrphanedJobSnapshots moved to AutoTagService.RunArchive.cs */

    /* GetFileSystemTimestampUtc moved to AutoTagService.RunArchive.cs */

    /* TryDeleteDirectory moved to AutoTagService.RunArchive.cs */

    /* TryDeleteFile moved to AutoTagService.RunArchive.cs */

    /* NormalizeRunIndexSummaries moved to AutoTagService.RunArchive.cs */

    /* GetRunIndexGroupKey moved to AutoTagService.RunArchive.cs */

    /* ResolveResumeRootJobId moved to AutoTagService.RunArchive.cs */

    /* LoadArchivedRunSummaries moved to AutoTagService.RunArchive.cs */

    /* InvalidateArchivedRunSummariesCache moved to AutoTagService.RunArchive.cs */

    /* AppendArchivedLog moved to AutoTagService.RunArchive.cs */

    /* AppendArchivedStatus moved to AutoTagService.RunArchive.cs */

    /* SaveRunSummary moved to AutoTagService.RunArchive.cs */

    /* BuildRunSummary moved to AutoTagService.RunArchive.cs */

    /* ResolveRunHistoryDate moved to AutoTagService.RunArchive.cs */

    /* LoadRunSummary moved to AutoTagService.RunArchive.cs */

    /* LoadRunSummaryFromPath moved to AutoTagService.RunArchive.cs */

    /* TryReadJobResumeFromJobId moved to AutoTagService.RunArchive.cs */

    /* ReadRunLogLines moved to AutoTagService.RunArchive.cs */

    /* ReadRunStatusHistory moved to AutoTagService.RunArchive.cs */

    /* TryRepairArchivedLogsFromJob moved to AutoTagService.RunArchive.cs */

    /* TryRepairArchivedStatusFromJob moved to AutoTagService.RunArchive.cs */

    /* GetArchivedLogCount moved to AutoTagService.RunArchive.cs */

    /* GetArchivedStatusCount moved to AutoTagService.RunArchive.cs */

    /* CountArchiveFileLines moved to AutoTagService.RunArchive.cs */

    /* GetRunHistoryDirectory moved to AutoTagService.RunArchive.cs */

    /* GetRunSummaryPath moved to AutoTagService.RunArchive.cs */

    /* GetRunLogPath moved to AutoTagService.RunArchive.cs */

    /* GetRunStatusHistoryPath moved to AutoTagService.RunArchive.cs */

    /* GetRunTagDiffsPath moved to AutoTagService.RunArchive.cs */

    /* GetRunTagDiffCheckpointDirectory moved to AutoTagService.RunArchive.cs */

    /* SaveTagDiffCheckpoint moved to AutoTagService.RunArchive.cs */

    /* SaveArchivedTagDiffs moved to AutoTagService.RunArchive.cs */

    /* LoadPersistedTagDiffs moved to AutoTagService.RunArchive.cs */

    /* ReadRunTagDiffs moved to AutoTagService.RunArchive.cs */

    /* TryRepairArchivedTagDiffsFromJob moved to AutoTagService.RunArchive.cs */

    /* ParseStatusHistoryEntries moved to AutoTagService.RunArchive.cs */

    /* RecordMalformedHistoryEntry moved to AutoTagService.Recovery.cs */

    /* EnumerateHistoryRoots moved to AutoTagService.Recovery.cs */

    /* ResolveRunFilePath moved to AutoTagService.Recovery.cs */

    /* EnumerateRunFileCandidates moved to AutoTagService.Recovery.cs */

    /* BackfillArchivedRuns moved to AutoTagService.Recovery.cs */

    /* ShouldBackfillArchivedRunsOnStartup moved to AutoTagService.Recovery.cs */

    /* ShouldRepairRunArchive moved to AutoTagService.Recovery.cs */

    /* IsRunArchiveComplete moved to AutoTagService.Recovery.cs */

    /* MaterializeRunArchive moved to AutoTagService.Recovery.cs */

    /* SaveJob moved to AutoTagService.Recovery.cs */

    /* SaveJobThrottled moved to AutoTagService.Recovery.cs */

    /* ShouldThrottleJobSave moved to AutoTagService.Recovery.cs */

    /* LoadJob moved to AutoTagService.Recovery.cs */

    /* NormalizeLoadedJobState moved to AutoTagService.Recovery.cs */

    /* ResolveLastActivityTimestamp moved to AutoTagService.Recovery.cs */

    /* RecoverStuckJobsAsync moved to AutoTagService.Recovery.cs */

    /* StopActiveJobsWithoutProgressAsync moved to AutoTagService.Recovery.cs */

    /* RecoverPersistedRunningJobsAsync moved to AutoTagService.Recovery.cs */

    /* RecoverPersistedRunningJobAsync moved to AutoTagService.Recovery.cs */

    /* TryAutoResumeRecoveredJobAsync moved to AutoTagService.Recovery.cs */

    /* WaitForJobToLeaveActiveSetAsync moved to AutoTagService.Recovery.cs */

    /* IsRunningStatus moved to AutoTagService.Recovery.cs */

    /* GetLastProgressTimestamp moved to AutoTagService.Recovery.cs */

    /* FormatDuration moved to AutoTagService.Recovery.cs */

    /* TryFindRuntimeConfigPath moved to AutoTagService.Recovery.cs */

}
