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
    internal const string CanceledStatus = "canceled";
    internal const string InterruptedStatus = "interrupted";
    internal const string PausedStatus = "paused";
    internal const string FailedStatus = "failed";
    internal const string CompletedStatus = "completed";
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
}

public class TaggingStatus
{
    public string Status { get; set; } = "";
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

    public sealed record StartJobOptions(
        string Trigger = AutoTagLiterals.ManualTrigger,
        TechnicalTagSettings? TechnicalOverride = null,
        string? ProfileId = null,
        string? ProfileName = null,
        string? RunIntent = null,
        FolderStructureSettings? FolderStructureOverride = null,
        string? EnhancementFeature = null,
        string? EnhancementGroupId = null);

    public async Task<AutoTagJob?> StartJob(
        string path,
        string configJson,
        StartJobOptions? options = null)
    {
        options ??= new StartJobOptions();
        var normalizedPath = NormalizePathForJob(path);
        var normalizedTrigger = NormalizeRunTrigger(options.Trigger);
        var normalizedRunIntent = NormalizeRunIntent(options.RunIntent);
        var resumeSeed = TryResolveResumeCheckpointSeed(normalizedPath, normalizedRunIntent, options.ProfileId);
        var resumeSourceJob = resumeSeed == null ? null : GetJob(resumeSeed.SourceJobId) ?? LoadJob(resumeSeed.SourceJobId);
        var resumedJobId = resumeSeed?.ResumeJobId ?? Guid.NewGuid().ToString("N");
        var resumedStartedAt = resumeSeed?.StartedAt ?? DateTimeOffset.UtcNow;

        var blockedByTriggerPolicy = TryCreateBlockedJobForTriggerPolicy(
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            options.ProfileId,
            options.ProfileName);
        if (blockedByTriggerPolicy != null)
        {
            return blockedByTriggerPolicy;
        }

        if (await ShouldSkipForActiveDownloadsAsync())
        {
            _logger.LogInformation("AutoTag skipped: downloads active.");
            return null;
        }

        var blockedByScope = await TryCreateBlockedJobForScopePolicyAsync(
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            options.ProfileId,
            options.ProfileName);
        if (blockedByScope != null)
        {
            return blockedByScope;
        }

        var blockedByActiveJob = TryCreateBlockedJobForActiveJobPolicy(
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            options.ProfileId,
            options.ProfileName);
        if (blockedByActiveJob != null)
        {
            return blockedByActiveJob;
        }

        if (!HasEligibleInputFiles(normalizedPath, configJson))
        {
            return CreateSkippedJob(
                "No eligible audio files were found for this run.",
                normalizedPath,
                normalizedTrigger,
                normalizedRunIntent,
                options.ProfileId,
                options.ProfileName);
        }

        var job = new AutoTagJob
        {
            Id = resumedJobId,
            Status = AutoTagLiterals.RunningStatus,
            StartedAt = resumedStartedAt,
            RootPath = normalizedPath,
            Trigger = normalizedTrigger,
            RunIntent = normalizedRunIntent,
            ProfileId = string.IsNullOrWhiteSpace(options.ProfileId) ? null : options.ProfileId.Trim(),
            ProfileName = string.IsNullOrWhiteSpace(options.ProfileName) ? null : options.ProfileName.Trim(),
            EnhancementFeature = NormalizeEnhancementFeature(options.EnhancementFeature),
            EnhancementGroupId = string.IsNullOrWhiteSpace(options.EnhancementGroupId) ? null : options.EnhancementGroupId.Trim(),
            ResumeCheckpoint = resumeSeed?.Checkpoint,
            ResumeFromJobId = null,
            LastActivityAt = DateTimeOffset.UtcNow
        };
        if (resumeSourceJob != null)
        {
            HydrateResumeJob(job, resumeSourceJob);
        }

        _jobs[job.Id] = job;
        _activeJobIds.TryAdd(job.Id, 0);
        SaveJob(job);
        TrySaveLastJobId(job.Id);
        AppendActivityLog(job.Id, $"autotag started: {normalizedPath}");
        if (resumeSeed != null)
        {
            AppendLog(
                job,
                $"resume checkpoint loaded from job {resumeSeed.SourceJobId}: stage={resumeSeed.Checkpoint.StageName}, platformIndex={resumeSeed.Checkpoint.PlatformIndex}, fileIndex={resumeSeed.Checkpoint.FileIndex}");
        }

        InitializeRunArchive(job);
        _ = PrepareRuntimeConfigAndRunJobAsync(job, normalizedPath, configJson, options);

        return job;
    }

    private async Task PrepareRuntimeConfigAndRunJobAsync(
        AutoTagJob job,
        string normalizedPath,
        string configJson,
        StartJobOptions options)
    {
        try
        {
            AppendLog(job, "runtime config preparing");
            var runtimeConfigJson = SanitizeConfigJson(configJson);
            runtimeConfigJson = await InjectPlatformDefaultsAsync(runtimeConfigJson);
            runtimeConfigJson = await InjectPlatformAuthAsync(runtimeConfigJson);
            runtimeConfigJson = InjectRunTrigger(runtimeConfigJson, job.Trigger);
            runtimeConfigJson = InjectProfileRuntimeSettings(
                runtimeConfigJson,
                options.TechnicalOverride,
                options.FolderStructureOverride,
                job.ProfileId,
                job.ProfileName);
            var persistedConfigJson = RedactSensitiveConfigJson(runtimeConfigJson);
            var runtimeConfigPath = WriteRuntimeConfigFile(job.Id, "base", runtimeConfigJson);
            TrySaveLastConfig(persistedConfigJson);
            AppendLog(job, "runtime config ready");

            await RunJobAsync(job, normalizedPath, runtimeConfigPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "AutoTag runtime config preparation failed for job {JobId}.", job.Id);
            job.Status = AutoTagLiterals.FailedStatus;
            job.Error = $"Runtime config preparation failed: {ex.Message}";
            job.ExitCode = 1;
            job.FinishedAt = DateTimeOffset.UtcNow;
            AppendLog(job, job.Error);
            SaveJob(job);
            AppendActivityLog(job.Id, "autotag failed: runtime config preparation");
            NotifyCompleted(job);
            _activeJobStages.TryRemove(job.Id, out _);
            _activeJobIds.TryRemove(job.Id, out _);
            Volatile.Write(ref _latestTerminalJob, CreateCompactTerminalJob(job));
            _jobs.TryRemove(job.Id, out _);
            _lastActivityLines.TryRemove(job.Id, out _);
            _archiveLocks.TryRemove(job.Id, out _);
            _lastRunIndexUpdateUtc.TryRemove(job.Id, out _);
        }
    }

    private AutoTagJob? TryCreateBlockedJobForTriggerPolicy(
        string normalizedPath,
        string normalizedTrigger,
        string normalizedRunIntent,
        string? profileId,
        string? profileName)
    {
        if (!IsEnhancementRunIntent(normalizedRunIntent)
            || IsAllowedEnhancementTrigger(normalizedTrigger))
        {
            return null;
        }

        var blockedJob = CreateBlockedJob(
            $"Enhancement run blocked: trigger '{normalizedTrigger}' is not allowed. Enhancement runs must be started manually or by schedule.",
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            profileId,
            profileName);
        AppendActivityLog(blockedJob.Id, "autotag blocked: invalid enhancement trigger");
        _logger.LogWarning(
            "AutoTag enhancement run blocked by trigger policy. intent={Intent}, trigger={Trigger}, path={Path}",
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedRunIntent),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedTrigger),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedPath));
        return blockedJob;
    }

    private async Task<bool> ShouldSkipForActiveDownloadsAsync()
    {
        return await _queueRepository.HasActiveDownloadsAsync();
    }

    private AutoTagJob? TryCreateBlockedJobForActiveJobPolicy(
        string normalizedPath,
        string normalizedTrigger,
        string normalizedRunIntent,
        string? profileId,
        string? profileName)
    {
        var activeJobId = _activeJobIds.Keys.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(activeJobId))
        {
            return null;
        }

        var blockedJob = CreateBlockedJob(
            $"AutoTag run blocked: another AutoTag job is already running ({activeJobId}).",
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            profileId,
            profileName);
        AppendActivityLog(blockedJob.Id, "autotag blocked: another job is already running");
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "AutoTag run blocked because another job is already running. activeJobId={ActiveJobId}, intent={Intent}, trigger={Trigger}, path={Path}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(activeJobId),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedRunIntent),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedTrigger),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedPath));
        }
        return blockedJob;
    }

    private async Task<AutoTagJob?> TryCreateBlockedJobForScopePolicyAsync(
        string normalizedPath,
        string normalizedTrigger,
        string normalizedRunIntent,
        string? profileId,
        string? profileName)
    {
        var runIntentScopeError = await ValidateRunIntentScopeAsync(normalizedPath, normalizedRunIntent, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(runIntentScopeError))
        {
            return null;
        }

        var blockedJob = CreateBlockedJob(
            runIntentScopeError,
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            profileId,
            profileName);
        AppendActivityLog(blockedJob.Id, $"autotag blocked: {runIntentScopeError}");
        _logger.LogWarning(
            "AutoTag blocked by scope policy. intent={Intent}, trigger={Trigger}, path={Path}, reason={Reason}",
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedRunIntent),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedTrigger),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(normalizedPath),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(runIntentScopeError));
        return blockedJob;
    }

    private static void HydrateResumeJob(AutoTagJob target, AutoTagJob source)
    {
        target.OkCount = source.OkCount;
        target.ErrorCount = source.ErrorCount;
        target.ReviewCount = source.ReviewCount;
        target.SkippedCount = source.SkippedCount;
        target.Progress = source.Progress;
        target.EnhancementFeature ??= source.EnhancementFeature;
        target.EnhancementGroupId ??= source.EnhancementGroupId;
        target.CurrentPhase = source.CurrentPhase;
        target.CurrentBatch = source.CurrentBatch;
        target.BatchCount = source.BatchCount;
        target.BatchProcessed = source.BatchProcessed;
        target.BatchSize = source.BatchSize;
        target.ProcessedItems = source.ProcessedItems;
        target.TotalItems = source.TotalItems;
        target.TargetReason ??= source.TargetReason;
        target.TargetRequested = source.TargetRequested > 0 ? source.TargetRequested : target.TargetRequested;
        target.TargetUsable = source.TargetUsable > 0 ? source.TargetUsable : target.TargetUsable;
        target.EnhancementManifestPath ??= source.EnhancementManifestPath;
        target.ExitCode = null;
        target.Error = null;
        target.LastActivityAt = source.LastActivityAt > DateTimeOffset.MinValue
            ? source.LastActivityAt
            : source.StartedAt;
        if (source.Logs.Count > 0)
        {
            target.Logs.AddRange(source.Logs);
        }

        if (source.StatusHistory.Count > 0)
        {
            target.StatusHistory.AddRange(source.StatusHistory);
        }

        if (source.EnhancedFilePaths.Count > 0)
        {
            target.EnhancedFilePaths.AddRange(source.EnhancedFilePaths);
        }

        if (source.StartedPlatforms.Count > 0)
        {
            target.StartedPlatforms.AddRange(source.StartedPlatforms);
        }

        foreach (var (diffPath, diffValue) in source.TagDiffs)
        {
            target.TagDiffs[diffPath] = diffValue;
        }
    }

    private AutoTagJob CreateBlockedJob(
        string error,
        string rootPath,
        string trigger,
        string runIntent,
        string? profileId,
        string? profileName)
    {
        var blockedJob = new AutoTagJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = "blocked",
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            Error = error,
            RootPath = rootPath,
            Trigger = trigger,
            RunIntent = runIntent,
            ProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim(),
            ProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName.Trim()
        };

        SaveJob(blockedJob);
        TrySaveLastJobId(blockedJob.Id);
        Volatile.Write(ref _latestTerminalJob, CreateCompactTerminalJob(blockedJob));
        return blockedJob;
    }

    private AutoTagJob CreateSkippedJob(
        string message,
        string rootPath,
        string trigger,
        string runIntent,
        string? profileId,
        string? profileName)
    {
        var skippedJob = new AutoTagJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = AutoTagLiterals.SkippedStatus,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            Error = message,
            RootPath = rootPath,
            Trigger = trigger,
            RunIntent = runIntent,
            ProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim(),
            ProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName.Trim()
        };

        SaveJob(skippedJob);
        TrySaveLastJobId(skippedJob.Id);
        Volatile.Write(ref _latestTerminalJob, CreateCompactTerminalJob(skippedJob));
        AppendActivityLog(skippedJob.Id, $"autotag skipped: {message}");
        return skippedJob;
    }

    private async Task<string?> ValidateRunIntentScopeAsync(
        string normalizedPath,
        string runIntent,
        CancellationToken cancellationToken)
    {
        if (string.Equals(runIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            if (!ConfiguredDownloadRootResolver.TryResolve(
                    _settingsService,
                    "download location",
                    "download location is not configured.",
                    out var downloadRoot,
                    out var error))
            {
                return $"Download enrichment run blocked: {error}";
            }

            if (!IsPathUnderRoot(normalizedPath, downloadRoot))
            {
                return $"Download enrichment run blocked: path '{normalizedPath}' is outside configured download location '{downloadRoot}'.";
            }

            return null;
        }

        if (!IsEnhancementRunIntent(runIntent))
        {
            return null;
        }

        var libraryRoots = await ResolveAllowedLibraryRootsAsync(cancellationToken);
        if (libraryRoots.Count == 0)
        {
            return "Enhancement run blocked: no accessible library folders are configured.";
        }

        if (!libraryRoots.Any(root => IsPathUnderRoot(normalizedPath, root)))
        {
            return $"Enhancement run blocked: path '{normalizedPath}' is outside configured library roots.";
        }

        if (ConfiguredDownloadRootResolver.TryResolve(
                _settingsService,
                "download location",
                "download location is not configured.",
                out var configuredDownloadRoot,
                out _)
            && IsPathUnderRoot(normalizedPath, configuredDownloadRoot))
        {
            return $"Enhancement run blocked: path '{normalizedPath}' is inside download location '{configuredDownloadRoot}'.";
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> ResolveAllowedLibraryRootsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var folders = _libraryRepository.IsConfigured
                ? await _libraryRepository.GetFoldersAsync(cancellationToken)
                : await _activityLog.GetFoldersAsync();
            return LibraryFolderRootResolver.ResolveAccessibleRoots(folders);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LibraryFolderRootResolver.ResolveAccessibleRoots(await _activityLog.GetFoldersAsync());
        }
    }

    private ResumeCheckpointSeed? TryResolveResumeCheckpointSeed(
        string normalizedPath,
        string normalizedRunIntent,
        string? profileId)
    {
        try
        {
            if (!Directory.Exists(_jobsDir))
            {
                return null;
            }

            var normalizedProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim();
            var latestMatchingJob = FindLatestResumeScopeJob(
                normalizedPath,
                normalizedRunIntent,
                normalizedProfileId);
            if (!IsEligibleResumeCandidate(latestMatchingJob, normalizedPath, normalizedRunIntent, normalizedProfileId))
            {
                return null;
            }

            return BuildResumeCheckpointSeed(latestMatchingJob!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed resolving AutoTag resume checkpoint seed.");
            return null;
        }
    }

    private AutoTagJob? FindLatestResumeScopeJob(
        string normalizedPath,
        string normalizedRunIntent,
        string? normalizedProfileId)
    {
        return Directory.EnumerateFiles(_jobsDir, AutoTagLiterals.JsonFileSearchPattern)
            .Select(TryLoadResumeScopeJob)
            .Where(job => job is not null
                && IsResumeScopeMatch(job, normalizedPath, normalizedRunIntent, normalizedProfileId))
            .Select(job => job!)
            .Aggregate<AutoTagJob, AutoTagJob?>(
                null,
                static (latestMatchingJob, job) => latestMatchingJob == null || job.StartedAt >= latestMatchingJob.StartedAt
                    ? job
                    : latestMatchingJob);
    }

    private AutoTagJob? TryLoadResumeScopeJob(string path)
    {
        var jobId = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        return _jobs.TryGetValue(jobId, out var cachedJob) ? cachedJob : LoadJob(jobId);
    }

    private bool IsEligibleResumeCandidate(
        AutoTagJob? latestMatchingJob,
        string normalizedPath,
        string normalizedRunIntent,
        string? normalizedProfileId)
    {
        return latestMatchingJob != null
            && IsResumeCandidate(latestMatchingJob, normalizedPath, normalizedRunIntent, normalizedProfileId);
    }

    private ResumeCheckpointSeed? BuildResumeCheckpointSeed(AutoTagJob job)
    {
        var checkpoint = CloneResumeCheckpoint(job.ResumeCheckpoint);
        if (checkpoint == null)
        {
            return null;
        }

        var rootJob = ResolveResumeRootJob(job);
        return new ResumeCheckpointSeed(job.Id, rootJob.Id, rootJob.StartedAt, checkpoint);
    }

    private AutoTagJob ResolveResumeRootJob(AutoTagJob job)
    {
        var current = job;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { job.Id };
        for (var depth = 0; depth < 20; depth += 1)
        {
            if (string.IsNullOrWhiteSpace(current.ResumeFromJobId) || !seen.Add(current.ResumeFromJobId))
            {
                return current;
            }

            var parent = GetJob(current.ResumeFromJobId) ?? LoadJob(current.ResumeFromJobId);
            if (parent == null)
            {
                return current;
            }

            current = parent;
        }

        return current;
    }

    private static bool IsResumeScopeMatch(
        AutoTagJob job,
        string normalizedPath,
        string normalizedRunIntent,
        string? normalizedProfileId)
    {
        if (!string.Equals(NormalizeRunIntent(job.RunIntent), normalizedRunIntent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(NormalizePathForJob(job.RootPath ?? string.Empty), normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(normalizedProfileId)
            && !string.Equals(job.ProfileId?.Trim(), normalizedProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private bool IsResumeCandidate(
        AutoTagJob job,
        string normalizedPath,
        string normalizedRunIntent,
        string? normalizedProfileId)
    {
        if (!IsResumeScopeMatch(job, normalizedPath, normalizedRunIntent, normalizedProfileId))
        {
            return false;
        }

        if (job.ResumeCheckpoint == null)
        {
            return false;
        }

        var status = job.Status?.Trim();
        var staleRunning = string.Equals(status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase)
            && !_activeJobIds.ContainsKey(job.Id);
        if (!string.Equals(status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase)
            && !staleRunning)
        {
            return false;
        }

        return true;
    }

    private static AutoTagResumeCheckpoint? CloneResumeCheckpoint(AutoTagResumeCheckpoint? checkpoint)
    {
        if (checkpoint == null)
        {
            return null;
        }

        return new AutoTagResumeCheckpoint
        {
            StageName = checkpoint.StageName,
            StageConfigHash = checkpoint.StageConfigHash,
            PlatformIndex = checkpoint.PlatformIndex,
            FileIndex = checkpoint.FileIndex,
            PlatformCount = checkpoint.PlatformCount,
            FileCount = checkpoint.FileCount,
            LastPath = checkpoint.LastPath,
            UpdatedAt = checkpoint.UpdatedAt
        };
    }

    private static string NormalizePathForJob(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return path.Trim();
        }
    }

    private static bool HasEligibleInputFiles(string rootPath, string configJson)
    {
        var normalizedRoot = NormalizeRootPath(rootPath);
        if (normalizedRoot == null)
        {
            return false;
        }

        if (!TryParseAutoTagConfig(configJson, out var root))
        {
            // Avoid suppressing valid runs due to config parse issues.
            return true;
        }

        var includeSubfolders = ReadBool(root, AutoTagLiterals.IncludeSubfoldersKey) ?? true;
        var targetFiles = ReadStringList(root, AutoTagLiterals.TargetFilesKey);
        if (targetFiles.Count > 0)
        {
            return HasEligibleTargetFiles(targetFiles, normalizedRoot);
        }

        return HasEligibleFilesInDirectory(normalizedRoot, includeSubfolders);
    }

    private static string? NormalizeRootPath(string rootPath)
    {
        var normalizedRoot = NormalizePathForJob(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalizedRoot) || !Directory.Exists(normalizedRoot))
        {
            return null;
        }

        return normalizedRoot;
    }

    private static bool TryParseAutoTagConfig(string configJson, out JsonObject root)
    {
        root = new JsonObject();
        try
        {
            root = JsonNode.Parse(configJson) as JsonObject ?? new JsonObject();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static bool HasEligibleTargetFiles(IEnumerable<string> targetFiles, string normalizedRoot)
    {
        foreach (var rawPath in targetFiles)
        {
            var candidate = NormalizePathForJob(rawPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsPathWithinScope(candidate, normalizedRoot)
                || !File.Exists(candidate))
            {
                continue;
            }

            var extension = Path.GetExtension(candidate);
            if (!string.IsNullOrWhiteSpace(extension) && EligibleAudioExtensions.Contains(extension))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEligibleFilesInDirectory(string normalizedRoot, bool includeSubfolders)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        return Directory.EnumerateFiles(normalizedRoot, "*", options)
            .Select(Path.GetExtension)
            .Any(extension => !string.IsNullOrWhiteSpace(extension) && EligibleAudioExtensions.Contains(extension));
    }

    private static bool IsPathWithinScope(string candidatePath, string scopePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(scopePath))
        {
            return false;
        }

        if (string.Equals(candidatePath, scopePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var scopeWithSeparator = scopePath.EndsWith(Path.DirectorySeparatorChar)
                                 || scopePath.EndsWith(Path.AltDirectorySeparatorChar)
            ? scopePath
            : scopePath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(scopeWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    public AutoTagJob? GetJob(string id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            return job;
        }

        var loaded = LoadJob(id);
        if (loaded != null)
        {
            NormalizeLoadedJobState(loaded);
            if (IsActiveJobStatus(loaded.Status))
            {
                _jobs[id] = loaded;
            }
        }

        return loaded;
    }

    public AutoTagJob? GetLatestJob()
    {
        var jobId = TryGetLastJobId();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        if (_jobs.TryGetValue(jobId, out var activeJob))
        {
            return activeJob;
        }

        var terminalJob = Volatile.Read(ref _latestTerminalJob);
        if (string.Equals(terminalJob?.Id, jobId, StringComparison.OrdinalIgnoreCase))
        {
            return terminalJob;
        }

        var loaded = LoadJob(jobId);
        if (loaded == null)
        {
            return null;
        }

        NormalizeLoadedJobState(loaded);
        if (IsActiveJobStatus(loaded.Status))
        {
            _jobs[jobId] = loaded;
            return loaded;
        }

        terminalJob = CreateCompactTerminalJob(loaded);
        Volatile.Write(ref _latestTerminalJob, terminalJob);
        return terminalJob;
    }

    public IReadOnlyList<AutoTagRunDaySummary> GetArchivedRunCalendar(int year, int month)
    {
        var summaries = GetArchivedRunSummaries()
            .Where(summary => GetRunDate(GetRunHistoryTimestamp(summary)).Year == year
                && GetRunDate(GetRunHistoryTimestamp(summary)).Month == month)
            .OrderBy(summary => summary.StartedAt)
            .ToList();

        return summaries
            .GroupBy(summary => GetRunDateToken(GetRunHistoryTimestamp(summary)))
            .Select(group => new AutoTagRunDaySummary
            {
                Date = group.Key,
                RunCount = group.Count(),
                Runs = group.OrderByDescending(run => run.StartedAt).ToList()
            })
            .OrderBy(day => day.Date, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<AutoTagRunSummary> GetArchivedRunsByDate(DateOnly date)
    {
        var token = date.ToString("yyyy-MM-dd");
        return GetArchivedRunSummaries()
            .Where(summary => string.Equals(GetRunDateToken(GetRunHistoryTimestamp(summary)), token, StringComparison.Ordinal))
            .OrderByDescending(summary => summary.StartedAt)
            .ToList();
    }

    internal static DateTimeOffset GetRunHistoryTimestamp(AutoTagRunSummary summary)
    {
        return summary.HistoryDate ?? summary.StartedAt;
    }

    internal static DateOnly GetRunDate(DateTimeOffset timestamp)
    {
        var localTimestamp = TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.Local);
        return DateOnly.FromDateTime(localTimestamp.DateTime);
    }

    internal static string GetRunDateToken(DateTimeOffset timestamp)
    {
        return GetRunDate(timestamp).ToString("yyyy-MM-dd");
    }

    public AutoTagRunArchive? GetArchivedRun(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var archiveLock = _archiveLocks.GetOrAdd(id, static _ => new object());
        lock (archiveLock)
        {
            var summary = LoadRunSummary(id);
            if (summary == null)
            {
                return null;
            }
            if (IsExpiredArchivedRun(summary, DateTimeOffset.UtcNow.Subtract(ResolveArchivedRunRetentionPeriod())))
            {
                DeleteArchivedRunFiles(summary.Id);
                PruneExpiredArchivedRuns(force: true);
                return null;
            }

            var logs = ReadRunLogLines(id);
            var statusHistory = ReadRunStatusHistory(id);
            var job = (logs.Count == 0 || statusHistory.Count == 0)
                ? GetJob(id) ?? LoadJob(id)
                : null;
            if (logs.Count == 0 && summary.LogCount > 0 && job?.Logs.Count > 0)
            {
                logs = job.Logs
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
                if (logs.Count > 0)
                {
                    _ = TryRepairArchivedLogsFromJob(id, GetRunLogPath(id));
                }
            }
            if (statusHistory.Count == 0 && summary.StatusEntryCount > 0 && job?.StatusHistory.Count > 0)
            {
                statusHistory = job.StatusHistory.ToList();
                if (statusHistory.Count > 0)
                {
                    _ = TryRepairArchivedStatusFromJob(id, GetRunStatusHistoryPath(id));
                }
            }

            return new AutoTagRunArchive
            {
                Summary = summary,
                Logs = logs,
                StatusHistory = statusHistory
            };
        }
    }

    public AutoTagTagDiff? GetTagDiff(string jobId, string path, string? platform = null)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = NormalizeDiffPath(path);
        var job = GetJob(jobId) ?? LoadJob(jobId);
        if (job != null)
        {
            var diffFromJob = TryResolveTagDiff(job.TagDiffs, normalized, path, platform);
            if (diffFromJob != null)
            {
                return diffFromJob;
            }
        }

        var diffFromArchive = TryResolveTagDiff(ReadRunTagDiffs(jobId), normalized, path, platform);
        if (diffFromArchive != null)
        {
            return diffFromArchive;
        }

        if (job == null)
        {
            return null;
        }

        // Fallback for older jobs: if no diff snapshots were persisted, capture a current snapshot
        // so the UI can still display tag data for troubleshooting.
        try
        {
            var current = BuildTagSnapshot(normalized);
            var fallback = new AutoTagTagDiff
            {
                Path = normalized,
                LastPlatform = null,
                Before = null,
                After = current
            };
            lock (job.TagDiffs)
            {
                job.TagDiffs[normalized] = fallback;
            }
            SaveJob(job);
            return SelectRequestedPlatformDiff(fallback, platform);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AutoTag diff fallback snapshot failed for Path");
        }

        return null;
    }

    private static AutoTagTagDiff? TryResolveTagDiff(
        Dictionary<string, AutoTagTagDiff>? tagDiffs,
        string normalizedPath,
        string rawPath,
        string? platform)
    {
        if (tagDiffs == null || tagDiffs.Count == 0)
        {
            return null;
        }

        if (tagDiffs.TryGetValue(normalizedPath, out var normalized))
        {
            return SelectRequestedPlatformDiff(normalized, platform);
        }

        if (tagDiffs.TryGetValue(rawPath, out var raw))
        {
            return SelectRequestedPlatformDiff(raw, platform);
        }

        return null;
    }

    private static AutoTagTagDiff SelectRequestedPlatformDiff(AutoTagTagDiff stored, string? requestedPlatform)
    {
        if (string.IsNullOrWhiteSpace(requestedPlatform))
        {
            return CloneDiff(stored);
        }

        var completed = (stored.PlatformDiffs ?? new List<AutoTagPlatformDiffSnapshot>())
            .Where(step => step.After != null)
            .ToList();
        if (completed.Count == 0)
        {
            return CloneDiff(stored);
        }

        var targetIndex = completed.FindLastIndex(step =>
            string.Equals(step.Platform, requestedPlatform, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
        {
            return CloneDiff(stored);
        }

        var target = completed[targetIndex];
        var isFinal = targetIndex == completed.Count - 1;
        var baseSnapshot = stored.Before ?? completed[0].Before ?? target.Before;
        var cumulativeSteps = completed
            .Take(targetIndex + 1)
            .Select(ClonePlatformDiff)
            .ToList();

        var selected = new AutoTagTagDiff
        {
            Path = stored.Path,
            LastPlatform = target.Platform,
            TargetPlatform = target.Platform,
            IsFinalPlatformDiff = isFinal,
            BasePlatform = "original",
            Before = baseSnapshot,
            After = target.After,
            PlatformDiffs = cumulativeSteps
        };

        if (selected.After != null)
        {
            selected.RetainedSources = ComputeRetainedSources(
                selected.Before,
                selected.After,
                selected.PlatformDiffs);
        }

        return selected;
    }

    private static AutoTagTagDiff CloneDiff(AutoTagTagDiff source)
    {
        return new AutoTagTagDiff
        {
            Path = source.Path,
            LastPlatform = source.LastPlatform,
            BasePlatform = source.BasePlatform,
            TargetPlatform = source.TargetPlatform,
            IsFinalPlatformDiff = source.IsFinalPlatformDiff,
            Before = source.Before,
            After = source.After,
            RetainedSources = source.RetainedSources != null
                ? new Dictionary<string, string>(source.RetainedSources, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            PlatformDiffs = (source.PlatformDiffs ?? new List<AutoTagPlatformDiffSnapshot>())
                .Select(ClonePlatformDiff)
                .ToList()
        };
    }

    private static AutoTagPlatformDiffSnapshot ClonePlatformDiff(AutoTagPlatformDiffSnapshot source)
    {
        return new AutoTagPlatformDiffSnapshot
        {
            Platform = source.Platform,
            Status = source.Status,
            CapturedAt = source.CapturedAt,
            Before = source.Before,
            After = source.After
        };
    }

    private static Dictionary<string, string> ComputeRetainedSources(
        AutoTagTagSnapshot? baseline,
        AutoTagTagSnapshot finalSnapshot,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed)
    {
        var retained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRetainedMetaSources(retained, baseline, finalSnapshot, completed);
        AddRetainedTagSources(retained, baseline, finalSnapshot, completed);
        return retained;
    }

    private static void AddRetainedMetaSources(
        Dictionary<string, string> retained,
        AutoTagTagSnapshot? baseline,
        AutoTagTagSnapshot finalSnapshot,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed)
    {
        foreach (var metaKey in DiffMetaKeys)
        {
            var source = ResolveValueSource(
                GetMetaFieldValue(finalSnapshot, metaKey),
                baseline is null ? null : GetMetaFieldValue(baseline, metaKey),
                completed,
                step => GetMetaFieldValue(step.After, metaKey),
                step => GetMetaFieldValue(step.Before, metaKey));
            if (!string.IsNullOrWhiteSpace(source))
            {
                retained[metaKey] = source;
            }
        }
    }

    private static void AddRetainedTagSources(
        Dictionary<string, string> retained,
        AutoTagTagSnapshot? baseline,
        AutoTagTagSnapshot finalSnapshot,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed)
    {
        var finalTags = finalSnapshot.Tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in finalTags.Keys)
        {
            finalTags.TryGetValue(key, out var finalTagValue);
            var baselineTagValue = baseline?.Tags != null && baseline.Tags.TryGetValue(key, out var value)
                ? value
                : null;
            var source = ResolveValueSource(
                finalTagValue,
                baselineTagValue,
                completed,
                step => step.After != null && step.After.Tags.TryGetValue(key, out var stepValue) ? stepValue : null,
                step => step.Before != null && step.Before.Tags.TryGetValue(key, out var stepValue) ? stepValue : null);
            if (!string.IsNullOrWhiteSpace(source))
            {
                retained[$"tag:{key.ToLowerInvariant()}"] = source;
            }
        }
    }

    private static string? ResolveValueSource<T>(
        T? finalValue,
        T? baselineValue,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector,
        Func<AutoTagPlatformDiffSnapshot, T?> beforeSelector)
    {
        var mergedSources = ResolveMergedValueSources(
            finalValue,
            baselineValue,
            completed,
            afterSelector,
            beforeSelector);
        if (!string.IsNullOrWhiteSpace(mergedSources))
        {
            return mergedSources;
        }

        var normalizedFinal = NormalizeCompareValue(finalValue);
        if (string.IsNullOrEmpty(normalizedFinal))
        {
            return null;
        }

        var normalizedBaseline = NormalizeCompareValue(baselineValue);
        var (currentValue, currentSource) = ResolveCurrentValueAndSource(completed, normalizedBaseline, afterSelector);

        if (string.Equals(currentValue, normalizedFinal, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(currentSource))
            {
                return currentSource;
            }

            return string.Equals(normalizedBaseline, normalizedFinal, StringComparison.Ordinal)
                ? "original"
                : null;
        }

        return ResolveFallbackTransitionSource(
            normalizedFinal,
            normalizedBaseline,
            completed,
            afterSelector,
            beforeSelector);
    }

    private static (string CurrentValue, string? CurrentSource) ResolveCurrentValueAndSource<T>(
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        string normalizedBaseline,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector)
    {
        var currentValue = normalizedBaseline;
        string? currentSource = null;

        foreach (var step in completed)
        {
            var stepAfter = NormalizeCompareValue(afterSelector(step));
            if (string.Equals(stepAfter, currentValue, StringComparison.Ordinal))
            {
                continue;
            }

            currentValue = stepAfter;
            if (!string.IsNullOrWhiteSpace(step.Platform))
            {
                currentSource = step.Platform;
            }
        }

        return (currentValue, currentSource);
    }

    private static string? ResolveFallbackTransitionSource<T>(
        string normalizedFinal,
        string normalizedBaseline,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector,
        Func<AutoTagPlatformDiffSnapshot, T?> beforeSelector)
    {
        foreach (var step in completed)
        {
            var after = NormalizeCompareValue(afterSelector(step));
            if (!string.Equals(after, normalizedFinal, StringComparison.Ordinal))
            {
                continue;
            }

            var before = NormalizeCompareValue(beforeSelector(step));
            var effectiveBefore = string.IsNullOrEmpty(before) ? normalizedBaseline : before;
            if (!string.Equals(effectiveBefore, normalizedFinal, StringComparison.Ordinal))
            {
                return step.Platform;
            }
        }

        return null;
    }

    private static string? ResolveMergedValueSources<T>(
        T? finalValue,
        T? baselineValue,
        IReadOnlyList<AutoTagPlatformDiffSnapshot> completed,
        Func<AutoTagPlatformDiffSnapshot, T?> afterSelector,
        Func<AutoTagPlatformDiffSnapshot, T?> beforeSelector)
    {
        var finalParts = NormalizeCompareParts(finalValue);
        if (finalParts.Count <= 1)
        {
            return null;
        }

        var baselineParts = NormalizeCompareParts(baselineValue);
        var sources = new List<string>();
        foreach (var step in completed)
        {
            if (string.IsNullOrWhiteSpace(step.Platform))
            {
                continue;
            }

            var beforeParts = NormalizeCompareParts(beforeSelector(step));
            if (beforeParts.Count == 0)
            {
                beforeParts = baselineParts;
            }

            var afterParts = NormalizeCompareParts(afterSelector(step));
            if (afterParts.Count == 0)
            {
                continue;
            }

            var stepChanged = !string.Equals(
                NormalizeCompareValue(beforeSelector(step)),
                NormalizeCompareValue(afterSelector(step)),
                StringComparison.Ordinal);
            var retainedContribution = afterParts.Intersect(finalParts, StringComparer.Ordinal).Any();
            var introducedContribution = afterParts
                .Except(beforeParts, StringComparer.Ordinal)
                .Intersect(finalParts, StringComparer.Ordinal)
                .Any();
            var changedToFinalValue = string.Equals(
                NormalizeCompareValue(afterSelector(step)),
                NormalizeCompareValue(finalValue),
                StringComparison.Ordinal)
                && !string.Equals(
                    NormalizeCompareValue(beforeSelector(step)),
                    NormalizeCompareValue(finalValue),
                    StringComparison.Ordinal);

            if ((introducedContribution || changedToFinalValue || retainedContribution)
                && stepChanged
                && !sources.Contains(step.Platform, StringComparer.OrdinalIgnoreCase))
            {
                sources.Add(step.Platform);
            }
        }

        return sources.Count > 1 ? string.Join(", ", sources) : null;
    }

    private static object? GetMetaFieldValue(AutoTagTagSnapshot? snapshot, string key)
    {
        if (snapshot?.Meta == null || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var property = typeof(QuickTagDumpMeta).GetProperties()
            .FirstOrDefault(prop => string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase));
        return property?.GetValue(snapshot.Meta);
    }

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

    public string? TryGetLastJobId()
    {
        try
        {
            if (!File.Exists(_lastJobPath))
            {
                return null;
            }

            var json = File.ReadAllText(_lastJobPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var node = JsonNode.Parse(json);
            return node?["jobId"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load last AutoTag job id.");
            return null;
        }
    }

    public string? TryGetLastConfigJson()
    {
        try
        {
            if (!File.Exists(_lastConfigPath))
            {
                return null;
            }

            var json = File.ReadAllText(_lastConfigPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return RedactSensitiveConfigJson(SanitizeConfigJson(json));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load last AutoTag config.");
            return null;
        }
    }

    public async Task<bool> StopJobAsync(string id, string? stopReason = null)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            var loaded = LoadJob(id);
            if (loaded == null)
            {
                return false;
            }
            NormalizeLoadedJobState(loaded);
            if (!IsActiveJobStatus(loaded.Status))
            {
                return false;
            }
            job = loaded;
            _jobs[id] = job;
        }

        var normalizedStopReason = NormalizeStopReason(stopReason);
        var stopStatus = ResolveStopStatus(job, normalizedStopReason);
        var previousStatus = job.Status;
        var previousError = job.Error;
        job.Status = stopStatus;
        job.Error = BuildStopError(job, normalizedStopReason);
        SaveJob(job);

        var stopped = await _autoTagRunner.StopAsync(id, CancellationToken.None);
        if (_jobCancellationSources.TryGetValue(id, out var cancellation))
        {
            await cancellation.CancelAsync();
            stopped = true;
        }

        if (stopped)
        {
            AppendActivityLog(
                job.Id,
                BuildStopActivityLog(stopStatus, normalizedStopReason));
            NotifyRunStopped(job, stopStatus, normalizedStopReason);
            return true;
        }

        if (string.Equals(job.Status, stopStatus, StringComparison.OrdinalIgnoreCase))
        {
            job.Status = previousStatus;
            job.Error = previousError;
            SaveJob(job);
        }

        return false;
    }

    private void NotifyRunStopped(AutoTagJob job, string stopStatus, string stopReason)
    {
        if (!string.Equals(stopStatus, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stopStatus, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _notifications.Raise(
            "run_paused",
            $"{(IsEnhancementRunIntent(job.RunIntent) ? "Enhancement" : "AutoTag")} run {stopStatus}",
            job.Error ?? BuildStopError(job, stopReason),
            "Warning",
            $"run_paused:{job.Id}",
            "job",
            job.Id);
    }

    private static string ResolveStopStatus(AutoTagJob job, string stopReason)
    {
        if (!IsEnhancementRunIntent(job.RunIntent)
            && !IsManualEnrichmentRunIntent(job.RunIntent))
        {
            return AutoTagLiterals.CanceledStatus;
        }

        return string.Equals(stopReason, AutoTagLiterals.AutomationTrigger, StringComparison.OrdinalIgnoreCase)
            ? AutoTagLiterals.PausedStatus
            : string.Equals(stopReason, "user", StringComparison.OrdinalIgnoreCase)
                ? AutoTagLiterals.CanceledStatus
                : AutoTagLiterals.InterruptedStatus;
    }

    private static string NormalizeStopReason(string? stopReason)
    {
        if (string.IsNullOrWhiteSpace(stopReason))
        {
            return "user";
        }

        return stopReason.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.AutomationTrigger => AutoTagLiterals.AutomationTrigger,
            AutoTagLiterals.ScheduleTrigger => AutoTagLiterals.ScheduleTrigger,
            AutoTagLiterals.RecoveryTrigger => AutoTagLiterals.RecoveryTrigger,
            _ => "user"
        };
    }

    private static string BuildStopError(AutoTagJob job, string stopReason)
    {
        if (!IsEnhancementRunIntent(job.RunIntent)
            && !IsManualEnrichmentRunIntent(job.RunIntent))
        {
            return stopReason switch
            {
                AutoTagLiterals.AutomationTrigger => "Stopped by automation.",
                AutoTagLiterals.ScheduleTrigger => "Stopped after schedule change.",
                AutoTagLiterals.RecoveryTrigger => "Stopped by stale recovery.",
                _ => "Stopped by user."
            };
        }

        return stopReason switch
        {
            AutoTagLiterals.AutomationTrigger => "Paused by automation. Resume is available after download finalization.",
            AutoTagLiterals.ScheduleTrigger => "Interrupted after schedule change. Resume is available.",
            AutoTagLiterals.RecoveryTrigger => "Interrupted by stale recovery. Resume is available.",
            _ => "Stopped by user."
        };
    }

    private static string BuildStopActivityLog(string stopStatus, string stopReason)
    {
        var actor = stopReason switch
        {
            AutoTagLiterals.AutomationTrigger => "automation",
            AutoTagLiterals.ScheduleTrigger => "schedule change",
            AutoTagLiterals.RecoveryTrigger => "stale recovery",
            _ => "user"
        };

        if (string.Equals(stopStatus, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return $"autotag paused by {actor}";
        }

        return string.Equals(stopStatus, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            ? $"autotag interrupted by {actor}"
            : $"autotag canceled by {actor}";
    }

    private async Task RunJobAsync(
        AutoTagJob job,
        string path,
        string configPath)
    {
        var fileOutcomes = new Dictionary<string, FileTagOutcome>(StringComparer.OrdinalIgnoreCase);
        var runtimeConfigPaths = InitializeRuntimeConfigPaths(configPath);
        using var jobCancellation = new CancellationTokenSource();
        _jobCancellationSources[job.Id] = jobCancellation;

        try
        {
            await RunJobCoreAsync(job, path, configPath, fileOutcomes, runtimeConfigPaths, jobCancellation.Token);
            NotifyCompleted(job);
        }
        catch (OperationCanceledException)
        {
            HandleRunJobCanceled(job);
            NotifyCompleted(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await HandleRunJobFailureAsync(job, ex, path, configPath, fileOutcomes);
        }
        finally
        {
            if (!ShouldPreserveRuntimeConfigFilesForResume(job))
            {
                CleanupRuntimeConfigFiles(runtimeConfigPaths);
            }
            _activeJobStages.TryRemove(job.Id, out _);
            _activeJobIds.TryRemove(job.Id, out _);
            _jobCancellationSources.TryRemove(job.Id, out _);
            if (!IsActiveJobStatus(job.Status))
            {
                SaveArchivedTagDiffs(job.Id, job.TagDiffs);
                Volatile.Write(ref _latestTerminalJob, CreateCompactTerminalJob(job));
            }
            _jobs.TryRemove(job.Id, out _);
            _lastActivityLines.TryRemove(job.Id, out _);
            _archiveLocks.TryRemove(job.Id, out _);
            _lastRunIndexUpdateUtc.TryRemove(job.Id, out _);
        }
    }

    private static bool IsActiveJobStatus(string? status)
        => string.Equals(status, AutoTagLiterals.QueuedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.TaggingStatus, StringComparison.OrdinalIgnoreCase);

    private static AutoTagJob CreateCompactTerminalJob(AutoTagJob source)
    {
        var compact = new AutoTagJob
        {
            Id = source.Id,
            Status = source.Status,
            StartedAt = source.StartedAt,
            FinishedAt = source.FinishedAt,
            ExitCode = source.ExitCode,
            Error = source.Error,
            Progress = source.Progress,
            OkCount = source.OkCount,
            ErrorCount = source.ErrorCount,
            ReviewCount = source.ReviewCount,
            SkippedCount = source.SkippedCount,
            RootPath = source.RootPath,
            Trigger = source.Trigger,
            RunIntent = source.RunIntent,
            ProfileId = source.ProfileId,
            ProfileName = source.ProfileName,
            EnhancementFeature = source.EnhancementFeature,
            EnhancementGroupId = source.EnhancementGroupId,
            CurrentPhase = source.CurrentPhase,
            CurrentBatch = source.CurrentBatch,
            BatchCount = source.BatchCount,
            BatchProcessed = source.BatchProcessed,
            BatchSize = source.BatchSize,
            ProcessedItems = source.ProcessedItems,
            TotalItems = source.TotalItems,
            TargetReason = source.TargetReason,
            TargetRequested = source.TargetRequested,
            TargetUsable = source.TargetUsable,
            EnhancementManifestPath = source.EnhancementManifestPath,
            AutoMoveSummary = source.AutoMoveSummary,
            CurrentPlatform = source.CurrentPlatform,
            LastStatus = source.LastStatus,
            ResumeCheckpoint = source.ResumeCheckpoint,
            ResumeFromJobId = source.ResumeFromJobId,
            LastActivityAt = source.LastActivityAt
        };
        compact.Logs.AddRange(source.Logs);
        compact.StatusHistory.AddRange(source.StatusHistory);
        return compact;
    }

    private static AutoTagJob CreateJobPersistenceSnapshot(AutoTagJob source)
    {
        var snapshot = CreateCompactTerminalJob(source);
        snapshot.EnhancementWorkflows.AddRange(source.EnhancementWorkflows);
        snapshot.EnhancedFilePaths.AddRange(source.EnhancedFilePaths);
        snapshot.StartedPlatforms.AddRange(source.StartedPlatforms);
        return snapshot;
    }

    private bool HasOtherActiveJobs(string jobId)
    {
        return _activeJobIds.Keys.Any(activeJobId => !string.Equals(activeJobId, jobId, StringComparison.Ordinal));
    }

    private async Task RunJobCoreAsync(
        AutoTagJob job,
        string path,
        string configPath,
        Dictionary<string, FileTagOutcome> fileOutcomes,
        HashSet<string> runtimeConfigPaths,
        CancellationToken cancellationToken)
    {
        await PrepareEnhancementRunAsync(job, configPath, cancellationToken);
        var stages = await BuildStageConfigsAsync(job, configPath);
        var includesEnrichmentStage = stages.Any(stage =>
            string.Equals(stage.Name, AutoTagLiterals.EnrichmentStage, StringComparison.OrdinalIgnoreCase));
        var includesEnhancementStage = stages.Any(stage =>
            string.Equals(stage.Name, AutoTagLiterals.EnhancementStage, StringComparison.OrdinalIgnoreCase));
        var includesEnhancementWorkflows = ShouldRunIntegratedEnhancementWorkflows(job, configPath);
        RegisterStageRuntimeConfigPaths(runtimeConfigPaths, stages);
        if (TryMarkNoStagesConfigured(job, stages, includesEnhancementWorkflows))
        {
            return;
        }
        EnsureInitialEnrichmentResumeCheckpoint(job, stages);

        var execution = await ExecuteStagesAsync(job, stages, path, configPath, fileOutcomes, cancellationToken);
        if (!execution.Success || IsTerminalStopStatus(job.Status))
        {
            FinalizeStageExecution(job, execution.Success);
            return;
        }

        await RunSuccessPostProcessingAsync(
            job,
            path,
            new SuccessPostProcessingContext
            {
                ConfigPath = configPath,
                IncludesEnrichmentStage = includesEnrichmentStage,
                IncludesEnhancementStage = includesEnhancementStage,
                IncludesEnhancementWorkflows = includesEnhancementWorkflows,
                FileOutcomes = fileOutcomes
            },
            cancellationToken);
        FinalizeStageExecution(job, success: true);
    }

    private void FinalizeStageExecution(AutoTagJob job, bool success)
    {
        if (!IsTerminalStopStatus(job.Status))
        {
            job.Status = success ? AutoTagLiterals.CompletedStatus : AutoTagLiterals.FailedStatus;
        }
        if (string.Equals(job.Status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase))
        {
            job.Progress = 1d;
            job.CurrentPhase = "completed";
            job.ResumeCheckpoint = null;
            job.ResumeFromJobId = null;
        }
        job.ExitCode = success ? 0 : 1;
        job.FinishedAt = DateTimeOffset.UtcNow;
        AppendPlatformSummary(job);
        SaveJob(job);
        AppendActivityLog(job.Id, $"autotag finished: status={job.Status}");
    }

    private void HandleRunJobCanceled(AutoTagJob job)
    {
        if (IsTerminalStopStatus(job.Status))
        {
            return;
        }

        job.Status = IsEnhancementRunIntent(job.RunIntent) || IsManualEnrichmentRunIntent(job.RunIntent)
            ? AutoTagLiterals.InterruptedStatus
            : AutoTagLiterals.CanceledStatus;
        job.Error = IsEnhancementRunIntent(job.RunIntent) || IsManualEnrichmentRunIntent(job.RunIntent)
            ? "Interrupted. Resume is available."
            : "Stopped.";
        job.ExitCode = 1;
        job.FinishedAt = DateTimeOffset.UtcNow;
        SaveJob(job);
    }

    private static bool IsTerminalStopStatus(string? status)
    {
        return string.Equals(status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldPreserveRuntimeConfigFilesForResume(AutoTagJob job)
    {
        if (job.ResumeCheckpoint == null)
        {
            return false;
        }

        return string.Equals(job.Status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> InitializeRuntimeConfigPaths(string configPath)
    {
        var runtimeConfigPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            runtimeConfigPaths.Add(configPath);
        }

        return runtimeConfigPaths;
    }

    private static void RegisterStageRuntimeConfigPaths(HashSet<string> runtimeConfigPaths, IReadOnlyList<AutoTagStageConfig> stages)
    {
        foreach (var stageConfigPath in stages
                     .Select(static stage => stage.ConfigPath)
                     .Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            runtimeConfigPaths.Add(stageConfigPath!);
        }
    }

    private bool TryMarkNoStagesConfigured(
        AutoTagJob job,
        IReadOnlyCollection<AutoTagStageConfig> stages,
        bool includesEnhancementWorkflows)
    {
        if (stages.Count > 0)
        {
            return false;
        }

        if (includesEnhancementWorkflows)
        {
            AppendLog(job, "gap-fill tagging skipped: no runnable gap-fill tagging stage was configured");
            return false;
        }

        if (string.Equals(job.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            job.Status = AutoTagLiterals.SkippedStatus;
            job.Error = "No runnable download enrichment stage was configured.";
            job.ExitCode = 0;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.ResumeCheckpoint = null;
            job.ResumeFromJobId = null;
            SaveJob(job);
            AppendActivityLog(job.Id, "autotag skipped: no runnable download enrichment stage configured");
            return true;
        }

        job.Status = AutoTagLiterals.FailedStatus;
        job.Error = "No AutoTag stages configured.";
        job.ExitCode = 1;
        job.FinishedAt = DateTimeOffset.UtcNow;
        job.ResumeCheckpoint = null;
        job.ResumeFromJobId = null;
        SaveJob(job);
        AppendActivityLog(job.Id, "autotag failed: no stages configured");
        return true;
    }

    private readonly record struct StageRunResult(bool Success);

    private async Task<StageRunResult> ExecuteStagesAsync(
        AutoTagJob job,
        IReadOnlyList<AutoTagStageConfig> stages,
        string path,
        string configPath,
        Dictionary<string, FileTagOutcome> fileOutcomes,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var stageResult = await ExecuteSingleStageAsync(job, stage, index, stages.Count, path, fileOutcomes, cancellationToken);
            if (!stageResult.Success)
            {
                return new StageRunResult(false);
            }
        }

        return new StageRunResult(true);
    }

    private readonly record struct StageExecutionResult(bool Success);

    private async Task<StageExecutionResult> ExecuteSingleStageAsync(
        AutoTagJob job,
        AutoTagStageConfig stage,
        int stageIndex,
        int totalStages,
        string path,
        Dictionary<string, FileTagOutcome> fileOutcomes,
        CancellationToken cancellationToken)
    {
        AppendLog(job, BuildStageStartedLog(stage, stageIndex, totalStages));
        _activeJobStages[job.Id] = stage.Name;
        var resumeCursor = ResolveResumeCursor(job, stage);
        if (resumeCursor != null)
        {
            AppendLog(
                job,
                $"resume checkpoint active for stage '{stage.Name}': platformIndex={resumeCursor.PlatformIndex}, fileIndex={resumeCursor.FileIndex}");
        }
        else if (job.ResumeCheckpoint != null && !CanApplyResumeCheckpoint(job.ResumeCheckpoint, stage))
        {
            job.ResumeCheckpoint = null;
            SaveJob(job);
        }
        EnsureInitialEnrichmentResumeCheckpoint(job, stage);

        try
        {
            var result = await _autoTagRunner.RunAsync(
                job.Id,
                path,
                stage.ConfigPath,
                status => UpdateStatus(job, status, stage.Name, stage.ConfigHash, stageIndex, totalStages, fileOutcomes),
                line => AppendLog(job, line),
                IsEnhancementRunIntent(job.RunIntent)
                    ? (files, token) => ApplyCompletedGapFillBatchAsync(job, stage.ConfigPath, files, token)
                    : null,
                resumeCursor,
                cancellationToken);
            if (string.Equals(result.Error, "stopped", StringComparison.OrdinalIgnoreCase))
            {
                return HandleStoppedStage(job);
            }

            if (!result.Success)
            {
                if (TryHandlePausedStage(job, result.Error))
                {
                    return new StageExecutionResult(false);
                }

                job.Status = AutoTagLiterals.FailedStatus;
                job.Error = result.Error;
                return new StageExecutionResult(false);
            }

            AppendLog(job, BuildStageFinishedLog(stage, stageIndex, totalStages));
            if (CanApplyResumeCheckpoint(job.ResumeCheckpoint, stage))
            {
                job.ResumeCheckpoint = null;
                SaveJob(job);
            }

            return new StageExecutionResult(true);
        }
        finally
        {
            _activeJobStages.TryRemove(job.Id, out _);
        }
    }

    private bool TryHandlePausedStage(AutoTagJob job, string? error)
    {
        const string PausedPrefix = "paused:";
        if (string.IsNullOrWhiteSpace(error)
            || !error.TrimStart().StartsWith(PausedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var message = error.TrimStart()[PausedPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "AutoTag paused.";
        }

        job.Status = AutoTagLiterals.PausedStatus;
        job.Error = message;
        AppendLog(job, $"autotag paused: {message}");
        NotifyDownloadToast(message, "warning");
        return true;
    }

    private static StageExecutionResult HandleStoppedStage(AutoTagJob job)
    {
        if (IsTerminalStopStatus(job.Status))
        {
            return new StageExecutionResult(false);
        }

        job.Status = AutoTagLiterals.CanceledStatus;
        job.Error = "Stopped by user.";
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
        var autoMove = await RunFinalAutoMoveAsync(job, path, context.ConfigPath, context.FileOutcomes, cancellationToken);
        if (IsManualEnrichmentRunIntent(job.RunIntent) && !autoMove.Completed)
        {
            throw new InvalidOperationException(
                autoMove.Summary.Error ?? "Manual enrichment finalization did not move every fully enriched file.");
        }
        await RunIntegratedEnhancementWorkflowsAsync(
            job,
            path,
            context.ConfigPath,
            context.IncludesEnhancementWorkflows,
            cancellationToken);
        var isManualEnrichment = IsManualEnrichmentRunIntent(job.RunIntent);
        var hasEnhancementWork = context.IncludesEnhancementStage
            || context.IncludesEnhancementWorkflows
            || isManualEnrichment;
        if (autoMove.Completed && !isManualEnrichment)
        {
            await TriggerPlexScanAfterMoveAsync(job, cancellationToken);
        }
        await IngestKnownFilesAfterAutoMoveAsync(
            job,
            autoMove.Summary,
            cancellationToken);
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

    private async Task<AutoMoveExecutionResult> RunFinalAutoMoveAsync(
        AutoTagJob job,
        string path,
        string configPath,
        Dictionary<string, FileTagOutcome> fileOutcomes,
        CancellationToken cancellationToken)
    {
        if (!IsManualEnrichmentRunIntent(job.RunIntent)
            && ConfiguredDownloadRootResolver.TryResolve(
                _settingsService,
                "download location",
                "download location is not configured.",
                out var configuredDownloadRoot,
                out _)
            && IsPathUnderRoot(path, configuredDownloadRoot))
        {
            AppendLog(job, "auto-move skipped: download-root finalization is owned by download orchestration");
            var summary = new AutoTagMoveSummary
            {
                Error = "auto-move skipped for download-root run."
            };
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(false, summary);
        }

        if (IsEnhancementRunIntent(job.RunIntent))
        {
            AppendLog(job, "auto-move skipped: enhancement run uses configured enhancement workflows only");
            var summary = new AutoTagMoveSummary
            {
                Error = "auto-move skipped for enhancement run."
            };
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(false, summary);
        }

        if (string.Equals(job.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            AppendLog(job, "auto-move skipped: download enrichment finalization is owned by download orchestration");
            var summary = new AutoTagMoveSummary
            {
                Error = "auto-move skipped for download enrichment run."
            };
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(false, summary);
        }

        var (taggedFiles, failedFiles) = BuildMoveFileSets(fileOutcomes);
        if (IsManualEnrichmentRunIntent(job.RunIntent))
        {
            failedFiles = Array.Empty<string>();
        }
        AppendLog(job, "tagging completed, auto-move starting");
        var result = await MoveAfterAutoTagAsync(job, path, configPath, taggedFiles, failedFiles, cancellationToken);
        if (!IsManualEnrichmentRunIntent(job.RunIntent) || !result.Completed)
        {
            return result;
        }

        var remainingTaggedFiles = taggedFiles
            .Where(file => !string.IsNullOrWhiteSpace(file) && File.Exists(file))
            .ToList();
        if (remainingTaggedFiles.Count == 0)
        {
            return result;
        }

        result.Summary.Error = $"Manual enrichment finalization left {remainingTaggedFiles.Count} enriched file(s) in staging.";
        ApplyAutoMoveSummary(job, result.Summary);
        AppendLog(job, result.Summary.Error);
        return new AutoMoveExecutionResult(false, result.Summary);
    }

    private async Task HandleRunJobFailureAsync(
        AutoTagJob job,
        Exception ex,
        string path,
        string configPath,
        Dictionary<string, FileTagOutcome> fileOutcomes)
    {
        _logger.LogError(ex, "AutoTag job {JobId} failed", job.Id);
        job.Status = AutoTagLiterals.FailedStatus;
        job.Error = ex.Message;
        job.FinishedAt = DateTimeOffset.UtcNow;
        AppendPlatformSummary(job);
        SaveJob(job);
        AppendActivityLog(job.Id, $"autotag failed: {job.Error ?? "unknown error"}");

        if (IsManualEnrichmentRunIntent(job.RunIntent) && job.AutoMoveSummary != null)
        {
            NotifyCompleted(job);
            return;
        }

        AppendLog(job, "tagging failed, evaluating post-failure auto-move");
        var autoMove = await RunFinalAutoMoveAsync(job, path, configPath, fileOutcomes, CancellationToken.None);
        if (autoMove.Completed)
        {
            await TriggerPlexScanAfterMoveAsync(job, CancellationToken.None);
            await IngestKnownFilesAfterAutoMoveAsync(
                job,
                autoMove.Summary,
                CancellationToken.None);
        }

        NotifyCompleted(job);
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Stage planning intentionally evaluates enrichment and enhancement branches explicitly for deterministic run semantics.")]
    private async Task<List<AutoTagStageConfig>> BuildStageConfigsAsync(AutoTagJob job, string configPath)
    {
        var root = LoadConfigRoot(configPath);
        if (root == null)
        {
            return new List<AutoTagStageConfig>();
        }

        var platformCaps = await LoadPlatformCapabilitiesAsync();
        var eligiblePlatforms = await ResolveEligiblePlatformsAsync(root, platformCaps, job);
        var stages = new List<AutoTagStageConfig>();
        var runIntent = NormalizeRunIntent(job.RunIntent);

        var shouldRunEnrichment = ShouldRunEnrichmentForIntent(runIntent);
        var enrichmentSkipReason = string.Empty;
        if (shouldRunEnrichment
            && TryBuildEnrichmentStages(
                root,
                platformCaps,
                eligiblePlatforms,
                new EnrichmentBuildContext(runIntent, job.Id),
                out var enrichmentStages,
                out enrichmentSkipReason,
                out var enrichmentStrippedKeys))
        {
            stages.AddRange(enrichmentStages);
            AppendStageSchemaLog(job, AutoTagLiterals.EnrichmentStage, enrichmentStrippedKeys);
        }
        else
        {
            var reason = shouldRunEnrichment
                ? enrichmentSkipReason
                : $"disabled for run intent '{runIntent}'";
            AppendLog(job, $"enrichment skipped: {reason}");
        }

        var shouldRunEnhancement = ShouldRunEnhancementForIntent(runIntent);
        var enhancementSkipReason = "gap-fill tags not configured";
        if (shouldRunEnhancement
            && TryBuildEnhancementStage(
                root,
                platformCaps,
                eligiblePlatforms,
                new EnhancementBuildContext(runIntent, job.Id),
                out var enhancementStage,
                out enhancementSkipReason,
                out var enhancementStrippedKeys))
        {
            stages.Add(enhancementStage);
            AppendStageSchemaLog(job, AutoTagLiterals.EnhancementStage, enhancementStrippedKeys);
        }
        else
        {
            if (shouldRunEnhancement && HasConfiguredEnhancementWorkflows(root))
            {
                AppendLog(job, $"gap-fill tagging skipped: {enhancementSkipReason}");
            }
            else
            {
                var reason = shouldRunEnhancement
                    ? enhancementSkipReason
                    : $"disabled for run intent '{runIntent}'";
                AppendLog(job, $"enhancement skipped: {reason}");
            }
        }

        return stages;
    }

    private async Task IngestKnownFilesAfterAutoMoveAsync(
        AutoTagJob job,
        AutoTagMoveSummary autoMoveSummary,
        CancellationToken cancellationToken)
    {
        if (autoMoveSummary.MovedCount <= 0)
        {
            return;
        }

        if (await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
        {
            AppendLog(job, "post auto-move direct library ingestion skipped (downloads active).");
            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Post auto-move direct library ingestion skipped because downloads became active."));
            return;
        }

        var changedFolderIds = await ResolveChangedLibraryFolderIdsAsync(autoMoveSummary, cancellationToken);
        if (changedFolderIds.Count == 0)
        {
            AppendLog(job, "post auto-move direct library ingestion skipped (no moved library folders).");
            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Post auto-move direct library ingestion skipped because no changed library folders were detected."));
            return;
        }

        if (autoMoveSummary.ChangedFilePaths.Count > 0)
        {
            var changedFilesByFolder = await ResolveChangedLibraryFilesByFolderAsync(
                autoMoveSummary,
                changedFolderIds,
                cancellationToken);
            if (changedFilesByFolder.Count == 0)
            {
                changedFilesByFolder = changedFolderIds.ToDictionary(
                    folderId => folderId,
                    _ => autoMoveSummary.ChangedFilePaths.ToList());
            }

            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Post auto-move direct library ingestion starting for {autoMoveSummary.ChangedFilePaths.Count} file(s) in folder(s): {string.Join(", ", changedFolderIds)}."));
            AppendLog(job, $"post auto-move direct library ingestion starting for {autoMoveSummary.ChangedFilePaths.Count} file(s) in folder(s): {string.Join(", ", changedFolderIds)}");
            var ingestion = await _knownFileIngestionService.IngestAndVerifyAsync(
                changedFilesByFolder,
                cancellationToken);
            if (!ingestion.IsComplete)
            {
                _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "error",
                    $"Post auto-move direct library ingestion incomplete; {ingestion.MissingFilePaths.Count} moved audio file(s) are missing from the library DB."));
                AppendLog(job, $"post auto-move direct library ingestion incomplete; {ingestion.MissingFilePaths.Count} moved audio file(s) missing from DB");
            }
            return;
        }

        _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Post auto-move direct library ingestion skipped because no changed file paths were reported (folders={string.Join(", ", changedFolderIds)}, moved={autoMoveSummary.MovedCount}, skipped={autoMoveSummary.SkippedCount}, failed={autoMoveSummary.FailedCount})."));
        AppendLog(job, $"post auto-move direct library ingestion skipped (no changed file paths; folders={string.Join(", ", changedFolderIds)}, moved={autoMoveSummary.MovedCount}, skipped={autoMoveSummary.SkippedCount}, failed={autoMoveSummary.FailedCount})");
    }

    private async Task<List<long>> ResolveChangedLibraryFolderIdsAsync(
        AutoTagMoveSummary autoMoveSummary,
        CancellationToken cancellationToken)
    {
        var changed = autoMoveSummary.ChangedFolderIds
            .Where(folderId => folderId > 0)
            .ToHashSet();
        if (autoMoveSummary.MovedCount <= 0 || autoMoveSummary.DestinationRoots.Count == 0 || !_libraryRepository.IsConfigured)
        {
            return changed.OrderBy(folderId => folderId).ToList();
        }

        var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
        foreach (var destinationRoot in autoMoveSummary.DestinationRoots)
        {
            AddMatchingLibraryFolders(destinationRoot, folders, changed);
        }

        return changed.OrderBy(folderId => folderId).ToList();
    }

    private async Task<Dictionary<long, List<string>>> ResolveChangedLibraryFilesByFolderAsync(
        AutoTagMoveSummary autoMoveSummary,
        List<long> changedFolderIds,
        CancellationToken cancellationToken)
    {
        var grouped = new Dictionary<long, List<string>>();
        if (autoMoveSummary.ChangedFilePaths.Count == 0
            || changedFolderIds.Count == 0
            || !_libraryRepository.IsConfigured)
        {
            return grouped;
        }

        var changedFolderIdSet = changedFolderIds.ToHashSet();
        var folders = (await _libraryRepository.GetFoldersAsync(cancellationToken))
            .Where(folder => changedFolderIdSet.Contains(folder.Id))
            .ToList();

        foreach (var path in autoMoveSummary.ChangedFilePaths)
        {
            foreach (var folder in folders)
            {
                TryAddPathToFolderGroup(grouped, path, folder);
            }
        }

        return grouped;
    }

    private static void TryAddPathToFolderGroup(
        Dictionary<long, List<string>> grouped,
        string path,
        FolderDto folder)
    {
        try
        {
            if (!IsPathUnderRoot(path, folder.RootPath))
            {
                return;
            }

            if (!grouped.TryGetValue(folder.Id, out var paths))
            {
                paths = new List<string>();
                grouped[folder.Id] = paths;
            }

            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ignore paths the runtime cannot normalize; folder-level fallback remains available.
        }
    }

    private static void AddMatchingLibraryFolders(
        string destinationRoot,
        IReadOnlyCollection<FolderDto> folders,
        HashSet<long> changed)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            return;
        }

        foreach (var folder in folders.Where(static folder => folder.Id > 0 && !string.IsNullOrWhiteSpace(folder.RootPath)))
        {
            try
            {
                if (IsPathUnderRoot(destinationRoot, folder.RootPath))
                {
                    changed.Add(folder.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ignore paths the runtime cannot normalize; explicit changed folder ids remain authoritative.
            }
        }
    }

    private static List<long> ParseFolderIds(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is not JsonArray values)
        {
            return new List<long>();
        }

        var parsed = new List<long>();
        foreach (var item in values)
        {
            if (item is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var longValue) && longValue > 0)
            {
                parsed.Add(longValue);
                continue;
            }

            if (item is JsonValue stringValue
                && stringValue.TryGetValue<string>(out var raw)
                && long.TryParse(raw, out var parsedValue)
                && parsedValue > 0)
            {
                parsed.Add(parsedValue);
            }
        }

        return parsed
            .Distinct()
            .ToList();
    }

    private static bool IsMusicCapableFolder(FolderDto folder)
    {
        var normalized = (folder.DesiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        return !normalized.Contains("video", StringComparison.Ordinal)
            && !normalized.Contains("podcast", StringComparison.Ordinal);
    }

    private static bool PathsOverlap(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftPrefix = normalizedLeft + Path.DirectorySeparatorChar;
        var rightPrefix = normalizedRight + Path.DirectorySeparatorChar;
        return normalizedLeft.StartsWith(rightPrefix, StringComparison.OrdinalIgnoreCase)
            || normalizedRight.StartsWith(leftPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadBoundedInt(JsonObject node, string propertyName, int fallback, int min, int max)
    {
        if (!node.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return Math.Clamp(intValue, min, max);
        }

        if (value.TryGetValue<string>(out var raw) && int.TryParse(raw, out var parsed))
        {
            return Math.Clamp(parsed, min, max);
        }

        return fallback;
    }

    private static int? ReadOptionalInt(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is null)
        {
            return null;
        }

        if (valueNode is JsonValue intNode && intNode.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (valueNode is JsonValue stringNode
            && stringNode.TryGetValue<string>(out var raw)
            && int.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private JsonObject? LoadConfigRoot(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            var json = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag config could not be read.");
            return null;
        }
    }

    private static AutoTagOrganizerOptions BuildFolderUniformityOptions(
        JsonObject folderUniformity)
    {
        var options = new AutoTagOrganizerOptions
        {
            IncludeSubfolders = ReadBool(folderUniformity, AutoTagLiterals.IncludeSubfoldersKey) ?? true,
            MoveMisplacedFiles = ReadBool(folderUniformity, "moveMisplacedFiles") ?? true,
            MergeIntoExistingDestinationFolders = ReadBool(folderUniformity, "mergeIntoExistingDestinationFolders") != false,
            RenameFilesToTemplate = ReadBool(folderUniformity, "renameFilesToTemplate") != false,
            RemoveEmptyFolders = ReadBool(folderUniformity, "removeEmptyFolders") != false,
            MergeNoAudioArtistFolders = ReadBool(folderUniformity, "mergeNoAudioArtistFolders") != false,
            ReconcileOrphanArtistFolders = ReadBool(folderUniformity, "reconcileOrphanArtistFolders") != false,
            QuarantineNoAudioDirectories = ReadBool(folderUniformity, "quarantineNoAudioDirectories") == true,
            ResolveSameTrackQualityConflicts = ReadBool(folderUniformity, "resolveSameTrackQualityConflicts") != false,
            KeepBothOnUnresolvedConflicts = ReadBool(folderUniformity, "keepBothOnUnresolvedConflicts") != false,
            OnlyMoveWhenTagged = ReadBool(folderUniformity, "onlyMoveWhenTagged") == true,
            OnlyReorganizeAlbumsWithFullTrackSets = ReadBool(folderUniformity, "onlyReorganizeAlbumsWithFullTrackSets") == true,
            SkipCompilationFolders = ReadBool(folderUniformity, "skipCompilationFolders") == true,
            SkipVariousArtistsFolders = ReadBool(folderUniformity, "skipVariousArtistsFolders") == true,
            GenerateReconciliationReport = ReadBool(folderUniformity, "generateReconciliationReport") == true,
            UseShazamForUntaggedFiles = ReadBool(folderUniformity, "useShazamForUntaggedFiles") == true,
            DuplicateConflictPolicy = folderUniformity["duplicateConflictPolicy"]?.GetValue<string>() ?? AutoTagOrganizerOptions.DuplicateConflictKeepBest,
            DuplicatesFolderName = folderUniformity["duplicatesFolderName"]?.GetValue<string>() ?? DuplicateCleanerService.DuplicatesFolderName
        };

        return options;
    }

    private static List<string> ResolveEnhancementRequestedTags(JsonObject baseRoot)
        => AutoTagPlatformTagContract.ResolveRequestedTags(baseRoot);

    private bool TryBuildEnhancementStage(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        IReadOnlyList<string> eligiblePlatforms,
        EnhancementBuildContext context,
        out AutoTagStageConfig stage,
        out string skipReason,
        out List<string> strippedKeys)
    {
        stage = null!;
        skipReason = "gap-fill tags not configured";
        strippedKeys = new List<string>();

        if (!EnhancementWorkflowSelection.IsGapFillRunnable(baseRoot))
        {
            return false;
        }

        var requested = ResolveEnhancementRequestedTags(baseRoot);
        var platforms = eligiblePlatforms
            .Where(platform => !IsLyricsProviderPlatform(platform))
            .ToList();
        if (platforms.Count == 0)
        {
            skipReason = "no eligible enhancement platforms enabled";
            return false;
        }

        var filtered = FilterSupportedTags(requested, platforms, platformCaps);
        if (filtered.Count == 0)
        {
            skipReason = "no supported enhancement tags for enabled platforms";
            return false;
        }

        var stageRoot = CloneRoot(baseRoot);
        WriteStringList(stageRoot, AutoTagLiterals.PlatformsKey, platforms);
        stageRoot[AutoTagLiterals.MultiPlatformKey] = platforms.Count > 1;
        WriteStringList(stageRoot, "tags", filtered);
        if (string.Equals(context.RunIntent, AutoTagLiterals.RunIntentEnhancementRecentDownloads, StringComparison.OrdinalIgnoreCase))
        {
            var targetFiles = ReadStringList(baseRoot, AutoTagLiterals.TargetFilesKey);
            if (targetFiles.Count == 0)
            {
                skipReason = "no recent downloaded files were available for enhancement";
                return false;
            }

            WriteStringList(stageRoot, AutoTagLiterals.TargetFilesKey, targetFiles);
        }
        else if (string.Equals(context.RunIntent, AutoTagLiterals.RunIntentEnhancementOnly, StringComparison.OrdinalIgnoreCase))
        {
            stageRoot[AutoTagLiterals.LibraryWideEnhancementBatchSizeKey] = 40;
        }

        stageRoot["skipTagged"] = ReadBool(baseRoot, "enhancementSkipTagged")
            ?? ReadBool(baseRoot, "skipTagged")
            ?? false;
        var forceFingerprint = ReadBool(baseRoot, AutoTagLiterals.EnhancementForceFingerprintKey)
            ?? ReadBool(baseRoot, AutoTagLiterals.ManualForceFingerprintKey)
            ?? false;
        if (forceFingerprint)
        {
            stageRoot[AutoTagLiterals.EnhancementForceFingerprintKey] = true;
            if (platforms.Any(platform => string.Equals(platform, "shazam", StringComparison.OrdinalIgnoreCase)))
            {
                ConfigureShazamFingerprintBootstrap(stageRoot);
            }
        }

        if (ReadBool(baseRoot, AutoTagLiterals.EnhancementUntrustedTargetsKey) == true)
        {
            stageRoot[AutoTagLiterals.EnhancementUntrustedTargetsKey] = true;
        }

        strippedKeys = ApplyStageSchema(stageRoot, EnhancementStageAllowedKeys);

        var configJson = stageRoot.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        var configPath = WriteRuntimeConfigFile(context.JobId, AutoTagLiterals.EnhancementStage, configJson);
        stage = new AutoTagStageConfig(
            AutoTagLiterals.EnhancementStage,
            configPath,
            filtered.Count,
            ComputeConfigHash(configJson));
        return true;
    }

    private static JsonObject CloneRoot(JsonObject root)
    {
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        return (JsonNode.Parse(json) as JsonObject) ?? new JsonObject();
    }

    private static string ComputeConfigHash(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(configJson);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static List<string> ReadStringList(JsonObject root, string propertyName)
    {
        if (root[propertyName] is not JsonArray array)
        {
            return new List<string>();
        }

        return array
            .Select(item => item?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();
    }

    private static void WriteStringList(JsonObject root, string propertyName, IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            array.Add(value);
        }

        root[propertyName] = array;
    }

    private static HashSet<string> BuildStageAllowedKeys(
        bool includeSkipTagged,
        bool includeConflictResolution,
        bool includeTargetFiles,
        bool includeLibraryWideEnhancementBatchSize = false)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AutoTagLiterals.PlatformsKey,
            "path",
            "tags",
            AutoTagLiterals.OverwriteTagsKey,
            "separators",
            AutoTagLiterals.OverwriteKey,
            "mergeGenres",
            "camelot",
            "shortTitle",
            "strictness",
            "matchDuration",
            "maxDurationDifference",
            "matchById",
            "enableShazam",
            "forceShazam",
            AutoTagLiterals.IncludeSubfoldersKey,
            AutoTagLiterals.MultiPlatformKey,
            "parseFilename",
            "id3v24",
            "trackNumberLeadingZeroes",
            "stylesOptions",
            "multipleMatches",
            "titleRegex",
            AutoTagLiterals.CustomKey,
            "stylesCustomTag",
            "id3CommLang",
            "capitalizeGenres",
            AutoTagLiterals.DownloadTagSourceKey,
            TracknameTemplateKey,
            "saveArtwork",
            "saveAnimatedArtwork",
            "animatedArtworkFormats",
            "dlAlbumcoverForPlaylist",
            "saveArtworkArtist",
            "coverImageTemplate",
            "artistImageTemplate",
            "localArtworkFormat",
            "organizeSidecarsIntoTemplateFolders",
            "embedMaxQualityCover",
            "jpegImageQuality",
            "runTrigger",
            "technical",
            "folderStructure",
            "materializeToTemplatePath",
            "profileId",
            "profileName",
            "threads"
            // Playlist intake is intentionally disabled for AutoTag stage configs.
            // "isPlaylist"
        };

        if (includeSkipTagged)
        {
            keys.Add("skipTagged");
        }

        if (includeConflictResolution)
        {
            keys.Add("conflictResolution");
        }

        if (includeTargetFiles)
        {
            keys.Add(AutoTagLiterals.TargetFilesKey);
        }

        if (includeLibraryWideEnhancementBatchSize)
        {
            keys.Add(AutoTagLiterals.LibraryWideEnhancementBatchSizeKey);
        }

        return keys;
    }

    private static List<string> ApplyStageSchema(JsonObject root, HashSet<string> allowedKeys)
    {
        var stripped = root
            .Select(pair => pair.Key)
            .Where(key => !allowedKeys.Contains(key))
            .ToList();
        foreach (var key in stripped)
        {
            root.Remove(key);
        }
        stripped.Sort(StringComparer.OrdinalIgnoreCase);
        return stripped;
    }

    private void AppendStageSchemaLog(AutoTagJob job, string stageName, List<string> strippedKeys)
    {
        if (strippedKeys.Count == 0)
        {
            return;
        }

        AppendLog(job, $"{stageName} config: removed ignored keys ({string.Join(", ", strippedKeys)})");
    }

    private async Task<Dictionary<string, PlatformTagCapabilities>> LoadPlatformCapabilitiesAsync()
    {
        var result = new Dictionary<string, PlatformTagCapabilities>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = await _metadataService.GetPlatformsJsonAsync();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            if (JsonNode.Parse(json) is not JsonArray array)
            {
                return result;
            }

            foreach (var node in array.OfType<JsonObject>())
            {
                var id = GetPlatformId(node);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var supportedTags = ReadPlatformList(node, "supportedTags");
                var downloadTags = ReadPlatformList(node, AutoTagLiterals.DownloadTagsKey);
                var requiresAuth = ReadPlatformRequiresAuth(node);

                var normalizedSupported = supportedTags
                    .Select(NormalizeSupportedTagKey)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var tag in downloadTags
                             .Select(NormalizeSupportedTagKey)
                             .Where(tag => !string.IsNullOrWhiteSpace(tag))
                             .Select(tag => tag!))
                {
                    normalizedSupported.Add(tag);
                }

                result[id.Trim()] = new PlatformTagCapabilities(normalizedSupported, requiresAuth);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load AutoTag platform metadata.");
        }

        return result;
    }

    private static string? GetPlatformId(JsonObject node)
    {
        return node["id"]?.GetValue<string>()
            ?? node[AutoTagLiterals.PlatformKey]?["id"]?.GetValue<string>();
    }

    private static List<string> ReadPlatformList(JsonObject node, string key)
    {
        var values = ReadStringList(node, key);
        if (values.Count == 0 && node[AutoTagLiterals.PlatformKey] is JsonObject platformNode)
        {
            values = ReadStringList(platformNode, key);
        }

        return values;
    }

    private static bool ReadPlatformRequiresAuth(JsonObject node)
    {
        return ReadBool(node, "requiresAuth")
            ?? (node[AutoTagLiterals.PlatformKey] is JsonObject platformNode ? ReadBool(platformNode, "requiresAuth") : null)
            ?? false;
    }

    private static List<string> FilterSupportedTags(
        IEnumerable<string> requested,
        IEnumerable<string> platforms,
        Dictionary<string, PlatformTagCapabilities> platformCaps)
    {
        var supported = AutoTagPlatformTagContract.ToSupportedTagMap(
            platformCaps,
            static caps => caps.SupportedTags);
        return AutoTagPlatformTagContract.FilterOfferedTags(
            requested,
            platforms,
            supported,
            NormalizeSupportedTagKey);
    }

    private async Task<List<string>> ResolveEligiblePlatformsAsync(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        AutoTagJob job)
    {
        var configured = ReadStringList(baseRoot, AutoTagLiterals.PlatformsKey)
            .Where(platform => !string.IsNullOrWhiteSpace(platform))
            .Select(platform => platform.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (configured.Count == 0)
        {
            return new List<string>();
        }

        var candidates = configured;

        if (candidates.Count == 0)
        {
            return candidates;
        }

        PlatformAuthState? authState = null;
        try
        {
            authState = await _platformAuthService.LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load platform auth state while filtering AutoTag platforms.");
        }

        var removedUnauthenticated = new List<string>();
        var eligible = new List<string>();

        foreach (var platform in candidates)
        {
            if (!RequiresPlatformAuth(platform, platformCaps))
            {
                eligible.Add(platform);
                continue;
            }

            if (IsPlatformAuthenticated(platform, authState))
            {
                eligible.Add(platform);
                continue;
            }

            removedUnauthenticated.Add(platform);
        }

        if (removedUnauthenticated.Count > 0)
        {
            AppendLog(job, $"platform filter: excluded unauthenticated platforms ({string.Join(", ", removedUnauthenticated)})");
        }

        return eligible;
    }

    private static bool RequiresPlatformAuth(string platformId, Dictionary<string, PlatformTagCapabilities> platformCaps)
    {
        if (string.IsNullOrWhiteSpace(platformId))
        {
            return false;
        }

        if (platformCaps.TryGetValue(platformId.Trim(), out var caps))
        {
            return caps.RequiresAuth;
        }

        return platformId.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.SpotifySource => true,
            AutoTagLiterals.DiscogsPlatform => true,
            AutoTagLiterals.LastFmPlatform => true,
            AutoTagLiterals.BpmSupremePlatform => true,
            AutoTagLiterals.AppleMusicPlatform => true,
            AutoTagLiterals.PlexPlatform => true,
            AutoTagLiterals.JellyfinPlatform => true,
            _ => false
        };
    }

    private static bool IsPlatformAuthenticated(string platformId, PlatformAuthState? state)
    {
        var key = platformId.Trim().ToLowerInvariant();
        return key switch
        {
            AutoTagLiterals.SpotifySource => IsSpotifyAuthenticated(state?.Spotify),
            AutoTagLiterals.DiscogsPlatform => !string.IsNullOrWhiteSpace(state?.Discogs?.Token),
            AutoTagLiterals.LastFmPlatform => !string.IsNullOrWhiteSpace(state?.LastFm?.ApiKey),
            AutoTagLiterals.BpmSupremePlatform => HasBpmSupremeCredentials(state?.BpmSupreme),
            AutoTagLiterals.AppleMusicPlatform => state?.AppleMusic?.WrapperReady == true,
            AutoTagLiterals.ITunesPlatform => state?.AppleMusic?.WrapperReady == true,
            AutoTagLiterals.PlexPlatform => IsPlexAuthenticated(state?.Plex),
            AutoTagLiterals.JellyfinPlatform => IsJellyfinAuthenticated(state?.Jellyfin),
            _ => false
        };
    }

    private static bool IsSpotifyAuthenticated(SpotifyConfig? spotify)
    {
        if (spotify == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(spotify.ActiveAccount))
        {
            var active = spotify.Accounts.FirstOrDefault(account =>
                account.Name.Equals(spotify.ActiveAccount, StringComparison.OrdinalIgnoreCase));
            if (active != null && !string.IsNullOrWhiteSpace(active.BlobPath) && File.Exists(active.BlobPath))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(spotify.WebPlayerSpDc))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(spotify.ClientId) &&
            !string.IsNullOrWhiteSpace(spotify.ClientSecret);
    }

    private static bool HasBpmSupremeCredentials(BpmSupremeAuth? bpmSupreme)
    {
        var email = bpmSupreme?.Email;
        var password = bpmSupreme?.Password;
        return !string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password);
    }

    private static bool IsPlexAuthenticated(PlexAuth? plex)
    {
        return plex is not null &&
            !string.IsNullOrWhiteSpace(plex.Url) &&
            !string.IsNullOrWhiteSpace(plex.Token);
    }

    private static bool IsJellyfinAuthenticated(JellyfinAuth? jellyfin)
    {
        return jellyfin is not null &&
            !string.IsNullOrWhiteSpace(jellyfin.Url) &&
            (!string.IsNullOrWhiteSpace(jellyfin.ApiKey) ||
             !string.IsNullOrWhiteSpace(jellyfin.Username));
    }

    private static bool? ReadBool(JsonObject? node, string propertyName)
    {
        if (node == null)
        {
            return null;
        }

        if (!node.TryGetPropertyValue(propertyName, out var value) || value is not JsonValue jsonValue)
        {
            return null;
        }

        return jsonValue.TryGetValue<bool>(out var parsed) ? parsed : null;
    }

    private static string NormalizeDownloadTagSource(string? downloadTagSource)
    {
        return DownloadTagSourceHelper.NormalizeStoredSource(downloadTagSource, AutoTagLiterals.DeezerSource);
    }

    private static string? ResolveDownloadSourcePlatform(JsonObject root)
    {
        if (!root.TryGetPropertyValue(AutoTagLiterals.DownloadTagSourceKey, out var sourceNode) || sourceNode is null)
        {
            return null;
        }

        if (sourceNode is not JsonValue sourceValue || !sourceValue.TryGetValue<string>(out var rawSource))
        {
            return null;
        }

        return NormalizeDownloadTagSource(rawSource) switch
        {
            AutoTagLiterals.DeezerSource => AutoTagLiterals.DeezerSource,
            AutoTagLiterals.SpotifySource => AutoTagLiterals.SpotifySource,
            _ => null
        };
    }

    private static string? NormalizeSupportedTagKey(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var normalized = tag.Trim();
        return SupportedTagKeyMap.TryGetValue(normalized, out var mapped) ? mapped : null;
    }

    private void NotifyCompleted(AutoTagJob job)
    {
        _activeJobStages.TryRemove(job.Id, out _);
        _activeJobIds.TryRemove(job.Id, out _);
        _jobCancellationSources.TryRemove(job.Id, out _);

        try
        {
            JobCompleted?.Invoke(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "AutoTag job {JobId}: completion handler failed.", job.Id);
            }
        }
    }

    private static AutoTagOrganizerOptions LoadOrganizerOptions(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return new AutoTagOrganizerOptions();
            }

            var json = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AutoTagOrganizerOptions();
            }

            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null)
            {
                return new AutoTagOrganizerOptions();
            }

            var enhancementNode = node[AutoTagLiterals.EnhancementStage] as JsonObject;
            var folderUniformityNode = enhancementNode?["folderUniformity"] as JsonObject;
            var tagsNode = node["tags"] as JsonObject;
            var options = new AutoTagOrganizerOptions
            {
                OnlyMoveWhenTagged = ReadBool(folderUniformityNode, "onlyMoveWhenTagged") == true,
                MoveTaggedPath = node["moveSuccess"]?.GetValue<bool>() == true
                    ? node["moveSuccessPath"]?.GetValue<string>()
                    : null,
                MoveUntaggedPath = node["moveFailed"]?.GetValue<bool>() == true
                    ? node["moveFailedPath"]?.GetValue<string>()
                    : null,
                IncludeSubfolders = node[AutoTagLiterals.IncludeSubfoldersKey]?.GetValue<bool>() ?? true,
                MoveMisplacedFiles = ReadBool(folderUniformityNode, "moveMisplacedFiles") ?? true,
                RenameFilesToTemplate = ReadBool(folderUniformityNode, "renameFilesToTemplate") != false,
                RemoveEmptyFolders = ReadBool(folderUniformityNode, "removeEmptyFolders") != false,
                UsePrimaryArtistFoldersOverride =
                    tagsNode?["singleAlbumArtist"]?.GetValue<bool?>(),
                MultiArtistSeparatorOverride =
                    tagsNode?[AutoTagLiterals.MultiArtistSeparatorKey]?.GetValue<string>()
            };

            return options;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AutoTagOrganizerOptions();
        }
    }

    private async Task<AutoTagOrganizerOptions> LoadOrganizerOptionsAsync(AutoTagJob job, string configPath)
    {
        var options = LoadOrganizerOptions(configPath);
        await ApplyJobProfileOrganizerOverridesAsync(job, options, CancellationToken.None);
        return options;
    }

    private async Task ApplyJobProfileOrganizerOverridesAsync(
        AutoTagJob job,
        AutoTagOrganizerOptions options,
        CancellationToken cancellationToken)
    {
        var profile = await ResolveJobProfileAsync(job, cancellationToken);
        if (profile != null)
        {
            AutoTagOrganizerProfileOverlay.ApplyTaggingProfileOverrides(options, profile);
            return;
        }
        throw new InvalidOperationException("AutoTag organization requires a valid profile.");
    }

    private async Task<TaggingProfile?> ResolveJobProfileAsync(AutoTagJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.ProfileId) && string.IsNullOrWhiteSpace(job.ProfileName))
        {
            return null;
        }

        try
        {
            var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: false, cancellationToken);
            return AutoTagProfileResolutionService.ResolveProfileReference(state.Profiles, job.ProfileId)
                ?? AutoTagProfileResolutionService.ResolveProfileReference(state.Profiles, job.ProfileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve AutoTag profile for organizer overrides.");
            return null;
        }
    }

    private async Task<AutoMoveExecutionResult> MoveAfterAutoTagAsync(
        AutoTagJob job,
        string rootPath,
        string configPath,
        IReadOnlyCollection<string> taggedFiles,
        IReadOnlyCollection<string> failedFiles,
        CancellationToken cancellationToken)
    {
        if (_disableAutoMove)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("AutoTag job {JobId}: auto-move skipped (disabled).", job.Id);
            }
            AppendLog(job, "auto-move skipped: disabled");
            var disabledSummary = new AutoTagMoveSummary
            {
                Error = "auto-move disabled by configuration."
            };
            ApplyAutoMoveSummary(job, disabledSummary);
            return new AutoMoveExecutionResult(false, disabledSummary);
        }

        try
        {
            _logger.LogInformation("AutoTag job JobId: auto-move started for RootPath");
            AppendLog(job, "auto-move started");
            var organizerOptions = await LoadOrganizerOptionsAsync(job, configPath);
            if (IsManualEnrichmentRunIntent(job.RunIntent))
            {
                organizerOptions.BatchScopedFilesOnly = true;
                organizerOptions.MoveUntaggedPath = null;
                organizerOptions.OnlyMoveWhenTagged = true;
            }
            var summary = await _downloadMoveService.MoveForRootWithSummaryAsync(
                rootPath,
                organizerOptions,
                taggedFiles,
                failedFiles,
                cancellationToken);
            _logger.LogInformation("AutoTag job JobId: auto-move finished for RootPath");
            AppendLog(job, "auto-move finished");
            ApplyAutoMoveSummary(job, summary);
            return new AutoMoveExecutionResult(true, summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: auto-move failed.", job.Id);
            AppendLog(job, $"auto-move failed: {ex.Message}");
            var failedSummary = new AutoTagMoveSummary
            {
                FailedCount = 1,
                Error = ex.Message
            };
            ApplyAutoMoveSummary(job, failedSummary);
            return new AutoMoveExecutionResult(false, failedSummary);
        }
    }

    private void ApplyAutoMoveSummary(AutoTagJob job, AutoTagMoveSummary summary)
    {
        job.AutoMoveSummary = summary.Clone();
        var destinations = summary.DestinationRoots.Count > 0
            ? string.Join(", ", summary.DestinationRoots)
            : "<none>";
        const string label = "auto-move summary";
        AppendLog(
            job,
            $"{label}: moved={summary.MovedCount}, skipped={summary.SkippedCount}, failed={summary.FailedCount}, destinations=[{destinations}]");
        if (!string.IsNullOrWhiteSpace(summary.Error))
        {
            AppendLog(job, $"{label}: error={summary.Error}");
        }

        SaveJob(job);
    }

    private async Task<bool> TriggerPlexScanAfterMoveAsync(AutoTagJob job, CancellationToken cancellationToken)
    {
        var plex = await LoadConfiguredPlexForScanAsync(job);
        if (plex == null)
        {
            return false;
        }

        return await TriggerPlexScanAsync(job, plex, "after auto-move", cancellationToken);
    }

    private async Task TriggerConfiguredMediaServerRefreshAfterEnhancementAsync(
        AutoTagJob job,
        bool includesEnhancementStage,
        CancellationToken cancellationToken)
    {
        if (!includesEnhancementStage
            || (!ShouldRunEnhancementForIntent(job.RunIntent)
                && !IsManualEnrichmentRunIntent(job.RunIntent)))
        {
            return;
        }

        try
        {
            AppendLog(job, "media server metadata refresh starting after enhancement (request only; not waiting for a full library reindex).");
            using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            refreshTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            var refresh = await _mediaServerRefreshService.RefreshConfiguredServersAsync(
                refreshTimeout.Token,
                updateTrackIndex: false);
            AppendLog(
                job,
                $"media server metadata refresh requested after enhancement: configured={refresh.ConfiguredServerCount}, refreshed={refresh.RefreshedServerCount}, failed=[{string.Join(", ", refresh.FailedServers)}]");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppendLog(job, "media server metadata refresh after enhancement timed out; enhancement run will finish without waiting.");
        }
        catch (OperationCanceledException)
        {
            AppendLog(job, "media server metadata refresh after enhancement was canceled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: configured media server refresh after enhancement failed.", job.Id);
            AppendLog(job, $"media server metadata refresh after enhancement failed: {ex.Message}");
        }
    }

    private async Task<PlexAuth?> LoadConfiguredPlexForScanAsync(AutoTagJob job)
    {
        try
        {
            var authState = await _platformAuthService.LoadAsync();
            var plex = authState.Plex;
            if (!IsPlexAuthenticated(plex))
            {
                return null;
            }

            return plex;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: failed loading Plex auth state for scan.", job.Id);
            return null;
        }
    }

    private async Task<bool> TriggerPlexScanAsync(
        AutoTagJob job,
        PlexAuth plex,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var plexUrl = plex.Url;
            var plexToken = plex.Token;
            if (string.IsNullOrWhiteSpace(plexUrl) || string.IsNullOrWhiteSpace(plexToken))
            {
                return false;
            }

            AppendLog(job, $"plex scan starting {reason}");

            var sections = await _plexApiClient.GetLibrarySectionsAsync(plexUrl, plexToken, cancellationToken);
            var musicSections = sections
                .Where(section => string.Equals(section.Type, AutoTagLiterals.ArtistTag, StringComparison.OrdinalIgnoreCase))
                .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (musicSections.Count == 0)
            {
                AppendLog(job, "plex scan skipped: no music libraries found");
                return false;
            }

            var refreshed = 0;
            foreach (var section in musicSections)
            {
                refreshed += await _plexApiClient.RefreshLibraryAsync(plexUrl, plexToken, section.Key, cancellationToken) ? 1 : 0;
            }

            AppendLog(job, $"plex scan requested: {musicSections.Count} libraries (refreshed={refreshed})");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: Plex scan {Reason} failed.", job.Id, reason);
            AppendLog(job, $"plex scan failed: {ex.Message}");
            return false;
        }
    }

    private static bool ResolveDisableAutoMove()
    {
        var env = Environment.GetEnvironmentVariable("DEEZSPOTAG_DISABLE_AUTOMOVE");
        if (!string.IsNullOrWhiteSpace(env) && bool.TryParse(env, out var value))
        {
            return value;
        }
        return false;
    }

    private static string SanitizeConfigJson(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return configJson;
        }

        try
        {
            var node = JsonNode.Parse(configJson);
            if (node == null)
            {
                return configJson;
            }
            RemoveNulls(node);
            EnsureEffectivePlatforms(node);
            EnsureSupportedDownloadTagSource(node);
            EnsureOverwriteDefaults(node);
            EnsureTracknameTemplateCanonical(node);
            EnsureEnhancementFolderScopesCanonical(node);
            EnsureLegacyFolderUniformityStructureMirrorsRemoved(node);
            EnsureLegacyOrganizerConfigRemoved(node);
            EnsureLegacyDeezerAuthRemoved(node);
            EnsureSpotifySecret(node);
            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return configJson;
        }
    }

    private static void EnsureEffectivePlatforms(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        var platforms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root[AutoTagLiterals.PlatformsKey] is JsonArray platformArray)
        {
            foreach (var platformId in platformArray
                         .Select(static entry => entry?.GetValue<string>()?.Trim())
                         .Where(static id => !string.IsNullOrWhiteSpace(id))
                         .Cast<string>()
                         .Where(seen.Add))
            {
                platforms.Add(platformId);
            }
        }

        var normalized = new JsonArray();
        foreach (var platform in platforms)
        {
            normalized.Add(platform);
        }

        root[AutoTagLiterals.PlatformsKey] = normalized;
        root[AutoTagLiterals.MultiPlatformKey] = platforms.Count > 1;
        EnsureShazamFlagsFollowPlatforms(root, platforms);
    }

    private static void EnsureShazamFlagsFollowPlatforms(JsonObject root, IReadOnlyCollection<string> platforms)
    {
        var shazamEnabled = platforms.Any(platform => string.Equals(platform, "shazam", StringComparison.OrdinalIgnoreCase));
        root["enableShazam"] = shazamEnabled;
        if (!shazamEnabled)
        {
            root["forceShazam"] = false;
        }
    }

    private static void EnsureTracknameTemplateCanonical(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        root.Remove("filenameTemplate");
        root.Remove("albumTracknameTemplate");
        root.Remove("playlistTracknameTemplate");
    }

    private static void EnsureSupportedDownloadTagSource(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        if (!root.TryGetPropertyValue(AutoTagLiterals.DownloadTagSourceKey, out var sourceNode) || sourceNode is null)
        {
            return;
        }

        if (sourceNode is JsonValue sourceValue && sourceValue.TryGetValue<string>(out var rawSource))
        {
            root[AutoTagLiterals.DownloadTagSourceKey] = NormalizeDownloadTagSource(rawSource);
            return;
        }

        root[AutoTagLiterals.DownloadTagSourceKey] = AutoTagLiterals.DeezerSource;
    }

    private static void EnsureOverwriteDefaults(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        if (!root.TryGetPropertyValue(AutoTagLiterals.OverwriteKey, out var overwriteNode)
            || overwriteNode is not JsonValue overwriteValue
            || !overwriteValue.TryGetValue<bool>(out _))
        {
            root[AutoTagLiterals.OverwriteKey] = false;
        }

        if (!root.TryGetPropertyValue(AutoTagLiterals.OverwriteTagsKey, out var overwriteTagsNode) || overwriteTagsNode is not JsonArray overwriteArray)
        {
            root[AutoTagLiterals.OverwriteTagsKey] = new JsonArray();
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new JsonArray();
        foreach (var entry in overwriteArray)
        {
            if (entry is not JsonValue value || !value.TryGetValue<string>(out var tag) || string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var trimmed = tag.Trim();
            if (!seen.Add(trimmed))
            {
                continue;
            }

            normalized.Add(trimmed);
        }

        root[AutoTagLiterals.OverwriteTagsKey] = normalized;
    }

    private static void EnsureEnhancementFolderScopesCanonical(JsonNode node)
    {
        if (node is not JsonObject root
            || root[AutoTagLiterals.EnhancementStage] is not JsonObject enhancement)
        {
            return;
        }

        CanonicalizeEnhancementFolderScopeSection(enhancement, "folderUniformity");
        CanonicalizeEnhancementFolderScopeSection(enhancement, "coverMaintenance");
        CanonicalizeEnhancementFolderScopeSection(enhancement, "qualityChecks");
        EnhancementWorkflowSelection.CanonicalizeSidecars(enhancement);
        CanonicalizeEnhancementFolderScopeSection(enhancement, "sidecars");
    }

    private static void CanonicalizeEnhancementFolderScopeSection(JsonObject enhancement, string sectionName)
    {
        if (enhancement[sectionName] is not JsonObject section)
        {
            return;
        }

        var folderIds = ParseFolderIds(section, "folderIds");
        if (folderIds.Count == 0 && TryParseLegacyFolderId(section["folderId"], out var legacyFolderId))
        {
            folderIds.Add(legacyFolderId);
        }

        var normalized = new JsonArray();
        foreach (var folderId in folderIds.Distinct())
        {
            normalized.Add(folderId);
        }

        section["folderIds"] = normalized;
        section.Remove("folderId");
    }

    private static bool TryParseLegacyFolderId(JsonNode? folderIdNode, out long folderId)
    {
        folderId = 0;
        if (folderIdNode is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<long>(out var longValue) && longValue > 0)
        {
            folderId = longValue;
            return true;
        }

        if (value.TryGetValue<int>(out var intValue) && intValue > 0)
        {
            folderId = intValue;
            return true;
        }

        if (value.TryGetValue<string>(out var stringValue)
            && long.TryParse(stringValue, out var parsedValue)
            && parsedValue > 0)
        {
            folderId = parsedValue;
            return true;
        }

        return false;
    }

    private static void EnsureLegacyFolderUniformityStructureMirrorsRemoved(JsonNode node)
    {
        if (node is not JsonObject root
            || root[AutoTagLiterals.EnhancementStage] is not JsonObject enhancement
            || enhancement["folderUniformity"] is not JsonObject folderUniformity)
        {
            return;
        }

        folderUniformity.Remove("usePrimaryArtistFolders");
        folderUniformity.Remove(AutoTagLiterals.MultiArtistSeparatorKey);
        folderUniformity.Remove("createArtistFolder");
        folderUniformity.Remove("artistNameTemplate");
        folderUniformity.Remove("createAlbumFolder");
        folderUniformity.Remove("albumNameTemplate");
        folderUniformity.Remove("createCDFolder");
        folderUniformity.Remove("createStructurePlaylist");
        folderUniformity.Remove("createSingleFolder");
        folderUniformity.Remove("createPlaylistFolder");
        folderUniformity.Remove("playlistNameTemplate");
        folderUniformity.Remove("illegalCharacterReplacer");
        folderUniformity.Remove("renameSpotifyArtistFolders");
    }

    private static void EnsureLegacyOrganizerConfigRemoved(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        root.Remove("organizer");
    }

    private static void EnsureLegacyDeezerAuthRemoved(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(static pair => pair.Key).ToList())
                {
                    if (string.Equals(key, "arl", StringComparison.OrdinalIgnoreCase))
                    {
                        obj.Remove(key);
                        continue;
                    }

                    if (obj[key] is { } child)
                    {
                        EnsureLegacyDeezerAuthRemoved(child);
                    }
                }
                break;
            case JsonArray array:
                foreach (var child in array.Where(static item => item != null))
                {
                    EnsureLegacyDeezerAuthRemoved(child!);
                }
                break;
        }
    }

    private static string RedactSensitiveConfigJson(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return configJson;
        }

        try
        {
            var node = JsonNode.Parse(configJson);
            if (node == null)
            {
                return configJson;
            }

            RedactSensitiveNode(node);
            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return configJson;
        }
    }

    private static void RedactSensitiveNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    foreach (var key in obj.Select(pair => pair.Key).ToList())
                    {
                        if (ShouldRedactConfigKey(key))
                        {
                            obj.Remove(key);
                            continue;
                        }

                        if (obj[key] is { } child)
                        {
                            RedactSensitiveNode(child);
                        }
                    }
                    break;
                }
            case JsonArray array:
                {
                    foreach (var item in array.Where(static item => item != null))
                    {
                        RedactSensitiveNode(item!);
                    }
                    break;
                }
        }
    }

    private static bool ShouldRedactConfigKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (RedactedConfigKeys.Contains(key))
        {
            return true;
        }

        var normalized = NormalizeConfigKeyForRedaction(key);
        return RedactedConfigKeys.Contains(normalized);
    }

    private static string NormalizeConfigKeyForRedaction(string value)
    {
        var chars = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private void TrySaveLastConfig(string configJson)
    {
        try
        {
            var safeConfig = RedactSensitiveConfigJson(configJson);
            File.WriteAllText(_lastConfigPath, safeConfig, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to persist last AutoTag config.");
        }
    }

    private string WriteRuntimeConfigFile(string jobId, string stage, string configJson)
    {
        Directory.CreateDirectory(_runtimeConfigDir);
        var stageToken = string.IsNullOrWhiteSpace(stage)
            ? "stage"
            : NormalizeConfigKeyForRedaction(stage);
        var fileName = $"autotag-{jobId}-{stageToken}-{Guid.NewGuid():N}.json";
        var path = Path.Join(_runtimeConfigDir, fileName);
        File.WriteAllText(path, configJson, new UTF8Encoding(false));
        return path;
    }

    private void CleanupRuntimeConfigFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!IsRuntimeConfigPath(path))
                {
                    continue;
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Failed deleting runtime AutoTag config file {Path}", path);
                }
            }
        }
    }

    private bool IsRuntimeConfigPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRuntimeRoot = Path.GetFullPath(_runtimeConfigDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRuntimeRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void TrySaveLastJobId(string jobId)
    {
        try
        {
            var payload = new JsonObject { ["jobId"] = jobId };
            File.WriteAllText(_lastJobPath, payload.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to persist last AutoTag job id.");
        }
    }

    private async Task<string> InjectPlatformDefaultsAsync(string configJson)
    {
        try
        {
            var platformsJson = await _metadataService.GetPlatformsJsonAsync();
            if (string.IsNullOrWhiteSpace(platformsJson))
            {
                return configJson;
            }

            var platformDoc = JsonNode.Parse(platformsJson) as JsonArray;
            if (platformDoc == null)
            {
                return configJson;
            }

            var node = JsonNode.Parse(configJson) as JsonObject;
            if (node == null)
            {
                return configJson;
            }

            var custom = GetOrCreateCustomNode(node);

            foreach (var entry in platformDoc)
            {
                if (entry is not JsonObject platform || !TryGetPlatformOptionDefaults(platform, out var platformId, out var customOptions))
                {
                    continue;
                }

                var platformCustom = GetOrCreatePlatformCustomNode(custom, platformId);

                foreach (var optionNode in customOptions)
                {
                    TryApplyPlatformOptionDefault(platformCustom, optionNode);
                }
            }

            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return configJson;
        }
    }

    private static bool TryGetPlatformOptionDefaults(
        JsonObject platform,
        out string platformId,
        out JsonArray customOptions)
    {
        var platformInfo = platform[AutoTagLiterals.PlatformKey] as JsonObject ?? platform;
        platformId = platform["id"]?.GetValue<string>() ?? platformInfo["id"]?.GetValue<string>() ?? string.Empty;
        customOptions = platformInfo["customOptions"]?["options"] as JsonArray ?? new JsonArray();
        return !string.IsNullOrWhiteSpace(platformId) && customOptions.Count > 0;
    }

    private static JsonObject GetOrCreateCustomNode(JsonObject node)
    {
        if (node[AutoTagLiterals.CustomKey] is JsonObject custom)
        {
            return custom;
        }

        custom = new JsonObject();
        node[AutoTagLiterals.CustomKey] = custom;
        return custom;
    }

    private static JsonObject GetOrCreatePlatformCustomNode(JsonObject custom, string platformId)
    {
        if (custom[platformId] is JsonObject platformCustom)
        {
            return platformCustom;
        }

        platformCustom = new JsonObject();
        custom[platformId] = platformCustom;
        return platformCustom;
    }

    private static void TryApplyPlatformOptionDefault(JsonObject platformCustom, JsonNode? optionNode)
    {
        if (optionNode is not JsonObject option)
        {
            return;
        }

        var optionId = option["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(optionId) || platformCustom[optionId] != null)
        {
            return;
        }

        var value = option["value"]?["value"];
        if (value != null)
        {
            platformCustom[optionId] = value.DeepClone();
        }
    }

    private static string NormalizeRunTrigger(string? trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return AutoTagLiterals.ManualTrigger;
        }

        return trigger.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.ManualTrigger => AutoTagLiterals.ManualTrigger,
            AutoTagLiterals.AutomationTrigger => AutoTagLiterals.AutomationTrigger,
            AutoTagLiterals.ScheduleTrigger => AutoTagLiterals.ScheduleTrigger,
            _ => AutoTagLiterals.InvalidTrigger
        };
    }

    private static string NormalizeRunIntent(string? runIntent)
    {
        if (string.IsNullOrWhiteSpace(runIntent))
        {
            return AutoTagLiterals.RunIntentDefault;
        }

        return runIntent.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.RunIntentDownloadEnrichment => AutoTagLiterals.RunIntentDownloadEnrichment,
            AutoTagLiterals.RunIntentEnhancementOnly => AutoTagLiterals.RunIntentEnhancementOnly,
            AutoTagLiterals.RunIntentEnhancementRecentDownloads => AutoTagLiterals.RunIntentEnhancementRecentDownloads,
            AutoTagLiterals.RunIntentManualEnrichment => AutoTagLiterals.RunIntentManualEnrichment,
            _ => AutoTagLiterals.RunIntentDefault
        };
    }

    private static string? NormalizeEnhancementFeature(string? feature)
    {
        if (string.IsNullOrWhiteSpace(feature))
        {
            return null;
        }

        return feature.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.EnhancementFeatureGapFill => AutoTagLiterals.EnhancementFeatureGapFill,
            AutoTagLiterals.EnhancementFeatureFolderUniformity => AutoTagLiterals.EnhancementFeatureFolderUniformity,
            AutoTagLiterals.EnhancementFeatureQualityChecks => AutoTagLiterals.EnhancementFeatureQualityChecks,
            AutoTagLiterals.EnhancementFeatureSidecars => AutoTagLiterals.EnhancementFeatureSidecars,
            AutoTagLiterals.EnhancementFeatureCoverMaintenance => AutoTagLiterals.EnhancementFeatureSidecars,
            AutoTagLiterals.EnhancementFeatureLyricsRefreshLegacy => null,
            AutoTagLiterals.EnhancementFeatureManualEnrichment => AutoTagLiterals.EnhancementFeatureManualEnrichment,
            _ => null
        };
    }

    private static bool IsEnhancementRunIntent(string? runIntent)
    {
        return NormalizeRunIntent(runIntent) switch
        {
            AutoTagLiterals.RunIntentEnhancementOnly => true,
            AutoTagLiterals.RunIntentEnhancementRecentDownloads => true,
            _ => false
        };
    }

    private static bool IsManualEnrichmentRunIntent(string? runIntent)
        => string.Equals(
            NormalizeRunIntent(runIntent),
            AutoTagLiterals.RunIntentManualEnrichment,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedEnhancementTrigger(string? trigger)
    {
        if (string.Equals(trigger, AutoTagLiterals.ManualTrigger, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, AutoTagLiterals.ScheduleTrigger, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldRunEnrichmentForIntent(string? runIntent)
    {
        return !IsEnhancementRunIntent(runIntent);
    }

    private static bool ShouldRunEnhancementForIntent(string? runIntent)
    {
        var normalized = NormalizeRunIntent(runIntent);
        return !string.Equals(normalized, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, AutoTagLiterals.RunIntentManualEnrichment, StringComparison.OrdinalIgnoreCase);
    }

    private static string InjectRunTrigger(string configJson, string trigger)
    {
        try
        {
            var node = JsonNode.Parse(configJson) as JsonObject;
            if (node == null)
            {
                return configJson;
            }

            node["runTrigger"] = NormalizeRunTrigger(trigger);
            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return configJson;
        }
    }

    private string InjectProfileRuntimeSettings(
        string configJson,
        TechnicalTagSettings? technical,
        FolderStructureSettings? folderStructure,
        string? profileId,
        string? profileName)
    {
        if (technical == null
            && folderStructure == null
            && string.IsNullOrWhiteSpace(profileId)
            && string.IsNullOrWhiteSpace(profileName))
        {
            return configJson;
        }

        if (string.IsNullOrWhiteSpace(configJson))
        {
            return configJson;
        }

        try
        {
            if (JsonNode.Parse(configJson) is not JsonObject root)
            {
                return configJson;
            }

            if (technical != null)
            {
                root["technical"] = JsonSerializer.SerializeToNode(technical, _jsonOptions);
            }

            if (folderStructure != null)
            {
                root["folderStructure"] = JsonSerializer.SerializeToNode(folderStructure, _jsonOptions);
            }

            if (!string.IsNullOrWhiteSpace(profileId))
            {
                root["profileId"] = profileId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(profileName))
            {
                root["profileName"] = profileName.Trim();
            }

            return root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to inject profile runtime settings into AutoTag config.");
            return configJson;
        }
    }

    private async Task<string> InjectPlatformAuthAsync(string configJson)
    {
        try
        {
            var state = await _platformAuthService.LoadAsync();
            if (state == null)
            {
                return configJson;
            }

            var node = JsonNode.Parse(configJson) as JsonObject;
            if (node == null)
            {
                return configJson;
            }

            var custom = GetOrCreateCustomNode(node);
            ApplyDiscogsAuthDefaults(custom, state.Discogs);
            ApplyLastFmAuthDefaults(custom, state.LastFm);
            ApplyBpmSupremeAuthDefaults(custom, state.BpmSupreme);

            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return configJson;
        }
    }

    private static void ApplyDiscogsAuthDefaults(JsonObject custom, DiscogsAuth? discogsAuth)
    {
        if (string.IsNullOrWhiteSpace(discogsAuth?.Token))
        {
            return;
        }

        var discogs = GetOrCreatePlatformCustomNode(custom, AutoTagLiterals.DiscogsPlatform);
        SetIfEmpty(discogs, "token", discogsAuth.Token);
    }

    private static void ApplyLastFmAuthDefaults(JsonObject custom, LastFmAuth? lastFmAuth)
    {
        if (string.IsNullOrWhiteSpace(lastFmAuth?.ApiKey))
        {
            return;
        }

        var lastFm = GetOrCreatePlatformCustomNode(custom, AutoTagLiterals.LastFmPlatform);
        SetIfEmpty(lastFm, "apiKey", lastFmAuth.ApiKey);
    }

    private static void ApplyBpmSupremeAuthDefaults(JsonObject custom, BpmSupremeAuth? bpmAuth)
    {
        if (bpmAuth == null)
        {
            return;
        }

        var bpm = GetOrCreatePlatformCustomNode(custom, AutoTagLiterals.BpmSupremePlatform);
        SetIfEmpty(bpm, "email", bpmAuth.Email);
        SetIfEmpty(bpm, "password", bpmAuth.Password);
        SetIfEmpty(bpm, "library", bpmAuth.Library);
    }

    private static void SetIfEmpty(JsonObject target, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (target.TryGetPropertyValue(key, out var existingNode)
            && existingNode is JsonValue existingValue
            && existingValue.TryGetValue<string>(out var existingText)
            && !string.IsNullOrWhiteSpace(existingText))
        {
            return;
        }

        target[key] = value.Trim();
    }


    private static void RemoveNulls(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kvp => kvp.Key).ToList())
            {
                var value = obj[key];
                if (value is null || value.GetValueKind() == JsonValueKind.Null)
                {
                    obj.Remove(key);
                }
                else
                {
                    RemoveNulls(value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(static child => child != null))
            {
                RemoveNulls(child!);
            }
        }
    }

    private static void EnsureSpotifySecret(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        if (root[AutoTagLiterals.SpotifySource] is not JsonObject spotify)
        {
            return;
        }

        if (!spotify.TryGetPropertyValue("clientSecret", out var secret))
        {
            spotify["clientSecret"] = "";
            return;
        }
        if (secret is null || secret.GetValueKind() == JsonValueKind.Null)
        {
            spotify["clientSecret"] = "";
        }
    }

    private void UpdateStatus(
        AutoTagJob job,
        TaggingStatusWrap status,
        string stageName,
        string stageConfigHash,
        int stageIndex,
        int stageCount,
        IDictionary<string, FileTagOutcome>? fileOutcomes)
    {
        if (status.Status == null)
        {
            return;
        }

        job.LastStatus = status;
        job.Progress = ScaleProgress(status.Progress, stageIndex, stageCount);
        job.CurrentPlatform = status.Platform;
        if ((IsEnhancementRunIntent(job.RunIntent)
             || IsManualEnrichmentRunIntent(job.RunIntent))
            && (string.Equals(stageName, AutoTagLiterals.EnhancementStage, StringComparison.OrdinalIgnoreCase)
                || string.Equals(stageName, AutoTagLiterals.EnrichmentStage, StringComparison.OrdinalIgnoreCase))
            && status.FileCount is > 0)
        {
            var fileIndex = Math.Clamp(status.FileIndex ?? 0, 0, status.FileCount.Value - 1);
            job.CurrentPhase = string.IsNullOrWhiteSpace(job.EnhancementFeature)
                ? AutoTagLiterals.EnhancementFeatureGapFill
                : job.EnhancementFeature;
            job.TotalItems = job.TargetUsable > 0 ? job.TargetUsable : status.FileCount.Value;
            job.ProcessedItems = Math.Min(job.TotalItems, fileIndex + 1);
            job.BatchSize = Math.Min(EnhancementBatchSize, status.FileCount.Value - (fileIndex / EnhancementBatchSize * EnhancementBatchSize));
            job.CurrentBatch = fileIndex / EnhancementBatchSize + 1;
            job.BatchCount = (int)Math.Ceiling(status.FileCount.Value / (double)EnhancementBatchSize);
            job.BatchProcessed = fileIndex % EnhancementBatchSize + 1;
        }
        TryCaptureTagDiff(job, status);
        ApplyIdentityReviewGuard(job, status);
        RouteReviewFileIfNeeded(job, status);
        AppendStatusHistory(job, status);
        TrackFileOutcome(fileOutcomes, status);
        TrackEnhancedFilePath(job, stageName, status);
        switch (status.Status.Status)
        {
            case AutoTagLiterals.OkStatus:
            case AutoTagLiterals.TaggedStatus:
                job.OkCount += 1;
                break;
            case AutoTagLiterals.ErrorStatus:
                job.ErrorCount += 1;
                break;
            case AutoTagLiterals.ReviewStatus:
                job.ReviewCount += 1;
                break;
            case AutoTagLiterals.SkippedStatus:
                job.SkippedCount += 1;
                break;
        }
        TryUpdateResumeCheckpoint(job, stageName, stageConfigHash, status);
        SaveJob(job);
    }

    private static void TrackEnhancedFilePath(AutoTagJob job, string stageName, TaggingStatusWrap status)
    {
        var statusValue = status.Status;
        if (!IsEnhancementRunIntent(job.RunIntent)
            || !string.Equals(stageName, AutoTagLiterals.EnhancementStage, StringComparison.OrdinalIgnoreCase)
            || !IsSuccessfulTagStatus(statusValue?.Status)
            || string.IsNullOrWhiteSpace(statusValue?.Path))
        {
            return;
        }

        var normalizedPath = NormalizePathForJob(statusValue.Path);
        if (job.EnhancedFilePaths.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        job.EnhancedFilePaths.Add(normalizedPath);
    }


    private void RouteReviewFileIfNeeded(AutoTagJob job, TaggingStatusWrap status)
    {
        var statusValue = status.Status;
        if (statusValue == null
            || !string.Equals(statusValue.Status, AutoTagLiterals.ReviewStatus, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(statusValue.Path))
        {
            return;
        }

        var isManualEnrichment = IsManualEnrichmentRunIntent(job.RunIntent);
        if (!isManualEnrichment
            && !string.Equals(status.Platform, "shazam", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (!isManualEnrichment && !HasShazamReviewCandidate(statusValue))
        {
            AppendLog(
                job,
                $"review folder: ignored Shazam review status without candidate metadata for {statusValue.Path}; file remains in place.");
            return;
        }

        var settings = _settingsService.LoadSettings();
        var reviewFolder = settings.ReviewFolderPath?.Trim();
        if (string.IsNullOrWhiteSpace(reviewFolder))
        {
            PauseForMissingReviewFolder(
                job,
                "Review folder is not configured. Configure it in Settings > Download Path and mount it in Docker before Shazam review handling can continue.");
        }

        var reviewRoot = ResolveReviewFolderIoPath(reviewFolder!);
        if (!IsReviewFolderWritable(reviewRoot, out var validationError))
        {
            PauseForMissingReviewFolder(
                job,
                $"Review folder is not writable or not mounted: {validationError}. Configure it in Settings > Download Path.");
        }

        var sourcePath = DownloadPathResolver.ResolveIoPath(statusValue.Path);
        if (!File.Exists(sourcePath))
        {
            PauseForMissingReviewFolder(
                job,
                $"Shazam flagged a file for review, but the source file no longer exists: {statusValue.Path}");
        }

        var destinationPath = ResolveReviewDestinationPath(sourcePath, reviewRoot, settings.DownloadLocation);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Move(sourcePath, destinationPath);

        var reportPath = Path.ChangeExtension(destinationPath, ".review.txt");
        File.WriteAllText(reportPath, BuildReviewReport(job, statusValue, sourcePath, destinationPath), new UTF8Encoding(false));
        statusValue.ReviewDestinationPath = destinationPath;
        statusValue.ReviewReportPath = reportPath;
        AppendLog(job, $"review folder: moved Shazam-flagged file to {destinationPath}");
    }

    private void PauseForMissingReviewFolder(AutoTagJob job, string message)
    {
        AppendLog(job, message);
        throw new AutoTagRunPausedException(message);
    }

    private void NotifyDownloadToast(string message, string type)
    {
        try
        {
            _downloadEvents.Send("toastNotification", new
            {
                message,
                type,
                action = new
                {
                    label = "Settings",
                    href = "/Settings#download-path-settings"
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to publish download toast notification.");
        }
    }

    private static string ResolveReviewFolderIoPath(string reviewFolder)
        => DownloadPathResolver.ResolveIoPath(reviewFolder.Trim());

    private static bool IsReviewFolderWritable(string reviewRoot, out string error)
    {
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(reviewRoot))
            {
                error = "path is empty";
                return false;
            }

            Directory.CreateDirectory(reviewRoot);
            var probePath = Path.Join(reviewRoot, $".deezspotag-review-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "ok", new UTF8Encoding(false));
            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool HasShazamReviewCandidate(TaggingStatus status)
    {
        return !string.IsNullOrWhiteSpace(status.CandidateTitle)
            || !string.IsNullOrWhiteSpace(status.CandidateArtist)
            || !string.IsNullOrWhiteSpace(status.CandidateIsrc)
            || status.CandidateDurationSeconds.HasValue;
    }

    private static string ResolveReviewDestinationPath(string sourcePath, string reviewRoot, string downloadLocation)
    {
        var relativePath = Path.GetFileName(sourcePath);
        try
        {
            var downloadRoot = DownloadPathResolver.ResolveIoPath(downloadLocation);
            if (!string.IsNullOrWhiteSpace(downloadRoot)
                && IsPathUnderRoot(sourcePath, downloadRoot))
            {
                relativePath = Path.GetRelativePath(downloadRoot, sourcePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            relativePath = Path.GetFileName(sourcePath);
        }

        var destinationPath = Path.GetFullPath(Path.Join(reviewRoot, relativePath));
        var reviewRootFull = Path.GetFullPath(reviewRoot);
        if (!IsPathUnderRoot(destinationPath, reviewRootFull))
        {
            destinationPath = Path.Join(reviewRootFull, Path.GetFileName(sourcePath));
        }

        return GetAvailableReviewPath(destinationPath);
    }

    private static string GetAvailableReviewPath(string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            return destinationPath;
        }

        var directory = Path.GetDirectoryName(destinationPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(destinationPath);
        var extension = Path.GetExtension(destinationPath);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Join(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Join(directory, $"{name} ({Guid.NewGuid():N}){extension}");
    }

    private static string BuildReviewReport(
        AutoTagJob job,
        TaggingStatus status,
        string sourcePath,
        string destinationPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("DeezSpoTag AutoTag Review");
        builder.AppendLine($"Timestamp: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"Job Id: {job.Id}");
        builder.AppendLine($"Reason: {FirstNonEmpty(status.ReviewReason, status.Message, "Shazam flagged this file for review.")}");
        builder.AppendLine($"Original path: {sourcePath}");
        builder.AppendLine($"Review path: {destinationPath}");
        builder.AppendLine();
        builder.AppendLine("Source");
        builder.AppendLine($"Title: {status.SourceTitle ?? ""}");
        builder.AppendLine($"Artist: {status.SourceArtist ?? ""}");
        builder.AppendLine($"ISRC: {status.SourceIsrc ?? ""}");
        builder.AppendLine($"Duration seconds: {FormatNullableDouble(status.SourceDurationSeconds)}");
        builder.AppendLine();
        builder.AppendLine("Shazam candidate");
        builder.AppendLine($"Title: {status.CandidateTitle ?? ""}");
        builder.AppendLine($"Artist: {status.CandidateArtist ?? ""}");
        builder.AppendLine($"ISRC: {status.CandidateIsrc ?? ""}");
        builder.AppendLine($"Duration seconds: {FormatNullableDouble(status.CandidateDurationSeconds)}");
        return builder.ToString();
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FormatNullableDouble(double? value)
        => value.HasValue ? value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    private static bool IsTerminalStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.OkStatus => true,
            AutoTagLiterals.TaggedStatus => true,
            AutoTagLiterals.ReviewStatus => true,
            AutoTagLiterals.ErrorStatus => true,
            AutoTagLiterals.SkippedStatus => true,
            _ => false
        };
    }

    private static void TryUpdateResumeCheckpoint(
        AutoTagJob job,
        string stageName,
        string stageConfigHash,
        TaggingStatusWrap status)
    {
        if (!IsTerminalStatus(status.Status?.Status))
        {
            return;
        }

        if (status.NextPlatformIndex is not int nextPlatformIndex
            || status.NextFileIndex is not int nextFileIndex
            || status.PlatformCount is not int platformCount
            || status.FileCount is not int fileCount
            || platformCount <= 0
            || fileCount <= 0)
        {
            return;
        }

        job.ResumeCheckpoint = new AutoTagResumeCheckpoint
        {
            StageName = stageName,
            StageConfigHash = stageConfigHash,
            PlatformIndex = Math.Max(0, nextPlatformIndex),
            FileIndex = Math.Max(0, nextFileIndex),
            PlatformCount = platformCount,
            FileCount = fileCount,
            LastPath = status.Status?.Path,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static AutoTagResumeCursor? ResolveResumeCursor(AutoTagJob job, AutoTagStageConfig stage)
    {
        if (!CanApplyResumeCheckpoint(job.ResumeCheckpoint, stage))
        {
            return null;
        }

        var checkpoint = job.ResumeCheckpoint!;
        return new AutoTagResumeCursor(
            Math.Max(0, checkpoint.PlatformIndex),
            Math.Max(0, checkpoint.FileIndex),
            checkpoint.PlatformCount,
            checkpoint.FileCount,
            checkpoint.LastPath);
    }

    private static bool CanApplyResumeCheckpoint(AutoTagResumeCheckpoint? checkpoint, AutoTagStageConfig stage)
    {
        if (checkpoint == null)
        {
            return false;
        }

        if (checkpoint.PlatformCount <= 0 || checkpoint.FileCount <= 0)
        {
            return false;
        }

        if (checkpoint.PlatformIndex < 0
            || checkpoint.FileIndex < 0
            || checkpoint.PlatformIndex >= checkpoint.PlatformCount
            || checkpoint.FileIndex > checkpoint.FileCount)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(checkpoint.StageName) || string.IsNullOrWhiteSpace(checkpoint.StageConfigHash))
        {
            return false;
        }

        return string.Equals(checkpoint.StageName, stage.Name, StringComparison.OrdinalIgnoreCase)
               && string.Equals(checkpoint.StageConfigHash, stage.ConfigHash, StringComparison.OrdinalIgnoreCase);
    }

    private void TryCaptureTagDiff(AutoTagJob job, TaggingStatusWrap status)
    {
        if (!TryResolveCaptureMode(status, out var normalizedStatus, out var captureBefore, out var captureAfter))
        {
            return;
        }

        var normalizedPath = NormalizeDiffPath(status.Status!.Path!);
        if (!TryBuildDiffSnapshot(normalizedPath, out var snapshot) || snapshot == null)
        {
            return;
        }

        lock (job.TagDiffs)
        {
            var diff = GetOrCreateTagDiff(job.TagDiffs, normalizedPath);
            var platformDiff = GetOrCreatePlatformDiff(diff, status.Platform, normalizedStatus, captureBefore, captureAfter);
            ApplyCapturedDiffSnapshot(diff, platformDiff, snapshot, status.Platform, normalizedStatus, captureBefore, captureAfter);
            SaveTagDiffCheckpoint(job.Id, normalizedPath, diff);
        }
    }

    private static void ApplyIdentityReviewGuard(AutoTagJob job, TaggingStatusWrap status)
    {
        if (status.Status == null
            || string.IsNullOrWhiteSpace(status.Status.Path)
            || !IsSuccessfulTagStatus(status.Status.Status))
        {
            return;
        }

        var normalizedPath = NormalizeDiffPath(status.Status.Path);
        AutoTagTagDiff? diff;
        lock (job.TagDiffs)
        {
            job.TagDiffs.TryGetValue(normalizedPath, out diff);
        }

        var reviewReason = EvaluateIdentityReviewGuard(diff);
        if (string.IsNullOrWhiteSpace(reviewReason))
        {
            return;
        }

        status.Status.Status = AutoTagLiterals.ReviewStatus;
        status.Status.Message = reviewReason;
    }

    private static bool IsSuccessfulTagStatus(string? status)
    {
        return string.Equals(status, AutoTagLiterals.OkStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.TaggedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static string? EvaluateIdentityReviewGuard(AutoTagTagDiff? diff)
    {
        if (diff?.Before == null || diff.After == null)
        {
            return null;
        }

        var beforeTitle = GetMetaFieldValue(diff.Before, AutoTagTitleKey);
        var afterTitle = GetMetaFieldValue(diff.After, AutoTagTitleKey);
        var beforeArtists = GetMetaFieldValue(diff.Before, AutoTagArtistsKey);
        var afterArtists = GetMetaFieldValue(diff.After, AutoTagArtistsKey);
        var beforeAlbumArtists = GetMetaFieldValue(diff.Before, "albumArtists");
        var afterAlbumArtists = GetMetaFieldValue(diff.After, "albumArtists");

        var artistSimilarity = Math.Max(
            ComputeIdentitySimilarity(beforeArtists, afterArtists),
            ComputeIdentitySimilarity(beforeAlbumArtists, afterAlbumArtists));
        var artistChanged = HasMeaningfulIdentityChange(beforeArtists, afterArtists)
            || HasMeaningfulIdentityChange(beforeAlbumArtists, afterAlbumArtists);
        if (artistChanged && artistSimilarity < IdentityReviewArtistSimilarityThreshold)
        {
            return $"requires user review: artist identity changed sharply (artist similarity {artistSimilarity:0.000})";
        }

        if (!HasMeaningfulIdentityChange(beforeTitle, afterTitle))
        {
            return null;
        }

        var titleSimilarity = ComputeIdentitySimilarity(beforeTitle, afterTitle);
        if (titleSimilarity >= IdentityReviewTitleSimilarityThreshold || !artistChanged)
        {
            return null;
        }

        return $"requires user review: identity changed sharply (title similarity {titleSimilarity:0.000}, artist similarity {artistSimilarity:0.000})";
    }

    private static bool HasMeaningfulIdentityChange(object? before, object? after)
    {
        var normalizedBefore = NormalizeCompareValue(before);
        var normalizedAfter = NormalizeCompareValue(after);
        return !string.IsNullOrWhiteSpace(normalizedBefore)
            && !string.IsNullOrWhiteSpace(normalizedAfter)
            && !string.Equals(normalizedBefore, normalizedAfter, StringComparison.Ordinal);
    }

    private static double ComputeIdentitySimilarity(object? before, object? after)
    {
        var normalizedBefore = AutoTagSimilarity.NormalizeText(NormalizeCompareValue(before));
        var normalizedAfter = AutoTagSimilarity.NormalizeText(NormalizeCompareValue(after));
        return AutoTagSimilarity.ComputeScore(normalizedBefore, normalizedAfter);
    }

    private static bool TryResolveCaptureMode(
        TaggingStatusWrap status,
        out string normalizedStatus,
        out bool captureBefore,
        out bool captureAfter)
    {
        normalizedStatus = string.Empty;
        captureBefore = false;
        captureAfter = false;
        if (status.Status == null || string.IsNullOrWhiteSpace(status.Status.Path))
        {
            return false;
        }

        var statusValue = status.Status.Status?.Trim() ?? string.Empty;
        normalizedStatus = statusValue.ToLowerInvariant();
        var message = status.Status.Message ?? string.Empty;
        var isAlreadyTagged = normalizedStatus == AutoTagLiterals.SkippedStatus
            && message.Contains("already tagged", StringComparison.OrdinalIgnoreCase);
        captureBefore = normalizedStatus == AutoTagLiterals.TaggingStatus || isAlreadyTagged;
        captureAfter = normalizedStatus is AutoTagLiterals.TaggedStatus or AutoTagLiterals.OkStatus || isAlreadyTagged;
        return captureBefore || captureAfter;
    }

    private bool TryBuildDiffSnapshot(string normalizedPath, out AutoTagTagSnapshot? snapshot)
    {
        try
        {
            snapshot = BuildTagSnapshot(normalizedPath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "AutoTag diff snapshot failed for {Path}", normalizedPath);
            }
            snapshot = null;
            return false;
        }
    }

    private static AutoTagTagDiff GetOrCreateTagDiff(Dictionary<string, AutoTagTagDiff> diffs, string normalizedPath)
    {
        if (diffs.TryGetValue(normalizedPath, out var existing))
        {
            existing.PlatformDiffs ??= new List<AutoTagPlatformDiffSnapshot>();
            return existing;
        }

        var created = new AutoTagTagDiff
        {
            Path = normalizedPath,
            PlatformDiffs = new List<AutoTagPlatformDiffSnapshot>()
        };
        diffs[normalizedPath] = created;
        return created;
    }

    private static AutoTagPlatformDiffSnapshot? GetOrCreatePlatformDiff(
        AutoTagTagDiff diff,
        string? platform,
        string normalizedStatus,
        bool captureBefore,
        bool captureAfter)
    {
        if (captureBefore)
        {
            var beforeStep = new AutoTagPlatformDiffSnapshot
            {
                Platform = platform ?? string.Empty,
                Status = normalizedStatus,
                CapturedAt = DateTimeOffset.UtcNow
            };
            diff.PlatformDiffs.Add(beforeStep);
            return beforeStep;
        }

        if (!captureAfter)
        {
            return null;
        }

        var existingAfter = diff.PlatformDiffs.LastOrDefault(step =>
            string.Equals(step.Platform, platform, StringComparison.OrdinalIgnoreCase)
            && step.After == null);
        if (existingAfter != null)
        {
            return existingAfter;
        }

        var createdAfter = new AutoTagPlatformDiffSnapshot
        {
            Platform = platform ?? string.Empty,
            Status = normalizedStatus,
            CapturedAt = DateTimeOffset.UtcNow
        };
        diff.PlatformDiffs.Add(createdAfter);
        return createdAfter;
    }

    private static void ApplyCapturedDiffSnapshot(
        AutoTagTagDiff diff,
        AutoTagPlatformDiffSnapshot? platformDiff,
        AutoTagTagSnapshot snapshot,
        string? platform,
        string normalizedStatus,
        bool captureBefore,
        bool captureAfter)
    {
        if (captureBefore && diff.Before == null)
        {
            diff.Before = snapshot;
        }

        if (captureBefore && platformDiff != null && platformDiff.Before == null)
        {
            platformDiff.Before = snapshot;
        }

        if (!captureAfter)
        {
            return;
        }

        diff.After = snapshot;
        diff.LastPlatform = platform;
        if (platformDiff == null)
        {
            return;
        }

        platformDiff.After = snapshot;
        platformDiff.Status = normalizedStatus;
        platformDiff.CapturedAt = DateTimeOffset.UtcNow;
    }

    private AutoTagTagSnapshot BuildTagSnapshot(string path)
    {
        var dump = _quickTagService.Dump(path, includeArtworkData: false, enforceLibraryPathCheck: false);
        return new AutoTagTagSnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Meta = dump.Meta,
            Tags = CloneTags(dump.Tags)
        };
    }

    private static Dictionary<string, List<string>> CloneTags(Dictionary<string, List<string>> tags)
    {
        var clone = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in tags)
        {
            if (BinaryArtworkTagKeys.Contains(key))
            {
                continue;
            }
            clone[key] = values?.ToList() ?? new List<string>();
        }
        return clone;
    }

    private static string NormalizeDiffPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return path;
        }
    }

    private static void TrackFileOutcome(IDictionary<string, FileTagOutcome>? fileOutcomes, TaggingStatusWrap status)
    {
        if (fileOutcomes == null || status.Status == null || string.IsNullOrWhiteSpace(status.Status.Path))
        {
            return;
        }

        var terminalStatus = status.Status.Status;
        if (terminalStatus is not AutoTagLiterals.OkStatus
            and not AutoTagLiterals.TaggedStatus
            and not AutoTagLiterals.SkippedStatus
            and not AutoTagLiterals.ErrorStatus
            and not AutoTagLiterals.ReviewStatus)
        {
            return;
        }

        var filePath = status.Status.Path;
        try
        {
            filePath = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep raw path if canonicalization fails.
        }

        if (!fileOutcomes.TryGetValue(filePath, out var outcome))
        {
            outcome = new FileTagOutcome();
            fileOutcomes[filePath] = outcome;
        }

        outcome.Seen = true;
        switch (status.Status.Status)
        {
            case AutoTagLiterals.OkStatus:
            case AutoTagLiterals.TaggedStatus:
                outcome.Tagged = true;
                break;
            case AutoTagLiterals.SkippedStatus:
                if ((!string.IsNullOrWhiteSpace(status.Status.Message)
                     && status.Status.Message.Contains("already tagged", StringComparison.OrdinalIgnoreCase))
                    || string.Equals(status.Status.Outcome, "no_eligible_tags", StringComparison.Ordinal))
                {
                    outcome.CompletedWithoutChanges = true;
                }
                break;
        }
    }

    private static (IReadOnlyCollection<string> TaggedFiles, IReadOnlyCollection<string> FailedFiles) BuildMoveFileSets(
        IDictionary<string, FileTagOutcome> fileOutcomes)
    {
        if (fileOutcomes.Count == 0)
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var tagged = new List<string>(fileOutcomes.Count);
        var failed = new List<string>(fileOutcomes.Count);

        foreach (var pair in fileOutcomes)
        {
            var outcome = pair.Value;
            if (!outcome.Seen)
            {
                continue;
            }

            if (outcome.Tagged || outcome.CompletedWithoutChanges)
            {
                tagged.Add(pair.Key);
                continue;
            }

            failed.Add(pair.Key);
        }

        return (tagged, failed);
    }

    private static double ScaleProgress(double progress, int stageIndex, int stageCount)
    {
        if (stageCount <= 1)
        {
            return progress;
        }

        var clamped = progress;
        if (clamped < 0)
        {
            clamped = 0;
        }
        else if (clamped > 1)
        {
            clamped = 1;
        }

        var idx = stageIndex < 0 ? 0 : stageIndex;
        if (idx >= stageCount)
        {
            idx = stageCount - 1;
        }

        return (idx + clamped) / stageCount;
    }

    private static string BuildStageStartedLog(AutoTagStageConfig stage, int stageIndex, int stageCount)
    {
        _ = stageIndex;
        _ = stageCount;
        var name = FormatStageName(stage.Name);
        return $"{name} tagging started ({stage.TagCount} tags)";
    }

    private static string BuildStageFinishedLog(AutoTagStageConfig stage, int stageIndex, int stageCount)
    {
        _ = stageIndex;
        _ = stageCount;
        var name = FormatStageName(stage.Name);
        return $"{name} tagging finished";
    }

    private static string FormatStageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Stage";
        }

        var trimmed = name.Trim();
        if (trimmed.Length == 1)
        {
            return trimmed.ToUpperInvariant();
        }

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private void AppendStatusHistory(AutoTagJob job, TaggingStatusWrap status)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var snapshot = new TaggingStatusSnapshot
        {
            Timestamp = timestamp,
            Status = status
        };
        job.LastActivityAt = timestamp;
        lock (job.StatusHistory)
        {
            job.StatusHistory.Add(snapshot);
            if (job.StatusHistory.Count > 300)
            {
                job.StatusHistory.RemoveRange(0, job.StatusHistory.Count - 300);
            }
        }
        AppendArchivedStatus(job.Id, snapshot);
    }

    private void AppendLog(AutoTagJob job, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var cleaned = AnsiRegex.Replace(line, string.Empty);
        job.LastActivityAt = DateTimeOffset.UtcNow;
        TrackStartedPlatform(job, cleaned);
        lock (job.Logs)
        {
            job.Logs.Add(cleaned);
            if (job.Logs.Count > 200)
            {
                job.Logs.RemoveRange(0, job.Logs.Count - 200);
            }
        }
        AppendActivityLog(job.Id, cleaned);
        AppendArchivedLog(job.Id, cleaned);
        SaveJob(job);
    }

    private void AppendActivityLog(string jobId, string line)
    {
        try
        {
            var level = ResolveLogLevel(line);
            var cleaned = AnsiRegex.Replace(line, string.Empty).Trim();
            cleaned = StripLinePrefix(cleaned);
            if (string.IsNullOrEmpty(cleaned))
            {
                return;
            }
            if (_lastActivityLines.TryGetValue(jobId, out var lastLine) &&
                string.Equals(lastLine, cleaned, StringComparison.Ordinal))
            {
                return;
            }
            _lastActivityLines[jobId] = cleaned;
            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                level,
                $"[autotag] {cleaned}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to add AutoTag line to activity logs.");
        }
    }

    private void TrackStartedPlatform(AutoTagJob job, string line)
    {
        var cleaned = AnsiRegex.Replace(line, string.Empty).Trim();
        cleaned = StripLinePrefix(cleaned);
        if (!TryExtractStartedPlatform(cleaned, out var platform))
        {
            return;
        }

        lock (job.Logs)
        {
            if (!job.StartedPlatforms.Any(p => string.Equals(p, platform, StringComparison.OrdinalIgnoreCase)))
            {
                job.StartedPlatforms.Add(platform);
                SaveJob(job);
            }
        }
    }

    private void AppendPlatformSummary(AutoTagJob job)
    {
        if (job.StartedPlatforms.Count == 0)
        {
            return;
        }

        var summary = $"onetagger_autotag: platforms started: {string.Join(", ", job.StartedPlatforms)}";
        AppendActivityLog(job.Id, summary);
    }

    private static bool TryExtractStartedPlatform(string line, out string platform)
    {
        platform = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        const string marker = "onetagger_autotag:";
        var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var message = line[(markerIndex + marker.Length)..].TrimStart();
        const string starting = "starting ";
        if (!message.StartsWith(starting, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = message[starting.Length..].Trim();
        if (rest.StartsWith("tagger", StringComparison.OrdinalIgnoreCase) ||
            rest.StartsWith("tagging", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rest))
        {
            return false;
        }

        platform = rest;
        return true;
    }

    private static string ResolveLogLevel(string line)
    {
        var upper = line.ToUpperInvariant();
        if (upper.Contains("[ERROR]") || upper.Contains(" ERROR "))
        {
            return "error";
        }
        if (upper.Contains("[WARN]") || upper.Contains("[WARNING]") || upper.Contains(" WARN "))
        {
            return "warning";
        }
        if (upper.Contains("[DEBUG]") || upper.Contains(" DEBUG "))
        {
            return "debug";
        }
        return "info";
    }

    private static string StripLinePrefix(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var cleaned = line.Trim();
        var timestampEnd = cleaned.IndexOf(']');
        if (cleaned.StartsWith('[') && timestampEnd > 0)
        {
            cleaned = cleaned[(timestampEnd + 1)..].TrimStart();
        }

        if (cleaned.StartsWith('['))
        {
            var levelEnd = cleaned.IndexOf(']');
            if (levelEnd > 0)
            {
                cleaned = cleaned[(levelEnd + 1)..].TrimStart();
            }
        }

        return cleaned;
    }

    private void InitializeRunArchive(AutoTagJob job)
    {
        try
        {
            Directory.CreateDirectory(GetRunHistoryDirectory(job.Id));
            SaveRunSummary(job);

            var logPath = GetRunLogPath(job.Id);
            if (!File.Exists(logPath))
            {
                File.WriteAllText(logPath, string.Empty, new UTF8Encoding(false));
            }

            var statusPath = GetRunStatusHistoryPath(job.Id);
            if (!File.Exists(statusPath))
            {
                File.WriteAllText(statusPath, string.Empty, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to initialize AutoTag run archive for {JobId}", job.Id);
            }
        }
    }

    private IReadOnlyList<AutoTagRunSummary> GetArchivedRunSummaries()
    {
        PruneExpiredArchivedRuns();
        lock (_archivedRunSummariesCacheLock)
        {
            if (_archivedRunSummariesCache is not null && _archivedRunSummariesCacheExpiresUtc > DateTimeOffset.UtcNow)
            {
                return _archivedRunSummariesCache;
            }

            var summaries = LoadRunIndexSummaries();
            _archivedRunSummariesCache = summaries;
            _archivedRunSummariesCacheExpiresUtc = DateTimeOffset.UtcNow.Add(ArchivedRunSummariesCacheTtl);
            return summaries;
        }
    }

    private IReadOnlyList<AutoTagRunSummary> LoadRunIndexSummaries()
    {
        lock (_runIndexLock)
        {
            var indexed = TryLoadRunIndex();
            if (indexed.Count > 0 || (File.Exists(_runIndexPath) && new FileInfo(_runIndexPath).Length > 0))
            {
                return indexed;
            }

            var summaries = LoadArchivedRunSummaries();
            PersistRunIndex(summaries);
            return summaries;
        }
    }

    public void WarmRunIndexIfMissing()
    {
        PruneExpiredArchivedRuns();
        if (File.Exists(_runIndexPath) && new FileInfo(_runIndexPath).Length > 0)
        {
            return;
        }

        try
        {
            lock (_runIndexLock)
            {
                if (File.Exists(_runIndexPath) && new FileInfo(_runIndexPath).Length > 0)
                {
                    return;
                }

                PersistRunIndex(LoadArchivedRunSummaries());
            }
            PruneExpiredArchivedRuns(force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to warm AutoTag run index.");
        }
    }

    private IReadOnlyList<AutoTagRunSummary> TryLoadRunIndex()
    {
        try
        {
            if (!File.Exists(_runIndexPath))
            {
                return Array.Empty<AutoTagRunSummary>();
            }

            var json = File.ReadAllText(_runIndexPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<AutoTagRunSummary>();
            }

            var document = JsonSerializer.Deserialize<AutoTagRunIndexDocument>(json, _jsonOptions);
            return NormalizeRunIndexSummaries(document?.Runs ?? new List<AutoTagRunSummary>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load AutoTag run index.");
            var summaries = LoadArchivedRunSummaries();
            PersistRunIndex(summaries);
            return summaries;
        }
    }

    private void UpdateRunIndex(AutoTagRunSummary summary, bool force = false)
    {
        if (!force && !ShouldUpdateRunIndex(summary))
        {
            return;
        }

        lock (_runIndexLock)
        {
            var summaries = TryLoadRunIndex()
                .Where(run => !string.Equals(run.Id, summary.Id, StringComparison.OrdinalIgnoreCase))
                .Append(summary)
                .ToList();
            PersistRunIndex(summaries);
        }

        InvalidateArchivedRunSummariesCache();
        _activitiesRealtime.PublishAutoTagRunChanged(summary);
        PruneExpiredArchivedRuns();
    }

    private bool ShouldUpdateRunIndex(AutoTagRunSummary summary)
    {
        if (IsTerminalRunStatus(summary.Status))
        {
            _lastRunIndexUpdateUtc[summary.Id] = DateTimeOffset.UtcNow;
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var lastUpdate = _lastRunIndexUpdateUtc.GetOrAdd(summary.Id, now);
        if (lastUpdate == now || now - lastUpdate < RunIndexUpdateInterval)
        {
            return lastUpdate == now;
        }

        _lastRunIndexUpdateUtc[summary.Id] = now;
        return true;
    }

    private static bool IsTerminalRunStatus(string? status)
    {
        return string.Equals(status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, AutoTagLiterals.SkippedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private void PersistRunIndex(IReadOnlyCollection<AutoTagRunSummary> summaries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_runIndexPath) ?? _historyDir);
            var document = new AutoTagRunIndexDocument
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Runs = NormalizeRunIndexSummaries(summaries).ToList()
            };
            File.WriteAllText(
                _runIndexPath,
                JsonSerializer.Serialize(document, _jsonOptions),
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to persist AutoTag run index.");
        }
    }

    private void PruneExpiredArchivedRuns(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastArchivedRunPruneUtc < ArchivedRunPruneInterval)
        {
            return;
        }

        lock (_archivedRunPruneLock)
        {
            now = DateTimeOffset.UtcNow;
            if (!force && now - _lastArchivedRunPruneUtc < ArchivedRunPruneInterval)
            {
                return;
            }

            _lastArchivedRunPruneUtc = now;
            var cutoffUtc = now.Subtract(ResolveArchivedRunRetentionPeriod());
            try
            {
                var summaries = TryLoadRunIndex().ToList();
                if (summaries.Count == 0)
                {
                    summaries = LoadArchivedRunSummaries().ToList();
                }

                var retained = new List<AutoTagRunSummary>();
                var expired = new List<AutoTagRunSummary>();
                foreach (var summary in summaries)
                {
                    if (IsExpiredArchivedRun(summary, cutoffUtc))
                    {
                        expired.Add(summary);
                    }
                    else
                    {
                        retained.Add(summary);
                    }
                }

                foreach (var summary in expired)
                {
                    DeleteArchivedRunFiles(summary.Id);
                }

                if (expired.Count > 0)
                {
                    lock (_runIndexLock)
                    {
                        PersistRunIndex(retained);
                    }
                }

                PruneOrphanedArchivedRunArtifacts(cutoffUtc);
                InvalidateArchivedRunSummariesCache();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to prune expired AutoTag run history.");
            }
        }
    }

    private TimeSpan ResolveArchivedRunRetentionPeriod()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            var days = settings.AutoTagHistoryRetentionDays;
            if (days < 1 || days > 365)
            {
                days = new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings().AutoTagHistoryRetentionDays;
            }

            return TimeSpan.FromDays(days);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to resolve AutoTag history retention; using default.");
            }

            return TimeSpan.FromDays(new DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings().AutoTagHistoryRetentionDays);
        }
    }

    private bool IsExpiredArchivedRun(AutoTagRunSummary summary, DateTimeOffset cutoffUtc)
    {
        if (string.IsNullOrWhiteSpace(summary.Id) || _activeJobIds.ContainsKey(summary.Id))
        {
            return false;
        }

        return GetRunHistoryTimestamp(summary).ToUniversalTime() < cutoffUtc;
    }

    private void DeleteArchivedRunFiles(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        var normalizedJobId = Path.GetFileName(jobId.Trim());
        if (string.IsNullOrWhiteSpace(normalizedJobId))
        {
            return;
        }

        foreach (var root in EnumerateHistoryRoots())
        {
            TryDeleteDirectory(Path.Join(root, normalizedJobId));
        }

        TryDeleteFile(Path.Join(_jobsDir, normalizedJobId + ".json"));
        _archiveLocks.TryRemove(normalizedJobId, out _);
        _lastRunIndexUpdateUtc.TryRemove(normalizedJobId, out _);
    }

    private void PruneOrphanedArchivedRunArtifacts(DateTimeOffset cutoffUtc)
    {
        var retainedIds = new HashSet<string>(
            TryLoadRunIndex()
                .Select(summary => summary.Id)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateHistoryRoots())
        {
            PruneOrphanedHistoryDirectories(root, retainedIds, cutoffUtc);
        }

        PruneOrphanedJobSnapshots(retainedIds, cutoffUtc);
    }

    private void PruneOrphanedHistoryDirectories(string root, HashSet<string> retainedIds, DateTimeOffset cutoffUtc)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var jobId = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(jobId)
                    || _activeJobIds.ContainsKey(jobId)
                    || retainedIds.Contains(jobId))
                {
                    continue;
                }

                if (GetFileSystemTimestampUtc(directory) < cutoffUtc)
                {
                    DeleteArchivedRunFiles(jobId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to prune orphaned AutoTag history directories from {Root}.", root);
            }
        }
    }

    private void PruneOrphanedJobSnapshots(HashSet<string> retainedIds, DateTimeOffset cutoffUtc)
    {
        try
        {
            if (!Directory.Exists(_jobsDir))
            {
                return;
            }

            foreach (var jobPath in Directory.EnumerateFiles(_jobsDir, AutoTagLiterals.JsonFileSearchPattern))
            {
                var jobId = Path.GetFileNameWithoutExtension(jobPath);
                if (string.IsNullOrWhiteSpace(jobId)
                    || _activeJobIds.ContainsKey(jobId)
                    || retainedIds.Contains(jobId))
                {
                    continue;
                }

                if (GetFileSystemTimestampUtc(jobPath) < cutoffUtc)
                {
                    TryDeleteFile(jobPath);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to prune orphaned AutoTag job snapshots.");
            }
        }
    }

    private static DateTimeOffset GetFileSystemTimestampUtc(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return Directory.GetLastWriteTimeUtc(path);
            }

            if (File.Exists(path))
            {
                return File.GetLastWriteTimeUtc(path);
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // Use a current timestamp when the filesystem cannot provide one so pruning stays conservative.
        }

        return DateTimeOffset.UtcNow;
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to delete expired AutoTag history directory {Path}.", path);
            }
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to delete expired AutoTag history file {Path}.", path);
            }
        }
    }

    private List<AutoTagRunSummary> NormalizeRunIndexSummaries(IEnumerable<AutoTagRunSummary> summaries)
    {
        return summaries
            .Where(static summary => !string.IsNullOrWhiteSpace(summary.Id))
            .GroupBy(GetRunIndexGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(summary => summary.StartedAt).First())
            .OrderByDescending(static summary => summary.StartedAt)
            .ToList();
    }

    private string GetRunIndexGroupKey(AutoTagRunSummary summary)
    {
        if (string.IsNullOrWhiteSpace(summary.ResumeFromJobId))
        {
            return summary.Id;
        }

        var rootId = ResolveResumeRootJobId(summary.Id, summary.ResumeFromJobId);
        return string.IsNullOrWhiteSpace(rootId) ? summary.Id : rootId;
    }

    private string? ResolveResumeRootJobId(string jobId, string? resumeFromJobId)
    {
        var currentId = jobId;
        var parentId = resumeFromJobId;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { jobId };
        for (var depth = 0; depth < 20; depth += 1)
        {
            if (string.IsNullOrWhiteSpace(parentId) || !seen.Add(parentId))
            {
                return currentId;
            }

            currentId = parentId;
            parentId = TryReadJobResumeFromJobId(currentId);
        }

        return currentId;
    }

    private IReadOnlyList<AutoTagRunSummary> LoadArchivedRunSummaries()
    {
        try
        {
            var summaries = new Dictionary<string, AutoTagRunSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in EnumerateHistoryRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var runDir in Directory.EnumerateDirectories(root))
                {
                    var jobId = Path.GetFileName(runDir);
                    if (string.IsNullOrWhiteSpace(jobId) || summaries.ContainsKey(jobId))
                    {
                        continue;
                    }

                    var summaryPath = Path.Join(runDir, "summary.json");
                    var summary = LoadRunSummaryFromPath(summaryPath);
                    if (summary != null)
                    {
                        summaries[jobId] = summary;
                    }
                }
            }

            return summaries.Values
                .OrderByDescending(summary => summary.StartedAt)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to enumerate archived AutoTag runs.");
            return Array.Empty<AutoTagRunSummary>();
        }
    }

    private void InvalidateArchivedRunSummariesCache()
    {
        lock (_archivedRunSummariesCacheLock)
        {
            _archivedRunSummariesCache = null;
            _archivedRunSummariesCacheExpiresUtc = DateTimeOffset.MinValue;
        }
    }

    private void AppendArchivedLog(string jobId, string line)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            var archiveLock = _archiveLocks.GetOrAdd(jobId, static _ => new object());
            lock (archiveLock)
            {
                Directory.CreateDirectory(GetRunHistoryDirectory(jobId));
                File.AppendAllText(GetRunLogPath(jobId), line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to append archived AutoTag log for {JobId}", jobId);
            }
        }
    }

    private void AppendArchivedStatus(string jobId, TaggingStatusSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        try
        {
            var archiveLock = _archiveLocks.GetOrAdd(jobId, static _ => new object());
            lock (archiveLock)
            {
                Directory.CreateDirectory(GetRunHistoryDirectory(jobId));
                var json = JsonSerializer.Serialize(snapshot, _jsonCompactOptions);
                File.AppendAllText(GetRunStatusHistoryPath(jobId), json + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to append archived AutoTag status for {JobId}", jobId);
            }
        }
    }

    private void SaveRunSummary(AutoTagJob job)
    {
        try
        {
            var archiveLock = _archiveLocks.GetOrAdd(job.Id, static _ => new object());
            lock (archiveLock)
            {
                Directory.CreateDirectory(GetRunHistoryDirectory(job.Id));
                var summary = BuildRunSummary(job);
                File.WriteAllText(
                    GetRunSummaryPath(job.Id),
                    JsonSerializer.Serialize(summary, _jsonOptions),
                    new UTF8Encoding(false));
                UpdateRunIndex(summary);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to save AutoTag run summary for {JobId}", job.Id);
            }
        }
    }

    private AutoTagRunSummary BuildRunSummary(AutoTagJob job)
    {
        return new AutoTagRunSummary
        {
            Id = job.Id,
            Status = job.Status,
            StartedAt = job.StartedAt,
            FinishedAt = job.FinishedAt,
            ExitCode = job.ExitCode,
            Error = job.Error,
            Progress = job.Progress,
            OkCount = job.OkCount,
            ErrorCount = job.ErrorCount,
            ReviewCount = job.ReviewCount,
            SkippedCount = job.SkippedCount,
            RootPath = job.RootPath,
            Trigger = string.IsNullOrWhiteSpace(job.Trigger) ? AutoTagLiterals.ManualTrigger : job.Trigger,
            RunIntent = NormalizeRunIntent(job.RunIntent),
            ProfileId = job.ProfileId,
            ProfileName = job.ProfileName,
            EnhancementFeature = job.EnhancementFeature,
            EnhancementGroupId = job.EnhancementGroupId,
            CurrentPhase = job.CurrentPhase,
            CurrentBatch = job.CurrentBatch,
            BatchCount = job.BatchCount,
            BatchProcessed = job.BatchProcessed,
            BatchSize = job.BatchSize,
            ProcessedItems = job.ProcessedItems,
            TotalItems = job.TotalItems,
            TargetReason = job.TargetReason,
            TargetRequested = job.TargetRequested,
            TargetUsable = job.TargetUsable,
            EnhancementManifestPath = job.EnhancementManifestPath,
            AutoMoveSummary = job.AutoMoveSummary?.Clone(),
            ResumeFromJobId = string.IsNullOrWhiteSpace(job.ResumeFromJobId) ? null : job.ResumeFromJobId,
            HistoryDate = ResolveRunHistoryDate(job),
            LogCount = GetArchivedLogCount(job.Id, job.Logs.Count),
            StatusEntryCount = GetArchivedStatusCount(job.Id, job.StatusHistory.Count)
        };
    }

    private static DateTimeOffset? ResolveRunHistoryDate(AutoTagJob job)
    {
        if (!IsEnhancementRunIntent(job.RunIntent)
            && !IsManualEnrichmentRunIntent(job.RunIntent))
        {
            return null;
        }

        return ResolveLastActivityTimestamp(job);
    }

    private AutoTagRunSummary? LoadRunSummary(string jobId)
    {
        try
        {
            var path = ResolveRunFilePath(jobId, "summary.json");
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return LoadRunSummaryFromPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to load AutoTag run summary for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return null;
        }
    }

    private AutoTagRunSummary? LoadRunSummaryFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        var summary = JsonSerializer.Deserialize<AutoTagRunSummary>(json, _jsonOptions);
        if (summary == null)
        {
            return null;
        }

        NormalizeLegacyUserStoppedEnhancement(summary);
        if (string.IsNullOrWhiteSpace(summary.ResumeFromJobId))
        {
            summary.ResumeFromJobId = TryReadJobResumeFromJobId(summary.Id);
        }

        return summary;
    }

    private string? TryReadJobResumeFromJobId(string jobId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return null;
            }

            var path = Path.Join(_jobsDir, $"{jobId}.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return document.RootElement.TryGetProperty(nameof(AutoTagJob.ResumeFromJobId), out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read AutoTag resume source for {JobId}.", jobId);
            }
            return null;
        }
    }

    private List<string> ReadRunLogLines(string jobId)
    {
        try
        {
            var candidatePaths = EnumerateRunFileCandidates(jobId, "autotag.log").ToList();
            if (candidatePaths.Count == 0)
            {
                var fallbackPath = GetRunLogPath(jobId);
                var repairedMissingArchive = TryRepairArchivedLogsFromJob(jobId, fallbackPath);
                return repairedMissingArchive.Count > 0 ? repairedMissingArchive : new List<string>();
            }

            var archived = candidatePaths
                .Select(path => File.ReadAllLines(path, Encoding.UTF8)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList())
                .OrderByDescending(lines => lines.Count)
                .FirstOrDefault() ?? new List<string>();
            if (archived.Count > 0)
            {
                return archived;
            }

            var repaired = TryRepairArchivedLogsFromJob(jobId, GetRunLogPath(jobId));
            return repaired.Count > 0 ? repaired : archived;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read archived AutoTag logs for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new List<string>();
        }
    }

    private List<TaggingStatusSnapshot> ReadRunStatusHistory(string jobId)
    {
        try
        {
            var candidatePaths = EnumerateRunFileCandidates(jobId, "status-history.ndjson").ToList();
            if (candidatePaths.Count == 0)
            {
                var fallbackPath = GetRunStatusHistoryPath(jobId);
                var repairedMissingArchive = TryRepairArchivedStatusFromJob(jobId, fallbackPath);
                return repairedMissingArchive.Count > 0 ? repairedMissingArchive : new List<TaggingStatusSnapshot>();
            }

            List<TaggingStatusSnapshot> entries = new();
            var skippedMalformed = 0;
            foreach (var path in candidatePaths)
            {
                var (candidateEntries, candidateSkippedMalformed) = ParseStatusHistoryEntries(path);
                if (candidateEntries.Count > entries.Count)
                {
                    entries = candidateEntries;
                    skippedMalformed = candidateSkippedMalformed;
                }
            }
            if (entries.Count == 0)
            {
                var repaired = TryRepairArchivedStatusFromJob(jobId, GetRunStatusHistoryPath(jobId));
                if (repaired.Count > 0)
                {
                    return repaired;
                }
            }

            if (skippedMalformed > 0)
            {
                _logger.LogWarning(
                    "Skipped {SkippedMalformed} malformed AutoTag status entries for {JobId} while reading archive history.",
                    skippedMalformed,
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }

            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read archived AutoTag status history for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new List<TaggingStatusSnapshot>();
        }
    }

    private List<string> TryRepairArchivedLogsFromJob(string jobId, string archiveLogPath)
    {
        try
        {
            var job = LoadJob(jobId);
            var logs = (job?.Logs ?? new List<string>())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (logs.Count == 0)
            {
                return new List<string>();
            }

            var archiveDirectory = Path.GetDirectoryName(archiveLogPath);
            if (!string.IsNullOrWhiteSpace(archiveDirectory))
            {
                Directory.CreateDirectory(archiveDirectory);
            }
            File.WriteAllLines(archiveLogPath, logs, new UTF8Encoding(false));
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Recovered archived AutoTag logs for {JobId} from job snapshot ({Count} lines).",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId),
                    logs.Count);
            }
            return logs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to recover archived AutoTag logs for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new List<string>();
        }
    }

    private List<TaggingStatusSnapshot> TryRepairArchivedStatusFromJob(string jobId, string archiveStatusPath)
    {
        try
        {
            var job = LoadJob(jobId);
            var statusHistory = (job?.StatusHistory ?? new List<TaggingStatusSnapshot>()).ToList();
            if (statusHistory.Count == 0)
            {
                return new List<TaggingStatusSnapshot>();
            }

            var archiveDirectory = Path.GetDirectoryName(archiveStatusPath);
            if (!string.IsNullOrWhiteSpace(archiveDirectory))
            {
                Directory.CreateDirectory(archiveDirectory);
            }
            var statusLines = statusHistory
                .Select(entry => JsonSerializer.Serialize(entry, _jsonCompactOptions))
                .ToList();
            File.WriteAllLines(archiveStatusPath, statusLines, new UTF8Encoding(false));
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Recovered archived AutoTag status history for {JobId} from job snapshot ({Count} entries).",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId),
                    statusHistory.Count);
            }
            return statusHistory;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to recover archived AutoTag status history for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new List<TaggingStatusSnapshot>();
        }
    }

    private int GetArchivedLogCount(string jobId, int fallback)
    {
        try
        {
            var path = GetRunLogPath(jobId);
            return File.Exists(path) ? File.ReadLines(path, Encoding.UTF8).Count() : fallback;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return fallback;
        }
    }

    private int GetArchivedStatusCount(string jobId, int fallback)
    {
        try
        {
            var path = GetRunStatusHistoryPath(jobId);
            if (!File.Exists(path))
            {
                return fallback;
            }

            var (entries, _) = ParseStatusHistoryEntries(path);
            return entries.Count;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return fallback;
        }
    }

    private string GetRunHistoryDirectory(string jobId) => Path.Join(_historyDir, jobId);

    private string GetRunSummaryPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "summary.json");

    private string GetRunLogPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "autotag.log");

    private string GetRunStatusHistoryPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "status-history.ndjson");

    private string GetRunTagDiffsPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "tag-diffs.json");

    private string GetRunTagDiffCheckpointDirectory(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "tag-diff-checkpoints");

    private void SaveTagDiffCheckpoint(string jobId, string normalizedPath, AutoTagTagDiff diff)
    {
        try
        {
            var directory = GetRunTagDiffCheckpointDirectory(jobId);
            Directory.CreateDirectory(directory);
            var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath))).ToLowerInvariant();
            var path = Path.Join(directory, keyHash + ".json");
            var tempPath = path + ".tmp";
            var payload = new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase)
            {
                [normalizedPath] = diff
            };
            File.WriteAllText(tempPath, JsonSerializer.Serialize(payload, _jsonOptions), new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to persist AutoTag tag-diff checkpoint for {JobId}", jobId);
            }
        }
    }

    private void SaveArchivedTagDiffs(string jobId, Dictionary<string, AutoTagTagDiff>? tagDiffs)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(GetRunHistoryDirectory(jobId));
            var payload = (tagDiffs == null || tagDiffs.Count == 0)
                ? new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, AutoTagTagDiff>(tagDiffs, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(
                GetRunTagDiffsPath(jobId),
                JsonSerializer.Serialize(payload, _jsonOptions),
                new UTF8Encoding(false));
            TryDeleteDirectory(GetRunTagDiffCheckpointDirectory(jobId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to persist archived AutoTag tag diffs for {JobId}", jobId);
            }
        }
    }

    private Dictionary<string, AutoTagTagDiff> LoadPersistedTagDiffs(string jobId)
    {
        var resolved = new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = ResolveRunFilePath(jobId, "tag-diffs.json");
            if (!string.IsNullOrWhiteSpace(path))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, AutoTagTagDiff>>(
                    File.ReadAllText(path, Encoding.UTF8),
                    _jsonOptions);
                if (parsed != null)
                {
                    foreach (var (pathKey, diff) in parsed)
                    {
                        resolved[pathKey] = diff;
                    }
                }
            }

            var checkpointDirectory = GetRunTagDiffCheckpointDirectory(jobId);
            if (Directory.Exists(checkpointDirectory))
            {
                foreach (var checkpointPath in Directory.EnumerateFiles(checkpointDirectory, "*.json"))
                {
                    var checkpoint = JsonSerializer.Deserialize<Dictionary<string, AutoTagTagDiff>>(
                        File.ReadAllText(checkpointPath, Encoding.UTF8),
                        _jsonOptions);
                    if (checkpoint == null)
                    {
                        continue;
                    }
                    foreach (var (pathKey, diff) in checkpoint)
                    {
                        resolved[pathKey] = diff;
                    }
                }
            }
            return resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to load persisted AutoTag tag diffs for {JobId}", jobId);
            }
            return resolved;
        }
    }

    private Dictionary<string, AutoTagTagDiff> ReadRunTagDiffs(string jobId)
    {
        try
        {
            var path = ResolveRunFilePath(jobId, "tag-diffs.json");
            if (string.IsNullOrWhiteSpace(path))
            {
                return new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, AutoTagTagDiff>>(json, _jsonOptions);
            var resolved = parsed != null
                ? new Dictionary<string, AutoTagTagDiff>(parsed, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
            if (resolved.Count > 0)
            {
                return resolved;
            }

            var repaired = TryRepairArchivedTagDiffsFromJob(jobId, path);
            return repaired.Count > 0 ? repaired : resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read archived AutoTag tag diffs for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private Dictionary<string, AutoTagTagDiff> TryRepairArchivedTagDiffsFromJob(string jobId, string archiveTagDiffPath)
    {
        try
        {
            var job = LoadJob(jobId);
            if (job?.TagDiffs == null || job.TagDiffs.Count == 0)
            {
                return new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
            }

            var repaired = new Dictionary<string, AutoTagTagDiff>(job.TagDiffs, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(
                archiveTagDiffPath,
                JsonSerializer.Serialize(repaired, _jsonOptions),
                new UTF8Encoding(false));
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Recovered archived AutoTag tag diffs for {JobId} from job snapshot ({Count} entries).",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId),
                    repaired.Count);
            }
            return repaired;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to recover archived AutoTag tag diffs for {JobId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(jobId));
            }
            return new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private (List<TaggingStatusSnapshot> Entries, int SkippedMalformed) ParseStatusHistoryEntries(string path)
    {
        var entries = new List<TaggingStatusSnapshot>();
        var skippedMalformed = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8)
            .Select(static rawLine => rawLine?.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<TaggingStatusSnapshot>(line!, _jsonOptions);
                if (entry != null)
                {
                    entries.Add(entry);
                }
                else
                {
                    skippedMalformed += 1;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                RecordMalformedHistoryEntry(ref skippedMalformed);
            }
        }

        return (entries, skippedMalformed);
    }

    private static void RecordMalformedHistoryEntry(ref int skippedMalformed)
        => skippedMalformed += 1;

    private HashSet<string> EnumerateHistoryRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            try
            {
                var normalized = Path.GetFullPath(root);
                roots.Add(normalized);
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                // Ignore invalid paths.
            }
        }

        AddRoot(_historyDir);
        AddRoot(_workersHistoryDir);

        var configuredDataRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDataRoot))
        {
            AddRoot(Path.Join(configuredDataRoot, AutoTagFolderName, HistoryFolderName));
        }

        var configuredConfigRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configuredConfigRoot))
        {
            AddRoot(Path.Join(configuredConfigRoot, AutoTagFolderName, HistoryFolderName));
        }

        return roots;
    }

    private string? ResolveRunFilePath(string jobId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return EnumerateHistoryRoots()
            .Select(root => Path.Join(root, jobId, fileName))
            .FirstOrDefault(File.Exists);
    }

    private IEnumerable<string> EnumerateRunFileCandidates(string jobId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(fileName))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var normalized in EnumerateHistoryRoots()
            .Select(root => Path.Join(root, jobId, fileName))
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Where(seen.Add))
        {
            yield return normalized;
        }
    }

    private void BackfillArchivedRuns()
    {
        try
        {
            if (!Directory.Exists(_jobsDir))
            {
                return;
            }

            foreach (var jobId in Directory.EnumerateFiles(_jobsDir, AutoTagLiterals.JsonFileSearchPattern)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(jobId => !string.IsNullOrWhiteSpace(jobId)))
            {
                var currentJobId = jobId!;
                var archiveComplete = IsRunArchiveComplete(currentJobId);
                var needsRepair = archiveComplete && ShouldRepairRunArchive(currentJobId);
                if (archiveComplete && !needsRepair)
                {
                    continue;
                }

                var job = LoadJob(currentJobId);
                if (job == null)
                {
                    continue;
                }

                MaterializeRunArchive(job);
                if (needsRepair && _logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Repaired stale AutoTag archive for {JobId}.", jobId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to backfill archived AutoTag runs.");
        }
    }

    private static bool ShouldBackfillArchivedRunsOnStartup(IConfiguration configuration)
    {
        var configured = Environment.GetEnvironmentVariable("DEEZSPOTAG_AUTOTAG_BACKFILL_ON_STARTUP");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return string.Equals(configured, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(configured, "yes", StringComparison.OrdinalIgnoreCase);
        }

        return configuration.GetValue("AutoTag:ArchiveBackfillOnStartup", false);
    }

    private bool ShouldRepairRunArchive(string jobId)
    {
        try
        {
            var summary = LoadRunSummary(jobId);
            if (summary == null)
            {
                return false;
            }

            var logPath = GetRunLogPath(jobId);
            if (summary.LogCount > 0 && File.Exists(logPath) && new FileInfo(logPath).Length == 0)
            {
                return true;
            }

            var statusPath = GetRunStatusHistoryPath(jobId);
            if (summary.StatusEntryCount > 0 && File.Exists(statusPath) && new FileInfo(statusPath).Length == 0)
            {
                return true;
            }

            var tagDiffsPath = GetRunTagDiffsPath(jobId);
            if (File.Exists(tagDiffsPath))
            {
                var content = File.ReadAllText(tagDiffsPath, Encoding.UTF8).Trim();
                if (string.IsNullOrEmpty(content) || string.Equals(content, "{}", StringComparison.Ordinal))
                {
                    var job = LoadJob(jobId);
                    if (job?.TagDiffs != null && job.TagDiffs.Count > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private bool IsRunArchiveComplete(string jobId)
    {
        return File.Exists(GetRunSummaryPath(jobId))
            && File.Exists(GetRunLogPath(jobId))
            && File.Exists(GetRunStatusHistoryPath(jobId));
    }

    private void MaterializeRunArchive(AutoTagJob job)
    {
        try
        {
            var archiveLock = _archiveLocks.GetOrAdd(job.Id, static _ => new object());
            lock (archiveLock)
            {
                Directory.CreateDirectory(GetRunHistoryDirectory(job.Id));
                var summary = BuildRunSummary(job);
                File.WriteAllText(
                    GetRunSummaryPath(job.Id),
                    JsonSerializer.Serialize(summary, _jsonOptions),
                    new UTF8Encoding(false));

                File.WriteAllLines(
                    GetRunLogPath(job.Id),
                    (job.Logs ?? new List<string>()).Where(line => !string.IsNullOrWhiteSpace(line)),
                    new UTF8Encoding(false));

                var statusLines = (job.StatusHistory ?? new List<TaggingStatusSnapshot>())
                    .Select(entry => JsonSerializer.Serialize(entry, _jsonOptions))
                    .ToList();
                File.WriteAllLines(GetRunStatusHistoryPath(job.Id), statusLines, new UTF8Encoding(false));
                SaveArchivedTagDiffs(job.Id, job.TagDiffs);
                UpdateRunIndex(summary, force: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to materialize AutoTag run archive for {JobId}", job.Id);
            }
        }
    }

    private void SaveJob(AutoTagJob job)
    {
        try
        {
            var path = Path.Join(_jobsDir, $"{job.Id}.json");
            var json = JsonSerializer.Serialize(CreateJobPersistenceSnapshot(job), _jsonOptions);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            SaveRunSummary(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to persist AutoTag job {JobId}", job.Id);
            }
        }
    }

    private AutoTagJob? LoadJob(string id)
    {
        try
        {
            var path = Path.Join(_jobsDir, $"{id}.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var utf8 = File.ReadAllBytes(path);
            if (utf8.Length >= 3
                && utf8[0] == 0xEF
                && utf8[1] == 0xBB
                && utf8[2] == 0xBF)
            {
                var noBom = new byte[utf8.Length - 3];
                Buffer.BlockCopy(utf8, 3, noBom, 0, noBom.Length);
                utf8 = noBom;
            }

            var job = JsonSerializer.Deserialize<AutoTagJob>(utf8, _jsonOptions);
            if (job != null)
            {
                foreach (var (pathKey, diff) in LoadPersistedTagDiffs(job.Id))
                {
                    job.TagDiffs[pathKey] = diff;
                }
            }
            return job;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load AutoTag job JobId");
            return null;
        }
    }

    private void NormalizeLoadedJobState(AutoTagJob job)
    {
        job.Trigger = NormalizeRunTrigger(job.Trigger);
        job.RunIntent = NormalizeRunIntent(job.RunIntent);
        NormalizeLegacyUserStoppedEnhancement(job);
        if (job.LastActivityAt <= DateTimeOffset.MinValue)
        {
            job.LastActivityAt = ResolveLastActivityTimestamp(job);
        }

        if (!string.Equals(job.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_activeJobIds.ContainsKey(job.Id))
        {
            return;
        }

        job.Status = AutoTagLiterals.InterruptedStatus;
        job.ExitCode = 1;
        job.FinishedAt ??= DateTimeOffset.UtcNow;
        job.Error ??= "AutoTag job was interrupted by an application restart; resume is available.";
        SaveJob(job);
        RecordStaleRecoveryPending(job);
    }

    private static void NormalizeLegacyUserStoppedEnhancement(AutoTagRunState run)
    {
        if (!IsEnhancementRunIntent(run.RunIntent)
            && !IsManualEnrichmentRunIntent(run.RunIntent))
        {
            return;
        }

        if (!string.Equals(NormalizeRunTrigger(run.Trigger), AutoTagLiterals.ManualTrigger, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(run.Status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            || !IsLegacyUserInterruptedStopMessage(run.Error))
        {
            return;
        }

        run.Status = AutoTagLiterals.CanceledStatus;
        run.Error = "Stopped by user.";
    }

    private static bool IsLegacyUserInterruptedStopMessage(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        var normalized = error.Trim();
        return string.Equals(normalized, "Interrupted by user. Resume is available.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Interrupted. Resume is available.", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset ResolveLastActivityTimestamp(AutoTagJob job)
    {
        var timestamp = job.StartedAt;
        if (job.ResumeCheckpoint?.UpdatedAt > timestamp)
        {
            timestamp = job.ResumeCheckpoint.UpdatedAt;
        }

        var latestStatusTimestamp = job.StatusHistory
            .Select(static entry => entry.Timestamp)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (latestStatusTimestamp > timestamp)
        {
            timestamp = latestStatusTimestamp;
        }

        if (job.FinishedAt > timestamp)
        {
            timestamp = job.FinishedAt.Value;
        }

        return timestamp;
    }

    public async Task RecoverStuckJobsAsync(
        TimeSpan staleWindow,
        bool restartStalePersistedJobs,
        CancellationToken cancellationToken)
    {
        if (staleWindow <= TimeSpan.Zero)
        {
            staleWindow = TimeSpan.FromMinutes(30);
        }

        await StopActiveJobsWithoutProgressAsync(staleWindow, cancellationToken);
        await RecoverPersistedRunningJobsAsync(staleWindow, restartStalePersistedJobs, cancellationToken);
    }

    private async Task StopActiveJobsWithoutProgressAsync(TimeSpan staleWindow, CancellationToken cancellationToken)
    {
        foreach (var jobId in _activeJobIds.Keys.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_jobs.TryGetValue(jobId, out var job) || !IsRunningStatus(job.Status))
            {
                continue;
            }

            var idleFor = DateTimeOffset.UtcNow - GetLastProgressTimestamp(job);
            if (idleFor < staleWindow)
            {
                continue;
            }

            if (!_stuckRecoveryJobs.TryAdd(job.Id, 0))
            {
                continue;
            }

            AppendLog(
                job,
                $"stuck watchdog: no AutoTag progress for {FormatDuration(idleFor)}; canceling active run so it can resume.");

            try
            {
                if (await StopJobAsync(job.Id, "recovery")
                    && await WaitForJobToLeaveActiveSetAsync(job.Id, TimeSpan.FromSeconds(30), cancellationToken))
                {
                    await TryAutoResumeRecoveredJobAsync(job, cancellationToken);
                }
            }
            finally
            {
                _stuckRecoveryJobs.TryRemove(job.Id, out _);
            }
        }
    }

    private async Task RecoverPersistedRunningJobsAsync(
        TimeSpan staleWindow,
        bool restartStalePersistedJobs,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_jobsDir))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_jobsDir, AutoTagLiterals.JsonFileSearchPattern).OrderByDescending(File.GetLastWriteTimeUtc).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_activeJobIds.IsEmpty)
            {
                return;
            }

            var jobId = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(jobId)
                || _activeJobIds.ContainsKey(jobId)
                || !_stuckRecoveryJobs.TryAdd(jobId, 0))
            {
                continue;
            }

            try
            {
                var job = _jobs.TryGetValue(jobId, out var cachedJob) ? cachedJob : LoadJob(jobId);
                if (job == null || !IsRunningStatus(job.Status) || _activeJobIds.ContainsKey(job.Id))
                {
                    continue;
                }

                var idleFor = DateTimeOffset.UtcNow - GetLastProgressTimestamp(job, path);
                if (idleFor < staleWindow)
                {
                    continue;
                }

                await RecoverPersistedRunningJobAsync(job, idleFor, restartStalePersistedJobs, cancellationToken);
            }
            finally
            {
                _stuckRecoveryJobs.TryRemove(jobId, out _);
            }
        }
    }

    private async Task RecoverPersistedRunningJobAsync(
        AutoTagJob job,
        TimeSpan idleFor,
        bool restartStalePersistedJobs,
        CancellationToken cancellationToken)
    {
        job.Trigger = NormalizeRunTrigger(job.Trigger);
        job.RunIntent = NormalizeRunIntent(job.RunIntent);
        job.Status = AutoTagLiterals.InterruptedStatus;
        job.ExitCode = 1;
        job.FinishedAt ??= DateTimeOffset.UtcNow;
        job.Error = $"AutoTag job had no progress for {FormatDuration(idleFor)} and was recovered as interrupted.";
        SaveJob(job);
        AppendLog(job, "stuck watchdog: recovered stale running job; resume checkpoint preserved.");
        AppendActivityLog(job.Id, "autotag interrupted by stuck watchdog");

        if (!restartStalePersistedJobs)
        {
            RecordStaleRecoveryPending(job);
            return;
        }

        await TryAutoResumeRecoveredJobAsync(job, cancellationToken);
    }

    private async Task TryAutoResumeRecoveredJobAsync(AutoTagJob job, CancellationToken cancellationToken)
    {
        if (job.ResumeCheckpoint == null)
        {
            AppendLog(job, "stuck watchdog: auto-resume skipped because no resume checkpoint is available.");
            RecordStaleRecoveryPending(job);
            return;
        }

        if (string.IsNullOrWhiteSpace(job.RootPath))
        {
            AppendLog(job, "stuck watchdog: auto-resume skipped because the job root path is missing.");
            RecordStaleRecoveryPending(job);
            return;
        }

        var runtimeConfigPath = TryFindRuntimeConfigPath(job.Id, "base");
        if (string.IsNullOrWhiteSpace(runtimeConfigPath) || !File.Exists(runtimeConfigPath))
        {
            AppendLog(job, "stuck watchdog: auto-resume skipped because the runtime config was not found.");
            RecordStaleRecoveryPending(job);
            return;
        }

        try
        {
            var configJson = await File.ReadAllTextAsync(runtimeConfigPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(configJson))
            {
                AppendLog(job, "stuck watchdog: auto-resume skipped because the runtime config is empty.");
                RecordStaleRecoveryPending(job);
                return;
            }

            AppendLog(job, "stuck watchdog: auto-resume starting from preserved checkpoint.");
            var resumed = await StartJob(
                job.RootPath!,
                configJson,
                new StartJobOptions(
                    Trigger: job.Trigger,
                    ProfileId: job.ProfileId,
                    ProfileName: job.ProfileName,
                    RunIntent: job.RunIntent));
            if (resumed == null)
            {
                AppendLog(job, "stuck watchdog: auto-resume skipped because downloads are active.");
                return;
            }

            AppendLog(job, $"stuck watchdog: auto-resume created job {resumed.Id} (status={resumed.Status}).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag stuck watchdog failed to auto-resume job {JobId}.", job.Id);
            AppendLog(job, $"stuck watchdog: auto-resume failed: {ex.Message}");
            RecordStaleRecoveryPending(job);
        }
    }

    private async Task<bool> WaitForJobToLeaveActiveSetAsync(
        string jobId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_activeJobIds.ContainsKey(jobId))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        if (_jobs.TryGetValue(jobId, out var job))
        {
            AppendLog(job, "stuck watchdog: auto-resume deferred because the canceled run is still active.");
        }

        return false;
    }

    private static bool IsRunningStatus(string? status)
        => string.Equals(status?.Trim(), AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset GetLastProgressTimestamp(AutoTagJob job, string? jobPath = null)
    {
        var timestamp = job.StartedAt;
        if (job.LastActivityAt > timestamp)
        {
            timestamp = job.LastActivityAt;
        }

        if (job.ResumeCheckpoint?.UpdatedAt > timestamp)
        {
            timestamp = job.ResumeCheckpoint.UpdatedAt;
        }

        var lastStatusTimestamp = job.StatusHistory
            .Select(entry => entry.Timestamp)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (lastStatusTimestamp > timestamp)
        {
            timestamp = lastStatusTimestamp;
        }

        if (!string.IsNullOrWhiteSpace(jobPath) && File.Exists(jobPath))
        {
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(jobPath), TimeSpan.Zero);
            if (modified > timestamp)
            {
                timestamp = modified;
            }
        }

        return timestamp;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:0.0}h";
        }

        return $"{Math.Max(1, duration.TotalMinutes):0}m";
    }

    private void RecordStaleRecoveryPending(AutoTagJob job)
    {
        AppendLog(job, "stale recovery: auto-move disabled; file finalization remains owned by its authoritative pipeline");
    }

    private string? TryFindRuntimeConfigPath(string jobId, string stage)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(stage) || !Directory.Exists(_runtimeConfigDir))
            {
                return null;
            }

            var stageToken = NormalizeConfigKeyForRedaction(stage);
            var pattern = $"autotag-{jobId}-{stageToken}-*.json";
            return Directory
                .EnumerateFiles(_runtimeConfigDir, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to locate runtime config for stale recovery job {JobId}.", jobId);
            }
            return null;
        }
    }
}
