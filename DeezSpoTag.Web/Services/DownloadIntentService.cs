using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.Buffers;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Metadata.Qobuz;
using DeezSpoTag.Core.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Deezer;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadIntentService
{
    private sealed record EnqueuePreparation(
        DeezSpoTagSettings Settings,
        bool IsPodcastIntent,
        long? MetadataDestinationFolderId);

    private sealed record EnqueueRoutingState(
        string NormalizedPreferredEngine,
        bool IntentRequestsAuto,
        bool AppleOnlyRequired,
        List<string> AutoSources,
        string PreferredEngine,
        string? TargetQuality,
        PlatformLinkResult? Availability,
        bool UseAtmosStereoDual);

    private sealed record InitialContentRoutingState(
        bool ExplicitAtmosRequest,
        bool ExplicitStereoRequest,
        string? TargetQuality);

    private sealed record ResolvedEnqueueTarget(
        string Engine,
        string? SelectedQuality,
        int SelectedAutoIndex,
        bool AllowCrossEngineFallback,
        string AvailabilityWarning,
        (string Engine, string? SourceUrl, string Message, string MappingSource) Resolution,
        List<string> AutoSources);

    private sealed record PayloadIdentity(
        string? Isrc,
        string? DeezerTrackId,
        string? DeezerAlbumId,
        string? DeezerArtistId,
        string? SpotifyTrackId,
        string? SpotifyAlbumId,
        string? SpotifyArtistId,
        string? AppleTrackId,
        string? AppleAlbumId,
        string? AppleArtistId,
        string? QobuzTrackId,
        string? TidalTrackId,
        string? AmazonTrackId,
        string Engine,
        string? ContentType,
        int? DurationMs,
        string TrackTitle,
        string TrackArtist,
        string? TrackPrimaryArtist,
        string? Album,
        IReadOnlyList<string>? Genres,
        bool? Explicit,
        string? ReleaseDate,
        string? PayloadQuality,
        string? PayloadQualityBucket,
        string? RequestedAudioVariant,
        long? DestinationFolderId);

    private sealed record StandardPayloadContext(
        string SourceUrl,
        string CollectionType,
        string ContentType,
        List<string> AutoSources,
        int SelectedAutoIndex,
        List<FallbackPlanStep> FallbackPlan,
        string ReleaseDate,
        int DurationSeconds,
        long? DestinationFolderId,
        string QualityBucket);

    private sealed record AtmosSecondaryEnqueueRequest(
        DownloadIntent Intent,
        DeezSpoTagSettings Settings,
        long? PrimaryDestinationFolderId,
        long? SecondaryDestinationFolderId,
        bool AllowQualityUpgrade,
        List<string> Queued,
        PlatformLinkResult? Availability,
        bool PreferIsrcOnly,
        IReadOnlyList<PlaylistTrackBlockRule>? BlockRules,
        CancellationToken CancellationToken);

    private sealed record EnqueueItemContext(
        PayloadIdentity Identity,
        DeezSpoTagSettings Settings,
        IReadOnlyList<PlaylistTrackBlockRule>? BlockRules,
        bool AllowQualityUpgrade,
        int? RequestedQualityRank,
        bool QueueQualityUpgradeRequested,
        int RequestedRank,
        int? RequestedLocalQualityRank,
        bool LocalQualityUpgradeRequested);

    private sealed record EnqueueResolutionState(
        DeezSpoTagSettings Settings,
        EnqueueRoutingState Routing,
        ResolvedEnqueueTarget ResolvedTarget);

    private sealed record EnqueueFallbackRequest(
        DownloadIntent Intent,
        DeezSpoTagSettings Settings,
        string TargetEngine,
        string? Quality,
        bool MusicIntent,
        bool AllowCrossEngineFallback,
        bool UseAtmosStereoDual,
        List<string> AutoSources,
        PlatformLinkResult? Availability);
    private sealed record DestinationRoutingResult(
        long? PrimaryDestinationFolderId,
        long? SecondaryDestinationFolderId,
        DownloadIntentResult? Failure);

    private sealed record IntentResolutionBootstrap(
        string SourceUrl,
        bool IsPodcastIntent,
        string? NormalizedDeezerId);

    private const string SpotifyPlatform = "spotify";
    private const string AutoService = "auto";
    private const string AtmosQuality = "atmos";
    private const string AtmosQualityUpper = "ATMOS";
    private const string TidalAtmosQuality = "DOLBY_ATMOS";
    private const string ApplePlatform = "apple";
    private const string DeezerPlatform = "deezer";
    private const string TidalPlatform = "tidal";
    private const string AmazonPlatform = "amazon";
    private const string QobuzPlatform = "qobuz";
    private const string TrackType = "track";
    private const string EpisodeType = "episode";
    private const string AlbumType = "album";
    private const string StereoType = "stereo";
    private const string EnglishUsLocale = "en-US";
    private const string SongsField = "songs";
    private const string PlatformLinkSpotifyKey = "platform-link-spotify";
    private const string AppleMusicDomain = "music.apple.com";
    private const string DeezerDomain = "deezer.com";
    private const string QobuzDomain = "qobuz.com";
    private const string AttributesField = "attributes";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly SearchValues<char> QueryFragmentSeparators = SearchValues.Create("?#");
    private static readonly string[] AllIdentityEngines =
    {
        DeezerPlatform,
        SpotifyPlatform,
        ApplePlatform,
        QobuzPlatform,
        TidalPlatform,
        AmazonPlatform
    };
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DownloadOrchestrationService _orchestrationService;
    private readonly IDeezSpoTagListener _deezspotagListener;
    private readonly IServiceProvider _serviceProvider;
    private readonly ITrackIdentityResolver _trackIdentityResolver;
    private readonly QobuzTrackResolver _qobuzTrackResolver;
    private readonly ISpotifyIdResolver _spotifyIdResolver;
    private readonly IActivityLogWriter _activityLog;
    private readonly DeezerClient _deezerClient;
    private readonly AuthenticatedDeezerService _authenticatedDeezerService;
    private readonly TidalDownloadService _tidalDownloadService;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly AutoTag.ItunesMatcher _itunesMatcher;
    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly SpotifyPathfinderMetadataClient _spotifyPathfinderClient;
    private readonly ArtistPageCacheRepository _artistPageCacheRepository;
    private readonly IDownloadTagSettingsResolver _downloadTagSettingsResolver;
    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly AmazonMusicMetadataService _amazonMusicMetadataService;
    private readonly IDownloadApiHealthTracker _apiHealthTracker;
    private readonly DownloadDedupeService _dedupeService;
    private readonly ILogger<DownloadIntentService> _logger;
    private IReadOnlyDictionary<string, string>? _genreAliasMap;
    private IReadOnlyList<string>? _genreBlockList;
    private bool _genreTagNormalizationEnabled;

    public DownloadIntentService(
        ILogger<DownloadIntentService> logger,
        IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _queueRepository = serviceProvider.GetRequiredService<DownloadQueueRepository>();
        _settingsService = serviceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        _orchestrationService = serviceProvider.GetRequiredService<DownloadOrchestrationService>();
        _deezspotagListener = serviceProvider.GetRequiredService<IDeezSpoTagListener>();
        _trackIdentityResolver = serviceProvider.GetRequiredService<ITrackIdentityResolver>();
        _qobuzTrackResolver = serviceProvider.GetRequiredService<QobuzTrackResolver>();
        _spotifyIdResolver = serviceProvider.GetRequiredService<ISpotifyIdResolver>();
        _activityLog = serviceProvider.GetRequiredService<IActivityLogWriter>();
        _deezerClient = serviceProvider.GetRequiredService<DeezerClient>();
        _authenticatedDeezerService = serviceProvider.GetRequiredService<AuthenticatedDeezerService>();
        _tidalDownloadService = serviceProvider.GetRequiredService<TidalDownloadService>();
        _appleCatalogService = serviceProvider.GetRequiredService<AppleMusicCatalogService>();
        _itunesMatcher = serviceProvider.GetRequiredService<AutoTag.ItunesMatcher>();
        _libraryRepository = serviceProvider.GetRequiredService<LibraryRepository>();
        _spotifyMetadataService = serviceProvider.GetRequiredService<SpotifyMetadataService>();
        _spotifyPathfinderClient = serviceProvider.GetRequiredService<SpotifyPathfinderMetadataClient>();
        _artistPageCacheRepository = serviceProvider.GetRequiredService<ArtistPageCacheRepository>();
        _downloadTagSettingsResolver = serviceProvider.GetRequiredService<IDownloadTagSettingsResolver>();
        _boomplayMetadataService = serviceProvider.GetRequiredService<BoomplayMetadataService>();
        _amazonMusicMetadataService = serviceProvider.GetRequiredService<AmazonMusicMetadataService>();
        _apiHealthTracker = serviceProvider.GetRequiredService<IDownloadApiHealthTracker>();
        _dedupeService = serviceProvider.GetRequiredService<DownloadDedupeService>();
        _logger = logger;
    }

    public async Task<string?> ResolveAmazonDeezerIdAsync(
        DownloadIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);

        await TryHydrateIntentFromAmazonAsync(intent, cancellationToken);
        return NormalizeDeezerTrackId(intent.DeezerId);
    }

    public Task<DownloadIntentResult> EnqueueAsync(
        DownloadIntent intent,
        CancellationToken cancellationToken,
        bool preferIsrcOnly = false,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules = null,
        bool allowAutomaticSecondaryQuality = true)
        => EnqueueCoreAsync(
            intent,
            preferIsrcOnly,
            allowManualQueueDuringEnrichment: false,
            blockRules,
            allowAutomaticSecondaryQuality,
            cancellationToken);

    public Task<DownloadIntentResult> EnqueueManualAsync(
        DownloadIntent intent,
        CancellationToken cancellationToken,
        bool preferIsrcOnly = false,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules = null)
        => EnqueueCoreAsync(
            intent,
            preferIsrcOnly,
            allowManualQueueDuringEnrichment: true,
            blockRules,
            allowAutomaticSecondaryQuality: true,
            cancellationToken);

    public async Task<DownloadIntentResult> EnqueueManualVisibleAsync(
        DownloadIntent intent,
        CancellationToken cancellationToken,
        bool preferIsrcOnly = false,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules = null)
    {
        var gateFailure = await TryBlockByDownloadGateAsync(allowManualQueueDuringEnrichment: true, cancellationToken);
        if (gateFailure != null)
        {
            return gateFailure;
        }

        var preparation = await PrepareEnqueueAsync(intent, applyManualDownloadPreference: true, cancellationToken);
        var profileValidation = await TryValidateEnqueueProfileAsync(intent, preparation, cancellationToken);
        if (profileValidation.Failure != null)
        {
            return profileValidation.Failure;
        }

        var routingFailure = TryValidateExplicitEngineRouting(intent, preparation);
        if (routingFailure != null)
        {
            return routingFailure;
        }

        var settings = preparation.Settings;
        var engine = ResolveVisibleQueueEngine(intent, settings, preparation.IsPodcastIntent);
        if (string.IsNullOrWhiteSpace(engine))
        {
            return new DownloadIntentResult
            {
                Success = false,
                Message = "No supported download engine is available for this item.",
                Engine = string.Empty,
                SkipReasonCodes = new List<string> { "engine_unavailable" },
                SkipReasons = new List<string> { "No supported download engine is available for this item." }
            };
        }

        var selectedQuality = ApplyResolvedQuality(
            intent,
            settings,
            engine,
            string.IsNullOrWhiteSpace(intent.Quality) ? ResolvePreferredQuality(settings, engine) : intent.Quality);
        selectedQuality = ResolveEnabledDownloadQuality(settings, engine, selectedQuality);
        var useMultiQuality = IsMultiQualityDualEnabled(settings.MultiQuality);
        var destinationRouting = await ResolveDestinationRoutingAsync(
            intent,
            settings,
            useMultiQuality,
            useAtmosStereoDual: useMultiQuality && IsMusicIntent(intent) && !IsVideoIntent(intent),
            engine,
            cancellationToken);
        if (destinationRouting.Failure != null)
        {
            return destinationRouting.Failure;
        }

        intent.DestinationFolderId = destinationRouting.PrimaryDestinationFolderId;
        intent.SecondaryDestinationFolderId = destinationRouting.SecondaryDestinationFolderId;
        intent.AlbumArtist = ResolveEffectiveAlbumArtist(
            intent.AlbumArtist,
            intent.Artist,
            settings.Tags?.SingleAlbumArtist != false);

        var payload = BuildVisiblePreResolutionPayload(intent, settings, engine, selectedQuality);
        var requestedQualityRank = ParseRequestedQualityRank(selectedQuality ?? intent.Quality);
        var queued = new List<string>();
        var enqueueDecision = await EnqueueItemAsync(
            payload,
            blockRules,
            intent.AllowQualityUpgrade,
            requestedQualityRank,
            initialStatus: "queued",
            cancellationToken);
        if (!enqueueDecision.Success)
        {
            return new DownloadIntentResult
            {
                Success = false,
                Engine = engine,
                Skipped = 1,
                Message = enqueueDecision.Message,
                SkipReasonCodes = new List<string> { enqueueDecision.ReasonCode },
                SkipReasons = new List<string> { enqueueDecision.Message }
            };
        }

        var queueUuid = enqueueDecision.QueueUuid ?? payload.Id;
        queued.Add(queueUuid);
        NotifyQueueAdded(payload);
        await TryEnqueueVisibleAtmosSecondaryAsync(
            intent,
            settings,
            destinationRouting,
            blockRules,
            queued,
            cancellationToken);
        if (IsMusicIntent(intent))
        {
            _orchestrationService.MarkDownloadQueued();
        }

        return new DownloadIntentResult
        {
            Success = true,
            Engine = engine,
            Queued = queued,
            Message = $"Queued {queued.Count} item(s)."
        };
    }

    private async Task<DownloadIntentResult> EnqueueCoreAsync(
        DownloadIntent intent,
        bool preferIsrcOnly,
        bool allowManualQueueDuringEnrichment,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules,
        bool allowAutomaticSecondaryQuality,
        CancellationToken cancellationToken)
    {
        var resolution = await TryPrepareEnqueueResolutionAsync(
            intent,
            preferIsrcOnly,
            allowManualQueueDuringEnrichment,
            allowAutomaticSecondaryQuality,
            sourceSettingsSnapshot: null,
            cancellationToken);
        if (resolution.Failure != null)
        {
            return resolution.Failure;
        }

        var state = resolution.State!;
        var settings = state.Settings;
        var routing = state.Routing;
        var resolvedTarget = state.ResolvedTarget;
        var multiQuality = settings.MultiQuality;
        var useMultiQuality = IsMultiQualityDualEnabled(multiQuality);
        var engine = resolvedTarget.Engine;
        var selectedQuality = ApplyResolvedQuality(intent, settings, engine, resolvedTarget.SelectedQuality);
        selectedQuality = ResolveEnabledDownloadQuality(settings, engine, selectedQuality);
        var autoSources = resolvedTarget.AutoSources;
        var useAtmosStereoDual = routing.UseAtmosStereoDual;
        var allowCrossEngineFallback = resolvedTarget.AllowCrossEngineFallback;
        var isMusicIntent = IsMusicIntent(intent);
        var queued = new List<string>();
        var relatedQueueUuids = new List<string>();
        var skipReasonCodes = new List<string>();
        var skipReasons = new List<string>();
        var primaryFallback = BuildEnqueueFallbackInfo(new EnqueueFallbackRequest(
            intent,
            settings,
            engine,
            selectedQuality,
            isMusicIntent,
            allowCrossEngineFallback,
            useAtmosStereoDual,
            autoSources,
            routing.Availability));
        var fallbackPlan = primaryFallback.FallbackPlan;
        var selectedAutoIndex = primaryFallback.AutoIndex;

        if (fallbackPlan.Count > 0)
        {
            var planSummary = string.Join(" → ", fallbackPlan.Select(step =>
                string.IsNullOrWhiteSpace(step.Quality) ? step.Engine : $"{step.Engine}|{step.Quality}"));
            _activityLog.Info($"Fallback plan: start_index={selectedAutoIndex} steps=[{planSummary}]");
        }
        var requestedQualityRank = ParseRequestedQualityRank(selectedQuality ?? intent.Quality);
        var destinationRouting = await ResolveDestinationRoutingAsync(
            intent,
            settings,
            useMultiQuality,
            useAtmosStereoDual,
            engine,
            cancellationToken);
        if (destinationRouting.Failure != null)
        {
            return destinationRouting.Failure;
        }
        var primaryDestinationFolderId = destinationRouting.PrimaryDestinationFolderId;
        var secondaryDestinationFolderId = destinationRouting.SecondaryDestinationFolderId;
        intent.DestinationFolderId = primaryDestinationFolderId;
        intent.SecondaryDestinationFolderId = secondaryDestinationFolderId;

        intent.AlbumArtist = ResolveEffectiveAlbumArtist(
            intent.AlbumArtist,
            intent.Artist,
            settings.Tags?.SingleAlbumArtist != false);
        var payload = BuildVisiblePreResolutionPayload(intent, settings, engine, selectedQuality);
        payload.AutoSources = primaryFallback.AutoSources;
        payload.AutoIndex = primaryFallback.AutoIndex;
        payload.FallbackPlan = primaryFallback.FallbackPlan;
        payload.DestinationFolderId = primaryDestinationFolderId;
        payload.QualityBucket = useAtmosStereoDual ? StereoType : payload.QualityBucket;

        var enqueueDecision = await EnqueueItemAsync(
            payload,
            blockRules,
            intent.AllowQualityUpgrade,
            requestedQualityRank,
            cancellationToken);
        var skipped = 0;
        if (enqueueDecision.Success)
        {
            var queueUuid = enqueueDecision.QueueUuid ?? payload.Id;
            queued.Add(queueUuid);
            NotifyQueueAdded(payload);
        }
        else
        {
            skipped = 1;
            RecordSkipReason(skipReasonCodes, skipReasons, relatedQueueUuids, enqueueDecision);
        }

        if (useAtmosStereoDual
            && !IsAtmosQuality(selectedQuality)
            && (enqueueDecision.Success || ShouldContinueWithSecondaryAfterPrimarySkip(enqueueDecision)))
        {
            await TryEnqueueVisibleAtmosSecondaryAsync(
                intent,
                settings,
                destinationRouting,
                blockRules,
                queued,
                cancellationToken);
        }

        if (queued.Count > 0)
        {
            _activityLog.Info($"Intent queued: engine={engine} count={queued.Count}");
            if (IsMusicIntent(intent))
            {
                _orchestrationService.MarkDownloadQueued();
            }
        }

        var message = queued.Count > 0 ? $"Queued {queued.Count} item(s)." : (skipReasons.FirstOrDefault() ?? "Nothing queued.");
        return new DownloadIntentResult
        {
            Success = queued.Count > 0,
            Engine = engine,
            Queued = queued,
            RelatedQueueUuids = relatedQueueUuids,
            Skipped = skipped,
            Message = message,
            SkipReasonCodes = skipReasonCodes,
            SkipReasons = skipReasons
        };
    }

    [ExcludeFromCodeCoverage]
    public async Task<QueuePreResolutionPayload.ResolutionResult> ResolveQueuedPayloadAsync(
        DownloadQueueItem item,
        CancellationToken cancellationToken)
    {
        var payload = QueuePreResolutionPayload.ParseOrEmpty(item.PayloadJson);
        var intent = BuildIntentFromQueueItem(item);
        if (string.Equals(item.Engine, TidalPlatform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ContentType, DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveQueuedTidalAtmosPayloadAsync(item, intent, cancellationToken);
        }

        var savedAutoSources = ReadQueuedAutoSources(payload);
        if (savedAutoSources.Count > 0)
        {
            return await ResolveQueuedPayloadFromSavedPlanAsync(
                item,
                intent,
                payload,
                savedAutoSources,
                cancellationToken);
        }

        var resolution = await TryPrepareEnqueueResolutionAsync(
            intent,
            preferIsrcOnly: false,
            allowManualQueueDuringEnrichment: false,
            allowAutomaticSecondaryQuality: false,
            QueueSourceSettingsSnapshot.ReadFromPayload(payload),
            cancellationToken);
        if (resolution.Failure != null)
        {
            return new QueuePreResolutionPayload.ResolutionResult(
                item.Engine,
                null,
                null,
                null,
                null,
                resolution.Failure.Message);
        }

        var state = resolution.State!;
        var target = state.ResolvedTarget;
        var selectedQuality = ApplyResolvedQuality(intent, state.Settings, target.Engine, target.SelectedQuality);
        selectedQuality = ResolveEnabledDownloadQuality(state.Settings, target.Engine, selectedQuality);
        var fallbackInfo = BuildEnqueueFallbackInfo(new EnqueueFallbackRequest(
            intent,
            state.Settings,
            target.Engine,
            selectedQuality,
            IsMusicIntent(intent),
            target.AllowCrossEngineFallback,
            state.Routing.UseAtmosStereoDual,
            target.AutoSources,
            state.Routing.Availability));

        await ResolveTrackIdentityMatrixAsync(
            intent,
            state.Settings,
            BuildIdentityTargetsForDownload(
                state.Settings,
                fallbackInfo.FallbackPlan
                    .Select(step => step.Engine)
                    .Append(target.Engine)),
            cancellationToken);

        var sourceUrl = ResolveSourceUrlFromIntentIdentity(intent, target.Engine)
            ?? target.Resolution.SourceUrl
            ?? intent.SourceUrl;

        var qobuzId = FirstNonEmpty(
            intent.QobuzId,
            TryExtractQobuzTrackId(sourceUrl)?.ToString(CultureInfo.InvariantCulture));
        var tidalId = FirstNonEmpty(
            intent.TidalId,
            TryExtractTidalTrackId(sourceUrl));
        var amazonId = EngineLinkParser.NormalizeAmazonTrackId(intent.AmazonId)
            ?? EngineLinkParser.TryExtractAmazonTrackId(sourceUrl, RegexTimeout);
        var resolvedEngine = string.IsNullOrWhiteSpace(target.Engine) ? item.Engine : target.Engine;
        var identityError = ValidateQueuedResolvedIdentity(
            resolvedEngine,
            sourceUrl,
            intent.DeezerId,
            intent.AppleId,
            qobuzId,
            tidalId,
            amazonId);
        if (!string.IsNullOrWhiteSpace(identityError))
        {
            return new QueuePreResolutionPayload.ResolutionResult(
                resolvedEngine,
                null,
                selectedQuality,
                fallbackInfo.AutoIndex,
                fallbackInfo.FallbackPlan,
                identityError,
                Isrc: intent.Isrc,
                DeezerId: intent.DeezerId,
                DeezerAlbumId: intent.DeezerAlbumId,
                DeezerArtistId: intent.DeezerArtistId,
                SpotifyId: intent.SpotifyId,
                AppleId: intent.AppleId,
                AppleAlbumId: intent.AppleAlbumId,
                AppleAlbumName: intent.AppleAlbumName,
                AppleArtistName: intent.AppleArtistName,
                AppleIsrc: intent.AppleIsrc,
                AppleDurationMs: intent.AppleDurationMs,
                QobuzId: qobuzId,
                TidalId: tidalId,
                AmazonId: amazonId,
                DurationMs: intent.DurationMs > 0 ? intent.DurationMs : item.DurationMs,
                DestinationFolderId: intent.DestinationFolderId ?? item.DestinationFolderId,
                ContentType: string.IsNullOrWhiteSpace(intent.ContentType) ? item.ContentType : intent.ContentType);
        }

        return new QueuePreResolutionPayload.ResolutionResult(
            resolvedEngine,
            sourceUrl,
            selectedQuality,
            fallbackInfo.AutoIndex,
            fallbackInfo.FallbackPlan,
            target.Resolution.Message,
            Isrc: intent.Isrc,
            DeezerId: intent.DeezerId,
            DeezerAlbumId: intent.DeezerAlbumId,
            DeezerArtistId: intent.DeezerArtistId,
            SpotifyId: intent.SpotifyId,
            AppleId: intent.AppleId,
            AppleAlbumId: intent.AppleAlbumId,
            AppleAlbumName: intent.AppleAlbumName,
            AppleArtistName: intent.AppleArtistName,
            AppleIsrc: intent.AppleIsrc,
            AppleDurationMs: intent.AppleDurationMs,
            QobuzId: qobuzId,
            TidalId: tidalId,
            AmazonId: amazonId,
            DurationMs: intent.DurationMs > 0 ? intent.DurationMs : item.DurationMs,
            DestinationFolderId: intent.DestinationFolderId ?? item.DestinationFolderId,
            ContentType: string.IsNullOrWhiteSpace(intent.ContentType) ? item.ContentType : intent.ContentType);
    }

    private async Task<QueuePreResolutionPayload.ResolutionResult> ResolveQueuedPayloadFromSavedPlanAsync(
        DownloadQueueItem item,
        DownloadIntent intent,
        JsonObject payload,
        List<string> autoSources,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var snapshot = QueueSourceSettingsSnapshot.ReadFromPayload(payload);
        if (snapshot?.HasValues == true)
        {
            settings = snapshot.ApplyTo(settings);
        }

        NormalizeEnqueueSettings(settings);
        var fallbackPlan = FallbackPayloadNormalizer.ReadFallbackPlan(payload);
        if (fallbackPlan.Count == 0)
        {
            fallbackPlan = BuildFallbackPlanFromSources(intent, autoSources, settings.FallbackSearch);
        }

        string? lastSkipReason = null;
        for (var index = 0; index < autoSources.Count; index++)
        {
            var step = DownloadSourceOrder.DecodeAutoSource(autoSources[index]);
            if (string.IsNullOrWhiteSpace(step.Source))
            {
                continue;
            }

            var candidate = await ResolveIntentAsync(
                intent,
                step.Source,
                preferIsrcOnly: false,
                preResolved: null,
                effectiveSettings: settings,
                cancellationToken);
            if (!TryAcceptResolvedCandidate(step.Source, candidate, out var skipReason))
            {
                lastSkipReason = skipReason;
                continue;
            }

            var sourceUrl = ResolveSourceUrlFromIntentIdentity(intent, step.Source)
                ?? candidate.SourceUrl
                ?? intent.SourceUrl;
            var qobuzId = FirstNonEmpty(
                intent.QobuzId,
                TryExtractQobuzTrackId(sourceUrl)?.ToString(CultureInfo.InvariantCulture));
            var tidalId = FirstNonEmpty(
                intent.TidalId,
                TryExtractTidalTrackId(sourceUrl));
            var amazonId = EngineLinkParser.NormalizeAmazonTrackId(intent.AmazonId)
                ?? EngineLinkParser.TryExtractAmazonTrackId(sourceUrl, RegexTimeout);
            var identityError = ValidateQueuedResolvedIdentity(
                step.Source,
                sourceUrl,
                intent.DeezerId,
                intent.AppleId,
                qobuzId,
                tidalId,
                amazonId);
            if (!string.IsNullOrWhiteSpace(identityError))
            {
                lastSkipReason = identityError;
                continue;
            }

            return new QueuePreResolutionPayload.ResolutionResult(
                step.Source,
                sourceUrl,
                step.Quality,
                index,
                fallbackPlan,
                null,
                Isrc: intent.Isrc,
                DeezerId: intent.DeezerId,
                DeezerAlbumId: intent.DeezerAlbumId,
                DeezerArtistId: intent.DeezerArtistId,
                SpotifyId: intent.SpotifyId,
                AppleId: intent.AppleId,
                AppleAlbumId: intent.AppleAlbumId,
                AppleAlbumName: intent.AppleAlbumName,
                AppleArtistName: intent.AppleArtistName,
                AppleIsrc: intent.AppleIsrc,
                AppleDurationMs: intent.AppleDurationMs,
                QobuzId: qobuzId,
                TidalId: tidalId,
                AmazonId: amazonId,
                DurationMs: intent.DurationMs > 0 ? intent.DurationMs : item.DurationMs,
                DestinationFolderId: intent.DestinationFolderId ?? item.DestinationFolderId,
                ContentType: string.IsNullOrWhiteSpace(intent.ContentType) ? item.ContentType : intent.ContentType);
        }

        return new QueuePreResolutionPayload.ResolutionResult(
            item.Engine,
            null,
            null,
            null,
            fallbackPlan,
            string.IsNullOrWhiteSpace(lastSkipReason)
                ? "Track unavailable in enabled download sources."
                : $"Track unavailable in enabled download sources. Last skip: {lastSkipReason}",
            Isrc: intent.Isrc,
            DeezerId: intent.DeezerId,
            DeezerAlbumId: intent.DeezerAlbumId,
            DeezerArtistId: intent.DeezerArtistId,
            SpotifyId: intent.SpotifyId,
            AppleId: intent.AppleId,
            AppleAlbumId: intent.AppleAlbumId,
            AppleAlbumName: intent.AppleAlbumName,
            AppleArtistName: intent.AppleArtistName,
            AppleIsrc: intent.AppleIsrc,
            AppleDurationMs: intent.AppleDurationMs,
            QobuzId: intent.QobuzId,
            TidalId: intent.TidalId,
            AmazonId: intent.AmazonId,
            DurationMs: intent.DurationMs > 0 ? intent.DurationMs : item.DurationMs,
            DestinationFolderId: intent.DestinationFolderId ?? item.DestinationFolderId,
            ContentType: string.IsNullOrWhiteSpace(intent.ContentType) ? item.ContentType : intent.ContentType);
    }

    private static List<string> ReadQueuedAutoSources(JsonObject payload)
    {
        var node = payload["AutoSources"] ?? payload["autoSources"];
        if (node is not JsonArray array)
        {
            return new List<string>();
        }

        return array
            .Select(item => item?.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();
    }

    private static string? ValidateQueuedResolvedIdentity(
        string engine,
        string? sourceUrl,
        string? deezerId,
        string? appleId,
        string? qobuzId,
        string? tidalId,
        string? amazonId)
    {
        var normalizedEngine = NormalizeEngineName(engine);
        var url = sourceUrl ?? string.Empty;
        return normalizedEngine switch
        {
            DeezerPlatform => !string.IsNullOrWhiteSpace(NormalizeDeezerTrackId(deezerId))
                || IsServiceUrlMatch(url, DeezerPlatform)
                    ? null
                    : "Deezer identity unavailable for this track.",
            ApplePlatform => !string.IsNullOrWhiteSpace(appleId)
                || IsServiceUrlMatch(url, ApplePlatform)
                    ? null
                    : "Apple Music identity unavailable for this track.",
            QobuzPlatform => !string.IsNullOrWhiteSpace(qobuzId)
                || IsServiceUrlMatch(url, QobuzPlatform)
                    ? null
                    : "Qobuz identity unavailable for this track.",
            TidalPlatform => !string.IsNullOrWhiteSpace(tidalId)
                || IsServiceUrlMatch(url, TidalPlatform)
                    ? null
                    : "Tidal identity unavailable for this track.",
            AmazonPlatform => !string.IsNullOrWhiteSpace(amazonId)
                || IsServiceUrlMatch(url, AmazonPlatform)
                    ? null
                    : "Amazon Music identity unavailable for this track.",
            _ => null
        };
    }

    [ExcludeFromCodeCoverage]
    private async Task<QueuePreResolutionPayload.ResolutionResult> ResolveQueuedTidalAtmosPayloadAsync(
        DownloadQueueItem item,
        DownloadIntent intent,
        CancellationToken cancellationToken)
    {
        var durationMs = intent.DurationMs > 0 ? intent.DurationMs : item.DurationMs;
        var durationSeconds = durationMs.HasValue && durationMs.Value > 0
            ? (int)Math.Round(durationMs.Value / 1000d)
            : 0;
        var resolvedAtmosTrack = await _tidalDownloadService.ResolveAtmosTrackAsync(
            intent.Title ?? string.Empty,
            intent.Artist ?? string.Empty,
            intent.Album ?? string.Empty,
            FirstNonEmpty(intent.TidalId, TryExtractTidalTrackId(intent.SourceUrl)) ?? string.Empty,
            intent.Isrc ?? string.Empty,
            durationSeconds,
            cancellationToken);
        var sourceUrl = resolvedAtmosTrack?.Url;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return new QueuePreResolutionPayload.ResolutionResult(
                TidalPlatform,
                null,
                null,
                null,
                null,
                "Tidal Atmos track not found for ISRC or metadata.");
        }

        return new QueuePreResolutionPayload.ResolutionResult(
            TidalPlatform,
            sourceUrl,
            TidalAtmosQuality,
            0,
            Array.Empty<FallbackPlanStep>(),
            null,
            Isrc: FirstNonEmpty(resolvedAtmosTrack?.Isrc, intent.Isrc),
            SpotifyId: intent.SpotifyId,
            TidalId: FirstNonEmpty(intent.TidalId, TryExtractTidalTrackId(sourceUrl)),
            DurationMs: durationMs,
            DestinationFolderId: intent.DestinationFolderId ?? item.DestinationFolderId,
            ContentType: DownloadContentTypes.Atmos,
            Album: ResolveResolvedAlbumForAtmos(intent.Album, resolvedAtmosTrack?.Album),
            AlbumArtist: FirstNonEmpty(intent.AlbumArtist, resolvedAtmosTrack?.Artist, intent.Artist));
    }

    [ExcludeFromCodeCoverage]
    private static DownloadIntent BuildIntentFromQueueItem(DownloadQueueItem item)
    {
        var payload = QueuePreResolutionPayload.ParseOrEmpty(item.PayloadJson);
        return new DownloadIntent
        {
            SourceService = ReadPayloadString(payload, "SourceService", "sourceService") ?? item.Engine,
            SourceUrl = ReadPayloadString(payload, "SourceUrl", "sourceUrl") ?? string.Empty,
            Url = ReadPayloadString(payload, "Url", "url") ?? string.Empty,
            PreferredEngine = ReadPayloadString(payload, "Engine", "engine") ?? item.Engine,
            Quality = ReadPayloadString(payload, "Quality", "quality") ?? string.Empty,
            ContentType = ReadPayloadString(payload, "ContentType", "contentType") ?? item.ContentType ?? string.Empty,
            SpotifyId = FirstNonEmpty(
                ReadPayloadString(payload, "SpotifyId", "spotifyId"),
                item.SpotifyTrackId) ?? string.Empty,
            DeezerId = FirstNonEmpty(
                ReadPayloadString(payload, "DeezerId", "deezerId"),
                item.DeezerTrackId) ?? string.Empty,
            DeezerAlbumId = FirstNonEmpty(
                ReadPayloadString(payload, "DeezerAlbumId", "deezerAlbumId"),
                item.DeezerAlbumId) ?? string.Empty,
            DeezerArtistId = FirstNonEmpty(
                ReadPayloadString(payload, "DeezerArtistId", "deezerArtistId"),
                item.DeezerArtistId) ?? string.Empty,
            AppleId = FirstNonEmpty(
                ReadPayloadString(payload, "AppleId", "appleId"),
                item.AppleTrackId) ?? string.Empty,
            QobuzId = EngineLinkParser.NormalizeNumericTrackId(
                FirstNonEmpty(item.QobuzTrackId, ReadPayloadStringAny(payload, "QobuzId", "qobuzId", "QobuzTrackId", "qobuzTrackId"))) ?? string.Empty,
            TidalId = EngineLinkParser.NormalizeNumericTrackId(
                FirstNonEmpty(item.TidalTrackId, ReadPayloadStringAny(payload, "TidalId", "tidalId", "TidalTrackId", "tidalTrackId"))) ?? string.Empty,
            AmazonId = EngineLinkParser.NormalizeAmazonTrackId(
                FirstNonEmpty(item.AmazonTrackId, ReadPayloadStringAny(payload, "AmazonId", "amazonId", "AmazonTrackId", "amazonTrackId"))) ?? string.Empty,
            Isrc = FirstNonEmpty(
                ReadPayloadString(payload, "Isrc", "isrc"),
                item.Isrc) ?? string.Empty,
            Title = ReadPayloadString(payload, "Title", "title") ?? item.TrackTitle,
            Artist = ReadPayloadString(payload, "Artist", "artist") ?? item.ArtistName,
            Album = ReadPayloadString(payload, "Album", "album") ?? string.Empty,
            AlbumArtist = ReadPayloadString(payload, "AlbumArtist", "albumArtist") ?? string.Empty,
            Cover = ReadPayloadString(payload, "Cover", "cover") ?? string.Empty,
            ReleaseDate = ReadPayloadString(payload, "ReleaseDate", "releaseDate") ?? string.Empty,
            DurationMs = item.DurationMs ?? ResolvePayloadDurationMs(payload),
            DestinationFolderId = item.DestinationFolderId ?? ReadPayloadInt64(payload, "DestinationFolderId", "destinationFolderId"),
            WatchlistSource = ReadPayloadString(payload, "WatchlistSource", "watchlistSource") ?? string.Empty,
            WatchlistPlaylistId = ReadPayloadString(payload, "WatchlistPlaylistId", "watchlistPlaylistId") ?? string.Empty,
            WatchlistTrackId = ReadPayloadString(payload, "WatchlistTrackId", "watchlistTrackId") ?? string.Empty,
            WatchlistOrigin = ReadPayloadString(payload, "WatchlistOrigin", "watchlistOrigin") ?? string.Empty,
            WatchlistUnavailableSettingsFingerprint = ReadPayloadString(
                payload,
                "WatchlistUnavailableSettingsFingerprint",
                "watchlistUnavailableSettingsFingerprint") ?? string.Empty
        };
    }

    [ExcludeFromCodeCoverage]
    private static string? ReadPayloadString(JsonObject payload, string pascalKey, string camelKey)
    {
        var node = payload[pascalKey] ?? payload[camelKey];
        var value = node?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    [ExcludeFromCodeCoverage]
    private static string? ReadPayloadStringAny(JsonObject payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = payload[key]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    [ExcludeFromCodeCoverage]
    private static long? ReadPayloadInt64(JsonObject payload, string pascalKey, string camelKey)
    {
        var value = ReadPayloadString(payload, pascalKey, camelKey);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    [ExcludeFromCodeCoverage]
    private static int ResolvePayloadDurationMs(JsonObject payload)
    {
        var durationMs = ReadPayloadInt64(payload, "DurationMs", "durationMs");
        if (durationMs.HasValue)
        {
            return (int)Math.Clamp(durationMs.Value, 0, int.MaxValue);
        }

        var durationSeconds = ReadPayloadInt64(payload, "DurationSeconds", "durationSeconds");
        return durationSeconds.HasValue
            ? (int)Math.Clamp(durationSeconds.Value * 1000, 0, int.MaxValue)
            : 0;
    }

    private async Task<AmazonCatalogItem?> ResolveAmazonAtmosAvailabilityAsync(
        DownloadIntent intent,
        CancellationToken cancellationToken)
    {
        var amazonId = EngineLinkParser.NormalizeAmazonTrackId(intent.AmazonId)
            ?? EngineLinkParser.TryExtractAmazonTrackId(intent.SourceUrl, RegexTimeout);
        var resolved = await _amazonMusicMetadataService.ResolveAtmosTrackAsync(
            intent.Title,
            intent.Artist,
            intent.Album,
            intent.DurationMs > 0 ? intent.DurationMs : null,
            intent.Isrc,
            amazonId,
            cancellationToken);
        if (resolved is null
            || !resolved.HasAtmos
            || string.IsNullOrWhiteSpace(resolved.Id)
            || string.IsNullOrWhiteSpace(resolved.Url))
        {
            return null;
        }

        intent.AmazonId = resolved.Id;
        return resolved;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private async Task<DestinationRoutingResult> ResolveDestinationRoutingAsync(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        bool useMultiQuality,
        bool useAtmosStereoDual,
        string engine,
        CancellationToken cancellationToken)
    {
        var multiQuality = settings.MultiQuality;
        var primaryDestinationFolderId = useMultiQuality
            ? (intent.DestinationFolderId ?? multiQuality!.PrimaryDestinationFolderId)
            : intent.DestinationFolderId;
        var secondaryDestinationFolderId = useMultiQuality
            ? (intent.SecondaryDestinationFolderId ?? multiQuality!.SecondaryDestinationFolderId)
            : null;
        if (!useAtmosStereoDual)
        {
            return new DestinationRoutingResult(primaryDestinationFolderId, secondaryDestinationFolderId, null);
        }

        if (!secondaryDestinationFolderId.HasValue)
        {
            return new DestinationRoutingResult(
                primaryDestinationFolderId,
                secondaryDestinationFolderId,
                new DownloadIntentResult
                {
                    Success = false,
                    Engine = engine,
                    Message = "Dual-quality routing requires a dedicated Atmos destination folder."
                });
        }

        if (primaryDestinationFolderId.HasValue
            && primaryDestinationFolderId.Value == secondaryDestinationFolderId.Value)
        {
            return new DestinationRoutingResult(
                primaryDestinationFolderId,
                secondaryDestinationFolderId,
                new DownloadIntentResult
                {
                    Success = false,
                    Engine = engine,
                    Message = "Stereo and Atmos destination folders must be different in dual-quality mode."
                });
        }

        var distinctRootCheck = await DownloadDestinationGuard.ValidateDistinctRootsAsync(
            primaryDestinationFolderId,
            secondaryDestinationFolderId,
            _libraryRepository,
            cancellationToken);
        if (!distinctRootCheck.Ok)
        {
            return new DestinationRoutingResult(
                primaryDestinationFolderId,
                secondaryDestinationFolderId,
                new DownloadIntentResult
                {
                    Success = false,
                    Engine = engine,
                    Message = distinctRootCheck.Error ?? "Stereo and Atmos destinations must resolve to different folder roots."
                });
        }

        return new DestinationRoutingResult(primaryDestinationFolderId, secondaryDestinationFolderId, null);
    }

    private async Task<(DownloadIntentResult? Failure, EnqueueResolutionState? State)> TryPrepareEnqueueResolutionAsync(
        DownloadIntent intent,
        bool preferIsrcOnly,
        bool allowManualQueueDuringEnrichment,
        bool allowAutomaticSecondaryQuality,
        QueueSourceSettingsSnapshot? sourceSettingsSnapshot,
        CancellationToken cancellationToken)
    {
        var gateFailure = await TryBlockByDownloadGateAsync(allowManualQueueDuringEnrichment, cancellationToken);
        if (gateFailure != null)
        {
            return (gateFailure, null);
        }

        var preparation = await PrepareEnqueueAsync(intent, allowManualQueueDuringEnrichment, cancellationToken, sourceSettingsSnapshot);
        var profileValidation = await TryValidateEnqueueProfileAsync(intent, preparation, cancellationToken);
        if (profileValidation.Failure != null)
        {
            return (profileValidation.Failure, null);
        }

        if (!ShouldSkipWatchlistPreEnqueueMetadataHydration(intent))
        {
            await PopulateIntentMetadataAsync(intent, preparation.Settings, profileValidation.ResolvedDownloadTagSource, cancellationToken);
        }
        var settings = preparation.Settings;
        var routingFailure = TryValidateExplicitEngineRouting(intent, preparation);
        if (routingFailure != null)
        {
            return (routingFailure, null);
        }

        var routing = PrepareEnqueueRouting(
            intent,
            preparation,
            allowAutomaticSecondaryQuality);
        var noSourcesFailure = TryValidateRoutingSources(routing);
        if (noSourcesFailure != null)
        {
            return (noSourcesFailure, null);
        }

        var resolvedTarget = BuildPendingEnqueueTarget(intent, routing, settings);

        return (null, new EnqueueResolutionState(settings, routing, resolvedTarget));
    }

    private static ResolvedEnqueueTarget BuildPendingEnqueueTarget(
        DownloadIntent intent,
        EnqueueRoutingState routing,
        DeezSpoTagSettings settings)
    {
        var isAuto = !IsIntentPodcast(intent, intent.SourceUrl ?? string.Empty)
            && (routing.IntentRequestsAuto || string.Equals(settings.Service, AutoService, StringComparison.OrdinalIgnoreCase));
        var allowCrossEngineFallback = !IsIntentPodcast(intent, intent.SourceUrl ?? string.Empty) && isAuto;
        var autoIndex = ResolveAutoStartIndex(intent.PreferredEngine, routing.PreferredEngine, routing.AutoSources);
        var selectedEngine = routing.AppleOnlyRequired
            ? ApplePlatform
            : isAuto
                ? DownloadSourceOrder.DecodeAutoSource(routing.AutoSources[Math.Clamp(autoIndex, 0, routing.AutoSources.Count - 1)]).Source
                : routing.PreferredEngine;
        var selectedQuality = isAuto
            ? DownloadSourceOrder.DecodeAutoSource(routing.AutoSources[Math.Clamp(autoIndex, 0, routing.AutoSources.Count - 1)]).Quality ?? routing.TargetQuality
            : routing.TargetQuality;

        return new ResolvedEnqueueTarget(
            selectedEngine,
            selectedQuality,
            autoIndex,
            allowCrossEngineFallback,
            string.Empty,
            (selectedEngine, null, string.Empty, "queue-pending"),
            routing.AutoSources);
    }

    private (List<FallbackPlanStep> FallbackPlan, List<string> AutoSources, int AutoIndex) BuildEnqueueFallbackInfo(
        EnqueueFallbackRequest request)
    {
        var intent = request.Intent;
        var settings = request.Settings;
        var engine = request.TargetEngine;
        var quality = request.Quality;
        var allowCrossEngineFallback = request.AllowCrossEngineFallback;
        var useAtmosStereoDual = request.UseAtmosStereoDual;
        var autoSources = request.AutoSources;
        if (!request.MusicIntent)
        {
            var nonMusicQuality = quality;
            if (string.Equals(NormalizeContentType(intent.ContentType), DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase)
                || IsPodcastSource(intent.SourceUrl, null))
            {
                nonMusicQuality = DownloadContentTypes.Podcast;
            }
            else if (string.Equals(NormalizeContentType(intent.ContentType), DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase)
                     || IsVideoSource(intent.SourceUrl, null))
            {
                nonMusicQuality = DownloadContentTypes.Video;
            }

            var nonMusicSources = new List<string> { DownloadSourceOrder.EncodeAutoSource(engine, nonMusicQuality) };
            var nonMusicPlan = BuildFallbackPlanFromSources(intent, nonMusicSources, settings.FallbackSearch);
            return (nonMusicPlan, nonMusicSources, 0);
        }

        var payloadSources = IsAtmosSourceRequest(intent.ContentType, quality)
            ? autoSources.Where(IsAtmosEncodedSource).ToList()
            : allowCrossEngineFallback
                ? ResolveCrossEngineFallbackSources(intent, autoSources, settings, engine, quality)
                : DownloadSourceOrder.ResolveEngineQualitySources(
                    settings,
                    engine,
                    quality,
                    strict: UseStrictQualityFallback(settings, engine, quality));
        payloadSources = PrioritizeFallbackSourcesByHealth(
            payloadSources,
            settings,
            allowCrossEngineFallback,
            engine,
            _apiHealthTracker);

        if (useAtmosStereoDual
            && string.Equals(engine, ApplePlatform, StringComparison.OrdinalIgnoreCase)
            && !IsAtmosQuality(quality))
        {
            payloadSources = payloadSources
                .Where(source =>
                {
                    var decoded = DownloadSourceOrder.DecodeAutoSource(source);
                    return !IsAtmosQuality(decoded.Quality);
                })
                .ToList();
        }

        var plan = BuildFallbackPlanFromSources(intent, payloadSources, settings.FallbackSearch);
        var index = DownloadSourceOrder.FindAutoIndex(payloadSources, engine, quality);
        var clampedIndex = payloadSources.Count == 0 ? 0 : Math.Max(0, Math.Min(index, payloadSources.Count - 1));
        return (plan, payloadSources, clampedIndex);
    }

    private static List<string> ResolveCrossEngineFallbackSources(
        DownloadIntent intent,
        List<string> autoSources,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string engine,
        string? quality)
    {
        if (IsAtmosSourceRequest(intent.ContentType, quality))
        {
            return DownloadSourceOrder.CollapseAutoSourcesByService(autoSources)
                .Where(IsAtmosEncodedSource)
                .ToList();
        }

        var strict = UseStrictQualityFallback(settings, engine, quality);
        return DownloadSourceOrder.ResolveFallbackPlanSources(
            settings,
            autoSources,
            engine,
            quality,
            strict,
            includeDeezer: true);
    }

    private static bool IsAtmosSourceRequest(string? contentType, string? quality)
        => string.Equals(contentType?.Trim(), DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase)
           || IsAtmosQuality(quality);

    private static bool IsAtmosEncodedSource(string encodedSource)
    {
        var decoded = DownloadSourceOrder.DecodeAutoSource(encodedSource);
        return IsAtmosSourceRequest(null, decoded.Quality)
            && (string.Equals(decoded.Source, ApplePlatform, StringComparison.OrdinalIgnoreCase)
                || string.Equals(decoded.Source, TidalPlatform, StringComparison.OrdinalIgnoreCase)
                || string.Equals(decoded.Source, AmazonPlatform, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildTidalTrackUrl(string tidalId)
        => $"https://tidal.com/browse/track/{Uri.EscapeDataString(tidalId)}";

    private static string BuildQobuzTrackUrl(string qobuzId)
        => $"https://play.qobuz.com/track/{Uri.EscapeDataString(qobuzId)}";

    private static string? ResolveDeezerTrackIdForEnqueue(
        DownloadIntent intent,
        string? resolvedSourceUrl,
        bool isPodcastIntent)
    {
        var deezerTrackId = NormalizeDeezerTrackId(intent.DeezerId);
        var sourceUrl = resolvedSourceUrl ?? intent.SourceUrl;
        if (string.IsNullOrWhiteSpace(deezerTrackId) && isPodcastIntent)
        {
            deezerTrackId = NormalizeDeezerTrackId(TryExtractDeezerEpisodeId(sourceUrl));
        }
        if (string.IsNullOrWhiteSpace(deezerTrackId))
        {
            deezerTrackId = NormalizeDeezerTrackId(TryExtractDeezerTrackId(sourceUrl));
        }

        return deezerTrackId;
    }

    private static string ResolveDeezerCollectionType(DownloadIntent intent, bool isPodcastIntent)
    {
        if (isPodcastIntent)
        {
            return EpisodeType;
        }

        return string.IsNullOrWhiteSpace(intent.Album) ? TrackType : AlbumType;
    }

    private static string? ResolveDeezerSourceUrl(string? resolvedSourceUrl, string deezerTrackId, bool isPodcastIntent)
    {
        var sourceUrl = resolvedSourceUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            sourceUrl = isPodcastIntent
                ? $"https://www.deezer.com/episode/{deezerTrackId}"
                : $"https://www.deezer.com/track/{deezerTrackId}";
        }
        if (isPodcastIntent
            && !IsPodcastSource(sourceUrl, null)
            && !IsUsablePodcastStreamUrl(sourceUrl))
        {
            sourceUrl = $"https://www.deezer.com/episode/{deezerTrackId}";
        }

        return sourceUrl;
    }

    private async Task<(bool Applied, string? Error, string? ResolvedDownloadTagSource)> ApplyDownloadProfileOverridesAsync(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        long? destinationFolderId,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await _downloadTagSettingsResolver.ResolveProfileAsync(destinationFolderId, cancellationToken);
            if (profile == null)
            {
                return (false, "Destination music folder requires a valid AutoTag profile.", null);
            }

            var engineContext = string.IsNullOrWhiteSpace(intent.PreferredEngine)
                || string.Equals(intent.PreferredEngine, AutoService, StringComparison.OrdinalIgnoreCase)
                ? intent.SourceService
                : intent.PreferredEngine;

            var resolvedSource = DownloadEngineSettingsHelper.ApplyResolvedProfileToSettings(
                settings,
                profile,
                currentEngine: engineContext);
            var storedDownloadTagSource = DownloadTagSourceHelper.NormalizeStoredSource(
                profile.DownloadTagSource,
                DownloadTagSourceHelper.DeezerSource);
            var metadataTagSource = string.Equals(
                storedDownloadTagSource,
                DownloadTagSourceHelper.FollowDownloadEngineSource,
                StringComparison.OrdinalIgnoreCase)
                ? null
                : resolvedSource;
            return (true, null, metadataTagSource);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to apply download profile overrides for folder {FolderId}", destinationFolderId);
            }

            if (ex is InvalidOperationException invalidOperationException
                && (invalidOperationException.Message.StartsWith("Destination music folder requires a valid AutoTag profile.", StringComparison.Ordinal)
                    || invalidOperationException.Message.StartsWith("Download profile source resolution failed:", StringComparison.Ordinal)))
            {
                return (false, invalidOperationException.Message, null);
            }

            return (false, "Failed to apply destination profile settings.", null);
        }
    }

    private async Task<string?> ResolveAppleIdForStorefrontAsync(
        string? appleId,
        string sourceUrl,
        string? isrc,
        bool isVideo,
        bool preferSourceAppleId,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        appleId = ResolveAppleIdFromSourcePreference(appleId, sourceUrl, preferSourceAppleId, out var shouldReturnPreferredSourceId);
        if (shouldReturnPreferredSourceId
            || IsAppleStationId(appleId)
            || IsAppleStationUrl(sourceUrl))
        {
            return appleId;
        }

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
        var mediaUserToken = settings.AppleMusic?.MediaUserToken;

        if (isVideo)
        {
            if (string.IsNullOrWhiteSpace(appleId))
            {
                return appleId;
            }

            return await ResolveAppleVideoIdOrFallbackAsync(appleId, storefront, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(appleId))
        {
            var songResolved = await TryResolveAppleSongIdAsync(appleId, storefront, mediaUserToken, cancellationToken);
            if (!string.IsNullOrWhiteSpace(songResolved))
            {
                return songResolved;
            }
        }

        var isrcResolved = await TryResolveAppleSongIdByIsrcAsync(isrc, storefront, mediaUserToken, cancellationToken);
        if (!string.IsNullOrWhiteSpace(isrcResolved))
        {
            return isrcResolved;
        }

        return appleId;
    }

    private static string? ResolveAppleIdFromSourcePreference(
        string? appleId,
        string sourceUrl,
        bool preferSourceAppleId,
        out bool shouldReturnPreferredSourceId)
    {
        shouldReturnPreferredSourceId = false;
        var sourceAppleId = AppleIdParser.TryExtractFromUrl(sourceUrl);
        if (string.IsNullOrWhiteSpace(sourceAppleId) || IsAppleStationId(sourceAppleId))
        {
            return appleId;
        }

        if (preferSourceAppleId)
        {
            // For Atmos-targeted intents keep the explicit URL track id to avoid storefront remaps
            // landing on a stereo-only catalog variant.
            shouldReturnPreferredSourceId = true;
            return sourceAppleId;
        }

        return sourceAppleId;
    }

    private async Task<string?> ResolveAppleVideoIdOrFallbackAsync(string appleId, string storefront, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await _appleCatalogService.GetMusicVideoAsync(appleId, storefront, EnglishUsLocale, cancellationToken);
            var resolved = TryExtractAppleIdFromCatalog(doc);
            return string.IsNullOrWhiteSpace(resolved) ? appleId : resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple catalog video lookup failed for {AppleId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(appleId));
            }
            return appleId;
        }
    }

    private async Task<string?> TryResolveAppleSongIdAsync(
        string appleId,
        string storefront,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await _appleCatalogService.GetSongAsync(appleId, storefront, EnglishUsLocale, cancellationToken, mediaUserToken);
            return TryExtractAppleIdFromCatalog(doc);
        }
        catch (HttpRequestException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple catalog song lookup failed for {AppleId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(appleId));
            }
            return null;
        }
        catch (JsonException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple catalog song payload could not be parsed for {AppleId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(appleId));
            }
            return null;
        }
    }

    private async Task<string?> TryResolveAppleSongIdByIsrcAsync(
        string? isrc,
        string storefront,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        try
        {
            using var isrcDoc = await _appleCatalogService.GetSongByIsrcAsync(isrc, storefront, EnglishUsLocale, cancellationToken, mediaUserToken);
            return TryExtractAppleIdFromCatalog(isrcDoc);
        }
        catch (HttpRequestException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple catalog ISRC lookup failed for {Isrc}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(isrc));
            }
            return null;
        }
        catch (JsonException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple catalog ISRC payload could not be parsed for {Isrc}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(isrc));
            }
            return null;
        }
    }

    private static string? TryExtractAppleIdFromCatalog(JsonDocument? doc)
    {
        if (doc == null)
        {
            return null;
        }

        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var dataArr)
            && dataArr.ValueKind == JsonValueKind.Array
            && dataArr.GetArrayLength() > 0)
        {
            return dataArr[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object &&
            results.TryGetProperty(SongsField, out var songs) && songs.ValueKind == JsonValueKind.Object &&
            songs.TryGetProperty("data", out var songData) && songData.ValueKind == JsonValueKind.Array &&
            songData.GetArrayLength() > 0)
        {
            return songData[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }

        return null;
    }

    private async Task<DownloadIntentResult?> TryBlockByDownloadGateAsync(
        bool allowManualQueueDuringEnrichment,
        CancellationToken cancellationToken)
    {
        var downloadGate = allowManualQueueDuringEnrichment
            ? await _orchestrationService.EvaluateManualQueueGateAsync(cancellationToken)
            : await _orchestrationService.EvaluateDownloadGateAsync(cancellationToken);
        if (downloadGate.Allowed)
        {
            return null;
        }

        return new DownloadIntentResult
        {
            Success = false,
            Message = string.IsNullOrWhiteSpace(downloadGate.Message)
                ? "Downloads paused while AutoTag is running."
                : downloadGate.Message,
            Engine = string.Empty,
            SkipReasonCodes = new List<string> { "download_gate_paused" },
            SkipReasons = new List<string>
            {
                string.IsNullOrWhiteSpace(downloadGate.Message)
                    ? "Downloads paused while AutoTag is running."
                    : downloadGate.Message
            }
        };
    }

    private async Task<EnqueuePreparation> PrepareEnqueueAsync(
        DownloadIntent intent,
        bool applyManualDownloadPreference,
        CancellationToken cancellationToken,
        QueueSourceSettingsSnapshot? sourceSettingsSnapshot = null)
    {
        var settings = _settingsService.LoadSettings();
        if (sourceSettingsSnapshot?.HasValues == true)
        {
            settings = sourceSettingsSnapshot.ApplyTo(settings);
        }
        NormalizeEnqueueSettings(settings);
        var isPodcastIntent = NormalizeIntentContentType(intent);
        ApplyIntentDownloadEngineOrder(intent, settings);
        if (applyManualDownloadPreference && !isPodcastIntent && IsMusicIntent(intent))
        {
            ApplyManualDownloadPreferenceIfMissing(intent, settings);
        }

        if (!intent.DestinationFolderId.HasValue)
        {
            intent.DestinationFolderId = await ResolveRoutedDestinationFolderIdAsync(intent, settings, cancellationToken);
        }

        return new EnqueuePreparation(settings, isPodcastIntent, ResolveMetadataDestinationFolderId(intent, settings));
    }

    private static void ApplyManualDownloadPreferenceIfMissing(DownloadIntent intent, DeezSpoTagSettings settings)
    {
        if (string.IsNullOrWhiteSpace(intent.PreferredEngine))
        {
            intent.PreferredEngine = ManualDownloadPreferenceResolver.ResolvePreferredEngine(settings);
        }
    }

    private static void ApplyIntentDownloadEngineOrder(DownloadIntent intent, DeezSpoTagSettings settings)
    {
        if (intent.DownloadEngineOrder == null)
        {
            return;
        }

        var normalized = DownloadSourceOrder.NormalizeDownloadEngineOrderSettings(intent.DownloadEngineOrder);
        normalized.Enabled = true;
        settings.DownloadEngineOrder = normalized;
        settings.Service = AutoService;
        intent.PreferredEngine = AutoService;
    }

    private static void NormalizeEnqueueSettings(DeezSpoTagSettings settings)
    {
        if (string.Equals(settings.Service, SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
        {
            settings.Service = AutoService;
        }
    }

    private static bool NormalizeIntentContentType(DownloadIntent intent)
    {
        if (string.Equals(intent.ContentType, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase)
            || IsVideoSource(intent.SourceUrl, null))
        {
            intent.ContentType = DownloadContentTypes.Video;
            intent.Quality ??= DownloadContentTypes.Video;
            return false;
        }

        var isPodcastIntent = string.Equals(intent.ContentType, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase)
            || IsPodcastSource(intent.SourceUrl, null)
            || string.Equals(NormalizeContentType(intent.ContentType), DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase);
        if (isPodcastIntent)
        {
            intent.ContentType = DownloadContentTypes.Podcast;
            intent.Quality = DownloadContentTypes.Podcast;
        }

        return isPodcastIntent;
    }

    private static long? ResolveMetadataDestinationFolderId(DownloadIntent intent, DeezSpoTagSettings settings)
    {
        var routingMultiQuality = settings.MultiQuality;
        var useMultiQualityForRouting = IsMultiQualityDualEnabled(routingMultiQuality);
        return useMultiQualityForRouting
            ? (intent.DestinationFolderId ?? routingMultiQuality!.PrimaryDestinationFolderId)
            : intent.DestinationFolderId;
    }

    private static DownloadIntentResult? TryValidateExplicitEngineRouting(DownloadIntent intent, EnqueuePreparation preparation)
    {
        var normalizedPreferredEngine = intent.PreferredEngine?.Trim().ToLowerInvariant() ?? string.Empty;
        var intentRequestsAuto = string.Equals(normalizedPreferredEngine, AutoService, StringComparison.OrdinalIgnoreCase);
        var targetQuality = string.IsNullOrWhiteSpace(intent.Quality) ? null : intent.Quality;
        if (RequiresAppleOnly(intent, targetQuality)
            && !string.IsNullOrWhiteSpace(normalizedPreferredEngine)
            && !intentRequestsAuto
            && !string.Equals(normalizedPreferredEngine, ApplePlatform, StringComparison.OrdinalIgnoreCase))
        {
            return new DownloadIntentResult
            {
                Success = false,
                Message = "Apple video and Apple Atmos downloads must use the Apple engine.",
                Engine = string.Empty
            };
        }

        if (!preparation.IsPodcastIntent)
        {
            return null;
        }

        var podcastEngine = ResolvePodcastEngine(intent, normalizedPreferredEngine);
        return string.IsNullOrWhiteSpace(podcastEngine)
            ? new DownloadIntentResult
            {
                Success = false,
                Message = "Podcast downloads require an explicit supported engine/source URL. No fallback is allowed.",
                Engine = string.Empty
            }
            : null;
    }

    private EnqueueRoutingState PrepareEnqueueRouting(
        DownloadIntent intent,
        EnqueuePreparation preparation,
        bool allowAutomaticSecondaryQuality)
    {
        var settings = preparation.Settings;
        var initialRouting = ApplyInitialContentRouting(intent, preparation);
        var explicitAtmosRequest = initialRouting.ExplicitAtmosRequest;
        var explicitStereoRequest = initialRouting.ExplicitStereoRequest;
        var targetQuality = initialRouting.TargetQuality;

        PlatformLinkResult? availability = null;
        var multiQuality = settings.MultiQuality;
        var useMultiQuality = allowAutomaticSecondaryQuality && IsMultiQualityDualEnabled(multiQuality);

        // Secondary Atmos queueing should not depend solely on pre-hydrated Atmos metadata.
        // In dual-profile mode we always keep a stereo primary + Atmos secondary path for
        // music intents (except videos), even when the incoming request explicitly mentions Atmos.
        var useAtmosStereoDual = useMultiQuality && IsMusicIntent(intent) && !IsVideoIntent(intent);
        var normalizedPreferredEngine = intent.PreferredEngine?.Trim().ToLowerInvariant() ?? string.Empty;
        var intentRequestsAuto = string.Equals(normalizedPreferredEngine, AutoService, StringComparison.OrdinalIgnoreCase);
        var appleOnlyRequired = RequiresAppleOnly(intent, targetQuality);
        if (appleOnlyRequired)
        {
            normalizedPreferredEngine = ApplePlatform;
            intent.PreferredEngine = ApplePlatform;
            intentRequestsAuto = false;
        }

        if (preparation.IsPodcastIntent)
        {
            normalizedPreferredEngine = ResolvePodcastEngine(intent, normalizedPreferredEngine);
            intent.PreferredEngine = normalizedPreferredEngine;
            intentRequestsAuto = false;
        }

        if (useAtmosStereoDual && appleOnlyRequired && !IsVideoIntent(intent))
        {
            appleOnlyRequired = false;
        }

        var routingServiceOverride = ResolveRoutingServiceOverride(normalizedPreferredEngine);
        var autoSources = preparation.IsPodcastIntent
            ? DownloadSourceOrder.ResolveEngineQualitySources(settings, normalizedPreferredEngine, DownloadContentTypes.Podcast, strict: true)
            : DownloadSourceOrder.ResolveQualityAutoSources(
                settings,
                includeDeezer: true,
                targetQuality: targetQuality,
                forcedServiceOverride: routingServiceOverride);
        autoSources = PrioritizeAutoSourcesByHealth(autoSources, settings, intentRequestsAuto, normalizedPreferredEngine);
        var preferredEngine = ResolvePreferredEngine(normalizedPreferredEngine, intentRequestsAuto, appleOnlyRequired, preparation.IsPodcastIntent, autoSources);
        targetQuality = NormalizeTargetQuality(intent, settings, preferredEngine, targetQuality, explicitStereoRequest, useAtmosStereoDual);
        targetQuality = ResolveEnabledDownloadQuality(settings, preferredEngine, targetQuality);
        if (useAtmosStereoDual)
        {
            autoSources = RemoveAtmosAutoSources(autoSources);
        }

        intent.SpotifyId = FirstNonEmpty(intent.SpotifyId, TryExtractSpotifyId(intent.SourceUrl)) ?? string.Empty;

        return new EnqueueRoutingState(normalizedPreferredEngine, intentRequestsAuto, appleOnlyRequired, autoSources, preferredEngine, targetQuality, availability, useAtmosStereoDual);
    }

    private static InitialContentRoutingState ApplyInitialContentRouting(
        DownloadIntent intent,
        EnqueuePreparation preparation)
    {
        var normalizedRequestedContentType = NormalizeContentType(intent.ContentType);
        var explicitAtmosRequest = string.Equals(normalizedRequestedContentType, DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase);
        var explicitStereoRequest = string.Equals(normalizedRequestedContentType, DownloadContentTypes.Stereo, StringComparison.OrdinalIgnoreCase);
        if (explicitAtmosRequest && string.IsNullOrWhiteSpace(intent.Quality))
        {
            intent.Quality = AtmosQuality;
        }

        var targetQuality = string.IsNullOrWhiteSpace(intent.Quality) ? null : intent.Quality;
        if (ShouldApplySettingsAtmosSource(intent, preparation, explicitStereoRequest))
        {
            var atmosEngine = NormalizeAtmosEngine(preparation.Settings.MultiQuality?.AtmosEngine);
            intent.ContentType = DownloadContentTypes.Atmos;
            targetQuality = ResolveAtmosQualityForEngine(atmosEngine);
            intent.Quality = targetQuality;
            explicitAtmosRequest = true;
        }

        return new InitialContentRoutingState(explicitAtmosRequest, explicitStereoRequest, targetQuality);
    }

    private static bool ShouldApplySettingsAtmosSource(
        DownloadIntent intent,
        EnqueuePreparation preparation,
        bool explicitStereoRequest)
    {
        return SettingsRequestsAtmosSource(preparation.Settings)
            && IsMusicIntent(intent)
            && !IsVideoIntent(intent)
            && !preparation.IsPodcastIntent
            && !explicitStereoRequest;
    }

    private static string? ResolveRoutingServiceOverride(string normalizedPreferredEngine)
    {
        if (string.IsNullOrWhiteSpace(normalizedPreferredEngine))
        {
            return null;
        }

        return string.Equals(normalizedPreferredEngine, AutoService, StringComparison.OrdinalIgnoreCase)
            ? AutoService
            : normalizedPreferredEngine;
    }

    private static DownloadIntentResult? TryValidateRoutingSources(EnqueueRoutingState routing)
    {
        if (routing.AutoSources.Count != 0)
        {
            return null;
        }

        return new DownloadIntentResult
        {
            Success = false,
            Message = "No auto sources available for fallback.",
            Engine = string.Empty
        };
    }

    private static int ResolveAutoStartIndex(string? preferredEngine, string resolvedPreferredEngine, List<string> autoSources)
    {
        if (string.IsNullOrWhiteSpace(preferredEngine))
        {
            return 0;
        }

        for (var i = 0; i < autoSources.Count; i++)
        {
            var step = DownloadSourceOrder.DecodeAutoSource(autoSources[i]);
            if (string.Equals(step.Source, resolvedPreferredEngine, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static bool TryAcceptResolvedCandidate(
        string candidateEngine,
        (string Engine, string? SourceUrl, string Message, string MappingSource) candidate,
        out string reason)
    {
        reason = string.Empty;
        if (!string.IsNullOrWhiteSpace(candidate.Message) && candidate.Engine == string.Empty)
        {
            reason = candidate.Message;
            return false;
        }

        var missingUrl = string.IsNullOrWhiteSpace(candidate.SourceUrl)
            && candidateEngine is DeezerPlatform or QobuzPlatform or TidalPlatform or AmazonPlatform or ApplePlatform;
        if (missingUrl)
        {
            reason = "missing_url";
            return false;
        }

        return true;
    }

    private static string ResolvePreferredEngine(
        string normalizedPreferredEngine,
        bool intentRequestsAuto,
        bool appleOnlyRequired,
        bool isPodcastIntent,
        List<string> autoSources)
    {
        if (appleOnlyRequired)
        {
            return ApplePlatform;
        }

        if (isPodcastIntent)
        {
            return normalizedPreferredEngine;
        }

        var shouldUseAutoSource = string.IsNullOrWhiteSpace(normalizedPreferredEngine) || intentRequestsAuto;
        return shouldUseAutoSource
            ? DownloadSourceOrder.DecodeAutoSource(autoSources[0]).Source
            : normalizedPreferredEngine;
    }

    private static string? NormalizeTargetQuality(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        string preferredEngine,
        string? targetQuality,
        bool explicitStereoRequest,
        bool useAtmosStereoDual)
    {
        if (string.IsNullOrWhiteSpace(targetQuality))
        {
            targetQuality = ResolvePreferredQuality(settings, preferredEngine);
        }

        if (explicitStereoRequest && (string.IsNullOrWhiteSpace(targetQuality) || IsAtmosQuality(targetQuality)))
        {
            targetQuality = ResolveStereoPreferredQuality(settings, preferredEngine) ?? targetQuality;
        }

        if (!useAtmosStereoDual)
        {
            return targetQuality;
        }

        if (string.IsNullOrWhiteSpace(targetQuality) || IsAtmosQuality(targetQuality))
        {
            targetQuality = ResolveStereoPreferredQuality(settings, preferredEngine) ?? targetQuality;
        }

        if (!string.IsNullOrWhiteSpace(intent.Quality) && IsAtmosQuality(intent.Quality))
        {
            intent.Quality = targetQuality ?? intent.Quality;
        }

        return targetQuality;
    }

    private static List<string> RemoveAtmosAutoSources(IEnumerable<string> autoSources)
    {
        return autoSources
            .Where(source =>
            {
                var decoded = DownloadSourceOrder.DecodeAutoSource(source);
                return !IsAtmosQuality(decoded.Quality);
            })
            .ToList();
    }

    private static bool IsVideoIntent(DownloadIntent intent)
    {
        return IsVideoSource(intent.SourceUrl, null)
            || string.Equals(intent.ContentType, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase);
    }

    private string? ApplyResolvedQuality(DownloadIntent intent, DeezSpoTagSettings settings, string engine, string? selectedQuality)
    {
        if (!IsMusicIntent(intent))
        {
            return ResolveNonMusicResolvedQuality(intent, selectedQuality);
        }

        return selectedQuality;
    }

    private static string? ResolveEnabledDownloadQuality(DeezSpoTagSettings settings, string? engine, string? selectedQuality)
    {
        if (settings.DownloadEngineOrder?.Enabled != true || string.IsNullOrWhiteSpace(engine))
        {
            return selectedQuality;
        }

        var enabledQualities = DownloadSourceOrder.ResolveEngineQualitySources(
                settings,
                engine,
                requestedQuality: null,
                strict: false)
            .Select(DownloadSourceOrder.DecodeAutoSource)
            .Where(step => string.Equals(step.Source, engine, StringComparison.OrdinalIgnoreCase))
            .Select(step => step.Quality)
            .Where(quality => !string.IsNullOrWhiteSpace(quality))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (enabledQualities.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(selectedQuality)
            && enabledQualities.Any(quality => string.Equals(quality, selectedQuality, StringComparison.OrdinalIgnoreCase)))
        {
            return selectedQuality;
        }

        return enabledQualities[0];
    }

    private static string? ResolveNonMusicResolvedQuality(DownloadIntent intent, string? selectedQuality)
    {
        var normalizedContentType = NormalizeContentType(intent.ContentType);
        if (string.Equals(normalizedContentType, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase)
            || IsPodcastSource(intent.SourceUrl, null))
        {
            return DownloadContentTypes.Podcast;
        }

        if (string.Equals(normalizedContentType, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase)
            || IsVideoSource(intent.SourceUrl, null))
        {
            return DownloadContentTypes.Video;
        }

        return selectedQuality;
    }

    private async Task<(DownloadIntentResult? Failure, string? ResolvedDownloadTagSource)> TryValidateEnqueueProfileAsync(DownloadIntent intent, EnqueuePreparation preparation, CancellationToken cancellationToken)
    {
        if (!IsMusicIntent(intent))
        {
            return (null, null);
        }

        var profileResult = await ApplyDownloadProfileOverridesAsync(intent, preparation.Settings, preparation.MetadataDestinationFolderId, cancellationToken);
        if (profileResult.Applied)
        {
            return (null, profileResult.ResolvedDownloadTagSource);
        }

        return (new DownloadIntentResult
        {
            Success = false,
            Engine = string.Empty,
            Message = profileResult.Error ?? "Destination music folder requires a valid AutoTag profile."
        }, null);
    }

    private async Task<(string Engine, string? SourceUrl, string Message, string MappingSource)> ResolveIntentAsync(
        DownloadIntent intent,
        string engine,
        bool preferIsrcOnly,
        PlatformLinkResult? preResolved,
        DeezSpoTagSettings? effectiveSettings,
        CancellationToken cancellationToken)
    {
        var bootstrap = BootstrapIntentResolution(intent);
        var sourceUrl = bootstrap.SourceUrl;
        var settings = effectiveSettings ?? _settingsService.LoadSettings();
        await TryHydrateIntentIsrcFromBootstrapAsync(intent, bootstrap);
        var directIdentityResult = TryResolveDirectEngineIdentity(intent, engine, sourceUrl);
        if (directIdentityResult.HasValue)
        {
            return directIdentityResult.Value;
        }

        var directResult = TryResolveDirectIntentSource(engine, sourceUrl, bootstrap.NormalizedDeezerId, bootstrap.IsPodcastIntent);
        if (directResult.HasValue)
        {
            return directResult.Value;
        }

        if (ShouldBypassQobuzWatchlistPreResolve(intent, engine, sourceUrl))
        {
            return (engine, sourceUrl, string.Empty, "watchlist-qobuz-deferred");
        }

        await ResolveTrackIdentityMatrixAsync(
            intent,
            settings,
            BuildIdentityTargetsForDownload(settings, new[] { engine }),
            cancellationToken);
        var generatedIdentityResult = TryResolveDirectEngineIdentity(intent, engine, sourceUrl);
        if (generatedIdentityResult.HasValue)
        {
            return generatedIdentityResult.Value;
        }

        var amazonDeezerResult = TryResolveAmazonMappedDeezerSource(intent, engine);
        if (amazonDeezerResult.HasValue)
        {
            return amazonDeezerResult.Value;
        }

        var userCountry = settings.DeezerCountry;
        var strictSpotifyDeezerMode = IsStrictSpotifyDeezerMode(settings, engine, sourceUrl, intent.SpotifyId);
        var resolverStrictMode = settings.StrictSpotifyDeezerMode;

        var isrcFastResult = await TryResolveIsrcIntentSourceAsync(intent, engine, preferIsrcOnly, cancellationToken);
        if (isrcFastResult.HasValue)
        {
            return isrcFastResult.Value;
        }

        if (strictSpotifyDeezerMode)
        {
            return BuildStrictSpotifyDeezerFailure(intent.Isrc);
        }

        var nativeResolutionResult = await TryResolveEngineSpecificIntentUrlAsync(intent, engine, cancellationToken);
        if (nativeResolutionResult.HasValue)
        {
            return nativeResolutionResult.Value;
        }

        if (string.Equals(engine, SpotifyPlatform, StringComparison.OrdinalIgnoreCase)
            || string.Equals(engine, TidalPlatform, StringComparison.OrdinalIgnoreCase))
        {
            var nativeMismatchResult = BuildMismatchedEngineResolution(engine, sourceUrl, intent.Isrc);
            return nativeMismatchResult ?? (engine, sourceUrl, string.Empty, string.Empty);
        }

        PlatformLinkResult? platformLinks = await ResolvePlatformLinksForIntentAsync(
            intent,
            sourceUrl,
            bootstrap.NormalizedDeezerId,
            userCountry,
            preResolved,
            cancellationToken);
        await TryHydrateIntentIsrcFromSourceUrlAsync(intent, platformLinks, sourceUrl);

        var mappedResult = await TryResolveViaPlatformLinksAsync(
            intent,
            platformLinks,
            engine,
            sourceUrl,
            userCountry,
            "platform-link",
            cancellationToken);
        if (mappedResult.HasValue)
        {
            return mappedResult.Value;
        }

        platformLinks = await TryResolveFallbackPlatformLinksAsync(intent, engine, settings, resolverStrictMode, userCountry, platformLinks, cancellationToken);
        mappedResult = await TryResolveViaPlatformLinksAsync(
            intent,
            platformLinks,
            engine,
            sourceUrl,
            userCountry,
            "platform-link-fallback-search",
            cancellationToken);
        if (mappedResult.HasValue)
        {
            return mappedResult.Value;
        }

        await TryHydrateQobuzIntentIsrcAsync(intent, platformLinks, engine, settings, cancellationToken);

        var mismatchResult = BuildMismatchedEngineResolution(engine, sourceUrl, intent.Isrc);
        if (mismatchResult.HasValue)
        {
            return mismatchResult.Value;
        }

        return (engine, sourceUrl, string.Empty, string.Empty);
    }

    private static bool ShouldBypassQobuzWatchlistPreResolve(
        DownloadIntent intent,
        string engine,
        string sourceUrl)
    {
        if (!string.Equals(engine, QobuzPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isWatchlistIntent = !string.IsNullOrWhiteSpace(intent.WatchlistOrigin)
            || !string.IsNullOrWhiteSpace(intent.WatchlistSource)
            || !string.IsNullOrWhiteSpace(intent.WatchlistPlaylistId)
            || !string.IsNullOrWhiteSpace(intent.WatchlistTrackId);
        if (!isWatchlistIntent)
        {
            return false;
        }

        if (string.Equals(intent.SourceService, "boomplay", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(sourceUrl);
    }

    private static bool ShouldSkipWatchlistPreEnqueueMetadataHydration(DownloadIntent intent)
    {
        var isWatchlistIntent = !string.IsNullOrWhiteSpace(intent.WatchlistOrigin)
            || !string.IsNullOrWhiteSpace(intent.WatchlistSource)
            || !string.IsNullOrWhiteSpace(intent.WatchlistPlaylistId)
            || !string.IsNullOrWhiteSpace(intent.WatchlistTrackId);
        if (!isWatchlistIntent)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.SourceUrl)
            && string.IsNullOrWhiteSpace(intent.SpotifyId)
            && string.IsNullOrWhiteSpace(intent.DeezerId)
            && string.IsNullOrWhiteSpace(intent.AppleId)
            && string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return false;
        }

        if (string.Equals(intent.SourceService, "boomplay", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(intent.Title)
            && !string.IsNullOrWhiteSpace(intent.Artist);
    }

    private static IntentResolutionBootstrap BootstrapIntentResolution(DownloadIntent intent)
    {
        var sourceUrl = intent.SourceUrl ?? string.Empty;
        var isPodcastIntent = IsIntentPodcast(intent, sourceUrl);
        var normalizedDeezerId = BootstrapIntentDeezerIdentity(intent, sourceUrl, isPodcastIntent, ref sourceUrl);
        return new IntentResolutionBootstrap(sourceUrl, isPodcastIntent, normalizedDeezerId);
    }

    private async Task TryHydrateIntentIsrcFromBootstrapAsync(
        DownloadIntent intent,
        IntentResolutionBootstrap bootstrap)
    {
        if (bootstrap.IsPodcastIntent
            || !string.IsNullOrWhiteSpace(intent.Isrc)
            || string.IsNullOrWhiteSpace(bootstrap.NormalizedDeezerId))
        {
            return;
        }

        intent.Isrc = await ResolveDeezerIsrcAsync(bootstrap.NormalizedDeezerId) ?? string.Empty;
    }

    private async Task TryHydrateIntentFromAmazonAsync(
        DownloadIntent intent,
        CancellationToken cancellationToken)
    {
        var amazonId = EngineLinkParser.NormalizeAmazonTrackId(intent.AmazonId)
            ?? EngineLinkParser.TryExtractAmazonTrackId(intent.SourceUrl, RegexTimeout);
        if (string.IsNullOrWhiteSpace(amazonId))
        {
            intent.AmazonId = string.Empty;
            return;
        }

        intent.AmazonId = amazonId;

        if (string.IsNullOrWhiteSpace(intent.DeezerId))
        {
            intent.DeezerId = await ResolveDeezerTrackIdFromMetadataAsync(intent) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(intent.Isrc))
        {
            var normalizedDeezerId = NormalizeDeezerTrackId(intent.DeezerId);
            if (!string.IsNullOrWhiteSpace(normalizedDeezerId))
            {
                intent.DeezerId = normalizedDeezerId;
                intent.Isrc = await ResolveDeezerIsrcAsync(normalizedDeezerId) ?? string.Empty;
            }
        }
    }

    private static (string Engine, string? SourceUrl, string Message, string MappingSource)? TryResolveAmazonMappedDeezerSource(
        DownloadIntent intent,
        string engine)
    {
        if (!string.Equals(engine, DeezerPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalizedDeezerId = NormalizeDeezerTrackId(intent.DeezerId);
        return string.IsNullOrWhiteSpace(normalizedDeezerId)
            ? null
            : (engine, $"https://www.deezer.com/track/{normalizedDeezerId}", string.Empty, "amazon-id-deezer-metadata");
    }

    private static bool IsStrictSpotifyDeezerMode(
        DeezSpoTagSettings settings,
        string engine,
        string sourceUrl,
        string? spotifyId)
    {
        return settings.StrictSpotifyDeezerMode
            && string.Equals(engine, DeezerPlatform, StringComparison.OrdinalIgnoreCase)
            && IsSpotifyDrivenIntent(sourceUrl, spotifyId);
    }

    private static (string Engine, string? SourceUrl, string Message, string MappingSource) BuildStrictSpotifyDeezerFailure(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return (string.Empty, string.Empty, "Strict Spotify->Deezer mode requires an ISRC to resolve an exact match.", string.Empty);
        }

        return (string.Empty, string.Empty, "Strict Spotify->Deezer mode could not resolve an exact Deezer match by ISRC.", string.Empty);
    }

    private async Task TryHydrateIntentIsrcFromSourceUrlAsync(
        DownloadIntent intent,
        PlatformLinkResult? platformLinks,
        string sourceUrl)
    {
        if (platformLinks != null || !string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return;
        }

        var deezerTrackId = TryExtractDeezerTrackId(sourceUrl);
        if (!string.IsNullOrWhiteSpace(deezerTrackId))
        {
            intent.Isrc = await ResolveDeezerIsrcAsync(deezerTrackId) ?? string.Empty;
        }
    }

    private async Task<(string Engine, string? SourceUrl, string Message, string MappingSource)?> TryResolveViaPlatformLinksAsync(
        DownloadIntent intent,
        PlatformLinkResult? platformLinks,
        string engine,
        string sourceUrl,
        string userCountry,
        string mappingSource,
        CancellationToken cancellationToken)
    {
        if (platformLinks == null)
        {
            return null;
        }

        var mapped = await TryResolvePlatformLinkMappingAsync(
            intent,
            platformLinks,
            engine,
            sourceUrl,
            userCountry,
            mappingSource,
            cancellationToken);
        if (!mapped.Resolved)
        {
            return null;
        }

        return (engine, mapped.Url, string.Empty, mapped.MappingSource);
    }

    private async Task TryHydrateQobuzIntentIsrcAsync(
        DownloadIntent intent,
        PlatformLinkResult? platformLinks,
        string engine,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(engine, QobuzPlatform, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return;
        }

        var deezerId = await ResolveQobuzDeezerIdAsync(intent, platformLinks, cancellationToken);
        if (!string.IsNullOrWhiteSpace(deezerId))
        {
            var normalizedDeezerId = NormalizeDeezerTrackId(deezerId);
            if (!string.IsNullOrWhiteSpace(normalizedDeezerId))
            {
                intent.Isrc = await ResolveDeezerIsrcAsync(normalizedDeezerId) ?? string.Empty;
            }
        }

        if (!string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return;
        }

        try
        {
            var normalized = await ResolveFallbackDeezerIdAsync(
                intent,
                settings.FallbackSearch,
                settings.StrictSpotifyDeezerMode,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                intent.Isrc = await ResolveDeezerIsrcAsync(normalized) ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Fallback Deezer lookup failed for Spotify intent.");
        }
    }

    private async Task<string> ResolveQobuzDeezerIdAsync(
        DownloadIntent intent,
        PlatformLinkResult? platformLinks,
        CancellationToken cancellationToken)
    {
        var deezerId = string.IsNullOrWhiteSpace(intent.DeezerId) ? string.Empty : intent.DeezerId;
        if (platformLinks != null)
        {
            deezerId = !string.IsNullOrWhiteSpace(platformLinks.DeezerId)
                ? platformLinks.DeezerId
                : TryExtractDeezerTrackId(platformLinks.DeezerUrl ?? string.Empty) ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(deezerId))
        {
            return deezerId;
        }

        var spotifyId = string.IsNullOrWhiteSpace(intent.SpotifyId)
            ? platformLinks?.SpotifyId
            : intent.SpotifyId;
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return string.Empty;
        }

        intent.SpotifyId = spotifyId;
        await EnsureSpotifyMappedDeezerIdentityAsync(intent, _settingsService.LoadSettings(), cancellationToken);
        return NormalizeDeezerTrackId(intent.DeezerId) ?? string.Empty;
    }

    private async Task<(string Engine, string? SourceUrl, string Message, string MappingSource)?> TryResolveEngineSpecificIntentUrlAsync(
        DownloadIntent intent,
        string engine,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var normalizedEngine = NormalizeEngineName(engine);
        var resolution = await _trackIdentityResolver.ResolveAsync(
            new TrackIdentityResolutionRequest(
                SourcePlatform: FirstNonEmpty(intent.SourceService, InferPlatformFromUrl(intent.SourceUrl)),
                SourceUrl: FirstNonEmpty(intent.SourceUrl, intent.Url),
                Title: intent.Title,
                Artist: intent.Artist,
                Album: intent.Album,
                Isrc: intent.Isrc,
                DurationMs: intent.DurationMs > 0 ? intent.DurationMs : null,
                SpotifyId: intent.SpotifyId,
                DeezerId: intent.DeezerId,
                AppleId: intent.AppleId,
                QobuzId: intent.QobuzId,
                TidalId: intent.TidalId,
                AmazonId: intent.AmazonId,
                TargetPlatforms: new[] { normalizedEngine },
                Storefront: settings.AppleMusic?.Storefront,
                Language: EnglishUsLocale,
                MediaUserToken: settings.AppleMusic?.MediaUserToken),
            cancellationToken);
        ApplyResolvedIdentity(intent, resolution);
        var resolvedUrl = ResolveSourceUrlFromIdentityResolution(resolution, normalizedEngine)
            ?? ResolveSourceUrlFromIntentIdentity(intent, normalizedEngine);
        return string.IsNullOrWhiteSpace(resolvedUrl)
            ? null
            : (engine, resolvedUrl, string.Empty, "central-identity");
    }

    private static string? ResolveSourceUrlFromIdentityResolution(
        TrackIdentityResolution resolution,
        string engine)
        => NormalizeEngineName(engine) switch
        {
            SpotifyPlatform => resolution.SpotifyUrl,
            DeezerPlatform => resolution.DeezerUrl,
            ApplePlatform => resolution.AppleUrl,
            QobuzPlatform => resolution.QobuzUrl,
            TidalPlatform => resolution.TidalUrl,
            AmazonPlatform => resolution.AmazonUrl,
            _ => null
        };

    private static (string Engine, string? SourceUrl, string Message, string MappingSource)? BuildMismatchedEngineResolution(
        string engine,
        string sourceUrl,
        string? isrc)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl) || IsServiceUrlMatch(sourceUrl, engine))
        {
            return null;
        }

        if (string.Equals(engine, QobuzPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(isrc)
                ? (string.Empty, string.Empty, "missing_isrc", string.Empty)
                : (engine, string.Empty, string.Empty, "qobuz-isrc");
        }

        return (string.Empty, string.Empty, "Unable to resolve mapping for requested engine.", string.Empty);
    }

    private async Task<PlatformLinkResult?> ResolveAvailabilityAsync(DownloadIntent intent, CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var userCountry = settings.DeezerCountry;
        ApplySourceUrlIdentity(intent);
        var platformLinks = await ResolveInitialAvailabilityAsync(intent, userCountry, cancellationToken);
        platformLinks = await ApplyQobuzAvailabilityFallbackAsync(intent, platformLinks, cancellationToken);
        ApplyAvailabilityIdentity(intent, platformLinks);
        await ResolveMissingAppleIdentityAsync(intent, settings, cancellationToken);
        return platformLinks;
    }

    private async Task ResolveTrackIdentityMatrixAsync(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        IEnumerable<string>? targetEngines,
        CancellationToken cancellationToken)
    {
        ApplySourceUrlIdentity(intent);
        var engines = BuildIdentityEngineSet(targetEngines);
        if (engines.Count == 0)
        {
            return;
        }

        var resolution = await ResolveCentralIdentityAsync(intent, settings, engines, cancellationToken);
        ApplyResolvedIdentity(intent, resolution);
    }

    private static IEnumerable<string> BuildIdentityTargetsForDownload(
        DeezSpoTagSettings settings,
        IEnumerable<string> targetEngines)
    {
        var engines = targetEngines
            .Where(engine => !string.IsNullOrWhiteSpace(engine))
            .Select(engine => engine.Trim())
            .ToList();

        if (ShouldResolveAppleIdentityForArtwork(settings)
            && !engines.Contains(ApplePlatform, StringComparer.OrdinalIgnoreCase))
        {
            engines.Add(ApplePlatform);
        }

        if (settings.SaveArtwork || settings.Tags?.Cover == true)
        {
            AddIdentityTargets(engines, ArtworkFallbackHelper.ResolveOrder(settings));
        }

        if (settings.SaveArtworkArtist)
        {
            AddIdentityTargets(engines, ArtworkFallbackHelper.ResolveArtistOrder(settings));
        }

        if (LyricsSettingsPolicy.CanFetchLyrics(settings))
        {
            var lyricsProviders = string.IsNullOrWhiteSpace(settings.LyricsFallbackOrder)
                ? new[] { ApplePlatform, DeezerPlatform, SpotifyPlatform }
                : settings.LyricsFallbackOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AddIdentityTargets(
                engines,
                settings.LyricsFallbackEnabled ? lyricsProviders : lyricsProviders.Take(1));
        }

        return engines;
    }

    private static void AddIdentityTargets(List<string> engines, IEnumerable<string> sources)
    {
        foreach (var source in sources)
        {
            var normalized = source.Trim().ToLowerInvariant() switch
            {
                "applemusic" or "apple-music" or "apple_music" or "itunes" => ApplePlatform,
                _ => source.Trim().ToLowerInvariant()
            };
            if (normalized is not (ApplePlatform or DeezerPlatform or SpotifyPlatform)
                || engines.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            engines.Add(normalized);
        }
    }

    private static bool ShouldResolveAppleIdentityForArtwork(DeezSpoTagSettings settings)
    {
        if (settings.SaveAnimatedArtwork)
        {
            return true;
        }

        var needsCoverArtwork = settings.SaveArtwork || settings.Tags?.Cover == true;
        return needsCoverArtwork
               && (string.IsNullOrWhiteSpace(settings.ArtworkFallbackOrder)
                   || settings.ArtworkFallbackOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Any(source => string.Equals(source, ApplePlatform, StringComparison.OrdinalIgnoreCase)));
    }

    private Task<TrackIdentityResolution> ResolveCentralIdentityAsync(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        IEnumerable<string>? targetEngines,
        CancellationToken cancellationToken)
    {
        var engines = BuildIdentityEngineSet(targetEngines);
        return _trackIdentityResolver.ResolveAsync(
            new TrackIdentityResolutionRequest(
                SourcePlatform: FirstNonEmpty(intent.SourceService, InferPlatformFromUrl(intent.SourceUrl)),
                SourceUrl: FirstNonEmpty(intent.SourceUrl, intent.Url),
                Title: intent.Title,
                Artist: intent.Artist,
                Album: intent.Album,
                Isrc: intent.Isrc,
                DurationMs: intent.DurationMs > 0 ? intent.DurationMs : null,
                SpotifyId: intent.SpotifyId,
                DeezerId: intent.DeezerId,
                AppleId: intent.AppleId,
                QobuzId: intent.QobuzId,
                TidalId: intent.TidalId,
                AmazonId: intent.AmazonId,
                TargetPlatforms: engines,
                Storefront: settings.AppleMusic?.Storefront,
                Language: EnglishUsLocale,
                MediaUserToken: settings.AppleMusic?.MediaUserToken),
            cancellationToken);
    }

    private static PlatformLinkResult? BuildAvailabilityFromIdentity(TrackIdentityResolution resolution)
    {
        var availability = new PlatformLinkResult
        {
            Isrc = resolution.Isrc,
            SpotifyId = resolution.SpotifyId,
            SpotifyUrl = resolution.SpotifyUrl,
            DeezerId = resolution.DeezerId,
            DeezerUrl = resolution.DeezerUrl,
            AppleMusicUrl = resolution.AppleUrl,
            QobuzUrl = resolution.QobuzUrl,
            TidalUrl = resolution.TidalUrl,
            AmazonUrl = resolution.AmazonUrl,
            SourceTitle = resolution.Title,
            SourceArtist = resolution.Artist,
            SourceType = TrackType
        };

        return availability.HasAnyResolvedLink() || !string.IsNullOrWhiteSpace(availability.Isrc)
            ? availability
            : null;
    }

    private async Task<string?> ResolveQobuzUrlFromCentralIdentityAsync(
        DownloadIntent intent,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var resolution = await ResolveCentralIdentityAsync(intent, settings, new[] { QobuzPlatform }, cancellationToken);
        ApplyResolvedIdentity(intent, resolution);
        return resolution.QobuzUrl ?? ResolveSourceUrlFromIntentIdentity(intent, QobuzPlatform);
    }

    private static void ApplyResolvedIdentity(DownloadIntent intent, TrackIdentityResolution resolution)
    {
        intent.Isrc = FirstNonEmpty(intent.Isrc, resolution.Isrc) ?? string.Empty;
        intent.Title = FirstNonEmpty(intent.Title, resolution.Title) ?? string.Empty;
        intent.Artist = FirstNonEmpty(intent.Artist, resolution.Artist) ?? string.Empty;
        intent.Album = FirstNonEmpty(intent.Album, resolution.Album) ?? string.Empty;
        if (intent.DurationMs <= 0 && resolution.DurationMs is > 0)
        {
            intent.DurationMs = resolution.DurationMs.Value;
        }

        intent.SpotifyId = FirstNonEmpty(intent.SpotifyId, resolution.SpotifyId) ?? string.Empty;
        intent.DeezerId = FirstNonEmpty(NormalizeDeezerTrackId(intent.DeezerId), NormalizeDeezerTrackId(resolution.DeezerId)) ?? string.Empty;
        intent.AppleId = FirstNonEmpty(intent.AppleId, resolution.AppleId) ?? string.Empty;
        intent.AppleAlbumId = FirstNonEmpty(intent.AppleAlbumId, resolution.AppleAlbumId) ?? string.Empty;
        intent.AppleAlbumName = FirstNonEmpty(intent.AppleAlbumName, resolution.AppleAlbumName) ?? string.Empty;
        intent.AppleArtistName = FirstNonEmpty(intent.AppleArtistName, resolution.AppleArtistName) ?? string.Empty;
        intent.AppleIsrc = FirstNonEmpty(intent.AppleIsrc, resolution.AppleIsrc) ?? string.Empty;
        intent.AppleDurationMs ??= resolution.AppleDurationMs;
        intent.QobuzId = FirstNonEmpty(intent.QobuzId, resolution.QobuzId) ?? string.Empty;
        intent.TidalId = FirstNonEmpty(intent.TidalId, resolution.TidalId) ?? string.Empty;
        intent.AmazonId = EngineLinkParser.NormalizeAmazonTrackId(intent.AmazonId)
            ?? EngineLinkParser.NormalizeAmazonTrackId(resolution.AmazonId)
            ?? string.Empty;
    }

    private static HashSet<string> BuildIdentityEngineSet(IEnumerable<string>? targetEngines)
    {
        var engines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (targetEngines == null)
        {
            return engines;
        }

        foreach (var engine in targetEngines)
        {
            var normalized = NormalizeEngineName(engine);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                engines.Add(normalized);
            }
        }

        return engines;
    }

    private async Task<string?> ResolveTidalUrlFromBuiltInLookupAsync(
        DownloadIntent intent,
        CancellationToken cancellationToken)
    {
        var tidalId = FirstNonEmpty(intent.TidalId, TryExtractTidalTrackId(intent.SourceUrl), TryExtractTidalTrackId(intent.Url));
        if (!string.IsNullOrWhiteSpace(tidalId))
        {
            var persistedUrl = BuildTidalTrackUrl(tidalId);
            var persistedDurationSeconds = intent.DurationMs > 0
                ? Math.Max(1, (int)Math.Round(intent.DurationMs / 1000d))
                : 0;
            if (await _tidalDownloadService.ValidateTrackUrlAsync(
                    persistedUrl,
                    intent.Title ?? string.Empty,
                    intent.Artist ?? string.Empty,
                    intent.Album ?? string.Empty,
                    intent.Isrc ?? string.Empty,
                    persistedDurationSeconds,
                    cancellationToken))
            {
                intent.TidalId = tidalId;
                return persistedUrl;
            }

            intent.TidalId = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(intent.Title) || string.IsNullOrWhiteSpace(intent.Artist))
        {
            return null;
        }

        var durationSeconds = intent.DurationMs > 0
            ? Math.Max(1, (int)Math.Round(intent.DurationMs / 1000d))
            : 0;
        return await _tidalDownloadService.ResolveTrackUrlAsync(
            intent.Title,
            intent.Artist,
            intent.Isrc ?? string.Empty,
            durationSeconds,
            cancellationToken);
    }

    private static string? ResolveSourceUrlFromIntentIdentity(DownloadIntent intent, string engine)
    {
        var normalizedEngine = NormalizeEngineName(engine);
        if (!string.IsNullOrWhiteSpace(intent.SourceUrl)
            && IsServiceUrlMatch(intent.SourceUrl, normalizedEngine))
        {
            return intent.SourceUrl;
        }

        return normalizedEngine switch
        {
            DeezerPlatform => string.IsNullOrWhiteSpace(NormalizeDeezerTrackId(intent.DeezerId))
                ? null
                : $"https://www.deezer.com/track/{NormalizeDeezerTrackId(intent.DeezerId)}",
            SpotifyPlatform => string.IsNullOrWhiteSpace(intent.SpotifyId)
                ? null
                : $"https://open.spotify.com/track/{Uri.EscapeDataString(intent.SpotifyId)}",
            QobuzPlatform => string.IsNullOrWhiteSpace(intent.QobuzId)
                ? null
                : BuildQobuzTrackUrl(intent.QobuzId),
            TidalPlatform => string.IsNullOrWhiteSpace(intent.TidalId)
                ? null
                : BuildTidalTrackUrl(intent.TidalId),
            AmazonPlatform => string.IsNullOrWhiteSpace(EngineLinkParser.NormalizeAmazonTrackId(intent.AmazonId))
                ? null
                : $"https://music.amazon.com/tracks/{EngineLinkParser.NormalizeAmazonTrackId(intent.AmazonId)}",
            ApplePlatform => !string.IsNullOrWhiteSpace(intent.SourceUrl) && IsServiceUrlMatch(intent.SourceUrl, ApplePlatform)
                ? intent.SourceUrl
                : null,
            _ => null
        };
    }

    private async Task<PlatformLinkResult?> ResolveInitialAvailabilityAsync(
        DownloadIntent intent,
        string? userCountry,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var resolution = await ResolveCentralIdentityAsync(intent, settings, AllIdentityEngines, cancellationToken);
        ApplyResolvedIdentity(intent, resolution);
        return BuildAvailabilityFromIdentity(resolution);
    }

    private static void ApplyAvailabilityIdentity(DownloadIntent intent, PlatformLinkResult? platformLinks)
    {
        if (platformLinks == null)
        {
            return;
        }

        intent.Isrc = FirstNonEmpty(intent.Isrc, platformLinks.Isrc) ?? string.Empty;
        intent.SpotifyId = FirstNonEmpty(intent.SpotifyId, platformLinks.SpotifyId) ?? string.Empty;
    }

    private static void ApplySourceUrlIdentity(DownloadIntent intent)
    {
        var sourceUrl = intent.SourceUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return;
        }

        intent.SpotifyId = FirstNonEmpty(intent.SpotifyId, TryExtractSpotifyId(sourceUrl)) ?? string.Empty;
        intent.DeezerId = FirstNonEmpty(
            NormalizeDeezerTrackId(intent.DeezerId),
            NormalizeDeezerTrackId(TryExtractDeezerTrackId(sourceUrl))) ?? string.Empty;
        intent.AppleId = FirstNonEmpty(intent.AppleId, AppleIdParser.TryExtractFromUrl(sourceUrl)) ?? string.Empty;
        intent.TidalId = FirstNonEmpty(intent.TidalId, TryExtractTidalTrackId(sourceUrl)) ?? string.Empty;
        intent.QobuzId = FirstNonEmpty(
            intent.QobuzId,
            TryExtractQobuzTrackId(sourceUrl)?.ToString(CultureInfo.InvariantCulture)) ?? string.Empty;
        intent.AmazonId = EngineLinkParser.NormalizeAmazonTrackId(intent.AmazonId)
            ?? EngineLinkParser.TryExtractAmazonTrackId(sourceUrl, RegexTimeout)
            ?? string.Empty;
    }

    private async Task ResolveMissingAppleIdentityAsync(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(intent.AppleId)
            || (string.IsNullOrWhiteSpace(intent.Isrc)
                && (string.IsNullOrWhiteSpace(intent.Title) || string.IsNullOrWhiteSpace(intent.Artist))))
        {
            return;
        }

        try
        {
            var appleUrl = IsServiceUrlMatch(intent.SourceUrl ?? string.Empty, ApplePlatform)
                ? intent.SourceUrl ?? string.Empty
                : string.Empty;

            intent.AppleId = await ResolveAppleIdForStorefrontAsync(
                intent.AppleId,
                appleUrl,
                intent.Isrc,
                IsVideoIntent(intent),
                preferSourceAppleId: false,
                settings,
                cancellationToken) ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(intent.AppleId))
            {
                return;
            }

            intent.AppleId = await ResolveAppleIdViaItunesMatcherAsync(intent, settings, cancellationToken) ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Apple identity resolution failed before queue persistence: title='{Title}' artist='{Artist}' isrc='{Isrc}'",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(intent.Title),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(intent.Artist),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(intent.Isrc));
        }
    }

    private async Task<string?> ResolveAppleIdViaItunesMatcherAsync(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(intent.Title) || string.IsNullOrWhiteSpace(intent.Artist))
        {
            return null;
        }

        var info = new AutoTag.AutoTagAudioInfo
        {
            Title = intent.Title,
            Artist = intent.Artist,
            Artists = new List<string> { intent.Artist },
            Album = string.IsNullOrWhiteSpace(intent.Album) ? null : intent.Album,
            DurationSeconds = intent.DurationMs > 0 ? (int?)Math.Max(1, (int)Math.Round(intent.DurationMs / 1000d)) : null,
            Isrc = intent.Isrc
        };
        var config = new AutoTag.AutoTagMatchingConfig
        {
            MatchDuration = intent.DurationMs > 0,
            MaxDurationDifferenceSeconds = 8,
            Strictness = 0.82
        };
        var itunesConfig = new AutoTag.ItunesMatchConfig
        {
            Country = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront,
            SearchLimit = 10,
            MatchById = false
        };
        var match = await _itunesMatcher.MatchAsync(info, config, itunesConfig, cancellationToken);
        var trackId = match?.Track?.TrackId;
        if (match == null || string.IsNullOrWhiteSpace(trackId) || match.Accuracy < config.Strictness)
        {
            return null;
        }

        if (IsrcValidator.IsValid(intent.Isrc)
            && IsrcValidator.IsValid(match.Track.Isrc)
            && !string.Equals(intent.Isrc, match.Track.Isrc, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trackId;
    }

    private async Task<PlatformLinkResult?> ApplyQobuzAvailabilityFallbackAsync(
        DownloadIntent intent,
        PlatformLinkResult? platformLinks,
        CancellationToken cancellationToken)
    {
        if (!CanResolveQobuzByMetadata(intent)
            || (platformLinks != null && !string.IsNullOrWhiteSpace(platformLinks.QobuzUrl)))
        {
            return platformLinks;
        }

        if (IsrcValidator.IsValid(intent.Isrc))
        {
            // A valid ISRC is sufficient for the queued Qobuz processor to resolve the final source.
            // Do not block user-visible queue insertion on an extra Qobuz metadata lookup here.
            return platformLinks;
        }

        string? qobuzUrl;
        try
        {
            qobuzUrl = await ResolveQobuzUrlFromBuiltInLookupAsync(intent, cancellationToken)
                ?? await ResolveQobuzUrlFromCentralIdentityAsync(intent, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _activityLog.Warn($"Qobuz fallback throttled before queue insert: title='{intent.Title}' artist='{intent.Artist}' isrc='{intent.Isrc}'");
            return platformLinks;
        }

        if (string.IsNullOrWhiteSpace(qobuzUrl))
        {
            LogQobuzFallbackMiss(intent, platformLinks);
            return platformLinks;
        }

        if (platformLinks == null)
        {
            platformLinks = new PlatformLinkResult();
            _activityLog.Info($"Qobuz fallback hit (no platform-link): title='{intent.Title}' artist='{intent.Artist}' url='{qobuzUrl}'");
        }
        else
        {
            _activityLog.Info($"Qobuz fallback hit: title='{intent.Title}' artist='{intent.Artist}' url='{qobuzUrl}'");
        }

        platformLinks.QobuzUrl = qobuzUrl;
        return platformLinks;
    }

    private void LogQobuzFallbackMiss(DownloadIntent intent, PlatformLinkResult? platformLinks)
    {
        if (platformLinks == null)
        {
            _activityLog.Warn($"Qobuz fallback miss (no platform-link): title='{intent.Title}' artist='{intent.Artist}' isrc='{intent.Isrc}'");
            return;
        }

        _activityLog.Warn($"Qobuz fallback miss: title='{intent.Title}' artist='{intent.Artist}' isrc='{intent.Isrc}'");
    }

    private static bool CanResolveQobuzByMetadata(DownloadIntent intent)
    {
        return !string.IsNullOrWhiteSpace(intent.Isrc)
            || (!string.IsNullOrWhiteSpace(intent.Title)
                && !string.IsNullOrWhiteSpace(intent.Artist));
    }

    private async Task<string?> ResolveQobuzUrlFromBuiltInLookupAsync(
        DownloadIntent intent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(intent.Isrc)
            && (string.IsNullOrWhiteSpace(intent.Title) || string.IsNullOrWhiteSpace(intent.Artist)))
        {
            return null;
        }

        var resolution = await _qobuzTrackResolver.ResolveTrackAsync(
            intent.Isrc,
            intent.Title,
            intent.Artist,
            intent.Album,
            intent.DurationMs > 0 ? intent.DurationMs : null,
            cancellationToken);
        return resolution?.Track.Id > 0
            ? $"https://play.qobuz.com/track/{resolution.Track.Id}"
            : null;
    }

    private async Task TryHydrateAtmosCapabilityAsync(
        DownloadIntent intent,
        PlatformLinkResult? availability,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (intent.HasAtmos)
        {
            return;
        }

        var sourceUrl = intent.SourceUrl ?? string.Empty;
        string? appleUrl = null;
        if (!string.IsNullOrWhiteSpace(sourceUrl)
            && sourceUrl.Contains(AppleMusicDomain, StringComparison.OrdinalIgnoreCase))
        {
            appleUrl = sourceUrl;
        }
        else if (!string.IsNullOrWhiteSpace(availability?.AppleMusicUrl))
        {
            appleUrl = availability.AppleMusicUrl;
        }

        if (string.IsNullOrWhiteSpace(appleUrl))
        {
            return;
        }

        await PopulateAppleMetadataAsync(intent, appleUrl, settings, cancellationToken);
        if (intent.HasAtmos)
        {
            _activityLog.Info(
                $"Atmos capability detected from Apple metadata: title='{intent.Title ?? string.Empty}' artist='{intent.Artist ?? string.Empty}'");
        }
    }

    private static string? GetAvailabilityUrl(PlatformLinkResult availability, string engine)
    {
        return engine switch
        {
            DeezerPlatform => availability.DeezerUrl,
            ApplePlatform => availability.AppleMusicUrl,
            TidalPlatform => availability.TidalUrl,
            AmazonPlatform => availability.AmazonUrl,
            QobuzPlatform => availability.QobuzUrl,
            _ => null
        };
    }

    private async Task<(bool Resolved, string Url, string MappingSource)> TryResolvePlatformLinkMappingAsync(
        DownloadIntent intent,
        PlatformLinkResult platformLinks,
        string engine,
        string sourceUrl,
        string userCountry,
        string mappingSource,
        CancellationToken cancellationToken)
    {
        intent.Isrc = string.IsNullOrWhiteSpace(intent.Isrc) ? platformLinks.Isrc ?? string.Empty : intent.Isrc;
        if (string.IsNullOrWhiteSpace(intent.SpotifyId) && !string.IsNullOrWhiteSpace(platformLinks.SpotifyId))
        {
            intent.SpotifyId = platformLinks.SpotifyId;
        }

        var mappedUrl = ResolvePlatformMappedUrl(platformLinks, engine, sourceUrl);
        if (!string.Equals(engine, QobuzPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return (true, mappedUrl ?? string.Empty, mappingSource);
        }

        if (string.IsNullOrWhiteSpace(mappedUrl))
        {
            return (false, string.Empty, string.Empty);
        }

        var mappedTrackId = TryExtractQobuzTrackId(mappedUrl);
        if (!mappedTrackId.HasValue)
        {
            return (false, string.Empty, string.Empty);
        }

        if (!QobuzTrackId.TryCreate(mappedTrackId.Value, out var qobuzTrackId))
        {
            return (false, string.Empty, string.Empty);
        }

        var validated = await _qobuzTrackResolver.ValidateTrackIdAsync(
            qobuzTrackId,
            intent.Isrc,
            intent.Title,
            intent.Artist,
            intent.Album,
            intent.DurationMs > 0 ? intent.DurationMs : null,
            cancellationToken);
        if (validated?.Track.Id > 0)
        {
            return (true, $"https://play.qobuz.com/track/{validated.Track.Id}", $"{mappingSource}:validated");
        }

        _activityLog.Warn(
            $"Rejected Qobuz mapped URL that did not match requested track: title='{intent.Title}' artist='{intent.Artist}' isrc='{intent.Isrc}'");
        return (false, string.Empty, string.Empty);
    }

    private static int? TryExtractQobuzTrackId(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed)
            || !parsed.Host.Contains(QobuzDomain, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("track", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(segments[i + 1], out var trackId)
                && trackId > 0)
            {
                return trackId;
            }
        }

        return null;
    }

    private static string? ResolvePlatformMappedUrl(PlatformLinkResult platformLinks, string engine, string sourceUrl)
        => engine switch
        {
            DeezerPlatform => platformLinks.DeezerUrl,
            ApplePlatform => platformLinks.AppleMusicUrl,
            AmazonPlatform => null,
            QobuzPlatform => platformLinks.QobuzUrl,
            _ => sourceUrl
        };

    private static string? TryExtractSpotifyId(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            sourceUrl,
            @"spotify\.com\/track\/(?<id>[a-zA-Z0-9]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            RegexTimeout);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private async Task<AppleVideoMetadata?> TryGetAppleVideoMetadataAsync(
        string sourceUrl,
        string? appleId,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (!AppleVideoClassifier.IsVideoUrl(sourceUrl))
        {
            return null;
        }

        appleId = string.IsNullOrWhiteSpace(appleId) ? AppleIdParser.TryExtractFromUrl(sourceUrl) : appleId;
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return null;
        }

        try
        {
            var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic.Storefront) ? "us" : settings.AppleMusic.Storefront;
            using var doc = await _appleCatalogService.GetMusicVideoAsync(appleId, storefront, EnglishUsLocale, cancellationToken);
            if (!TryExtractVideoAttributes(doc.RootElement, out var attrs))
            {
                return null;
            }

            return new AppleVideoMetadata(
                attrs.Name,
                attrs.ArtistName,
                attrs.AlbumName,
                attrs.Isrc,
                attrs.ReleaseDate,
                attrs.ArtworkUrl,
                attrs.DurationSeconds,
                attrs.HasAtmos);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple MV metadata lookup failed for {Url}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(sourceUrl));
            }
            return null;
        }
    }

    private static bool TryExtractVideoAttributes(JsonElement root, out AppleCatalogVideoAttributes attrs)
    {
        return AppleCatalogVideoAttributeParser.TryParse(root, AttributesField, out attrs);
    }

    private static bool IsAppleStationId(string? appleId)
        => !string.IsNullOrWhiteSpace(appleId)
           && appleId.StartsWith("ra.", StringComparison.OrdinalIgnoreCase);

    private static bool IsAppleStationUrl(string? sourceUrl)
        => !string.IsNullOrWhiteSpace(sourceUrl)
           && sourceUrl.Contains("/station/", StringComparison.OrdinalIgnoreCase);

    private static string? TryExtractDeezerTrackId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!url.Contains(DeezerDomain, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var last = parts.LastOrDefault();
        if (string.IsNullOrWhiteSpace(last))
        {
            return null;
        }

        if (string.Equals(last, TrackType, StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
        {
            last = parts[^2];
        }

        var id = StripQueryAndFragment(last);
        return long.TryParse(id, out var numeric) && numeric > 0
            ? numeric.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string? TryExtractDeezerEpisodeId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!url.Contains(DeezerDomain, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!string.Equals(segments[i], EpisodeType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = StripQueryAndFragment(segments[i + 1]);
            if (long.TryParse(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string StripQueryAndFragment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var separatorIndex = value.AsSpan().IndexOfAny(QueryFragmentSeparators);
        return separatorIndex >= 0 ? value[..separatorIndex] : value;
    }

    private async Task<string?> ResolveDeezerIsrcAsync(string trackId)
    {
        try
        {
            var gwTrack = await _deezerClient.GetTrackWithFallbackAsync(trackId);
            if (!string.IsNullOrWhiteSpace(gwTrack?.Isrc))
            {
                return gwTrack.Isrc;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Deezer GW ISRC lookup failed for {TrackId}",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(trackId));
            }
        }

        try
        {
            var apiTrack = await _deezerClient.GetTrack(trackId);
            if (!string.IsNullOrWhiteSpace(apiTrack?.Isrc))
            {
                return apiTrack.Isrc;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Deezer API ISRC lookup failed for {TrackId}",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(trackId));
            }
        }

        return null;
    }

    private static SpotifyTrackSummary BuildSpotifyTrackSummary(DownloadIntent intent)
    {
        return new SpotifyTrackSummary(
            string.IsNullOrWhiteSpace(intent.SpotifyId) ? string.Empty : intent.SpotifyId,
            intent.Title ?? string.Empty,
            intent.Artist ?? string.Empty,
            intent.Album,
            intent.DurationMs > 0 ? intent.DurationMs : null,
            intent.SourceUrl ?? string.Empty,
            intent.Cover,
            intent.Isrc);
    }

    private async Task<string?> ResolveFallbackDeezerIdAsync(
        DownloadIntent intent,
        bool fallbackSearch,
        bool strictMode,
        CancellationToken cancellationToken)
    {
        var resolvedDeezerId = await SpotifyTracklistResolver.ResolveDeezerTrackIdAsync(
            _deezerClient,
            BuildSpotifyTrackSummary(intent),
            new SpotifyTrackResolveOptions(
                AllowFallbackSearch: fallbackSearch,
                PreferIsrcOnly: false,
                StrictMode: strictMode,
                BypassNegativeCanonicalCache: false,
                Logger: _logger,
                CancellationToken: cancellationToken));
        return NormalizeDeezerTrackId(resolvedDeezerId);
    }

    private async Task EnsureSpotifyMappedDeezerIdentityAsync(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var normalizedExistingDeezerId = NormalizeDeezerTrackId(intent.DeezerId);
        if (!string.IsNullOrWhiteSpace(normalizedExistingDeezerId))
        {
            intent.DeezerId = normalizedExistingDeezerId;
            await EnsureDeezerBackedIsrcAsync(intent);
            return;
        }

        var spotifyId = FirstNonEmpty(
            intent.SpotifyId,
            TryExtractSpotifyId(intent.SourceUrl),
            TryExtractSpotifyId(intent.Url));
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return;
        }

        intent.SpotifyId = spotifyId;
        var summary = BuildSpotifyTrackSummary(intent);
        var cachedDeezerId = SpotifyTracklistResolver.TryResolveCachedDeezerTrackId(summary, _logger);
        if (!string.IsNullOrWhiteSpace(cachedDeezerId))
        {
            intent.DeezerId = cachedDeezerId;
            await EnsureDeezerBackedIsrcAsync(intent);
            return;
        }

        if (string.IsNullOrWhiteSpace(summary.Name)
            || string.IsNullOrWhiteSpace(summary.Artists))
        {
            return;
        }

        var resolvedDeezerId = await SpotifyTracklistResolver.ResolveDeezerTrackIdAsync(
            _deezerClient,
            summary,
            new SpotifyTrackResolveOptions(
                AllowFallbackSearch: true,
                PreferIsrcOnly: false,
                StrictMode: settings.StrictSpotifyDeezerMode,
                BypassNegativeCanonicalCache: false,
                Logger: _logger,
                CancellationToken: cancellationToken));
        var normalizedDeezerId = NormalizeDeezerTrackId(resolvedDeezerId);
        if (!string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            intent.DeezerId = normalizedDeezerId;
            await EnsureDeezerBackedIsrcAsync(intent);
        }
    }

    private async Task EnsureDeezerBackedIsrcAsync(DownloadIntent intent)
    {
        if (!string.IsNullOrWhiteSpace(intent.Isrc)
            || string.IsNullOrWhiteSpace(intent.DeezerId))
        {
            return;
        }

        intent.Isrc = await ResolveDeezerIsrcAsync(intent.DeezerId) ?? string.Empty;
    }

    private static string? NormalizeDeezerTrackId(string? trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        return long.TryParse(trackId, out var numeric) && numeric > 0
            ? numeric.ToString(CultureInfo.InvariantCulture)
            : null;
    }


    private static bool SupportsIsrcResolution(string engine)
    {
        return string.Equals(engine, QobuzPlatform, StringComparison.OrdinalIgnoreCase)
               || string.Equals(engine, TidalPlatform, StringComparison.OrdinalIgnoreCase)
               || string.Equals(engine, ApplePlatform, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> ResolveIsrcUrlAsync(
        string engine,
        DownloadIntent intent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return null;
        }

        if (string.Equals(engine, QobuzPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveQobuzUrlFromBuiltInLookupAsync(intent, cancellationToken)
                ?? await ResolveQobuzUrlFromCentralIdentityAsync(intent, cancellationToken);
        }

        if (string.Equals(engine, ApplePlatform, StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveAppleSongUrlAsync(intent, cancellationToken);
        }

        if (string.Equals(engine, DeezerPlatform, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var apiTrack = await _deezerClient.GetTrackByIsrcAsync(intent.Isrc);
                var deezerId = apiTrack?.Id?.ToString();
                if (!string.IsNullOrWhiteSpace(deezerId))
                {
                    return $"https://www.deezer.com/track/{deezerId}";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Deezer ISRC URL resolve failed for {Isrc}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(intent.Isrc));
                }
            }

            return null;
        }

        if (string.Equals(engine, TidalPlatform, StringComparison.OrdinalIgnoreCase))
        {
            var durationSeconds = intent.DurationMs > 0 ? (int)Math.Round(intent.DurationMs / 1000d) : 0;
            return await _tidalDownloadService.ResolveTrackUrlAsync(
                intent.Title ?? string.Empty,
                intent.Artist ?? string.Empty,
                intent.Isrc ?? string.Empty,
                durationSeconds,
                cancellationToken);
        }

        return null;
    }

    private async Task<string?> ResolveAppleSongUrlAsync(DownloadIntent intent, CancellationToken cancellationToken)
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic.Storefront) ? "us" : settings.AppleMusic.Storefront;
            var language = EnglishUsLocale;

            if (!string.IsNullOrWhiteSpace(intent.Isrc))
            {
                using var isrcDoc = await _appleCatalogService.SearchAsync(
                    intent.Isrc,
                    limit: 5,
                    storefront: storefront,
                    language: language,
                    cancellationToken: cancellationToken,
                    options: new AppleMusicCatalogService.AppleSearchOptions(
                        TypesOverride: SongsField));
                var isrcMatch = TryExtractAppleSongUrl(isrcDoc.RootElement, intent.Isrc);
                if (!string.IsNullOrWhiteSpace(isrcMatch))
                {
                    return isrcMatch;
                }
            }

            var term = string.Join(' ', new[] { intent.Artist, intent.Title }.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (string.IsNullOrWhiteSpace(term))
            {
                return null;
            }

            using var doc = await _appleCatalogService.SearchAsync(
                term,
                limit: 5,
                storefront: storefront,
                language: language,
                cancellationToken: cancellationToken,
                options: new AppleMusicCatalogService.AppleSearchOptions(
                    TypesOverride: SongsField));
            return TryExtractAppleSongUrl(doc.RootElement, intent.Isrc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Apple catalog search failed for {Title} - {Artist}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(intent.Title),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(intent.Artist));
            return null;
        }
    }

    private async Task PopulateIntentMetadataAsync(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        string? resolvedDownloadTagSource,
        CancellationToken cancellationToken)
    {
        var sourceUrl = intent.SourceUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceUrl) && !string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            sourceUrl = $"https://open.spotify.com/track/{intent.SpotifyId}";
        }
        var isBoomplaySource = BoomplayMetadataService.IsBoomplayUrl(sourceUrl)
            || string.Equals(intent.SourceService, "boomplay", StringComparison.OrdinalIgnoreCase);

        var downloadTagSource = DownloadTagSourceHelper.NormalizeResolvedDownloadTagSource(resolvedDownloadTagSource);
        if (!string.IsNullOrWhiteSpace(downloadTagSource))
        {
            await PopulatePreferredMetadataSourceAsync(intent, downloadTagSource, sourceUrl, cancellationToken);
        }

        if (isBoomplaySource)
        {
            await PopulateBoomplayIntentMetadataAsync(intent, sourceUrl, cancellationToken);
        }

        if (HasCompleteIntentMetadata(intent))
        {
            await PopulateAppleMetadataWhenNeededAsync(intent, sourceUrl, settings, cancellationToken);
            return;
        }

        await PopulateSourceSpecificMetadataAsync(intent, sourceUrl, settings, cancellationToken);
    }

    private async Task PopulatePreferredMetadataSourceAsync(
        DownloadIntent intent,
        string downloadTagSource,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        if (string.Equals(downloadTagSource, SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
        {
            await EnsureSpotifyIdentityAsync(intent, sourceUrl, cancellationToken);

            var spotifyUrl = !string.IsNullOrWhiteSpace(intent.SpotifyId)
                ? $"https://open.spotify.com/track/{intent.SpotifyId}"
                : sourceUrl;

            if (!string.IsNullOrWhiteSpace(spotifyUrl))
            {
                await PopulateSpotifyMetadataAsync(intent, spotifyUrl, cancellationToken, overwriteExisting: true);
            }

            return;
        }

        if (string.Equals(downloadTagSource, DeezerPlatform, StringComparison.OrdinalIgnoreCase))
        {
            await EnsureDeezerIdentityAsync(intent, sourceUrl);

            var deezerUrl = !string.IsNullOrWhiteSpace(intent.DeezerId)
                ? $"https://www.deezer.com/track/{intent.DeezerId}"
                : sourceUrl;

            if (!string.IsNullOrWhiteSpace(deezerUrl))
            {
                await PopulateDeezerMetadataAsync(intent, deezerUrl, overwriteExisting: true);
            }
        }
    }

    private async Task EnsureSpotifyIdentityAsync(DownloadIntent intent, string sourceUrl, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            return;
        }

        await PopulateTidalMetadataWhenNeededAsync(intent, sourceUrl, cancellationToken);

        var spotifyId = TryExtractSpotifyId(sourceUrl);
        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            intent.SpotifyId = spotifyId;
            return;
        }

        intent.SpotifyId = await _spotifyIdResolver.ResolveTrackIdAsync(
            intent.Title ?? string.Empty,
            intent.Artist ?? string.Empty,
            intent.Album,
            intent.Isrc,
            cancellationToken) ?? string.Empty;
    }

    private async Task EnsureDeezerIdentityAsync(DownloadIntent intent, string sourceUrl)
    {
        var normalizedExistingDeezerId = NormalizeDeezerTrackId(intent.DeezerId);
        if (!string.IsNullOrWhiteSpace(normalizedExistingDeezerId))
        {
            intent.DeezerId = normalizedExistingDeezerId;
            return;
        }
        intent.DeezerId = string.Empty;

        var deezerId = NormalizeDeezerTrackId(TryExtractDeezerTrackId(sourceUrl));
        if (!string.IsNullOrWhiteSpace(deezerId))
        {
            intent.DeezerId = deezerId;
            return;
        }

        var deezerByIsrc = await ResolveDeezerTrackIdFromIsrcAsync(intent.Isrc);
        if (!string.IsNullOrWhiteSpace(deezerByIsrc))
        {
            intent.DeezerId = deezerByIsrc;
            return;
        }

        var deezerByMetadata = await ResolveDeezerTrackIdFromMetadataAsync(intent);
        if (!string.IsNullOrWhiteSpace(deezerByMetadata))
        {
            intent.DeezerId = deezerByMetadata;
        }
    }

    private async Task<string?> ResolveDeezerTrackIdFromIsrcAsync(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        try
        {
            var normalizedIsrc = isrc.Trim().ToUpperInvariant();
            var track = await _deezerClient.GetTrackByIsrcAsync(normalizedIsrc);
            return NormalizeDeezerTrackId(track?.Id?.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer ID ISRC lookup failed for {Isrc}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(isrc));
            }

            return null;
        }
    }

    private async Task<string?> ResolveDeezerTrackIdFromMetadataAsync(DownloadIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.Title)
            || string.IsNullOrWhiteSpace(intent.Artist))
        {
            return null;
        }

        try
        {
            var durationMs = intent.DurationMs > 0 ? intent.DurationMs : (int?)null;
            var resolvedId = await _deezerClient.GetTrackIdFromMetadataAsync(
                intent.Artist,
                intent.Title,
                intent.Album ?? string.Empty,
                durationMs);
            return NormalizeDeezerTrackId(resolvedId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Deezer ID metadata lookup failed for '{Title}' by '{Artist}'",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(intent.Title),
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(intent.Artist));
            }

            return null;
        }
    }

    private static bool HasCompleteIntentMetadata(DownloadIntent intent)
    {
        return !string.IsNullOrWhiteSpace(intent.Title)
            && !string.IsNullOrWhiteSpace(intent.Artist)
            && !string.IsNullOrWhiteSpace(intent.Album)
            && !string.IsNullOrWhiteSpace(intent.Isrc)
            && !string.IsNullOrWhiteSpace(intent.Cover)
            && intent.DurationMs > 0;
    }

    private static bool ShouldOverwriteString(bool overwriteExisting, string? existingValue, string? resolvedValue) =>
        (overwriteExisting || string.IsNullOrWhiteSpace(existingValue))
        && !string.IsNullOrWhiteSpace(resolvedValue);

    private static bool ShouldOverwriteInt(bool overwriteExisting, int existingValue, int resolvedValue) =>
        (overwriteExisting || existingValue <= 0)
        && resolvedValue > 0;

    private static bool ShouldOverwriteNullable<T>(bool overwriteExisting, T? existingValue, T? resolvedValue)
        where T : struct =>
        (overwriteExisting || !existingValue.HasValue)
        && resolvedValue.HasValue;

    private static void ApplyIntentStringValue(bool overwriteExisting, string? existingValue, string? resolvedValue, Action<string> assign)
    {
        if (ShouldOverwriteString(overwriteExisting, existingValue, resolvedValue))
        {
            assign(resolvedValue!);
        }
    }

    private static void ApplyIntentIntValue(bool overwriteExisting, int existingValue, int resolvedValue, Action<int> assign)
    {
        if (ShouldOverwriteInt(overwriteExisting, existingValue, resolvedValue))
        {
            assign(resolvedValue);
        }
    }

    private static void ApplyIntentNullableValue<T>(bool overwriteExisting, T? existingValue, T? resolvedValue, Action<T> assign)
        where T : struct
    {
        if (ShouldOverwriteNullable(overwriteExisting, existingValue, resolvedValue))
        {
            assign(resolvedValue!.Value);
        }
    }

    private static string ResolveAlbumArtist(string? albumArtist, string? artist) =>
        !string.IsNullOrWhiteSpace(albumArtist) ? albumArtist : artist ?? string.Empty;

    private List<string> NormalizeGenres(IEnumerable<string>? values)
    {
        var aliasMap = GetGenreAliasMap();
        return GenreTagAliasNormalizer.NormalizeExpandFilterAndDedupeValues(
            values,
            aliasMap,
            _genreTagNormalizationEnabled,
            GetGenreBlockList());
    }

    private IReadOnlyDictionary<string, string> GetGenreAliasMap()
    {
        if (_genreAliasMap != null)
        {
            return _genreAliasMap;
        }

        var settings = _settingsService.LoadSettings();
        _genreTagNormalizationEnabled = settings.NormalizeGenreTags;
        _genreBlockList = settings.GenreTagBlockList;
        _genreAliasMap = settings.NormalizeGenreTags
            ? GenreTagAliasNormalizer.BuildAliasMap(settings.GenreTagAliasRules)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return _genreAliasMap;
    }

    private IReadOnlyList<string> GetGenreBlockList()
    {
        _ = GetGenreAliasMap();
        return _genreBlockList ?? GenreTagAliasNormalizer.DefaultBlockedGenres;
    }

    private async Task<List<string>> ResolveSpotifyGenresAsync(
        IEnumerable<string>? artistIds,
        CancellationToken cancellationToken)
    {
        if (artistIds == null)
        {
            return new List<string>();
        }

        foreach (var artistId in artistIds)
        {
            var cachedGenres = await _artistPageCacheRepository.GetGenresAsync(SpotifyPlatform, artistId, cancellationToken);
            var normalizedCachedGenres = NormalizeGenres(cachedGenres);
            if (normalizedCachedGenres.Count > 0)
            {
                return normalizedCachedGenres;
            }

            var artist = await _spotifyPathfinderClient.FetchArtistOverviewAsync(artistId, cancellationToken);
            var pathfinderGenres = NormalizeGenres(artist?.Genres);
            if (pathfinderGenres.Count > 0)
            {
                await _artistPageCacheRepository.UpsertGenresAsync(SpotifyPlatform, artistId, pathfinderGenres, cancellationToken);
                return pathfinderGenres;
            }

            var fallbackGenres = NormalizeGenres(await _spotifyMetadataService.FetchArtistGenresFromSpotifyAsync(artistId, cancellationToken));
            if (fallbackGenres.Count > 0)
            {
                await _artistPageCacheRepository.UpsertGenresAsync(SpotifyPlatform, artistId, fallbackGenres, cancellationToken);
                return fallbackGenres;
            }
        }

        return new List<string>();
    }

    private async Task PopulateSpotifyMetadataAsync(
        DownloadIntent intent,
        string sourceUrl,
        CancellationToken cancellationToken,
        bool overwriteExisting = false)
    {
        try
        {
            var metadata = await _spotifyMetadataService.FetchByUrlAsync(sourceUrl, cancellationToken);
            if (metadata == null)
            {
                return;
            }

            var summary = metadata.TrackList.FirstOrDefault();
            var resolvedSourceUrl = metadata.SourceUrl ?? string.Empty;
            var resolvedSpotifyId = metadata.Id ?? string.Empty;
            var resolvedTitle = metadata.Name ?? summary?.Name ?? string.Empty;
            var resolvedArtist = metadata.Subtitle ?? summary?.Artists ?? string.Empty;
            var resolvedAlbum = summary?.Album ?? string.Empty;
            var resolvedCover = metadata.ImageUrl ?? summary?.ImageUrl ?? string.Empty;
            var resolvedDuration = metadata.DurationMs ?? summary?.DurationMs ?? 0;
            var resolvedIsrc = summary?.Isrc ?? string.Empty;
            var resolvedReleaseDate = summary?.ReleaseDate ?? string.Empty;
            var resolvedTrackNumber = summary?.TrackNumber ?? 0;
            var resolvedDiscNumber = summary?.DiscNumber ?? 0;
            var resolvedTrackTotal = summary?.TrackTotal ?? 0;
            var resolvedExplicit = summary?.Explicit;
            var resolvedLabel = summary?.Label ?? string.Empty;
            var resolvedGenres = summary?.Genres;
            ApplyIntentStringValue(overwriteExisting, intent.SourceUrl, resolvedSourceUrl, value => intent.SourceUrl = value);
            ApplyIntentStringValue(overwriteExisting, intent.SpotifyId, resolvedSpotifyId, value => intent.SpotifyId = value);
            ApplyIntentStringValue(overwriteExisting, intent.Title, resolvedTitle, value => intent.Title = value);
            ApplyIntentStringValue(overwriteExisting, intent.Artist, resolvedArtist, value => intent.Artist = value);
            ApplyIntentStringValue(overwriteExisting, intent.Album, resolvedAlbum, value => intent.Album = value);
            ApplyIntentStringValue(overwriteExisting, intent.AlbumArtist, resolvedArtist, value => intent.AlbumArtist = value);
            ApplyIntentStringValue(overwriteExisting, intent.Cover, resolvedCover, value => intent.Cover = value);
            ApplyIntentIntValue(overwriteExisting, intent.DurationMs, resolvedDuration, value => intent.DurationMs = value);
            ApplyIntentStringValue(overwriteExisting, intent.Isrc, resolvedIsrc, value => intent.Isrc = value);
            ApplyIntentStringValue(overwriteExisting, intent.ReleaseDate, resolvedReleaseDate, value => intent.ReleaseDate = value);
            ApplyIntentIntValue(overwriteExisting, intent.TrackNumber, resolvedTrackNumber, value => intent.TrackNumber = value);
            ApplyIntentIntValue(overwriteExisting, intent.DiscNumber, resolvedDiscNumber, value => intent.DiscNumber = value);
            ApplyIntentIntValue(overwriteExisting, intent.TrackTotal, resolvedTrackTotal, value => intent.TrackTotal = value);
            ApplyIntentStringValue(overwriteExisting, intent.Label, resolvedLabel, value => intent.Label = value);
            ApplyIntentNullableValue(overwriteExisting, intent.Explicit, resolvedExplicit, value => intent.Explicit = value);

            var artistIds = summary?.ArtistIds;
            if (overwriteExisting || intent.Genres.Count == 0)
            {
                var resolvedArtistGenres = await ResolveSpotifyGenresAsync(artistIds, cancellationToken);
                if (resolvedArtistGenres.Count > 0)
                {
                    intent.Genres = resolvedArtistGenres;
                }
                else
                {
                    var normalizedGenres = NormalizeGenres(resolvedGenres);
                    if (normalizedGenres.Count > 0)
                    {
                        intent.Genres = normalizedGenres;
                    }
                }
            }
            ApplyIntentStringValue(overwriteExisting, intent.Url, resolvedSourceUrl, value => intent.Url = value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify metadata lookup failed for intent url {Url}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(sourceUrl));
            }
        }
    }

    private static bool IsIntentPodcast(DownloadIntent intent, string sourceUrl)
    {
        return string.Equals(NormalizeContentType(intent.ContentType), DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase)
            || IsPodcastSource(sourceUrl, null);
    }

    private static string? BootstrapIntentDeezerIdentity(DownloadIntent intent, string sourceUrl, bool isPodcastIntent, ref string normalizedSourceUrl)
    {
        var normalizedExistingDeezerId = NormalizeDeezerTrackId(intent.DeezerId);
        intent.DeezerId = ResolveBootstrapDeezerId(normalizedExistingDeezerId, sourceUrl, isPodcastIntent);

        var normalizedDeezerId = NormalizeDeezerTrackId(intent.DeezerId);
        if (string.IsNullOrWhiteSpace(normalizedSourceUrl) && !string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            normalizedSourceUrl = isPodcastIntent
                ? $"https://www.deezer.com/episode/{normalizedDeezerId}"
                : $"https://www.deezer.com/track/{normalizedDeezerId}";
        }

        return normalizedDeezerId;
    }

    private static string ResolveBootstrapDeezerId(string? normalizedExistingDeezerId, string sourceUrl, bool isPodcastIntent)
    {
        if (!string.IsNullOrWhiteSpace(normalizedExistingDeezerId))
        {
            return normalizedExistingDeezerId;
        }

        return isPodcastIntent
            ? TryExtractDeezerEpisodeId(sourceUrl) ?? string.Empty
            : TryExtractDeezerTrackId(sourceUrl) ?? string.Empty;
    }

    private static (string Engine, string? SourceUrl, string Message, string MappingSource)? TryResolveDirectIntentSource(
        string engine,
        string sourceUrl,
        string? normalizedDeezerId,
        bool isPodcastIntent)
    {
        if (string.Equals(engine, DeezerPlatform, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            if (isPodcastIntent && IsUsablePodcastStreamUrl(sourceUrl))
            {
                return (engine, sourceUrl, string.Empty, "direct-episode-stream");
            }

            var deezerUrl = isPodcastIntent
                ? $"https://www.deezer.com/episode/{normalizedDeezerId}"
                : $"https://www.deezer.com/track/{normalizedDeezerId}";
            return (engine, deezerUrl, string.Empty, "deezer-id");
        }

        return !string.IsNullOrWhiteSpace(sourceUrl) && IsServiceUrlMatch(sourceUrl, engine)
            ? (engine, sourceUrl, string.Empty, "direct")
            : null;
    }

    private static (string Engine, string? SourceUrl, string Message, string MappingSource)? TryResolveDirectEngineIdentity(
        DownloadIntent intent,
        string engine,
        string sourceUrl)
    {
        if (string.Equals(engine, SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
        {
            var spotifyId = FirstNonEmpty(intent.SpotifyId, TryExtractSpotifyId(sourceUrl));
            return string.IsNullOrWhiteSpace(spotifyId)
                ? null
                : (engine, $"https://open.spotify.com/track/{Uri.EscapeDataString(spotifyId)}", string.Empty, "spotify-id");
        }

        if (string.Equals(engine, TidalPlatform, StringComparison.OrdinalIgnoreCase))
        {
            var tidalId = FirstNonEmpty(intent.TidalId, TryExtractTidalTrackId(sourceUrl));
            return string.IsNullOrWhiteSpace(tidalId)
                ? null
                : (engine, BuildTidalTrackUrl(tidalId), string.Empty, "tidal-id");
        }

        if (string.Equals(engine, QobuzPlatform, StringComparison.OrdinalIgnoreCase))
        {
            var qobuzId = FirstNonEmpty(intent.QobuzId, TryExtractQobuzTrackId(sourceUrl)?.ToString(CultureInfo.InvariantCulture));
            return string.IsNullOrWhiteSpace(qobuzId)
                ? null
                : (engine, BuildQobuzTrackUrl(qobuzId), string.Empty, "qobuz-id");
        }

        if (string.Equals(engine, AmazonPlatform, StringComparison.OrdinalIgnoreCase))
        {
            var amazonId = EngineLinkParser.NormalizeAmazonTrackId(intent.AmazonId)
                ?? EngineLinkParser.TryExtractAmazonTrackId(sourceUrl, RegexTimeout);
            return string.IsNullOrWhiteSpace(amazonId)
                ? null
                : (engine, $"https://music.amazon.com/tracks/{amazonId}", string.Empty, "amazon-id");
        }

        return null;
    }

    private async Task<(string Engine, string? SourceUrl, string Message, string MappingSource)?> TryResolveIsrcIntentSourceAsync(
        DownloadIntent intent,
        string engine,
        bool preferIsrcOnly,
        CancellationToken cancellationToken)
    {
        if (preferIsrcOnly && SupportsIsrcResolution(engine) && !string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return (engine, string.Empty, string.Empty, "isrc-fast");
        }

        if (string.IsNullOrWhiteSpace(intent.Isrc) || !SupportsIsrcResolution(engine))
        {
            return null;
        }

        var isrcUrl = await ResolveIsrcUrlAsync(engine, intent, cancellationToken);
        return string.IsNullOrWhiteSpace(isrcUrl)
            ? null
            : (engine, isrcUrl, string.Empty, "isrc");
    }

    private async Task<PlatformLinkResult?> ResolvePlatformLinksForIntentAsync(
        DownloadIntent intent,
        string sourceUrl,
        string? normalizedDeezerId,
        string? userCountry,
        PlatformLinkResult? preResolved,
        CancellationToken cancellationToken)
    {
        PlatformLinkResult? platformLinks = preResolved;
        if (platformLinks == null)
        {
            if (!string.IsNullOrWhiteSpace(normalizedDeezerId))
            {
                intent.DeezerId = normalizedDeezerId;
            }
            if (string.IsNullOrWhiteSpace(intent.SourceUrl) && !string.IsNullOrWhiteSpace(sourceUrl))
            {
                intent.SourceUrl = sourceUrl;
            }

            var settings = _settingsService.LoadSettings();
            var resolution = await ResolveCentralIdentityAsync(intent, settings, AllIdentityEngines, cancellationToken);
            ApplyResolvedIdentity(intent, resolution);
            platformLinks = BuildAvailabilityFromIdentity(resolution);
        }
        if (platformLinks == null && string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            var spotifyId = TryExtractSpotifyId(sourceUrl)
                ?? await _spotifyIdResolver.ResolveTrackIdAsync(
                    intent.Title ?? string.Empty,
                    intent.Artist ?? string.Empty,
                    intent.Album,
                    intent.Isrc,
                    cancellationToken);
            if (!string.IsNullOrWhiteSpace(spotifyId))
            {
                intent.SpotifyId = spotifyId;
                var settings = _settingsService.LoadSettings();
                var resolution = await ResolveCentralIdentityAsync(intent, settings, AllIdentityEngines, cancellationToken);
                ApplyResolvedIdentity(intent, resolution);
                platformLinks = BuildAvailabilityFromIdentity(resolution);
            }
        }
        return platformLinks;
    }

    private async Task<PlatformLinkResult?> TryResolveFallbackPlatformLinksAsync(
        DownloadIntent intent,
        string engine,
        DeezSpoTagSettings settings,
        bool resolverStrictMode,
        string? userCountry,
        PlatformLinkResult? platformLinks,
        CancellationToken cancellationToken)
    {
        if (platformLinks != null
            || !settings.FallbackSearch
            || (engine != DeezerPlatform && engine != ApplePlatform))
        {
            return platformLinks;
        }

        try
        {
            var normalized = await ResolveFallbackDeezerIdAsync(intent, settings.FallbackSearch, resolverStrictMode, cancellationToken);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            intent.DeezerId = normalized;
            intent.Isrc = await ResolveDeezerIsrcAsync(normalized) ?? intent.Isrc ?? string.Empty;
            var resolution = await ResolveCentralIdentityAsync(intent, settings, AllIdentityEngines, cancellationToken);
            ApplyResolvedIdentity(intent, resolution);
            return BuildAvailabilityFromIdentity(resolution);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Fallback Deezer search failed for intent.");
            return null;
        }
    }

    private async Task PopulateAppleMetadataAsync(DownloadIntent intent, string sourceUrl, DeezSpoTagSettings settings, CancellationToken cancellationToken)
    {
        var appleId = AppleIdParser.TryExtractFromUrl(sourceUrl);
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(intent.AppleId))
        {
            intent.AppleId = appleId;
        }

        if (IsAppleStationId(appleId) || IsAppleStationUrl(sourceUrl))
        {
            await PopulateAppleStationMetadataAsync(intent, appleId, settings, cancellationToken);
            return;
        }

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
        var language = EnglishUsLocale;
        try
        {
            using var doc = await _appleCatalogService.GetSongAsync(
                appleId,
                storefront,
                language,
                cancellationToken,
                settings.AppleMusic?.MediaUserToken);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                return;
            }

            var item = data[0];
            if (!TryResolveAppleSongAttributes(item, out var catalogAppleId, out var attrs))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(intent.AppleId) && !string.IsNullOrWhiteSpace(catalogAppleId))
            {
                intent.AppleId = catalogAppleId;
            }

            ApplyAppleSongCatalogMetadata(intent, attrs, settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple metadata lookup failed for intent url {Url}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(sourceUrl));
            }
        }
    }

    private static bool TryResolveAppleSongAttributes(JsonElement item, out string? catalogAppleId, out JsonElement attrs)
    {
        catalogAppleId = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;
        return item.TryGetProperty(AttributesField, out attrs);
    }

    private static void ApplyAppleSongCatalogMetadata(DownloadIntent intent, JsonElement attrs, DeezSpoTagSettings settings)
    {
        ApplyMissingAppleString(intent.Title, attrs, "name", value => intent.Title = value);
        ApplyMissingAppleString(intent.Artist, attrs, "artistName", value => intent.Artist = value);
        ApplyMissingAppleString(intent.Album, attrs, "albumName", value => intent.Album = value);
        ApplyMissingAppleString(intent.Isrc, attrs, "isrc", value => intent.Isrc = value);
        ApplyMissingAppleInt(intent.DurationMs, attrs, "durationInMillis", value => intent.DurationMs = value);
        ApplyMissingAppleString(intent.ReleaseDate, attrs, "releaseDate", value => intent.ReleaseDate = value);
        ApplyMissingAppleInt(intent.TrackNumber, attrs, "trackNumber", value => intent.TrackNumber = value);
        ApplyMissingAppleInt(intent.DiscNumber, attrs, "discNumber", value => intent.DiscNumber = value);
        ApplyAppleGenres(intent, attrs);
        ApplyMissingAppleString(intent.Label, attrs, "recordLabel", value => intent.Label = value);
        ApplyMissingAppleString(intent.Copyright, attrs, "copyright", value => intent.Copyright = value);
        ApplyMissingAppleString(intent.Composer, attrs, "composerName", value => intent.Composer = value);
        ApplyAppleExplicitMetadata(intent, attrs);
        ApplyMissingAppleString(intent.Url, attrs, "url", value => intent.Url = value);
        ApplyMissingAppleString(intent.Barcode, attrs, "upc", value => intent.Barcode = value);
        ApplyAppleAtmosMetadata(intent, attrs);
        ApplyAppleDigitalMasterMetadata(intent, attrs);
        ApplyAppleArtworkMetadata(intent, attrs, settings);
    }

    private static void ApplyMissingAppleString(string? currentValue, JsonElement attrs, string propertyName, Action<string> assign)
    {
        if (!string.IsNullOrWhiteSpace(currentValue))
        {
            return;
        }

        assign(ReadAppleString(attrs, propertyName));
    }

    private static void ApplyMissingAppleInt(int currentValue, JsonElement attrs, string propertyName, Action<int> assign)
    {
        if (currentValue > 0)
        {
            return;
        }

        assign(ReadAppleInt(attrs, propertyName));
    }

    private static string ReadAppleString(JsonElement attrs, string propertyName)
    {
        return attrs.TryGetProperty(propertyName, out var valueElement)
            ? valueElement.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int ReadAppleInt(JsonElement attrs, string propertyName)
    {
        return attrs.TryGetProperty(propertyName, out var valueElement) && valueElement.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static void ApplyAppleGenres(DownloadIntent intent, JsonElement attrs)
    {
        if (intent.Genres.Count != 0 || !attrs.TryGetProperty("genreNames", out var genresElement) || genresElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        intent.Genres.AddRange(genresElement.EnumerateArray()
            .Where(static genre => genre.ValueKind == JsonValueKind.String)
            .Select(static genre => genre.GetString())
            .OfType<string>()
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static void ApplyAppleExplicitMetadata(DownloadIntent intent, JsonElement attrs)
    {
        if (intent.Explicit.HasValue || !attrs.TryGetProperty("contentRating", out var ratingElement))
        {
            return;
        }

        var rating = ratingElement.GetString();
        if (!string.IsNullOrWhiteSpace(rating))
        {
            intent.Explicit = string.Equals(rating, "explicit", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void ApplyAppleAtmosMetadata(DownloadIntent intent, JsonElement attrs)
    {
        if (intent.HasAtmos || !attrs.TryGetProperty("audioTraits", out var traits) || traits.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        intent.HasAtmos = traits.EnumerateArray().Any(static trait =>
            trait.ValueKind == JsonValueKind.String
            && trait.GetString()?.IndexOf(AtmosQuality, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static void ApplyAppleDigitalMasterMetadata(DownloadIntent intent, JsonElement attrs)
    {
        if (intent.HasAppleDigitalMaster)
        {
            return;
        }

        intent.HasAppleDigitalMaster = ReadAppleBoolean(attrs, "isAppleDigitalMaster")
            || ReadAppleBoolean(attrs, "isMasteredForItunes");
    }

    private static bool ReadAppleBoolean(JsonElement attrs, string propertyName)
    {
        return attrs.TryGetProperty(propertyName, out var valueElement)
            && valueElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && valueElement.GetBoolean();
    }

    private static void ApplyAppleArtworkMetadata(DownloadIntent intent, JsonElement attrs, DeezSpoTagSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(intent.Cover)
            || !attrs.TryGetProperty("artwork", out var artwork)
            || !artwork.TryGetProperty("url", out var urlElement))
        {
            return;
        }

        var raw = urlElement.GetString() ?? string.Empty;
        var dims = AppleQueueHelpers.GetAppleArtworkDimensions(settings);
        var format = AppleQueueHelpers.GetAppleArtworkFormat(settings);
        intent.Cover = AppleQueueHelpers.BuildAppleArtworkUrl(raw, dims.SizeText, dims.Width, dims.Height, format);
    }

    private async Task PopulateAppleStationMetadataAsync(
        DownloadIntent intent,
        string stationId,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
        try
        {
            using var doc = await _appleCatalogService.GetStationAsync(stationId, storefront, EnglishUsLocale, cancellationToken);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
            {
                return;
            }

            var station = data[0];
            if (!station.TryGetProperty(AttributesField, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var stationName = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
            var stationUrl = attrs.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? string.Empty : string.Empty;
            ApplyAppleStationDefaults(intent, stationName, stationUrl);

            if (string.IsNullOrWhiteSpace(intent.Cover))
            {
                intent.Cover = AppleCatalogVideoAttributeParser.ResolveArtwork(attrs, 1200);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple station metadata lookup failed for {StationId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(stationId));
            }
        }
    }

    private static void ApplyAppleStationDefaults(DownloadIntent intent, string stationName, string stationUrl)
    {
        if (string.IsNullOrWhiteSpace(intent.Title))
        {
            intent.Title = stationName;
        }

        if (string.IsNullOrWhiteSpace(intent.Album))
        {
            intent.Album = stationName;
        }

        if (string.IsNullOrWhiteSpace(intent.Artist))
        {
            intent.Artist = "Apple Music";
        }

        if (string.IsNullOrWhiteSpace(intent.AlbumArtist))
        {
            intent.AlbumArtist = intent.Artist;
        }

        if (string.IsNullOrWhiteSpace(intent.Url))
        {
            intent.Url = stationUrl;
        }
    }

    private async Task PopulateBoomplayMetadataAsync(
        DownloadIntent intent,
        string sourceUrl,
        CancellationToken cancellationToken,
        bool overwriteExisting = false)
    {
        if (!BoomplayMetadataService.TryParseBoomplayUrl(sourceUrl, out var type, out var id)
            || !string.Equals(type, TrackType, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var track = await _boomplayMetadataService.GetSongAsync(id, cancellationToken);
            if (track == null)
            {
                return;
            }

            ApplyIntentStringValue(overwriteExisting, intent.SourceUrl, track.Url, value => intent.SourceUrl = value);
            ApplyIntentStringValue(overwriteExisting, intent.Title, track.Title, value => intent.Title = value);
            ApplyIntentStringValue(overwriteExisting, intent.Artist, track.Artist, value => intent.Artist = value);
            ApplyIntentStringValue(overwriteExisting, intent.Album, track.Album, value => intent.Album = value);
            ApplyIntentStringValue(overwriteExisting, intent.AlbumArtist, ResolveAlbumArtist(track.AlbumArtist, track.Artist), value => intent.AlbumArtist = value);
            ApplyIntentStringValue(overwriteExisting, intent.Cover, track.CoverUrl, value => intent.Cover = value);
            ApplyIntentIntValue(overwriteExisting, intent.DurationMs, track.DurationMs, value => intent.DurationMs = value);
            ApplyIntentStringValue(overwriteExisting, intent.Isrc, track.Isrc, value => intent.Isrc = value);
            ApplyIntentStringValue(overwriteExisting, intent.Label, track.Publisher, value => intent.Label = value);
            ApplyIntentStringValue(overwriteExisting, intent.Composer, track.Composer, value => intent.Composer = value);
            ApplyIntentIntValue(overwriteExisting, intent.TrackNumber, track.TrackNumber, value => intent.TrackNumber = value);
            ApplyIntentStringValue(overwriteExisting, intent.ReleaseDate, track.ReleaseDate, value => intent.ReleaseDate = value);
            if (overwriteExisting || intent.Genres.Count == 0)
            {
                var normalizedGenres = NormalizeGenres(track.Genres);
                if (normalizedGenres.Count > 0)
                {
                    intent.Genres = normalizedGenres;
                }
            }
            ApplyIntentStringValue(overwriteExisting, intent.Url, track.Url, value => intent.Url = value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Boomplay metadata lookup failed for intent url {Url}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(sourceUrl));
            }
        }
    }

    private async Task PopulateDeezerMetadataAsync(
        DownloadIntent intent,
        string sourceUrl,
        bool overwriteExisting = false,
        bool forceCoverOverwrite = false)
    {
        var trackId = NormalizeDeezerTrackId(string.IsNullOrWhiteSpace(intent.DeezerId)
            ? TryExtractDeezerTrackId(sourceUrl)
            : intent.DeezerId);
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }
        intent.DeezerId = trackId;

        try
        {
            var client = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
            if (client == null)
            {
                _logger.LogWarning("Skipping Deezer metadata lookup: user not authenticated.");
                return;
            }

            var track = await client.GetTrackAsync(trackId);
            if (track == null)
            {
                return;
            }

            ApplyDeezerCoreMetadata(intent, track, sourceUrl, overwriteExisting);
            ApplyDeezerReleaseMetadata(intent, track, overwriteExisting);
            ApplyDeezerGenres(intent, track, overwriteExisting);
            ApplyDeezerCommercialMetadata(intent, track, overwriteExisting);
            ApplyDeezerCover(intent, track, overwriteExisting, forceCoverOverwrite);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer metadata lookup failed for intent url {Url}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(sourceUrl));
            }
        }
    }

    private static string? TryExtractAppleSongUrl(JsonElement root, string? isrc)
    {
        if (!root.TryGetProperty("results", out var results)
            || !results.TryGetProperty(SongsField, out var songs)
            || !songs.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty(AttributesField, out var attributes))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(isrc)
                && attributes.TryGetProperty("isrc", out var isrcValue)
                && string.Equals(isrcValue.GetString(), isrc, StringComparison.OrdinalIgnoreCase)
                && attributes.TryGetProperty("url", out var urlValue))
            {
                return urlValue.GetString();
            }
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty(AttributesField, out var attributes))
            {
                continue;
            }

            if (attributes.TryGetProperty("url", out var urlValue))
            {
                return urlValue.GetString();
            }
        }

        return null;
    }

    private static bool IsServiceUrlMatch(string url, string engine)
    {
        return engine switch
        {
            DeezerPlatform => url.Contains(DeezerDomain, StringComparison.OrdinalIgnoreCase),
            ApplePlatform => url.Contains(AppleMusicDomain, StringComparison.OrdinalIgnoreCase),
            TidalPlatform => url.Contains("tidal.com", StringComparison.OrdinalIgnoreCase),
            AmazonPlatform => url.Contains("amazon.", StringComparison.OrdinalIgnoreCase)
                        || url.Contains("music.amazon", StringComparison.OrdinalIgnoreCase),
            QobuzPlatform => url.Contains(QobuzDomain, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsSpotifyDrivenIntent(string sourceUrl, string? spotifyId)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return !string.IsNullOrWhiteSpace(spotifyId);
        }

        if (sourceUrl.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase)
            || sourceUrl.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (sourceUrl.Contains(DeezerDomain, StringComparison.OrdinalIgnoreCase)
            || sourceUrl.Contains(AppleMusicDomain, StringComparison.OrdinalIgnoreCase)
            || sourceUrl.Contains("tidal.com", StringComparison.OrdinalIgnoreCase)
            || sourceUrl.Contains("amazon.", StringComparison.OrdinalIgnoreCase)
            || sourceUrl.Contains("music.amazon", StringComparison.OrdinalIgnoreCase)
            || sourceUrl.Contains(QobuzDomain, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(spotifyId);
    }

    private static List<FallbackPlanStep> BuildFallbackPlanFromSources(
        DownloadIntent intent,
        IReadOnlyList<string> planSources,
        bool fallbackSearchEnabled)
    {
        var steps = new List<FallbackPlanStep>();
        var sourceUrl = intent.SourceUrl ?? string.Empty;
        var hasPlatformLinkInputs = !string.IsNullOrWhiteSpace(sourceUrl)
            || !string.IsNullOrWhiteSpace(intent.DeezerId)
            || !string.IsNullOrWhiteSpace(intent.SpotifyId);
        var requiredInputsSnapshot = BuildRequiredInputsSnapshot(intent, sourceUrl);

        AppendFallbackPlanSteps(
            steps,
            planSources,
            sourceUrl,
            intent.Isrc,
            hasPlatformLinkInputs,
            fallbackSearchEnabled,
            requiredInputsSnapshot);

        return steps;
    }

    private static List<string> BuildRequiredInputsSnapshot(DownloadIntent intent, string sourceUrl)
    {
        var requiredInputs = new List<string>();
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            requiredInputs.Add("URL");
        }
        if (!string.IsNullOrWhiteSpace(intent.DeezerId))
        {
            requiredInputs.Add("DeezerId");
        }
        if (!string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            requiredInputs.Add("SpotifyId");
        }
        if (!string.IsNullOrWhiteSpace(intent.Isrc))
        {
            requiredInputs.Add("ISRC");
        }
        if (!string.IsNullOrWhiteSpace(intent.Title) || !string.IsNullOrWhiteSpace(intent.Artist))
        {
            requiredInputs.Add("TitleArtist");
        }

        return requiredInputs.Count == 0
            ? new List<string>()
            : requiredInputs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ResolveFallbackResolutionStrategy(
        string sourceUrl,
        string source,
        string? isrc,
        bool hasPlatformLinkInputs,
        bool fallbackSearchEnabled)
    {
        if (!string.IsNullOrWhiteSpace(sourceUrl) && IsServiceUrlMatch(sourceUrl, source))
        {
            return "direct_url";
        }

        if (SupportsIsrcResolution(source) && !string.IsNullOrWhiteSpace(isrc))
        {
            return "isrc";
        }

        if (hasPlatformLinkInputs)
        {
            return "mapped_url";
        }

        if (fallbackSearchEnabled)
        {
            return "search";
        }

        return "unknown";
    }

    private static void AppendFallbackPlanSteps(
        List<FallbackPlanStep> steps,
        IEnumerable<string> planSources,
        string sourceUrl,
        string? isrc,
        bool hasPlatformLinkInputs,
        bool fallbackSearchEnabled,
        IReadOnlyList<string> requiredInputsSnapshot)
    {
        var index = 0;
        foreach (var decoded in planSources
            .Select(DownloadSourceOrder.DecodeAutoSource)
            .Where(static decoded => !string.IsNullOrWhiteSpace(decoded.Source)))
        {
            var resolutionStrategy = ResolveFallbackResolutionStrategy(
                sourceUrl,
                decoded.Source,
                isrc,
                hasPlatformLinkInputs,
                fallbackSearchEnabled);

            steps.Add(new FallbackPlanStep(
                StepId: $"step-{index++}",
                Engine: decoded.Source,
                Quality: decoded.Quality,
                RequiredInputs: requiredInputsSnapshot.ToList(),
                ResolutionStrategy: resolutionStrategy));
        }
    }

    private List<string> PrioritizeAutoSourcesByHealth(
        IEnumerable<string> sources,
        DeezSpoTagSettings settings,
        bool intentRequestsAuto,
        string? normalizedPreferredEngine)
    {
        if (intentRequestsAuto || settings.DownloadEngineOrder?.Enabled == true)
        {
            return sources
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .ToList();
        }

        var protectedEngine = normalizedPreferredEngine;
        return _apiHealthTracker.PrioritizeSources(sources, protectedEngine).ToList();
    }

    private static List<string> PrioritizeFallbackSourcesByHealth(
        IEnumerable<string> sources,
        DeezSpoTagSettings settings,
        bool allowCrossEngineFallback,
        string engine,
        IDownloadApiHealthTracker apiHealthTracker)
    {
        var sourceList = sources
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .ToList();
        if (allowCrossEngineFallback || IsAutoService(settings.Service) || settings.DownloadEngineOrder?.Enabled == true)
        {
            return sourceList;
        }

        return apiHealthTracker.PrioritizeSources(
                sourceList,
                allowCrossEngineFallback ? null : engine)
            .ToList();
    }

    private static bool IsAutoService(string? service)
        => string.Equals(service?.Trim(), AutoService, StringComparison.OrdinalIgnoreCase);

    private static void ApplyDeezerCoreMetadata(DownloadIntent intent, DeezSpoTag.Core.Models.Deezer.ApiTrack track, string sourceUrl, bool overwriteExisting)
    {
        ApplyIntentStringValue(overwriteExisting, intent.SourceUrl, track.Link ?? sourceUrl, value => intent.SourceUrl = value);
        ApplyIntentStringValue(overwriteExisting, intent.DeezerId, track.Id, value => intent.DeezerId = value);
        ApplyIntentStringValue(overwriteExisting, intent.Title, track.Title, value => intent.Title = value);
        ApplyIntentStringValue(overwriteExisting, intent.Artist, track.Artist?.Name, value => intent.Artist = value);
        ApplyIntentStringValue(overwriteExisting, intent.Album, track.Album?.Title, value => intent.Album = value);
        ApplyIntentStringValue(overwriteExisting, intent.AlbumArtist, track.Album?.Artist?.Name, value => intent.AlbumArtist = value);
        ApplyIntentStringValue(overwriteExisting, intent.Isrc, track.Isrc, value => intent.Isrc = value);
        ApplyIntentIntValue(overwriteExisting, intent.DurationMs, track.Duration > 0 ? track.Duration * 1000 : 0, value => intent.DurationMs = value);
    }

    private static void ApplyDeezerReleaseMetadata(DownloadIntent intent, DeezSpoTag.Core.Models.Deezer.ApiTrack track, bool overwriteExisting)
    {
        var releaseDate = track.ReleaseDate
            ?? track.Album?.ReleaseDate
            ?? track.Album?.OriginalReleaseDate
            ?? string.Empty;
        ApplyIntentStringValue(overwriteExisting, intent.ReleaseDate, releaseDate, value => intent.ReleaseDate = value);
        ApplyIntentIntValue(overwriteExisting, intent.TrackNumber, track.TrackPosition, value => intent.TrackNumber = value);
        ApplyIntentIntValue(overwriteExisting, intent.DiscNumber, track.DiskNumber, value => intent.DiscNumber = value);
        ApplyIntentIntValue(overwriteExisting, intent.TrackTotal, track.Album?.NbTracks ?? 0, value => intent.TrackTotal = value);
        ApplyIntentIntValue(overwriteExisting, intent.DiscTotal, track.Album?.NbDisk ?? 0, value => intent.DiscTotal = value);
        ApplyIntentNullableValue(overwriteExisting, intent.Explicit, track.ExplicitLyrics || (track.Album?.ExplicitLyrics ?? false), value => intent.Explicit = value);
    }

    private void ApplyDeezerGenres(DownloadIntent intent, DeezSpoTag.Core.Models.Deezer.ApiTrack track, bool overwriteExisting)
    {
        if (!overwriteExisting && intent.Genres.Count > 0)
        {
            return;
        }

        var genres = track.Genres;
        if (genres == null || genres.Count == 0)
        {
            genres = track.Album?.Genres?.Data?
                .Select(g => g.Name ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        }

        var normalizedGenres = NormalizeGenres(genres);
        if (normalizedGenres.Count > 0)
        {
            intent.Genres = normalizedGenres;
        }
    }

    private static void ApplyDeezerCommercialMetadata(DownloadIntent intent, DeezSpoTag.Core.Models.Deezer.ApiTrack track, bool overwriteExisting)
    {
        ApplyIntentStringValue(overwriteExisting, intent.Label, track.Album?.Label, value => intent.Label = value);
        ApplyIntentStringValue(overwriteExisting, intent.Barcode, track.Album?.Upc, value => intent.Barcode = value);
        var copyright = track.Copyright ?? track.Album?.Copyright ?? string.Empty;
        ApplyIntentStringValue(overwriteExisting, intent.Copyright, copyright, value => intent.Copyright = value);
        var deezerUrl = track.Link ?? track.Share ?? string.Empty;
        ApplyIntentStringValue(overwriteExisting, intent.Url, deezerUrl, value => intent.Url = value);
    }

    private static void ApplyDeezerCover(DownloadIntent intent, DeezSpoTag.Core.Models.Deezer.ApiTrack track, bool overwriteExisting, bool forceCoverOverwrite)
    {
        var coverUrl = track.Album?.CoverXl
            ?? track.Album?.CoverBig
            ?? track.Album?.CoverMedium
            ?? string.Empty;
        if (forceCoverOverwrite && !string.IsNullOrWhiteSpace(coverUrl))
        {
            intent.Cover = coverUrl;
            return;
        }

        ApplyIntentStringValue(overwriteExisting, intent.Cover, coverUrl, value => intent.Cover = value);
    }

    private async Task PopulateBoomplayIntentMetadataAsync(DownloadIntent intent, string sourceUrl, CancellationToken cancellationToken)
    {
        await PopulateBoomplayMetadataAsync(intent, sourceUrl, cancellationToken);
        await EnsureDeezerIdentityAsync(intent, sourceUrl);
        if (string.IsNullOrWhiteSpace(intent.DeezerId))
        {
            return;
        }

        var deezerUrl = $"https://www.deezer.com/track/{intent.DeezerId}";
        await PopulateDeezerMetadataAsync(intent, deezerUrl, overwriteExisting: false, forceCoverOverwrite: true);
    }

    private async Task PopulateAppleMetadataWhenNeededAsync(
        DownloadIntent intent,
        string sourceUrl,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (sourceUrl.Contains(AppleMusicDomain, StringComparison.OrdinalIgnoreCase)
            && !intent.HasAppleDigitalMaster)
        {
            await PopulateAppleMetadataAsync(intent, sourceUrl, settings, cancellationToken);
        }
    }

    private async Task PopulateSourceSpecificMetadataAsync(
        DownloadIntent intent,
        string sourceUrl,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (IsSpotifySourceUrl(sourceUrl))
        {
            await PopulateSpotifyMetadataAsync(intent, sourceUrl, cancellationToken);
            return;
        }

        if (sourceUrl.Contains(DeezerDomain, StringComparison.OrdinalIgnoreCase))
        {
            await PopulateDeezerMetadataAsync(intent, sourceUrl);
            return;
        }

        if (sourceUrl.Contains(AppleMusicDomain, StringComparison.OrdinalIgnoreCase))
        {
            await PopulateAppleMetadataAsync(intent, sourceUrl, settings, cancellationToken);
            return;
        }

        if (IsTidalSourceUrl(sourceUrl))
        {
            await PopulateTidalMetadataAsync(intent, sourceUrl, cancellationToken);
        }
    }

    private static bool IsSpotifySourceUrl(string sourceUrl)
    {
        return sourceUrl.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase)
            || sourceUrl.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTidalSourceUrl(string sourceUrl)
    {
        return sourceUrl.Contains("tidal.com", StringComparison.OrdinalIgnoreCase);
    }

    private async Task PopulateTidalMetadataWhenNeededAsync(
        DownloadIntent intent,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        if (!IsTidalSourceUrl(sourceUrl) && string.IsNullOrWhiteSpace(intent.TidalId))
        {
            return;
        }

        await PopulateTidalMetadataAsync(intent, sourceUrl, cancellationToken);
    }

    private async Task PopulateTidalMetadataAsync(
        DownloadIntent intent,
        string sourceUrl,
        CancellationToken cancellationToken,
        bool overwriteExisting = false)
    {
        var tidalInput = FirstNonEmpty(intent.TidalId, TryExtractTidalTrackId(sourceUrl), sourceUrl);
        var track = await _tidalDownloadService.ResolveTrackMetadataAsync(tidalInput, cancellationToken);
        if (track == null)
        {
            return;
        }

        ApplyIntentStringValue(overwriteExisting, intent.SourceUrl, track.Url, value => intent.SourceUrl = value);
        ApplyIntentStringValue(overwriteExisting, intent.Url, track.Url, value => intent.Url = value);
        ApplyIntentStringValue(overwriteExisting, intent.TidalId, track.Id.ToString(CultureInfo.InvariantCulture), value => intent.TidalId = value);
        ApplyIntentStringValue(overwriteExisting, intent.Title, track.Title, value => intent.Title = value);
        ApplyIntentStringValue(overwriteExisting, intent.Artist, track.Artist, value => intent.Artist = value);
        ApplyIntentStringValue(overwriteExisting, intent.Album, track.Album, value => intent.Album = value);
        ApplyIntentStringValue(overwriteExisting, intent.AlbumArtist, track.Artist, value => intent.AlbumArtist = value);
        ApplyIntentStringValue(overwriteExisting, intent.Isrc, track.Isrc, value => intent.Isrc = value);
        ApplyIntentIntValue(overwriteExisting, intent.DurationMs, track.DurationSeconds * 1000, value => intent.DurationMs = value);
    }

    private static readonly Dictionary<string, int> CanonicalQualityRanks =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [AtmosQualityUpper] = 130,
            [TidalAtmosQuality] = 130,
            ["VIDEO"] = 125,
            ["27"] = 120,
            ["HI_RES_LOSSLESS"] = 115,
            ["ALAC"] = 110,
            ["7"] = 100,
            ["HI_RES"] = 95,
            ["6"] = 90,
            ["LOSSLESS"] = 80,
            ["FLAC"] = 70,
            ["9"] = 60,
            ["AAC"] = 50,
            ["5"] = 45,
            ["HIGH"] = 45,
            ["3"] = 40,
            ["LOW"] = 35,
            ["1"] = 30
        };

    private static readonly Dictionary<string, int> LocalQualityRanks =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [AtmosQualityUpper] = 5,
            [TidalAtmosQuality] = 5,
            ["VIDEO"] = 0,
            ["27"] = 4,
            ["HI_RES_LOSSLESS"] = 4,
            ["ALAC"] = 3,
            ["7"] = 4,
            ["HI_RES"] = 4,
            ["6"] = 3,
            ["LOSSLESS"] = 3,
            ["FLAC"] = 3,
            ["9"] = 3,
            ["AAC"] = 2,
            ["5"] = 2,
            ["HIGH"] = 2,
            ["3"] = 2,
            ["LOW"] = 1,
            ["1"] = 1
        };

    private static int? ParseRequestedQualityRank(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return null;
        }

        var normalized = quality.Trim();
        var catalogCanonicalRank = QualityCatalog.GetLibraryFolderCanonicalRank(normalized);
        if (catalogCanonicalRank.HasValue)
        {
            return catalogCanonicalRank.Value;
        }

        if (CanonicalQualityRanks.TryGetValue(normalized, out var canonicalRank))
        {
            return canonicalRank;
        }

        if (int.TryParse(normalized, out var parsed))
        {
            return parsed;
        }

        return MediaQualityInference.InferCanonicalQualityRankFromText(normalized, AtmosQuality);
    }

    private static int? ParseRequestedLocalQualityRank(string? quality, int? canonicalRequestedQualityRank)
    {
        if (!string.IsNullOrWhiteSpace(quality))
        {
            var normalized = quality.Trim();
            var catalogLocalRank = QualityCatalog.GetLibraryFolderLocalRank(normalized);
            if (catalogLocalRank.HasValue)
            {
                return catalogLocalRank.Value;
            }

            if (LocalQualityRanks.TryGetValue(normalized, out var mapped))
            {
                return mapped;
            }

            if (int.TryParse(normalized, out var parsed))
            {
                return MediaQualityInference.MapRequestedNumericQualityToLocalRank(parsed);
            }

            var inferredFromText = MediaQualityInference.InferLocalQualityRankFromText(normalized, AtmosQuality, treatPodcastAsVideo: false);
            if (inferredFromText.HasValue)
            {
                return inferredFromText.Value;
            }
        }

        if (!canonicalRequestedQualityRank.HasValue)
        {
            return null;
        }

        return MediaQualityInference.MapCanonicalRankToLocalRank(canonicalRequestedQualityRank.Value);
    }

    private static string? ResolvePreferredQuality(DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings, string? engine)
    {
        if (settings == null || string.IsNullOrWhiteSpace(engine))
        {
            return null;
        }

        var normalized = engine.Trim().ToLowerInvariant();
        if (settings.DownloadEngineOrder?.Enabled == true)
        {
            return DownloadSourceOrder.ResolveEngineQualitySources(settings, normalized, requestedQuality: null, strict: false)
                .Select(DownloadSourceOrder.DecodeAutoSource)
                .Where(source => string.Equals(source.Source, normalized, StringComparison.OrdinalIgnoreCase))
                .Select(source => source.Quality)
                .FirstOrDefault(quality => !string.IsNullOrWhiteSpace(quality));
        }

        string? preferred = normalized switch
        {
            ApplePlatform => settings.AppleMusic?.PreferredAudioProfile,
            DeezerPlatform => settings.MaxBitrate > 0 ? settings.MaxBitrate.ToString() : null,
            TidalPlatform => settings.TidalQuality,
            QobuzPlatform => settings.QobuzQuality,
            AmazonPlatform => "ULTRA_HD_FLAC",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        var options = QualityCatalog.GetEngineQualityOptions();
        if (!options.TryGetValue(normalized, out var engineOptions) || engineOptions.Count == 0)
        {
            return null;
        }

        return engineOptions[0].Value;
    }

    private static string? ResolveStereoPreferredQuality(DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings, string? engine)
    {
        var preferred = ResolvePreferredQuality(settings, engine);
        if (!IsAtmosQuality(preferred))
        {
            return preferred;
        }

        if (string.IsNullOrWhiteSpace(engine))
        {
            return preferred;
        }

        var normalized = engine.Trim().ToLowerInvariant();
        var options = QualityCatalog.GetEngineQualityOptions();
        if (!options.TryGetValue(normalized, out var engineOptions))
        {
            return preferred;
        }

        return engineOptions
            .Select(option => option.Value)
            .FirstOrDefault(value => !IsAtmosQuality(value))
            ?? preferred;
    }

    private async Task<long?> ResolveRoutedDestinationFolderIdAsync(
        DownloadIntent intent,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (intent == null || !_libraryRepository.IsConfigured)
        {
            return null;
        }

        var requestedMode = ResolveRequestedFolderMode(intent);
        if (string.IsNullOrWhiteSpace(requestedMode))
        {
            return null;
        }

        var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
        var enabledFolders = folders.Where(folder => folder.Enabled).ToList();
        if (enabledFolders.Count == 0)
        {
            return null;
        }

        if (string.Equals(requestedMode, DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase))
        {
            var configuredAtmosFolderId = settings.MultiQuality?.SecondaryDestinationFolderId;
            if (configuredAtmosFolderId.HasValue
                && enabledFolders.Any(folder => folder.Id == configuredAtmosFolderId.Value))
            {
                return configuredAtmosFolderId.Value;
            }

            return enabledFolders
                .FirstOrDefault(folder => IsFolderMode(folder, DownloadContentTypes.Atmos))
                ?.Id;
        }

        if (string.Equals(requestedMode, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase))
        {
            var byPath = FindFolderByRootPath(enabledFolders, settings.Video?.VideoDownloadLocation);
            if (byPath != null)
            {
                return byPath.Id;
            }

            return enabledFolders
                .FirstOrDefault(folder => IsFolderMode(folder, DownloadContentTypes.Video))
                ?.Id;
        }

        if (string.Equals(requestedMode, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase))
        {
            var byPath = FindFolderByRootPath(enabledFolders, settings.Podcast?.DownloadLocation);
            if (byPath != null)
            {
                return byPath.Id;
            }

            return enabledFolders
                .FirstOrDefault(folder => IsFolderMode(folder, DownloadContentTypes.Podcast))
                ?.Id;
        }

        return null;
    }

    private static string? ResolveRequestedFolderMode(DownloadIntent intent)
    {
        var normalizedContentType = NormalizeContentType(intent?.ContentType);
        if (string.Equals(normalizedContentType, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase)
            || IsVideoSource(intent?.SourceUrl, null))
        {
            return DownloadContentTypes.Video;
        }

        if (string.Equals(normalizedContentType, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase)
            || IsPodcastSource(intent?.SourceUrl, null))
        {
            return DownloadContentTypes.Podcast;
        }

        if (string.Equals(normalizedContentType, DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase)
            || IsAtmosQuality(intent?.Quality))
        {
            return DownloadContentTypes.Atmos;
        }

        return null;
    }

    private static bool IsFolderMode(FolderDto folder, string mode)
    {
        var normalized = NormalizeContentType(folder?.DesiredQuality);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = DownloadContentTypes.Stereo;
        }

        return string.Equals(normalized, mode, StringComparison.OrdinalIgnoreCase);
    }

    private static FolderDto? FindFolderByRootPath(IEnumerable<FolderDto> folders, string? rootPath)
    {
        var normalizedTarget = NormalizeRootPath(rootPath);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return null;
        }

        return folders.FirstOrDefault(folder =>
            string.Equals(NormalizeRootPath(folder.RootPath), normalizedTarget, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRootPath(string? path)
    {
        return (path ?? string.Empty)
            .Trim()
            .TrimEnd('/', '\\')
            .ToLowerInvariant();
    }

    private static string ResolveContentType(
        string? explicitContentType,
        string? sourceUrl,
        string? collectionType,
        bool hasAtmos,
        string? quality)
    {
        if (IsVideoSource(sourceUrl, collectionType))
        {
            return DownloadContentTypes.Video;
        }

        if (IsPodcastSource(sourceUrl, collectionType))
        {
            return DownloadContentTypes.Podcast;
        }

        var normalized = NormalizeContentType(explicitContentType);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        if (IsAtmosQuality(quality))
        {
            return DownloadContentTypes.Atmos;
        }

        if (hasAtmos && string.IsNullOrWhiteSpace(quality))
        {
            return DownloadContentTypes.Atmos;
        }

        return DownloadContentTypes.Stereo;
    }

    private static bool IsAtmosQuality(string? quality) =>
        !string.IsNullOrWhiteSpace(quality)
        && quality.Contains(AtmosQuality, StringComparison.OrdinalIgnoreCase);

    private static bool IsAppleAtmosQuality(string? quality) =>
        string.Equals(quality?.Trim(), AtmosQuality, StringComparison.OrdinalIgnoreCase);

    private static bool SettingsRequestsAtmosSource(DeezSpoTagSettings settings)
        => string.Equals(settings.DownloadSourceContentType?.Trim(), DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase);

    private static bool IsMultiQualityDualEnabled(MultiQualityDownloadSettings? multiQuality)
    {
        if (multiQuality == null)
        {
            return false;
        }

        // Backward/forward compatibility:
        // some persisted configs only toggle one of these flags.
        return multiQuality.Enabled || multiQuality.SecondaryEnabled;
    }

    private static bool IsAppleAtmosOnlyRequest(string? engine, string? quality)
    {
        return string.Equals(engine, ApplePlatform, StringComparison.OrdinalIgnoreCase)
            && IsAtmosQuality(quality);
    }

    private static bool UseStrictQualityFallback(
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string? engine,
        string? quality)
    {
        // Atmos is Apple-only in this pipeline. Keep it strict: no stereo fallback chain.
        if (IsAppleAtmosOnlyRequest(engine, quality))
        {
            return true;
        }

        return !settings.FallbackBitrate;
    }

    private static string? NormalizeContentType(string? contentType)
    {
        var normalized = contentType?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool IsVideoSource(string? sourceUrl, string? collectionType)
    {
        return AppleVideoClassifier.IsVideo(sourceUrl, collectionType);
    }

    private static string ResolvePodcastEngine(DownloadIntent intent, string normalizedPreferredEngine)
    {
        if (IsKnownDownloadEngine(normalizedPreferredEngine)
            && !string.Equals(normalizedPreferredEngine, AutoService, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPreferredEngine;
        }

        var sourceServiceEngine = NormalizeEngineName(intent.SourceService);
        if (IsKnownDownloadEngine(sourceServiceEngine))
        {
            return sourceServiceEngine;
        }

        var sourceUrlEngine = ResolveEngineFromUrl(intent.SourceUrl);
        if (IsKnownDownloadEngine(sourceUrlEngine))
        {
            return sourceUrlEngine;
        }

        return string.Empty;
    }

    private static string ResolveVisibleQueueEngine(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        bool isPodcastIntent)
    {
        var normalizedPreferredEngine = NormalizeEngineName(intent.PreferredEngine);
        if (isPodcastIntent)
        {
            return ResolvePodcastEngine(intent, normalizedPreferredEngine);
        }

        if (RequiresAppleOnly(intent, intent.Quality))
        {
            return ApplePlatform;
        }

        if (IsKnownDownloadEngine(normalizedPreferredEngine)
            && !string.Equals(normalizedPreferredEngine, AutoService, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPreferredEngine;
        }

        var targetQuality = string.IsNullOrWhiteSpace(intent.Quality) ? null : intent.Quality;
        var shouldUseConfiguredOrder = string.Equals(normalizedPreferredEngine, AutoService, StringComparison.OrdinalIgnoreCase)
            || settings.DownloadEngineOrder?.Enabled == true
            || IsAutoService(settings.Service);
        if (shouldUseConfiguredOrder)
        {
            var configuredSources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: targetQuality);
            if (configuredSources.Count > 0)
            {
                return DownloadSourceOrder.DecodeAutoSource(configuredSources[0]).Source;
            }
        }

        var sourceServiceEngine = NormalizeEngineName(intent.SourceService);
        if (IsKnownDownloadEngine(sourceServiceEngine))
        {
            return sourceServiceEngine;
        }

        var sourceUrlEngine = ResolveEngineFromUrl(intent.SourceUrl);
        if (IsKnownDownloadEngine(sourceUrlEngine))
        {
            return sourceUrlEngine;
        }

        var autoSources = DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: targetQuality);
        return autoSources.Count == 0
            ? string.Empty
            : DownloadSourceOrder.DecodeAutoSource(autoSources[0]).Source;
    }

    private static EngineQueueItemBase BuildVisiblePreResolutionPayload(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        string engine,
        string? selectedQuality,
        string? contentTypeOverride = null,
        long? destinationFolderIdOverride = null)
    {
        var contentType = string.IsNullOrWhiteSpace(contentTypeOverride)
            ? ResolveContentType(
                intent.ContentType,
                intent.SourceUrl,
                string.IsNullOrWhiteSpace(intent.Album) ? TrackType : AlbumType,
                intent.HasAtmos,
                selectedQuality)
            : contentTypeOverride;
        var autoSources = ResolveVisiblePreResolutionSources(intent, settings, engine, selectedQuality, contentType);
        var selectedAutoIndex = DownloadSourceOrder.FindAutoIndex(autoSources, engine, selectedQuality);
        var fallbackPlan = BuildFallbackPlanFromSources(intent, autoSources, settings.FallbackSearch);
        var durationSeconds = intent.DurationMs > 0 ? (int)Math.Round(intent.DurationMs / 1000d) : 0;
        var payload = CreateQueuePayloadForEngine(engine);
        PopulateStandardQueuePayload(payload, intent, new StandardPayloadContext(
            intent.SourceUrl ?? string.Empty,
            string.IsNullOrWhiteSpace(intent.Album) ? TrackType : AlbumType,
            contentType,
            autoSources,
            Math.Max(0, selectedAutoIndex),
            fallbackPlan,
            intent.ReleaseDate ?? string.Empty,
            durationSeconds,
            destinationFolderIdOverride ?? intent.DestinationFolderId,
            string.Equals(contentType, DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase)
                ? AtmosQuality
                : string.Empty));
        payload.Engine = engine;
        payload.SourceService = string.IsNullOrWhiteSpace(intent.SourceService) ? engine : intent.SourceService;
        payload.Quality = selectedQuality ?? ResolvePreferredQuality(settings, engine) ?? string.Empty;
        ApplyIntentMetadataForPayload(payload, intent);
        ApplyVisiblePayloadResolutionState(payload);
        return payload;
    }

    private static void ApplyVisiblePayloadResolutionState(EngineQueueItemBase payload)
    {
        var sourceUrl = payload.SourceUrl ?? string.Empty;
        PopulateEngineIdentityFromSourceUrl(payload, sourceUrl);
        var hasDirectIdentity = payload.Engine switch
        {
            TidalPlatform => !string.IsNullOrWhiteSpace(payload.TidalId),
            QobuzPlatform => !string.IsNullOrWhiteSpace(payload.QobuzId),
            AmazonPlatform => !string.IsNullOrWhiteSpace(payload.AmazonId),
            ApplePlatform => !string.IsNullOrWhiteSpace(payload.AppleId)
                || IsServiceUrlMatch(sourceUrl, ApplePlatform),
            DeezerPlatform => !string.IsNullOrWhiteSpace(payload.DeezerId),
            _ => false
        };

        if (!hasDirectIdentity)
        {
            payload.ResolutionStatus = QueuePreResolutionPayload.Pending;
            return;
        }

        payload.ResolutionStatus = QueuePreResolutionPayload.Resolved;
        payload.ResolvedAtUtc = DateTimeOffset.UtcNow;
        payload.ResolvedEngine = payload.Engine;
        payload.ResolvedSourceUrl = sourceUrl;
        payload.ResolvedQuality = payload.Quality;
        payload.ResolvedAutoIndex = payload.AutoIndex;
    }

    private static void PopulateEngineIdentityFromSourceUrl(EngineQueueItemBase payload, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return;
        }

        switch (payload.Engine)
        {
            case DeezerPlatform when string.IsNullOrWhiteSpace(payload.DeezerId):
                payload.DeezerId = EngineLinkParser.TryExtractDeezerTrackId(sourceUrl) ?? string.Empty;
                break;
            case QobuzPlatform when string.IsNullOrWhiteSpace(payload.QobuzId):
                payload.QobuzId = EngineLinkParser.TryExtractQobuzTrackId(sourceUrl) ?? string.Empty;
                break;
            case TidalPlatform when string.IsNullOrWhiteSpace(payload.TidalId):
                payload.TidalId = EngineLinkParser.TryExtractTidalTrackId(sourceUrl) ?? string.Empty;
                break;
            case AmazonPlatform when string.IsNullOrWhiteSpace(payload.AmazonId):
                payload.AmazonId = EngineLinkParser.TryExtractAmazonTrackId(sourceUrl, EngineLinkParser.RegexTimeout) ?? string.Empty;
                break;
        }
    }

    private static List<string> ResolveVisiblePreResolutionSources(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        string engine,
        string? selectedQuality,
        string? contentType)
    {
        if (IsAtmosSourceRequest(contentType, selectedQuality))
        {
            return BuildAtmosAutoSources(settings.MultiQuality, settings.MultiQuality?.AtmosDownloadFallback == true);
        }

        var useCrossEngineOrder = IsMusicIntent(intent)
            && !IsVideoIntent(intent)
            && (IsAutoService(settings.Service) || settings.DownloadEngineOrder?.Enabled == true);
        if (useCrossEngineOrder)
        {
            var sources = DownloadSourceOrder.ResolveQualityAutoSources(
                settings,
                includeDeezer: true,
                targetQuality: selectedQuality);
            if (sources.Count > 0)
            {
                return sources;
            }
        }

        return DownloadSourceOrder.ResolveEngineQualitySources(
            settings,
            engine,
            selectedQuality,
            strict: UseStrictQualityFallback(settings, engine, selectedQuality));
    }

    private static EngineQueueItemBase CreateQueuePayloadForEngine(string engine)
        => engine switch
        {
            ApplePlatform => new AppleQueueItem(),
            TidalPlatform => new TidalQueueItem(),
            AmazonPlatform => new AmazonQueueItem(),
            QobuzPlatform => new QobuzQueueItem(),
            _ => new DeezerQueueItem()
        };

    private static void ApplyIntentMetadataForPayload(EngineQueueItemBase payload, DownloadIntent intent)
    {
        switch (payload)
        {
            case DeezerQueueItem deezer:
                ApplyIntentMetadata(deezer, intent);
                break;
            case AppleQueueItem apple:
                ApplyIntentMetadataToStereoPayload(apple, intent);
                break;
            case TidalQueueItem tidal:
                ApplyIntentMetadata(tidal, intent);
                break;
            case AmazonQueueItem amazon:
                ApplyIntentMetadata(amazon, intent);
                break;
            case QobuzQueueItem qobuz:
                ApplyIntentMetadata(qobuz, intent);
                break;
        }
    }

    private static string NormalizeEngineName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private static string? InferPlatformFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("spotify.com", StringComparison.Ordinal))
        {
            return SpotifyPlatform;
        }

        if (host.Contains(DeezerDomain, StringComparison.Ordinal))
        {
            return DeezerPlatform;
        }

        if (host.Contains("music.apple.", StringComparison.Ordinal)
            || host.Contains("itunes.apple.", StringComparison.Ordinal))
        {
            return ApplePlatform;
        }

        if (host.Contains("tidal.com", StringComparison.Ordinal))
        {
            return TidalPlatform;
        }

        if (host.Contains(QobuzDomain, StringComparison.Ordinal))
        {
            return QobuzPlatform;
        }

        if (host.Contains("amazon.", StringComparison.Ordinal))
        {
            return AmazonPlatform;
        }

        return null;
    }

    private static bool IsKnownDownloadEngine(string? engine)
    {
        var normalized = NormalizeEngineName(engine);
        return DownloadSourceCatalog.GetEngineOptions()
            .Any(option => string.Equals(option.Value, normalized, StringComparison.Ordinal));
    }

    private static string ResolveEngineFromUrl(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.Contains(AppleMusicDomain, StringComparison.Ordinal))
        {
            return ApplePlatform;
        }
        if (host.Contains(DeezerDomain, StringComparison.Ordinal))
        {
            return DeezerPlatform;
        }
        if (host.Contains("tidal.com", StringComparison.Ordinal))
        {
            return TidalPlatform;
        }
        if (host.Contains("amazon.", StringComparison.Ordinal))
        {
            return AmazonPlatform;
        }
        if (host.Contains(QobuzDomain, StringComparison.Ordinal))
        {
            return QobuzPlatform;
        }

        return string.Empty;
    }

    private static bool IsPodcastSource(string? sourceUrl, string? collectionType)
    {
        if (!string.IsNullOrWhiteSpace(collectionType)
            && string.Equals(collectionType, EpisodeType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(sourceUrl)
               && sourceUrl.Contains("/episode/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsablePodcastStreamUrl(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isDeezerEpisodePage = uri.Host.Contains(DeezerDomain, StringComparison.OrdinalIgnoreCase)
                                  && uri.AbsolutePath.Contains("/episode", StringComparison.OrdinalIgnoreCase);
        return !isDeezerEpisodePage;
    }

    private static bool IsMusicIntent(DownloadIntent intent)
    {
        if (intent == null)
        {
            return false;
        }

        var normalizedContentType = NormalizeContentType(intent.ContentType);
        if (string.Equals(normalizedContentType, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedContentType, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsVideoSource(intent.SourceUrl, null) || IsPodcastSource(intent.SourceUrl, null))
        {
            return false;
        }

        return true;
    }

    private static bool RequiresAppleOnly(DownloadIntent intent, string? targetQuality)
    {
        if (intent == null)
        {
            return false;
        }

        if (string.Equals(intent.ContentType, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsVideoSource(intent.SourceUrl, null))
        {
            return true;
        }

        if (IsAppleAtmosQuality(targetQuality))
        {
            return true;
        }

        return false;
    }

    private static bool RequiresVerifiedAtmosCapability(DownloadIntent intent, string? targetQuality)
    {
        if (intent == null)
        {
            return false;
        }

        var normalizedContentType = NormalizeContentType(intent.ContentType);
        return string.Equals(normalizedContentType, DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase)
               || IsAtmosQuality(targetQuality);
    }

    private async Task<bool> ValidateAppleAtmosCapabilityAsync(
        DownloadIntent intent,
        string? resolvedSourceUrl,
        PlatformLinkResult? availability,
        DeezSpoTagSettings settings,
        string queueBranch,
        CancellationToken cancellationToken)
    {
        var appleSourceUrl = ResolveAppleAtmosValidationSourceUrl(intent, resolvedSourceUrl, availability, settings);
        if (string.IsNullOrWhiteSpace(appleSourceUrl))
        {
            _activityLog.Warn($"Skipped {queueBranch} Atmos queue: Apple source URL unavailable for capability check.");
            return false;
        }

        var probeIntent = BuildAppleAtmosProbeIntent(intent);
        await PopulateAppleMetadataAsync(probeIntent, appleSourceUrl, settings, cancellationToken);
        if (probeIntent.HasAtmos)
        {
            intent.HasAtmos = true;
            if (string.IsNullOrWhiteSpace(intent.AppleId))
            {
                intent.AppleId = probeIntent.AppleId;
            }

            return true;
        }

        _activityLog.Warn(
            $"Skipped {queueBranch} Atmos queue: no Atmos variant found for title='{intent.Title ?? string.Empty}' artist='{intent.Artist ?? string.Empty}'.");
        return false;
    }

    private static string? ResolveAppleAtmosValidationSourceUrl(
        DownloadIntent intent,
        string? resolvedSourceUrl,
        PlatformLinkResult? availability,
        DeezSpoTagSettings settings)
    {
        if (ContainsAppleMusicUrl(resolvedSourceUrl))
        {
            return resolvedSourceUrl;
        }

        if (ContainsAppleMusicUrl(intent.SourceUrl))
        {
            return intent.SourceUrl;
        }

        if (ContainsAppleMusicUrl(availability?.AppleMusicUrl))
        {
            return availability!.AppleMusicUrl;
        }

        if (string.IsNullOrWhiteSpace(intent.AppleId))
        {
            return null;
        }

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
            ? "us"
            : settings.AppleMusic!.Storefront;
        return $"https://music.apple.com/{storefront}/song/{intent.AppleId}";
    }

    private static bool ContainsAppleMusicUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
               && url.Contains(AppleMusicDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static DownloadIntent BuildAppleAtmosProbeIntent(DownloadIntent intent)
    {
        return new DownloadIntent
        {
            AppleId = intent.AppleId,
            Isrc = intent.Isrc,
            Title = intent.Title,
            Artist = intent.Artist,
            Album = intent.Album,
            SourceUrl = intent.SourceUrl
        };
    }

    private async Task<bool> TryEnqueueAtmosSecondaryAsync(AtmosSecondaryEnqueueRequest request)
    {
        var secondaryDestinationFolderId =
            request.SecondaryDestinationFolderId
            ?? request.Settings.MultiQuality?.SecondaryDestinationFolderId
            ?? request.Intent.SecondaryDestinationFolderId;
        if (secondaryDestinationFolderId is null)
        {
            _logger.LogWarning(
                "Multi-quality secondary skipped: secondary destination folder is required for Atmos.");
            return false;
        }
        if (request.PrimaryDestinationFolderId.HasValue
            && request.PrimaryDestinationFolderId.Value == secondaryDestinationFolderId.Value)
        {
            _logger.LogWarning(
                "Multi-quality secondary skipped: Atmos destination {DestinationFolderId} matches primary destination.",
                secondaryDestinationFolderId.Value);
            return false;
        }
        var distinctRootCheck = await DownloadDestinationGuard.ValidateDistinctRootsAsync(
            request.PrimaryDestinationFolderId,
            secondaryDestinationFolderId,
            _libraryRepository,
            request.CancellationToken);
        if (!distinctRootCheck.Ok)
        {
            _logger.LogWarning(
                "Multi-quality secondary skipped: {Reason}",
                distinctRootCheck.Error ?? "Stereo and Atmos destinations must resolve to different folder roots.");
            return false;
        }

        var atmosEngines = ResolveAtmosEngineOrder(
            request.Settings.MultiQuality,
            request.Settings.MultiQuality?.AtmosSearchFallback == true);
        foreach (var atmosEngine in atmosEngines)
        {
            var queued = atmosEngine switch
            {
                var engine when string.Equals(engine, TidalPlatform, StringComparison.OrdinalIgnoreCase)
                    => await TryEnqueueTidalAtmosSecondaryAsync(request, secondaryDestinationFolderId.Value),
                var engine when string.Equals(engine, AmazonPlatform, StringComparison.OrdinalIgnoreCase)
                    => await TryEnqueueAmazonAtmosSecondaryAsync(request, secondaryDestinationFolderId.Value),
                _ => await TryEnqueueAppleAtmosSecondaryAsync(request, secondaryDestinationFolderId.Value)
            };
            if (queued)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryEnqueueAppleAtmosSecondaryAsync(
        AtmosSecondaryEnqueueRequest request,
        long secondaryDestinationFolderId)
    {
        const string secondaryQuality = AtmosQualityUpper;
        var candidate = await ResolveIntentAsync(
            request.Intent,
            ApplePlatform,
            request.PreferIsrcOnly,
            request.Availability,
            request.Settings,
            request.CancellationToken);
        if (!string.IsNullOrWhiteSpace(candidate.Message) && candidate.Engine == string.Empty)
        {
            _activityLog.Warn($"Secondary Atmos mapping skipped: {candidate.Message}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate.SourceUrl))
        {
            _activityLog.Warn("Secondary Atmos mapping skipped: Apple URL unavailable.");
            return false;
        }

        if (!await ValidateAppleAtmosCapabilityAsync(
                request.Intent,
                candidate.SourceUrl,
                request.Availability,
                request.Settings,
                "secondary",
                request.CancellationToken))
        {
            return false;
        }

        var (payload, isVideo) = await BuildApplePayloadBaseAsync(
            request.Intent,
            candidate.SourceUrl,
            secondaryQuality,
            request.Settings,
            request.CancellationToken);
        if (isVideo)
        {
            _activityLog.Warn("Secondary Atmos mapping skipped: Apple video detected.");
            return false;
        }

        // Secondary branch in dual routing is Atmos-only by design.
        payload.ContentType = DownloadContentTypes.Atmos;
        payload.Id = Guid.NewGuid().ToString("N");
        payload.DestinationFolderId = secondaryDestinationFolderId;
        payload.QualityBucket = AtmosQuality;
        var autoSources = BuildAtmosAutoSources(request.Settings.MultiQuality, request.Settings.MultiQuality?.AtmosDownloadFallback == true);
        var fallbackInfo = BuildEnqueueFallbackInfo(new EnqueueFallbackRequest(
            request.Intent,
            request.Settings,
            ApplePlatform,
            secondaryQuality,
            MusicIntent: IsMusicIntent(request.Intent),
            AllowCrossEngineFallback: request.Settings.MultiQuality?.AtmosDownloadFallback == true,
            UseAtmosStereoDual: false,
            AutoSources: autoSources,
            Availability: request.Availability));
        payload.FallbackPlan = fallbackInfo.FallbackPlan;
        payload.AutoSources = fallbackInfo.AutoSources;
        payload.AutoIndex = fallbackInfo.AutoIndex;

        var enqueueDecision = await EnqueueItemAsync(
            payload,
            request.BlockRules,
            request.AllowQualityUpgrade,
            ParseRequestedQualityRank(secondaryQuality),
            request.CancellationToken);
        if (enqueueDecision.Success)
        {
            request.Queued.Add(enqueueDecision.QueueUuid ?? payload.Id);
            _deezspotagListener.SendAddedToQueue(payload.ToQueuePayload());
            return true;
        }

        return false;
    }

    private async Task<bool> TryEnqueueTidalAtmosSecondaryAsync(
        AtmosSecondaryEnqueueRequest request,
        long secondaryDestinationFolderId)
    {
        const string secondaryQuality = TidalAtmosQuality;
        var durationSeconds = request.Intent.DurationMs > 0 ? (int)Math.Round(request.Intent.DurationMs / 1000d) : 0;
        await ResolveTrackIdentityMatrixAsync(
            request.Intent,
            request.Settings,
            BuildIdentityTargetsForDownload(request.Settings, new[] { TidalPlatform }),
            request.CancellationToken);
        var resolvedAtmosTrack = await _tidalDownloadService.ResolveAtmosTrackAsync(
            request.Intent.Title ?? string.Empty,
            request.Intent.Artist ?? string.Empty,
            request.Intent.Album ?? string.Empty,
            FirstNonEmpty(request.Intent.TidalId, TryExtractTidalTrackId(request.Intent.SourceUrl)) ?? string.Empty,
            request.Intent.Isrc ?? string.Empty,
            durationSeconds,
            request.CancellationToken);
        var tidalAtmosUrl = resolvedAtmosTrack?.Url;
        if (string.IsNullOrWhiteSpace(tidalAtmosUrl))
        {
            _activityLog.Warn("Secondary Atmos mapping skipped: Tidal URL unavailable.");
            return false;
        }

        var autoSources = BuildAtmosAutoSources(request.Settings.MultiQuality, request.Settings.MultiQuality?.AtmosDownloadFallback == true);
        var fallbackInfo = BuildEnqueueFallbackInfo(new EnqueueFallbackRequest(
            request.Intent,
            request.Settings,
            TidalPlatform,
            secondaryQuality,
            MusicIntent: IsMusicIntent(request.Intent),
            AllowCrossEngineFallback: request.Settings.MultiQuality?.AtmosDownloadFallback == true,
            UseAtmosStereoDual: false,
            AutoSources: autoSources,
            Availability: request.Availability));
        var payload = new TidalQueueItem();
        PopulateStandardQueuePayload(payload, request.Intent, new StandardPayloadContext(
            tidalAtmosUrl,
            string.IsNullOrWhiteSpace(request.Intent.Album) ? TrackType : AlbumType,
            DownloadContentTypes.Atmos,
            fallbackInfo.AutoSources,
            fallbackInfo.AutoIndex,
            fallbackInfo.FallbackPlan,
            string.Empty,
            durationSeconds,
            secondaryDestinationFolderId,
            AtmosQuality));
        payload.Quality = secondaryQuality;
        var resolvedTidalAtmosId = TryExtractTidalTrackId(tidalAtmosUrl) ?? string.Empty;
        payload.TidalId = resolvedTidalAtmosId;
        payload.Id = Guid.NewGuid().ToString("N");
        payload.QualityBucket = AtmosQuality;
        ApplyIntentMetadata(payload, request.Intent);
        payload.TidalId = resolvedTidalAtmosId;
        payload.Isrc = FirstNonEmpty(resolvedAtmosTrack?.Isrc, request.Intent.Isrc) ?? string.Empty;
        payload.Album = ResolveResolvedAlbumForAtmos(request.Intent.Album, resolvedAtmosTrack?.Album) ?? string.Empty;
        payload.AlbumArtist = FirstNonEmpty(
            request.Intent.AlbumArtist,
            resolvedAtmosTrack?.Artist,
            request.Intent.Artist) ?? string.Empty;

        var enqueueDecision = await EnqueueItemAsync(
            payload,
            request.BlockRules,
            request.AllowQualityUpgrade,
            ParseRequestedQualityRank(secondaryQuality),
            request.CancellationToken);
        if (enqueueDecision.Success)
        {
            request.Queued.Add(enqueueDecision.QueueUuid ?? payload.Id);
            _deezspotagListener.SendAddedToQueue(payload.ToQueuePayload());
            return true;
        }

        return false;
    }

    private async Task<bool> TryEnqueueAmazonAtmosSecondaryAsync(
        AtmosSecondaryEnqueueRequest request,
        long secondaryDestinationFolderId)
    {
        const string secondaryQuality = TidalAtmosQuality;
        var amazonTrack = await ResolveAmazonAtmosAvailabilityAsync(request.Intent, request.CancellationToken);
        if (amazonTrack is null)
        {
            _activityLog.Warn("Secondary Atmos mapping skipped: Amazon Music Atmos unavailable.");
            return false;
        }

        var durationSeconds = request.Intent.DurationMs > 0 ? (int)Math.Round(request.Intent.DurationMs / 1000d) : 0;
        var autoSources = BuildAtmosAutoSources(request.Settings.MultiQuality, request.Settings.MultiQuality?.AtmosDownloadFallback == true);
        var fallbackInfo = BuildEnqueueFallbackInfo(new EnqueueFallbackRequest(
            request.Intent,
            request.Settings,
            AmazonPlatform,
            secondaryQuality,
            MusicIntent: IsMusicIntent(request.Intent),
            AllowCrossEngineFallback: request.Settings.MultiQuality?.AtmosDownloadFallback == true,
            UseAtmosStereoDual: false,
            AutoSources: autoSources,
            Availability: request.Availability));
        var payload = new AmazonQueueItem();
        PopulateStandardQueuePayload(payload, request.Intent, new StandardPayloadContext(
            amazonTrack.Url,
            string.IsNullOrWhiteSpace(request.Intent.Album) ? TrackType : AlbumType,
            DownloadContentTypes.Atmos,
            fallbackInfo.AutoSources,
            fallbackInfo.AutoIndex,
            fallbackInfo.FallbackPlan,
            string.Empty,
            durationSeconds,
            secondaryDestinationFolderId,
            AtmosQuality));
        payload.Quality = secondaryQuality;
        payload.AmazonId = amazonTrack.Id;
        payload.Id = Guid.NewGuid().ToString("N");
        payload.QualityBucket = AtmosQuality;
        ApplyIntentMetadata(payload, request.Intent);

        var enqueueDecision = await EnqueueItemAsync(
            payload,
            request.BlockRules,
            request.AllowQualityUpgrade,
            ParseRequestedQualityRank(secondaryQuality),
            request.CancellationToken);
        if (enqueueDecision.Success)
        {
            request.Queued.Add(enqueueDecision.QueueUuid ?? payload.Id);
            _deezspotagListener.SendAddedToQueue(payload.ToQueuePayload());
            return true;
        }

        return false;
    }

    private static string[] ResolveAtmosEngineOrder(
        MultiQualityDownloadSettings? multiQuality,
        bool includeFallbackEngine)
    {
        var primary = NormalizeAtmosEngine(multiQuality?.AtmosEngine);
        if (!includeFallbackEngine)
        {
            return new[] { primary };
        }

        var fallbackOrder = new[] { ApplePlatform, TidalPlatform, AmazonPlatform };
        return new[] { primary }
            .Concat(fallbackOrder.Where(engine => !string.Equals(engine, primary, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static List<string> BuildAtmosAutoSources(
        MultiQualityDownloadSettings? multiQuality,
        bool includeFallbackEngine)
    {
        return ResolveAtmosEngineOrder(multiQuality, includeFallbackEngine)
            .Select(engine => DownloadSourceOrder.EncodeAutoSource(engine, ResolveAtmosQualityForEngine(engine)))
            .ToList();
    }

    private static string NormalizeAtmosEngine(string? engine)
    {
        var normalized = engine?.Trim();
        if (string.Equals(normalized, TidalPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return TidalPlatform;
        }

        if (string.Equals(normalized, AmazonPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return AmazonPlatform;
        }

        return ApplePlatform;
    }

    private static string ResolveAtmosQualityForEngine(string engine)
        => string.Equals(engine, TidalPlatform, StringComparison.OrdinalIgnoreCase)
           || string.Equals(engine, AmazonPlatform, StringComparison.OrdinalIgnoreCase)
            ? TidalAtmosQuality
            : AtmosQualityUpper;

    private static string? TryExtractTidalTrackId(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed)
            || !parsed.Host.Contains("tidal.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("track", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(segments[i + 1], out var trackId)
                && trackId > 0)
            {
                return trackId.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private async Task<(AppleQueueItem Payload, bool IsVideo)> BuildApplePayloadBaseAsync(
        DownloadIntent intent,
        string sourceUrl,
        string? selectedQuality,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var isVideo = IsVideoSource(sourceUrl, null)
            || string.Equals(intent.ContentType, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase);
        var isStation = IsAppleStationUrl(sourceUrl);
        var appleId = AppleIdParser.TryExtractFromUrl(sourceUrl);
        var preferSourceAppleId = IsAtmosQuality(selectedQuality) || intent.HasAtmos;
        appleId = await ResolveAppleIdForStorefrontAsync(
            appleId,
            sourceUrl,
            intent.Isrc,
            isVideo,
            preferSourceAppleId,
            settings,
            cancellationToken);
        var videoMeta = await TryGetAppleVideoMetadataAsync(sourceUrl, appleId, settings, cancellationToken);
        var effectiveQuality = ResolveAppleEffectiveQuality(intent, selectedQuality, settings, videoMeta, isVideo);
        var collectionType = ResolveAppleCollectionType(intent, isVideo, isStation, videoMeta);
        var contentType = ResolveContentType(
            intent.ContentType,
            sourceUrl,
            collectionType,
            videoMeta?.HasAtmos == true || intent.HasAtmos,
            effectiveQuality);
        var durationSeconds = ResolveDurationSeconds(intent, videoMeta);
        var resolvedTitle = ResolvePreferredValue(videoMeta?.Title, intent.Title);
        var resolvedArtist = ResolvePreferredValue(videoMeta?.Artist, intent.Artist);
        var resolvedAlbum = ResolvePreferredValue(videoMeta?.AlbumName, intent.Album);
        var resolvedAlbumArtist = ResolvePreferredValue(videoMeta?.Artist, intent.AlbumArtist);
        var resolvedIsrc = ResolvePreferredValue(videoMeta?.Isrc, intent.Isrc);
        var resolvedCover = ResolvePreferredValue(videoMeta?.Cover, intent.Cover);
        var resolvedReleaseDate = ResolvePreferredValue(videoMeta?.ReleaseDate, intent.ReleaseDate);

        var payload = new AppleQueueItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Engine = ApplePlatform,
            QueueOrigin = "intent",
            SourceService = ApplePlatform,
            SourceUrl = sourceUrl,
            AppleId = appleId ?? string.Empty,
            WatchlistSource = intent.WatchlistSource ?? string.Empty,
            WatchlistPlaylistId = intent.WatchlistPlaylistId ?? string.Empty,
            WatchlistTrackId = intent.WatchlistTrackId ?? string.Empty,
            WatchlistOrigin = intent.WatchlistOrigin ?? string.Empty,
            WatchlistUnavailableSettingsFingerprint = intent.WatchlistUnavailableSettingsFingerprint ?? string.Empty,
            CollectionName = resolvedAlbum,
            CollectionType = collectionType,
            ContentType = contentType,
            Title = resolvedTitle,
            Artist = resolvedArtist,
            Album = resolvedAlbum,
            AlbumArtist = ResolveEffectiveAlbumArtist(
                resolvedAlbumArtist,
                resolvedArtist,
                settings.Tags?.SingleAlbumArtist != false),
            Isrc = resolvedIsrc,
            Genres = intent.Genres.ToList(),
            Label = intent.Label ?? string.Empty,
            Copyright = intent.Copyright ?? string.Empty,
            Explicit = intent.Explicit,
            Composer = intent.Composer ?? string.Empty,
            Url = intent.Url ?? string.Empty,
            Barcode = intent.Barcode ?? string.Empty,
            DeezerId = intent.DeezerId ?? string.Empty,
            Cover = resolvedCover,
            ReleaseDate = resolvedReleaseDate,
            DurationSeconds = durationSeconds,
            Position = intent.Position,
            TrackNumber = intent.TrackNumber,
            DiscNumber = intent.DiscNumber,
            TrackTotal = intent.TrackTotal,
            DiscTotal = intent.DiscTotal,
            Danceability = intent.Danceability,
            Energy = intent.Energy,
            Valence = intent.Valence,
            Acousticness = intent.Acousticness,
            Instrumentalness = intent.Instrumentalness,
            Speechiness = intent.Speechiness,
            Loudness = intent.Loudness,
            Tempo = intent.Tempo,
            TimeSignature = intent.TimeSignature,
            Liveness = intent.Liveness,
            MusicKey = intent.MusicKey ?? string.Empty,
            Quality = effectiveQuality,
            HasAppleDigitalMaster = intent.HasAppleDigitalMaster,
            SpotifyId = intent.SpotifyId ?? string.Empty,
            Size = 1
        };

        return (payload, isVideo);
    }

    private static string ResolveAppleEffectiveQuality(
        DownloadIntent intent,
        string? selectedQuality,
        DeezSpoTagSettings settings,
        AppleVideoMetadata? videoMeta,
        bool isVideo)
    {
        var normalizedRequestedContentType = NormalizeContentType(intent.ContentType);
        var prefersStereoVariant = string.Equals(
            normalizedRequestedContentType,
            DownloadContentTypes.Stereo,
            StringComparison.OrdinalIgnoreCase);
        var prefersAtmosVariant = string.Equals(
            normalizedRequestedContentType,
            DownloadContentTypes.Atmos,
            StringComparison.OrdinalIgnoreCase);
        var preferredAppleProfile = settings.AppleMusic.PreferredAudioProfile ?? AtmosQuality;
        var videoHasAtmosCapability = videoMeta?.HasAtmos == true || intent.HasAtmos || prefersAtmosVariant || IsAtmosQuality(selectedQuality);
        var requestedVideoQuality = selectedQuality;
        if (string.IsNullOrWhiteSpace(requestedVideoQuality)
            || string.Equals(requestedVideoQuality, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase))
        {
            requestedVideoQuality = videoHasAtmosCapability ? AtmosQuality : DownloadContentTypes.Video;
        }

        if (isVideo)
        {
            return requestedVideoQuality;
        }

        if (!string.IsNullOrWhiteSpace(selectedQuality))
        {
            return selectedQuality;
        }

        if (prefersStereoVariant)
        {
            return ResolveStereoPreferredQuality(settings, ApplePlatform) ?? preferredAppleProfile;
        }

        if (prefersAtmosVariant || videoMeta?.HasAtmos == true)
        {
            return AtmosQuality;
        }

        return preferredAppleProfile;
    }

    private static string ResolveAppleCollectionType(
        DownloadIntent intent,
        bool isVideo,
        bool isStation,
        AppleVideoMetadata? videoMeta)
    {
        if (isVideo)
        {
            return "music-video";
        }

        if (isStation)
        {
            return "station";
        }

        if (!string.IsNullOrWhiteSpace(videoMeta?.AlbumName))
        {
            return "music-video";
        }

        return string.IsNullOrWhiteSpace(intent.Album) ? TrackType : AlbumType;
    }

    private static int ResolveDurationSeconds(DownloadIntent intent, AppleVideoMetadata? videoMeta)
    {
        return videoMeta?.DurationSeconds ?? (intent.DurationMs > 0 ? (int)Math.Round(intent.DurationMs / 1000d) : 0);
    }

    private static string ResolvePreferredValue(string? preferredValue, string? fallbackValue)
    {
        return string.IsNullOrWhiteSpace(preferredValue)
            ? fallbackValue ?? string.Empty
            : preferredValue;
    }

    private static string ResolveEffectiveAlbumArtist(string? albumArtist, string? artist, bool singleAlbumArtist)
    {
        var fallbackArtist = artist ?? string.Empty;
        var candidate = string.IsNullOrWhiteSpace(albumArtist) ? fallbackArtist : albumArtist!;
        if (!singleAlbumArtist)
        {
            return candidate;
        }

        var primary = DeezSpoTag.Core.Utils.ArtistNameNormalizer.ExtractPrimaryArtist(
            string.IsNullOrWhiteSpace(fallbackArtist) ? candidate : fallbackArtist);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary;
        }

        var normalizedCandidate = DeezSpoTag.Core.Utils.ArtistNameNormalizer.ExtractPrimaryArtist(candidate);
        return string.IsNullOrWhiteSpace(normalizedCandidate) ? candidate : normalizedCandidate;
    }

    private static void ApplyIntentMetadata(DeezerQueueItem payload, DownloadIntent intent)
    {
        if (!string.Equals(payload.ContentType, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase))
        {
            ApplyIntentMetadataToStereoPayload(payload, intent);
        }

        payload.DeezerAlbumId = ResolveIntentString(intent.DeezerAlbumId, payload.DeezerAlbumId);
        payload.DeezerArtistId = ResolveIntentString(intent.DeezerArtistId, payload.DeezerArtistId);
        if (string.Equals(payload.ContentType, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(payload.DeezerArtistId)
            && !string.IsNullOrWhiteSpace(payload.DeezerAlbumId))
        {
            payload.DeezerArtistId = payload.DeezerAlbumId;
        }

        ApplyWatchlistMetadata(payload, intent);
    }

    private static void ApplyIntentMetadata(TidalQueueItem payload, DownloadIntent intent)
    {
        ApplyIntentMetadataToStereoPayload(payload, intent);
    }

    private static void ApplyIntentMetadata(QobuzQueueItem payload, DownloadIntent intent)
    {
        ApplyIntentMetadataToStereoPayload(payload, intent);
    }

    private static void ApplyIntentMetadata(AmazonQueueItem payload, DownloadIntent intent)
    {
        ApplyIntentMetadataToStereoPayload(payload, intent);
    }

    private static void ApplyIntentMetadataToStereoPayload<TPayload>(TPayload payload, DownloadIntent intent)
        where TPayload : class
    {
        dynamic p = payload;
        var trackNumber = ResolveIntentTrackNumber(intent, p.SpotifyTrackNumber, p.Position);
        var discNumber = ResolveIntentDiscNumber(intent, p.SpotifyDiscNumber);
        var trackTotal = ResolveIntentTrackTotal(intent, p.SpotifyTotalTracks);

        p.ReleaseDate = ResolveIntentReleaseDate(intent, p.ReleaseDate);
        p.TrackNumber = trackNumber;
        p.DiscNumber = discNumber;
        p.TrackTotal = trackTotal;
        p.DiscTotal = intent.DiscTotal > 0 ? intent.DiscTotal : p.DiscTotal;
        p.SpotifyTrackNumber = trackNumber;
        p.SpotifyDiscNumber = discNumber;
        p.SpotifyTotalTracks = trackTotal;
        p.Genres = ResolveIntentGenres(intent, p.Genres);
        p.Label = ResolveIntentString(intent.Label, p.Label);
        p.Copyright = ResolveIntentString(intent.Copyright, p.Copyright);
        p.Explicit = intent.Explicit ?? p.Explicit;
        p.Composer = ResolveIntentString(intent.Composer, p.Composer);
        p.Url = ResolveIntentString(intent.Url, p.Url);
        p.Barcode = ResolveIntentString(intent.Barcode, p.Barcode);
        p.AppleId = ResolveIntentString(intent.AppleId, p.AppleId);
        p.QobuzId = ResolveIntentString(intent.QobuzId, p.QobuzId);
        p.TidalId = ResolveIntentString(intent.TidalId, p.TidalId);
        p.AmazonId = ResolveIntentString(intent.AmazonId, p.AmazonId);
        ApplyWatchlistMetadata(payload, intent);
        ApplyIntentAudioFeaturesToStereoPayload(payload, intent);
    }

    private static void ApplyIntentAudioFeaturesToStereoPayload<TPayload>(TPayload payload, DownloadIntent intent)
        where TPayload : class
    {
        dynamic p = payload;
        p.Danceability = ResolveIntentDouble(intent.Danceability, p.Danceability);
        p.Energy = ResolveIntentDouble(intent.Energy, p.Energy);
        p.Valence = ResolveIntentDouble(intent.Valence, p.Valence);
        p.Acousticness = ResolveIntentDouble(intent.Acousticness, p.Acousticness);
        p.Instrumentalness = ResolveIntentDouble(intent.Instrumentalness, p.Instrumentalness);
        p.Speechiness = ResolveIntentDouble(intent.Speechiness, p.Speechiness);
        p.Loudness = ResolveIntentDouble(intent.Loudness, p.Loudness);
        p.Tempo = ResolveIntentDouble(intent.Tempo, p.Tempo);
        p.TimeSignature = ResolveIntentInt(intent.TimeSignature, p.TimeSignature);
        p.Liveness = ResolveIntentDouble(intent.Liveness, p.Liveness);
        p.MusicKey = ResolveIntentString(intent.MusicKey, p.MusicKey);
    }

    private static void ApplyWatchlistMetadata<TPayload>(TPayload payload, DownloadIntent intent)
        where TPayload : class
    {
        dynamic p = payload;
        p.WatchlistSource = ResolveIntentString(intent.WatchlistSource, p.WatchlistSource);
        p.WatchlistPlaylistId = ResolveIntentString(intent.WatchlistPlaylistId, p.WatchlistPlaylistId);
        p.WatchlistTrackId = ResolveIntentString(intent.WatchlistTrackId, p.WatchlistTrackId);
        p.WatchlistOrigin = ResolveIntentString(intent.WatchlistOrigin, p.WatchlistOrigin);
        p.WatchlistUnavailableSettingsFingerprint = ResolveIntentString(
            intent.WatchlistUnavailableSettingsFingerprint,
            p.WatchlistUnavailableSettingsFingerprint);
    }

    private static int ResolveIntentTrackNumber(DownloadIntent intent, int existingTrackNumber, int fallbackPosition)
    {
        if (intent.TrackNumber > 0)
        {
            return intent.TrackNumber;
        }

        if (existingTrackNumber > 0)
        {
            return existingTrackNumber;
        }

        return fallbackPosition > 0 ? fallbackPosition : 0;
    }

    private static double? ResolveIntentDouble(double? value, double? existing)
    {
        return value ?? existing;
    }

    private static int? ResolveIntentInt(int? value, int? existing)
    {
        return value ?? existing;
    }

    private static int ResolveIntentDiscNumber(DownloadIntent intent, int existingDiscNumber)
    {
        if (intent.DiscNumber > 0)
        {
            return intent.DiscNumber;
        }

        if (existingDiscNumber > 0)
        {
            return existingDiscNumber;
        }

        return 1;
    }

    private static int ResolveIntentTrackTotal(DownloadIntent intent, int existingTrackTotal)
    {
        if (intent.TrackTotal > 0)
        {
            return intent.TrackTotal;
        }

        return existingTrackTotal > 0 ? existingTrackTotal : 0;
    }

    private static string ResolveIntentReleaseDate(DownloadIntent intent, string existingReleaseDate)
    {
        if (!string.IsNullOrWhiteSpace(intent.ReleaseDate))
        {
            return intent.ReleaseDate;
        }

        return existingReleaseDate ?? string.Empty;
    }

    private static List<string> ResolveIntentGenres(DownloadIntent intent, List<string>? existingGenres)
    {
        if (intent.Genres.Count > 0)
        {
            return intent.Genres
                .Where(static genre => !string.IsNullOrWhiteSpace(genre))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return existingGenres?.ToList() ?? new List<string>();
    }

    private static string ResolveIntentString(string? intentValue, string? existingValue)
    {
        return !string.IsNullOrWhiteSpace(intentValue)
            ? intentValue
            : existingValue ?? string.Empty;
    }

    private static string? ResolveResolvedAlbumForAtmos(string? intentAlbum, string? resolvedAlbum)
    {
        string? selectedAlbum;
        if (!string.IsNullOrWhiteSpace(resolvedAlbum)
            && IsPlaceholderAlbum(intentAlbum))
        {
            selectedAlbum = resolvedAlbum.Trim();
        }
        else
        {
            selectedAlbum = FirstNonEmpty(intentAlbum, resolvedAlbum);
        }

        var normalized = TrackTitleMatcher.RemoveAtmosVersionMarker(selectedAlbum);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool IsPlaceholderAlbum(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Trim().Equals("Unknown Album", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<EnqueueItemDecision> EnqueueItemAsync<TPayload>(
        TPayload payload,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules,
        bool allowQualityUpgrade,
        int? requestedQualityRank,
        string initialStatus,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        if (payload == null)
        {
            return EnqueueItemDecision.Fail("invalid_payload", "Queue payload is missing.");
        }

        var context = BuildEnqueueItemContext(payload, blockRules, allowQualityUpgrade, requestedQualityRank);
        var requireResolvedEngineIdentity = !IsPreResolutionPayload(payload);
        var payloadFailure = TryValidateResolvedQueuePayload(payload, context, requireResolvedEngineIdentity);
        if (payloadFailure != null)
        {
            return payloadFailure;
        }

        var destinationFailure = await TryValidateEnqueueDestinationAsync(payload, context, cancellationToken);
        if (destinationFailure != null)
        {
            return destinationFailure;
        }

        var finalOutputPath = await ResolveExpectedFinalOutputPathAsync(payload, context, cancellationToken);
        if (payload is EngineQueueItemBase enginePayloadForDestination)
        {
            enginePayloadForDestination.ExpectedFinalOutputPath = finalOutputPath ?? string.Empty;
        }
        var dedupeDecision = await _dedupeService.CheckAsync(BuildDedupeRequest(context, finalOutputPath), cancellationToken);
        if (!dedupeDecision.Allowed)
        {
            return EnqueueItemDecision.Fail(
                dedupeDecision.ReasonCode ?? "duplicate",
                dedupeDecision.Message ?? "Skipped: matching track is already managed by DeezSpoTag.",
                dedupeDecision.QueueUuid);
        }

        return await InsertQueueItemAsync(payload, context, initialStatus, cancellationToken);
    }

    private Task<EnqueueItemDecision> EnqueueItemAsync<TPayload>(
        TPayload payload,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules,
        bool allowQualityUpgrade,
        int? requestedQualityRank,
        CancellationToken cancellationToken)
        where TPayload : class
        => EnqueueItemAsync(payload, blockRules, allowQualityUpgrade, requestedQualityRank, "queued", cancellationToken);

    private static bool IsPreResolutionPayload<TPayload>(TPayload payload)
        where TPayload : class
    {
        var value = TryGetPayloadString(payload, "ResolutionStatus");
        return string.Equals(value, QueuePreResolutionPayload.Pending, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, QueuePreResolutionPayload.Resolving, StringComparison.OrdinalIgnoreCase);
    }

    private EnqueueItemContext BuildEnqueueItemContext<TPayload>(
        TPayload payload,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules,
        bool allowQualityUpgrade,
        int? requestedQualityRank)
        where TPayload : class
    {
        var identity = BuildPayloadIdentity(payload);
        var settings = _settingsService.LoadSettings();
        var requestedLocalQualityRank = ParseRequestedLocalQualityRank(identity.PayloadQuality, requestedQualityRank);
        return new EnqueueItemContext(
            identity,
            settings,
            blockRules,
            allowQualityUpgrade,
            requestedQualityRank,
            allowQualityUpgrade && requestedQualityRank.HasValue,
            requestedQualityRank ?? int.MinValue,
            requestedLocalQualityRank,
            allowQualityUpgrade && requestedLocalQualityRank.HasValue);
    }

    private static EnqueueItemDecision? TryValidateResolvedQueuePayload<TPayload>(
        TPayload payload,
        EnqueueItemContext context,
        bool requireResolvedEngineIdentity)
        where TPayload : class
    {
        var identity = context.Identity;
        if (RequiresResolvedMusicMetadata(identity)
            && (string.IsNullOrWhiteSpace(identity.TrackTitle) || string.IsNullOrWhiteSpace(identity.TrackArtist)))
        {
            return EnqueueItemDecision.Fail(
                "unresolved_metadata",
                "Skipped: track metadata could not be resolved for this download.");
        }

        var sourceUrl = TryGetPayloadString(payload, "SourceUrl");
        if (requireResolvedEngineIdentity && !HasRequiredEngineIdentity(identity, sourceUrl))
        {
            return EnqueueItemDecision.Fail(
                "unresolved_engine_identity",
                $"Skipped: {identity.Engine} download could not be matched to a valid source.");
        }

        return null;
    }

    private static bool RequiresResolvedMusicMetadata(PayloadIdentity identity)
    {
        var contentType = NormalizeContentType(identity.ContentType);
        return !string.Equals(contentType, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contentType, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRequiredEngineIdentity(PayloadIdentity identity, string? sourceUrl)
    {
        return identity.Engine switch
        {
            DeezerPlatform => !string.IsNullOrWhiteSpace(identity.DeezerTrackId)
                || IsServiceUrlMatch(sourceUrl ?? string.Empty, DeezerPlatform),
            ApplePlatform => !string.IsNullOrWhiteSpace(identity.AppleTrackId)
                || IsServiceUrlMatch(sourceUrl ?? string.Empty, ApplePlatform),
            TidalPlatform => !string.IsNullOrWhiteSpace(identity.TidalTrackId)
                || IsServiceUrlMatch(sourceUrl ?? string.Empty, TidalPlatform),
            AmazonPlatform => !string.IsNullOrWhiteSpace(identity.AmazonTrackId)
                || IsServiceUrlMatch(sourceUrl ?? string.Empty, AmazonPlatform),
            QobuzPlatform => !string.IsNullOrWhiteSpace(identity.QobuzTrackId)
                || IsServiceUrlMatch(sourceUrl ?? string.Empty, QobuzPlatform),
            _ => true
        };
    }

    private async Task<EnqueueItemDecision?> TryValidateEnqueueDestinationAsync<TPayload>(
        TPayload payload,
        EnqueueItemContext context,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        if (IsDirectDestinationPayload(payload))
        {
            return null;
        }

        var destinationCheck = await DownloadDestinationGuard.ValidateAsync(
            context.Identity.DestinationFolderId,
            context.Settings.DownloadLocation,
            _libraryRepository,
            cancellationToken,
            context.Identity.ContentType);
        if (destinationCheck.Ok)
        {
            return null;
        }

        _activityLog.Warn($"Queue blocked: {destinationCheck.Error}");
        return EnqueueItemDecision.Fail("destination_invalid", destinationCheck.Error ?? "Destination folder is invalid.");
    }

    private async Task<EnqueueItemDecision> InsertQueueItemAsync<TPayload>(
        TPayload payload,
        EnqueueItemContext context,
        string initialStatus,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        ApplySourceSettingsSnapshot(payload, context.Settings);
        var json = JsonSerializer.Serialize(payload);
        var item = new DownloadQueueItem(
            Id: 0,
            QueueUuid: (string)payload!.GetType().GetProperty("Id")!.GetValue(payload)!,
            Engine: context.Identity.Engine,
            ArtistName: (string)payload.GetType().GetProperty("Artist")!.GetValue(payload)!,
            TrackTitle: (string)payload.GetType().GetProperty("Title")!.GetValue(payload)!,
            Isrc: context.Identity.Isrc,
            DeezerTrackId: context.Identity.DeezerTrackId,
            DeezerAlbumId: context.Identity.DeezerAlbumId,
            DeezerArtistId: context.Identity.DeezerArtistId,
            SpotifyTrackId: context.Identity.SpotifyTrackId,
            SpotifyAlbumId: context.Identity.SpotifyAlbumId,
            SpotifyArtistId: context.Identity.SpotifyArtistId,
            AppleTrackId: context.Identity.AppleTrackId,
            AppleAlbumId: context.Identity.AppleAlbumId,
            AppleArtistId: context.Identity.AppleArtistId,
            DurationMs: context.Identity.DurationMs,
            DestinationFolderId: context.Identity.DestinationFolderId,
            QualityRank: context.RequestedQualityRank,
            QueueOrder: null,
            ContentType: context.Identity.ContentType,
            Status: string.IsNullOrWhiteSpace(initialStatus) ? "queued" : initialStatus,
            PayloadJson: json,
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var insertId = await _queueRepository.EnqueueAsync(item, skipDuplicateCheck: false, cancellationToken: cancellationToken);
        if (!insertId.HasValue)
        {
            return EnqueueItemDecision.Fail("queue_insert_ignored", "Skipped: item was not added to queue because a duplicate already exists.");
        }

        return EnqueueItemDecision.Ok(item.QueueUuid);
    }

    private async Task<string?> ResolveExpectedFinalOutputPathAsync<TPayload>(
        TPayload payload,
        EnqueueItemContext context,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        if (payload is not EngineQueueItemBase enginePayload
            || IsDirectDestinationPayload(payload)
            || context.Identity.DestinationFolderId == null)
        {
            return null;
        }

        var settings = CloneSettings(context.Settings);
        await DownloadEngineSettingsHelper.ResolveAndApplyProfileAsync(
            _downloadTagSettingsResolver,
            settings,
            context.Identity.DestinationFolderId,
            _logger,
            cancellationToken,
            new DownloadEngineSettingsHelper.ProfileResolutionOptions(CurrentEngine: context.Identity.Engine));

        using var scope = _serviceProvider.CreateScope();
        var pathProcessor = scope.ServiceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
        var trackContext = EngineAudioPostDownloadHelper.BuildTrackContext(
            enginePayload,
            settings,
            pathProcessor,
            context.Identity.Engine,
            ResolvePayloadSourceIdForEngine(context.Identity));

        return !string.IsNullOrWhiteSpace(trackContext.PathResult.WritePath)
            ? DownloadPathResolver.ResolveIoPath(trackContext.PathResult.WritePath)
            : Path.Join(
                DownloadPathResolver.ResolveIoPath(trackContext.PathResult.FilePath),
                trackContext.PathResult.Filename);
    }

    private static DeezSpoTagSettings CloneSettings(DeezSpoTagSettings settings)
        => JsonSerializer.Deserialize<DeezSpoTagSettings>(JsonSerializer.Serialize(settings)) ?? settings;

    private static string? ResolvePayloadSourceIdForEngine(PayloadIdentity identity)
        => identity.Engine switch
        {
            DeezerPlatform => identity.DeezerTrackId,
            SpotifyPlatform => identity.SpotifyTrackId,
            ApplePlatform => identity.AppleTrackId,
            QobuzPlatform => identity.QobuzTrackId,
            TidalPlatform => identity.TidalTrackId,
            AmazonPlatform => identity.AmazonTrackId,
            _ => null
        };

    private static DownloadDedupeRequest BuildDedupeRequest(EnqueueItemContext context, string? finalOutputPath)
        => new()
        {
            Isrc = context.Identity.Isrc,
            DeezerTrackId = context.Identity.DeezerTrackId,
            DeezerAlbumId = context.Identity.DeezerAlbumId,
            DeezerArtistId = context.Identity.DeezerArtistId,
            SpotifyTrackId = context.Identity.SpotifyTrackId,
            SpotifyAlbumId = context.Identity.SpotifyAlbumId,
            SpotifyArtistId = context.Identity.SpotifyArtistId,
            AppleTrackId = context.Identity.AppleTrackId,
            AppleAlbumId = context.Identity.AppleAlbumId,
            AppleArtistId = context.Identity.AppleArtistId,
            QobuzTrackId = context.Identity.QobuzTrackId,
            TidalTrackId = context.Identity.TidalTrackId,
            AmazonTrackId = context.Identity.AmazonTrackId,
            TrackTitle = context.Identity.TrackTitle,
            TrackArtist = context.Identity.TrackArtist,
            TrackPrimaryArtist = context.Identity.TrackPrimaryArtist,
            Album = context.Identity.Album,
            Genres = context.Identity.Genres,
            Explicit = context.Identity.Explicit,
            ReleaseDate = context.Identity.ReleaseDate,
            DurationMs = context.Identity.DurationMs,
            DestinationFolderId = context.Identity.DestinationFolderId,
            ContentType = context.Identity.ContentType,
            RequestedAudioVariant = context.Identity.RequestedAudioVariant,
            RequestedLocalQualityRank = context.LocalQualityUpgradeRequested ? context.RequestedLocalQualityRank : null,
            FinalOutputPath = finalOutputPath,
            BlockRules = context.BlockRules
        };

    private static void PopulateStandardQueuePayload(
        EngineQueueItemBase payload,
        DownloadIntent intent,
        StandardPayloadContext context)
    {
        payload.Id = Guid.NewGuid().ToString("N");
        payload.QueueOrigin = "intent";
        payload.SourceUrl = context.SourceUrl;
        payload.CollectionName = intent.Album ?? string.Empty;
        payload.CollectionType = context.CollectionType;
        payload.Title = intent.Title ?? string.Empty;
        payload.Artist = intent.Artist ?? string.Empty;
        payload.Album = intent.Album ?? string.Empty;
        payload.AlbumArtist = string.IsNullOrWhiteSpace(intent.AlbumArtist) ? intent.Artist ?? string.Empty : intent.AlbumArtist;
        payload.Isrc = intent.Isrc ?? string.Empty;
        payload.DeezerId = !string.IsNullOrWhiteSpace(payload.DeezerId)
            ? payload.DeezerId
            : intent.DeezerId ?? string.Empty;
        payload.AppleId = intent.AppleId ?? string.Empty;
        payload.AppleAlbumId = intent.AppleAlbumId ?? string.Empty;
        payload.AppleAlbumName = intent.AppleAlbumName ?? string.Empty;
        payload.AppleArtistName = intent.AppleArtistName ?? string.Empty;
        payload.AppleIsrc = intent.AppleIsrc ?? string.Empty;
        payload.AppleDurationMs = intent.AppleDurationMs;
        payload.QobuzId = FirstNonEmpty(intent.QobuzId, TryExtractQobuzTrackId(context.SourceUrl)?.ToString(CultureInfo.InvariantCulture)) ?? string.Empty;
        payload.TidalId = FirstNonEmpty(intent.TidalId, TryExtractTidalTrackId(context.SourceUrl)) ?? string.Empty;
        payload.AmazonId = EngineLinkParser.NormalizeAmazonTrackId(intent.AmazonId)
            ?? EngineLinkParser.TryExtractAmazonTrackId(context.SourceUrl, RegexTimeout)
            ?? string.Empty;
        payload.ContentType = context.ContentType;
        payload.Cover = intent.Cover ?? string.Empty;
        payload.AutoSources = context.AutoSources;
        payload.AutoIndex = Math.Max(0, context.SelectedAutoIndex);
        payload.FallbackPlan = context.FallbackPlan;
        payload.ReleaseDate = context.ReleaseDate;
        payload.DurationSeconds = context.DurationSeconds;
        payload.Position = intent.Position;
        payload.SpotifyId = intent.SpotifyId ?? string.Empty;
        payload.DestinationFolderId = context.DestinationFolderId;
        payload.QualityBucket = context.QualityBucket;
        payload.Size = 1;
    }

    private async Task TryEnqueueVisibleAtmosSecondaryAsync(
        DownloadIntent intent,
        DeezSpoTagSettings settings,
        DestinationRoutingResult destinationRouting,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules,
        List<string> queued,
        CancellationToken cancellationToken)
    {
        if (!IsMultiQualityDualEnabled(settings.MultiQuality)
            || !IsMusicIntent(intent)
            || IsVideoIntent(intent)
            || !destinationRouting.SecondaryDestinationFolderId.HasValue)
        {
            return;
        }

        await TryEnqueueAtmosSecondaryAsync(
            new AtmosSecondaryEnqueueRequest(
                intent,
                settings,
                destinationRouting.PrimaryDestinationFolderId,
                destinationRouting.SecondaryDestinationFolderId,
                intent.AllowQualityUpgrade,
                queued,
                Availability: null,
                PreferIsrcOnly: false,
                blockRules,
                cancellationToken));
    }

    private void NotifyQueueAdded(EngineQueueItemBase payload)
    {
        switch (payload)
        {
            case DeezerQueueItem deezer:
                _deezspotagListener.SendAddedToQueue(deezer.ToQueuePayload());
                break;
            case AppleQueueItem apple:
                _deezspotagListener.SendAddedToQueue(apple.ToQueuePayload());
                break;
            case TidalQueueItem tidal:
                _deezspotagListener.SendAddedToQueue(tidal.ToQueuePayload());
                break;
            case AmazonQueueItem amazon:
                _deezspotagListener.SendAddedToQueue(amazon.ToQueuePayload());
                break;
            case QobuzQueueItem qobuz:
                _deezspotagListener.SendAddedToQueue(qobuz.ToQueuePayload());
                break;
        }
    }

    private static string? TryGetPayloadQuality<TPayload>(TPayload payload) =>
        payload switch
        {
            AppleQueueItem apple => apple.Quality,
            QobuzQueueItem qobuz => qobuz.Quality,
            TidalQueueItem tidal => tidal.Quality,
            _ => null
        };

    private static string? TryGetPayloadContentType<TPayload>(TPayload payload) =>
        payload switch
        {
            AppleQueueItem apple => apple.ContentType,
            QobuzQueueItem qobuz => qobuz.ContentType,
            TidalQueueItem tidal => tidal.ContentType,
            AmazonQueueItem amazon => amazon.ContentType,
            _ => null
        };

    private static void RecordSkipReason(
        List<string> reasonCodes,
        List<string> reasons,
        List<string>? relatedQueueUuids,
        EnqueueItemDecision decision)
    {
        if (!string.IsNullOrWhiteSpace(decision.ReasonCode))
        {
            reasonCodes.Add(decision.ReasonCode);
        }

        if (!string.IsNullOrWhiteSpace(decision.Message))
        {
            reasons.Add(decision.Message);
        }

        if (relatedQueueUuids != null && !string.IsNullOrWhiteSpace(decision.QueueUuid))
        {
            relatedQueueUuids.Add(decision.QueueUuid);
        }
    }

    private static bool ShouldContinueWithSecondaryAfterPrimarySkip(EnqueueItemDecision decision)
    {
        if (decision == null)
        {
            return false;
        }

        // Atmos secondary should still be attempted even when stereo primary
        // fails/skips, because the variants are independent routes.
        return !decision.Success;
    }

    private static void ApplySourceSettingsSnapshot<TPayload>(TPayload payload, DeezSpoTagSettings settings)
    {
        if (payload is not EngineQueueItemBase queuePayload)
        {
            return;
        }

        queuePayload.SourceSettingsSnapshot = QueueSourceSettingsSnapshot.Capture(settings);
    }

    private sealed record EnqueueItemDecision(
        bool Success,
        string? QueueUuid,
        string ReasonCode,
        string Message)
    {
        public static EnqueueItemDecision Ok(string queueUuid) =>
            new(true, queueUuid, string.Empty, string.Empty);

        public static EnqueueItemDecision Fail(string reasonCode, string message) =>
            new(false, null, reasonCode ?? string.Empty, message ?? string.Empty);

        public static EnqueueItemDecision Fail(string reasonCode, string message, string? queueUuid) =>
            new(false, string.IsNullOrWhiteSpace(queueUuid) ? null : queueUuid, reasonCode ?? string.Empty, message ?? string.Empty);
    }

    private static string? TryGetPayloadIsrc<TPayload>(TPayload payload)
    {
        if (EqualityComparer<TPayload>.Default.Equals(payload, default!))
        {
            return null;
        }

        var payloadObject = (object)payload!;
        var type = payloadObject.GetType();
        var property = type.GetProperty("Isrc") ?? type.GetProperty("ISRC");
        if (property == null)
        {
            return null;
        }

        var value = property.GetValue(payloadObject);
        return value?.ToString();
    }

    private static bool IsDirectDestinationPayload<TPayload>(TPayload payload)
    {
        var normalizedContentType = NormalizeContentType(TryGetPayloadContentType(payload) ?? TryGetPayloadString(payload, "ContentType"));
        if (string.Equals(normalizedContentType, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedContentType, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (payload is AppleQueueItem apple
            && AppleVideoClassifier.IsVideo(apple.SourceUrl, apple.CollectionType, apple.ContentType))
        {
            return true;
        }

        return false;
    }

    private static string? TryGetPayloadString<TPayload>(TPayload payload, string propertyName)
    {
        if (EqualityComparer<TPayload>.Default.Equals(payload, default!) || string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        var payloadObject = (object)payload!;
        var property = payloadObject.GetType().GetProperty(propertyName);
        if (property == null)
        {
            return null;
        }

        return property.GetValue(payloadObject)?.ToString();
    }

    private static string? NormalizePrimaryArtistForDedupe(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var primary = ArtistNameNormalizer.ExtractPrimaryArtist(artistName);
        if (string.IsNullOrWhiteSpace(primary))
        {
            return null;
        }

        return string.Equals(primary, artistName.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : primary;
    }

    private static PayloadIdentity BuildPayloadIdentity<TPayload>(TPayload payload)
        where TPayload : class
    {
        var engine = payload.GetType().GetProperty("Engine")?.GetValue(payload) as string ?? string.Empty;
        var contentType = TryGetPayloadContentType(payload) ?? TryGetPayloadString(payload, "ContentType");
        var genericArtistId = TryGetPayloadString(payload, "ArtistId");
        var deezerArtistId = ResolvePayloadArtistId(engine, genericArtistId, TryGetPayloadString(payload, "DeezerArtistId"), DeezerPlatform);
        var spotifyArtistId = ResolvePayloadArtistId(engine, genericArtistId, TryGetPayloadString(payload, "SpotifyArtistId"), SpotifyPlatform);
        var appleArtistId = ResolvePayloadArtistId(engine, genericArtistId, TryGetPayloadString(payload, "AppleArtistId"), ApplePlatform);
        var trackArtist = (string)payload.GetType().GetProperty("Artist")!.GetValue(payload)!;
        var payloadQuality = TryGetPayloadQuality(payload);
        var payloadQualityBucket = TryGetPayloadString(payload, "QualityBucket");
        IReadOnlyList<string> genres = payload is EngineQueueItemBase queuePayload
            ? queuePayload.Genres
            : Array.Empty<string>();
        return new PayloadIdentity(
            TryGetPayloadIsrc(payload),
            TryGetPayloadString(payload, "DeezerId"),
            TryGetPayloadString(payload, "DeezerAlbumId") ?? TryGetPayloadString(payload, "AlbumId"),
            deezerArtistId,
            TryGetPayloadString(payload, "SpotifyId"),
            TryGetPayloadString(payload, "SpotifyAlbumId"),
            spotifyArtistId,
            TryGetPayloadString(payload, "AppleId"),
            TryGetPayloadString(payload, "AppleAlbumId"),
            appleArtistId,
            TryGetPayloadString(payload, "QobuzId") ?? TryGetPayloadString(payload, "QobuzTrackId"),
            TryGetPayloadString(payload, "TidalId") ?? TryGetPayloadString(payload, "TidalTrackId"),
            TryGetPayloadString(payload, "AmazonId") ?? TryGetPayloadString(payload, "AmazonTrackId"),
            engine,
            contentType,
            payload.GetType().GetProperty("DurationSeconds")!.GetValue(payload) is int duration && duration > 0 ? duration * 1000 : (int?)null,
            (string)payload.GetType().GetProperty("Title")!.GetValue(payload)!,
            trackArtist,
            NormalizePrimaryArtistForDedupe(trackArtist),
            TryGetPayloadString(payload, "Album"),
            genres,
            payload is EngineQueueItemBase basePayload ? basePayload.Explicit : null,
            TryGetPayloadString(payload, "ReleaseDate"),
            payloadQuality,
            payloadQualityBucket,
            ResolveRequestedAudioVariant(contentType, payloadQuality, payloadQualityBucket),
            payload.GetType().GetProperty("DestinationFolderId")?.GetValue(payload) as long?);
    }

    private static string? ResolvePayloadArtistId(string engine, string? genericArtistId, string? explicitArtistId, string platform)
    {
        if (!string.IsNullOrWhiteSpace(explicitArtistId))
        {
            return explicitArtistId;
        }

        return string.Equals(engine, platform, StringComparison.OrdinalIgnoreCase)
            ? genericArtistId
            : explicitArtistId;
    }

    private static string? ResolveRequestedAudioVariant(
        string? contentType,
        string? quality,
        string? qualityBucket)
    {
        var normalizedContentType = NormalizeContentType(contentType);
        if (string.Equals(normalizedContentType, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedContentType, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(normalizedContentType, DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase)
            || IsAtmosQuality(quality)
            || IsAtmosQuality(qualityBucket))
        {
            return AtmosQuality;
        }

        return StereoType;
    }

    private sealed record AppleVideoMetadata(
        string Title,
        string Artist,
        string AlbumName,
        string Isrc,
        string ReleaseDate,
        string Cover,
        int DurationSeconds,
        bool HasAtmos);

}
