using System.Collections.Concurrent;
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
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services.CoverPort;
using DeezSpoTag.Web.Services.AutoTag;

namespace DeezSpoTag.Web.Services;

internal static class AutoTagLiterals
{
    internal const string QueuedStatus = "queued";
    internal const string RunningStatus = "running";
    internal const string TaggingStatus = "tagging";
    internal const string OkStatus = "ok";
    internal const string TaggedStatus = "tagged";
    internal const string SkippedStatus = "skipped";
    internal const string ErrorStatus = "error";
    internal const string ManualTrigger = "manual";
    internal const string AutomationTrigger = "automation";
    internal const string ScheduleTrigger = "schedule";
    internal const string RunIntentDefault = "default";
    internal const string RunIntentDownloadEnrichment = "download_enrichment";
    internal const string RunIntentEnhancementOnly = "enhancement_only";
    internal const string RunIntentEnhancementRecentDownloads = "enhancement_recent_downloads";
    internal const string CanceledStatus = "canceled";
    internal const string InterruptedStatus = "interrupted";
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
    internal const string IncludeSubfoldersKey = "includeSubfolders";
    internal const string ArtistTag = "artist";
    internal const string ReleaseDateTag = "releaseDate";
    internal const string LanguageTag = "language";
    internal const string DownloadTagsKey = "downloadTags";
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
    public int SkippedCount { get; set; }
    public string? RootPath { get; set; }
    public string Trigger { get; set; } = AutoTagLiterals.ManualTrigger;
    public string RunIntent { get; set; } = AutoTagLiterals.RunIntentDefault;
    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }
    public AutoTagMoveSummary? AutoMoveSummary { get; set; }
}

public class AutoTagJob : AutoTagRunState
{
    public string? CurrentPlatform { get; set; }
    public TaggingStatusWrap? LastStatus { get; set; }
    public List<TaggingStatusSnapshot> StatusHistory { get; } = new();
    public List<string> Logs { get; } = new();
    public List<string> StartedPlatforms { get; } = new();
    public Dictionary<string, AutoTagTagDiff> TagDiffs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public AutoTagResumeCheckpoint? ResumeCheckpoint { get; set; }
    public string? ResumeFromJobId { get; set; }
}

public sealed class AutoTagRunSummary : AutoTagRunState
{
    public int LogCount { get; set; }
    public int StatusEntryCount { get; set; }
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

public class AutoTagService
{
    private readonly record struct EnrichmentBuildContext(string RunIntent, string JobId);
    private readonly record struct EnhancementBuildContext(string RunIntent, string JobId);
    private readonly record struct AutoMoveExecutionResult(bool Completed, AutoTagMoveSummary Summary);
    private sealed record ResumeCheckpointSeed(string SourceJobId, AutoTagResumeCheckpoint Checkpoint);

    private readonly ConcurrentDictionary<string, AutoTagJob> _jobs = new();
    private readonly ConcurrentDictionary<string, byte> _activeJobIds = new();
    private readonly ConcurrentDictionary<string, string> _activeJobStages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastActivityLines = new();
    private readonly ConcurrentDictionary<string, byte> _staleRecoveryCleanupJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AutoTagService> _logger;
    private readonly LibraryConfigStore _activityLog;
    private readonly AuthenticatedDeezerService _deezerAuth;
    private readonly AutoTagMetadataService _metadataService;
    private readonly DeezSpoTag.Web.Services.AutoTag.IAutoTagRunner _autoTagRunner;
    private readonly AutoTagLibraryOrganizer _libraryOrganizer;
    private readonly AutoTagDownloadMoveService _downloadMoveService;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly QuickTagService _quickTagService;
    private readonly PlatformAuthService _platformAuthService;
    private readonly PlexApiClient _plexApiClient;
    private readonly SpotifyBlobService _spotifyBlobService;
    private readonly DeezSpoTag.Services.Settings.DeezSpoTagSettingsService _settingsService;
    private readonly LibraryRepository _libraryRepository;
    private readonly LibraryScanRunner _libraryScanRunner;
    private readonly QualityScannerService _qualityScannerService;
    private readonly DuplicateCleanerService _duplicateCleanerService;
    private readonly LyricsRefreshQueueService _lyricsRefreshQueueService;
    private readonly CoverLibraryMaintenanceService _coverMaintenanceService;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly UserPreferencesStore _userPreferencesStore;
    private readonly string _jobsDir;
    private readonly string _historyDir;
    private readonly string _workersHistoryDir;
    private readonly string _runtimeConfigDir;
    private readonly string _lastConfigPath;
    private readonly string _lastJobPath;
    private readonly bool _disableAutoMove;
    private readonly TimeSpan _organizerCooldown = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, object> _archiveLocks = new(StringComparer.OrdinalIgnoreCase);
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
        ["title"] = "title",
        [AutoTagLiterals.ArtistTag] = AutoTagLiterals.ArtistTag,
        ["artists"] = AutoTagLiterals.ArtistTag,
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
        ["catalogNumber"] = "catalogNumber",
        ["trackNumber"] = "trackNumber",
        ["discNumber"] = "discNumber",
        ["duration"] = "duration",
        ["trackTotal"] = "trackTotal",
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
        ["involvedPeople"] = "involvedPeople",
        ["source"] = "source",
        ["rating"] = "rating",
        [AutoTagLiterals.LanguageTag] = AutoTagLiterals.LanguageTag
    };
    private static readonly HashSet<string> EnrichmentStageAllowedKeys = BuildStageAllowedKeys(includeSkipTagged: false, includeConflictResolution: true, includeTargetFiles: false);
    private static readonly HashSet<string> EnhancementStageAllowedKeys = BuildStageAllowedKeys(includeSkipTagged: true, includeConflictResolution: false, includeTargetFiles: true);
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
    private static readonly string[] DiffMetaKeys =
    {
        "title",
        "artists",
        "album",
        "albumArtists",
        "composers",
        "trackNumber",
        "trackTotal",
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
    private sealed record PlatformTagCapabilities(HashSet<string> SupportedTags, bool RequiresAuth);
    private sealed record AutoTagStageConfig(string Name, string ConfigPath, int TagCount, string ConfigHash);
    private sealed class FileTagOutcome
    {
        public bool Seen { get; set; }
        public bool Tagged { get; set; }
        public bool SkippedAlreadyTagged { get; set; }
    }

    public sealed class AutoTagServiceCollaborators
    {
        public required LibraryConfigStore ActivityLog { get; init; }
        public required AuthenticatedDeezerService DeezerAuth { get; init; }
        public required AutoTagMetadataService MetadataService { get; init; }
        public required DeezSpoTag.Web.Services.AutoTag.IAutoTagRunner AutoTagRunner { get; init; }
        public required AutoTagLibraryOrganizer LibraryOrganizer { get; init; }
        public required AutoTagDownloadMoveService DownloadMoveService { get; init; }
        public required DownloadQueueRepository QueueRepository { get; init; }
        public required QuickTagService QuickTagService { get; init; }
        public required PlatformAuthService PlatformAuthService { get; init; }
        public required PlexApiClient PlexApiClient { get; init; }
        public required SpotifyBlobService SpotifyBlobService { get; init; }
        public required DeezSpoTag.Services.Settings.DeezSpoTagSettingsService SettingsService { get; init; }
        public required LibraryRepository LibraryRepository { get; init; }
        public required LibraryScanRunner LibraryScanRunner { get; init; }
        public required QualityScannerService QualityScannerService { get; init; }
        public required DuplicateCleanerService DuplicateCleanerService { get; init; }
        public required LyricsRefreshQueueService LyricsRefreshQueueService { get; init; }
        public required CoverLibraryMaintenanceService CoverMaintenanceService { get; init; }
        public required AutoTagProfileResolutionService ProfileResolutionService { get; init; }
        public required UserPreferencesStore UserPreferencesStore { get; init; }
    }

    public event Action<AutoTagJob>? JobCompleted;

    public AutoTagService(
        IWebHostEnvironment env,
        ILogger<AutoTagService> logger,
        AutoTagServiceCollaborators collaborators)
    {
        _logger = logger;
        _activityLog = collaborators.ActivityLog;
        _deezerAuth = collaborators.DeezerAuth;
        _metadataService = collaborators.MetadataService;
        _autoTagRunner = collaborators.AutoTagRunner;
        _libraryOrganizer = collaborators.LibraryOrganizer;
        _downloadMoveService = collaborators.DownloadMoveService;
        _queueRepository = collaborators.QueueRepository;
        _quickTagService = collaborators.QuickTagService;
        _platformAuthService = collaborators.PlatformAuthService;
        _plexApiClient = collaborators.PlexApiClient;
        _spotifyBlobService = collaborators.SpotifyBlobService;
        _settingsService = collaborators.SettingsService;
        _libraryRepository = collaborators.LibraryRepository;
        _libraryScanRunner = collaborators.LibraryScanRunner;
        _qualityScannerService = collaborators.QualityScannerService;
        _duplicateCleanerService = collaborators.DuplicateCleanerService;
        _lyricsRefreshQueueService = collaborators.LyricsRefreshQueueService;
        _coverMaintenanceService = collaborators.CoverMaintenanceService;
        _profileResolutionService = collaborators.ProfileResolutionService;
        _userPreferencesStore = collaborators.UserPreferencesStore;
        var appDataRoot = AppDataPaths.GetDataRoot(env);
        var autoTagRoot = Path.Join(appDataRoot, AutoTagFolderName);
        _jobsDir = Path.Join(autoTagRoot, "jobs");
        _historyDir = Path.Join(autoTagRoot, HistoryFolderName);
        var workerDataRoot = AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir());
        _workersHistoryDir = Path.Join(workerDataRoot, AutoTagFolderName, HistoryFolderName);
        _runtimeConfigDir = Path.Join(autoTagRoot, "runtime");
        _lastConfigPath = Path.Join(autoTagRoot, "last-config.json");
        _lastJobPath = Path.Join(autoTagRoot, "last-job.json");
        Directory.CreateDirectory(autoTagRoot);
        Directory.CreateDirectory(_jobsDir);
        Directory.CreateDirectory(_historyDir);
        Directory.CreateDirectory(_runtimeConfigDir);
        _disableAutoMove = ResolveDisableAutoMove();
        BackfillArchivedRuns();
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

        foreach (var activeJobId in _activeJobIds.Keys)
        {
            if (_jobs.TryGetValue(activeJobId, out var activeJob)
                && string.Equals(activeJob.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase)
                && IsEnhancementRunIntent(activeJob.RunIntent))
            {
                jobId = activeJobId;
                return true;
            }
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

    public async Task<AutoTagJob> StartJob(
        string path,
        string configJson,
        string trigger = AutoTagLiterals.ManualTrigger,
        TechnicalTagSettings? technicalOverride = null,
        string? profileId = null,
        string? profileName = null,
        string? runIntent = null)
    {
        var normalizedPath = NormalizePathForJob(path);
        var normalizedTrigger = NormalizeRunTrigger(trigger);
        var normalizedRunIntent = NormalizeRunIntent(runIntent);
        var resumeSeed = TryResolveResumeCheckpointSeed(normalizedPath, normalizedRunIntent, profileId);
        var isEnhancementResume = resumeSeed != null && IsEnhancementRunIntent(normalizedRunIntent);
        var resumeSourceJob = isEnhancementResume ? GetJob(resumeSeed!.SourceJobId) : null;
        var resumedJobId = isEnhancementResume ? resumeSeed!.SourceJobId : Guid.NewGuid().ToString("N");
        var resumedStartedAt = isEnhancementResume
            ? (resumeSourceJob?.StartedAt ?? DateTimeOffset.UtcNow)
            : DateTimeOffset.UtcNow;

        var blockedByActiveDownloads = await TryCreateBlockedJobForActiveDownloadsAsync(
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            profileId,
            profileName);
        if (blockedByActiveDownloads != null)
        {
            return blockedByActiveDownloads;
        }

        var blockedByScope = await TryCreateBlockedJobForScopePolicyAsync(
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            profileId,
            profileName);
        if (blockedByScope != null)
        {
            return blockedByScope;
        }

        if (!HasEligibleInputFiles(normalizedPath, configJson))
        {
            return CreateNotStartedJob(
                "No eligible audio files were found for this run.",
                normalizedPath,
                normalizedTrigger,
                normalizedRunIntent,
                profileId,
                profileName);
        }

        var job = new AutoTagJob
        {
            Id = resumedJobId,
            Status = AutoTagLiterals.RunningStatus,
            StartedAt = resumedStartedAt,
            RootPath = normalizedPath,
            Trigger = normalizedTrigger,
            RunIntent = normalizedRunIntent,
            ProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim(),
            ProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName.Trim(),
            ResumeCheckpoint = resumeSeed?.Checkpoint,
            ResumeFromJobId = isEnhancementResume ? null : resumeSeed?.SourceJobId
        };
        if (isEnhancementResume && resumeSourceJob != null)
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
        var runtimeConfigJson = SanitizeConfigJson(configJson);
        runtimeConfigJson = await InjectDeezerAuthAsync(runtimeConfigJson);
        runtimeConfigJson = InjectDeezerDownloadOptions(runtimeConfigJson);
        runtimeConfigJson = await InjectPlatformDefaultsAsync(runtimeConfigJson);
        runtimeConfigJson = await InjectPlatformAuthAsync(runtimeConfigJson);
        runtimeConfigJson = InjectRunTrigger(runtimeConfigJson, normalizedTrigger);
        runtimeConfigJson = InjectTechnicalSettings(runtimeConfigJson, technicalOverride, job.ProfileId, job.ProfileName);
        var persistedConfigJson = RedactSensitiveConfigJson(runtimeConfigJson);
        await TrySeedSpotifyTokenCacheAsync();
        var runtimeConfigPath = WriteRuntimeConfigFile(job.Id, "base", runtimeConfigJson);
        TrySaveLastConfig(persistedConfigJson);

        _ = Task.Run(() => RunJobAsync(job, normalizedPath, runtimeConfigPath));

        return job;
    }

    private async Task<AutoTagJob?> TryCreateBlockedJobForActiveDownloadsAsync(
        string normalizedPath,
        string normalizedTrigger,
        string normalizedRunIntent,
        string? profileId,
        string? profileName)
    {
        if (!await _queueRepository.HasActiveDownloadsAsync())
        {
            return null;
        }

        var blockedJob = CreateBlockedJob(
            "Downloads active; AutoTag skipped.",
            normalizedPath,
            normalizedTrigger,
            normalizedRunIntent,
            profileId,
            profileName);
        AppendActivityLog(blockedJob.Id, "autotag skipped: downloads active");
        _logger.LogInformation("AutoTag skipped: downloads active.");
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
            normalizedRunIntent,
            normalizedTrigger,
            normalizedPath,
            runIntentScopeError);
        return blockedJob;
    }

    private static void HydrateResumeJob(AutoTagJob target, AutoTagJob source)
    {
        target.OkCount = source.OkCount;
        target.ErrorCount = source.ErrorCount;
        target.SkippedCount = source.SkippedCount;
        target.Progress = source.Progress;
        target.ExitCode = null;
        target.Error = null;
        if (source.Logs.Count > 0)
        {
            target.Logs.AddRange(source.Logs);
        }

        if (source.StatusHistory.Count > 0)
        {
            target.StatusHistory.AddRange(source.StatusHistory);
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

        _jobs[blockedJob.Id] = blockedJob;
        SaveJob(blockedJob);
        TrySaveLastJobId(blockedJob.Id);
        return blockedJob;
    }

    private static AutoTagJob CreateNotStartedJob(
        string message,
        string rootPath,
        string trigger,
        string runIntent,
        string? profileId,
        string? profileName)
    {
        return new AutoTagJob
        {
            Id = string.Empty,
            Status = "skipped",
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            Error = message,
            RootPath = rootPath,
            Trigger = trigger,
            RunIntent = runIntent,
            ProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim(),
            ProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName.Trim()
        };
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
                : _activityLog.GetFolders();
            return LibraryFolderRootResolver.ResolveAccessibleRoots(folders);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LibraryFolderRootResolver.ResolveAccessibleRoots(_activityLog.GetFolders());
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
        AutoTagJob? latestMatchingJob = null;
        foreach (var path in Directory.EnumerateFiles(_jobsDir, "*.json"))
        {
            var job = TryLoadResumeScopeJob(path);
            if (job == null
                || !IsResumeScopeMatch(job, normalizedPath, normalizedRunIntent, normalizedProfileId))
            {
                continue;
            }

            if (latestMatchingJob == null || job.StartedAt >= latestMatchingJob.StartedAt)
            {
                latestMatchingJob = job;
            }
        }

        return latestMatchingJob;
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

    private static ResumeCheckpointSeed? BuildResumeCheckpointSeed(AutoTagJob job)
    {
        var checkpoint = CloneResumeCheckpoint(job.ResumeCheckpoint);
        return checkpoint == null
            ? null
            : new ResumeCheckpointSeed(job.Id, checkpoint);
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
        if (!string.Equals(status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
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
        catch
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
            _jobs[id] = loaded;
        }

        return loaded;
    }

    public AutoTagJob? GetLatestJob()
    {
        var jobId = TryGetLastJobId();
        return string.IsNullOrWhiteSpace(jobId) ? null : GetJob(jobId);
    }

    public IReadOnlyList<AutoTagRunDaySummary> GetArchivedRunCalendar(int year, int month)
    {
        var summaries = GetArchivedRunSummaries()
            .Where(summary => summary.StartedAt.Year == year && summary.StartedAt.Month == month)
            .OrderBy(summary => summary.StartedAt)
            .ToList();

        return summaries
            .GroupBy(summary => summary.StartedAt.ToString("yyyy-MM-dd"))
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
            .Where(summary => string.Equals(summary.StartedAt.ToString("yyyy-MM-dd"), token, StringComparison.Ordinal))
            .OrderByDescending(summary => summary.StartedAt)
            .ToList();
    }

    public AutoTagRunArchive? GetArchivedRun(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var summary = LoadRunSummary(id);
        if (summary == null)
        {
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
        var baseSnapshot = targetIndex > 0
            ? completed[targetIndex - 1].After
            : stored.Before ?? target.Before;
        if (isFinal)
        {
            baseSnapshot = stored.Before ?? completed.FirstOrDefault()?.Before ?? baseSnapshot;
        }

        var basePlatform = "original";
        if (!isFinal && targetIndex > 0)
        {
            basePlatform = completed[targetIndex - 1].Platform;
        }

        var selected = new AutoTagTagDiff
        {
            Path = stored.Path,
            LastPlatform = stored.LastPlatform,
            TargetPlatform = target.Platform,
            IsFinalPlatformDiff = isFinal,
            BasePlatform = basePlatform,
            Before = baseSnapshot,
            After = target.After,
            PlatformDiffs = completed.Select(ClonePlatformDiff).ToList()
        };

        if (isFinal && selected.After != null)
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

    public async Task<bool> StopJobAsync(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            var loaded = LoadJob(id);
            if (loaded == null)
            {
                return false;
            }
            job = loaded;
            _jobs[id] = job;
        }

        var stopped = await _autoTagRunner.StopAsync(id, CancellationToken.None);
        if (stopped)
        {
            var interruptedStatus = IsEnhancementRunIntent(job.RunIntent)
                ? AutoTagLiterals.InterruptedStatus
                : AutoTagLiterals.CanceledStatus;
            job.Status = interruptedStatus;
            job.Error = IsEnhancementRunIntent(job.RunIntent)
                ? "Interrupted by user. Resume is available."
                : "Stopped by user.";
            SaveJob(job);
            AppendActivityLog(
                job.Id,
                string.Equals(interruptedStatus, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
                    ? "autotag interrupted by user"
                    : "autotag canceled by user");
        }

        return stopped;
    }

    private async Task RunJobAsync(AutoTagJob job, string path, string configPath)
    {
        var fileOutcomes = new Dictionary<string, FileTagOutcome>(StringComparer.OrdinalIgnoreCase);
        var runtimeConfigPaths = InitializeRuntimeConfigPaths(configPath);

        try
        {
            var stages = await BuildStageConfigsAsync(job, configPath);
            var includesEnhancementStage = stages.Any(stage =>
                string.Equals(stage.Name, AutoTagLiterals.EnhancementStage, StringComparison.OrdinalIgnoreCase));
            RegisterStageRuntimeConfigPaths(runtimeConfigPaths, stages);
            if (TryMarkNoStagesConfigured(job, stages))
            {
                return;
            }

            var success = await ExecuteStagesAsync(job, stages, path, fileOutcomes);

            if (!string.Equals(job.Status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(job.Status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase))
            {
                job.Status = success ? AutoTagLiterals.CompletedStatus : AutoTagLiterals.FailedStatus;
            }
            if (string.Equals(job.Status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase))
            {
                job.ResumeCheckpoint = null;
                job.ResumeFromJobId = null;
            }
            job.ExitCode = success ? 0 : 1;
            job.FinishedAt = DateTimeOffset.UtcNow;
            AppendPlatformSummary(job);
            SaveJob(job);
            AppendActivityLog(job.Id, $"autotag finished: status={job.Status}");

            if (!string.Equals(job.Status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(job.Status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase))
            {
                await RunSuccessPostProcessingAsync(job, path, configPath, includesEnhancementStage, fileOutcomes);
            }

            NotifyCompleted(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await HandleRunJobFailureAsync(job, ex, path, configPath, fileOutcomes);
        }
        finally
        {
            CleanupRuntimeConfigFiles(runtimeConfigPaths);
            _activeJobStages.TryRemove(job.Id, out _);
            _activeJobIds.TryRemove(job.Id, out _);
        }
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

    private bool TryMarkNoStagesConfigured(AutoTagJob job, IReadOnlyCollection<AutoTagStageConfig> stages)
    {
        if (stages.Count > 0)
        {
            return false;
        }

        job.Status = AutoTagLiterals.FailedStatus;
        job.Error = "No AutoTag stages configured.";
        job.ExitCode = 1;
        job.FinishedAt = DateTimeOffset.UtcNow;
        job.ResumeCheckpoint = null;
        job.ResumeFromJobId = null;
        SaveJob(job);
        AppendActivityLog(job.Id, "autotag failed: no stages configured");
        NotifyCompleted(job);
        return true;
    }

    private async Task<bool> ExecuteStagesAsync(
        AutoTagJob job,
        IReadOnlyList<AutoTagStageConfig> stages,
        string path,
        Dictionary<string, FileTagOutcome> fileOutcomes)
    {
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var stageResult = await ExecuteSingleStageAsync(job, stage, index, stages.Count, path, fileOutcomes);
            if (!stageResult.Success)
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct StageExecutionResult(bool Success);

    private async Task<StageExecutionResult> ExecuteSingleStageAsync(
        AutoTagJob job,
        AutoTagStageConfig stage,
        int stageIndex,
        int totalStages,
        string path,
        Dictionary<string, FileTagOutcome> fileOutcomes)
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

        try
        {
            var result = await _autoTagRunner.RunAsync(
                job.Id,
                path,
                stage.ConfigPath,
                status => UpdateStatus(job, status, stage.Name, stage.ConfigHash, stageIndex, totalStages, fileOutcomes),
                line => AppendLog(job, line),
                resumeCursor,
                CancellationToken.None);
            if (string.Equals(result.Error, "stopped", StringComparison.OrdinalIgnoreCase))
            {
                return HandleStoppedStage(job);
            }

            if (!result.Success)
            {
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

    private static StageExecutionResult HandleStoppedStage(AutoTagJob job)
    {
        var interrupted = IsEnhancementRunIntent(job.RunIntent);
        job.Status = interrupted
            ? AutoTagLiterals.InterruptedStatus
            : AutoTagLiterals.CanceledStatus;
        job.Error = interrupted
            ? "Interrupted by user. Resume is available."
            : "Stopped by user.";
        return new StageExecutionResult(false);
    }

    private async Task RunSuccessPostProcessingAsync(
        AutoTagJob job,
        string path,
        string configPath,
        bool includesEnhancementStage,
        Dictionary<string, FileTagOutcome> fileOutcomes)
    {
        var (taggedFiles, failedFiles) = BuildMoveFileSets(fileOutcomes);
        AppendLog(job, "tagging completed, auto-move starting");
        var autoMove = await MoveAfterAutoTagAsync(job, path, configPath, taggedFiles, failedFiles);
        AppendLog(job, "auto-move completed, organizer starting");
        await OrganizeJobAsync(job, path, configPath);
        await RunIntegratedEnhancementWorkflowsAsync(
            job,
            path,
            configPath,
            includesEnhancementStage,
            CancellationToken.None);
        var plexTriggeredByEnhancement = await TriggerPlexMetadataRefreshAfterEnhancementAsync(
            job,
            includesEnhancementStage,
            CancellationToken.None);
        var plexRefreshRequestedAfterMove = false;
        if (autoMove.Completed && !plexTriggeredByEnhancement)
        {
            plexRefreshRequestedAfterMove = await TriggerPlexScanAfterMoveAsync(job, CancellationToken.None);
        }
        await TriggerLibraryScanAfterAutoMovePlexRefreshRequestedAsync(
            job,
            autoMove.Completed && (plexTriggeredByEnhancement || plexRefreshRequestedAfterMove),
            CancellationToken.None);
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

        var (taggedFiles, failedFiles) = BuildMoveFileSets(fileOutcomes);
        AppendLog(job, "tagging failed, auto-move starting");
        var autoMove = await MoveAfterAutoTagAsync(job, path, configPath, taggedFiles, failedFiles);
        AppendLog(job, "auto-move completed, organizer starting");
        await OrganizeJobAsync(job, path, configPath);
        if (autoMove.Completed)
        {
            var plexRefreshRequestedAfterMove = await TriggerPlexScanAfterMoveAsync(job, CancellationToken.None);
            await TriggerLibraryScanAfterAutoMovePlexRefreshRequestedAsync(
                job,
                plexRefreshRequestedAfterMove,
                CancellationToken.None);
        }

        NotifyCompleted(job);
    }

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
            && TryBuildEnrichmentStage(
                root,
                platformCaps,
                eligiblePlatforms,
                new EnrichmentBuildContext(runIntent, job.Id),
                out var enrichmentStage,
                out enrichmentSkipReason,
                out var enrichmentStrippedKeys))
        {
            stages.Add(enrichmentStage);
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
            var reason = shouldRunEnhancement
                ? enhancementSkipReason
                : $"disabled for run intent '{runIntent}'";
            AppendLog(job, $"enhancement skipped: {reason}");
        }

        return stages;
    }

    private async Task RunIntegratedEnhancementWorkflowsAsync(
        AutoTagJob job,
        string rootPath,
        string configPath,
        bool includesEnhancementStage,
        CancellationToken cancellationToken)
    {
        if (!includesEnhancementStage
            || !ShouldRunEnhancementForIntent(job.RunIntent)
            || !string.Equals(job.Status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase)
            || !IsEnhancementWorkflowTrigger(job.Trigger))
        {
            return;
        }

        var root = LoadConfigRoot(configPath);
        if (root == null || root[AutoTagLiterals.EnhancementStage] is not JsonObject enhancementRoot)
        {
            return;
        }

        var enhancementLyricsSettings = BuildEnhancementLyricsSettings(root);
        var enabledFolders = await ResolveEnabledMusicFoldersAsync(cancellationToken);
        await RunConfiguredFolderUniformityAsync(job, rootPath, enhancementRoot, enabledFolders, cancellationToken);
        await RunConfiguredCoverMaintenanceAsync(job, rootPath, enhancementRoot, enabledFolders, cancellationToken);
        await RunConfiguredQualityChecksAsync(job, rootPath, enhancementRoot, enhancementLyricsSettings, enabledFolders, cancellationToken);
    }

    private async Task TriggerLibraryScanAfterAutoMovePlexRefreshRequestedAsync(
        AutoTagJob job,
        bool plexRefreshRequested,
        CancellationToken cancellationToken)
    {
        if (!plexRefreshRequested)
        {
            return;
        }

        if (await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
        {
            AppendLog(job, "post auto-move library scan skipped (downloads active).");
            _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Post auto-move library scan skipped because downloads became active."));
            return;
        }

        _activityLog.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            "Post auto-move library scan enqueued after Plex metadata refresh request."));
        AppendLog(job, "post auto-move library scan enqueued (Spotify artist metadata fetch enabled).");

        _ = _libraryScanRunner.EnqueueAsync(
            refreshImages: false,
            reset: false,
            folderId: null,
            skipSpotifyFetch: false,
            cacheSpotifyImages: false);
    }

    private static bool IsEnhancementWorkflowTrigger(string? trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return true;
        }

        return string.Equals(trigger, AutoTagLiterals.ManualTrigger, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, AutoTagLiterals.ScheduleTrigger, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunConfiguredFolderUniformityAsync(
        AutoTagJob job,
        string rootPath,
        JsonObject enhancementRoot,
        IReadOnlyList<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        if (!TryGetFolderUniformityConfig(enhancementRoot, out var folderUniformity))
        {
            return;
        }

        var scopedFolders = ResolveScopedFolders(rootPath, folderUniformity!, enabledFolders);
        var rootPaths = ResolveFolderUniformityRootPaths(rootPath, folderUniformity!, enabledFolders, scopedFolders);
        if (rootPaths.Count == 0)
        {
            AppendLog(job, "enhancement workflow: folder uniformity skipped (no eligible folders/paths).");
            return;
        }

        var settings = _settingsService.LoadSettings();
        ApplyEnhancementTrackTemplateOverrides(settings, enhancementRoot);
        var profileState = scopedFolders.Count > 0
            ? await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken)
            : null;
        var scopedFoldersByPath = BuildScopedFoldersByPath(scopedFolders);

        AppendLog(job, $"enhancement workflow: folder uniformity starting ({rootPaths.Count} path(s)).");
        await RunFolderUniformityForPathsAsync(job, folderUniformity!, rootPaths, settings, profileState, scopedFoldersByPath, cancellationToken);
        await RunFolderUniformityDedupeAsync(job, folderUniformity!, scopedFolders, rootPaths, enabledFolders, cancellationToken);

        AppendLog(job, "enhancement workflow: folder uniformity completed.");
    }

    private static void ApplyEnhancementTrackTemplateOverrides(DeezSpoTagSettings settings, JsonObject enhancementRoot)
    {
        if (settings == null || enhancementRoot == null)
        {
            return;
        }

        ApplyTemplateIfPresent(enhancementRoot, "tracknameTemplate", value => settings.TracknameTemplate = value);
        settings.AlbumTracknameTemplate = settings.TracknameTemplate;
        settings.PlaylistTracknameTemplate = settings.TracknameTemplate;
    }

    private static void ApplyTemplateIfPresent(JsonObject source, string key, Action<string> assign)
    {
        if (source[key] is not JsonValue node
            || !node.TryGetValue<string>(out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        assign(value.Trim());
    }

    private static bool TryGetFolderUniformityConfig(JsonObject enhancementRoot, out JsonObject? folderUniformity)
    {
        if (enhancementRoot["folderUniformity"] is not JsonObject config
            || ReadBool(config, "enforceFolderStructure") != true)
        {
            folderUniformity = null;
            return false;
        }

        folderUniformity = config;
        return true;
    }

    private static List<string> ResolveFolderUniformityRootPaths(
        string rootPath,
        JsonObject folderUniformity,
        IReadOnlyList<FolderDto> enabledFolders,
        List<FolderDto> scopedFolders)
    {
        return scopedFolders.Count > 0
            ? scopedFolders
                .Select(folder => folder.RootPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : ResolveRootPathsForWorkflow(rootPath, folderUniformity, enabledFolders);
    }

    private static Dictionary<string, FolderDto> BuildScopedFoldersByPath(IReadOnlyList<FolderDto> scopedFolders)
    {
        return scopedFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.RootPath))
            .GroupBy(folder => Path.GetFullPath(folder.RootPath.Trim()), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private async Task RunFolderUniformityForPathsAsync(
        AutoTagJob job,
        JsonObject folderUniformity,
        IReadOnlyList<string> rootPaths,
        DeezSpoTagSettings settings,
        AutoTagProfileResolutionService.ResolvedState? profileState,
        Dictionary<string, FolderDto> scopedFoldersByPath,
        CancellationToken cancellationToken)
    {
        foreach (var path in rootPaths)
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var options = BuildFolderUniformityOptions(folderUniformity, settings);
            if (profileState != null && scopedFoldersByPath.TryGetValue(path, out var folder))
            {
                var profile = AutoTagProfileResolutionService.ResolveFolderProfile(
                    profileState,
                    folder.Id,
                    folder.AutoTagProfileId);
                if (profile == null)
                {
                    AppendLog(job, $"enhancement workflow: folder uniformity skipped for '{path}' (missing AutoTag profile).");
                    continue;
                }

                AutoTagOrganizerProfileOverlay.ApplyTaggingProfileOverrides(options, profile);
            }

            var organizerReport = options.GenerateReconciliationReport
                ? new AutoTagLibraryOrganizer.AutoTagOrganizerReport()
                : null;
            await _libraryOrganizer.OrganizePathAsync(path, options, organizerReport, line => AppendLog(job, $"folder uniformity: {line}"));
            if (organizerReport != null)
            {
                AppendLog(job,
                    $"folder uniformity report: planned={organizerReport.PlannedMoves}, files={organizerReport.MovedFiles}, sidecars={organizerReport.MovedSidecars}, duplicate-replacements={organizerReport.ReplacedDuplicates}, duplicate-quarantine={organizerReport.QuarantinedDuplicates}.");
            }
        }
    }

    private async Task RunFolderUniformityDedupeAsync(
        AutoTagJob job,
        JsonObject folderUniformity,
        List<FolderDto> scopedFolders,
        IReadOnlyList<string> rootPaths,
        IReadOnlyList<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        if (ReadBool(folderUniformity, "runDedupe") == false)
        {
            return;
        }

        var dedupeFolders = scopedFolders.Count > 0
            ? scopedFolders
            : enabledFolders
                .Where(folder => !string.IsNullOrWhiteSpace(folder.RootPath)
                    && rootPaths.Any(path => PathsOverlap(path, folder.RootPath)))
                .ToList();
        if (dedupeFolders.Count == 0)
        {
            return;
        }

        var duplicateResult = await _duplicateCleanerService.ScanAsync(
            dedupeFolders,
            new DuplicateCleanerOptions
            {
                UseDuplicatesFolder = true,
                DuplicatesFolderName = folderUniformity["duplicatesFolderName"]?.GetValue<string>() ?? DuplicateCleanerService.DuplicatesFolderName,
                UseShazamForIdentity = ReadBool(folderUniformity, "useShazamForDedupe") == true,
                ConflictPolicy = folderUniformity["duplicateConflictPolicy"]?.GetValue<string>() ?? AutoTagOrganizerOptions.DuplicateConflictKeepBest
            },
            cancellationToken);
        AppendLog(job,
            $"enhancement workflow: folder-uniformity dedupe finished (found={duplicateResult.DuplicatesFound}, moved={duplicateResult.Deleted}, folder={duplicateResult.DuplicatesFolderName}).");
    }

    private async Task RunConfiguredCoverMaintenanceAsync(
        AutoTagJob job,
        string rootPath,
        JsonObject enhancementRoot,
        IReadOnlyList<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        if (enhancementRoot["coverMaintenance"] is not JsonObject coverMaintenance)
        {
            return;
        }

        var replaceMissingEmbedded = ReadBool(coverMaintenance, "replaceMissingEmbeddedCovers") == true;
        var syncExternalCovers = ReadBool(coverMaintenance, "syncExternalCovers") == true;
        var queueAnimatedArtwork = ReadBool(coverMaintenance, "queueAnimatedArtwork") == true;
        if (!replaceMissingEmbedded && !syncExternalCovers && !queueAnimatedArtwork)
        {
            return;
        }

        var rootPaths = ResolveRootPathsForWorkflow(rootPath, coverMaintenance, enabledFolders);
        if (rootPaths.Count == 0)
        {
            AppendLog(job, "enhancement workflow: cover maintenance skipped (no eligible folders/paths).");
            return;
        }

        var minResolution = ReadBoundedInt(coverMaintenance, "minResolution", 500, 100, 5000);
        var workerCount = ReadBoundedInt(coverMaintenance, "workerCount", 8, 1, 32);
        var settings = _settingsService.LoadSettings();
        var request = new CoverLibraryMaintenanceRequest(
            RootPaths: rootPaths,
            IncludeSubfolders: true,
            WorkerCount: workerCount,
            UpgradeLowResolutionCovers: true,
            MinResolution: minResolution,
            TargetResolution: Math.Max(minResolution, 1200),
            SizeTolerancePercent: 25,
            PreserveSourceFormat: false,
            ReplaceMissingEmbeddedCovers: replaceMissingEmbedded,
            SyncExternalCovers: syncExternalCovers,
            QueueAnimatedArtwork: queueAnimatedArtwork,
            AppleStorefront: string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront,
            AnimatedArtworkMaxResolution: settings.Video?.AppleMusicVideoMaxResolution ?? 2160,
            EnabledSources: null,
            CoverImageTemplate: settings.CoverImageTemplate);

        AppendLog(job, $"enhancement workflow: cover maintenance starting ({rootPaths.Count} path(s)).");
        var result = await _coverMaintenanceService.RunAsync(request, cancellationToken);
        AppendLog(job, $"enhancement workflow: cover maintenance finished ({result.Message})");
    }

    private async Task RunConfiguredQualityChecksAsync(
        AutoTagJob job,
        string rootPath,
        JsonObject enhancementRoot,
        DeezSpoTagSettings enhancementLyricsSettings,
        IReadOnlyList<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        if (enhancementRoot["qualityChecks"] is not JsonObject qualityChecks)
        {
            return;
        }

        var options = BuildQualityCheckOptions(qualityChecks);
        if (!options.ShouldRunAnyWorkflow)
        {
            return;
        }

        var scopedFolders = ResolveScopedFolders(rootPath, qualityChecks, enabledFolders);
        if (scopedFolders.Count == 0)
        {
            AppendLog(job, "enhancement workflow: quality checks skipped (no eligible library folders in scope).");
            return;
        }

        var scopedFolderIds = scopedFolders
            .Select(folder => folder.Id)
            .Distinct()
            .ToList();

        await StartQualityScannerIfRequestedAsync(job, qualityChecks, options, scopedFolderIds, cancellationToken);
        await RunDuplicateCheckIfRequestedAsync(job, options, scopedFolders, cancellationToken);
        await RunLyricsRefreshIfRequestedAsync(job, options, scopedFolderIds, enhancementLyricsSettings, cancellationToken);
    }

    private static QualityCheckOptions BuildQualityCheckOptions(JsonObject qualityChecks)
    {
        var flagDuplicates = ReadBool(qualityChecks, "flagDuplicates") == true;
        var flagMissingTags = ReadBool(qualityChecks, "flagMissingTags") == true;
        var flagMismatchedMetadata = ReadBool(qualityChecks, "flagMismatchedMetadata") == true;
        var queueAtmosAlternatives = ReadBool(qualityChecks, "queueAtmosAlternatives") == true;
        var queueLyricsRefresh = ReadBool(qualityChecks, "queueLyricsRefresh") == true;
        var technicalProfiles = ReadStringList(qualityChecks, "technicalProfiles")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var runQualityUpgradeStage = flagMissingTags || flagMismatchedMetadata || technicalProfiles.Count > 0;
        var runQualityScanner = flagMissingTags || flagMismatchedMetadata || queueAtmosAlternatives || technicalProfiles.Count > 0;
        return new QualityCheckOptions(
            FlagDuplicates: flagDuplicates,
            UseDuplicatesFolder: ReadBool(qualityChecks, "useDuplicatesFolder") != false,
            UseShazamForDedupe: ReadBool(qualityChecks, "useShazamForDedupe") == true,
            DuplicatesFolderName: qualityChecks["duplicatesFolderName"]?.GetValue<string>(),
            QueueLyricsRefresh: queueLyricsRefresh,
            QueueAtmosAlternatives: queueAtmosAlternatives,
            RunQualityUpgradeStage: runQualityUpgradeStage,
            RunQualityScanner: runQualityScanner,
            TechnicalProfiles: technicalProfiles);
    }

    private async Task StartQualityScannerIfRequestedAsync(
        AutoTagJob job,
        JsonObject qualityChecks,
        QualityCheckOptions options,
        List<long> scopedFolderIds,
        CancellationToken cancellationToken)
    {
        if (!options.RunQualityScanner)
        {
            return;
        }

        var started = await _qualityScannerService.StartAsync(
            new QualityScannerStartRequest
            {
                Scope = "all",
                FolderId = scopedFolderIds.Count == 1 ? scopedFolderIds[0] : null,
                RunQualityUpgradeStage = options.RunQualityUpgradeStage,
                QueueAtmosAlternatives = options.QueueAtmosAlternatives,
                CooldownMinutes = ReadOptionalInt(qualityChecks, "cooldownMinutes"),
                Trigger = "enhancement",
                MarkAutomationWindow = false,
                TechnicalProfiles = options.TechnicalProfiles,
                FolderIds = scopedFolderIds
            },
            cancellationToken);
        AppendLog(job, started
            ? "enhancement workflow: quality scanner started."
            : "enhancement workflow: quality scanner skipped (already running).");
    }

    private async Task RunDuplicateCheckIfRequestedAsync(
        AutoTagJob job,
        QualityCheckOptions options,
        IReadOnlyList<FolderDto> scopedFolders,
        CancellationToken cancellationToken)
    {
        if (!options.FlagDuplicates)
        {
            return;
        }

        var duplicateOptions = new DuplicateCleanerOptions
        {
            UseDuplicatesFolder = options.UseDuplicatesFolder,
            DuplicatesFolderName = options.DuplicatesFolderName ?? DuplicateCleanerService.DuplicatesFolderName,
            UseShazamForIdentity = options.UseShazamForDedupe
        };
        var duplicateResult = await _duplicateCleanerService.ScanAsync(scopedFolders, duplicateOptions, cancellationToken);
        AppendLog(job,
            $"enhancement workflow: duplicate check finished (scanned={duplicateResult.FilesScanned}, found={duplicateResult.DuplicatesFound}, moved={duplicateResult.Deleted}, folder={duplicateResult.DuplicatesFolderName}).");
    }

    private async Task RunLyricsRefreshIfRequestedAsync(
        AutoTagJob job,
        QualityCheckOptions options,
        List<long> scopedFolderIds,
        DeezSpoTagSettings lyricsSettings,
        CancellationToken cancellationToken)
    {
        if (!options.QueueLyricsRefresh)
        {
            return;
        }

        if (!LyricsSettingsPolicy.CanFetchLyrics(lyricsSettings))
        {
            AppendLog(job, "enhancement workflow: lyrics refresh skipped (lyrics fetching disabled by settings).");
            return;
        }

        var tracks = await _libraryRepository.GetQualityScanTracksAsync(
            "all",
            scopedFolderIds.Count == 1 ? scopedFolderIds[0] : null,
            minFormat: null,
            minBitDepth: null,
            minSampleRateHz: null,
            cancellationToken);

        tracks = FilterTracksByScopedFolders(tracks, scopedFolderIds);
        tracks = FilterTracksByTechnicalProfiles(tracks, options.TechnicalProfiles);

        var trackIds = tracks
            .Select(track => track.TrackId)
            .Distinct()
            .ToList();
        var enqueueResult = _lyricsRefreshQueueService.Enqueue(trackIds, lyricsSettings);
        AppendLog(job,
            $"enhancement workflow: lyrics refresh queued (requested={enqueueResult.Requested}, enqueued={enqueueResult.Enqueued}, skipped={enqueueResult.Skipped}).");
    }

    private DeezSpoTagSettings BuildEnhancementLyricsSettings(JsonObject configRoot)
    {
        var settings = _settingsService.LoadSettings();
        var technical = TryReadTechnicalSettings(configRoot);
        if (technical != null)
        {
            TechnicalLyricsSettingsApplier.Apply(settings, technical);
        }

        return settings;
    }

    private TechnicalTagSettings? TryReadTechnicalSettings(JsonObject? configRoot)
    {
        if (configRoot == null
            || configRoot["technical"] is not JsonObject technicalNode)
        {
            return null;
        }

        try
        {
            return technicalNode.Deserialize<TechnicalTagSettings>(_jsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to parse technical settings from enhancement config root.");
            return null;
        }
    }

    private static IReadOnlyList<QualityScanTrackDto> FilterTracksByScopedFolders(
        IReadOnlyList<QualityScanTrackDto> tracks,
        List<long> scopedFolderIds)
    {
        if (scopedFolderIds.Count <= 1)
        {
            return tracks;
        }

        var allowed = scopedFolderIds.ToHashSet();
        return tracks
            .Where(track => track.DestinationFolderId.HasValue && allowed.Contains(track.DestinationFolderId.Value))
            .ToList();
    }

    private static IReadOnlyList<QualityScanTrackDto> FilterTracksByTechnicalProfiles(
        IReadOnlyList<QualityScanTrackDto> tracks,
        IReadOnlyList<string> technicalProfiles)
    {
        if (technicalProfiles.Count == 0)
        {
            return tracks;
        }

        var allowedProfiles = technicalProfiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tracks
                .Where(track => allowedProfiles.Contains(QualityScanTrackFormatter.FormatTechnicalProfile(track)))
                .ToList();
    }

    private sealed record QualityCheckOptions(
        bool FlagDuplicates,
        bool UseDuplicatesFolder,
        bool UseShazamForDedupe,
        string? DuplicatesFolderName,
        bool QueueLyricsRefresh,
        bool QueueAtmosAlternatives,
        bool RunQualityUpgradeStage,
        bool RunQualityScanner,
        IReadOnlyList<string> TechnicalProfiles)
    {
        public bool ShouldRunAnyWorkflow => RunQualityScanner || FlagDuplicates || QueueLyricsRefresh;
    }

    private async Task<IReadOnlyList<FolderDto>> ResolveEnabledMusicFoldersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FolderDto> folders;
        try
        {
            folders = _libraryRepository.IsConfigured
                ? await _libraryRepository.GetFoldersAsync(cancellationToken)
                : _activityLog.GetFolders();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            folders = _activityLog.GetFolders();
        }

        return folders
            .Where(folder => folder.Enabled
                && !string.IsNullOrWhiteSpace(folder.RootPath)
                && IsMusicCapableFolder(folder))
            .ToList();
    }

    private static List<FolderDto> ResolveScopedFolders(
        string rootPath,
        JsonObject workflowOptions,
        IReadOnlyList<FolderDto> enabledFolders)
    {
        var requestedIds = ParseFolderIds(workflowOptions, "folderIds");
        if (requestedIds.Count > 0)
        {
            var requested = requestedIds.ToHashSet();
            return enabledFolders
                .Where(folder => requested.Contains(folder.Id))
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return new List<FolderDto>();
        }

        return enabledFolders
            .Where(folder => PathsOverlap(rootPath, folder.RootPath))
            .ToList();
    }

    private static List<string> ResolveRootPathsForWorkflow(
        string rootPath,
        JsonObject workflowOptions,
        IReadOnlyList<FolderDto> enabledFolders)
    {
        var scopedFolders = ResolveScopedFolders(rootPath, workflowOptions, enabledFolders);
        if (scopedFolders.Count > 0)
        {
            return scopedFolders
                .Select(folder => folder.RootPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(Path.GetFullPath)
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return new List<string>();
        }

        var normalizedRoot = Path.GetFullPath(rootPath);
        return Directory.Exists(normalizedRoot)
            ? new List<string> { normalizedRoot }
            : new List<string>();
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
        JsonObject folderUniformity,
        DeezSpoTagSettings settings)
    {
        var options = new AutoTagOrganizerOptions
        {
            IncludeSubfolders = ReadBool(folderUniformity, AutoTagLiterals.IncludeSubfoldersKey) ?? true,
            MoveMisplacedFiles = ReadBool(folderUniformity, "moveMisplacedFiles") ?? true,
            MergeIntoExistingDestinationFolders = ReadBool(folderUniformity, "mergeIntoExistingDestinationFolders") != false,
            RenameFilesToTemplate = ReadBool(folderUniformity, "renameFilesToTemplate") != false,
            RemoveEmptyFolders = ReadBool(folderUniformity, "removeEmptyFolders") != false,
            ResolveSameTrackQualityConflicts = ReadBool(folderUniformity, "resolveSameTrackQualityConflicts") != false,
            KeepBothOnUnresolvedConflicts = ReadBool(folderUniformity, "keepBothOnUnresolvedConflicts") != false,
            OnlyMoveWhenTagged = ReadBool(folderUniformity, "onlyMoveWhenTagged") == true,
            OnlyReorganizeAlbumsWithFullTrackSets = ReadBool(folderUniformity, "onlyReorganizeAlbumsWithFullTrackSets") == true,
            SkipCompilationFolders = ReadBool(folderUniformity, "skipCompilationFolders") == true,
            SkipVariousArtistsFolders = ReadBool(folderUniformity, "skipVariousArtistsFolders") == true,
            GenerateReconciliationReport = ReadBool(folderUniformity, "generateReconciliationReport") == true,
            UseShazamForUntaggedFiles = ReadBool(folderUniformity, "useShazamForUntaggedFiles") == true,
            DuplicateConflictPolicy = folderUniformity["duplicateConflictPolicy"]?.GetValue<string>() ?? AutoTagOrganizerOptions.DuplicateConflictKeepBest,
            ArtworkPolicy = folderUniformity["artworkPolicy"]?.GetValue<string>() ?? AutoTagOrganizerOptions.ArtworkPolicyPreserveExisting,
            LyricsPolicy = folderUniformity["lyricsPolicy"]?.GetValue<string>() ?? AutoTagOrganizerOptions.LyricsPolicyMerge
        };

        // Folder-structure and technical defaults are driven by canonical settings/profile overlays.
        AutoTagOrganizerProfileOverlay.ApplySettingsOverrides(options, settings);
        return options;
    }

    private bool TryBuildEnrichmentStage(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        IReadOnlyList<string> eligiblePlatforms,
        EnrichmentBuildContext context,
        out AutoTagStageConfig stage,
        out string skipReason,
        out List<string> strippedKeys)
    {
        stage = null!;
        skipReason = "tags not configured";
        strippedKeys = new List<string>();

        var requested = ResolveEnrichmentRequestedTags(baseRoot, context.RunIntent);
        if (requested.Count == 0)
        {
            return false;
        }

        var platforms = eligiblePlatforms.ToList();
        if (platforms.Count == 0)
        {
            skipReason = "no eligible enrichment platforms enabled";
            return false;
        }

        var excludedPlatform = string.Equals(
            NormalizeRunIntent(context.RunIntent),
            AutoTagLiterals.RunIntentDownloadEnrichment,
            StringComparison.OrdinalIgnoreCase)
            ? ResolveDownloadSourcePlatform(baseRoot)
            : null;
        if (!string.IsNullOrWhiteSpace(excludedPlatform))
        {
            platforms = platforms
                .Where(platform => !string.Equals(platform, excludedPlatform, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        if (platforms.Count == 0)
        {
            skipReason = string.IsNullOrWhiteSpace(excludedPlatform)
                ? "no enrichment platforms enabled"
                : $"no enrichment platforms enabled after excluding download source ({excludedPlatform})";
            return false;
        }

        var filtered = FilterSupportedTags(requested, platforms, platformCaps);
        if (filtered.Count == 0)
        {
            skipReason = "no supported enrichment tags for enabled platforms";
            return false;
        }

        var stageRoot = CloneRoot(baseRoot);
        WriteStringList(stageRoot, "tags", filtered);
        WriteStringList(stageRoot, AutoTagLiterals.PlatformsKey, platforms);
        var platformCount = ReadStringList(stageRoot, AutoTagLiterals.PlatformsKey).Count;
        stageRoot[AutoTagLiterals.MultiPlatformKey] = platformCount > 1;
        strippedKeys = ApplyStageSchema(stageRoot, EnrichmentStageAllowedKeys);

        var configJson = stageRoot.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        var configPath = WriteRuntimeConfigFile(context.JobId, AutoTagLiterals.EnrichmentStage, configJson);
        stage = new AutoTagStageConfig(
            AutoTagLiterals.EnrichmentStage,
            configPath,
            filtered.Count,
            ComputeConfigHash(configJson));
        return true;
    }

    private static List<string> ResolveEnrichmentRequestedTags(JsonObject baseRoot, string? runIntent)
    {
        _ = runIntent;
        return MergeRequestedAndDownloadTags(
            requested: ReadStringList(baseRoot, "tags"),
            downloadTags: ReadStringList(baseRoot, AutoTagLiterals.DownloadTagsKey));
    }

    private static List<string> ResolveEnhancementRequestedTags(JsonObject baseRoot)
    {
        return MergeRequestedAndDownloadTags(
            requested: ReadStringList(baseRoot, "gapFillTags"),
            downloadTags: ReadStringList(baseRoot, AutoTagLiterals.DownloadTagsKey));
    }

    private static List<string> MergeRequestedAndDownloadTags(List<string> requested, List<string> downloadTags)
    {
        if (downloadTags.Count == 0)
        {
            return requested;
        }

        var merged = new List<string>(requested);
        var seen = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in downloadTags.Where(seen.Add))
        {
            merged.Add(tag);
        }

        return merged;
    }

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

        var requested = ResolveEnhancementRequestedTags(baseRoot);
        var platforms = eligiblePlatforms.ToList();
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

        // Enhancement should process on-disk files by default and only skip when
        // an explicit enhancement-level setting is provided.
        stageRoot["skipTagged"] = ReadBool(baseRoot, "enhancementSkipTagged")
            ?? false;
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

    private static HashSet<string> BuildStageAllowedKeys(bool includeSkipTagged, bool includeConflictResolution, bool includeTargetFiles)
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
            "writeLrc",
            "capitalizeGenres",
            AutoTagLiterals.DownloadTagSourceKey,
            "tracknameTemplate",
            "saveArtwork",
            "dlAlbumcoverForPlaylist",
            "saveArtworkArtist",
            "coverImageTemplate",
            "artistImageTemplate",
            "localArtworkFormat",
            "embedMaxQualityCover",
            "jpegImageQuality",
            "runTrigger",
            "technical",
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
        var requestSet = requested
            .Select(NormalizeSupportedTagKey)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestSet.Count == 0)
        {
            return new List<string>();
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var platformId in platforms)
        {
            if (string.IsNullOrWhiteSpace(platformId))
            {
                continue;
            }

            if (platformCaps.TryGetValue(platformId.Trim(), out var caps))
            {
                foreach (var tag in caps.SupportedTags)
                {
                    allowed.Add(tag);
                }
            }
        }

        if (allowed.Count == 0)
        {
            return new List<string>();
        }

        return requestSet.Where(tag => allowed.Contains(tag)).ToList();
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

    private async Task OrganizeJobAsync(AutoTagJob job, string rootPath, string configPath)
    {
        if (job.Status is not AutoTagLiterals.CompletedStatus and not AutoTagLiterals.FailedStatus)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("AutoTag job {JobId}: organizer skipped (status {Status})", job.Id, job.Status);
            }
            return;
        }

        if (ShouldSkipOrganizerForMultiQualityStaging(rootPath))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("AutoTag job {JobId}: organizer skipped (multi-quality staging root).", job.Id);
            }
            AppendLog(job, "organizer skipped: multi-quality staging root");
            return;
        }

        if (_disableAutoMove)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("AutoTag job {JobId}: organizer skipped (disabled).", job.Id);
            }
            AppendLog(job, "organizer skipped: disabled");
            return;
        }

        if (!await WaitForOrganizerCooldownAsync(_organizerCooldown, CancellationToken.None))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("AutoTag job {JobId}: organizer skipped (downloads active).", job.Id);
            }
            AppendLog(job, "organizer skipped: downloads active");
            return;
        }

        try
        {
            _logger.LogInformation("AutoTag job JobId: organizer started for RootPath");
            AppendLog(job, "organizer started");
            var organizerOptions = LoadOrganizerOptions(configPath);
            await _libraryOrganizer.OrganizePathAsync(rootPath, organizerOptions, message => AppendLog(job, message));
            _logger.LogInformation("AutoTag job JobId: organizer finished for RootPath");
            AppendLog(job, "organizer finished");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag job {JobId}: library organizer failed.", job.Id);
            AppendLog(job, $"organizer failed: {ex.Message}");
        }
    }

    private static bool ShouldSkipOrganizerForMultiQualityStaging(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            var normalized = Path.GetFullPath(rootPath);
            var stereo = Path.Join(normalized, "Stereo");
            var atmos = Path.Join(normalized, "Atmos");
            return Directory.Exists(stereo) || Directory.Exists(atmos);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
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
        if (!string.IsNullOrWhiteSpace(options.MoveUntaggedPath))
        {
            return options;
        }

        if (string.Equals(job.Trigger, AutoTagLiterals.ManualTrigger, StringComparison.OrdinalIgnoreCase))
        {
            return options;
        }

        var manualFailedPath = await TryResolveManualFailedMovePathAsync();
        if (!string.IsNullOrWhiteSpace(manualFailedPath))
        {
            options.MoveUntaggedPath = manualFailedPath;
        }

        return options;
    }

    private async Task<string?> TryResolveManualFailedMovePathAsync()
    {
        try
        {
            var prefs = await _userPreferencesStore.LoadAsync();
            if (!prefs.AutoTagPreferences.HasValue)
            {
                return null;
            }

            var autoTagPrefs = prefs.AutoTagPreferences.Value;
            if (autoTagPrefs.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return TryGetCaseInsensitiveStringProperty(autoTagPrefs, "moveFailedPath", out var moveFailedPath)
                ? moveFailedPath
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed loading manual failed destination from user preferences.");
            return null;
        }
    }

    private static bool TryGetCaseInsensitiveProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value;
            return true;
        }

        return false;
    }

    private static bool TryGetCaseInsensitiveStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetCaseInsensitiveProperty(element, propertyName, out var raw)
            || raw.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var resolved = raw.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return false;
        }

        value = resolved;
        return true;
    }

    private async Task<AutoMoveExecutionResult> MoveAfterAutoTagAsync(
        AutoTagJob job,
        string rootPath,
        string configPath,
        IReadOnlyCollection<string> taggedFiles,
        IReadOnlyCollection<string> failedFiles)
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
            var summary = await _downloadMoveService.MoveForRootWithSummaryAsync(
                rootPath,
                organizerOptions,
                taggedFiles,
                failedFiles,
                CancellationToken.None);
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
        var label = summary.RecoveryCleanup ? "auto-move recovery summary" : "auto-move summary";
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

    private async Task<bool> TriggerPlexMetadataRefreshAfterEnhancementAsync(
        AutoTagJob job,
        bool includesEnhancementStage,
        CancellationToken cancellationToken)
    {
        if (!includesEnhancementStage
            || !ShouldRunEnhancementForIntent(job.RunIntent)
            || !string.Equals(job.Status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var plex = await LoadConfiguredPlexForScanAsync(job);
        if (plex == null)
        {
            return false;
        }

        return await TriggerPlexScanAsync(job, plex, "after enhancement run", cancellationToken);
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
                .Where(section => string.Equals(section.Type, "artist", StringComparison.OrdinalIgnoreCase))
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
                if (await _plexApiClient.RefreshLibraryAsync(plexUrl, plexToken, section.Key, cancellationToken))
                {
                    refreshed++;
                }
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

    private async Task<bool> WaitForOrganizerCooldownAsync(TimeSpan cooldown, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + cooldown;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
            {
                return false;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            var delay = remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
            if (delay <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                return false;
            }
        }

        return !await _queueRepository.HasActiveDownloadsAsync(cancellationToken);
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
            EnsureEnhancementTagsMergeEnrichmentAndEnhancement(node);
            EnsureEnhancementFolderScopesCanonical(node);
            EnsureLegacyFolderUniformityStructureMirrorsRemoved(node);
            EnsureLegacyOrganizerConfigRemoved(node);
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

        var trackTemplate = ReadString(root, "tracknameTemplate");
        if (string.IsNullOrWhiteSpace(trackTemplate))
        {
            foreach (var legacyKey in new[] { "filenameTemplate", "albumTracknameTemplate", "playlistTracknameTemplate" })
            {
                var legacyTemplate = ReadString(root, legacyKey);
                if (string.IsNullOrWhiteSpace(legacyTemplate))
                {
                    continue;
                }

                root["tracknameTemplate"] = legacyTemplate.Trim();
                break;
            }
        }

        root.Remove("filenameTemplate");
        root.Remove("albumTracknameTemplate");
        root.Remove("playlistTracknameTemplate");
    }

    private static string? ReadString(JsonObject root, string key)
    {
        if (root[key] is not JsonValue value
            || !value.TryGetValue<string>(out var text))
        {
            return null;
        }

        text = text.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
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
            || root["enhancement"] is not JsonObject enhancement)
        {
            return;
        }

        CanonicalizeEnhancementFolderScopeSection(enhancement, "folderUniformity");
        CanonicalizeEnhancementFolderScopeSection(enhancement, "coverMaintenance");
        CanonicalizeEnhancementFolderScopeSection(enhancement, "qualityChecks");
    }

    private static void EnsureEnhancementTagsMergeEnrichmentAndEnhancement(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        var enhancementTags = ReadCanonicalTagArray(root, "gapFillTags");
        var enrichmentTags = ReadCanonicalTagArray(root, "tags");
        var merged = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in enrichmentTags.Where(seen.Add))
        {
            merged.Add(tag);
        }

        foreach (var tag in enhancementTags.Where(seen.Add))
        {
            merged.Add(tag);
        }

        root["gapFillTags"] = merged;
    }

    private static List<string> ReadCanonicalTagArray(JsonObject root, string propertyName)
    {
        if (root[propertyName] is not JsonArray array)
        {
            return new List<string>();
        }

        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in array)
        {
            if (value is not JsonValue tagValue || !tagValue.TryGetValue<string>(out var rawTag))
            {
                continue;
            }

            var tag = rawTag?.Trim();
            if (string.IsNullOrWhiteSpace(tag) || !seen.Add(tag))
            {
                continue;
            }

            tags.Add(tag);
        }

        return tags;
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
            || root["enhancement"] is not JsonObject enhancement
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
    }

    private static void EnsureLegacyOrganizerConfigRemoved(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        root.Remove("organizer");
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

    private async Task<string> InjectDeezerAuthAsync(string configJson)
    {
        var arl = await _deezerAuth.GetArlAsync();
        if (string.IsNullOrWhiteSpace(arl))
        {
            return configJson;
        }

        try
        {
            var node = JsonNode.Parse(configJson) as JsonObject;
            if (node == null)
            {
                return configJson;
            }

            if (node[AutoTagLiterals.CustomKey] is not JsonObject custom)
            {
                custom = new JsonObject();
                node[AutoTagLiterals.CustomKey] = custom;
            }

            if (custom[AutoTagLiterals.DeezerSource] is not JsonObject deezer)
            {
                deezer = new JsonObject();
                custom[AutoTagLiterals.DeezerSource] = deezer;
            }

            if (deezer["art_resolution"] == null)
            {
                deezer["art_resolution"] = 1200;
            }

            if (deezer["arl"] == null)
            {
                deezer["arl"] = arl;
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

    private string InjectDeezerDownloadOptions(string configJson)
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            var node = JsonNode.Parse(configJson) as JsonObject;
            if (node == null)
            {
                return configJson;
            }

            if (node[AutoTagLiterals.CustomKey] is not JsonObject custom)
            {
                custom = new JsonObject();
                node[AutoTagLiterals.CustomKey] = custom;
            }

            if (custom[AutoTagLiterals.DeezerSource] is not JsonObject deezer)
            {
                deezer = new JsonObject();
                custom[AutoTagLiterals.DeezerSource] = deezer;
            }

            if (deezer["max_bitrate"] == null && settings.MaxBitrate > 0)
            {
                deezer["max_bitrate"] = settings.MaxBitrate;
            }

            if (deezer["language"] == null && !string.IsNullOrWhiteSpace(settings.DeezerLanguage))
            {
                deezer["language"] = settings.DeezerLanguage;
            }

            if (deezer["country"] == null && !string.IsNullOrWhiteSpace(settings.DeezerCountry))
            {
                deezer["country"] = settings.DeezerCountry;
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
            AutoTagLiterals.AutomationTrigger => AutoTagLiterals.AutomationTrigger,
            AutoTagLiterals.ScheduleTrigger => AutoTagLiterals.ScheduleTrigger,
            _ => AutoTagLiterals.ManualTrigger
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
            _ => AutoTagLiterals.RunIntentDefault
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

    private static bool ShouldRunEnrichmentForIntent(string? runIntent)
    {
        return !IsEnhancementRunIntent(runIntent);
    }

    private static bool ShouldRunEnhancementForIntent(string? runIntent)
    {
        return !string.Equals(
            NormalizeRunIntent(runIntent),
            AutoTagLiterals.RunIntentDownloadEnrichment,
            StringComparison.OrdinalIgnoreCase);
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

    private string InjectTechnicalSettings(
        string configJson,
        TechnicalTagSettings? technical,
        string? profileId,
        string? profileName)
    {
        if (technical == null && string.IsNullOrWhiteSpace(profileId) && string.IsNullOrWhiteSpace(profileName))
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
            _logger.LogDebug(ex, "Failed to inject profile technical settings into AutoTag config.");
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
        SetIfEmpty(discogs, "token", discogsAuth?.Token);
    }

    private static void ApplyLastFmAuthDefaults(JsonObject custom, LastFmAuth? lastFmAuth)
    {
        if (string.IsNullOrWhiteSpace(lastFmAuth?.ApiKey))
        {
            return;
        }

        var lastFm = GetOrCreatePlatformCustomNode(custom, AutoTagLiterals.LastFmPlatform);
        SetIfEmpty(lastFm, "apiKey", lastFmAuth?.ApiKey);
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

    private async Task TrySeedSpotifyTokenCacheAsync()
    {
        try
        {
            var state = await _platformAuthService.LoadAsync();
            var spotify = state?.Spotify;
            if (spotify == null || string.IsNullOrWhiteSpace(spotify.ActiveAccount))
            {
                return;
            }

            var active = spotify.Accounts.FirstOrDefault(a =>
                a.Name.Equals(spotify.ActiveAccount, StringComparison.OrdinalIgnoreCase));
            var blobPath = active?.LibrespotBlobPath ?? active?.BlobPath;
            if (string.IsNullOrWhiteSpace(blobPath))
            {
                return;
            }

            if (!_spotifyBlobService.BlobExists(blobPath)
                || !await _spotifyBlobService.IsLibrespotBlobAsync(blobPath))
            {
                return;
            }

            var tokenResult = await _spotifyBlobService.GetWebApiAccessTokenAsync(blobPath);
            if (string.IsNullOrWhiteSpace(tokenResult.AccessToken))
            {
                return;
            }

            var cachePath = ResolveSpotifyTokenCachePath();
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                return;
            }

            var expiresAt = tokenResult.ExpiresAtUnixMs.HasValue && tokenResult.ExpiresAtUnixMs.Value > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(tokenResult.ExpiresAtUnixMs.Value)
                : DateTimeOffset.UtcNow.AddMinutes(5);
            var expiresIn = Math.Max(1, (int)Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalSeconds));

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var payload = new Dictionary<string, object?>
            {
                ["access_token"] = tokenResult.AccessToken,
                ["expires_in"] = expiresIn,
                ["expires_at"] = expiresAt.ToUniversalTime().ToString("O"),
                ["refresh_token"] = null,
                ["scope"] = "user-read-private"
            };

            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to seed Spotify token cache for AutoTag.");
        }
    }

    private static string? ResolveSpotifyTokenCachePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Join(home, ".config", "onetagger", "spotify_token_cache.json"),
            Path.Join(home, ".config", "OneTagger", "OneTagger", "spotify_token_cache.json"),
            Path.Join(home, ".config", "OneTagger", "spotify_token_cache.json")
        };

        var existingCandidate = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(existingCandidate))
        {
            return existingCandidate;
        }

        return candidates[0];
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
        AppendStatusHistory(job, status);
        TrackFileOutcome(fileOutcomes, status);
        TryCaptureTagDiff(job, status);
        switch (status.Status.Status)
        {
            case AutoTagLiterals.OkStatus:
            case AutoTagLiterals.TaggedStatus:
                job.OkCount += 1;
                break;
            case AutoTagLiterals.ErrorStatus:
                job.ErrorCount += 1;
                break;
            case AutoTagLiterals.SkippedStatus:
                job.SkippedCount += 1;
                break;
        }
        TryUpdateResumeCheckpoint(job, stageName, stageConfigHash, status);
        SaveJob(job);
    }

    private static bool IsTerminalStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            AutoTagLiterals.OkStatus => true,
            AutoTagLiterals.TaggedStatus => true,
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
        }
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
                if (!string.IsNullOrWhiteSpace(status.Status.Message)
                    && status.Status.Message.Contains("already tagged", StringComparison.OrdinalIgnoreCase))
                {
                    outcome.SkippedAlreadyTagged = true;
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

            if (outcome.Tagged || outcome.SkippedAlreadyTagged)
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
        var snapshot = new TaggingStatusSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            Status = status
        };
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
            SkippedCount = job.SkippedCount,
            RootPath = job.RootPath,
            Trigger = string.IsNullOrWhiteSpace(job.Trigger) ? AutoTagLiterals.ManualTrigger : job.Trigger,
            RunIntent = NormalizeRunIntent(job.RunIntent),
            ProfileId = job.ProfileId,
            ProfileName = job.ProfileName,
            AutoMoveSummary = job.AutoMoveSummary?.Clone(),
            LogCount = GetArchivedLogCount(job.Id, job.Logs.Count),
            StatusEntryCount = GetArchivedStatusCount(job.Id, job.StatusHistory.Count)
        };
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
                _logger.LogDebug(ex, "Failed to load AutoTag run summary for {JobId}", jobId);
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
        return JsonSerializer.Deserialize<AutoTagRunSummary>(json, _jsonOptions);
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

            List<string> archived = new();
            foreach (var path in candidatePaths)
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
                if (lines.Count > archived.Count)
                {
                    archived = lines;
                }
            }
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
                _logger.LogDebug(ex, "Failed to read archived AutoTag logs for {JobId}", jobId);
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
                    jobId);
            }

            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read archived AutoTag status history for {JobId}", jobId);
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
                    jobId,
                    logs.Count);
            }
            return logs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to recover archived AutoTag logs for {JobId}", jobId);
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
                    jobId,
                    statusHistory.Count);
            }
            return statusHistory;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to recover archived AutoTag status history for {JobId}", jobId);
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
        catch
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
        catch
        {
            return fallback;
        }
    }

    private string GetRunHistoryDirectory(string jobId) => Path.Join(_historyDir, jobId);

    private string GetRunSummaryPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "summary.json");

    private string GetRunLogPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "autotag.log");

    private string GetRunStatusHistoryPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "status-history.ndjson");

    private string GetRunTagDiffsPath(string jobId) => Path.Join(GetRunHistoryDirectory(jobId), "tag-diffs.json");

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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to persist archived AutoTag tag diffs for {JobId}", jobId);
            }
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
                _logger.LogDebug(ex, "Failed to read archived AutoTag tag diffs for {JobId}", jobId);
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
                    jobId,
                    repaired.Count);
            }
            return repaired;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to recover archived AutoTag tag diffs for {JobId}", jobId);
            }
            return new Dictionary<string, AutoTagTagDiff>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private (List<TaggingStatusSnapshot> Entries, int SkippedMalformed) ParseStatusHistoryEntries(string path)
    {
        var entries = new List<TaggingStatusSnapshot>();
        var skippedMalformed = 0;
        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var line = rawLine?.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<TaggingStatusSnapshot>(line, _jsonOptions);
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
            catch (Exception)
            {
                skippedMalformed += 1;
            }
        }

        return (entries, skippedMalformed);
    }

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
            catch
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

        foreach (var root in EnumerateHistoryRoots())
        {
            var candidate = Path.Join(root, jobId, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateRunFileCandidates(string jobId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(fileName))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EnumerateHistoryRoots())
        {
            var candidate = Path.Join(root, jobId, fileName);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var normalized = Path.GetFullPath(candidate);
            if (seen.Add(normalized))
            {
                yield return normalized;
            }
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

            foreach (var path in Directory.EnumerateFiles(_jobsDir, "*.json"))
            {
                var jobId = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(jobId))
                {
                    continue;
                }

                var archiveComplete = IsRunArchiveComplete(jobId);
                var needsRepair = archiveComplete && ShouldRepairRunArchive(jobId);
                if (archiveComplete && !needsRepair)
                {
                    continue;
                }

                var job = LoadJob(jobId);
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
        catch
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
                File.WriteAllText(
                    GetRunSummaryPath(job.Id),
                    JsonSerializer.Serialize(BuildRunSummary(job), _jsonOptions),
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
            var json = JsonSerializer.Serialize(job, _jsonOptions);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            SaveArchivedTagDiffs(job.Id, job.TagDiffs);
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

            return JsonSerializer.Deserialize<AutoTagJob>(utf8, _jsonOptions);
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

        if (!string.Equals(job.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_activeJobIds.ContainsKey(job.Id))
        {
            return;
        }

        job.Status = AutoTagLiterals.FailedStatus;
        job.ExitCode = 1;
        job.FinishedAt ??= DateTimeOffset.UtcNow;
        job.Error ??= "AutoTag job was interrupted and recovered as stale running state.";
        if (string.Equals(job.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase)
            && job.ResumeCheckpoint != null)
        {
            // Download-enrichment runs depend on the latest discovered file set; stale checkpoints can skip platforms/files.
            job.ResumeCheckpoint = null;
            job.Logs.Add("stale recovery: resume checkpoint cleared for download_enrichment");
        }
        SaveJob(job);
        TryQueueStaleRecoveryCleanup(job);
    }

    private void TryQueueStaleRecoveryCleanup(AutoTagJob job)
    {
        if (!string.Equals(job.RunIntent, AutoTagLiterals.RunIntentDownloadEnrichment, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(job.RootPath))
        {
            return;
        }

        if (!_staleRecoveryCleanupJobs.TryAdd(job.Id, 0))
        {
            return;
        }

        AppendLog(job, "stale recovery: cleanup auto-move queued");
        _ = Task.Run(() => RunStaleRecoveryCleanupAsync(job));
    }

    private async Task RunStaleRecoveryCleanupAsync(AutoTagJob job)
    {
        try
        {
            if (!await WaitForDownloadsToIdleForRecoveryAsync(CancellationToken.None))
            {
                var skippedSummary = new AutoTagMoveSummary
                {
                    RecoveryCleanup = true,
                    Error = "cleanup auto-move skipped: downloads remained active."
                };
                ApplyAutoMoveSummary(job, skippedSummary);
                return;
            }

            if (_disableAutoMove)
            {
                var disabledSummary = new AutoTagMoveSummary
                {
                    RecoveryCleanup = true,
                    Error = "cleanup auto-move skipped: disabled by configuration."
                };
                ApplyAutoMoveSummary(job, disabledSummary);
                return;
            }

            AppendLog(job, "stale recovery: cleanup auto-move starting");
            var runtimeConfigPath = TryFindRuntimeConfigPath(job.Id, "base");
            var organizerOptions = string.IsNullOrWhiteSpace(runtimeConfigPath)
                ? new AutoTagOrganizerOptions()
                : LoadOrganizerOptions(runtimeConfigPath);
            var summary = await _downloadMoveService.MoveForRootWithSummaryAsync(
                job.RootPath!,
                organizerOptions,
                Array.Empty<string>(),
                Array.Empty<string>(),
                CancellationToken.None);
            summary.RecoveryCleanup = true;
            ApplyAutoMoveSummary(job, summary);
            AppendLog(job, "stale recovery: cleanup auto-move finished");

            if (summary.MovedCount > 0 && string.IsNullOrWhiteSpace(summary.Error))
            {
                var plexRefreshRequestedAfterMove = await TriggerPlexScanAfterMoveAsync(job, CancellationToken.None);
                await TriggerLibraryScanAfterAutoMovePlexRefreshRequestedAsync(
                    job,
                    plexRefreshRequestedAfterMove,
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // No-op: service-level recovery path is best-effort.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag stale recovery cleanup failed for {JobId}.", job.Id);
            var failedSummary = new AutoTagMoveSummary
            {
                RecoveryCleanup = true,
                FailedCount = 1,
                Error = ex.Message
            };
            ApplyAutoMoveSummary(job, failedSummary);
            AppendLog(job, $"stale recovery: cleanup auto-move failed: {ex.Message}");
        }
        finally
        {
            _staleRecoveryCleanupJobs.TryRemove(job.Id, out _);
        }
    }

    private async Task<bool> WaitForDownloadsToIdleForRecoveryAsync(CancellationToken cancellationToken)
    {
        const int attempts = 12;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (!await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
            {
                return true;
            }

            if (attempt == attempts - 1)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        return false;
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
