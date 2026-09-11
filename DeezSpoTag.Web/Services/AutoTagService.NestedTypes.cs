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

public partial class AutoTagService
{
    private readonly record struct EnhancementBuildContext(string RunIntent, string JobId);
    private readonly record struct AutoMoveExecutionResult(bool Completed, AutoTagMoveSummary Summary);
    private sealed record ResumeCheckpointSeed(
        string SourceJobId,
        string ResumeJobId,
        DateTimeOffset StartedAt,
        AutoTagResumeCheckpoint Checkpoint);
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

    public sealed record StartJobOptions(
        string Trigger = AutoTagLiterals.ManualTrigger,
        TechnicalTagSettings? TechnicalOverride = null,
        string? ProfileId = null,
        string? ProfileName = null,
        string? RunIntent = null,
        FolderStructureSettings? FolderStructureOverride = null,
        string? EnhancementFeature = null,
        string? EnhancementGroupId = null,
        string? ResumeFromJobId = null);

    /// <summary>
    /// Outcome of an explicit resume attempt for API surfacing.
    /// </summary>
    public sealed record ResumeJobOutcome(bool Success, string? Error, string? ResumedJobId);

    public sealed record StopJobOutcome(bool Stopped, string? Status);

    private readonly record struct StageRunResult(bool Success);

    private readonly record struct StageExecutionResult(bool Success);

    private sealed class SuccessPostProcessingContext
    {
        public required string ConfigPath { get; init; }
        public required bool IncludesEnrichmentStage { get; init; }
        public required bool IncludesEnhancementStage { get; init; }
        public required bool IncludesEnhancementWorkflows { get; init; }
        public required Dictionary<string, FileTagOutcome> FileOutcomes { get; init; }
    }
}
