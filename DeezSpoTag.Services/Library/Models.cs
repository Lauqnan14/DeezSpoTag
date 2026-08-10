using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Library;

public sealed record LibrarySettingsDto(
    bool LivePreviewIngest,
    bool EnableSignalAnalysis);

public sealed record FolderDto(
    long Id,
    string RootPath,
    string DisplayName,
    bool Enabled,
    long? LibraryId,
    string? LibraryName,
    string DesiredQuality,
    string? AutoTagProfileId,
    bool AutoTagEnabled,
    bool ConvertEnabled,
    string? ConvertFormat,
    string? ConvertBitrate);

public sealed record FolderLibraryScopeDto(
    long FolderId,
    long LibraryId);

public sealed record HistoryTrackScopeResolution(
    long? TrackId,
    long? FolderId,
    long? LibraryId,
    string MatchType,
    string Reason)
{
    public bool Resolved => TrackId.HasValue && FolderId.HasValue && LibraryId.HasValue;
    public bool Ambiguous => string.Equals(MatchType, "ambiguous", StringComparison.Ordinal);
}

public sealed record LibraryDto(long Id, string Name);

public sealed record MixSummaryDto(
    string Id,
    string Name,
    string Description,
    int TrackCount,
    IReadOnlyList<string> CoverUrls,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long LibraryId);

public sealed record MixTrackDto(
    long TrackId,
    string Title,
    string ArtistName,
    string AlbumTitle,
    string? CoverPath,
    int? DurationMs,
    long? AudioFileId = null,
    string? FilePath = null,
    string? AudioVariant = null,
    string? VariantKey = null);

public sealed record MixDetailDto(
    MixSummaryDto Summary,
    IReadOnlyList<MixTrackDto> Tracks);

public sealed record RadioStationDto(
    string Id,
    string Name,
    string Description,
    string Type,
    string? Value,
    int TrackCount);

public sealed record RadioDetailDto(
    RadioStationDto Station,
    IReadOnlyList<MixTrackDto> Tracks);

public sealed record RecommendationStationDto(
    string Id,
    string Name,
    string Description,
    string Type,
    string? Value,
    int TrackCount,
    string? ImageUrl = null,
    string Status = "ready",
    IReadOnlyList<string>? ReasonCodes = null,
    string? Message = null);

public sealed record RecommendationArtistDto(
    string Id,
    string Name);

public sealed record RecommendationAlbumDto(
    string Id,
    string Title,
    string CoverMedium);

public sealed record RecommendationTrackDto(
    string Id,
    string Title,
    int Duration,
    string Isrc,
    int TrackPosition,
    RecommendationArtistDto Artist,
    RecommendationAlbumDto Album);

public sealed record RecommendationDetailDto(
    RecommendationStationDto Station,
    IReadOnlyList<RecommendationTrackDto> Tracks,
    DateTimeOffset GeneratedAtUtc,
    string Status = "ready",
    IReadOnlyList<string>? ReasonCodes = null,
    string? Message = null);

public sealed record RecommendationRejectionUpsertInput(
    long LibraryId,
    long? FolderId,
    string StationId,
    string TrackSourceId,
    string? Isrc,
    string? Title,
    string? Artist);

public sealed record RecommendationGenerationStateDto(
    long LibraryId,
    long FolderId,
    string StationId,
    DateOnly TargetDay,
    string Status,
    string? ReasonCode,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? LastError,
    int AttemptCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record RecommendationGenerationStateKey(
    long LibraryId,
    long FolderId,
    string StationId,
    DateOnly TargetDay);

public sealed record ManualUnavailableTrackDto(
    long Id,
    string QueueUuid,
    string Title,
    string Artist,
    string? Album,
    string? AlbumArtist,
    string? Isrc,
    string? Engine,
    string? SourceService,
    string? SourceUrl,
    string? DeezerId,
    string? SpotifyId,
    string? AppleId,
    string? QobuzId,
    string? TidalId,
    string? AmazonId,
    long? DestinationFolderId,
    string? ExpectedFinalPath,
    string? Quality,
    string? ContentType,
    string? Reason,
    string? PayloadJson,
    DateTimeOffset FirstUnavailableAtUtc,
    DateTimeOffset NextRetryAtUtc,
    DateTimeOffset AddedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ManualUnavailableTrackUpsertInput(
    string QueueUuid,
    string Title,
    string Artist,
    string? Album,
    string? AlbumArtist,
    string? Isrc,
    string? Engine,
    string? SourceService,
    string? SourceUrl,
    string? DeezerId,
    string? SpotifyId,
    string? AppleId,
    string? QobuzId,
    string? TidalId,
    string? AmazonId,
    long? DestinationFolderId,
    string? ExpectedFinalPath,
    string? Quality,
    string? ContentType,
    string? Reason,
    string? PayloadJson,
    DateTimeOffset? NextRetryAtUtc = null);

public sealed record ShazamTrackCacheDto(
    long TrackId,
    string Status,
    string? ShazamTrackId,
    string? Title,
    string? Artist,
    string? Isrc,
    IReadOnlyList<RecommendationTrackDto> RelatedTracks,
    DateTimeOffset? ScannedAtUtc,
    string? Error,
    string? FilePath = null,
    long? FileSize = null,
    DateTimeOffset? FileModifiedUtc = null,
    string? SpotifyId = null,
    string? AppleId = null,
    string? DeezerId = null,
    string? Album = null,
    string? ReleaseDate = null,
    bool? Explicit = null);

public sealed record LibraryShazamScanStatusDto(
    long LibraryId,
    int TotalTracks,
    int CachedTracks,
    int MatchedTracks,
    int NoMatchTracks,
    int ErrorTracks,
    int PendingTracks,
    DateTimeOffset? LastScannedAtUtc,
    bool Running);

public sealed record DecadeBucketDto(int Decade, int TrackCount);

public sealed record TrackAnalysisInputDto(
    long TrackId,
    long? LibraryId,
    string FilePath,
    int? DurationMs,
    IReadOnlyList<string>? AlternateFilePaths = null);

public sealed record TrackAnalysisResultDto(
    long TrackId,
    long? LibraryId,
    string Status,
    double? Energy,
    double? Rms,
    double? ZeroCrossing,
    double? SpectralCentroid,
    double? Bpm,
    DateTimeOffset? AnalyzedAtUtc,
    string? Error,
    string? AnalysisMode,
    string? AnalysisVersion,
    IReadOnlyList<string>? MoodTags,
    double? MoodHappy,
    double? MoodSad,
    double? MoodRelaxed,
    double? MoodAggressive,
    double? MoodParty,
    double? MoodAcoustic,
    double? MoodElectronic,
    double? Valence,
    double? Arousal,
    int? BeatsCount,
    string? Key,
    string? KeyScale,
    double? KeyStrength,
    double? Loudness,
    double? DynamicRange,
    double? Danceability,
    double? Instrumentalness,
    double? Acousticness,
    double? Speechiness,
    double? DanceabilityMl,
    IReadOnlyList<string>? EssentiaGenres,
    IReadOnlyList<string>? LastfmTags,
    // Vibe analysis - new Essentia model fields
    double? Approachability,
    double? Engagement,
    double? VoiceInstrumental,
    double? TonalAtonal,
    double? ValenceMl,
    double? ArousalMl,
    double? DynamicComplexity,
    double? LoudnessMl);

public sealed record PlayHistoryEntryDto(
    long TrackId,
    DateTimeOffset PlayedAtUtc,
    int PlayCount = 1);

public sealed record PlexTrackMetadataDto(
    long TrackId,
    string? PlexRatingKey,
    int? UserRating,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Moods,
    DateTimeOffset? UpdatedAtUtc);

public sealed record PlexTrackMetadataUpsertDto(
    long TrackId,
    string PlexRatingKey,
    DateTimeOffset UpdatedAtUtc);

public sealed record AnalysisStatusDto(
    int TotalTracks,
    int AnalyzedTracks,
    int PendingTracks,
    int ErrorTracks,
    DateTimeOffset? LastRunUtc);

public sealed record LatestTrackAnalysisDto(
    MixTrackDto Track,
    TrackAnalysisResultDto Analysis);

public sealed record VibeMatchTrackDto(
    long TrackId,
    string Title,
    string ArtistName,
    string AlbumTitle,
    string? CoverPath,
    int? DurationMs,
    double Score,
    string? AnalysisMode,
    double? Energy,
    double? Bpm,
    double? Valence,
    double? Arousal,
    double? Danceability,
    IReadOnlyList<string>? MoodTags);

public sealed record VibeMatchResponseDto(
    long SourceTrackId,
    string? SourceTitle,
    string? SourceArtist,
    TrackAnalysisResultDto? SourceAnalysis,
    IReadOnlyList<VibeMatchTrackDto> Matches);

public sealed record VibeOverlayFeatureDto(
    string Key,
    string Label,
    double? Value,
    double? Min,
    double? Max,
    string? Unit);

public sealed record QualityScanTrackDto(
    long TrackId,
    string Title,
    string ArtistName,
    string AlbumTitle,
    string Isrc,
    int? DurationMs,
    int BestQualityRank,
    int DesiredQualityRank,
    string DesiredQualityValue,
    long? DestinationFolderId,
    int? BestFormatRank,
    string BestFormatTier,
    string? BestCodec,
    string? BestExtension,
    int? BestBitrateKbps,
    int? BestBitsPerSample,
    int? BestSampleRateHz,
    long AlbumId,
    long AlbumArtistId,
    long AudioFileId,
    string AudioFilePath,
    int? DiscNumber,
    int? TrackNumber,
    int? TrackTotal);

public sealed record MissingCoreMetadataFileDto(
    long TrackId,
    long AudioFileId,
    long FolderId,
    string FilePath,
    IReadOnlyList<string> MissingFields,
    int RepairScore = 0);

public sealed record QualityScannerAutomationSettingsDto(
    bool Enabled,
    int IntervalMinutes,
    string Scope,
    long? FolderId,
    bool QueueAtmosAlternatives,
    int CooldownMinutes,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastFinishedUtc);

public sealed record QualityScannerRunProgressDto(
    int TotalTracks,
    int ProcessedTracks,
    int QualityMet,
    int LowQuality,
    int UpgradesQueued,
    int AtmosQueued,
    int DuplicateSkipped,
    int MatchMissed);

public sealed record QualityScannerTrackStateUpdateDto(
    long TrackId,
    long? RunId,
    int BestQualityRank,
    int DesiredQualityRank,
    string LastAction,
    DateTimeOffset? LastUpgradeQueuedUtc,
    DateTimeOffset? LastAtmosQueuedUtc,
    string? LastError);

public sealed record QualityScannerActionLogDto(
    long? RunId,
    long TrackId,
    string ActionType,
    string? Source,
    string? Quality,
    string? ContentType,
    long? DestinationFolderId,
    string? QueueUuid,
    string? Message);

public sealed record VibeOverlayTrackDto(
    long TrackId,
    string Title,
    string Artist,
    string Album,
    double Score,
    IReadOnlyList<VibeOverlayFeatureDto> Features,
    IReadOnlyList<string> MoodTags);

public sealed record VibeOverlayResponseDto(
    long SourceTrackId,
    string? SourceTitle,
    string? SourceArtist,
    IReadOnlyList<VibeOverlayFeatureDto> SourceFeatures,
    IReadOnlyList<string> SourceMoodTags,
    IReadOnlyList<VibeOverlayTrackDto> Matches);

public sealed record MoodPresetDto(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> MoodTags,
    int TrackCount = 0);

public sealed record MoodMixRequestDto(
    string? PresetId,
    long? LibraryId,
    int? Limit);

public sealed record MoodMixPreferencesDto(
    string? PresetId,
    long? LibraryId,
    int? Limit);

public sealed record MoodMixResponseDto(
    string Name,
    string? Description,
    IReadOnlyList<MixTrackDto> Tracks);

public sealed record FolderAliasDto(long Id, long FolderId, string AliasName);

public sealed record LibraryLogEntry(DateTimeOffset TimestampUtc, string Level, string Message);

public sealed record LibraryScanInfo(DateTimeOffset? LastRunUtc, int ArtistCount, int AlbumCount, int TrackCount);
public sealed record LibraryRecommendationSeedTrackDto(
    long TrackId,
    string Title,
    string Artist,
    string Album,
    int? DurationMs,
    string? Isrc,
    string? DeezerTrackId);
public sealed record LibraryStatsDto(
    int TotalArtists,
    int TotalAlbums,
    int TotalTracks,
    IReadOnlyList<LibraryStatsLibraryDto> Libraries,
    int TotalVideoItems = 0,
    int TotalPodcastItems = 0,
    LibraryStatsDetailDto? Detail = null);
public sealed record LibraryStatsLibraryDto(
    long LibraryId,
    string Name,
    int ArtistCount,
    int AlbumCount,
    int TrackCount,
    int VideoItemCount = 0,
    int PodcastItemCount = 0,
    int MusicFolderCount = 0,
    int VideoFolderCount = 0,
    int PodcastFolderCount = 0,
    int UnmetQualityCount = 0,
    int NoLyricsCount = 0);
public sealed record LibraryStatsBreakdownItemDto(string Value, int Count);
public sealed record LibraryStatsSourceCoverageDto(
    int DeezerTrackIds,
    int SpotifyTrackIds,
    int AppleTrackIds,
    int DeezerUrls,
    int SpotifyUrls,
    int AppleUrls);
public sealed record LibraryStatsDetailDto(
    int TracksWithLyrics,
    int TracksWithSyncedLyrics,
    int TracksWithUnsyncedLyrics,
    int TracksWithBothLyrics,
    int AlbumsWithAnimatedArtwork,
    LibraryStatsSourceCoverageDto SourceCoverage,
    IReadOnlyList<LibraryStatsBreakdownItemDto> Extensions,
    IReadOnlyList<LibraryStatsBreakdownItemDto> BitDepths,
    IReadOnlyList<LibraryStatsBreakdownItemDto> SampleRates,
    IReadOnlyList<LibraryStatsBreakdownItemDto> TechnicalProfiles,
    IReadOnlyList<LibraryStatsBreakdownItemDto> LyricsTypes);
public sealed record LibraryClearResultDto(int ArtistsRemoved, int AlbumsRemoved, int TracksRemoved);

public sealed record ArtistDto(
    long Id,
    string Name,
    bool AvailableLocally,
    string? PreferredImagePath,
    string? PreferredBackgroundPath,
    string? AppleBiography = null,
    DateTimeOffset? AppleBiographyCheckedAt = null,
    DateTimeOffset? LastFmImagesCheckedAt = null);
public sealed record ArtistPageDto(IReadOnlyList<ArtistDto> Items, int TotalCount, int Page, int PageSize);

public sealed record ArtistExternalMetadataBackfillDto(
    long Id,
    string Name,
    string? AppleBiography,
    DateTimeOffset? AppleBiographyCheckedAt,
    DateTimeOffset? LastFmImagesCheckedAt,
    string? AppleId);

public sealed record AlbumDto(
    long Id,
    long ArtistId,
    string Title,
    string? PreferredCoverPath,
    IReadOnlyList<string> LocalFolders,
    bool HasStereoVariant = false,
    bool HasAtmosVariant = false,
    int LocalTrackCount = 0,
    int LocalStereoTrackCount = 0,
    int LocalAtmosTrackCount = 0);

public sealed record AlbumDetailDto(long Id, long ArtistId, string Title, string? PreferredCoverPath, IReadOnlyList<string> LocalFolders);

public sealed record TrackDto(long Id, long AlbumId, string Title, int? DurationMs, int? Disc, int? TrackNo, bool AvailableLocally, string? LyricsStatus);
public sealed record AlbumTrackAudioInfoDto(
    long TrackId,
    long? AudioFileId,
    string? AudioVariant,
    string? Codec,
    string? Extension,
    int? BitrateKbps,
    int? SampleRateHz,
    int? BitsPerSample,
    int? Channels,
    int? QualityRank,
    string? FilePath,
    bool HasStereoVariant,
    bool HasAtmosVariant);
public sealed record TrackSourceLinksDto(
    string? DeezerTrackId,
    string? SpotifyTrackId,
    string? AppleTrackId,
    string? Isrc,
    string? DeezerUrl,
    string? SpotifyUrl,
    string? AppleUrl);

public sealed record ArtistSpotifyMatchSignalDto(
    long TrackId,
    string Title,
    string? SpotifyTrackId,
    string? Isrc,
    string? TagArtist,
    string? TagAlbumArtist);

public sealed record ArtistDetailDto(
    long Id,
    string Name,
    string? PreferredImagePath,
    string? PreferredBackgroundPath,
    string? AppleBiography = null,
    DateTimeOffset? AppleBiographyCheckedAt = null,
    DateTimeOffset? LastFmImagesCheckedAt = null);

public sealed record TrackSearchResultDto(
    long TrackId,
    string Title,
    string ArtistName,
    string AlbumTitle,
    int? DurationMs,
    string? CoverPath);

public sealed record TrackAudioInfoDto(
    long TrackId,
    string Title,
    string ArtistName,
    string AlbumTitle,
    int? DurationMs,
    string FilePath,
    string? CoverPath,
    long DestinationFolderId = 0);

public sealed record OfflineTrackSearchDto(
    string Title,
    string ArtistName,
    string AlbumTitle,
    string? CoverPath,
    string? DeezerId);

public sealed record OfflineAlbumSearchDto(
    string Title,
    string ArtistName,
    string? CoverPath,
    string? DeezerId);

public sealed record OfflineArtistSearchDto(
    string Name,
    string? ImagePath,
    string? DeezerId);

public sealed record WatchlistArtistDto(
    long ArtistId,
    string ArtistName,
    string? SpotifyId,
    string? DeezerId,
    string? AppleId,
    string? ArtistImagePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastCheckedUtc = null,
    long? DestinationFolderId = null,
    IReadOnlyList<string>? WatchedAlbumGroups = null,
    bool? TopSongsEnabled = null,
    bool? LatestReleasesOnly = null,
    string? PreferredEngine = null,
    IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules = null,
    long? AtmosDestinationFolderId = null,
    string? DownloadVariantMode = null,
    string? TopSongsSyncMode = null,
    bool? DownloadDiscographyEnabled = null,
    IReadOnlyList<PlaylistTrackBlockRule>? IgnoreRules = null,
    string? QobuzId = null,
    string? TidalId = null);

public sealed record PlaylistWatchlistDto(
    long Id,
    string Source,
    string SourceId,
    string Name,
    string? ImageUrl,
    string? Description,
    int? TrackCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastCheckedUtc = null,
    string? SnapshotId = null,
    string? LastRunStatus = null,
    string? LastRunMessage = null,
    DateTimeOffset? NextAttemptUtc = null,
    int? ConsecutiveFailures = null,
    int? SyncPriority = null,
    int? SyncedTrackCount = null,
    int? IncompleteTrackCount = null,
    int? IgnoredBlockedTrackCount = null,
    int? ReroutedTrackCount = null,
    string? OwnerName = null,
    int? EligibleTrackCount = null,
    int? QueuedTrackCount = null,
    int? DownloadingTrackCount = null,
    int? UnavailableTrackCount = null,
    int? ReviewTrackCount = null,
    string? SourceUrl = null,
    string? SourceStorefront = null);

public sealed record PlaylistWatchlistMetadataInput(
    string? Name,
    string? ImageUrl,
    string? Description,
    int? TrackCount,
    bool ClearImageUrl = false,
    string? OwnerName = null,
    string? SourceUrl = null,
    string? SourceStorefront = null);

public sealed record PlaylistTrackRoutingRule(
    string ConditionField,
    string ConditionOperator,
    string ConditionValue,
    long DestinationFolderId,
    int Order);

public sealed record PlaylistTrackBlockRule(
    string ConditionField,
    string ConditionOperator,
    string ConditionValue,
    int Order);

public sealed record PlaylistWatchPreferenceDto(
    string Source,
    string SourceId,
    long? DestinationFolderId,
    string? Service,
    IReadOnlyList<string>? SyncTargets,
    string? PreferredEngine,
    DownloadEngineOrderSettings? DownloadEngineOrder,
    string? DownloadVariantMode,
    string? SyncMode,
    bool UpdateArtwork,
    bool ReuseSavedArtwork,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules = null,
    IReadOnlyList<PlaylistTrackBlockRule>? IgnoreRules = null,
    long? AtmosDestinationFolderId = null,
    string? PlexPlaylistId = null,
    string? JellyfinPlaylistId = null,
    string? NavidromePlaylistId = null);

public sealed record PlaylistWatchArtworkStateDto(
    string Source,
    string SourceId,
    string? RemoteIdentity,
    string? StillContentHash,
    string? StillLocalPath,
    string? AnimatedContentHash,
    string? AnimatedLocalPath,
    string Status,
    string? LastError,
    DateTimeOffset? LastCheckedUtc,
    string? Revision);

public sealed record PlaylistWatchStateDto(
    string Source,
    string SourceId,
    string? SnapshotId,
    int? TrackCount,
    int? BatchNextOffset,
    string? BatchProcessingSnapshotId,
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset UpdatedAt,
    string? LastRunStatus = null,
    string? LastRunMessage = null,
    DateTimeOffset? NextAttemptUtc = null,
    int? ConsecutiveFailures = null,
    string? CurrentPhase = null,
    int? CurrentTrackIndex = null,
    int? CurrentTrackTotal = null,
    DateTimeOffset? HeartbeatUtc = null,
    DateTimeOffset? DeadlineUtc = null);

public sealed record PlaylistWatchTrackStatusSummaryDto(
    int QueuedCount,
    int CompletedCount,
    int FailedCount,
    int ActiveCount,
    int UnresolvedCount);

public sealed record WatchlistSchedulerStateDto(
    string WatchType,
    string? ActiveSource,
    string? ActiveSourceId,
    DateTimeOffset? ActiveStartedUtc,
    DateTimeOffset? LastProgressUtc,
    DateTimeOffset UpdatedAt);

public sealed record WatchlistSourceCircuitStateDto(
    string WatchType,
    string Source,
    bool IsOpen,
    DateTimeOffset? OpenUntilUtc,
    string? Reason,
    string? Fingerprint,
    int FailureCount,
    DateTimeOffset UpdatedAt);

// Mirrors WatchlistSourceCircuitStateDto but keyed by target media server (plex/jellyfin/
// navidrome) instead of source -- so repeated failures against one down/flaky target back off
// as a single unit instead of every playlist's sync job against it retrying independently.
public sealed record WatchlistTargetCircuitStateDto(
    string TargetService,
    bool IsOpen,
    DateTimeOffset? OpenUntilUtc,
    string? Reason,
    int FailureCount,
    DateTimeOffset UpdatedAt);

public sealed record PlaylistTrackCandidateCacheDto(
    string Source,
    string SourceId,
    string? SnapshotId,
    string CandidatesJson,
    DateTimeOffset UpdatedAt,
    int SchemaVersion = 0,
    string? IdentityRevision = null,
    string? ProviderReadinessRevision = null,
    bool IsComplete = false);

public sealed record BoomplayDeezerTrackMappingDto(
    string BoomplayTrackId,
    string? DeezerTrackId,
    string? Isrc,
    string Title,
    string Artist,
    string Album,
    string? CoverUrl,
    int? DurationMs,
    string SourceFingerprint,
    string MatcherVersion,
    string Status,
    string? LastError,
    DateTimeOffset? NextRetryUtc,
    DateTimeOffset UpdatedAt);

public sealed record PlaylistWatchTrackStatusDto(
    string TrackSourceId,
    string? Isrc,
    string Status,
    DateTimeOffset UpdatedAt,
    string? UnavailableReason,
    DateTimeOffset? UnavailableSinceUtc,
    DateTimeOffset? UnavailableLastCheckedUtc,
    DateTimeOffset? UnavailableNextRecheckUtc,
    string? UnavailableSettingsFingerprint,
    long? LocalTrackId = null,
    string? IdentityStatus = null,
    string? IdentityReason = null,
    string? TargetService = null,
    string? TargetPlaylistId = null,
    string? TargetItemId = null,
    string? SyncStatus = null,
    string? RedirectTrackSourceId = null,
    string? RedirectReason = null,
    DateTimeOffset? VerifiedAtUtc = null,
    int? SourcePosition = null,
    string? SyncedTargetServices = null);

public sealed record PlaylistWatchTrackVerification(
    string TrackSourceId,
    long? LocalTrackId,
    string IdentityStatus,
    string? IdentityReason = null,
    string? RedirectTrackSourceId = null,
    string? RedirectReason = null);

public sealed record PlaylistWatchTargetMembership(
    string TrackSourceId,
    long LocalTrackId,
    string TargetItemId);

public sealed record LocalTrackIdentityDto(
    long TrackId,
    string Title,
    string Artist,
    string Album,
    int? DurationMs,
    string? Isrc,
    IReadOnlyDictionary<string, string> SourceIds);

public sealed record PlaylistWatchTrackInsert(
    string TrackSourceId,
    string? Isrc,
    int? SourcePosition = null,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    int? DurationMs = null,
    string? CoverUrl = null,
    string? CandidateRevision = null,
    string? SnapshotId = null);

public sealed record PlaylistWatchDownloadClaimDto(
    string Source,
    string SourceId,
    string TrackSourceId,
    string QueueUuid,
    long? DestinationFolderId,
    string Status,
    DateTimeOffset UpdatedAt);

public sealed record WatchlistSyncJobDto(
    long Id,
    string Source,
    string PlaylistId,
    string TrackId,
    string TargetService,
    long? DestinationFolderId,
    IReadOnlyList<string> FinalFilePaths,
    int AttemptCount,
    DateTimeOffset NextAttemptUtc,
    string? QueueUuid = null,
    string? LeaseOwner = null,
    string Status = "pending",
    string? LastError = null,
    string? SnapshotId = null);

public sealed record WatchlistSyncJobStatusCounts(
    int Due,
    int Processing,
    int RetryWaiting,
    int ExpiredProcessing = 0,
    int RepairRequired = 0,
    int Blocked = 0,
    DateTimeOffset? OldestPendingUtc = null,
    string? LastError = null);

public sealed record WatchlistFinalizationOutboxDto(
    long Id,
    string QueueUuid,
    string? PayloadJson,
    IReadOnlyList<string> FinalFilePaths,
    string Status,
    int AttemptCount,
    DateTimeOffset NextAttemptUtc,
    string? LeaseOwner,
    DateTimeOffset? LeaseUntilUtc,
    string? LastError,
    DateTimeOffset UpdatedAt);

public sealed record MediaServerRefreshOutboxDto(
    long Id,
    long DestinationFolderId,
    string TargetService,
    IReadOnlyList<string> ChangedFilePaths,
    string Status,
    int AttemptCount,
    DateTimeOffset NextAttemptUtc,
    string? LeaseOwner,
    DateTimeOffset? LeaseUntilUtc,
    string? LastError,
    DateTimeOffset UpdatedAt);

public sealed record WatchlistReconciliationRequestDto(
    string Kind,
    string Source,
    string Identifier,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Status = "pending",
    int AttemptCount = 0,
    DateTimeOffset? NextAttemptUtc = null,
    string? LeaseOwner = null,
    DateTimeOffset? LeaseUntilUtc = null,
    string? LastError = null);

public sealed record MediaServerTrackMetadataUpsertDto(
    long TrackId,
    string Service,
    string TargetItemId,
    string? FilePath,
    DateTimeOffset UpdatedAtUtc,
    string? AudioVariant = null);

public sealed record ArtistWatchStateDto(
    long ArtistId,
    string? SpotifyId,
    int? BatchNextOffset,
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset UpdatedAt,
    string? LastRunStatus = null,
    string? LastRunMessage = null,
    DateTimeOffset? NextAttemptUtc = null,
    int? ConsecutiveFailures = null,
    string? CurrentPhase = null,
    DateTimeOffset? HeartbeatUtc = null,
    DateTimeOffset? DeadlineUtc = null,
    int? AppleNextOffset = null,
    int? DeezerNextOffset = null,
    int? TidalNextOffset = null);

public sealed record ArtistWatchAlbumInsert(string Source, string AlbumSourceId);

public sealed record PlaylistWatchIgnoreInsert(string TrackSourceId, string? Isrc);

public sealed record DownloadBlocklistEntryDto(
    long Id,
    string Field,
    string Value,
    bool Enabled,
    DateTimeOffset CreatedAt);

public sealed record DownloadBlocklistMatchDto(string Field, string Value);

public sealed record WatchlistHistoryDto(
    long Id,
    string Source,
    string WatchType,
    string SourceId,
    string Name,
    string CollectionType,
    int TrackCount,
    string Status,
    string? ArtistName,
    DateTimeOffset CreatedAt,
    string? ItemKey = null);

public sealed record WatchlistHistoryInsert(
    string Source,
    string WatchType,
    string SourceId,
    string Name,
    string CollectionType,
    int TrackCount,
    string Status,
    string? ArtistName,
    string? ItemKey = null);

public sealed record LocalArtistScanDto(string Name, string? ImagePath);

public sealed record LocalAlbumScanDto(
    string ArtistName,
    string Title,
    string? PreferredCoverPath,
    IReadOnlyList<string> LocalFolders,
    bool HasAnimatedArtwork = false);

public sealed record LocalTrackOtherTag(string Key, string Value);

public sealed record LocalTrackScanDto(
    string ArtistName,
    string AlbumTitle,
    string Title,
    string FilePath,
    string? TagTitle,
    string? TagArtist,
    string? TagAlbum,
    string? TagAlbumArtist,
    string? TagVersion,
    string? TagLabel,
    string? TagCatalogNumber,
    int? TagBpm,
    string? TagKey,
    int? TagTrackTotal,
    int? TagDurationMs,
    int? TagYear,
    int? TagTrackNo,
    int? TagDisc,
    string? TagGenre,
    string? TagIsrc,
    string? TagReleaseDate,
    string? TagPublishDate,
    string? TagUrl,
    string? TagReleaseId,
    string? TagTrackId,
    string? TagMetaTaggedDate,
    string? LyricsUnsynced,
    string? LyricsSynced,
    IReadOnlyList<string> TagGenres,
    IReadOnlyList<string> TagStyles,
    IReadOnlyList<string> TagMoods,
    IReadOnlyList<string> TagRemixers,
    IReadOnlyList<LocalTrackOtherTag> TagOtherTags,
    int? TrackNo,
    int? Disc,
    int? DurationMs,
    string? LyricsStatus,
    string? LyricsType,
    string? Codec,
    int? BitrateKbps,
    int? SampleRateHz,
    int? BitsPerSample,
    int? Channels,
    int? QualityRank,
    string? AudioVariant,
    string? DeezerTrackId,
    string? Isrc,
    string? DeezerAlbumId,
    string? DeezerArtistId,
    string? SpotifyTrackId,
    string? SpotifyAlbumId,
    string? SpotifyArtistId,
    string? AppleTrackId,
    string? AppleAlbumId,
    string? AppleArtistId,
    string? Source,
    string? SourceId,
    bool IsUnchanged = false);

public sealed record LocalScanFileState(
    string FilePath,
    string RelativePath,
    long Size,
    DateTime LastWriteUtc,
    LocalTrackScanDto Scan);

public sealed record WatchlistStateDriftReport(
    int AppliedWithoutMembership,
    int MembershipWithoutApplied,
    int OrphanedMembership,
    int MembershipForUnconfiguredTarget,
    int BlockedBelowAttemptCap)
{
    public int Total => AppliedWithoutMembership
        + MembershipWithoutApplied
        + OrphanedMembership
        + MembershipForUnconfiguredTarget;

    public bool HasDrift => Total > 0;
}
